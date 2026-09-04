#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace IntegratedModManager.Config;

public sealed class ImmPatchSettingsStore
{
	private static readonly object FileLock = new();

	private readonly ICoreAPI Api;
	private readonly ImmConfigRegistry Registry;
	private readonly string RootPath;

	public ImmPatchSettingsStore(ICoreAPI api, ImmConfigRegistry registry)
	{
		Api = api;
		Registry = registry;
		RootPath = Path.Combine(api.DataBasePath, "moddata", IntegratedModManagerSystem.ModId, "patchsettings");
	}

	public string GetPath(string modId) { ValidateModIdForPath(modId); return Path.Combine(RootPath, modId + ".json"); }

	public JObject ReadDocument(string modId)
	{
		lock (FileLock) { return ReadDocumentUnlocked(modId); }
	}

	public JToken GetEffectiveValue(string modId, ImmConfigEntry entry, ImmConfigSide side) { JObject document = ReadDocument(modId); return GetEffectiveValue(modId, entry, side, document); }

	public JToken GetEffectiveValue(string modId, ImmConfigEntry entry, ImmConfigSide side, JObject document)
	{
		JToken fallback = GetNormalizedDefault(entry);
		JObject? section = document[GetSectionName(side)] as JObject;
		JToken? saved = section?[entry.Code];

		if (saved == null) { return fallback; }
		if (ImmConfigValueValidator.TryNormalizePatchSetting(entry, saved, out JToken normalized, out string error)) { return normalized; }

		Api.Logger.Warning("[integratedmodmanager] Ignoring invalid PatchSettings override {0}:{1} ({2}): {3}", modId, entry.Code, side, error);

		return fallback;
	}

	public bool TryGetServerValue(string modId, string code, out JToken value, out string error)
	{
		value = JValue.CreateNull();

		if (!Registry.TryGetPatchSetting(modId, code, out _, out ImmConfigEntry entry, out ImmConfigSide side)) { error = $"PatchSetting '{code}' was not found for mod '{modId}'."; return false; }
		if (side != ImmConfigSide.Server) { error = $"PatchSetting '{modId}:{code}' is client-owned and cannot be evaluated by the server."; return false; }

		value = GetEffectiveValue(modId, entry, side);

		error = "";
		return true;
	}

	public bool TrySetOverride(string modId, string code, ImmConfigSide side, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;

		if (!Registry.TryGetPatchSetting(modId, code, out _, out ImmConfigEntry entry, out ImmConfigSide declaredSide)) { error = $"PatchSetting '{code}' was not found for mod '{modId}'."; return false; }
		if (declaredSide != side) { error = $"PatchSetting '{modId}:{code}' is not {side}-owned."; return false; }

		return TrySetOverride(modId, entry, side, submitted, out normalized, out error);
	}

	public bool TrySetOverrides(string modId, ImmConfigSide side, IReadOnlyList<(ImmConfigEntry Entry, JToken Value)> changes, out string error) { ImmAtomicFileBatch batch = new(); return TryCommitWithOverrides(modId, side, changes, batch, out error); }

	public bool TryCommitWithOverrides(string modId, ImmConfigSide side, IReadOnlyList<(ImmConfigEntry Entry, JToken Value)> changes, ImmAtomicFileBatch batch, out string error)
	{
		if (changes.Count == 0) { return batch.TryCommit(out error); }

		try
		{
			lock (FileLock)
			{
				JObject document = ReadDocumentUnlocked(modId);

				if (!TryPrepareOverrides(modId, side, changes, document, out JObject prepared, out error)) { return false; }
				AddPreparedDocumentToBatch(modId, prepared, batch);

				return batch.TryCommit(out error);
			}
		}
		catch (Exception exception) { error = $"Failed to save PatchSettings for '{modId}': {exception.Message}"; return false; }
	}

	private bool TryPrepareOverrides(string modId, ImmConfigSide side, IReadOnlyList<(ImmConfigEntry Entry, JToken Value)> changes, JObject sourceDocument, out JObject preparedDocument, out string error)
	{
		preparedDocument = (JObject)sourceDocument.DeepClone();

		if (changes.Count == 0) { error = ""; return true; }

		List<(ImmConfigEntry Entry, JToken Value)> normalizedChanges = new(changes.Count);

		foreach ((ImmConfigEntry entry, JToken value) in changes)
		{
			if (!ImmConfigValueValidator.TryNormalizePatchSetting(entry, value, out JToken normalized, out error)) { return false; }

			normalizedChanges.Add((entry, normalized));
		}

		try
		{
			string sectionName = GetSectionName(side);

			JObject? section = preparedDocument[sectionName] as JObject;

			if (section == null)
			{
				section = new JObject();

				preparedDocument[sectionName] = section;
			}

			foreach ((ImmConfigEntry entry, JToken normalized) in normalizedChanges)
			{
				if (IsDefaultValue(entry, normalized)) { section.Property(entry.Code)?.Remove(); }
				else { section[entry.Code] = normalized.DeepClone(); }
			}

			PruneDocument(modId, preparedDocument);

			error = "";
			return true;
		}
		catch (Exception exception) { error = $"Failed to prepare PatchSettings for '{modId}': {exception.Message}"; return false; }
	}

