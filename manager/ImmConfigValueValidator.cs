#nullable enable

using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

public static class ImmConfigValueValidator
{
	public static bool TryNormalizePatchSetting(ImmConfigEntry entry, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		error = "";

		if (entry.Default == null) { error = "PatchSetting has no Default value."; return false; }

		ImmConfigControlPacket control = new() { Type = entry.Type, ConfigSource = ImmConfigSource.PatchSettings, HasMin = entry.Min.HasValue, Min = entry.Min ?? 0, HasMax = entry.Max.HasValue, Max = entry.Max ?? 0, HasStep = entry.Step.HasValue, Step = entry.Step ?? 0, ElementType = entry.ElementType ?? "", Options = entry.Options?.Select(option => new ImmConfigOptionPacket { Label = option.Label, ValueJson = option.Value?.ToString(Newtonsoft.Json.Formatting.None) ?? "null" }).ToArray() ?? Array.Empty<ImmConfigOptionPacket>() };
		return TryNormalizeValue(control, entry.Default, submitted, out normalized, out error);
	}

	private const int MaximumSliderTicks = 10000;
	private const double StepTolerance = 0.000001;

	public static bool TryValidateCurrentValue(ImmConfigControlPacket control, JToken target, out string error)
	{
		error = "";

		switch (control.Type)
		{
			case "Boolean": return RequireTokenType(target, JTokenType.Boolean, "Boolean", out error);

			case "String": return RequireTokenType(target, JTokenType.String, "String", out error);

			case "Integer":
				if (!RequireTokenType(target, JTokenType.Integer, "Integer", out error)) { return false; }

				long integerValue = target.Value<long>();

				if (integerValue < int.MinValue || integerValue > int.MaxValue) { error = "Integer is outside the supported UI range."; return false; }

			return true;

			case "Decimal":
				if (target.Type == JTokenType.Float || target.Type == JTokenType.Integer) { return true; }

				error = "Map must target a numeric value.";
			return false;

			case "Slider": return TryGetSliderDefinition(control, target, out _, out _, out _, out _, out error);

			case "Dropdown":
				if (control.ConfigSource == ImmConfigSource.ModConfig && (target is JContainer || target.Type == JTokenType.Null)) { error = "Dropdown Map must target a primitive value."; return false; }

				bool found = control.Options.Any(option => JToken.DeepEquals(JToken.Parse(option.ValueJson), target));

				if (!found) { error = "Current value is not present in the declared Options."; }

			return found;

			case "Array": return TryValidateArray(control, target, out _, out error);

			default:
				error = $"Unsupported Type '{control.Type}'.";
			return false;
		}
	}

	public static bool TryNormalizeValue(ImmConfigControlPacket control, JToken target, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		error = "";

		switch (control.Type)
		{
			case "Boolean":
				if (target.Type != JTokenType.Boolean || submitted.Type != JTokenType.Boolean) { error = "expected a Boolean."; return false; }

			return true;

			case "String":
				if (target.Type != JTokenType.String || submitted.Type != JTokenType.String) { error = "expected a String."; return false; }

			return true;

			case "Integer":
				if (target.Type != JTokenType.Integer || submitted.Type != JTokenType.Integer) { error = "expected an Integer."; return false; }

				long integerValue = submitted.Value<long>();

				if (integerValue < int.MinValue || integerValue > int.MaxValue) { error = "Integer is outside the supported UI range."; return false; }

				normalized = new JValue((int)integerValue);
			return true;

			case "Decimal":
				if ((target.Type != JTokenType.Float && target.Type != JTokenType.Integer) || (submitted.Type != JTokenType.Float && submitted.Type != JTokenType.Integer)) { error = "expected a Decimal."; return false; }

				double decimalValue = submitted.Value<double>();

				if (!double.IsFinite(decimalValue)) { error = "Decimal must be finite."; return false; }

				normalized = new JValue(decimalValue);
			return true;

			case "Slider":
				if (!TryGetSliderDefinition(control, target, out double min, out double max, out double step, out bool integerTarget, out error)) { return false; }

				if (submitted.Type != JTokenType.Integer && submitted.Type != JTokenType.Float) { error = "expected a numeric value."; return false; }

				double sliderValue = submitted.Value<double>();

				if (!double.IsFinite(sliderValue) || sliderValue < min - StepTolerance || sliderValue > max + StepTolerance) { error = $"value must be between {min} and {max}."; return false; }

				double steps = (sliderValue - min) / step;

				if (Math.Abs(steps - Math.Round(steps)) > StepTolerance) { error = $"value must follow Step {step}."; return false; }

				if (integerTarget)
				{
					double rounded = Math.Round(sliderValue);

					if (Math.Abs(sliderValue - rounded) > StepTolerance || rounded < long.MinValue || rounded > long.MaxValue) { error = "value must be an Integer."; return false; }

					normalized = new JValue((long)rounded);
				}
				else { normalized = new JValue(sliderValue); }

			return true;

			case "Dropdown":
				ImmConfigOptionPacket? matchingOption = control.Options.FirstOrDefault(option => JToken.DeepEquals(JToken.Parse(option.ValueJson), submitted));

				if (matchingOption == null) { error = "value is not one of the declared Options."; return false; }

				if (control.ConfigSource == ImmConfigSource.ModConfig && (target is JContainer || target.Type == JTokenType.Null)) { error = "mapped config value is not primitive."; return false; }

				normalized = JToken.Parse(matchingOption.ValueJson);
			return true;

			case "Array":
				if (target.Type != JTokenType.Array || submitted.Type != JTokenType.Array) { error = "expected an Array."; return false; }

			return TryValidateArray(control, submitted, out normalized, out error);

			default:
				error = $"unsupported Type '{control.Type}'.";
			return false;
		}
	}

