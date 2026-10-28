#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

public static class ImmAdvancedSchemaValidator
{
	private const int MaximumDepth = 32;

	private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
	{
		"Object", "Dictionary", "Array", "Tuple",
		"Boolean", "String", "Integer", "Decimal", "Slider", "Dropdown"
	};

	public static void Validate(ImmAdvancedSchema? schema, string prefix, ImmConfigSource source)
	{
		if (schema == null) { throw new InvalidDataException($"{prefix} is required."); }

		ValidateNode(schema, prefix, source, depth: 0, objectField: false);
	}

	private static void ValidateNode(ImmAdvancedSchema schema, string prefix, ImmConfigSource source, int depth, bool objectField)
	{
		if (depth > MaximumDepth) { throw new InvalidDataException($"{prefix} exceeds the maximum Advanced nesting depth of {MaximumDepth}."); }
		if (!SupportedTypes.Contains(schema.Type)) { throw new InvalidDataException($"{prefix}.Type '{schema.Type}' is not supported."); }

		schema.Fields ??= [];
		schema.Items ??= [];
		schema.Options ??= [];

		if (objectField)
		{
			if (string.IsNullOrWhiteSpace(schema.Key)) { throw new InvalidDataException($"{prefix}.Key is required."); }
			if (schema.ColumnSpan < 1 || schema.ColumnSpan > 4) { throw new InvalidDataException($"{prefix}.ColumnSpan must be between 1 and 4."); }
		}
		else
		{
			if (schema.Optional) { throw new InvalidDataException($"{prefix}.Optional is only valid for entries inside Object.Fields."); }
			if (!string.IsNullOrWhiteSpace(schema.Key)) { throw new InvalidDataException($"{prefix}.Key is only valid for entries inside Object.Fields."); }
			if (schema.ColumnSpan != 1) { throw new InvalidDataException($"{prefix}.ColumnSpan is only valid for entries inside Object.Fields."); }
		}

		switch (schema.Type)
		{
			case "Object":
				ValidateObject(schema, prefix, source, depth);
			break;

			case "Dictionary":
				ValidateDictionary(schema, prefix, source, depth);
			break;

			case "Array":
				ValidateArray(schema, prefix, source, depth);
			break;

			case "Tuple":
				ValidateTuple(schema, prefix, source, depth);
			break;

			case "Slider":
				ValidateLeafMetadata(schema, prefix);
				ValidateSlider(schema, prefix);
			break;

			case "Dropdown":
				ValidateLeafMetadata(schema, prefix);
				ValidateDropdown(schema, prefix);
			break;

			default:
				ValidateLeafMetadata(schema, prefix);
			break;
		}

		if (schema.InitialValue != null)
		{
			if (!ImmAdvancedValueValidator.TryNormalizeValue(schema, source, schema.InitialValue, schema.InitialValue, out JToken normalized, out string error))
			{
				throw new InvalidDataException($"{prefix}.InitialValue is invalid: {error}");
			}

			schema.InitialValue = normalized;
		}
	}

	private static void ValidateObject(ImmAdvancedSchema schema, string prefix, ImmConfigSource source, int depth)
	{
		if (schema.Fields.Count == 0) { throw new InvalidDataException($"{prefix}.Fields requires at least one entry for Object."); }
		if (schema.Columns < 1 || schema.Columns > 4) { throw new InvalidDataException($"{prefix}.Columns must be between 1 and 4."); }
		if (schema.Element != null || schema.Value != null || schema.Items.Count > 0) { throw new InvalidDataException($"{prefix} Object cannot declare Element, Value, or Items."); }
		if (schema.MinItems != 0 || schema.MaxItems.HasValue) { throw new InvalidDataException($"{prefix} Object cannot declare MinItems or MaxItems."); }
		if (HasPrimitiveMetadata(schema)) { throw new InvalidDataException($"{prefix} Object cannot declare Min, Max, Step, or Options."); }
		if (!string.IsNullOrWhiteSpace(schema.KeyPattern) || !string.IsNullOrWhiteSpace(schema.KeyLabel)) { throw new InvalidDataException($"{prefix} Object cannot declare KeyPattern or KeyLabel."); }

		HashSet<string> keys = new(StringComparer.Ordinal);

		for (int index = 0; index < schema.Fields.Count; index++)
		{
			ImmAdvancedSchema? field = schema.Fields[index];
			if (field == null) { throw new InvalidDataException($"{prefix}.Fields[{index}] cannot be null."); }

			string fieldPrefix = $"{prefix}.Fields[{index}]";
			ValidateNode(field, fieldPrefix, source, depth + 1, objectField: true);

			if (!keys.Add(field.Key)) { throw new InvalidDataException($"{fieldPrefix}.Key '{field.Key}' is duplicated."); }
			if (field.ColumnSpan > schema.Columns) { throw new InvalidDataException($"{fieldPrefix}.ColumnSpan cannot exceed the parent Object.Columns value."); }
		}
	}

