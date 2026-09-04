#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;

namespace IntegratedModManager.Config;

internal sealed class ImmConfigLibOwnershipDetector : IImmExternalManagerDetector
{
	private const string ManagerId = "configlib";
	private const string ManagerName = "ConfigLib";
	private const string SystemName = "ConfigLib.ConfigLibModSystem";
	private const string DescriptorName = "configlib-patches.json";

	public void Detect(ICoreAPI api, ImmExternalManagerOwnership ownership)
	{
		if (!api.ModLoader.IsModEnabled(ManagerId)) { return; }

		ownership.RegisterManager(ManagerId);

		HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

		try
		{
			foreach (IAsset asset in api.Assets.GetMany(AssetCategory.config.Code))
			{
				if (!string.Equals(asset.Name, DescriptorName, StringComparison.OrdinalIgnoreCase)) { continue; }

				string domain = asset.Location.Domain;
				if (string.IsNullOrWhiteSpace(domain)) { continue; }

				claimed.Add(domain);
				ownership.AddClaim(ManagerId, ManagerName, domain);
			}
		}
		catch (Exception exception) { api.Logger.Warning("[integratedmodmanager] ConfigLib declarative ownership detection failed. Runtime ownership detection will still be used: {0}", exception.Message); }

		TryAddRuntimeDomains(api, ownership, claimed);

		string[] claimedMods = claimed.OrderBy(modId => modId, StringComparer.OrdinalIgnoreCase).ToArray();

		if (claimedMods.Length == 0) { api.Logger.Notification("[integratedmodmanager] ConfigLib detected. No managed mod domains were claimed."); }
		else { api.Logger.Notification("[integratedmodmanager] ConfigLib detected. IMM will cede ownership for the following domains: {0}.", string.Join(", ", claimedMods)); }
	}

	private static void TryAddRuntimeDomains(ICoreAPI api, ImmExternalManagerOwnership ownership, HashSet<string> claimed)
	{
		ModSystem? system;

		try { system = api.ModLoader.GetModSystem(SystemName); }
		catch (Exception exception)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigLib is active but its mod system could not be inspected. Declarative ownership detection will still be used: {0}", exception.Message);
			return;
		}

		if (system == null)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigLib is active but its mod system was not found? Declarative ownership detection will still be used.");
			return;
		}

		PropertyInfo? domainsProperty = system.GetType().GetProperty("Domains", BindingFlags.Instance | BindingFlags.Public);

		if (domainsProperty == null)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigLib is active but its managed Domains could not be inspected. Declarative ownership detection will still be used.");
			return;
		}

		object? value;

		try { value = domainsProperty.GetValue(system); }
		catch (Exception exception)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigLib is active but its managed Domains could not be read. Declarative ownership detection will still be used: {0}", exception.Message);
			return;
		}

		if (value is not IEnumerable domains)
		{
			api.Logger.Warning("[integratedmodmanager] ConfigLib is active but its managed Domains value was not enumerable. Declarative ownership detection will still be used.");
			return;
		}

		foreach (object? item in domains)
		{
			if (item is not string domain || string.IsNullOrWhiteSpace(domain)) { continue; }

			claimed.Add(domain);
			ownership.AddClaim(ManagerId, ManagerName, domain);
		}
	}
}
