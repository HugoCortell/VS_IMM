using System.Globalization;
using System.Text.RegularExpressions;
using Cairo;
using IntegratedModManager.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace IntegratedModManager.ModSelector;

public sealed class GuiDialogAdvancedEditor(ICoreClientAPI capi) : GuiDialog(capi)
{
	private const double PreferredWidth = 620;
	private const double PreferredHeight = 720;
	private const double ScreenMargin = 30;
	private const double Padding = 18;
	private const double HeaderHeight = 28;
	private const double HeaderGap = 12;
	private const double ScrollbarWidth = 16;
	private const double ScrollbarGap = 8;
	private const double CardGap = 8;
	private const double CardPadding = 8;
	private const double LabelHeight = 18;
	private const double LabelGap = 4;
	private const double InputHeight = 30;
	private const double DescriptionGap = 4;
	private const double ButtonWidth = 140;
	private const double ButtonHeight = 32;
	private const double ButtonGap = 14;
	private const double BottomPadding = 18;
	private const double MouseWheelStep = 64;
	private const double CollectionHeaderHeight = 30;
	private const double CollectionValueGap = 5;
	private const double CollectionActionWidth = 68;
	private const double StateButtonWidth = 78;
	private const double SmallGap = 6;

	private readonly List<NavigationFrame> Navigation = [];
	private readonly List<Action> Initializers = [];
	private readonly Dictionary<JProperty, string> PendingDictionaryKeys = [];

	private ImmAdvancedSchema? RootSchema;
	private ImmConfigSource ConfigSource;
	private JToken? WorkingRoot;
	private JToken? OriginalRoot;
	private Action<string>? ValueChanged;

	private string RootLabel = "";
	private string ValidationMessage = "";

	private ElementBounds? ClipBounds;
	private GuiElementContainer? ContentContainer;

	private float ScrollValue;
	private float ContentHeight;
	private bool NeedScrollbar;
	private bool Initializing;
	private bool RecomposeQueued;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.55;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

    public bool Show(string label, string schemaJson, string valueJson, ImmConfigSource configSource, Action<string> valueChanged)
	{
		TryClose();

		ImmAdvancedSchema? schema;
		JToken value;

		try
		{
			schema = JsonConvert.DeserializeObject<ImmAdvancedSchema>(schemaJson);
			value = JToken.Parse(valueJson);
		}
		catch
		{
			return false;
		}

		if (schema == null || !ImmAdvancedValueValidator.TryValidateCurrentValue(schema, configSource, value, out _)) { return false; }

		RootLabel = string.IsNullOrWhiteSpace(label) ? ImmLocalization.Get("advanced-editor-title") : label;
		RootSchema = schema;
		ConfigSource = configSource;
		WorkingRoot = value.DeepClone();
		OriginalRoot = value.DeepClone();
		ValueChanged = valueChanged;
		ValidationMessage = "";

		Navigation.Clear();
		PendingDictionaryKeys.Clear();

		Navigation.Add(new NavigationFrame(
			schema,
			RootLabel,
			() => WorkingRoot!,
			token => WorkingRoot = token
		));

		ScrollValue = 0;
		RecomposeQueued = false;

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
		if (RootSchema == null || WorkingRoot == null || Navigation.Count == 0) { return; }

		Initializers.Clear();
		ContentContainer = null;

		NavigationFrame frame = Navigation[^1];
		JToken current = frame.GetValue();

		double guiScale = Math.Max(0.1, RuntimeEnv.GUIScale);
		double screenWidth = capi.Render.FrameWidth / guiScale;
		double screenHeight = capi.Render.FrameHeight / guiScale;

		double width = Math.Min(PreferredWidth, Math.Max(360, screenWidth - ScreenMargin * 2));
		double height = Math.Min(PreferredHeight, Math.Max(400, screenHeight - ScreenMargin * 2));

		ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, width, height).WithAlignment(EnumDialogArea.CenterMiddle);
		ElementBounds titleBounds = ElementBounds.Fixed(Padding, 14, width - Padding * 2, HeaderHeight);

		string title = BuildTitle();
		CairoFont titleFont = CreateSingleLineTitleFont(title, titleBounds);

		double contentY = 14 + HeaderHeight + HeaderGap;
		double buttonY = height - BottomPadding - ButtonHeight;
		double statusY = buttonY - 26;
		double visibleHeight = Math.Max(140, statusY - contentY - 10);
		double availableWidth = width - Padding * 2;

		double contentWidthForMeasure = availableWidth;
		double calculatedHeight = MeasureContent(frame.Schema, current, contentWidthForMeasure);
		NeedScrollbar = calculatedHeight > visibleHeight + 1;

		double contentWidth = availableWidth - (NeedScrollbar ? ScrollbarWidth + ScrollbarGap : 0);

		if (NeedScrollbar) { calculatedHeight = MeasureContent(frame.Schema, current, contentWidth); }

		ContentHeight = (float)Math.Max(visibleHeight, calculatedHeight);
		ScrollValue = Math.Clamp(ScrollValue, 0, Math.Max(0, ContentHeight - (float)visibleHeight));

		ClipBounds = ElementBounds.Fixed(Padding, contentY, contentWidth, visibleHeight);
		ElementBounds contentBounds = ElementBounds.Fixed(0, -ScrollValue, contentWidth, ContentHeight);
		ElementBounds panelBounds = ElementBounds.Fixed(Padding - 8, contentY - 8, availableWidth + 16, visibleHeight + 16);
		ElementBounds scrollbarBounds = ElementBounds.Fixed(Padding + contentWidth + ScrollbarGap, contentY, ScrollbarWidth, visibleHeight).WithFixedPadding(2);
		ElementBounds statusBounds = ElementBounds.Fixed(Padding, statusY, availableWidth, 20);

