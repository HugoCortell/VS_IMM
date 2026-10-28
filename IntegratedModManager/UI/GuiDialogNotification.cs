using IntegratedModManager.Config;
using Vintagestory.API.Client;

namespace IntegratedModManager.UI;

public sealed class GuiDialogNotification : GuiDialog
{
	private const double Width = 420;
	private const double TextPadding = 20;
	private const double ButtonWidth = 160;
	private const double ButtonHeight = 32;
	private const double ButtonGap = 20;

	private string Title = "";
	private string Description = "";
	private string ButtonLabel = "";

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.4;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

	public GuiDialogNotification(ICoreClientAPI capi) : base(capi) { }

    public bool Show(string description, string buttonLabel) => Show("", description, buttonLabel);

    public bool Show(string title, string description, string buttonLabel)
	{
		Title = title ?? "";
		Description = description ?? "";
		ButtonLabel = string.IsNullOrWhiteSpace(buttonLabel) ? ImmLocalization.Get("button-alright") : buttonLabel;

		Compose();
		return TryOpen();
	}

	private void Compose()
	{
		CairoFont font = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);
		CairoFont titleFont = CairoFont.WhiteMediumText().WithWeight(Cairo.FontWeight.Bold).WithOrientation(EnumTextOrientation.Center);

		double textWidth = Width - TextPadding * 2;
		double titleHeight = string.IsNullOrWhiteSpace(Title) ? 0 : 28;
		double titleGap = titleHeight > 0 ? 10 : 0;
		double textHeight = Math.Max(24, new TextDrawUtil().GetMultilineTextHeight(font, Description, textWidth));
		double textY = TextPadding + titleHeight + titleGap;

		ElementBounds titleBounds = ElementBounds.Fixed(TextPadding, TextPadding, textWidth, titleHeight);
		ElementBounds textBounds = ElementBounds.Fixed(TextPadding, textY, textWidth, textHeight);
		ElementBounds buttonBounds = ElementBounds.Fixed((Width - ButtonWidth) / 2, textY + textHeight + ButtonGap, ButtonWidth, ButtonHeight);
		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);

		backgroundBounds.BothSizing = ElementSizing.FitToChildren;
		backgroundBounds.WithChildren(titleHeight > 0 ? new[] { titleBounds, textBounds, buttonBounds } : new[] { textBounds, buttonBounds });

		SingleComposer?.Dispose();

		GuiComposer composer = capi.Gui.CreateCompo("integratedmodmanager-notification", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle)).AddShadedDialogBG(backgroundBounds, withTitleBar: false).BeginChildElements(backgroundBounds);
		if (titleHeight > 0) { composer.AddStaticText(Title, titleFont, EnumTextOrientation.Center, titleBounds); }
		SingleComposer = composer.AddStaticText(Description, font, EnumTextOrientation.Center, textBounds).AddSmallButton(ButtonLabel, OnButtonClicked, buttonBounds).EndChildElements().Compose();
	}

	private bool OnButtonClicked() { TryClose(); return true; }
}
