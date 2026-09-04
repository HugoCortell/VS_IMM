#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using IntegratedModManager.Config;
using IntegratedModManager.UI;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace IntegratedModManager.ModSelector;

public sealed class ModSelectorEntry
{
	public Mod? LocalMod { get; }
	public string Name { get; }
	public string ModId { get; }
	public bool HasConfiguration { get; }
	public int WarningCount { get; }
	public int ErrorCount { get; }

	public bool HasWarnings => WarningCount > 0;
	public bool HasErrors => ErrorCount > 0;

	public ModSelectorEntry(ImmServerModPacket serverMod, Mod? localMod)
	{
		LocalMod = localMod;
		ModId = serverMod.ModId;
		Name = string.IsNullOrWhiteSpace(serverMod.Name) ? serverMod.ModId : serverMod.Name;

		HasConfiguration = serverMod.HasConfiguration;
		WarningCount = Math.Max(0, serverMod.WarningCount);
		ErrorCount = Math.Max(0, serverMod.ErrorCount);
	}
}

public sealed class GuiElementModGrid : GuiElement
{
	private const double RowGapGui = 16;
	private const double ColumnGapGui = 18;
	private const double ScrollbarReserveGui = 26;
	private const double ScrollbarHeightGui = 14;
	private const double ScrollbarWidthFraction = 0.70;
	private const double WheelStepGui = 110;
	private const double HoverIconScale = 1.08;

	private static readonly int CardBackgroundColor = ColorUtil.ColorFromRgba(49, 41, 33, 145);

	private static readonly int HoverCardBackgroundColor = ColorUtil.ColorFromRgba(70, 62, 53, 175);

	private readonly List<ModSelectorEntry> AllEntries;
	private readonly Dictionary<ModSelectorEntry, CardTexture> Textures = new();
	private readonly List<ModSelectorEntry> VisibleEntries = new();
	private readonly Action<ModSelectorEntry> ModClicked;
	private readonly int Rows;
	private readonly ImmImportantInformationHighlight HighlightMode;

	private LoadedTexture? MissingIconTexture;
	private LoadedTexture? DiagnosticFillTexture;
	private LoadedTexture? ScrollbarTrackTexture;
	private LoadedTexture? ScrollbarHandleTexture;
	private ElementBounds? ViewportBounds;

	private int ScrollbarTextureWidthPixels;
	private int ScrollbarTextureHeightPixels;
	private int ScrollbarHandleTextureWidthPixels;
	private int ScrollbarHandleTextureHeightPixels;

	private readonly Vec4f WarningPulseColor = new();
	private readonly Vec4f WarningHoverPulseColor = new();
	private readonly Vec4f ErrorPulseColor = new();
	private readonly Vec4f ErrorHoverPulseColor = new();
	private readonly Vec4f NeutralCardColor = new();
	private readonly Vec4f NeutralHoverCardColor = new();

	private double RowHeight;
	private double CellWidth;
	private double IconSize;
	private double ColumnPitch;
	private double ContentWidth;
	private double ScrollOffset;
	private double MaximumScroll;

	private double ScrollbarX;
	private double ScrollbarY;
	private double ScrollbarWidth;
	private double ScrollbarHeight;
	private double ScrollbarHandleX;
	private double ScrollbarHandleWidth;

	private bool DraggingScrollbar;
	private bool HasVisibleDiagnostics;
	private double ScrollbarDragOffset;

	public GuiElementModGrid(ICoreClientAPI capi, ElementBounds bounds, IEnumerable<ModSelectorEntry> entries, Action<ModSelectorEntry> modClicked, int rows, ImmImportantInformationHighlight highlightMode) : base(capi, bounds)
	{
		AllEntries = entries.ToList();
		VisibleEntries.AddRange(AllEntries);
		UpdateVisibleDiagnostics();
		ModClicked = modClicked;
		Rows = Math.Clamp(rows, 1, 6);
		HighlightMode = highlightMode;

		ImmDiagnosticPulse.SetNeutralCardColor(hovered: false, NeutralCardColor);

		ImmDiagnosticPulse.SetNeutralCardColor(hovered: true, NeutralHoverCardColor);

		if (HighlightMode == ImmImportantInformationHighlight.Flat) { UpdateDiagnosticColors(0.5); }
	}

