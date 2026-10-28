using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace IntegratedModManager.ModSelector;

internal static class ImmClipRender
{
	public static bool TryBegin(ICoreClientAPI api, ElementBounds bounds, ElementBounds? clipBounds, bool requireFullyInside, out bool clipPushed)
	{
		clipPushed = false;

		if (clipBounds == null) { return true; }

		bounds.CalcWorldBounds();
		clipBounds.CalcWorldBounds();

		double right = bounds.renderX + bounds.OuterWidth;
		double bottom = bounds.renderY + bounds.OuterHeight;
		double clipRight = clipBounds.renderX + clipBounds.InnerWidth;
		double clipBottom = clipBounds.renderY + clipBounds.InnerHeight;

		bool visible =
			requireFullyInside
				? bounds.renderX >= clipBounds.renderX &&
					bounds.renderY >= clipBounds.renderY &&
					right <= clipRight &&
					bottom <= clipBottom
				: right > clipBounds.renderX &&
					bottom > clipBounds.renderY &&
					bounds.renderX < clipRight &&
					bounds.renderY < clipBottom;

		if (!visible) { return false; }

		api.Render.PushScissor(clipBounds, stacking: true);
		clipPushed = true;
		return true;
	}

	public static void End(ICoreClientAPI api, bool clipPushed)
	{
		if (clipPushed) { api.Render.PopScissor(); }
	}
}

public sealed class GuiElementImmTextInput(ICoreClientAPI capi, ElementBounds bounds, Action<string> onTextChanged, CairoFont font) : GuiElementTextInput(capi, bounds, onTextChanged, font)
{
    public override void RenderInteractiveElements(float deltaTime)
	{
		if (!ImmClipRender.TryBegin(api, Bounds, InsideClipBounds, requireFullyInside: true, out bool clipPushed)) { return; }

		try { base.RenderInteractiveElements(deltaTime); }
		finally { ImmClipRender.End(api, clipPushed); }
	}
}

public sealed class GuiElementImmNumberInput(ICoreClientAPI capi, ElementBounds bounds, Action<string> onTextChanged, CairoFont font) : GuiElementNumberInput(capi, bounds, onTextChanged, font)
{
    public override void RenderInteractiveElements(float deltaTime)
	{
		if (!ImmClipRender.TryBegin(api, Bounds, InsideClipBounds, requireFullyInside: true, out bool clipPushed)) { return; }

		try { base.RenderInteractiveElements(deltaTime); }
		finally { ImmClipRender.End(api, clipPushed); }
	}
}

public sealed class GuiElementImmSlider(ICoreClientAPI capi, ActionConsumable<int> onNewSliderValue, ElementBounds bounds) : GuiElementSlider(capi, onNewSliderValue, bounds)
{
    public override void RenderInteractiveElements(float deltaTime)
	{
		if (!ImmClipRender.TryBegin(api, Bounds, InsideClipBounds, requireFullyInside: false, out bool clipPushed)) { return; }

		try { base.RenderInteractiveElements(deltaTime); }
		finally { ImmClipRender.End(api, clipPushed); }
	}
}

public sealed class GuiElementImmSwitch(ICoreClientAPI capi, Action<bool> onToggled, ElementBounds bounds, double size = 30, double padding = 4) : GuiElementSwitch(capi, onToggled, bounds, size, padding)
{
    public override void RenderInteractiveElements(float deltaTime)
	{
		if (!ImmClipRender.TryBegin(api, Bounds, InsideClipBounds, requireFullyInside: false, out bool clipPushed)) { return; }

		try { base.RenderInteractiveElements(deltaTime); }
		finally { ImmClipRender.End(api, clipPushed); }
	}
}

public sealed class GuiElementImmDropDown(ICoreClientAPI capi, string[] values, string[] names, int selectedIndex, SelectionChangedDelegate onSelectionChanged, ElementBounds bounds, CairoFont font, bool multiSelect) : GuiElementDropDown(capi, values, names, selectedIndex, onSelectionChanged, bounds, font, multiSelect)
{
    public override void RenderInteractiveElements(float deltaTime)
	{
		if (!ImmClipRender.TryBegin(api, Bounds, InsideClipBounds, requireFullyInside: false, out bool clipPushed)) { return; }

		try { base.RenderInteractiveElements(deltaTime); }
		finally { ImmClipRender.End(api, clipPushed); }
	}
}
