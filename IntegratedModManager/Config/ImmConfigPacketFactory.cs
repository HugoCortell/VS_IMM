using Newtonsoft.Json;

namespace IntegratedModManager.Config;

public static class ImmConfigPacketFactory
{
	private static readonly JsonSerializerSettings AdvancedSchemaSerializerSettings = new()
	{
		NullValueHandling = NullValueHandling.Ignore
	};

    public static bool HasConfigurationForSide(ImmConfigDescriptor descriptor, ImmConfigSide side) => descriptor.Configuration.Any(block => block.Settings.Any(entry => (entry.ConfigSide ?? block.ConfigSide) == side));

    public static ImmConfigBlockPacket CreateBlockPacket(int blockIndex, ImmConfigBlock block, ImmConfigControlPacket[] controls) => new ImmConfigBlockPacket
    {
        Index = blockIndex,
        ConfigFile = block.ConfigFile,
        ConfigLabel = block.ConfigLabel,
        ConfigSide = block.ConfigSide,
        ConfigSource = block.ConfigSource,
        ParseDescriptions = block.ParseDescriptions,
        Description = block.Description ?? "",
        Controls = controls
    };

    public static ImmConfigControlPacket CreateControlPacket(int globalIndex, int blockIndex, ImmConfigBlock block, ImmConfigEntry entry, ImmConfigSide effectiveSide) => new ImmConfigControlPacket
    {
        Index = globalIndex,
        BlockIndex = blockIndex,
        Type = entry.Type,
        Label = entry.Label,
        Description = entry.Description ?? "",
        Map = entry.Map,
        Code = entry.Code,
        ConfigSource = block.ConfigSource,
        ConfigSide = effectiveSide,
        DefaultValueJson = entry.Default?.ToString(Formatting.None) ?? "",
        HasMin = entry.Min.HasValue,
        Min = entry.Min ?? 0,
        HasMax = entry.Max.HasValue,
        Max = entry.Max ?? 0,
        HasStep = entry.Step.HasValue,
        Step = entry.Step ?? 0,
        ElementType = entry.ElementType ?? "",
        AdvancedSchemaJson = entry.Type == "Advanced" && entry.Advanced != null ? JsonConvert.SerializeObject(entry.Advanced, Formatting.None, AdvancedSchemaSerializerSettings) : "",
        Options = entry.Options?.Select(option => new ImmConfigOptionPacket { Label = option.Label, ValueJson = option.Value?.ToString(Formatting.None) ?? "null" }).ToArray() ?? Array.Empty<ImmConfigOptionPacket>()
    };
}
