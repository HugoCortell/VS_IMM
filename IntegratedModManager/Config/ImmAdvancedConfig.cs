#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

// Advanced Types (handled separately because only 0.1% of modders make use of it)
public sealed class ImmAdvancedSchema
{
	public string Type = "";

	// Object-field metadata. Key is only meaningful for entries inside Object.Fields.
	public string Key = "";
	public string Label = "";
	public string Description = "";

	public bool Optional;
	public bool Nullable;

	// Object layout metadata.
	public int Columns = 1;
	public int ColumnSpan = 1;

	// Used whenever IMM must construct a value that is not already present.
	public JToken? InitialValue;

	// Structural metadata.
	public List<ImmAdvancedSchema> Fields = new();
	public ImmAdvancedSchema? Element;
	public ImmAdvancedSchema? Value;
	public List<ImmAdvancedSchema> Items = new();

	// Dictionary metadata.
	public string KeyLabel = "";
	public string KeyPattern = "";

	// Collection metadata.
	public int MinItems;
	public int? MaxItems;

	// Primitive metadata.
	public double? Min;
	public double? Max;
	public double? Step;
	public List<ImmConfigOption> Options = new();
}
