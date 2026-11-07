using System.Globalization;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace IntegratedModManager.Config;

public sealed class ImmActiveDependency
{
	public int RuntimeId { get; }
	public string SourceModId { get; }
	public int EntryIndex { get; }
	public ImmDependencySeverity Severity { get; }

	public ImmActiveDependency(int runtimeId, string sourceModId, int entryIndex, ImmDependencySeverity severity)
	{
		RuntimeId = runtimeId;
		SourceModId = sourceModId;
		EntryIndex = entryIndex;
		Severity = severity;
	}
}

public sealed class ImmDependencyService
{
	private readonly ICoreServerAPI Api;
	private readonly ImmConfigRegistry Registry;
	private readonly ImmPatchSettingsStore PatchSettings;
	private readonly ImmExternalManagerOwnership ExternalManagers;
	private readonly bool OptimumActive;
	private readonly bool StratumActive;
	private readonly string? StratumVersion;

	private List<ImmActiveDependency> Active = [];
	private readonly HashSet<int> PendingRestartRuntimeIds = [];
	public IReadOnlyList<ImmActiveDependency> ActiveDependencies => Active;

	public int WarningCount => Active.Count(dependency => dependency.Severity == ImmDependencySeverity.Warning);
	public int ErrorCount => Active.Count(dependency => dependency.Severity == ImmDependencySeverity.Error);

	public ImmDependencyService(ICoreServerAPI api, ImmConfigRegistry registry, ImmPatchSettingsStore patchSettings, ImmExternalManagerOwnership externalManagers)
	{
		Api = api;
		Registry = registry;
		PatchSettings = patchSettings;
		ExternalManagers = externalManagers;

		var runtimeAssembly = api.GetType().Assembly;
		OptimumActive = runtimeAssembly.GetType("Optimum.OptimumInfo", throwOnError: false) != null;
		Type? stratumInfo = runtimeAssembly.GetType("Vintagestory.Server.StratumInfo", throwOnError: false);
		StratumActive = stratumInfo != null;
		try { StratumVersion = stratumInfo?.GetProperty("Version")?.GetValue(null) as string; }
		catch (Exception exception) { api.Logger.Warning("[integratedmodmanager] Could not read Stratum version: {0}", exception.Message); }
		if (StratumActive && !IsValidStratumVersion(StratumVersion))
		{
			api.Logger.Warning("[integratedmodmanager] Stratum version is unavailable or unrecognized; version comparisons will treat it as older than the requested version.");
		}
	}

    public ImmActiveDependency[] GetForMod(string sourceModId) => [.. Active.Where(dependency => string.Equals(dependency.SourceModId, sourceModId, StringComparison.OrdinalIgnoreCase))];

    public void EvaluateAll()
	{
		Dictionary<string, JObject?> configs = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> recipeCounts = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, CollectibleObject?> collectibles = new(StringComparer.OrdinalIgnoreCase);
		List<ImmActiveDependency> active = [];

		foreach (ImmRegisteredDependency registered in Registry.Dependencies)
		{
			if (PendingRestartRuntimeIds.Contains(registered.RuntimeId) || IsActive(registered, configs, recipeCounts, collectibles)) { active.Add(new ImmActiveDependency(registered.RuntimeId, registered.SourceModId, registered.EntryIndex, registered.Entry.Severity)); }
		}

		Active = active;
	}