		bool canBack = Navigation.Count > 1;
		bool canSetNull = frame.Schema.Nullable && current.Type != JTokenType.Null && IsContainer(frame.Schema.Type);
		int footerButtonCount = 1 + (canBack ? 1 : 0) + (canSetNull ? 1 : 0);
		double footerButtonWidth = Math.Min(ButtonWidth, (availableWidth - Math.Max(0, footerButtonCount - 1) * ButtonGap) / footerButtonCount);
		double footerWidth = footerButtonCount * footerButtonWidth + Math.Max(0, footerButtonCount - 1) * ButtonGap;
		double footerX = (width - footerWidth) / 2;
		double nextFooterX = footerX;
		ElementBounds nullBounds = ElementBounds.Fixed(nextFooterX, buttonY, footerButtonWidth, ButtonHeight);
		if (canSetNull) { nextFooterX += footerButtonWidth + ButtonGap; }
		ElementBounds backBounds = ElementBounds.Fixed(nextFooterX, buttonY, footerButtonWidth, ButtonHeight);
		if (canBack) { nextFooterX += footerButtonWidth + ButtonGap; }
		ElementBounds closeBounds = ElementBounds.Fixed(nextFooterX, buttonY, footerButtonWidth, ButtonHeight);

		SingleComposer?.Dispose();

		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-advanced-editor", dialogBounds)
			.AddShadedDialogBG(ElementBounds.Fill, withTitleBar: false)
			.AddStaticText(title, titleFont, EnumTextOrientation.Center, titleBounds)
			.AddInset(panelBounds, depth: 4, brightness: 0.85f);

		ContentContainer = new GuiElementContainer(capi, contentBounds) { InsideClipBounds = ClipBounds, unscaledCellSpacing = 0, Tabbable = true };
		PopulateContent(ContentContainer, frame, current, contentWidth);

		composer.BeginClip(ClipBounds).AddInteractiveElement(ContentContainer, "advancedcontent").EndClip();

		if (NeedScrollbar) { composer.AddVerticalScrollbar(OnScrollbarChanged, scrollbarBounds, "advancedscroll"); }

		composer.AddDynamicText(ValidationMessage, CairoFont.WhiteDetailText(), statusBounds, "advancedstatus");

		if (canSetNull) { composer.AddSmallButton(ImmLocalization.Get("advanced-set-null"), OnSetCurrentNullClicked, nullBounds); }
		if (canBack) { composer.AddSmallButton(ImmLocalization.Get("button-back"), OnBackClicked, backBounds); }
		composer.AddSmallButton(ImmLocalization.Get("button-close"), OnCloseClicked, closeBounds);

		SingleComposer = composer.Compose();

		Initializing = true;
		try { foreach (Action initialize in Initializers) { initialize(); } }
		finally { Initializing = false; }

