using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmConfigSide
{
	Server,
	Client
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmConfigSource
{
	ModConfig,
	PatchSettings
}

public sealed class ImmConfigDescriptor
{
	public List<ImmConfigBlock> Configuration = [];
	public List<ImmDependencyEntry> Dependencies = [];

	public Dictionary<string, JToken> Constants = [];
	public List<ImmContentPatchTarget> ContentPatches = [];
}

public sealed class ImmConfigBlock
{
	public string ConfigFile = "";
	public string ConfigLabel = "";
	public string Description = "";

	public ImmConfigSource ConfigSource = ImmConfigSource.ModConfig;
	public ImmConfigSide ConfigSide = ImmConfigSide.Server;
	public bool ParseDescriptions;

	public List<ImmConfigEntry> Settings = [];
}

public sealed class ImmConfigEntry
{
	public string Type = "";
	public string Label = "";
	public string Description = "";

	// ModConfig settings map directly into the owning config file.
	public string Map = "";

	// PatchSettings are IMM-owned values referenced by stable codes.
	public string Code = "";
	public JToken? Default;
	public List<ImmContentPatchTarget> Targets = [];

	public ImmConfigSide? ConfigSide;

	public double? Min;
	public double? Max;
	public double? Step;
	public string ElementType = "";

	// Only used when Type == "Advanced". Existing controls ignore this entirely.
	public ImmAdvancedSchema? Advanced;

	public List<ImmConfigOption> Options = [];
}

public sealed class ImmConfigOption
{
	public string Label = "";
	public JToken? Value;
}
