using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;
using Pc = ABB.Robotics.Controllers;
using Io = ABB.Robotics.Controllers.IOSystemDomain;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static object IoRead(Station station, IoReadRequest request)
    {
        var context = OwnedVirtualTask(station, request.Robot);
        using var controller = OpenOwnedVirtualConnection(station, context);
        var results = new List<object>();
        int total;
        int offset;
        int limit;
        if (request.Names is not null)
        {
            total = request.Names.Length;
            offset = 0;
            limit = total;
            foreach (var name in request.Names)
            {
                EnsureOwnedVirtualContext(station, context, controller);
                try
                {
                    using var signal = RequireIoSignal(controller, name);
                    results.Add(new { name, ok = true, data = DescribeIoSignal(signal) });
                }
                catch (Exception exception)
                {
                    results.Add(new { name, ok = false, error = IoError(exception) });
                }
            }
        }
        else
        {
            EnsureOwnedVirtualContext(station, context, controller);
            // GetSignals creates wrappers for the entire inventory, including entries
            // outside the page. Dispose every one after copying plain JSON values.
            var signals = controller.IOSystem.GetSignals(Io.IOFilterTypes.All).Cast<Io.Signal>().ToArray();
            try
            {
                total = signals.Length;
                offset = request.Offset;
                limit = request.Limit;
                foreach (var signal in signals.OrderBy(signal => signal.Name, StringComparer.Ordinal).Skip(offset).Take(limit))
                {
                    EnsureOwnedVirtualContext(station, context, controller);
                    try
                    {
                        results.Add(new { name = signal.Name, ok = true, data = DescribeIoSignal(signal) });
                    }
                    catch (Exception exception)
                    {
                        results.Add(new { name = signal.Name, ok = false, error = IoError(exception) });
                    }
                }
            }
            finally
            {
                foreach (var signal in signals) signal.Dispose();
            }
        }
        var hasMore = (long)offset + results.Count < total;
        return new
        {
            robot = context.Robot.Name, virtualController = context.Controller.Name,
            selection = request.Names is null ? "inventory" : "names",
            total, offset, limit, returned = results.Count, truncated = results.Count < total,
            hasMore, nextOffset = hasMore ? (int?)(offset + results.Count) : null,
            signals = results, scope = "station_owned_virtual_controller", readOnly = true,
            snapshotAtomic = false,
            note = "Values are observations while the virtual program may run; different signals and group value/state metadata can be read at different instants."
        };
    }

    private static object IoWrite(Station station, IoWriteRequest request)
    {
        var context = OwnedVirtualTask(station, request.Robot);
        using var controller = OpenOwnedVirtualConnection(station, context);
        using var signal = RequireIoSignal(controller, request.Name);
        var (minimum, maximum) = IoBounds(signal);
        var kind = IoKind(signal.Type);
        IoValueValidation.Validate(kind, request.Value, minimum, maximum);
        ValidateInputSimulation(signal, request.SimulateInput);
        var before = DescribeIoSignal(signal);
        ApplyVirtualIoOperation(station, context, controller, signal, request.SimulateInput, () =>
        {
            switch (signal)
            {
                case Io.GroupSignal group when kind == "group":
                    group.GroupValue = IoValueValidation.ToGroupValue(request.Value);
                    break;
                case Io.DigitalSignal digital when kind == "digital":
                    digital.Value = (float)request.Value;
                    break;
                case Io.AnalogSignal analog when kind == "analog":
                    analog.Value = IoValueValidation.ToAnalogValue(request.Value);
                    break;
                default:
                    throw Error("unsupported_signal_type", "The SDK signal wrapper does not match its declared signal type.");
            }
        });
        var observed = ObserveIoAfterWrite(station, context, controller, signal);
        return new
        {
            robot = context.Robot.Name, virtualController = context.Controller.Name,
            name = signal.Name, action = "write", requestedValue = request.Value,
            writeAccepted = true, simulateInput = request.SimulateInput,
            before, after = observed.Signal, verificationError = observed.Error,
            scope = "station_owned_virtual_controller", forced = false,
            note = "A single value write was accepted. Readback is an observation, not a latch: the running program, I/O logic or another writer may immediately change the value."
        };
    }

    private static Task<object> IoPulseAsync(Station station, IoPulseRequest request)
    {
        var context = OwnedVirtualTask(station, request.Robot);
        using var controller = OpenOwnedVirtualConnection(station, context);
        using var signal = RequireIoSignal(controller, request.Name);
        if (signal is not Io.DigitalSignal digital || IoKind(signal.Type) != "digital")
            throw Error("unsupported_signal_type", "io_pulse requires a digital input or output signal.");
        var (minimum, maximum) = IoBounds(signal);
        IoValueValidation.Validate("digital", 0, minimum, maximum);
        IoValueValidation.Validate("digital", 1, minimum, maximum);
        ValidateInputSimulation(signal, request.SimulateInput);
        var before = DescribeIoSignal(signal);
        // The SDK submits native pulse parameters and waits for request acknowledgement.
        // It creates no managed timer or delayed reset that needs this wrapper kept alive.
        ApplyVirtualIoOperation(station, context, controller, signal, request.SimulateInput,
            () => digital.Pulse(request.DurationMs));
        var observed = ObserveIoAfterWrite(station, context, controller, signal);
        return Task.FromResult<object>(new
        {
            robot = context.Robot.Name, virtualController = context.Controller.Name,
            name = signal.Name, action = "pulse", durationMs = request.DurationMs,
            pulseRequested = true, completed = false, simulateInput = request.SimulateInput,
            before, after = observed.Signal, verificationError = observed.Error,
            scope = "station_owned_virtual_controller", forced = false,
            note = "The native SDK pulse request was acknowledged; pulse completion is not awaited or verified. No managed sleep or delayed reset is used. Read the signal before considering another pulse."
        });
    }

    private static Io.Signal RequireIoSignal(Pc.Controller controller, string name) =>
        controller.IOSystem.GetSignal(name) ?? throw Error("signal_not_found", $"No virtual-controller I/O signal named '{name}'.");

    private static object DescribeIoSignal(Io.Signal signal)
    {
        var state = signal.State;
        var (minimum, maximum) = IoBounds(signal);
        // SignalState.Value and legacy GroupSignal.Value are floats and cannot carry
        // every uint32. Fetch GroupValue independently to retain all 32 group bits.
        object? value = signal is Io.GroupSignal group
            ? (object)group.GroupValue
            : float.IsFinite(state.Value) ? (object)(double)state.Value : null;
        return new
        {
            name = signal.Name, type = signal.Type.ToString(), kind = IoKind(signal.Type),
            direction = IsIoInput(signal.Type) ? "input" : IsIoOutput(signal.Type) ? "output" : "unknown",
            value, valueFinite = value is not null,
            minimum = double.IsFinite(minimum) ? (double?)minimum : null,
            maximum = double.IsFinite(maximum) ? (double?)maximum : null,
            simulated = state.Simulated, quality = state.Quality.ToString(),
            inputAsPhysical = signal.InputAsPhysical, unit = signal.Unit,
            observedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static (double Minimum, double Maximum) IoBounds(Io.Signal signal) => signal is Io.GroupSignal group
        ? (group.GroupMinValue, group.GroupMaxValue)
        : (signal.MinValue, signal.MaxValue);

    private static string IoKind(Io.SignalType type) => type switch
    {
        Io.SignalType.DigitalInput or Io.SignalType.DigitalOutput => "digital",
        Io.SignalType.AnalogInput or Io.SignalType.AnalogOutput => "analog",
        Io.SignalType.GroupInput or Io.SignalType.GroupOutput => "group",
        _ => "unknown"
    };

    private static bool IsIoInput(Io.SignalType type) => type is Io.SignalType.DigitalInput or Io.SignalType.AnalogInput or Io.SignalType.GroupInput;
    private static bool IsIoOutput(Io.SignalType type) => type is Io.SignalType.DigitalOutput or Io.SignalType.AnalogOutput or Io.SignalType.GroupOutput;

    private static void ValidateInputSimulation(Io.Signal signal, bool simulateInput)
    {
        if (simulateInput && !IsIoInput(signal.Type))
            throw Error("input_simulation_requires_input", "simulateInput:true is supported only for virtual-controller input signals.");
    }

    private static void ApplyVirtualIoOperation(Station station, VirtualTaskContext context, Pc.Controller controller,
        Io.Signal signal, bool simulateInput, Action operation)
    {
        // This is a wrapper flag passed to the one request, not controller force state.
        var previous = signal.InputAsPhysical;
        try
        {
            signal.InputAsPhysical = simulateInput;
            EnsureOwnedVirtualContext(station, context, controller);
            try { operation(); }
            catch (Exception exception)
            {
                throw Error("io_operation_uncertain", "The virtual I/O operation was attempted, but the SDK did not confirm success. The signal may have changed. Read its state before retrying; do not automatically repeat a pulse. SDK: " + exception.Message);
            }
        }
        finally { signal.InputAsPhysical = previous; }
    }

    private static IoReadback ObserveIoAfterWrite(Station station, VirtualTaskContext context, Pc.Controller controller, Io.Signal signal)
    {
        try
        {
            EnsureOwnedVirtualContext(station, context, controller);
            return new(DescribeIoSignal(signal), null);
        }
        catch (Exception exception)
        {
            return new(null, new BridgeError("io_readback_failed",
                "The virtual I/O request was accepted but readback failed. The write or pulse may already have happened; read the signal before retrying and do not automatically repeat a pulse. SDK: " + exception.Message));
        }
    }

    private static BridgeError IoError(Exception exception) => new(
        exception is BridgeException bridge ? bridge.Code : "sdk_io_error", exception.Message);

    private sealed record IoReadback(object? Signal, BridgeError? Error);
}