	private static void ValidateDictionary(ImmAdvancedSchema schema, string prefix, ImmConfigSource source, int depth)
	{
		if (schema.Value == null) { throw new InvalidDataException($"{prefix}.Value is required for Dictionary."); }
		if (schema.Fields.Count > 0 || schema.Element != null || schema.Items.Count > 0) { throw new InvalidDataException($"{prefix} Dictionary cannot declare Fields, Element, or Items."); }
		if (schema.Columns != 1) { throw new InvalidDataException($"{prefix}.Columns is only valid for Object."); }
		if (HasPrimitiveMetadata(schema)) { throw new InvalidDataException($"{prefix} Dictionary cannot declare Min, Max, Step, or Options."); }

		ValidateCollectionBounds(schema, prefix);
		if (schema.MinItems > 0 && (schema.InitialValue == null || schema.InitialValue.Type == JTokenType.Null)) { throw new InvalidDataException($"{prefix} Dictionary with MinItems greater than zero requires a non-null InitialValue because IMM cannot invent dictionary keys."); }

		if (!string.IsNullOrWhiteSpace(schema.KeyPattern))
		{
			try { _ = new Regex(schema.KeyPattern, RegexOptions.CultureInvariant); }
			catch (Exception exception) { throw new InvalidDataException($"{prefix}.KeyPattern is invalid: {exception.Message}"); }
		}

		ValidateNode(schema.Value, $"{prefix}.Value", source, depth + 1, objectField: false);
	}

	private static void ValidateArray(ImmAdvancedSchema schema, string prefix, ImmConfigSource source, int depth)
	{
		if (schema.Element == null) { throw new InvalidDataException($"{prefix}.Element is required for Array."); }
		if (schema.Fields.Count > 0 || schema.Value != null || schema.Items.Count > 0) { throw new InvalidDataException($"{prefix} Array cannot declare Fields, Value, or Items."); }
		if (schema.Columns != 1) { throw new InvalidDataException($"{prefix}.Columns is only valid for Object."); }
		if (HasPrimitiveMetadata(schema)) { throw new InvalidDataException($"{prefix} Array cannot declare Min, Max, Step, or Options."); }
		if (!string.IsNullOrWhiteSpace(schema.KeyPattern) || !string.IsNullOrWhiteSpace(schema.KeyLabel)) { throw new InvalidDataException($"{prefix} Array cannot declare KeyPattern or KeyLabel."); }

		ValidateCollectionBounds(schema, prefix);
		ValidateNode(schema.Element, $"{prefix}.Element", source, depth + 1, objectField: false);
	}

	private static void ValidateTuple(ImmAdvancedSchema schema, string prefix, ImmConfigSource source, int depth)
	{
		if (schema.Items.Count == 0) { throw new InvalidDataException($"{prefix}.Items requires at least one entry for Tuple."); }
		if (schema.Fields.Count > 0 || schema.Element != null || schema.Value != null) { throw new InvalidDataException($"{prefix} Tuple cannot declare Fields, Element, or Value."); }
		if (schema.Columns != 1) { throw new InvalidDataException($"{prefix}.Columns is only valid for Object."); }
		if (schema.MinItems != 0 || schema.MaxItems.HasValue) { throw new InvalidDataException($"{prefix} Tuple cannot declare MinItems or MaxItems."); }
		if (HasPrimitiveMetadata(schema)) { throw new InvalidDataException($"{prefix} Tuple cannot declare Min, Max, Step, or Options."); }
		if (!string.IsNullOrWhiteSpace(schema.KeyPattern) || !string.IsNullOrWhiteSpace(schema.KeyLabel)) { throw new InvalidDataException($"{prefix} Tuple cannot declare KeyPattern or KeyLabel."); }

		for (int index = 0; index < schema.Items.Count; index++)
		{
			ImmAdvancedSchema? item = schema.Items[index];
			if (item == null) { throw new InvalidDataException($"{prefix}.Items[{index}] cannot be null."); }

			ValidateNode(item, $"{prefix}.Items[{index}]", source, depth + 1, objectField: false);
		}
	}

