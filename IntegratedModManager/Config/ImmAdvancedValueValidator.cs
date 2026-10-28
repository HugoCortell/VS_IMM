using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

public static class ImmAdvancedValueValidator
{
    public static bool TryValidateCurrentValue(ImmAdvancedSchema schema, ImmConfigSource source, JToken target, out string error) => TryNormalizeNode(schema, source, target, target, out _, out error);

    public static bool TryNormalizeValue(ImmAdvancedSchema schema, ImmConfigSource source, JToken target, JToken submitted, out JToken normalized, out string error) => TryNormalizeNode(schema, source, target, submitted, out normalized, out error);

    public static bool TryNormalizeNode(ImmAdvancedSchema schema, ImmConfigSource source, JToken? target, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		error = "";

		if (submitted.Type == JTokenType.Null)
		{
			if (!schema.Nullable) { error = "value cannot be null."; return false; }

			normalized = JValue.CreateNull();
			return true;
		}

		switch (schema.Type)
		{
			case "Object": return TryNormalizeObject(schema, source, target, submitted, out normalized, out error);
			case "Dictionary": return TryNormalizeDictionary(schema, source, target, submitted, out normalized, out error);
			case "Array": return TryNormalizeArray(schema, source, target, submitted, out normalized, out error);
			case "Tuple": return TryNormalizeTuple(schema, source, target, submitted, out normalized, out error);
			
			case "Boolean":
			case "String":
			case "Integer":
			case "Decimal":
			case "Slider":
			case "Dropdown":
			return TryNormalizeLeaf(schema, source, target, submitted, out normalized, out error);
			
			default:
				error = $"unsupported Advanced Type '{schema.Type}'.";
			return false;
		}
	}

	public static bool TryCreateValue(ImmAdvancedSchema schema, ImmConfigSource source, bool forceNonNull, out JToken value, out string error)
	{
		error = "";

		// Advanced schemas are serialized into the control packet as JSON.
		// An omitted JToken-valued InitialValue can round-trip through Json.NET as a JSON null token rather than a C# null reference.
		// Treat JSON null as "no concrete InitialValue"
		// Nullable already defines whether newly-created values should begin as null. This also lets forceNonNull reliably construct a real value.
		if (schema.InitialValue != null && schema.InitialValue.Type != JTokenType.Null)
		{
			value = schema.InitialValue.DeepClone();
			return true;
		}

		if (schema.Nullable && !forceNonNull)
		{
			value = JValue.CreateNull();
			return true;
		}

		switch (schema.Type)
		{
			case "Boolean":
				value = new JValue(false);
			return true;

			case "String":
				value = new JValue("");
			return true;

			case "Integer":
				value = new JValue(0);
			return true;

			case "Decimal":
				value = new JValue(0.0);
			return true;

			case "Slider":
				if (!schema.Min.HasValue) { value = JValue.CreateNull(); error = "Slider has no Min value."; return false; }

				double sliderMin = schema.Min.Value;
				bool wholeSlider = IsWholeNumber(sliderMin) && IsWholeNumber(schema.Max ?? sliderMin) && IsWholeNumber(schema.Step ?? 1);

				value = wholeSlider ? new JValue((long)Math.Round(sliderMin)) : new JValue(sliderMin);
			return true;

			case "Dropdown":
				ImmConfigOption? option = schema.Options?.FirstOrDefault();
				if (option?.Value == null) { value = JValue.CreateNull(); error = "Dropdown has no Option value."; return false; }

				value = option.Value.DeepClone();
			return true;

			case "Object":
				JObject resultObject = [];

				foreach (ImmAdvancedSchema field in schema.Fields ?? [])
				{
					if (field.Optional) { continue; }
					if (!TryCreateValue(field, source, forceNonNull: false, out JToken fieldValue, out error)) { value = JValue.CreateNull(); return false; }

					resultObject[field.Key] = fieldValue;
				}

				value = resultObject;
			return true;

			case "Dictionary":
				if (schema.MinItems > 0) { value = JValue.CreateNull(); error = "Dictionary with MinItems greater than zero requires InitialValue."; return false; }

				value = new JObject();
			return true;

			case "Array":
				JArray resultArray = [];

				for (int index = 0; index < schema.MinItems; index++)
				{
					if (schema.Element == null || !TryCreateValue(schema.Element, source, forceNonNull: false, out JToken itemValue, out error)) { value = JValue.CreateNull(); return false; }
					resultArray.Add(itemValue);
				}

				value = resultArray;
			return true;

			case "Tuple":
				JArray tuple = [];

				foreach (ImmAdvancedSchema item in schema.Items ?? [])
				{
					if (!TryCreateValue(item, source, forceNonNull: false, out JToken itemValue, out error)) { value = JValue.CreateNull(); return false; }
					tuple.Add(itemValue);
				}

				value = tuple;
			return true;

			default:
				value = JValue.CreateNull();
				error = $"unsupported Advanced Type '{schema.Type}'.";
			return false;
		}
	}

