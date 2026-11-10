using System.Text.Json;

namespace RobotStudioOffline.Core;

public static class ConfigurationValues
{
    // These scalar fields describe the virtual mechanism or display text. The SDK
    // also exposes network peers, physical device bindings and safety settings in
    // the same database. A local VC connection alone cannot isolate those writes.
    public static bool CanEdit(string domain, string type, string attribute, TypeCode code)
    {
        var configType = type.ToUpperInvariant();
        if (domain.Equals("EIO", StringComparison.OrdinalIgnoreCase))
            return configType == "EIO_SIGNAL" && code == TypeCode.String &&
                attribute.ToUpperInvariant() is "LABEL" or "CATEGORY";
        return domain.Equals("MOC", StringComparison.OrdinalIgnoreCase) &&
            configType is "ACC_DATA" or "ARM_LOAD" or "JOG_PARAMETERS" or "MOTION_PROCESS_MODE" &&
            code is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or
                TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
    }

    public static bool CanEditSignalSetting(string attribute, TypeCode code, string? signalType, string? device, string? deviceMap)
    {
        // An unknown binding (null) is different from a confirmed empty binding.
        if (device != "" || deviceMap != "") return false;
        return attribute.ToUpperInvariant() switch
        {
            "DEFAULT" => (signalType is "DI" or "DO") && (code is TypeCode.Single or TypeCode.Double),
            "SIZE" => (signalType is "GI" or "GO") && (code is TypeCode.Int32 or TypeCode.Int64),
            _ => false
        };
    }

    public static void ValidateSignalSetting(string attribute, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            if (attribute.Equals("Default", StringComparison.OrdinalIgnoreCase) && number is 0 or 1) return;
            if (attribute.Equals("Size", StringComparison.OrdinalIgnoreCase) &&
                number is >= 1 and <= 32 && Math.Truncate(number) == number) return;
        }
        throw new BridgeException("configuration_value_range",
            "An unassigned DI/DO Default must be 0 or 1. An unassigned GI/GO Size must be an integer from 1 to 32.");
    }

    public static object Scalar(JsonElement input, TypeCode type)
    {
        if (type == TypeCode.String && input.ValueKind == JsonValueKind.String)
            return input.GetString()!;
        if (type == TypeCode.Boolean && input.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return input.GetBoolean();
        if (input.ValueKind == JsonValueKind.Number)
        {
            switch (type)
            {
                case TypeCode.Byte when input.TryGetByte(out var value): return value;
                case TypeCode.SByte when input.TryGetSByte(out var value): return value;
                case TypeCode.Int16 when input.TryGetInt16(out var value): return value;
                case TypeCode.UInt16 when input.TryGetUInt16(out var value): return value;
                case TypeCode.Int32 when input.TryGetInt32(out var value): return value;
                case TypeCode.UInt32 when input.TryGetUInt32(out var value): return value;
                case TypeCode.Int64 when input.TryGetInt64(out var value): return value;
                case TypeCode.UInt64 when input.TryGetUInt64(out var value): return value;
                case TypeCode.Single when input.TryGetSingle(out var value) && float.IsFinite(value): return value;
                case TypeCode.Double when input.TryGetDouble(out var value) && double.IsFinite(value): return value;
                case TypeCode.Decimal when input.TryGetDecimal(out var value): return value;
            }
        }
        throw new BridgeException("configuration_value_type", $"Supply a JSON scalar representable as the SDK type {type}.");
    }
}
