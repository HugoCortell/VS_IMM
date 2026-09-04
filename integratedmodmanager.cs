#nullable enable

using IntegratedModManager.Config;
using IntegratedModManager.ModSelector;
using IntegratedModManager.UI;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace IntegratedModManager;

public sealed class IntegratedModManagerSystem : ModSystem
{
	public const string ModId = "integratedmodmanager";

	private IntegratedModManagerNudge? Nudge;
	private ImmConfigRegistry? ConfigRegistry;
	private ImmDependencyService? DependencyService;
	private ImmConfigClient? ConfigClient;
	private GuiDialogModSelector? ModSelector;
	private GuiDialogModManager? ModManager;
	private GuiDialogNotification? Notification;
	private bool ClientSideStarted;

	public override bool ShouldLoad(EnumAppSide forSide) { return true; }

	public override void Start(ICoreAPI api)
	{
		IntegratedModManagerNudge.RegisterNetwork(api);
		ImmConfigNetwork.RegisterNetwork(api);
	}

	public override void AssetsLoaded(ICoreAPI api)
	{
		ImmContentPatchCoordinatorSystem? coordinator = api.ModLoader.GetModSystem<ImmContentPatchCoordinatorSystem>();

		ConfigRegistry = coordinator?.Registry;
	}

	public override void StartClientSide(ICoreClientAPI api)
	{
		ClientSideStarted = true;
		IntegratedModManagerConfig.Start(api);

		Nudge = new IntegratedModManagerNudge(api);
		Nudge.StartClient();

		ConfigClient = new ImmConfigClient(api);
		Notification = new GuiDialogNotification(api);

		ModManager = new GuiDialogModManager(api, ConfigClient, Notification);
		ModSelector = new GuiDialogModSelector(api, ConfigClient, OpenSelectedMod);

		api.ChatCommands.Create("imm").WithDescription(ImmLocalization.Get("command-description")).HandleWith(_ => { Nudge.Dismiss(); OpenManager(); return TextCommandResult.Success(); });
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		ImmContentPatchCoordinatorSystem? coordinator = api.ModLoader.GetModSystem<ImmContentPatchCoordinatorSystem>();

		if (coordinator == null || ConfigRegistry == null) { api.Logger.Error("[integratedmodmanager] Managed content patch coordinator was not initialized."); return; }

		DependencyService = new ImmDependencyService(api, ConfigRegistry, coordinator.PatchSettings, coordinator.ExternalManagers);
		DependencyService.EvaluateAll();

		IntegratedModManagerNudge.StartServer(api, DependencyService);
		ImmConfigService configService = new(api, ConfigRegistry, DependencyService, coordinator.PatchSettings, coordinator.ContentPatches, coordinator.ExternalManagers);
		ImmConfigNetwork.StartServer(api, configService);
	}

	public void OpenManager() { ModSelector?.TryOpen(); }

	private void OpenSelectedMod(ModSelectorEntry entry)
	{
		if (!entry.HasConfiguration && !entry.HasWarnings && !entry.HasErrors) { Notification?.Show(ImmLocalization.Get("notification-no-configuration"), ImmLocalization.Get("button-alright")); return; }
		if (ModManager == null) { return; }

		ModManager.SetMod(entry.ModId, entry.Name);
		ModManager.TryOpen();
	}

	public override void Dispose()
	{
		if (ClientSideStarted) { IntegratedModManagerConfig.Stop(); }
		ClientSideStarted = false;

		Notification?.Dispose();
		Notification = null;

		ModManager?.Dispose();
		ModManager = null;

		ModSelector?.Dispose();
		ModSelector = null;

		ConfigClient?.Dispose();
		ConfigClient = null;
		DependencyService = null;
		ConfigRegistry = null;

		Nudge?.Dispose();
		Nudge = null;

		base.Dispose();
	}
}
