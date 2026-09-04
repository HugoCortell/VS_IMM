#nullable enable

using System;
using IntegratedModManager.Config;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace IntegratedModManager.UI;

public sealed class GuiDialogNotification : GuiDialog
{
	private const double Width = 420;
	private const double TextPadding = 20;
	private const double ButtonWidth = 160;
	private const double ButtonHeight = 32;
	private const double ButtonGap = 20;

	private string Description = "";
	private string ButtonLabel = "";

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.4;
	public override bool DisableMouseGrab => true;
	public override bool PrefersUngrabbedMouse => true;
	public override bool CaptureAllInputs() => true;

	public GuiDialogNotification(ICoreClientAPI capi) : base(capi) { }

	public bool Show(string description, string buttonLabel)
	{
		Description = description ?? "";
		ButtonLabel = string.IsNullOrWhiteSpace(buttonLabel) ? ImmLocalization.Get("button-alright") : buttonLabel;

		Compose();
		return TryOpen();
	}

	private void Compose()
	{
		CairoFont font = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center);

		double textWidth = Width - TextPadding * 2;
		double textHeight = Math.Max(24, new TextDrawUtil().GetMultilineTextHeight(font, Description, textWidth));

		ElementBounds textBounds = ElementBounds.Fixed(TextPadding, TextPadding, textWidth, textHeight);
		ElementBounds buttonBounds = ElementBounds.Fixed((Width - ButtonWidth) / 2, TextPadding + textHeight + ButtonGap, ButtonWidth, ButtonHeight);
		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);

		backgroundBounds.BothSizing = ElementSizing.FitToChildren;
		backgroundBounds.WithChildren(textBounds, buttonBounds);

		SingleComposer?.Dispose();

		SingleComposer = capi.Gui.CreateCompo("integratedmodmanager-notification", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle)).AddShadedDialogBG(backgroundBounds, withTitleBar: false).BeginChildElements(backgroundBounds).AddStaticText(Description, font, EnumTextOrientation.Center, textBounds).AddSmallButton(ButtonLabel, OnButtonClicked, buttonBounds).EndChildElements().Compose();
	}

	private bool OnButtonClicked() { TryClose(); return true; }
}
