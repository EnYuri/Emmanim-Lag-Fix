using HarmonyLib;

namespace ModsQol.Code;

/// <summary>
/// Mods QoL's own code module. It is distributed inside the Emmanim Lag Fix package because
/// that mod already carries the audited loader, but it is a separate assembly with a separate
/// Harmony id and it patches nothing Emmanim Lag Fix touches.
///
/// It is inert without Mods QoL. The only thing it reacts to is a sentinel ComponentID that
/// exists nowhere but in Mods QoL's .rules, so with Mods QoL absent (or disabled, or rolled
/// back to a version that does not use the sentinel) every patch falls through to vanilla on
/// its first branch. That is the gate - deliberately a data-driven one rather than a mod-list
/// lookup, because it also covers a Mods QoL that ships without the sentinel and needs no
/// version negotiation in either direction.
/// </summary>
public static class EntryPoint
{
    public const string HarmonyId = "znayuri.mods_qol.code";

    public static void AssemblyLoadInitializer()
    {
        var harmony = new Harmony(HarmonyId);
        harmony.PatchAll(typeof(EntryPoint).Assembly);
        Halfling.Logging.Logger.Log(
            "Mods QoL code patches initialized (sentinel prefix '"
            + AnyStorageBinder.SentinelPrefix
            + "*'; inert unless Mods QoL rules use it).");
    }
}