	public bool TryResolve(int runtimeId, out string chatCommand, out ImmDependencyResolutionWarning warning, out string error)
	{
		chatCommand = "";
		warning = ImmDependencyResolutionWarning.None;
		error = "";

		if (!Registry.TryGetDependency(runtimeId, out ImmRegisteredDependency registered)) { error = "Dependency entry is no longer available."; return false; }
		if (!Active.Any(dependency => dependency.RuntimeId == runtimeId)) { error = "This dependency issue is no longer active."; return false; }

		Dictionary<string, JObject?> configs = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> recipeCounts = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, CollectibleObject?> collectibles = new(StringComparer.OrdinalIgnoreCase);

		if (!PendingRestartRuntimeIds.Contains(runtimeId) && !IsActive(registered, configs, recipeCounts, collectibles))
		{
			EvaluateAll();
			error = "This dependency issue is no longer active.";
			return false;
		}

		ImmDependencyResolution? resolution = registered.Entry.Resolution;

		if (resolution is null) { error = "This dependency issue has no automatic resolution."; return false; }
		bool success;

		switch (resolution.Type)
		{
			case ImmDependencyResolutionType.SetSetting:
				success = TryApplySettingResolution(registered.SourceModId, resolution, out error);
			break;

			case ImmDependencyResolutionType.InstallMod:
				chatCommand = $"/moddb install {resolution.ModId} {GameVersion.ShortGameVersion}";
				success = true;
			break;

			case ImmDependencyResolutionType.RunCommand:
				chatCommand = resolution.Value?.Value<string>() ?? "";
				success = !string.IsNullOrWhiteSpace(chatCommand);

				if (!success) { error = "RunCommand resolution has no command."; }
			break;

			default:
				error = "Unsupported dependency resolution type.";
				success = false;
			break;
		}

		if (success) { PendingRestartRuntimeIds.Add(runtimeId); }
		if (success && resolution.Type == ImmDependencyResolutionType.SetSetting) { warning = GetResolutionWarning(registered.SourceModId, resolution); }
		if (success && string.IsNullOrWhiteSpace(chatCommand)) { EvaluateAll(); }

		return success;
	}

	private ImmDependencyResolutionWarning GetResolutionWarning(string sourceModId, ImmDependencyResolution resolution)
	{
		ImmDependencySettingTarget? target = resolution.Target;
		if (target is null) { return ImmDependencyResolutionWarning.None; }

		string targetModId = string.IsNullOrWhiteSpace(target.ModId) ? sourceModId : target.ModId;
		if (!ExternalManagers.IsControlled(targetModId)) { return ImmDependencyResolutionWarning.None; }

		return !string.IsNullOrWhiteSpace(target.PatchSetting) ? ImmDependencyResolutionWarning.ExternallyManagedPatchSetting : ImmDependencyResolutionWarning.ExternallyManagedModConfig;
	}

	private bool IsActive(ImmRegisteredDependency registered, Dictionary<string, JObject?> configs, Dictionary<string, int> recipeCounts, Dictionary<string, CollectibleObject?> collectibles)
	{
		ImmDependencyEntry entry = registered.Entry;

		if (!string.Equals(entry.ModId, "game", StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.ModId, registered.SourceModId, StringComparison.OrdinalIgnoreCase) && !Api.ModLoader.IsModEnabled(entry.ModId)) { return false; }

		foreach (ImmDependencyCriterion criterion in entry.Criteria)
		{
			if (!TryEvaluateCriterion(registered.SourceModId, criterion, configs, recipeCounts, collectibles, out bool matched, out string error)) { Api.Logger.Warning("[integratedmodmanager] Could not evaluate dependency {0}[{1}] ({2}): {3}", registered.SourceModId, registered.EntryIndex, registered.RuntimeId, error); return false; }

			if (!matched) { return false; }
		}

		return true;
	}

	private bool TryEvaluateCriterion(string sourceModId, ImmDependencyCriterion criterion, Dictionary<string, JObject?> configs, Dictionary<string, int> recipeCounts, Dictionary<string, CollectibleObject?> collectibles, out bool matched, out string error)
	{
		switch (criterion.Type)
		{
			case ImmDependencyCriterionType.Setting: return TryEvaluateSetting(sourceModId, criterion, configs, out matched, out error);

			case ImmDependencyCriterionType.GridRecipeCount: return TryEvaluateGridRecipeCount(criterion, recipeCounts, out matched, out error);

			case ImmDependencyCriterionType.HasModID: return TryEvaluateHasModID(criterion, out matched, out error);

			case ImmDependencyCriterionType.Backend: return TryEvaluateBackend(criterion, out matched, out error);

			case ImmDependencyCriterionType.CollectibleBehavior: return TryEvaluateCollectibleBehavior(criterion, collectibles, out matched, out error);

			case ImmDependencyCriterionType.CollectibleAttribute: return TryEvaluateCollectibleAttribute(criterion, collectibles, out matched, out error);

			default:
				matched = false;
				error = "Unsupported criterion type.";
			return false;
		}
	}