    public static ImmConfigControlPacket CreateLeafControl(ImmAdvancedSchema schema, ImmConfigSource source) => new()
    {
        Type = schema.Type,
        ConfigSource = source,
        HasMin = schema.Min.HasValue,
        Min = schema.Min ?? 0,
        HasMax = schema.Max.HasValue,
        Max = schema.Max ?? 0,
        HasStep = schema.Step.HasValue,
        Step = schema.Step ?? 0,
        Options = schema.Options?.Select(option => new ImmConfigOptionPacket
        {
            Label = option.Label,
            ValueJson = option.Value?.ToString(Formatting.None) ?? "null"
        }).ToArray() ?? []
    };

    private static bool TryNormalizeObject(ImmAdvancedSchema schema, ImmConfigSource source, JToken? target, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		error = "";

		if (submitted is not JObject submittedObject) { error = "expected an Object."; return false; }
		if (target != null && target.Type != JTokenType.Null && target is not JObject) { error = "mapped value is not an Object."; return false; }

		JObject? targetObject = target as JObject;
		JObject result = targetObject != null ? (JObject)targetObject.DeepClone() : [];

		foreach (ImmAdvancedSchema field in schema.Fields)
		{
			JProperty? submittedProperty = submittedObject.Property(field.Key, StringComparison.Ordinal);
			JProperty? targetProperty = targetObject?.Property(field.Key, StringComparison.Ordinal);

			if (submittedProperty == null)
			{
				if (!field.Optional) { error = $"field '{field.Key}' is required."; return false; }

				result.Property(field.Key, StringComparison.Ordinal)?.Remove();
				continue;
			}

			JToken? fieldTarget = targetProperty?.Value;
			if (!TryNormalizeNode(field, source, fieldTarget, submittedProperty.Value, out JToken normalizedField, out string fieldError))
			{
				error = $"{field.Key}: {fieldError}";
				return false;
			}

			result[field.Key] = normalizedField;
		}

		normalized = result;
		return true;
	}

	private static bool TryNormalizeDictionary(ImmAdvancedSchema schema, ImmConfigSource source, JToken? target, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		error = "";

		if (submitted is not JObject submittedObject) { error = "expected a Dictionary object."; return false; }
		if (target != null && target.Type != JTokenType.Null && target is not JObject) { error = "mapped value is not a Dictionary object."; return false; }
		if (schema.Value == null) { error = "Dictionary schema has no Value definition."; return false; }
		if (!ValidateCollectionCount(schema, submittedObject.Count, out error)) { return false; }

		Regex? keyRegex = null;

		if (!string.IsNullOrWhiteSpace(schema.KeyPattern))
		{
			try { keyRegex = new Regex(schema.KeyPattern, RegexOptions.CultureInvariant); }
			catch (Exception exception) { error = $"Dictionary KeyPattern is invalid: {exception.Message}"; return false; }
		}

		JObject? targetObject = target as JObject;
		JObject result = [];

		foreach (JProperty property in submittedObject.Properties())
		{
			if (string.IsNullOrWhiteSpace(property.Name)) { error = "Dictionary keys cannot be empty or whitespace."; return false; }
			if (keyRegex != null && !keyRegex.IsMatch(property.Name)) { error = $"Dictionary key '{property.Name}' does not match KeyPattern."; return false; }

			JToken? itemTarget = targetObject?.Property(property.Name, StringComparison.Ordinal)?.Value;

			// A newly added/reordered dynamic entry has no hidden structure to merge.
			// Its submitted value is already produced from the declared schema by the Advanced editor.
			itemTarget ??= property.Value;

			if (!TryNormalizeNode(schema.Value, source, itemTarget, property.Value, out JToken normalizedItem, out string itemError))
			{
				error = $"{property.Name}: {itemError}";
				return false;
			}

			result.Add(property.Name, normalizedItem);
		}

		normalized = result;
		return true;
	}

