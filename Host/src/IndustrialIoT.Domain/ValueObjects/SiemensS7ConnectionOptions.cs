namespace IndustrialIoT.Domain.ValueObjects;

using System.Globalization;
using IndustrialIoT.Domain.Enums;

public sealed record SiemensS7ConnectionOptions(string PlcType, byte Rack, byte Slot)
{
    private static readonly string[] SettingKeys = ["PlcType", "CpuType", "Series", "Model", "Rack", "Slot"];

    public static DeviceConnectionConfig ForConnection(ProtocolType protocol, string? model, DeviceConnectionConfig config) =>
        protocol == ProtocolType.SiemensS7
            ? config with { ExtendedProperties = Normalize(protocol, model, config.ExtendedProperties) }
            : config;

    public static SiemensS7ConnectionOptions Resolve(IReadOnlyDictionary<string, string> properties, string? model = null)
    {
        var raw = Get(properties, "PlcType", "CpuType", "Series")
            ?? (!string.IsNullOrWhiteSpace(model) ? model : Get(properties, "Model")) ?? "S1200";
        var compact = string.Concat(raw.Where(character => !char.IsWhiteSpace(character) && character is not '-' and not '_'))
            .ToUpperInvariant();
        var cpu = compact switch
        {
            "S1200" or "S71200" => "S1200",
            "S1500" or "S71500" => "S1500",
            "S300" or "S7300" => "S300",
            "S400" or "S7400" => "S400",
            "S200SMART" or "S7200SMART" => "S200Smart",
            "S200" or "S7200" => "S200",
            _ => throw new ArgumentException($"Unknown S7 CPU '{raw}'. Set PlcType to S1200, S1500, S300, S400, S200Smart or S200."),
        };
        var rack = ReadByte(properties, "Rack", 0, 7);
        var slot = ReadByte(properties, "Slot", cpu switch { "S300" => 2, "S400" => 3, _ => 0 }, 31);
        if ((cpu is "S200" or "S200Smart") && (rack != 0 || slot != 0))
            throw new ArgumentException($"{cpu} uses a fixed TSAP in this driver; Rack and Slot must be 0.");
        return new(cpu, rack, slot);
    }

    public static Dictionary<string, string> Normalize(ProtocolType protocol, string? model,
        Dictionary<string, string>? properties, ProtocolType? previousProtocol = null)
    {
        var source = properties ?? [];
        if (previousProtocol.HasValue && previousProtocol != protocol
            && (previousProtocol == ProtocolType.SiemensS7 || protocol == ProtocolType.SiemensS7))
            source = WithoutSettings(source);
        if (protocol != ProtocolType.SiemensS7) return source;

        var options = Resolve(source, model);
        var normalized = WithoutSettings(source);
        if (Get(source, "PlcType", "CpuType", "Series") is not null)
            normalized["PlcType"] = options.PlcType;
        var configuredModel = !string.IsNullOrWhiteSpace(model) ? model : Get(source, "Model");
        if (configuredModel is not null) normalized["Model"] = configuredModel.Trim();
        // Keep omitted values omitted so later model changes still use that CPU's defaults.
        if (Get(source, "Rack") is not null) normalized["Rack"] = options.Rack.ToString(CultureInfo.InvariantCulture);
        if (Get(source, "Slot") is not null) normalized["Slot"] = options.Slot.ToString(CultureInfo.InvariantCulture);
        return normalized;
    }

    private static Dictionary<string, string> WithoutSettings(IReadOnlyDictionary<string, string> properties) =>
        properties.Where(property => !SettingKeys.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(property => property.Key, property => property.Value);

    private static byte ReadByte(IReadOnlyDictionary<string, string> properties, string key, byte fallback, byte maximum)
    {
        var raw = Get(properties, key);
        if (raw is null) return fallback;
        if (byte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value <= maximum)
            return value;
        throw new ArgumentException($"S7 {key} must be an integer between 0 and {maximum}.");
    }

    private static string? Get(IReadOnlyDictionary<string, string> properties, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (properties.TryGetValue(key, out var exact)) return exact;
            foreach (var property in properties)
                if (property.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }
        return null;
    }
}
