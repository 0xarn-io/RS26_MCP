using System.IO;
using System.Text.Json;
using ABB.Robotics.Math;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static async Task<object> SmartComponentsAsync(Station station, SmartRequest request)
    {
        EnsureCurrent(station);
        if (request.Action == "catalog" && request.Template == null)
            return new { templates = SmartRequests.Templates.Select(name => new { name, installed = File.Exists(SmartLibraryPath(name)) }).ToArray() };
        if (request.Action == "list")
        {
            var components = station.GetAllObjects().OfType<SmartComponent>().OrderBy(c => c.UniqueId, StringComparer.Ordinal).ToArray();
            var items = components.Skip(request.Offset).Take(request.Limit).Select(c => new
                { id = c.UniqueId, name = c.Name, template = SmartTemplate(c), canBeSimulated = c.CanBeSimulated }).ToArray();
            return new { total = components.Length, items, nextOffset = (long)request.Offset + items.Length < components.Length
                ? (int?)(request.Offset + items.Length) : null };
        }

        SmartComponent component;
        if (request.Template != null)
        {
            if (request.Action == "create") EnsureGeometryCanBeAdded(station, request.Name!);
            var path = SmartLibraryPath(request.Template);
            if (!File.Exists(path)) throw Error("smart_template_missing", $"The installed ABB library does not contain {request.Template}.");
            // The public AddLibraryComponentAsync sequence loads the library, copies its root instance and adds the copy.
            // Complete the async load before acquiring the undo context and recheck the selection on return.
            var library = await GraphicComponentLibrary.LoadAsync(path, true);
            RequireMainThread();
            EnsureCurrent(station);
            if (library.RootComponent is not SmartComponent root || !HasTemplateCode(root, request.Template))
                throw Error("smart_template_invalid", $"The selected installed library did not supply the expected {request.Template} component.");
            if (request.Action == "catalog") return DescribeSmartComponent(root, request.Template, null);
            EnsureGeometryCanBeAdded(station, request.Name!);
            component = (SmartComponent)root.CopyInstance();
            component.Name = request.Name!;
        }
        else
        {
            component = (SmartComponent)ResolveSceneObject(station, request.Component!, typeof(SmartComponent));
            if (!IsSupportedSmartComponent(component))
                throw Error("smart_template_unsupported", "Select an instance linked to one of the installed ABB templates in catalog.");
            if (request.Action == "inspect") return DescribeSmartComponent(component, SmartTemplate(component)!, station);
        }

        RequireStopped();
        Project.UndoContext.BeginUndoStep("Offline bridge: " + request.Action + " smart component");
        try
        {
            if (request.Action == "delete") return DeleteSmartComponent(station, component);
            if (request.Action == "create")
            {
                station.GraphicComponents.Add(component);
                if (component.CodeBehindStatus == CodeBehindStatus.Uninitialized) component.InitializeCodeBehind();
                if (component.CodeBehindStatus is not (CodeBehindStatus.Loaded or CodeBehindStatus.LoadedCompatibilityMode))
                    throw Error("smart_code_unavailable", $"RobotStudio could not load {request.Template} behavior ({component.CodeBehindStatus}).");
                var active = Simulator.ActiveConfiguration;
                if (component.CanBeSimulated && ReferenceEquals(active?.ContainingProject, station) &&
                    !active!.SimulatedObjects.Contains(component)) active.SimulatedObjects.Add(component);
            }
            if (request.Position.HasValue || request.Quaternion.HasValue)
            {
                var prior = ToFrame(component.Transform.GlobalMatrix);
                component.Transform.GlobalMatrix = MatrixFromFrame(new(request.Position ?? prior.Position, request.Quaternion ?? prior.Orientation));
            }
            foreach (var change in request.Properties ?? [])
            {
                var property = component.Properties.FirstOrDefault(p => p.Name == change.Name)
                    ?? throw Error("smart_property_missing", $"No property named '{change.Name}' exists on {component.Name}.");
                if (property.ReadOnly) throw Error("smart_property_read_only", $"{property.Name} is an output property.");
                var value = SmartPropertyValue(station, property, change.Value);
                var validation = property.TrySetValue(value);
                if (!validation.Success) throw Error("smart_property_invalid", $"{property.Name}: {validation.Result}. {validation.ErrorMessage}");
            }
            Station.UpdateGraphics();
            return DescribeSmartComponent(component, request.Template ?? SmartTemplate(component)!, station);
        }
        catch
        {
            Project.UndoContext.CancelUndoStep(CancelUndoStepType.Rollback);
            throw;
        }
        finally { Project.UndoContext.EndUndoStep(); }
    }

    private static string SmartLibraryPath(string template)
    {
        var bin = Path.GetDirectoryName(typeof(Station).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(bin, "..", "ABB Library", "Components", template + ".rslib"));
    }

    private static bool HasTemplateCode(SmartComponent component, string template) =>
        component.CodeBehindLocation == $"RobotStudio.Services.Modeling.BuiltIn.{template},RobotStudio.Services.SmartComponents.dll";

    private static string? SmartTemplate(SmartComponent component)
    {
        var file = component.Library?.FileInfo?.FullName;
        if (file is null) return null;
        var template = Path.GetFileNameWithoutExtension(file);
        return SmartRequests.Templates.Contains(template, StringComparer.Ordinal) && HasTemplateCode(component, template) &&
            string.Equals(file, SmartLibraryPath(template), StringComparison.OrdinalIgnoreCase) ? template : null;
    }

    private static bool IsSupportedSmartComponent(SmartComponent component) => SmartTemplate(component) != null;

    private static object DeleteSmartComponent(Station station, SmartComponent component)
    {
        if (!station.GraphicComponents.Contains(component))
            throw Error("smart_delete_unsupported", "Select a top-level component.");
        // ABB's linked spatial sensors own one hidden part that renders the sensing geometry.
        // Keep any other added content and refuse external references to that native part or its bodies.
        var template = SmartTemplate(component)!;
        var graphics = component.GraphicComponents.Cast<GraphicComponent>().ToArray();
        if (component.Frames.Count != 0 || graphics.Length > 1 || graphics.Any(g => g is not Part ||
            g.Frames.Count != 0 || !SmartRequests.IsSensorGraphic(template, g.Name, g.UIVisible)))
            throw Error("smart_component_in_use", "Remove added frames and child geometry before deleting the component.");
        var removed = new HashSet<ProjectObject> { component };
        foreach (var part in graphics.Cast<Part>())
        {
            removed.Add(part);
            foreach (var body in part.Bodies) removed.Add(body);
        }
        if (station.Attachments.Any(a => removed.Contains(a.AttachmentParent) || removed.Contains(a.AttachmentChild)) ||
            station.CollisionSets.Any(c => c.FirstGroup.Any(removed.Contains) || c.SecondGroup.Any(removed.Contains)) ||
            station.PhysicsConstraints.Any(c => removed.Contains(c.FirstComponent) || removed.Contains(c.SecondComponent)) ||
            component.IOSignals.Any(s => s.GetConnections().Length != 0) || component.Properties.Any(p => p.GetBindings().Length != 0) ||
            station.GetAllObjects().OfType<SmartComponent>().Any(c => c != component && c.Properties.Any(p =>
                (p.ValueType.IsInterface || typeof(ProjectObject).IsAssignableFrom(p.ValueType)) && p.Value is ProjectObject target && removed.Contains(target))))
            throw Error("smart_component_in_use", "Remove attachments, physics constraints, collision membership, property references and wiring before deleting the component.");
        var id = component.UniqueId;
        var name = component.Name;
        var memberships = station.SimulationConfigurations.Where(c => c.SimulatedObjects.Contains(component)).ToArray();
        foreach (var configuration in memberships) configuration.SimulatedObjects.Remove(component);
        if (!station.GraphicComponents.Remove(component))
            throw Error("smart_delete_failed", "RobotStudio did not remove the component.");
        return new { deleted = true, id, name, removedFromConfigurations = memberships.Select(c => c.UniqueId).ToArray() };
    }

    private static object? SmartPropertyValue(Station station, DynamicProperty property, JsonElement value)
    {
        var type = property.ValueType;
        if (type == typeof(string) && value.ValueKind == JsonValueKind.String) return value.GetString();
        if (type == typeof(bool) && value.ValueKind is JsonValueKind.False or JsonValueKind.True) return value.GetBoolean();
        if (type == typeof(int) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer)) return integer;
        if (type == typeof(double) && value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (type == typeof(Vector3) && value.ValueKind == JsonValueKind.Array)
            return new Vector3(value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());
        if (type != typeof(string) && (type.IsInterface || typeof(ProjectObject).IsAssignableFrom(type)))
        {
            if (value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.Object)
            {
                var selector = value.GetProperty("object").GetString()!;
                if (type == typeof(ProjectObjectTag))
                {
                    var tags = station.GetAllObjects().OfType<ProjectObjectTag>().ToArray();
                    var byId = tags.FirstOrDefault(t => t.UniqueId == selector);
                    if (byId != null) return byId;
                    var named = tags.Where(t => t.Name == selector).ToArray();
                    if (named.Length == 1) return named[0];
                    throw Error("smart_property_object", "Choose an existing station tag by unique name or ID.");
                }
                return ResolveSceneObject(station, selector, type);
            }
        }
        throw Error("smart_property_type", $"'{property.Name}' requires {type.Name}. Use {{\"object\":\"station name or ID\"}} for object references.");
    }

    private static object DescribeSmartComponent(SmartComponent component, string template, Station? station)
    {
        var properties = component.Properties.Where(p => p.UIVisible).Select(property => new
        {
            name = property.Name, type = property.ValueType.Name, readOnly = property.ReadOnly,
            value = SmartJsonValue(property.Value),
            quantity = property.Attributes.TryGetValue("Quantity", out var quantity) ? quantity : null,
            allowedValues = property.Attributes.TryGetValue("AllowedValues", out var allowed) ? allowed : null,
            description = property.Description
        }).ToArray();
        var signals = component.IOSignals.Select(s => new
        {
            name = s.Name, type = s.SignalType.ToString(), isInput = s.IsInput, autoReset = s.AutoReset,
            value = SmartJsonValue(s.Value)
        }).ToArray();
        var active = Simulator.ActiveConfiguration;
        var inActive = station != null && active != null && ReferenceEquals(active.ContainingProject, station);
        return new
        {
            template, name = component.Name, id = station != null ? component.UniqueId : null, codeBehindStatus = component.CodeBehindStatus.ToString(),
            canBeSimulated = component.CanBeSimulated, properties, signals,
            propertyUnits = "RobotStudio SDK native units (length in m, velocity in m/s, angles in rad).",
            activeConfigurationId = inActive ? active!.UniqueId : null,
            includedInActiveConfiguration = inActive ? (bool?)active!.SimulatedObjects.Contains(component) : null,
            worldPose = station != null ? Frame(component.Transform.GlobalMatrix) : null
        };
    }

    private static object? SmartJsonValue(object? value) => value switch
    {
        ProjectObject item => new { id = item.UniqueId, name = item.Name, type = item.GetType().Name },
        Vector3 point => double.IsFinite(point.x) && double.IsFinite(point.y) && double.IsFinite(point.z)
            ? new[] { point.x, point.y, point.z } : null,
        double scalar => double.IsFinite(scalar) ? scalar : null,
        float scalar => float.IsFinite(scalar) ? scalar : null,
        null or string or int or bool => value,
        _ => value.ToString()
    };
}
