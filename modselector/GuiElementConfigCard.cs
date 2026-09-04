#nullable enable

using Cairo;
using Vintagestory.API.Client;

namespace IntegratedModManager.ModSelector;

public sealed class GuiElementConfigCard : GuiElement
{
	public GuiElementConfigCard(ICoreClientAPI capi, ElementBounds bounds) : base(capi, bounds) { }

	public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
	{
		// This element is visual chrome only.
		// GuiElement's default implementation consumes mouse-down events, which prevents the interactive control layered inside this card from receiving them.
	}

	public override void ComposeElements(Context ctxStatic, ImageSurface surface)
	{
		Bounds.CalcWorldBounds();

		double[] color = GuiStyle.DialogDefaultBgColor;

		ctxStatic.SetSourceRGBA(color[0], color[1], color[2], color[3]);

		RoundRectangle(ctxStatic, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight, GuiStyle.ElementBGRadius);

		ctxStatic.Fill();

		EmbossRoundRectangleElement(ctxStatic, Bounds.drawX, Bounds.drawY, Bounds.InnerWidth, Bounds.InnerHeight, inverse: false, depth: 2, radius: (int)GuiStyle.ElementBGRadius);
	}
}
