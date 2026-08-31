using System.Reflection;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Selects ImeSharp's IMM32 backend instead of its TSF text store.
///
/// Cosmoteer receives both composition updates and text-input callbacks through
/// the TSF path on the Microsoft Korean IME. Intermediate syllable states such
/// as ㅇ, 아, and 안 were consequently inserted as separate committed text.
/// ImeSharp exposes tsfForceDisabled specifically to select its IMM32 fallback.
///
/// IMM32 is not yet proven correct either: it produced a different corruption,
/// where a syllable never commits until Enter is pressed. Because the two
/// backends fail differently, korean-ime-tsf.flag beside the mod's Code
/// directory restores vanilla's TSF path, so both can be captured with
/// <see cref="KoreanImeDiagnostics"/> without rebuilding the assembly.
/// </summary>
internal static class KoreanImeInputPatch
{
    private const string InputMethodTypeName = "ImeSharp.InputMethod";

    /// <summary>When present, leave ImeSharp on its default TSF text store.</summary>
    internal static readonly bool TsfRequested = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "korean-ime-tsf.flag")));

    private static readonly Type InputMethodType =
        AccessTools.TypeByName(InputMethodTypeName)
        ?? throw new TypeLoadException($"Could not find {InputMethodTypeName}.");

    private static readonly FieldInfo TsfForceDisabledField =
        AccessTools.Field(InputMethodType, "_tsfForceDisabled")
        ?? throw new MissingFieldException(InputMethodTypeName, "_tsfForceDisabled");

    internal static string BackendName => TsfRequested ? "TSF (vanilla)" : "IMM32";

    internal static void ForceImm32Backend()
    {
        if (TsfRequested)
        {
            return;
        }

        // Set this immediately in case InputMethod.Initialize already ran. TSF's
        // document manager is created lazily when an edit field first enables IME.
        TsfForceDisabledField.SetValue(null, true);
    }

    internal static Type GetInputMethodType() => InputMethodType;
}

[HarmonyPatch]
internal static class KoreanImeInitializationPatch
{
    private static bool Prepare() => !KoreanImeInputPatch.TsfRequested;

    private static MethodBase TargetMethod()
    {
        // RawKeyboard calls the one-argument form, so the two trailing defaults
        // are supplied at the call site and this prefix still sees them.
        return AccessTools.DeclaredMethod(
                KoreanImeInputPatch.GetInputMethodType(),
                "Initialize",
                [typeof(nint), typeof(bool), typeof(bool)])
            ?? throw new MissingMethodException(
                "ImeSharp.InputMethod.Initialize(nint, bool, bool) was not found.");
    }

    private static void Prefix(ref bool tsfForceDisabled)
    {
        tsfForceDisabled = true;
    }
}