	private static bool TryNormalizeArray(ImmAdvancedSchema schema, ImmConfigSource source, JToken? target, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		error = "";

		if (submitted is not JArray submittedArray) { error = "expected an Array."; return false; }
		if (target != null && target.Type != JTokenType.Null && target is not JArray) { error = "mapped value is not an Array."; return false; }
		if (schema.Element == null) { error = "Array schema has no Element definition."; return false; }
		if (!ValidateCollectionCount(schema, submittedArray.Count, out error)) { return false; }

		JArray result = [];

		for (int index = 0; index < submittedArray.Count; index++)
		{
			JToken item = submittedArray[index];

			// Arrays may be reordered.
			// Use the submitted item as its own structural baseline so undeclared properties travel with the item rather than being merged by old index.
			if (!TryNormalizeNode(schema.Element, source, item, item, out JToken normalizedItem, out string itemError))
			{
				error = $"item {index + 1}: {itemError}";
				return false;
			}

			result.Add(normalizedItem);
		}

		normalized = result;
		return true;
	}

	private static bool TryNormalizeTuple(ImmAdvancedSchema schema, ImmConfigSource source, JToken? target, JToken submitted, out JToken normalized, out string error)
	{
		normalized = submitted;
		error = "";

		if (submitted is not JArray submittedArray) { error = "expected a Tuple array."; return false; }
		if (target != null && target.Type != JTokenType.Null && target is not JArray) { error = "mapped value is not a Tuple array."; return false; }
		if (submittedArray.Count != schema.Items.Count) { error = $"Tuple requires exactly {schema.Items.Count} items."; return false; }

		JArray? targetArray = target as JArray;
		JArray result = [];

		for (int index = 0; index < schema.Items.Count; index++)
		{
			JToken item = submittedArray[index];
			JToken? itemTarget = targetArray != null && index < targetArray.Count ? targetArray[index] : item;

			if (!TryNormalizeNode(schema.Items[index], source, itemTarget, item, out JToken normalizedItem, out string itemError))
			{
				error = $"item {index + 1}: {itemError}";
				return false;
			}

			result.Add(normalizedItem);
		}

		normalized = result;
		return true;
	}

	private static bool TryNormalizeLeaf(ImmAdvancedSchema schema, ImmConfigSource source, JToken? target, JToken submitted, out JToken normalized, out string error)
	{
		ImmConfigControlPacket control = CreateLeafControl(schema, source);
		JToken baseline = target == null || (target.Type == JTokenType.Null && schema.Nullable) ? submitted : target;

		return ImmConfigValueValidator.TryNormalizeValue(control, baseline, submitted, out normalized, out error);
	}

	private static bool ValidateCollectionCount(ImmAdvancedSchema schema, int count, out string error)
	{
		error = "";

		if (count < schema.MinItems) { error = $"requires at least {schema.MinItems} items."; return false; }
		if (schema.MaxItems.HasValue && count > schema.MaxItems.Value) { error = $"allows at most {schema.MaxItems.Value} items."; return false; }

		return true;
	}

    private static bool IsWholeNumber(double value) => double.IsFinite(value) && Math.Abs(value - Math.Round(value)) <= 0.000001 && value >= long.MinValue && value <= long.MaxValue;
}
