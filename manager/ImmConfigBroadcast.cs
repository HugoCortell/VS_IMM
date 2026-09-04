#nullable enable

namespace IntegratedModManager.Config;

public static class ImmConfigBroadcast
{
	public const string Prefix = "imm.";

	public static string GetEventName(string modId) { return Prefix + modId; }
}