		if (NeedScrollbar)
		{
			GuiElementScrollbar scrollbar = SingleComposer.GetScrollbar("advancedscroll");
			float restoreScroll = ScrollValue;
			scrollbar.SetHeights((float)visibleHeight, ContentHeight);
			SetScroll(restoreScroll);
		}
	}

	private string BuildTitle()
	{
		if (Navigation.Count <= 1) { return RootLabel; }

		string current = Navigation[^1].Label;
		return string.IsNullOrWhiteSpace(current) ? RootLabel : $"{RootLabel} > {current}";
	}

	private double MeasureContent(ImmAdvancedSchema schema, JToken current, double contentWidth)
	{
		if (current.Type == JTokenType.Null) { return 76; }

		return schema.Type switch
		{
			"Object" => MeasureObject(schema, contentWidth),
			"Dictionary" => MeasureDictionary(schema, current as JObject, contentWidth),
			"Array" => MeasureArray(schema, current as JArray, contentWidth),
			"Tuple" => MeasureTuple(schema, current as JArray, contentWidth),
			_ => MeasureRootPrimitive(schema, contentWidth)
		};
	}

	private double MeasureObject(ImmAdvancedSchema schema, double contentWidth)
	{
		List<EditorRow> rows = BuildObjectRows(schema, contentWidth);
		return rows.Count == 0 ? 48 : rows.Max(row => row.Y + row.Height) + 4;
	}

	private double MeasureDictionary(ImmAdvancedSchema schema, JObject? dictionary, double contentWidth)
	{
		int count = dictionary?.Count ?? 0;
		double y = 4 + count * (92 + CardGap);
		return y + (CanAdd(schema, count) ? 52 : 0) + 4;
	}

	private double MeasureArray(ImmAdvancedSchema schema, JArray? array, double contentWidth)
	{
		int count = array?.Count ?? 0;
		double y = 4 + count * (88 + CardGap);
		return y + (CanAdd(schema, count) ? 42 + CardGap : 0) + 4;
	}

    private double MeasureTuple(ImmAdvancedSchema schema, JArray? tuple, double contentWidth) => Math.Max(48, 4 + schema.Items.Count * (76 + CardGap));

    private double MeasureRootPrimitive(ImmAdvancedSchema schema, double contentWidth)
	{
		double descriptionHeight = MeasureTextHeight(ImmLocalization.Resolve(schema.Description), CairoFont.WhiteDetailText(), Math.Max(80, contentWidth - 24));
		return 4 + 76 + (descriptionHeight > 0 ? descriptionHeight + DescriptionGap : 0) + 4;
	}

	private void PopulateContent(GuiElementContainer container, NavigationFrame frame, JToken current, double contentWidth)
	{
		if (current.Type == JTokenType.Null)
		{
			PopulateNullRoot(container, frame, contentWidth);
			return;
		}

		switch (frame.Schema.Type)
		{
			case "Object": PopulateObject(container, frame.Schema, (JObject)current, contentWidth); break;
			case "Dictionary": PopulateDictionary(container, frame.Schema, (JObject)current, contentWidth); break;
			case "Array": PopulateArray(container, frame.Schema, (JArray)current, contentWidth); break;
			case "Tuple": PopulateTuple(container, frame.Schema, (JArray)current, contentWidth); break;
			default: PopulateRootPrimitive(container, frame, current, contentWidth); break;
		}
	}

	private void PopulateNullRoot(GuiElementContainer container, NavigationFrame frame, double contentWidth)
	{
		double width = Math.Max(120, contentWidth - 8);
		ElementBounds cardBounds = ElementBounds.Fixed(4, 4, width, 64);
		container.Add(new GuiElementConfigCard(capi, cardBounds));

		AddText(container, ImmLocalization.Get("advanced-state-null"), CairoFont.WhiteDetailText(), ElementBounds.Fixed(12, 12, width - 116, 30));

		if (!frame.Schema.Nullable) { return; }

		AddButton(container, ImmLocalization.Get("advanced-set-value"), ElementBounds.Fixed(width - 92, 13, 84, 30), () =>
		{
			if (!ImmAdvancedValueValidator.TryCreateValue(frame.Schema, ConfigSource, forceNonNull: true, out JToken value, out string error)) { SetValidationMessage(error); return true; }

			frame.SetValue(value);
			EmitWorkingValue();
			QueueRecompose(resetScroll: true);
			return true;
		});
	}

	private void PopulateObject(GuiElementContainer container, ImmAdvancedSchema schema, JObject value, double contentWidth)
	{
		foreach (EditorRow row in BuildObjectRows(schema, contentWidth))
		{
			ImmAdvancedSchema field = row.Schema;
			JProperty? property = value.Property(field.Key, StringComparison.Ordinal);

			ElementBounds cardBounds = ElementBounds.Fixed(row.X, row.Y, row.Width, row.Height);
			container.Add(new GuiElementConfigCard(capi, cardBounds));

			string label = ResolveLabel(field, field.Key);
			AddText(container, label, CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(row.X + CardPadding, row.Y + CardPadding, row.Width - CardPadding * 2, LabelHeight));

			double bodyY = row.Y + CardPadding + LabelHeight + LabelGap;
			double bodyWidth = row.Width - CardPadding * 2;

			if (field.Optional || field.Nullable)
			{
				string state = GetStateLabel(property != null, property?.Value);
				AddButton(container, state, ElementBounds.Fixed(row.X + CardPadding, bodyY, StateButtonWidth, InputHeight), () => OnCycleObjectFieldState(value, field));

				if (property != null && property.Value.Type != JTokenType.Null)
				{
					AddNodeEditor(container, field, property.Value, ElementBounds.Fixed(row.X + CardPadding + StateButtonWidth + SmallGap, bodyY, Math.Max(40, bodyWidth - StateButtonWidth - SmallGap), InputHeight),
						token => value[field.Key] = token,
						() => NavigateTo(field, label, () => value[field.Key]!, token => value[field.Key] = token));
				}
			}
			else if (property != null)
			{
				AddNodeEditor(container, field, property.Value, ElementBounds.Fixed(row.X + CardPadding, bodyY, bodyWidth, InputHeight),
					token => value[field.Key] = token,
					() => NavigateTo(field, label, () => value[field.Key]!, token => value[field.Key] = token));
			}

			string description = ImmLocalization.Resolve(field.Description);
			if (!string.IsNullOrWhiteSpace(description))
			{
				double descriptionY = bodyY + InputHeight + DescriptionGap;
				AddRichText(container, description, CairoFont.WhiteDetailText(), ElementBounds.Fixed(row.X + CardPadding, descriptionY, bodyWidth, Math.Max(18, row.Height - (descriptionY - row.Y) - CardPadding)));
			}
		}
	}

	private List<EditorRow> BuildObjectRows(ImmAdvancedSchema schema, double contentWidth)
	{
		List<EditorRow> result = [];
		double outerWidth = Math.Max(120, contentWidth - 8);
		double columnGap = 8;
		const double minimumCellWidth = 116;
		int requestedColumns = Math.Clamp(schema.Columns, 1, 4);
		int fittingColumns = Math.Max(1, (int)Math.Floor((outerWidth + columnGap) / (minimumCellWidth + columnGap)));
		int columns = Math.Min(requestedColumns, fittingColumns);
		double cellWidth = (outerWidth - columnGap * (columns - 1)) / columns;
		double rowY = 4;
		int column = 0;
		List<EditorRow> pendingRow = [];

		void FlushRow()
		{
			if (pendingRow.Count == 0) { return; }

			double rowHeight = pendingRow.Max(item => item.Height);

			foreach (EditorRow item in pendingRow)
			{
				item.Y = rowY;
				item.Height = rowHeight;
				result.Add(item);
			}

			rowY += rowHeight + CardGap;
			pendingRow.Clear();
			column = 0;
		}

		foreach (ImmAdvancedSchema field in schema.Fields)
		{
			int span = Math.Clamp(field.ColumnSpan, 1, columns);

			if (column + span > columns) { FlushRow(); }

			double x = 4 + column * (cellWidth + columnGap);
			double width = cellWidth * span + columnGap * (span - 1);
			double descriptionHeight = MeasureTextHeight(ImmLocalization.Resolve(field.Description), CairoFont.WhiteDetailText(), Math.Max(60, width - CardPadding * 2));
			double height = CardPadding + LabelHeight + LabelGap + InputHeight + (descriptionHeight > 0 ? DescriptionGap + descriptionHeight : 0) + CardPadding;

			pendingRow.Add(new EditorRow(field, x, rowY, width, Math.Max(68, height)));
			column += span;

			if (column >= columns) { FlushRow(); }
		}

		FlushRow();
		return result;
	}

	private void PopulateDictionary(GuiElementContainer container, ImmAdvancedSchema schema, JObject dictionary, double contentWidth)
	{
		if (schema.Value == null) { return; }

		double width = Math.Max(120, contentWidth - 8);
		double y = 4;

		foreach (JProperty property in dictionary.Properties().ToArray())
		{
			ElementBounds cardBounds = ElementBounds.Fixed(4, y, width, 92);
			container.Add(new GuiElementConfigCard(capi, cardBounds));

			double innerX = 4 + CardPadding;
			double innerWidth = width - CardPadding * 2;
			double removeWidth = CollectionActionWidth;
			double setKeyWidth = CollectionActionWidth;
			double keyWidth = Math.Max(80, innerWidth - removeWidth - setKeyWidth - SmallGap * 2);

			GuiElementImmTextInput keyInput = new(capi, ElementBounds.Fixed(innerX, y + CardPadding, keyWidth, InputHeight), text => PendingDictionaryKeys[property] = text ?? "", CairoFont.TextInput());
			container.Add(keyInput);

			string keyText = PendingDictionaryKeys.TryGetValue(property, out string? pendingKey) ? pendingKey : property.Name;
			Initializers.Add(() => keyInput.SetValue(keyText, setCaretPosToEnd: false));

			AddButton(container, ImmLocalization.Get("advanced-set-key"), ElementBounds.Fixed(innerX + keyWidth + SmallGap, y + CardPadding, setKeyWidth, InputHeight), () => OnRenameDictionaryKey(dictionary, schema, property));
			AddButton(container, ImmLocalization.Get("advanced-remove"), ElementBounds.Fixed(innerX + keyWidth + SmallGap + setKeyWidth + SmallGap, y + CardPadding, removeWidth, InputHeight), () => OnRemoveDictionaryEntry(dictionary, schema, property));

			double valueY = y + CardPadding + CollectionHeaderHeight + CollectionValueGap;
			JToken itemValue = property.Value;

			if (schema.Value.Nullable)
			{
				AddButton(container, itemValue.Type == JTokenType.Null ? ImmLocalization.Get("advanced-state-null") : ImmLocalization.Get("advanced-state-value"), ElementBounds.Fixed(innerX, valueY, StateButtonWidth, InputHeight), () => OnToggleNullableValue(schema.Value, itemValue, token => property.Value = token));

				if (itemValue.Type != JTokenType.Null)
				{
					AddNodeEditor(container, schema.Value, itemValue, ElementBounds.Fixed(innerX + StateButtonWidth + SmallGap, valueY, Math.Max(40, innerWidth - StateButtonWidth - SmallGap), InputHeight),
						token => property.Value = token,
						() => NavigateTo(schema.Value, property.Name, () => property.Value, token => property.Value = token));
				}
			}
			else
			{
				AddNodeEditor(container, schema.Value, itemValue, ElementBounds.Fixed(innerX, valueY, innerWidth, InputHeight),
					token => property.Value = token,
					() => NavigateTo(schema.Value, property.Name, () => property.Value, token => property.Value = token));
			}

			y += 92 + CardGap;
		}

		if (CanAdd(schema, dictionary.Count))
		{
			double addY = y;
			double addWidth = 88;
			double keyInputWidth = Math.Max(80, width - CardPadding * 2 - addWidth - SmallGap);

			NavigationFrame frame = Navigation[^1];
			GuiElementImmTextInput newKeyInput = new(capi, ElementBounds.Fixed(4 + CardPadding, addY + 4, keyInputWidth, InputHeight), text => frame.PendingNewKey = text ?? "", CairoFont.TextInput());
			container.Add(newKeyInput);
			Initializers.Add(() =>
			{
				newKeyInput.SetValue(frame.PendingNewKey, setCaretPosToEnd: false);
				string keyLabel = ImmLocalization.Resolve(schema.KeyLabel);
				if (!string.IsNullOrWhiteSpace(keyLabel)) { newKeyInput.SetPlaceHolderText(keyLabel); }
			});

			AddButton(container, ImmLocalization.Get("advanced-add"), ElementBounds.Fixed(4 + CardPadding + keyInputWidth + SmallGap, addY + 4, addWidth, InputHeight), () => OnAddDictionaryEntry(dictionary, schema));
		}
	}

	private void PopulateArray(GuiElementContainer container, ImmAdvancedSchema schema, JArray array, double contentWidth)
	{
		if (schema.Element == null) { return; }

		double width = Math.Max(120, contentWidth - 8);
		double y = 4;

		for (int index = 0; index < array.Count; index++)
		{
			int itemIndex = index;
			ElementBounds cardBounds = ElementBounds.Fixed(4, y, width, 88);
			container.Add(new GuiElementConfigCard(capi, cardBounds));

			double innerX = 4 + CardPadding;
			double innerWidth = width - CardPadding * 2;
			double actionWidth = CollectionActionWidth;

			AddText(container, (index + 1).ToString(CultureInfo.InvariantCulture), CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(innerX, y + CardPadding + 5, 40, LabelHeight));

			double actionsX = innerX + innerWidth - actionWidth * 3 - SmallGap * 2;

			AddButton(container, "↑", ElementBounds.Fixed(actionsX, y + CardPadding, actionWidth, InputHeight), () => OnMoveArrayItem(array, itemIndex, -1));
			AddButton(container, "↓", ElementBounds.Fixed(actionsX + actionWidth + SmallGap, y + CardPadding, actionWidth, InputHeight), () => OnMoveArrayItem(array, itemIndex, 1));
			AddButton(container, ImmLocalization.Get("advanced-remove"), ElementBounds.Fixed(actionsX + (actionWidth + SmallGap) * 2, y + CardPadding, actionWidth, InputHeight), () => OnRemoveArrayItem(array, schema, itemIndex));

			double valueY = y + CardPadding + CollectionHeaderHeight + CollectionValueGap;
			JToken itemValue = array[index];

			if (schema.Element.Nullable)
			{
				AddButton(container, itemValue.Type == JTokenType.Null ? ImmLocalization.Get("advanced-state-null") : ImmLocalization.Get("advanced-state-value"), ElementBounds.Fixed(innerX, valueY, StateButtonWidth, InputHeight), () => OnToggleNullableValue(schema.Element, array[itemIndex], token => array[itemIndex] = token));

				if (itemValue.Type != JTokenType.Null)
				{
					AddNodeEditor(container, schema.Element, itemValue, ElementBounds.Fixed(innerX + StateButtonWidth + SmallGap, valueY, Math.Max(40, innerWidth - StateButtonWidth - SmallGap), InputHeight),
						token => array[itemIndex] = token,
						() => NavigateTo(schema.Element, $"#{itemIndex + 1}", () => array[itemIndex], token => array[itemIndex] = token));
				}
			}
			else
			{
				AddNodeEditor(container, schema.Element, itemValue, ElementBounds.Fixed(innerX, valueY, innerWidth, InputHeight),
					token => array[itemIndex] = token,
					() => NavigateTo(schema.Element, $"#{itemIndex + 1}", () => array[itemIndex], token => array[itemIndex] = token));
			}

			y += 88 + CardGap;
		}

		if (CanAdd(schema, array.Count))
		{
			AddButton(container, ImmLocalization.Get("advanced-add-item"), ElementBounds.Fixed(4 + CardPadding, y + 4, Math.Max(100, width - CardPadding * 2), InputHeight), () => OnAddArrayItem(array, schema));
		}
	}

	private void PopulateTuple(GuiElementContainer container, ImmAdvancedSchema schema, JArray tuple, double contentWidth)
	{
		double width = Math.Max(120, contentWidth - 8);
		double y = 4;

		for (int index = 0; index < schema.Items.Count && index < tuple.Count; index++)
		{
			int itemIndex = index;
			ImmAdvancedSchema itemSchema = schema.Items[index];
			JToken itemValue = tuple[index];

			ElementBounds cardBounds = ElementBounds.Fixed(4, y, width, 76);
			container.Add(new GuiElementConfigCard(capi, cardBounds));

			double innerX = 4 + CardPadding;
			double innerWidth = width - CardPadding * 2;
			string label = ResolveLabel(itemSchema, $"#{index + 1}");

			AddText(container, label, CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold), ElementBounds.Fixed(innerX, y + CardPadding, innerWidth, LabelHeight));

			double bodyY = y + CardPadding + LabelHeight + LabelGap;

			if (itemSchema.Nullable)
			{
				AddButton(container, itemValue.Type == JTokenType.Null ? ImmLocalization.Get("advanced-state-null") : ImmLocalization.Get("advanced-state-value"), ElementBounds.Fixed(innerX, bodyY, StateButtonWidth, InputHeight), () => OnToggleNullableValue(itemSchema, tuple[itemIndex], token => tuple[itemIndex] = token));

				if (itemValue.Type != JTokenType.Null)
				{
					AddNodeEditor(container, itemSchema, itemValue, ElementBounds.Fixed(innerX + StateButtonWidth + SmallGap, bodyY, Math.Max(40, innerWidth - StateButtonWidth - SmallGap), InputHeight),
						token => tuple[itemIndex] = token,
						() => NavigateTo(itemSchema, label, () => tuple[itemIndex], token => tuple[itemIndex] = token));
				}
			}
			else
			{
				AddNodeEditor(container, itemSchema, itemValue, ElementBounds.Fixed(innerX, bodyY, innerWidth, InputHeight),
					token => tuple[itemIndex] = token,
					() => NavigateTo(itemSchema, label, () => tuple[itemIndex], token => tuple[itemIndex] = token));
			}

			y += 76 + CardGap;
		}
	}

	private void PopulateRootPrimitive(GuiElementContainer container, NavigationFrame frame, JToken current, double contentWidth)
	{
		double width = Math.Max(120, contentWidth - 8);
		double descriptionHeight = MeasureTextHeight(ImmLocalization.Resolve(frame.Schema.Description), CairoFont.WhiteDetailText(), Math.Max(80, width - CardPadding * 2));
		double height = 68 + (descriptionHeight > 0 ? DescriptionGap + descriptionHeight : 0);

		container.Add(new GuiElementConfigCard(capi, ElementBounds.Fixed(4, 4, width, height)));

		double bodyX = 4 + CardPadding;
		double bodyY = 4 + CardPadding;
		double bodyWidth = width - CardPadding * 2;

		if (frame.Schema.Nullable)
		{
			AddButton(container, current.Type == JTokenType.Null ? ImmLocalization.Get("advanced-state-null") : ImmLocalization.Get("advanced-state-value"), ElementBounds.Fixed(bodyX, bodyY, StateButtonWidth, InputHeight), () => OnToggleNullableValue(frame.Schema, current, frame.SetValue));

			if (current.Type != JTokenType.Null)
			{
				AddPrimitiveEditor(container, frame.Schema, current, ElementBounds.Fixed(bodyX + StateButtonWidth + SmallGap, bodyY, Math.Max(40, bodyWidth - StateButtonWidth - SmallGap), InputHeight), frame.SetValue);
			}
		}
		else
		{
			AddPrimitiveEditor(container, frame.Schema, current, ElementBounds.Fixed(bodyX, bodyY, bodyWidth, InputHeight), frame.SetValue);
		}

		string description = ImmLocalization.Resolve(frame.Schema.Description);
		if (!string.IsNullOrWhiteSpace(description))
		{
			AddRichText(container, description, CairoFont.WhiteDetailText(), ElementBounds.Fixed(bodyX, bodyY + InputHeight + DescriptionGap, bodyWidth, descriptionHeight));
		}
	}

	private void AddNodeEditor(GuiElementContainer container, ImmAdvancedSchema schema, JToken current, ElementBounds bounds, Action<JToken> setter, ActionConsumable editContainer)
	{
		if (IsContainer(schema.Type))
		{
			AddButton(container, ImmLocalization.Get("button-edit"), bounds, editContainer);
			return;
		}

		AddPrimitiveEditor(container, schema, current, bounds, setter);
	}

	private void AddPrimitiveEditor(GuiElementContainer container, ImmAdvancedSchema schema, JToken current, ElementBounds bounds, Action<JToken> setter)
	{
		switch (schema.Type)
		{
			case "Boolean":
			{
				GuiElementImmSwitch toggle = new(capi, value =>
				{
					if (!Initializing) { CommitNodeChange(schema, current, new JValue(value), setter); }
				}, ElementBounds.Fixed(bounds.fixedX, bounds.fixedY, bounds.fixedWidth, bounds.fixedHeight), size: 24, padding: 3);

				container.Add(toggle);
				Initializers.Add(() => toggle.SetValue(current.Value<bool>()));
				break;
			}

			case "String":
			{
				GuiElementImmTextInput input = new(capi, bounds, text =>
				{
					if (!Initializing) { CommitNodeChange(schema, current, new JValue(text ?? ""), setter); }
				}, CairoFont.TextInput());

				container.Add(input);
				Initializers.Add(() => input.SetValue(current.Value<string>() ?? "", setCaretPosToEnd: false));
				break;
			}

			case "Integer":
			case "Decimal":
			{
				GuiElementImmNumberInput input = new(capi, bounds, text =>
				{
					if (Initializing) { return; }

					if (schema.Type == "Integer")
					{
						if (int.TryParse(text, NumberStyles.Integer, GlobalConstants.DefaultCultureInfo, out int integerValue))
						{
							CommitNodeChange(schema, current, new JValue(integerValue), setter);
						}
						else { SetValidationMessage(ImmLocalization.Get("advanced-error-integer")); }

						return;
					}

					if (double.TryParse(text, NumberStyles.Float, GlobalConstants.DefaultCultureInfo, out double decimalValue) && double.IsFinite(decimalValue))
					{
						CommitNodeChange(schema, current, new JValue(decimalValue), setter);
					}
					else { SetValidationMessage(ImmLocalization.Get("advanced-error-decimal")); }
				}, CairoFont.TextInput());

				container.Add(input);

				Initializers.Add(() =>
				{
					input.IntMode = schema.Type == "Integer";
					input.Interval = schema.Type == "Integer" ? 1 : 0.1f;
					input.SetValue(schema.Type == "Integer" ? current.Value<int>().ToString(CultureInfo.InvariantCulture) : current.Value<double>().ToString("G", GlobalConstants.DefaultCultureInfo));
				});
				break;
			}

			case "Slider":
			{
				ImmConfigControlPacket control = ImmAdvancedValueValidator.CreateLeafControl(schema, ConfigSource);

				if (!ImmConfigValueValidator.TryGetSliderDefinition(control, current, out double min, out _, out double step, out bool integerTarget, out string error))
				{
					AddText(container, error, CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor), bounds);
					break;
				}

				int tickCount = (int)Math.Round(((schema.Max ?? min) - min) / step);
				int currentTick = (int)Math.Round((current.Value<double>() - min) / step);

				GuiElementImmSlider slider = new(capi, tick =>
				{
					if (Initializing) { return true; }

					double value = min + tick * step;
					JToken candidate = integerTarget ? new JValue((long)Math.Round(value)) : new JValue(value);
					CommitNodeChange(schema, current, candidate, setter);
					return true;
				}, ElementBounds.Fixed(bounds.fixedX, bounds.fixedY + 6, bounds.fixedWidth, 20));

				container.Add(slider);

				Initializers.Add(() =>
				{
					slider.ShowTextWhenResting = false;
					slider.OnSliderTooltip = tick => FormatSliderValue(min + tick * step, integerTarget);
					slider.OnSliderRestingText = null;
					slider.SetValues(currentTick, 0, tickCount, 1);
				});

				break;
			}

			case "Dropdown":
			{
				ImmConfigOption[] options = schema.Options?.Where(option => option != null).ToArray() ?? Array.Empty<ImmConfigOption>();
				int selectedIndex = GetDropdownIndex(options, current);

				GuiElementImmDropDown dropdown = new(
					capi,
					Enumerable.Range(0, options.Length).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray(),
					options.Select(option => ImmLocalization.Resolve(option.Label)).ToArray(),
					selectedIndex,
					(value, selected) =>
					{
						if (Initializing || !selected || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= options.Length || options[index].Value == null) { return; }

						CommitNodeChange(schema, current, options[index].Value!.DeepClone(), setter);
					},
					bounds,
					CairoFont.WhiteSmallText(),
					multiSelect: false
				);

				container.Add(dropdown);
				break;
			}
		}
	}

	private void CommitNodeChange(ImmAdvancedSchema schema, JToken current, JToken candidate, Action<JToken> setter)
	{
		if (!ImmAdvancedValueValidator.TryNormalizeNode(schema, ConfigSource, current, candidate, out JToken normalized, out string error))
		{
			SetValidationMessage(error);
			return;
		}

		setter(normalized);
		SetValidationMessage("");
		EmitWorkingValue();
	}

	private bool OnCycleObjectFieldState(JObject parent, ImmAdvancedSchema field)
	{
		JProperty? property = parent.Property(field.Key, StringComparison.Ordinal);
		bool present = property != null;
		bool isNull = property?.Value.Type == JTokenType.Null;

		if (field.Optional && field.Nullable)
		{
			if (!present) { parent[field.Key] = JValue.CreateNull(); }
			else if (isNull)
			{
				if (!TryCreateConcrete(field, out JToken value)) { return true; }
				parent[field.Key] = value;
			}
			else { property!.Remove(); }
		}
		else if (field.Optional)
		{
			if (!present)
			{
				if (!TryCreateConcrete(field, out JToken value)) { return true; }
				parent[field.Key] = value;
			}
			else { property!.Remove(); }
		}
		else if (field.Nullable)
		{
			if (isNull)
			{
				if (!TryCreateConcrete(field, out JToken value)) { return true; }
				parent[field.Key] = value;
			}
			else { parent[field.Key] = JValue.CreateNull(); }
		}

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose();
		return true;
	}

	private bool OnToggleNullableValue(ImmAdvancedSchema schema, JToken current, Action<JToken> setter)
	{
		if (!schema.Nullable) { return true; }

		if (current.Type == JTokenType.Null)
		{
			if (!TryCreateConcrete(schema, out JToken value)) { return true; }
			setter(value);
		}
		else { setter(JValue.CreateNull()); }

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose();
		return true;
	}

	private bool TryCreateConcrete(ImmAdvancedSchema schema, out JToken value)
	{
		if (ImmAdvancedValueValidator.TryCreateValue(schema, ConfigSource, forceNonNull: true, out value, out string error)) { return true; }

		SetValidationMessage(error);
		return false;
	}

	private bool OnRenameDictionaryKey(JObject dictionary, ImmAdvancedSchema schema, JProperty property)
	{
		if (!PendingDictionaryKeys.TryGetValue(property, out string? requested)) { requested = property.Name; }
		requested ??= "";

		if (!ValidateDictionaryKey(dictionary, schema, requested, property.Name, out string error)) { SetValidationMessage(error); return true; }
		if (string.Equals(requested, property.Name, StringComparison.Ordinal)) { SetValidationMessage(""); return true; }

		JToken value = property.Value.DeepClone();
		JProperty replacement = new(requested, value);
		property.AddBeforeSelf(replacement);
		property.Remove();
		PendingDictionaryKeys.Remove(property);

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose();
		return true;
	}

	private bool OnRemoveDictionaryEntry(JObject dictionary, ImmAdvancedSchema schema, JProperty property)
	{
		if (dictionary.Count <= schema.MinItems) { SetValidationMessage(ImmLocalization.Get("advanced-error-min-items", schema.MinItems)); return true; }

		PendingDictionaryKeys.Remove(property);
		property.Remove();

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose();
		return true;
	}

	private bool OnAddDictionaryEntry(JObject dictionary, ImmAdvancedSchema schema)
	{
		if (schema.Value == null) { return true; }
		if (!CanAdd(schema, dictionary.Count)) { SetValidationMessage(ImmLocalization.Get("advanced-error-max-items", schema.MaxItems ?? dictionary.Count)); return true; }

		NavigationFrame frame = Navigation[^1];
		string key = frame.PendingNewKey;

		if (!ValidateDictionaryKey(dictionary, schema, key, existingKey: null, out string error)) { SetValidationMessage(error); return true; }
		if (!ImmAdvancedValueValidator.TryCreateValue(schema.Value, ConfigSource, forceNonNull: false, out JToken value, out error)) { SetValidationMessage(error); return true; }

		dictionary.Add(key, value);
		frame.PendingNewKey = "";

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose(scrollToBottom: true);
		return true;
	}

	private bool ValidateDictionaryKey(JObject dictionary, ImmAdvancedSchema schema, string key, string? existingKey, out string error)
	{
		error = "";

		if (string.IsNullOrWhiteSpace(key)) { error = ImmLocalization.Get("advanced-error-key-empty"); return false; }

		JProperty? collision = dictionary.Property(key, StringComparison.Ordinal);
		if (collision != null && !string.Equals(key, existingKey, StringComparison.Ordinal)) { error = ImmLocalization.Get("advanced-error-key-duplicate", key); return false; }

		if (!string.IsNullOrWhiteSpace(schema.KeyPattern))
		{
			try
			{
				if (!Regex.IsMatch(key, schema.KeyPattern, RegexOptions.CultureInvariant)) { error = ImmLocalization.Get("advanced-error-key-pattern"); return false; }
			}
			catch (Exception exception) { error = exception.Message; return false; }
		}

		return true;
	}

	private bool OnAddArrayItem(JArray array, ImmAdvancedSchema schema)
	{
		if (schema.Element == null) { return true; }
		if (!CanAdd(schema, array.Count)) { SetValidationMessage(ImmLocalization.Get("advanced-error-max-items", schema.MaxItems ?? array.Count)); return true; }
		if (!ImmAdvancedValueValidator.TryCreateValue(schema.Element, ConfigSource, forceNonNull: false, out JToken value, out string error)) { SetValidationMessage(error); return true; }

		array.Add(value);

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose(scrollToBottom: true);
		return true;
	}

	private bool OnRemoveArrayItem(JArray array, ImmAdvancedSchema schema, int index)
	{
		if (index < 0 || index >= array.Count) { return true; }
		if (array.Count <= schema.MinItems) { SetValidationMessage(ImmLocalization.Get("advanced-error-min-items", schema.MinItems)); return true; }

		array.RemoveAt(index);

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose();
		return true;
	}

	private bool OnMoveArrayItem(JArray array, int index, int direction)
	{
		int targetIndex = index + direction;
		if (index < 0 || index >= array.Count || targetIndex < 0 || targetIndex >= array.Count) { return true; }

		JToken value = array[index];
		value.Remove();
		array.Insert(targetIndex, value);

		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose();
		return true;
	}

	private bool NavigateTo(ImmAdvancedSchema schema, string label, Func<JToken> getter, Action<JToken> setter)
	{
		JToken token = getter();
		if (token.Type == JTokenType.Null || !IsContainer(schema.Type)) { return true; }

		Navigation.Add(new NavigationFrame(schema, label, getter, setter));
		ScrollValue = 0;
		SetValidationMessage("");
		QueueRecompose(resetScroll: true);
		return true;
	}

	private bool OnSetCurrentNullClicked()
	{
		if (Navigation.Count == 0) { return true; }

		NavigationFrame frame = Navigation[^1];
		if (!frame.Schema.Nullable) { return true; }

		frame.SetValue(JValue.CreateNull());
		SetValidationMessage("");
		EmitWorkingValue();
		QueueRecompose(resetScroll: true);
		return true;
	}

	private bool OnBackClicked()
	{
		if (Navigation.Count > 1) { Navigation.RemoveAt(Navigation.Count - 1); }
		ScrollValue = 0;
		SetValidationMessage("");
		QueueRecompose(resetScroll: true);
		return true;
	}

	private bool OnCloseClicked()
	{
		TryClose();
		return true;
	}

	private void EmitWorkingValue()
	{
		if (RootSchema == null || WorkingRoot == null || OriginalRoot == null) { return; }

		if (!ImmAdvancedValueValidator.TryNormalizeValue(RootSchema, ConfigSource, OriginalRoot, WorkingRoot, out JToken normalized, out string error))
		{
			SetValidationMessage(error);
			return;
		}

		ValueChanged?.Invoke(normalized.ToString(Formatting.None));
	}

	private void SetValidationMessage(string message)
	{
		ValidationMessage = message ?? "";
		SingleComposer?.GetDynamicText("advancedstatus")?.SetNewText(ValidationMessage, autoHeight: false);
	}

	private void QueueRecompose(bool resetScroll = false, bool scrollToBottom = false)
	{
		if (resetScroll) { ScrollValue = 0; }
		if (scrollToBottom) { ScrollValue = float.MaxValue; }
		if (RecomposeQueued) { return; }

		RecomposeQueued = true;
		capi.Event.EnqueueMainThreadTask(() =>
		{
			RecomposeQueued = false;
			if (IsOpened())
			{
				if (scrollToBottom) { ScrollValue = float.MaxValue; }
				Compose();
			}
		}, "integratedmodmanager-advanced-recompose");
	}

	private void AddButton(GuiElementContainer container, string text, ElementBounds bounds, ActionConsumable onClick)
	{
		CairoFont buttonFont = CairoFont.SmallButtonText();
		CairoFont pressedFont = CairoFont.SmallButtonText();
		pressedFont.Color = (double[])GuiStyle.ActiveButtonTextColor.Clone();

		GuiElementTextButton button = new(capi, text, buttonFont, pressedFont, onClick, bounds, EnumButtonStyle.Normal);
		button.SetOrientation(buttonFont.Orientation);
		container.Add(button);
	}

    private static bool IsContainer(string type) => type is "Object" or "Dictionary" or "Array" or "Tuple";
    private static bool CanAdd(ImmAdvancedSchema schema, int count) => !schema.MaxItems.HasValue || count < schema.MaxItems.Value;

    private static string ResolveLabel(ImmAdvancedSchema schema, string fallback)
	{
		string label = ImmLocalization.Resolve(schema.Label);
		return string.IsNullOrWhiteSpace(label) ? fallback : label;
	}

	private static string GetStateLabel(bool present, JToken? value)
	{
		if (!present) { return ImmLocalization.Get("advanced-state-missing"); }
		return value?.Type == JTokenType.Null ? ImmLocalization.Get("advanced-state-null") : ImmLocalization.Get("advanced-state-value");
	}

	private static int GetDropdownIndex(ImmConfigOption[] options, JToken current)
	{
		for (int index = 0; index < options.Length; index++)
		{
			if (options[index].Value != null && JToken.DeepEquals(options[index].Value, current)) { return index; }
		}

		return 0;
	}

    private static string FormatSliderValue(double value, bool integer) => integer ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.####", CultureInfo.InvariantCulture);

    private void AddText(GuiElementContainer container, string text, CairoFont font, ElementBounds bounds) => container.Add(new GuiElementStaticText(capi, text, font.Orientation, bounds, font));

    private void AddRichText(GuiElementContainer container, string text, CairoFont font, ElementBounds bounds) => container.Add(IntegratedModManager.UI.ImmRichText.Create(capi, text, font, bounds));

    private static double MeasureTextHeight(string? text, CairoFont font, double width)
	{
		if (string.IsNullOrWhiteSpace(text)) { return 0; }
		return Math.Ceiling(new TextDrawUtil().GetMultilineTextHeight(font, text, Math.Max(1, width)));
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

	private void OnScrollbarChanged(float value)
	{
		ScrollValue = Math.Clamp(value, 0, Math.Max(0, ContentHeight - (float)(ClipBounds?.fixedHeight ?? 0)));

		if (ContentContainer == null) { return; }

		ContentContainer.Bounds.fixedY = -ScrollValue;
		ContentContainer.Bounds.MarkDirtyRecursive();
		ContentContainer.Bounds.CalcWorldBounds();
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

		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("advancedscroll");
		if (scrollbar != null)
		{
			scrollbar.CurrentYPosition = ScrollValue;
			scrollbar.RecomposeHandle();
		}
	}

	public override void Dispose()
	{
		ValueChanged = null;
		RootSchema = null;
		WorkingRoot = null;
		OriginalRoot = null;
		Navigation.Clear();
		Initializers.Clear();
		PendingDictionaryKeys.Clear();
		base.Dispose();
	}

	private sealed class NavigationFrame(ImmAdvancedSchema schema, string label, Func<JToken> getValue, Action<JToken> setValue)
    {
        public ImmAdvancedSchema Schema { get; } = schema;
        public string Label { get; } = label;
        public Func<JToken> GetValue { get; } = getValue;
        public Action<JToken> SetValue { get; } = setValue;
        public string PendingNewKey = "";
    }

    private sealed class EditorRow(ImmAdvancedSchema schema, double x, double y, double width, double height)
    {
        public ImmAdvancedSchema Schema { get; } = schema;
        public double X { get; } = x;
        public double Y { get; set; } = y;
        public double Width { get; } = width;
        public double Height { get; set; } = height;
    }
}