	public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
	{
		Bounds.CalcWorldBounds();

		RecalculateGeometry();
		BuildTextures();

		if (DiagnosticFillTexture == null) { DiagnosticFillTexture = ImmDiagnosticPulse.CreateSolidTexture(api); }

		EnsureScrollbarTextures();
	}

	public void SetVisibleEntries(IEnumerable<ModSelectorEntry> entries)
	{
		VisibleEntries.Clear();
		VisibleEntries.AddRange(entries);
		UpdateVisibleDiagnostics();

		ScrollOffset = 0;
		RecalculateGeometry();
		EnsureScrollbarTextures();
	}

	private void UpdateVisibleDiagnostics()
	{
		HasVisibleDiagnostics = false;

		foreach (ModSelectorEntry entry in VisibleEntries)
		{
			if (entry.HasErrors || entry.HasWarnings) { HasVisibleDiagnostics = true; return; }
		}
	}

	private void UpdateDiagnosticColors(double phase)
	{
		ImmDiagnosticPulse.SetCardColor(ImmDiagnosticLevel.Warning, phase, hovered: false, WarningPulseColor);
		ImmDiagnosticPulse.SetCardColor(ImmDiagnosticLevel.Warning, phase, hovered: true, WarningHoverPulseColor);
		ImmDiagnosticPulse.SetCardColor(ImmDiagnosticLevel.Error, phase, hovered: false, ErrorPulseColor);
		ImmDiagnosticPulse.SetCardColor(ImmDiagnosticLevel.Error, phase, hovered: true, ErrorHoverPulseColor);
	}

	private void RecalculateGeometry()
	{
		Bounds.CalcWorldBounds();

		double scale = Math.Max(0.1, RuntimeEnv.GUIScale);
		double reserve = GuiElement.scaled(ScrollbarReserveGui);
		double viewportHeight = Math.Max(GuiElement.scaled(100), Bounds.InnerHeight - reserve);

		double rowGapGui = Math.Max(8, RowGapGui - Math.Max(0, Rows - 2) * 2);

		double rowGap = GuiElement.scaled(rowGapGui);
		double totalRowGap = rowGap * Math.Max(0, Rows - 1);

		RowHeight = Math.Max(1, (viewportHeight - totalRowGap) / Rows);

		IconSize = Math.Clamp(RowHeight - GuiElement.scaled(26), GuiElement.scaled(14), GuiElement.scaled(128));

		CellWidth = Math.Clamp(IconSize + GuiElement.scaled(32), GuiElement.scaled(90), GuiElement.scaled(180));

		ColumnPitch = CellWidth + GuiElement.scaled(ColumnGapGui);

		int columns = (VisibleEntries.Count + Rows - 1) / Rows;
		ContentWidth = columns == 0 ? 0 : columns * CellWidth + Math.Max(0, columns - 1) * GuiElement.scaled(ColumnGapGui);

		MaximumScroll = Math.Max(0, ContentWidth - Bounds.InnerWidth);
		ScrollOffset = Math.Clamp(ScrollOffset, 0, MaximumScroll);

		double viewportHeightGui = viewportHeight / scale;
		ViewportBounds = ElementBounds.Fixed(0, 0, Bounds.fixedWidth, viewportHeightGui).WithParent(Bounds);

		ViewportBounds.CalcWorldBounds();

		ScrollbarWidth = Bounds.InnerWidth * ScrollbarWidthFraction;
		ScrollbarHeight = GuiElement.scaled(ScrollbarHeightGui);
		ScrollbarX = Bounds.renderX + (Bounds.InnerWidth - ScrollbarWidth) / 2;
		ScrollbarY = Bounds.renderY + viewportHeight + (reserve - ScrollbarHeight) / 2;

		UpdateScrollbarHandle();
	}

