#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Cairo;
using IntegratedModManager.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace IntegratedModManager.ModSelector;

public sealed class GuiDialogArrayEditor : GuiDialog
{
	private const double PreferredWidth = 620;
	private const double PreferredHeight = 560;
	private const double ScreenMargin = 30;
	private const double Padding = 20;
	private const double HeaderHeight = 24;
	private const double HeaderGap = 10;
	private const double RowHeight = 48;
	private const double RowGap = 8;
	private const double RowPadding = 8;
	private const double IndexWidth = 38;
	private const double ScrollbarWidth = 16;
	private const double ScrollbarGap = 8;
	private const double ButtonWidth = 150;
	private const double ButtonHeight = 32;
	private const double BottomPadding = 18;
	private const double MouseWheelStep = 64;

	private readonly List<string> Rows = new();
	private readonly Dictionary<int, GuiElementImmTextInput> Inputs = new();

	private Action<string>? ValueChanged;
	private string Label = "";
	private string ElementType = "";
	private string ValidationMessage = "";

	private ElementBounds? ClipBounds;
	private GuiElementContainer? ListContainer;

	private float ScrollValue;
	private float ContentHeight;
	private bool NeedScrollbar;
	private bool Initializing;
	private bool ScrollToBottom;
	private bool RecomposeQueued;
	private int PendingFocusIndex = -1;
	private int PendingCaretPosition;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.5;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

	public GuiDialogArrayEditor(ICoreClientAPI capi) : base(capi) { }

	public bool Show(string label, string elementType, string valueJson, Action<string> valueChanged)
	{
		TryClose();

		Label = string.IsNullOrWhiteSpace(label) ? ImmLocalization.Get("array-editor-title") : label;

		ElementType = elementType ?? "";
		ValueChanged = valueChanged;
		ValidationMessage = "";
		Rows.Clear();

		try
		{
			JArray values = JArray.Parse(valueJson);

			foreach (JToken value in values) { Rows.Add(FormatValue(value)); }
		}
		catch { Rows.Clear(); }

		// One blank row is always kept at the end. Blank rows elsewhere are retained while the dialog is open and compacted out when committing.
		Rows.Add("");

		ScrollValue = 0;
		ScrollToBottom = false;
		RecomposeQueued = false;
		PendingFocusIndex = -1;
		PendingCaretPosition = 0;

		Compose();
		return TryOpen();
	}

	public override void OnMouseWheel(MouseWheelEventArgs args)
	{
		if (NeedScrollbar && ClipBounds != null && ClipBounds.PointInside(capi.Input.MouseX, capi.Input.MouseY))
		{
			SetScroll(ScrollValue - (float)(args.deltaPrecise * MouseWheelStep));

			args.SetHandled();
			return;
		}

		base.OnMouseWheel(args);
	}

	private void Compose()
	{
		Inputs.Clear();
		ListContainer = null;

		double guiScale = Math.Max(0.1, RuntimeEnv.GUIScale);

		double screenWidth = capi.Render.FrameWidth / guiScale;

		double screenHeight = capi.Render.FrameHeight / guiScale;

		double width = Math.Min(PreferredWidth, Math.Max(340, screenWidth - ScreenMargin * 2));

		double height = Math.Min(PreferredHeight, Math.Max(360, screenHeight - ScreenMargin * 2));

		ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle);

		ElementBounds titleBounds = ElementBounds.Fixed(Padding, 14, width - Padding * 2, HeaderHeight);

		CairoFont titleFont = CreateSingleLineTitleFont(Label, titleBounds);

		double contentY = 14 + HeaderHeight + HeaderGap;

		double buttonY = height - BottomPadding - ButtonHeight;

		double statusY = buttonY - 26;

		double visibleHeight = Math.Max(120, statusY - contentY - 10);

		double availableWidth = width - Padding * 2;

		double calculatedHeight = Math.Max(visibleHeight, Rows.Count * (RowHeight + RowGap) + 4);

		ContentHeight = (float)calculatedHeight;

		NeedScrollbar = ContentHeight > visibleHeight + 1;

		double contentWidth = availableWidth - (NeedScrollbar ? ScrollbarWidth + ScrollbarGap : 0);

		float maxScroll = Math.Max(0, ContentHeight - (float)visibleHeight);

		if (ScrollToBottom)
		{
			ScrollValue = maxScroll;
			ScrollToBottom = false;
		}
		else { ScrollValue = Math.Clamp(ScrollValue, 0, maxScroll); }

		ClipBounds = ElementBounds.Fixed(Padding, contentY, contentWidth, visibleHeight);

		ElementBounds listBounds = ElementBounds.Fixed(0, -ScrollValue, contentWidth, ContentHeight);

		ElementBounds panelBounds = ElementBounds.Fixed(Padding - 8, contentY - 8, availableWidth + 16, visibleHeight + 16);