	private bool TryEvaluateBackend(ImmDependencyCriterion criterion, out bool matched, out string error)
	{
		bool exists = criterion.Backend switch
		{
			ImmDependencyBackend.Optimum => OptimumActive,
			ImmDependencyBackend.Stratum => StratumActive,
			_ => false
		};

		error = "";
		if (criterion.Operator is ImmDependencyOperator.Exists or ImmDependencyOperator.NotExists)
		{
			matched = criterion.Operator == ImmDependencyOperator.Exists ? exists : !exists;
			return true;
		}

		if (!exists) { matched = false; return true; }

		// An unknown version is conservatively considered older (for minimum-version warnings).
		if (!TryCompareStratumVersions(StratumVersion, criterion.Value?.Value<string>(), out int comparison))
		{
			matched = criterion.Operator == ImmDependencyOperator.LessThan;
			return true;
		}

		matched = criterion.Operator switch
		{
			ImmDependencyOperator.Equal => comparison == 0,
			ImmDependencyOperator.NotEqual => comparison != 0,
			ImmDependencyOperator.GreaterThan => comparison > 0,
			ImmDependencyOperator.LessThan => comparison < 0,
			_ => false
		};
		return true;
	}

	private bool TryEvaluateCollectibleBehavior(ImmDependencyCriterion criterion, Dictionary<string, CollectibleObject?> collectibles, out bool matched, out string error)
	{
		matched = false;

		ImmDependencyCriterionTarget target = criterion.Target!;
		if (!TryGetCollectible(target, collectibles, out CollectibleObject? collectible, out error) || collectible is null) { return false; }

		ImmDependencyCollectibleClass collectibleClass = target.Class!.Value;
		Type? behaviorType = Api.ClassRegistry.GetCollectibleBehaviorClass(criterion.Behavior);

		if (collectibleClass == ImmDependencyCollectibleClass.Block)
		{
			Type? blockBehaviorType = Api.ClassRegistry.GetBlockBehaviorClass(criterion.Behavior);
			if (blockBehaviorType != null) { behaviorType = blockBehaviorType; }
		}

		bool exists = behaviorType != null && collectible.GetCollectibleBehavior(behaviorType, withInheritance: true) != null;

		matched = criterion.Operator == ImmDependencyOperator.Exists ? exists : !exists;
		error = "";
		return true;
	}

	private bool TryEvaluateCollectibleAttribute(ImmDependencyCriterion criterion, Dictionary<string, CollectibleObject?> collectibles, out bool matched, out string error)
	{
		matched = false;

		ImmDependencyCriterionTarget target = criterion.Target!;
		if (!TryGetCollectible(target, collectibles, out CollectibleObject? collectible, out error) || collectible is null) { return false; }

		JToken? attributes = collectible.Attributes?.Token;
		IReadOnlyList<JToken> values;

		if (attributes is null) { values = Array.Empty<JToken>(); }
		else
		{
			try
			{
				ImmContentPath path = criterion.CompiledPath ?? ImmContentPath.Compile(criterion.Path);
				values = path.Resolve(attributes);
			}
			catch (Exception exception)
			{
				error = $"Failed to resolve collectible attribute Path '{criterion.Path}': {exception.Message}";
				return false;
			}
		}

		if (criterion.Operator is ImmDependencyOperator.Exists or ImmDependencyOperator.NotExists)
		{
			bool exists = values.Count > 0;
			matched = criterion.Operator == ImmDependencyOperator.Exists ? exists : !exists;
			error = "";
			return true;
		}

		if (values.Count == 0)
		{
			error = "";
			return true;
		}

		if (values.Count > 1)
		{
			error = $"Collectible attribute Path '{criterion.Path}' resolved {values.Count} values; comparison criteria require exactly one.";
			return false;
		}

		return TryCompare(values[0], criterion.Value!, criterion.Operator, out matched, out error);
	}

