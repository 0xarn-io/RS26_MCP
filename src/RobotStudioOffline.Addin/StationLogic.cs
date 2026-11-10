using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static object StationLogic(Station station, LogicRequest request)
    {
        if (request.Action == "list")
        {
            var wires = station.Connections.Select(c => DescribeWire("signal", c.SourceObject, c.SourceSignal, c.TargetObject, c.TargetSignal))
                .Concat(station.Bindings.Select(b => DescribeWire("property", b.SourceObject, b.SourceProperty, b.TargetObject, b.TargetProperty)));
            return new { total = station.Connections.Count + station.Bindings.Count, request.Offset,
                items = wires.Skip(request.Offset).Take(request.Limit).ToArray() };
        }

        var destination = request.Target!;
        var target = LogicOwner(station, destination);
        if (request.Action is "set_input" or "pulse")
        {
            var component = (SmartComponent)target;
            RequireSupportedLogicComponent(component);
            var input = ComponentSignal(component, destination.Member);
            if (input.SignalType != IOSignalType.DigitalInput)
                throw Error("logic_signal_type", "Choose a digital Smart Component input.");
            if (request.Action == "pulse") input.Pulse();
            else
            {
                var outcome = input.TrySetValue(request.Value!.Value, null);
                if (!outcome.Success) throw Error("logic_signal_value", outcome.ErrorMessage);
            }
            return new { component = target.UniqueId, input = input.Name, action = request.Action,
                observed = input.Value, autoReset = input.AutoReset };
        }

        if (Simulator.State != SimulationState.Stopped) throw Error("simulation_active", "Stop simulation before editing station wiring.");
        var origin = request.Source!;
        var source = LogicOwner(station, origin);
        if (ReferenceEquals(source, target) && origin.Member == destination.Member)
            throw Error("logic_self_link", "Choose distinct source and target members.");
        var create = request.Action == "connect";
        var wire = DescribeWire(request.Kind!, source, origin.Member, target, destination.Member);
        var signals = station.Connections.Where(c => ReferenceEquals(c.TargetObject, target) && c.TargetSignal == destination.Member).ToArray();
        var bindings = station.Bindings.Where(b => ReferenceEquals(b.TargetObject, target) && b.TargetProperty == destination.Member).ToArray();
        var signal = signals.FirstOrDefault(c => ReferenceEquals(c.SourceObject, source) && c.SourceSignal == origin.Member);
        var binding = bindings.FirstOrDefault(b => ReferenceEquals(b.SourceObject, source) && b.SourceProperty == origin.Member);
        var exists = request.Kind == "signal" ? signal != null : binding != null;
        if (exists == create) return new { changed = false, connected = exists, wire };
        if (create)
        {
            if (source is SmartComponent fromComponent) RequireSupportedLogicComponent(fromComponent);
            if (target is SmartComponent toComponent) RequireSupportedLogicComponent(toComponent);
            if (signals.Length != 0 || bindings.Length != 0)
                throw Error("logic_target_in_use", "The selected member already has an incoming station connection. Inspect station_logic before replacing it.");
            if (request.Kind == "signal")
            {
                var outputType = SignalKind(station, source, origin, input: false);
                var inputType = SignalKind(station, target, destination, input: true);
                if (outputType != inputType) throw Error("logic_signal_type", "Source and target signal kinds must match.");
            }
            else
            {
                var output = ComponentProperty((SmartComponent)source, origin.Member);
                var input = ComponentProperty((SmartComponent)target, destination.Member);
                if (input.ReadOnly || !input.ValueType.IsAssignableFrom(output.ValueType))
                    throw Error("logic_property_type", "Choose a writable destination property compatible with the source property type.");
            }
        }
        Project.UndoContext.BeginUndoStep("Offline bridge: edit station wiring");
        try
        {
            if (request.Kind == "signal")
            {
                if (create) station.Connections.Add(new IOConnection(source, origin.Member, target, destination.Member));
                else station.Connections.Remove(signal!);
            }
            else
            {
                if (create) station.Bindings.Add(new PropertyBinding(source, origin.Member, target, destination.Member));
                else station.Bindings.Remove(binding!);
            }
            var connected = request.Kind == "signal"
                ? station.Connections.Any(c => c.SourceObject == source && c.TargetObject == target &&
                    c.SourceSignal == origin.Member && c.TargetSignal == destination.Member)
                : station.Bindings.Any(b => b.SourceObject == source && b.TargetObject == target &&
                    b.SourceProperty == origin.Member && b.TargetProperty == destination.Member);
            if (connected != create) throw Error("logic_wiring_failed", "RobotStudio did not apply the requested wiring change.");
            return new { changed = true, connected, wire };
        }
        catch { Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback); throw; }
        finally { Project.UndoContext.EndUndoStep(); }
    }

    private static ProjectObject LogicOwner(Station station, LogicEndpoint endpoint) => endpoint.Component != null
        ? ResolveSceneObject(station, endpoint.Component, typeof(SmartComponent)) : OwnedVirtualTask(station, endpoint.Robot).Controller;

    private static IOSignal ComponentSignal(SmartComponent component, string name) =>
        component.IOSignals.SingleOrDefault(s => s.Name == name) ?? throw Error("logic_signal_missing", $"No signal '{name}' on '{component.Name}'.");

    private static void RequireSupportedLogicComponent(SmartComponent component)
    {
        if (!IsSupportedSmartComponent(component))
            throw Error("smart_template_unsupported", "Select an instance of a supported installed ABB template from smart_component.");
    }

    private static DynamicProperty ComponentProperty(SmartComponent component, string name) =>
        component.Properties.SingleOrDefault(p => p.Name == name) ?? throw Error("logic_property_missing", $"No property '{name}' on '{component.Name}'.");

    private static string SignalKind(Station station, ProjectObject owner, LogicEndpoint endpoint, bool input)
    {
        if (owner is SmartComponent component)
        {
            var signal = ComponentSignal(component, endpoint.Member);
            if (signal.IsInput != input) throw Error("logic_signal_direction", "Connect an output to an input.");
            return signal.SignalType switch
            {
                IOSignalType.DigitalInput or IOSignalType.DigitalOutput => "digital",
                IOSignalType.AnalogInput or IOSignalType.AnalogOutput => "analog",
                IOSignalType.DigitalGroupInput or IOSignalType.DigitalGroupOutput => "group",
                _ => throw Error("logic_signal_type", "Unsupported Smart Component signal type.")
            };
        }
        var context = OwnedVirtualTask(station, endpoint.Robot);
        if (!ReferenceEquals(context.Controller, owner)) throw Error("virtual_task_required", "The mapped virtual controller changed.");
        using var controller = OpenOwnedVirtualConnection(station, context);
        using var vcSignal = RequireIoSignal(controller, endpoint.Member);
        if (input ? !IsIoInput(vcSignal.Type) : !IsIoOutput(vcSignal.Type))
            throw Error("logic_signal_direction", "Use a mapped virtual output as source or virtual input as target.");
        return IoKind(vcSignal.Type);
    }

    private static object DescribeWire(string kind, ProjectObject? source, string sourceMember, ProjectObject? target, string targetMember)
    {
        static object Endpoint(ProjectObject? owner, string member) =>
            new { id = owner?.UniqueId, name = owner?.Name, type = owner?.GetType().Name, member };
        return new { kind, source = Endpoint(source, sourceMember), target = Endpoint(target, targetMember) };
    }
}