	private static bool TryValidateArray(ImmConfigControlPacket control, JToken value, out JToken normalized, out string error)
	{
		normalized = value;
		error = "";

		if (value is not JArray array) { error = "Map must target an Array."; return false; }

		JArray result = new();

		for (int index = 0; index < array.Count; index++)
		{
			JToken item = array[index];

			switch (control.ElementType)
			{
				case "Boolean":
					if (item.Type != JTokenType.Boolean) { error = $"Array item {index + 1} must be a Boolean."; return false; }

					result.Add(item.Value<bool>());
				break;

				case "Integer":
					if (item.Type != JTokenType.Integer) { error = $"Array item {index + 1} must be an Integer."; return false; }

					long integerValue = item.Value<long>();

					if (integerValue < int.MinValue || integerValue > int.MaxValue) { error = $"Array item {index + 1} is outside the supported Integer range."; return false; }

					result.Add((int)integerValue);
				break;

				case "Decimal":
					if (item.Type != JTokenType.Float && item.Type != JTokenType.Integer) { error = $"Array item {index + 1} must be a Decimal."; return false; }

					double decimalValue = item.Value<double>();

					if (!double.IsFinite(decimalValue)) { error = $"Array item {index + 1} must be finite."; return false; }

					result.Add(decimalValue);
				break;

				case "String":
					if (item.Type != JTokenType.String || string.IsNullOrEmpty(item.Value<string>())) { error = $"Array item {index + 1} must be a non-empty String."; return false; }

					result.Add(item.Value<string>()!);
				break;

				default:
					error = $"Unsupported Array ElementType '{control.ElementType}'.";
				return false;
			}
		}

		normalized = result;
		return true;
	}

	public static bool TryGetSliderDefinition(ImmConfigControlPacket control, JToken target, out double min, out double max, out double step, out bool integerTarget, out string error)
	{
		min = control.Min;
		max = control.Max;
		step = control.HasStep ? control.Step : 1;

		integerTarget = control.ConfigSource == ImmConfigSource.PatchSettings ? IsWholeNumber(target) && IsWholeNumber(min) && IsWholeNumber(max) && IsWholeNumber(step) : target.Type == JTokenType.Integer;

		error = "";

		if (!control.HasMin || !control.HasMax) { error = "Slider requires Min and Max."; return false; }
		if (target.Type != JTokenType.Integer && target.Type != JTokenType.Float) { error = "Slider Map must target an Integer or Decimal."; return false; }
		if (step <= 0) { error = "Slider requires a positive Step."; return false; }
		if (integerTarget && (Math.Abs(min - Math.Round(min)) > StepTolerance || Math.Abs(max - Math.Round(max)) > StepTolerance || Math.Abs(step - Math.Round(step)) > StepTolerance)) { error = "Integer Slider requires whole-number Min, Max and Step values."; return false; }

		double tickCount = (max - min) / step;

		if (!double.IsFinite(tickCount) || tickCount <= 0 || tickCount > MaximumSliderTicks || Math.Abs(tickCount - Math.Round(tickCount)) > StepTolerance) { error = $"Slider range must resolve to at most {MaximumSliderTicks} whole Step intervals."; return false; }

		double current = target.Value<double>();
		double currentSteps = (current - min) / step;

		if (current < min - StepTolerance || current > max + StepTolerance || Math.Abs(currentSteps - Math.Round(currentSteps)) > StepTolerance) { error = "Current value does not fit the Slider range/Step."; return false; }

		return true;
	}

	private static bool IsWholeNumber(JToken token)
	{
		if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) { return false; }

		return IsWholeNumber(token.Value<double>());
	}

	private static bool IsWholeNumber(double value) { return double.IsFinite(value) && Math.Abs(value - Math.Round(value)) <= StepTolerance; }

	private static bool RequireTokenType(JToken token, JTokenType expected, string expectedName, out string error)
	{
		if (token.Type == expected) { error = ""; return true; }

		error = $"Map must target a {expectedName}.";
		return false;
	}
}