	private void BuildTextures()
	{
		DisposeTextures();

		if (AllEntries.Count == 0) { return; }

		int iconPixels = Math.Max(1, (int)Math.Round(IconSize));
		int nameMaxWidth = Math.Max(32, (int)Math.Round(CellWidth - GuiElement.scaled(10)));

		double rowHeightGui = RowHeight / Math.Max(0.1, RuntimeEnv.GUIScale);

		float nameFontSize = (float)Math.Clamp(rowHeightGui * 0.22, 9, 15);

		CairoFont nameFont = CairoFont.WhiteDetailText().WithFontSize(nameFontSize).WithWeight(FontWeight.Bold).WithOrientation(EnumTextOrientation.Center);

		MissingIconTexture = CreateMissingIconTexture(iconPixels);

		foreach (ModSelectorEntry entry in AllEntries)
		{
			LoadedTexture? iconTexture = null;
			BitmapExternal? icon = entry.LocalMod?.Icon;

			if (icon != null)
			{
				ImageSurface? iconSurface = null;
				LoadedTexture? loadedIcon = null;

				try
				{
					iconSurface = GuiElement.getImageSurfaceFromAsset(icon, iconPixels, iconPixels);

					loadedIcon = new LoadedTexture(api);
					api.Gui.LoadOrUpdateCairoTexture(iconSurface, true, ref loadedIcon);

					iconTexture = loadedIcon;
					loadedIcon = null;
				}
				catch
				{
					loadedIcon?.Dispose();
					iconTexture = null;
				}
				finally { iconSurface?.Dispose(); }
			}

			bool singleWord = !entry.Name.Any(char.IsWhiteSpace);

			double measuredNameWidth = nameFont.GetTextExtents(entry.Name).Width;

			LoadedTexture nameTexture = singleWord && measuredNameWidth <= nameMaxWidth ? api.Gui.TextTexture.GenTextTexture(entry.Name, nameFont) : api.Gui.TextTexture.GenTextTexture(entry.Name, nameFont, nameMaxWidth, null, EnumTextOrientation.Center);

			Textures[entry] = new CardTexture(iconTexture, nameTexture);
		}

		// CairoFont.Dispose() in 1.22.2 assumes SetupContext() was called. Vanilla GUI code likewise leaves these short-lived font descriptors undisposed.
	}

	private LoadedTexture CreateMissingIconTexture(int size)
	{
		using ImageSurface surface = new ImageSurface(Format.Argb32, size, size);

		using Context ctx = new Context(surface);

		ctx.SetSourceRGBA(0.13, 0.11, 0.09, 0.95);
		ctx.Paint();

		ctx.SetSourceRGBA(0.66, 0.55, 0.42, 0.85);
		ctx.LineWidth = Math.Max(1, size / 32.0);
		ctx.Rectangle(1, 1, Math.Max(1, size - 2), Math.Max(1, size - 2));
		ctx.Stroke();

		LoadedTexture texture = new LoadedTexture(api);

		try { api.Gui.LoadOrUpdateCairoTexture(surface, true, ref texture); return texture; }
		catch
		{
			texture.Dispose();
			throw;
		}
	}