		ElementBounds scrollbarBounds = ElementBounds.Fixed(Padding + contentWidth + ScrollbarGap, contentY, ScrollbarWidth, visibleHeight).WithFixedPadding(2);

		ElementBounds statusBounds = ElementBounds.Fixed(Padding, statusY, availableWidth, 20);

		ElementBounds closeBounds = ElementBounds.Fixed((width - ButtonWidth) / 2, buttonY, ButtonWidth, ButtonHeight);

		SingleComposer?.Dispose();

		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-array-editor", dialogBounds).AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false).AddStaticText(Label, titleFont, EnumTextOrientation.Center, titleBounds).AddInset(panelBounds, depth: 4, brightness: 0.85f);

		ListContainer = new GuiElementContainer(capi, listBounds) { InsideClipBounds = ClipBounds, unscaledCellSpacing = 0, Tabbable = true };

		PopulateRows(ListContainer, contentWidth);

		composer.BeginClip(ClipBounds).AddInteractiveElement(ListContainer, "arraylist").EndClip();

		if (NeedScrollbar) { composer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, "arrayscroll"); }

		composer.AddDynamicText(ValidationMessage, CairoFont.WhiteDetailText(), statusBounds, "arraystatus").AddSmallButton(ImmLocalization.Get("button-close"), OnCloseClicked, closeBounds);

		SingleComposer = composer.Compose();

		InitializeInputs();

		if (NeedScrollbar)
		{
			GuiElementScrollbar scrollbar = SingleComposer.GetScrollbar("arrayscroll");

			// SetHeights() triggers the scrollbar's change callback.
			// A newly composed scrollbar starts at zero, so preserve the intended position before initialization and restore it afterwards.
			float restoreScrollValue = ScrollValue;

			scrollbar.SetHeights((float)visibleHeight, ContentHeight);

			SetScroll(restoreScrollValue);
		}

		QueueFocusRestore();
	}

	private void PopulateRows(GuiElementContainer container, double contentWidth)
	{
		double cardWidth = Math.Max(120, contentWidth - 8);

		for (int index = 0; index < Rows.Count; index++)
		{
			double y = 4 + index * (RowHeight + RowGap);

			ElementBounds cardBounds = ElementBounds.Fixed(4, y, cardWidth, RowHeight);

			container.Add(new GuiElementConfigCard(capi, cardBounds));

			container.Add(new GuiElementStaticText(capi, (index + 1).ToString(CultureInfo.InvariantCulture), EnumTextOrientation.Center, ElementBounds.Fixed(4 + RowPadding, y + 13, IndexWidth, 22), CairoFont.WhiteDetailText()));

			ElementBounds inputBounds = ElementBounds.Fixed(4 + RowPadding + IndexWidth + 8, y + 8, cardWidth - RowPadding * 2 - IndexWidth - 8, 32);

			int rowIndex = index;

			GuiElementImmTextInput input = new(capi, inputBounds, value => OnRowChanged(rowIndex, value), CairoFont.TextInput());

			container.Add(input);
			Inputs[index] = input;
		}
	}

	private void InitializeInputs()
	{
		Initializing = true;

		try
		{
			foreach (KeyValuePair<int, GuiElementImmTextInput> pair in Inputs) { pair.Value.SetValue(Rows[pair.Key], setCaretPosToEnd: false); }
		}
		finally { Initializing = false; }
	}

	private void OnRowChanged(int index, string value)
	{
		if (Initializing || index < 0 || index >= Rows.Count) { return; }

		bool wasTrailing = index == Rows.Count - 1;

		Rows[index] = value ?? "";

		TryCommit();

		// Clearing a populated row is intentionally not a structural UI operation.
		// It remains visible and blank until the dialog is closed. This keeps every other row stationary while the user is editing.
		if (!wasTrailing || Rows[index].Length == 0) { return; }

		// Filling the one trailing blank row is the only action that changes the editor's structure while open.
		// Preserve the exact input/caret and restore it after the resized UI has been composed.
		Rows.Add("");

		PendingFocusIndex = index;
		PendingCaretPosition = Rows[index].Length;

		ScrollToBottom = true;
		QueueRecompose();
	}

	private void QueueRecompose()
	{
		if (RecomposeQueued) { return; }

		RecomposeQueued = true;

		capi.Event.EnqueueMainThreadTask(() => { RecomposeQueued = false; if (IsOpened()) { Compose(); } }, "integratedmodmanager-array-recompose");
	}

	private void QueueFocusRestore()
	{
		if (PendingFocusIndex < 0) { return; }

		int focusIndex = PendingFocusIndex;

		int caretPosition = PendingCaretPosition;

		PendingFocusIndex = -1;

		capi.Event.EnqueueMainThreadTask(() => RestoreFocus(focusIndex, caretPosition), "integratedmodmanager-array-focus");
	}

	private void RestoreFocus(int index, int caretPosition)
	{
		if (!IsOpened() || ListContainer == null || index < 0 || index >= Rows.Count || !Inputs.TryGetValue(index, out GuiElementImmTextInput? input)) { return; }

		// Focus through the container so it can clear any stale child focus left by the old composition before restoring the caret.
		ListContainer.FocusElement(input.TabIndex);

		input.SetCaretPos(Math.Min(caretPosition, Rows[index].Length));
	}

	private void TryCommit()
	{
		JArray result = new();

		for (int index = 0; index < Rows.Count; index++)
		{
			string row = Rows[index];

			// A blank entry means deletion. This also naturally ignores the permanent empty row at the bottom of the editor.
			if (row.Length == 0) { continue; }

			if (!TryParseValue(row, out JToken value, out string error)) { SetValidationMessage(ImmLocalization.Get("array-entry-error", index + 1, error)); return; }

			result.Add(value);
		}

		SetValidationMessage("");

		ValueChanged?.Invoke(result.ToString(Formatting.None));
	}

	private void SetValidationMessage(string message)
	{
		ValidationMessage = message;

		SingleComposer?.GetDynamicText("arraystatus")?.SetNewText(message, autoHeight: false);
	}

	private bool TryParseValue(string text, out JToken value, out string error)
	{
		value = JValue.CreateNull();
		error = "";

		switch (ElementType)
		{
			case "Boolean":
				if (bool.TryParse(text.Trim(), out bool booleanValue)) { value = new JValue(booleanValue); return true; }

				error = ImmLocalization.Get("array-error-boolean");
			return false;

			case "Integer":
				if (int.TryParse(text.Trim(), NumberStyles.Integer, GlobalConstants.DefaultCultureInfo, out int integerValue)) { value = new JValue(integerValue); return true; }

				error = ImmLocalization.Get("array-error-integer");
			return false;

			case "Decimal":
				if (double.TryParse(text.Trim(), NumberStyles.Float, GlobalConstants.DefaultCultureInfo, out double decimalValue) && double.IsFinite(decimalValue)) { value = new JValue(decimalValue); return true; }

				error = ImmLocalization.Get("array-error-decimal");
			return false;

			case "String":
				value = new JValue(text);
			return true;

			default:
				error = ImmLocalization.Get("array-error-unsupported-type", ElementType);
			return false;
		}
	}

	private string FormatValue(JToken value) { return ElementType switch { "Boolean" => value.Value<bool>() ? "true" : "false", "Integer" => value.Value<int>().ToString(CultureInfo.InvariantCulture), "Decimal" => value.Value<double>().ToString("G", GlobalConstants.DefaultCultureInfo), "String" => value.Value<string>() ?? "", _ => value.ToString(Formatting.None) }; }

	private void OnScrollbarChanged(float value)
	{
		ScrollValue = Math.Clamp(value, 0, Math.Max(0, ContentHeight - (float)(ClipBounds?.fixedHeight ?? 0)));

		if (ListContainer == null) { return; }

		ListContainer.Bounds.fixedY = -ScrollValue;

		ListContainer.Bounds.MarkDirtyRecursive();

		ListContainer.Bounds.CalcWorldBounds();
	}

	private void SetScroll(float value)
	{
		if (!NeedScrollbar)
		{
			ScrollValue = 0;
			OnScrollbarChanged(0);
			return;
		}

		float maxScroll = Math.Max(0, ContentHeight - (float)(ClipBounds?.fixedHeight ?? 0));

		ScrollValue = Math.Clamp(value, 0, maxScroll);

		OnScrollbarChanged(ScrollValue);

		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("arrayscroll");

		if (scrollbar != null)
		{
			scrollbar.CurrentYPosition = ScrollValue;

			scrollbar.RecomposeHandle();
		}
	}

	private static CairoFont CreateSingleLineTitleFont(string text, ElementBounds bounds)
	{
		CairoFont font = CairoFont.WhiteMediumText().WithWeight(FontWeight.Bold);

		double maxWidth = Math.Max(1, (bounds.fixedWidth - 4) * RuntimeEnv.GUIScale);

		double maxHeight = Math.Max(1, (bounds.fixedHeight - 2) * RuntimeEnv.GUIScale);

		double textWidth = Math.Max(1, font.GetTextExtents(text).Width);

		double textHeight = Math.Max(1, font.GetFontExtents().Height);

		double scale = Math.Min(1, Math.Min(maxWidth / textWidth, maxHeight / textHeight));

		font.UnscaledFontsize *= Math.Max(0.05, scale * 0.96);

		return font;
	}

	private bool OnCloseClicked() { TryClose(); return true; }

	public override void Dispose()
	{
		ValueChanged = null;
		Rows.Clear();
		Inputs.Clear();
		base.Dispose();
	}
}
