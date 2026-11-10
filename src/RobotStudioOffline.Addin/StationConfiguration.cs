using ABB.Robotics.RobotStudio.Stations;
using RobotStudioOffline.Core;
using Cfg = ABB.Robotics.Controllers.ConfigurationDomain;
using Pc = ABB.Robotics.Controllers;

namespace RobotStudioOffline.Addin;

public sealed partial class StationApi
{
    private static object IoProject(Station station, IoProjectRequest request) => Configuration(station, request);

    private static object Configuration(Station station, ConfigurationRequest request)
    {
        var context = OwnedVirtualTask(station, request.Robot);
        using var controller = OpenOwnedVirtualConnection(station, context);
        void CheckVc() => EnsureOwnedVirtualContext(station, context, controller);
        CheckVc();
        var database = controller.Configuration;
        // The SDK caches Domains, Types and Attributes on their respective parents.
        // GetInstances and GetInstance return separately owned wrappers.
        var domains = database.Domains;
        object result;
        if (request.Domain is null)
            result = ConfigurationPage("domains", domains.Select(d => d.Name), request);
        else
        {
            var domain = domains.FirstOrDefault(d => MatchesConfigName(d.Name, request.Domain))
                ?? throw Error("configuration_domain_not_found", $"The virtual controller has no configuration domain '{request.Domain}'.");
            if (request.Type is null)
                result = ConfigurationPage("types", domain.Types.Select(t => t.Name), request);
            else
            {
                var type = domain.Types.FirstOrDefault(t => MatchesConfigName(t.Name, request.Type))
                    ?? throw Error("configuration_type_not_found", $"Configuration type '{request.Type}' was not found in '{domain.Name}'.");
                result = InspectConfigurationType(type, request, controller, CheckVc);
            }
        }
        if (request.Action == "read") CheckVc();
        return new
        {
            robot = context.Robot.Name, virtualController = context.Controller.Name,
            scope = "station_owned_virtual_controller",
            source = request is IoProjectRequest ? "installed_eio_configuration" : "installed_configuration_database",
            action = request.Action, domain = request.Domain, type = request.Type, instance = request.Instance, result
        };
    }

    private static object InspectConfigurationType(Cfg.Type type, ConfigurationRequest request,
        Pc.Controller controller, Action checkVc)
    {
        if (request.Instance is null)
        {
            var instances = type.GetInstances();
            try { return ConfigurationPage("instances", instances.Select(i => i.Name), request); }
            finally { foreach (var instance in instances) instance.Dispose(); }
        }
        if (request.Action == "set")
        {
            var attribute = type.Attributes.FirstOrDefault(a => MatchesConfigName(a.Name, request.Attribute!))
                ?? throw Error("configuration_attribute_not_found", $"Configuration attribute '{request.Attribute}' was not found.");
            return SetParameter(type, attribute, request, controller, checkVc);
        }
        using var selected = type.GetInstance(request.Instance) ??
            throw Error("configuration_instance_not_found", $"Configuration instance '{request.Instance}' was not found.");
        var readOnly = selected.IsReadOnly;
        var attributes = type.Attributes.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
        var items = attributes.Skip(request.Offset).Take(request.Limit).Select(attribute =>
        {
            checkVc();
            try { return ParameterDetail(type, selected, attribute, readOnly); }
            catch (Exception ex) { return (object)new { name = attribute.Name, error = new BridgeError("configuration_attribute_read_failed", ex.Message) }; }
        }).ToArray();
        return ConfigurationPage("attributes", items, attributes.Length, request);
    }

    private static object ParameterDetail(Cfg.Type type, Cfg.Instance instance, Cfg.Attribute attribute, bool readOnly)
    {
        var size = attribute.Size;
        var code = attribute.TypeCode;
        const int arrayLimit = 64;
        // PC SDK array indices start at 1 (the scalar overload reads index 1).
        object? value = size == 1 ? JsonConfigValue(instance.GetAttribute(attribute.Name)) :
            Enumerable.Range(1, Math.Min(Math.Max(0, size), arrayLimit))
                .Select(i => JsonConfigValue(instance.GetAttribute(attribute.Name, i))).ToArray();
        return new
        {
            name = attribute.Name, value, typeCode = code.ToString(), size, valueTruncated = size > arrayLimit,
            canSet = !readOnly && size == 1 && CanSetParameter(type, instance, attribute),
            initialValue = JsonConfigValue(attribute.InitialValue), minimum = JsonConfigValue(attribute.MinValue), maximum = JsonConfigValue(attribute.MaxValue),
            unit = JsonConfigValue(attribute.Unit), mandatory = attribute.Mandatory
        };
    }