	public override void RenderInteractiveElements(float deltaTime)
	{
		if (ViewportBounds == null || VisibleEntries.Count == 0) { RenderScrollbar(); return; }

		ModSelectorEntry? hoveredEntry = GetEntryAt(api.Input.MouseX, api.Input.MouseY);

		bool highlightDiagnostics = HasVisibleDiagnostics && ImmDiagnosticPulse.IsHighlightEnabled(HighlightMode);

		if (highlightDiagnostics && HighlightMode == ImmImportantInformationHighlight.Pulsating) { UpdateDiagnosticColors(ImmDiagnosticPulse.GetPhase(api.ElapsedMilliseconds)); }

		double rowGapGui = Math.Max(8, RowGapGui - Math.Max(0, Rows - 2) * 2);

		double rowGap = GuiElement.scaled(rowGapGui);
		int totalColumns = (VisibleEntries.Count + Rows - 1) / Rows;

		int firstColumn = Math.Max(0, (int)Math.Floor(ScrollOffset / ColumnPitch));

		int lastColumn = Math.Min(totalColumns - 1, (int)Math.Ceiling((ScrollOffset + Bounds.InnerWidth) / ColumnPitch));

		api.Render.PushScissor(ViewportBounds, true);

		for (int column = firstColumn; column <= lastColumn; column++)
		{
			double cellX = Bounds.renderX + column * ColumnPitch - ScrollOffset;

			for (int row = 0; row < Rows; row++)
			{
				int index = column * Rows + row;

				if (index >= VisibleEntries.Count) { break; }

				ModSelectorEntry entry = VisibleEntries[index];

				if (!Textures.TryGetValue(entry, out CardTexture? texture)) { continue; }

				double cellY = Bounds.renderY + row * (RowHeight + rowGap);

				bool hovered = ReferenceEquals(entry, hoveredEntry);

				Vec4f? diagnosticColor = highlightDiagnostics && entry.HasErrors ? hovered ? ErrorHoverPulseColor : ErrorPulseColor : highlightDiagnostics && entry.HasWarnings ? hovered ? WarningHoverPulseColor : WarningPulseColor : null;

				RenderCard(cellX, cellY, texture, hovered, diagnosticColor);
			}
		}

		api.Render.PopScissor();
		RenderScrollbar();
	}

	private void RenderCard(double cellX, double cellY, CardTexture texture, bool hovered, Vec4f? diagnosticColor)
	{
		if (DiagnosticFillTexture != null)
		{
			Vec4f fillColor = diagnosticColor ?? (hovered ? NeutralHoverCardColor : NeutralCardColor);

			api.Render.Render2DTexture(DiagnosticFillTexture.TextureId, (float)cellX, (float)cellY, (float)CellWidth, (float)RowHeight, 50, fillColor);
		}

		api.Render.RenderRectangle((float)cellX, (float)cellY, 51, (float)CellWidth, (float)RowHeight, hovered ? HoverCardBackgroundColor : CardBackgroundColor);

		LoadedTexture iconTexture = texture.IconTexture ?? MissingIconTexture!;

		double iconScale = hovered ? HoverIconScale : 1.0;
		double renderedIconSize = IconSize * iconScale;

		double iconX = cellX + (CellWidth - renderedIconSize) / 2;

		double contentHeight = IconSize + GuiElement.scaled(4) + texture.NameTexture.Height;

		double contentY = cellY + Math.Max(0, (RowHeight - contentHeight) / 2);

		double iconY = contentY - (renderedIconSize - IconSize) / 2;

		api.Render.Render2DTexturePremultipliedAlpha(iconTexture.TextureId, iconX, iconY, renderedIconSize, renderedIconSize, 52);

		double nameX = cellX + (CellWidth - texture.NameTexture.Width) / 2;

		api.Render.Render2DTexturePremultipliedAlpha(texture.NameTexture.TextureId, nameX, contentY + IconSize + GuiElement.scaled(4), texture.NameTexture.Width, texture.NameTexture.Height, 52);
	}

	private void RenderScrollbar()
	{
		if (MaximumScroll <= 0) { return; }

		UpdateScrollbarHandle();
		EnsureScrollbarTextures();

		if (ScrollbarTrackTexture == null || ScrollbarHandleTexture == null) { return; }

		api.Render.Render2DTexturePremultipliedAlpha(ScrollbarTrackTexture.TextureId, (int)ScrollbarX, (int)ScrollbarY, (int)Math.Round(ScrollbarWidth), (int)Math.Round(ScrollbarHeight), 53);

		api.Render.Render2DTexturePremultipliedAlpha(ScrollbarHandleTexture.TextureId, (int)ScrollbarHandleX, (int)ScrollbarY, (int)Math.Round(ScrollbarHandleWidth), (int)Math.Round(ScrollbarHeight), 54);
	}