	private bool TryGetCollectible(ImmDependencyCriterionTarget target, Dictionary<string, CollectibleObject?> collectibles, out CollectibleObject? collectible, out string error)
	{
		string cacheKey = $"{target.Class}:{target.Code}";

		if (!collectibles.TryGetValue(cacheKey, out collectible))
		{
			AssetLocation code;

			try { code = new AssetLocation(target.Code); }
			catch (Exception exception)
			{
				error = $"Collectible Code '{target.Code}' is invalid: {exception.Message}";
				return false;
			}

			collectible = target.Class == ImmDependencyCollectibleClass.Block ? Api.World.GetBlock(code) : Api.World.GetItem(code);
			collectibles[cacheKey] = collectible;
		}

		if (collectible is null)
		{
			error = $"{target.Class} collectible '{target.Code}' was not found.";
			return false;
		}

		error = "";
		return true;
	}

	private bool TryEvaluateHasModID(ImmDependencyCriterion criterion, out bool matched, out string error)
	{
		string requiredModId = criterion.Value?.Value<string>() ?? "";

		if (string.IsNullOrWhiteSpace(requiredModId))
		{
			matched = false;
			error = "HasModID requires a non-empty mod ID string.";
			return false;
		}

		bool hasMod = string.Equals(requiredModId, "game", StringComparison.OrdinalIgnoreCase) || Api.ModLoader.IsModEnabled(requiredModId);

		// HasModID describes a required mod. The dependency issue becomes active when that required mod is missing.
		matched = !hasMod;
		error = "";
		return true;
	}

	private bool TryEvaluateSetting(string sourceModId, ImmDependencyCriterion criterion, Dictionary<string, JObject?> configs, out bool matched, out string error)
	{
		matched = false;

		ImmDependencySettingTarget target = criterion.Target!;

		if (!string.IsNullOrWhiteSpace(target.PatchSetting))
		{
			string targetModId = string.IsNullOrWhiteSpace(target.ModId) ? sourceModId : target.ModId;
			if (!PatchSettings.TryGetServerValue(targetModId, target.PatchSetting, out JToken patchValue, out error)) { return false; }

			return TryCompare(patchValue, criterion.Value!, criterion.Operator, out matched, out error);
		}

		if (!TryGetConfig(target.ConfigFile, configs, out JObject? config, out error) || config is null) { return false; }
		JToken? current;

		try { current = config.SelectToken(target.Map); }
		catch (Exception exception) { error = $"Invalid setting Map '{target.Map}': {exception.Message}"; return false; }

		if (current is null) { error = $"Setting '{target.ConfigFile}' to '{target.Map}' was not found."; return false; }
		if (current is JContainer || current.Type is JTokenType.Null or JTokenType.Undefined) { error = $"Setting '{target.ConfigFile}' to '{target.Map}' is not a primitive value."; return false; }

		return TryCompare(current, criterion.Value!, criterion.Operator, out matched, out error);
	}

	private bool TryEvaluateGridRecipeCount(ImmDependencyCriterion criterion, Dictionary<string, int> recipeCounts, out bool matched, out string error)
	{
		if (!recipeCounts.TryGetValue(criterion.Output, out int count))
		{
			AssetLocation outputCode = new(criterion.Output);

			count = Api.World.GridRecipes.Count(recipe => { if (!recipe.Enabled || recipe.Output is null) { return false; } AssetLocation? recipeOutput = recipe.Output.ResolvedItemStack?.Collectible?.Code ?? recipe.Output.Code; return recipeOutput != null && recipeOutput.Equals(outputCode); });
			recipeCounts[criterion.Output] = count;
		}

		return TryCompare(new JValue(count), criterion.Value!, criterion.Operator, out matched, out error);
	}

