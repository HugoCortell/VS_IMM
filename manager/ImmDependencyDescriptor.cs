#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmDependencySeverity
{
	Warning,
	Error
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmDependencyCriterionType
{
	Unknown,
	Setting,
	GridRecipeCount,
	HasModID
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmDependencyOperator
{
	Equal,
	NotEqual,
	GreaterThan,
	LessThan
}

[JsonConverter(typeof(StringEnumConverter))]
public enum ImmDependencyResolutionType
{
	Unknown,
	SetSetting,
	InstallMod,
	RunCommand
}

public sealed class ImmDependencyEntry
{
	[JsonIgnore] public int RuntimeId;

	public string ModId = "";
	public string Label = "";
	public string Description = "";
	public ImmDependencySeverity Severity = ImmDependencySeverity.Warning;
	public List<ImmDependencyCriterion> Criteria = new();
	public ImmDependencyResolution? Resolution;
}

public sealed class ImmDependencyCriterion
{
	public ImmDependencyCriterionType Type;
	public ImmDependencySettingTarget? Target;
	public string Output = "";
	public ImmDependencyOperator Operator = ImmDependencyOperator.Equal;
	public JToken? Value;
}

public sealed class ImmDependencyResolution
{
	public ImmDependencyResolutionType Type;
	public ImmDependencySettingTarget? Target;
	public JToken? Value;
	public string ModId = "";
}

public sealed class ImmDependencySettingTarget
{
	// ModConfig target
	public string ConfigFile = "";
	public string Map = "";

	// Optional owning mod for either target type. Defaults to the descriptor owner.
	public string ModId = "";
	public string PatchSetting = "";
}