	private void EnsureScrollbarTextures()
	{
		if (MaximumScroll <= 0) { return; }

		int trackWidth = Math.Max(1, (int)Math.Round(ScrollbarWidth));

		int trackHeight = Math.Max(1, (int)Math.Round(ScrollbarHeight));

		int handleWidth = Math.Max(1, (int)Math.Round(ScrollbarHandleWidth));

		if (ScrollbarTrackTexture == null || ScrollbarTextureWidthPixels != trackWidth || ScrollbarTextureHeightPixels != trackHeight)
		{
			ScrollbarTrackTexture?.Dispose();
			ScrollbarTrackTexture = CreateScrollbarTrackTexture(trackWidth, trackHeight);

			ScrollbarTextureWidthPixels = trackWidth;

			ScrollbarTextureHeightPixels = trackHeight;
		}

		if (ScrollbarHandleTexture == null || ScrollbarHandleTextureWidthPixels != handleWidth || ScrollbarHandleTextureHeightPixels != trackHeight)
		{
			ScrollbarHandleTexture?.Dispose();
			ScrollbarHandleTexture = CreateScrollbarHandleTexture(handleWidth, trackHeight);

			ScrollbarHandleTextureWidthPixels = handleWidth;

			ScrollbarHandleTextureHeightPixels = trackHeight;
		}
	}

	private LoadedTexture CreateScrollbarTrackTexture(int width, int height)
	{
		using ImageSurface surface = new(Format.Argb32, width, height);

		using Context ctx = new(surface);

		GuiElement.RoundRectangle(ctx, 0, 0, width, height, GuiStyle.ElementBGRadius);

		ctx.SetSourceRGBA(0, 0, 0, 0.2);

		ctx.Fill();

		EmbossRoundRectangleElement(ctx, 0, 0, width, height, inverse: true);

		LoadedTexture texture = new(api);

		generateTexture(surface, ref texture);

		return texture;
	}

	private LoadedTexture CreateScrollbarHandleTexture(int width, int height)
	{
		using ImageSurface surface = new(Format.Argb32, width, height);

		using Context ctx = new(surface);

		GuiElement.RoundRectangle(ctx, 0, 0, width, height, 1);

		ctx.SetSourceRGBA(GuiStyle.DialogHighlightColor);

		ctx.FillPreserve();

		ctx.SetSourceRGBA(0, 0, 0, 0.4);

		ctx.Fill();

		EmbossRoundRectangleElement(ctx, 0, 0, width, height, inverse: false, depth: 2, radius: 1);

		LoadedTexture texture = new(api);

		generateTexture(surface, ref texture);

		return texture;
	}

	private void UpdateScrollbarHandle()
	{
		if (MaximumScroll <= 0 || ContentWidth <= 0)
		{
			ScrollbarHandleWidth = ScrollbarWidth;
			ScrollbarHandleX = ScrollbarX;
			return;
		}

		ScrollbarHandleWidth = Math.Max(GuiElement.scaled(40), ScrollbarWidth * Bounds.InnerWidth / ContentWidth);

		ScrollbarHandleWidth = Math.Min(ScrollbarHandleWidth, ScrollbarWidth);

		double travel = ScrollbarWidth - ScrollbarHandleWidth;
		ScrollbarHandleX = ScrollbarX + travel * (ScrollOffset / MaximumScroll);
	}

	public override void OnMouseWheel(ICoreClientAPI api, MouseWheelEventArgs args)
	{
		if (MaximumScroll <= 0 || !Bounds.PointInside(api.Input.MouseX, api.Input.MouseY)) { return; }

		ScrollBy(-args.deltaPrecise * GuiElement.scaled(WheelStepGui));

		args.SetHandled();
	}

	public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
	{
		if (args.Button != EnumMouseButton.Left) { return; }

		if (MaximumScroll > 0 && args.X >= ScrollbarX && args.X <= ScrollbarX + ScrollbarWidth && args.Y >= ScrollbarY - GuiElement.scaled(5) && args.Y <= ScrollbarY + ScrollbarHeight + GuiElement.scaled(5))
		{
			UpdateScrollbarHandle();

			if (args.X >= ScrollbarHandleX && args.X <= ScrollbarHandleX + ScrollbarHandleWidth) { ScrollbarDragOffset = args.X - ScrollbarHandleX; }
			else
			{
				ScrollbarDragOffset = ScrollbarHandleWidth / 2;
				SetScrollFromHandleX(args.X - ScrollbarDragOffset);
			}

			DraggingScrollbar = true;
			args.Handled = true;
			return;
		}

		ModSelectorEntry? entry = GetEntryAt(args.X, args.Y);

		if (entry != null)
		{
			args.Handled = true;
			ModClicked(entry);
		}
	}