	private static void ValidateLeafMetadata(ImmAdvancedSchema schema, string prefix)
	{
		if (schema.Fields.Count > 0 || schema.Element != null || schema.Value != null || schema.Items.Count > 0) { throw new InvalidDataException($"{prefix} primitive node cannot declare Fields, Element, Value, or Items."); }
		if (schema.Columns != 1) { throw new InvalidDataException($"{prefix}.Columns is only valid for Object."); }
		if (schema.MinItems != 0 || schema.MaxItems.HasValue) { throw new InvalidDataException($"{prefix} primitive node cannot declare MinItems or MaxItems."); }
		if (!string.IsNullOrWhiteSpace(schema.KeyPattern) || !string.IsNullOrWhiteSpace(schema.KeyLabel)) { throw new InvalidDataException($"{prefix} primitive node cannot declare KeyPattern or KeyLabel."); }

		if (schema.Type is not ("Slider" or "Dropdown") && HasPrimitiveMetadata(schema))
		{
			throw new InvalidDataException($"{prefix} {schema.Type} cannot declare Min, Max, Step, or Options.");
		}

		if (schema.Type == "Slider" && schema.Options.Count > 0) { throw new InvalidDataException($"{prefix} Slider cannot declare Options."); }
		if (schema.Type == "Dropdown" && (schema.Min.HasValue || schema.Max.HasValue || schema.Step.HasValue)) { throw new InvalidDataException($"{prefix} Dropdown cannot declare Min, Max, or Step."); }
	}

	private static void ValidateSlider(ImmAdvancedSchema schema, string prefix)
	{
		if (!schema.Min.HasValue || !schema.Max.HasValue) { throw new InvalidDataException($"{prefix} Slider requires Min and Max."); }
		if (!double.IsFinite(schema.Min.Value) || !double.IsFinite(schema.Max.Value) || schema.Max.Value <= schema.Min.Value) { throw new InvalidDataException($"{prefix}.Max must be finite and greater than Min."); }
		if (schema.Step.HasValue && (!double.IsFinite(schema.Step.Value) || schema.Step.Value <= 0)) { throw new InvalidDataException($"{prefix}.Step must be finite and greater than zero."); }

		double step = schema.Step ?? 1;
		double tickCount = (schema.Max.Value - schema.Min.Value) / step;

		if (!double.IsFinite(tickCount) || tickCount <= 0 || tickCount > 10000 || Math.Abs(tickCount - Math.Round(tickCount)) > 0.000001)
		{
			throw new InvalidDataException($"{prefix} Slider range must resolve to at most 10000 whole Step intervals.");
		}
	}

	private static void ValidateDropdown(ImmAdvancedSchema schema, string prefix)
	{
		if (schema.Options.Count == 0) { throw new InvalidDataException($"{prefix} Dropdown requires at least one Option."); }

		List<JToken> values = [];

		for (int index = 0; index < schema.Options.Count; index++)
		{
			ImmConfigOption? option = schema.Options[index];
			if (option == null || string.IsNullOrWhiteSpace(option.Label)) { throw new InvalidDataException($"{prefix}.Options[{index}] requires Label."); }
			if (option.Value == null || option.Value is JContainer || option.Value.Type is JTokenType.Null or JTokenType.Undefined) { throw new InvalidDataException($"{prefix}.Options[{index}].Value must be a non-null primitive."); }

			foreach (JToken existing in values)
			{
				if (JToken.DeepEquals(existing, option.Value)) { throw new InvalidDataException($"{prefix}.Options[{index}].Value is duplicated."); }
			}

			values.Add(option.Value);
		}
	}

	private static void ValidateCollectionBounds(ImmAdvancedSchema schema, string prefix)
	{
		if (schema.MinItems < 0) { throw new InvalidDataException($"{prefix}.MinItems cannot be negative."); }
		if (schema.MaxItems.HasValue && schema.MaxItems.Value < schema.MinItems) { throw new InvalidDataException($"{prefix}.MaxItems cannot be less than MinItems."); }
	}

    private static bool HasPrimitiveMetadata(ImmAdvancedSchema schema) => schema.Min.HasValue || schema.Max.HasValue || schema.Step.HasValue || schema.Options.Count > 0;
}