	private static bool TryCompare(JToken current, JToken expected, ImmDependencyOperator comparison, out bool matched, out string error)
	{
		error = "";

		switch (comparison)
		{
			case ImmDependencyOperator.Equal:
				matched = ValuesEqual(current, expected);
			return true;

			case ImmDependencyOperator.NotEqual:
				matched = !ValuesEqual(current, expected);
			return true;

			case ImmDependencyOperator.GreaterThan:
			case ImmDependencyOperator.LessThan:
				if (!TryGetNumber(current, out double currentNumber) || !TryGetNumber(expected, out double expectedNumber))
				{
					matched = false;
					error = $"{comparison} requires numeric values.";
					return false;
				}

				matched = comparison == ImmDependencyOperator.GreaterThan ? currentNumber > expectedNumber : currentNumber < expectedNumber;

			return true;

			default:
				matched = false;
				error = "Unsupported comparison operator.";
			return false;
		}
	}

	private static bool ValuesEqual(JToken left, JToken right)
	{
		if (TryGetNumber(left, out double leftNumber) && TryGetNumber(right, out double rightNumber)) { return leftNumber.Equals(rightNumber); }
		return JToken.DeepEquals(left, right);
	}

	private static bool TryGetNumber(JToken token, out double value)
	{
		value = 0;

		if (token.Type is not (JTokenType.Integer or JTokenType.Float)) { return false; }

		try { value = token.Value<double>(); return double.IsFinite(value); }
		catch { return false; }
	}

	internal static bool IsValidStratumVersion(string? value) => TryParseStratumVersion(value, out _, out _, out _);

	private static bool TryCompareStratumVersions(string? current, string? expected, out int comparison)
	{
		comparison = 0;
		if (!TryParseStratumVersion(current, out Version currentGame, out int currentRevision, out string[] currentPrerelease)
			|| !TryParseStratumVersion(expected, out Version expectedGame, out int expectedRevision, out string[] expectedPrerelease)) { return false; }

		comparison = currentGame.CompareTo(expectedGame);
		if (comparison == 0) { comparison = currentRevision.CompareTo(expectedRevision); }
		if (comparison == 0) { comparison = CompareStratumPrerelease(currentPrerelease, expectedPrerelease); }
		return true;
	}

	private static bool TryParseStratumVersion(string? value, out Version game, out int revision, out string[] prerelease)
	{
		game = new Version(0, 0, 0);
		revision = 0;
		prerelease = [];
		if (string.IsNullOrWhiteSpace(value)) { return false; }

		string version = value.Trim();
		if (version.StartsWith('v') || version.StartsWith('V')) { version = version[1..]; }
		int metadata = version.IndexOf('+');
		if (metadata >= 0) { version = version[..metadata]; }

		const string marker = "-stratum.";
		int markerIndex = version.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		if (markerIndex <= 0 || !Version.TryParse(version[..markerIndex], out Version? parsedGame) || parsedGame.Build < 0 || parsedGame.Revision >= 0) { return false; }

		string remainder = version[(markerIndex + marker.Length)..];
		int suffixIndex = remainder.IndexOf('-');
		string revisionText = suffixIndex < 0 ? remainder : remainder[..suffixIndex];
		if (!int.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out revision)) { return false; }

		if (suffixIndex >= 0)
		{
			string suffix = remainder[(suffixIndex + 1)..];
			if (suffix.Length == 0) { return false; }
			prerelease = suffix.Split('.');
			if (prerelease.Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) { return false; }
		}