	public bool TrySetOverride(string modId, ImmConfigEntry entry, ImmConfigSide side, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		if (!ImmConfigValueValidator.TryNormalizePatchSetting(entry, submitted, out normalized, out error)) { return false; }

		return TrySetOverrides(modId, side, new[] { (entry, normalized) }, out error);
	}

	public bool TrySetServerValue(string modId, string code, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;

		if (!Registry.TryGetPatchSetting(modId, code, out _, out ImmConfigEntry entry, out ImmConfigSide side)) { error = $"PatchSetting '{code}' was not found for mod '{modId}'."; return false; }
		if (side != ImmConfigSide.Server) { error = $"PatchSetting '{modId}:{code}' is client-owned and cannot be changed by a server resolution."; return false; }

		return TrySetOverride(modId, entry, side, submitted, out normalized, out error);
	}

	private void AddPreparedDocumentToBatch(string modId, JObject document, ImmAtomicFileBatch batch)
	{
		string path = GetPath(modId);
		if (document.Properties().Any()) { batch.Write(path, document.ToString(Formatting.Indented)); return; }

		batch.Delete(path);
	}

	private JObject ReadDocumentUnlocked(string modId)
	{
		string path = GetPath(modId);

		if (!File.Exists(path)) { return new JObject(); }

		try
		{
			string text = File.ReadAllText(path);
			if (string.IsNullOrWhiteSpace(text)) { return new JObject(); }

			return JObject.Parse(text);
		}
		catch (Exception exception) { Api.Logger.Warning("[integratedmodmanager] Failed to read PatchSettings '{0}': {1}", path, exception.Message); return new JObject(); }
	}

	private void PruneDocument(string modId, JObject document)
	{
		if (!Registry.TryGet(modId, out ImmConfigDescriptor descriptor)) { return; }

		foreach (ImmConfigSide side in new[] { ImmConfigSide.Server, ImmConfigSide.Client })
		{
			string sectionName = GetSectionName(side);

			if (document[sectionName] is not JObject section) { continue; }
			Dictionary<string, ImmConfigEntry> valid = descriptor.Configuration.Where(block => block.ConfigSource == ImmConfigSource.PatchSettings).SelectMany(block => block.Settings.Select(entry => new { Block = block, Entry = entry })).Where(item => (item.Entry.ConfigSide ?? item.Block.ConfigSide) == side).ToDictionary(item => item.Entry.Code, item => item.Entry, StringComparer.Ordinal);

			foreach (JProperty property in section.Properties().ToArray())
			{
				if (!valid.TryGetValue(property.Name, out ImmConfigEntry? entry) || !ImmConfigValueValidator.TryNormalizePatchSetting(entry, property.Value, out JToken normalized, out _)) { property.Remove(); continue; }
				if (IsDefaultValue(entry, normalized)) { property.Remove(); continue; }

				property.Value.Replace(normalized);
			}

			if (!section.Properties().Any()) { document.Property(sectionName)?.Remove(); }
		}

		foreach (JProperty property in document.Properties().ToArray())
		{
			if (property.Name is not ("Server" or "Client")) { property.Remove(); }
		}
	}

	private static JToken GetNormalizedDefault(ImmConfigEntry entry)
	{
		if (entry.Default != null && ImmConfigValueValidator.TryNormalizePatchSetting(entry, entry.Default, out JToken normalized, out _)) { return normalized; }

		return entry.Default?.DeepClone() ?? JValue.CreateNull();
	}

	private static bool IsDefaultValue(ImmConfigEntry entry, JToken value) { return JToken.DeepEquals(value, GetNormalizedDefault(entry)); }

	private static string GetSectionName(ImmConfigSide side) { return side == ImmConfigSide.Server ? "Server" : "Client"; }

	private static void ValidateModIdForPath(string modId)
	{
		if (string.IsNullOrWhiteSpace(modId) || modId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || modId.Contains('/') || modId.Contains('\\') || modId is "." or "..")
		{
			throw new InvalidDataException($"Invalid mod domain '{modId}' for PatchSettings storage.");
		}
	}
}