    private static object SetParameter(Cfg.Type type, Cfg.Attribute attribute, ConfigurationRequest request,
        Pc.Controller controller, Action checkVc)
    {
        var code = attribute.TypeCode;
        void ValidateSelection(Cfg.Instance selected)
        {
            if (selected.IsReadOnly || attribute.Size != 1 || !CanSetParameter(type, selected, attribute))
                throw Error("configuration_edit_unsupported", "This field is read-only through the bridge. Select an attribute reported with canSet:true.");
        }
        using (var preflight = type.GetInstance(request.Instance!))
            ValidateSelection(preflight);
        var desired = ConfigurationValues.Scalar(request.Value!.Value, code);
        var expected = ConfigurationValues.Scalar(request.Expected!.Value, code);
        if (IsSignalSetting(type, attribute))
            ConfigurationValues.ValidateSignalSetting(attribute.Name, request.Value.Value);
        var warnings = new List<string>();
        checkVc();
        using var access = ConfigurationWriteAccess(controller, warnings);
        checkVc();
        using var instance = type.GetInstance(request.Instance!);
        ValidateSelection(instance);
        var before = instance.GetAttribute(attribute.Name);
        if (!Equals(expected, before))
            throw Error("configuration_value_changed", "The installed value differs from expected. Read the instance before submitting a new edit.");
        var changed = !Equals(before, desired);
        if (changed)
        {
            checkVc();
            try { instance.SetAttribute(attribute.Name, desired); }
            catch (Exception ex)
            {
                throw Error("configuration_write_uncertain", "The SDK did not confirm the write. Re-read the installed value before retrying. " + ex.Message);
            }
        }
        object? after;
        try
        {
            checkVc();
            // Verify via a newly resolved instance. The SDK documents Instance as a disconnected data set.
            using var readback = type.GetInstance(request.Instance!);
            after = readback.GetAttribute(attribute.Name);
        }
        catch (Exception ex)
        {
            throw Error("configuration_readback_failed", "The installed value could not be verified. Re-read the instance before retrying. " + ex.Message);
        }
        return new
        {
            attribute = attribute.Name, before, requested = desired, observed = JsonConfigValue(after),
            writeIssued = changed, verified = Equals(after, desired), warnings,
            activation = "Database readback only. RobotWare may require a virtual-controller restart to activate a parameter."
        };
    }

    private static bool IsSignalSetting(Cfg.Type type, Cfg.Attribute attribute) =>
        MatchesConfigName(type.Domain.Name, "EIO") && MatchesConfigName(type.Name, "EIO_SIGNAL") &&
        attribute.Name.ToUpperInvariant() is "DEFAULT" or "SIZE";

    private static bool CanSetParameter(Cfg.Type type, Cfg.Instance instance, Cfg.Attribute attribute)
    {
        if (ConfigurationValues.CanEdit(type.Domain.Name, type.Name, attribute.Name, attribute.TypeCode)) return true;
        if (!IsSignalSetting(type, attribute)) return false;
        // ABB 3HAC098390-001 pp. 409, 416 and 430. Use installed configuration
        // rather than a transient IOSystem.Signal.State.Simulated force flag.
        // Older RobotWare can expose Unit or UnitMap instead of Device/DeviceMap.
        if (!type.Attributes.Any(a => MatchesConfigName(a.Name, "Device")) ||
            !type.Attributes.Any(a => MatchesConfigName(a.Name, "DeviceMap"))) return false;
        return ConfigurationValues.CanEditSignalSetting(attribute.Name, attribute.TypeCode,
            instance.GetAttribute("SignalType") as string, instance.GetAttribute("Device") as string,
            instance.GetAttribute("DeviceMap") as string);
    }

    private static IDisposable? ConfigurationWriteAccess(Pc.Controller controller, List<string> warnings)
    {
        if (controller.RobotWareVersion.Major < 8)
            return controller.Configuration.IsMaster ? null : Pc.Mastership.Request(controller);
        // This uses the same station-owned VC access lease as module loading and
        // preserves write access that the owner already held in RobotStudio.
        try { return BeginRapidLoadAccess(controller, virtualLoad: true, warnings); }
        catch (BridgeException ex) when (ex.Code == "rapid_write_access_required")
        {
            throw Error("configuration_write_access_required", "RobotStudio needs Write Access on the selected virtual controller for the configuration edit.");
        }
    }

    private static bool MatchesConfigName(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static object ConfigurationPage(string level, IEnumerable<string> names, ConfigurationRequest request)
    {
        var ordered = names.Order(StringComparer.Ordinal).ToArray();
        return ConfigurationPage(level, ordered.Skip(request.Offset).Take(request.Limit).ToArray(), ordered.Length, request);
    }

    private static object ConfigurationPage<T>(string level, T[] items, int total, ConfigurationRequest request)
    {
        var hasMore = (long)request.Offset + items.Length < total;
        return new { level, total, request.Offset, request.Limit, items, hasMore, nextOffset = hasMore ? (int?)(request.Offset + items.Length) : null };
    }

    private static object? JsonConfigValue(object? value) => value switch
    {
        float f when !float.IsFinite(f) => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d when !double.IsFinite(d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal => value,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };
}
