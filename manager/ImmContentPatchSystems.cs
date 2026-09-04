#nullable enable

using Vintagestory.API.Common;

namespace IntegratedModManager.Config;

public sealed class ImmContentPatchCoordinatorSystem : ModSystem
{
	public ImmExternalManagerOwnership ExternalManagers { get; private set; } = null!;
	public ImmConfigRegistry Registry { get; private set; } = null!;
	public ImmPatchSettingsStore PatchSettings { get; private set; } = null!;
	public ImmContentPatchService ContentPatches { get; private set; } = null!;

	public override bool ShouldLoad(EnumAppSide forSide) { return true; }

	public override double ExecuteOrder() { return -0.001; }

	public override void AssetsLoaded(ICoreAPI api)
	{
		ExternalManagers = new ImmExternalManagerOwnership();
		ExternalManagers.Discover(api);

		Registry = new ImmConfigRegistry();
		Registry.Discover(api);

		PatchSettings = new ImmPatchSettingsStore(api, Registry);

		ContentPatches = new ImmContentPatchService(api, Registry, PatchSettings, ExternalManagers);

		ContentPatches.Initialize();
		ContentPatches.Apply(ImmPatchTiming.Early);
	}
}

public sealed class ImmContentPatchBeforeSystem : ModSystem
{
	public override bool ShouldLoad(EnumAppSide forSide) { return true; }

	public override double ExecuteOrder() { return 0.049; }

	public override void AssetsLoaded(ICoreAPI api) { api.ModLoader.GetModSystem<ImmContentPatchCoordinatorSystem>() ?.ContentPatches.Apply(ImmPatchTiming.BeforePatches); }
}

public sealed class ImmContentPatchAfterSystem : ModSystem
{
	public override bool ShouldLoad(EnumAppSide forSide) { return true; }

	public override double ExecuteOrder() { return 0.051; }

	public override void AssetsLoaded(ICoreAPI api) { api.ModLoader.GetModSystem<ImmContentPatchCoordinatorSystem>() ?.ContentPatches.Apply(ImmPatchTiming.AfterPatches); }
}