	public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
	{
		if (!DraggingScrollbar) { return; }

		SetScrollFromHandleX(args.X - ScrollbarDragOffset);
		args.Handled = true;
	}

	public override void OnMouseUp(ICoreClientAPI api, MouseEvent args) { DraggingScrollbar = false; }

	private ModSelectorEntry? GetEntryAt(double mouseX, double mouseY)
	{
		if (ViewportBounds == null || !ViewportBounds.PointInside((int)mouseX, (int)mouseY)) { return null; }

		double localX = mouseX - Bounds.renderX + ScrollOffset;

		double localY = mouseY - Bounds.renderY;

		if (localX < 0 || localY < 0) { return null; }

		int column = (int)Math.Floor(localX / ColumnPitch);
		double xWithinColumn = localX - column * ColumnPitch;

		if (xWithinColumn > CellWidth) { return null; }

		double rowGapGui = Math.Max(8, RowGapGui - Math.Max(0, Rows - 2) * 2);

		double rowPitch = RowHeight + GuiElement.scaled(rowGapGui);

		int row = (int)Math.Floor(localY / rowPitch);

		if (row < 0 || row >= Rows) { return null; }

		double yWithinRow = localY - row * rowPitch;

		if (yWithinRow > RowHeight) { return null; }

		int index = column * Rows + row;

		return index >= 0 && index < VisibleEntries.Count ? VisibleEntries[index] : null;
	}

	private void SetScrollFromHandleX(double handleX)
	{
		double travel = ScrollbarWidth - ScrollbarHandleWidth;

		if (travel <= 0) { ScrollOffset = 0; return; }

		double position = Math.Clamp(handleX - ScrollbarX, 0, travel);

		ScrollOffset = position / travel * MaximumScroll;

		UpdateScrollbarHandle();
	}

	private void ScrollBy(double amount)
	{
		ScrollOffset = Math.Clamp(ScrollOffset + amount, 0, MaximumScroll);

		UpdateScrollbarHandle();
	}

	private void DisposeTextures()
	{
		foreach (CardTexture texture in Textures.Values) { texture.Dispose(); }

		Textures.Clear();

		MissingIconTexture?.Dispose();
		MissingIconTexture = null;
	}

	public override void Dispose()
	{
		DisposeTextures();

		DiagnosticFillTexture?.Dispose();
		DiagnosticFillTexture = null;

		ScrollbarTrackTexture?.Dispose();
		ScrollbarTrackTexture = null;

		ScrollbarHandleTexture?.Dispose();
		ScrollbarHandleTexture = null;

		base.Dispose();
	}

	private sealed class CardTexture : IDisposable
	{
		public LoadedTexture? IconTexture { get; }
		public LoadedTexture NameTexture { get; }

		public CardTexture(LoadedTexture? iconTexture, LoadedTexture nameTexture)
		{
			IconTexture = iconTexture;
			NameTexture = nameTexture;
		}

		public void Dispose()
		{
			IconTexture?.Dispose();
			NameTexture.Dispose();
		}
	}
}

public static class GuiComposerModSelectorExtensions
{
	public static GuiComposer AddModSelectorGrid(this GuiComposer composer, IEnumerable<ModSelectorEntry> entries, Action<ModSelectorEntry> modClicked, int rows, ImmImportantInformationHighlight highlightMode, ElementBounds bounds, string? key = null)
	{
		if (!composer.Composed) { composer.AddInteractiveElement(new GuiElementModGrid(composer.Api, bounds, entries, modClicked, rows, highlightMode), key); }

		return composer;
	}

	public static GuiElementModGrid GetModSelectorGrid(this GuiComposer composer, string key) { return (GuiElementModGrid)composer.GetElement(key); }
}
