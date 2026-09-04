#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Util;

namespace IntegratedModManager.Config;

public sealed class ImmContentPath
{
	private readonly IPathStep[] Steps;

	private ImmContentPath(IPathStep[] steps) { Steps = steps; }

	public static ImmContentPath Compile(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new FormatException("Content patch Path cannot be empty.");
		}

		string normalized = path.Trim();

		if (normalized.StartsWith("/", StringComparison.Ordinal)) { normalized = normalized[1..]; }
		if (normalized.EndsWith("/", StringComparison.Ordinal)) { normalized = normalized[..^1]; }

		if (normalized.Length == 0)
		{
			throw new FormatException("Content patch Path cannot target the JSON root.");
		}

		if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.EndsWith("/", StringComparison.Ordinal) || normalized.Contains("//", StringComparison.Ordinal))
		{
			throw new FormatException("Content patch Path cannot contain empty path segments.");
		}

		string[] segments = normalized.Split('/');

		if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
		{
			throw new FormatException("Content patch Path cannot contain empty path segments.");
		}

		IPathStep[] steps = new IPathStep[segments.Length];

		for (int index = 0; index < segments.Length; index++) { steps[index] = ParseStep(segments[index]); }

		return new ImmContentPath(steps);
	}

	public IReadOnlyList<JToken> Resolve(JToken root)
	{
		IEnumerable<JToken> current = new[] { root };

		foreach (IPathStep step in Steps)
		{
			current = step.Apply(current).ToArray();

			if (!current.Any()) { break; }
		}

		return current.ToArray();
	}

	private static IPathStep ParseStep(string raw)
	{
		if (raw.StartsWith("[", StringComparison.Ordinal) && raw.EndsWith("]", StringComparison.Ordinal))
		{
			string body = raw[1..^1].Trim();

			if (body.StartsWith("\"", StringComparison.Ordinal) || body.StartsWith("'", StringComparison.Ordinal))
			{
				JToken literal = ParseSelectorValue(body);

				if (literal.Type != JTokenType.String)
				{
					throw new FormatException($"Path segment '{raw}' must contain a quoted property name.");
				}

				return new PropertyStep(UnescapeProperty(literal.Value<string>() ?? ""));
			}

			if (body == "*") { return new ArrayAllStep(); }

			if (body.StartsWith("$key=", StringComparison.Ordinal))
			{
				string pattern = UnescapeProperty(body[5..]);

				if (string.IsNullOrEmpty(pattern))
				{
					throw new FormatException($"Path segment '{raw}' has an empty object-key pattern.");
				}

				return new ObjectKeyPatternStep(pattern);
			}

			int rangeIndex = body.IndexOf("..", StringComparison.Ordinal);

			if (rangeIndex >= 0)
			{
				string startText = body[..rangeIndex];
				string endText = body[(rangeIndex + 2)..];

				if (!int.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int start) || !int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int end))
				{
					throw new FormatException($"Path segment '{raw}' has an invalid array range.");
				}

				if (start < 0 || end < 0)
				{
					throw new FormatException($"Path segment '{raw}' array ranges cannot use negative indexes.");
				}

				if (end < start)
				{
					throw new FormatException($"Path segment '{raw}' range end must not be less than its start.");
				}

				return new ArrayRangeStep(start, end);
			}

			if (int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out int arrayIndex))
			{
				if (arrayIndex < 0)
				{
					throw new FormatException($"Path segment '{raw}' array index cannot be negative.");
				}

				return new ArrayIndexStep(arrayIndex);
			}

			int equalsIndex = body.IndexOf('=');

			if (equalsIndex > 0)
			{
				string propertyName = UnescapeProperty(body[..equalsIndex].Trim());

				string expectedText = body[(equalsIndex + 1)..].Trim();

				if (string.IsNullOrWhiteSpace(propertyName) || expectedText.Length == 0)
				{
					throw new FormatException($"Path segment '{raw}' has an invalid selector.");
				}

				return new PropertyValueSelectorStep(propertyName, ParseSelectorValue(expectedText));
			}

			throw new FormatException($"Unsupported content path segment '{raw}'.");
		}

		return new PropertyStep(UnescapeProperty(raw));
	}

	private static JToken ParseSelectorValue(string text)
	{
		bool startsDouble = text.StartsWith("\"", StringComparison.Ordinal);
		bool startsSingle = text.StartsWith("'", StringComparison.Ordinal);

		if (startsDouble || startsSingle)
		{
			char quote = startsDouble ? '"' : '\'';

			if (text.Length < 2 || text[^1] != quote)
			{
				throw new FormatException($"Selector value '{text}' has an unterminated quoted string.");
			}

			try
			{
				if (startsSingle) { string inner = text[1..^1].Replace("\\'", "'", StringComparison.Ordinal); return new JValue(inner); }

				return JToken.Parse(text);
			}
			catch (Exception exception)
			{
				throw new FormatException($"Selector value '{text}' is not valid JSON.", exception);
			}
		}

		if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase) || char.IsDigit(text[0]) || text[0] is '-' or '+')
		{
			try { return JToken.Parse(text); }
			catch { } // Bare selector values deliberately fall back to strings.
		}

		return new JValue(text);
	}

	private static string UnescapeProperty(string value) { return value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal); }

	private interface IPathStep
	{
		IEnumerable<JToken> Apply(IEnumerable<JToken> input);
	}

	private sealed class PropertyStep : IPathStep
	{
		private readonly string Name;

		public PropertyStep(string name) { Name = name; }

		public IEnumerable<JToken> Apply(IEnumerable<JToken> input)
		{
			foreach (JToken token in input)
			{
				if (token is JObject obj && obj.TryGetValue(Name, StringComparison.Ordinal, out JToken? value) && value != null) { yield return value; }
			}
		}
	}

	private sealed class ArrayIndexStep : IPathStep
	{
		private readonly int Index;

		public ArrayIndexStep(int index) { Index = index; }

		public IEnumerable<JToken> Apply(IEnumerable<JToken> input)
		{
			foreach (JArray array in input.OfType<JArray>())
			{
				if (Index >= 0 && Index < array.Count) { yield return array[Index]; }
			}
		}
	}

	private sealed class ArrayAllStep : IPathStep
	{
		public IEnumerable<JToken> Apply(IEnumerable<JToken> input)
		{
			foreach (JArray array in input.OfType<JArray>()) { foreach (JToken item in array) { yield return item; } }
		}
	}

	private sealed class ArrayRangeStep : IPathStep
	{
		private readonly int Start;
		private readonly int End;

		public ArrayRangeStep(int start, int end)
		{
			Start = start;
			End = end;
		}

		public IEnumerable<JToken> Apply(IEnumerable<JToken> input)
		{
			foreach (JArray array in input.OfType<JArray>())
			{
				int start = Math.Max(0, Start);
				int end = Math.Min(End, array.Count - 1);

				for (int index = start; index <= end; index++) { yield return array[index]; }
			}
		}
	}

	private sealed class ObjectKeyPatternStep : IPathStep
	{
		private readonly string Pattern;

		public ObjectKeyPatternStep(string pattern) { Pattern = pattern; }

		public IEnumerable<JToken> Apply(IEnumerable<JToken> input)
		{
			foreach (JObject obj in input.OfType<JObject>())
			{
				foreach (JProperty property in obj.Properties())
				{
					if (WildcardUtil.Match(Pattern, property.Name)) { yield return property.Value; }
				}
			}
		}
	}

	private sealed class PropertyValueSelectorStep : IPathStep
	{
		private readonly string PropertyName;
		private readonly JToken Expected;

		public PropertyValueSelectorStep(string propertyName, JToken expected)
		{
			PropertyName = propertyName;
			Expected = expected;
		}

		public IEnumerable<JToken> Apply(IEnumerable<JToken> input)
		{
			foreach (JToken token in input)
			{
				IEnumerable<JToken> candidates = token switch { JArray array => array.Children(), JObject obj => obj.Properties().Select(property => property.Value), _ => Array.Empty<JToken>() };

				foreach (JObject candidate in candidates.OfType<JObject>())
				{
					if (candidate.TryGetValue(PropertyName, StringComparison.Ordinal, out JToken? actual) && actual != null && SelectorValuesEqual(actual, Expected)) { yield return candidate; }
				}
			}
		}

		private static bool SelectorValuesEqual(JToken left, JToken right)
		{
			if (left.Type is JTokenType.Integer or JTokenType.Float && right.Type is JTokenType.Integer or JTokenType.Float) { return left.Value<double>().Equals(right.Value<double>()); }

			return JToken.DeepEquals(left, right);
		}
	}
}