		game = parsedGame;
		return true;
	}

	private static int CompareStratumPrerelease(string[] current, string[] expected)
	{
		// Stable releases sort after their own prereleases, e.g. .3 > .3-indev.1.
		if (current.Length == 0) { return expected.Length == 0 ? 0 : 1; }
		if (expected.Length == 0) { return -1; }

		for (int index = 0; index < Math.Min(current.Length, expected.Length); index++)
		{
			string left = current[index];
			string right = expected[index];
			bool leftNumeric = left.All(char.IsAsciiDigit);
			bool rightNumeric = right.All(char.IsAsciiDigit);
			int order;

			if (leftNumeric && rightNumeric)
			{
				// Compare digit strings without overflow (indev.10 > indev.2).
				left = left.TrimStart('0');
				right = right.TrimStart('0');
				order = left.Length.CompareTo(right.Length);
				if (order == 0) { order = string.CompareOrdinal(left, right); }
			}
			else if (leftNumeric != rightNumeric) { order = leftNumeric ? -1 : 1; }
			else { order = string.CompareOrdinal(left, right); }

			if (order != 0) { return order; }
		}

		return current.Length.CompareTo(expected.Length);
	}

	private bool TryApplySettingResolution(string sourceModId, ImmDependencyResolution resolution, out string error)
	{
		error = "";

		ImmDependencySettingTarget target = resolution.Target!;

		if (!string.IsNullOrWhiteSpace(target.PatchSetting)) { string targetModId = string.IsNullOrWhiteSpace(target.ModId) ? sourceModId : target.ModId; return PatchSettings.TrySetServerValue(targetModId, target.PatchSetting, resolution.Value!, out _, out error); }

		JObject? config;

		try { config = Api.LoadModConfig<JObject>(target.ConfigFile); }
		catch (Exception exception) { error = $"Failed to read '{target.ConfigFile}': {exception.Message}"; return false; }

		if (config is null) { error = $"Config file '{target.ConfigFile}' was not found."; return false; }

		JToken? current;

		try { current = config.SelectToken(target.Map); }
		catch (Exception exception) { error = $"Invalid setting Map '{target.Map}': {exception.Message}"; return false; }

		if (current is null) { error = $"Setting '{target.ConfigFile}' to '{target.Map}' was not found."; return false; }

		if (!TryNormalizeResolutionValue(current, resolution.Value!, out JToken normalized, out error)) { return false; }

		try
		{
			current.Replace(normalized);
			Api.StoreModConfig(config, target.ConfigFile);
		}
		catch (Exception exception) { error = $"Failed to save '{target.ConfigFile}': {exception.Message}"; return false; }

		return true;
	}

	private static bool TryNormalizeResolutionValue(JToken current, JToken requested, out JToken normalized, out string error)
	{
		normalized = requested.DeepClone();
		error = "";

		switch (current.Type)
		{
			case JTokenType.Boolean:
				if (requested.Type == JTokenType.Boolean) { return true; }
			break;

			case JTokenType.String:
				if (requested.Type == JTokenType.String) { return true; }
			break;

			case JTokenType.Integer:
				if (requested.Type == JTokenType.Integer) { return true; }
			break;

			case JTokenType.Float:
				if (TryGetNumber(requested, out double numericValue)) { normalized = new JValue(numericValue); return true; }
			break;

			default:
				error = "Automatic setting resolution only supports boolean, string, integer, and decimal values.";
			return false;
		}

		error = $"Resolution value type does not match the current {current.Type} setting.";
		return false;
	}

	private bool TryGetConfig(string configFile, Dictionary<string, JObject?> configs, out JObject? config, out string error)
	{
		if (configs.TryGetValue(configFile, out config)) { error = config is null ? $"Config file '{configFile}' was not found." : ""; return config != null; }

		try
		{
			config = Api.LoadModConfig<JObject>(configFile);

			configs[configFile] = config;

			if (config is null) { error = $"Config file '{configFile}' was not found."; return false; }

			error = "";
			return true;
		}
		catch (Exception exception)
		{
			config = null;
			configs[configFile] = null;
			error = $"Failed to read '{configFile}': {exception.Message}";
			return false;
		}
	}
}
