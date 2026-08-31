using System.Globalization;
using System.Reflection;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Opt-in instrumentation for Korean IME input. Inert unless
/// korean-ime-diagnostics.flag exists beside the mod's Code directory.
///
/// Two different corruptions have now been observed and neither was explained
/// correctly by reading the decompiled source alone, so this records the actual
/// event order instead of guessing at it. It logs three layers per keystroke:
///
///   [IME.win32] the raw window messages Imm32Manager sees, with the GCS_*
///               flags that say whether a WM_IME_COMPOSITION carries a
///               committed result string, an in-progress composition, or both
///   [IME.cb]    the four InputMethod callbacks as RawKeyboard receives them
///   [IME.char]  every character RawKeyboard finally hands the text field,
///               which is what actually appears on screen
///
/// Nothing is modified; every patch is a read-only prefix. Remove the flag
/// after the capture.
/// </summary>
internal static class KoreanImeDiagnostics
{
    internal const uint WmChar = 0x0102;
    internal const uint WmImeStartComposition = 0x010D;
    internal const uint WmImeEndComposition = 0x010E;
    internal const uint WmImeComposition = 0x010F;
    internal const uint WmImeSetContext = 0x0281;
    internal const uint WmImeNotify = 0x0282;
    internal const uint WmImeChar = 0x0286;

    internal static readonly bool Enabled = File.Exists(Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(EntryPoint).Assembly.Location)!,
        "..",
        "korean-ime-diagnostics.flag")));

    internal static readonly Type RawKeyboardType =
        AccessTools.TypeByName("Halfling.Input.Raw.RawKeyboard")
        ?? throw new TypeLoadException("Could not find Halfling.Input.Raw.RawKeyboard.");

    internal static readonly Type Imm32ManagerType =
        AccessTools.TypeByName("ImeSharp.Imm32Manager")
        ?? throw new TypeLoadException("Could not find ImeSharp.Imm32Manager.");

    internal static void Log(string message) => Logger.Log(message);

    /// <summary>Renders text so an invisible or partial jamo is still readable.</summary>
    internal static string Show(string? text)
    {
        if (text == null)
        {
            return "<null>";
        }

        if (text.Length == 0)
        {
            return "\"\" (empty)";
        }

        var codes = string.Join(
            " ",
            text.Select(c => "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)));
        return "\"" + text + "\" [" + codes + "]";
    }

    internal static string Show(char? c)
    {
        if (!c.HasValue)
        {
            return "<null> (cursor move only)";
        }

        var code = "U+" + ((int)c.Value).ToString("X4", CultureInfo.InvariantCulture);
        return c.Value switch
        {
            '\b' => "backspace " + code,
            '\n' => "newline " + code,
            '\r' => "return " + code,
            _ => "'" + c.Value + "' " + code,
        };
    }

    /// <summary>
    /// The GCS_* / CS_* bits of a WM_IME_COMPOSITION lParam. GCS_RESULTSTR is
    /// the one that matters most: it means the same message is also committing
    /// finished text, which is what happens mid-word in Korean when one
    /// syllable closes as the next one opens.
    /// </summary>
    internal static string DescribeCompositionFlags(long lParam)
    {
        var parts = new List<string>();

        void Bit(long mask, string name)
        {
            if ((lParam & mask) != 0)
            {
                parts.Add(name);
            }
        }

        Bit(0x0001, "GCS_COMPREADSTR");
        Bit(0x0002, "GCS_COMPREADATTR");
        Bit(0x0004, "GCS_COMPREADCLAUSE");
        Bit(0x0008, "GCS_COMPSTR");
        Bit(0x0010, "GCS_COMPATTR");
        Bit(0x0020, "GCS_COMPCLAUSE");
        Bit(0x0040, "GCS_CURSORPOS");
        Bit(0x0080, "GCS_DELTASTART");
        Bit(0x0100, "GCS_RESULTREADSTR");
        Bit(0x0200, "GCS_RESULTREADCLAUSE");
        Bit(0x0800, "GCS_RESULTSTR");
        Bit(0x1000, "GCS_RESULTCLAUSE");
        Bit(0x2000, "CS_INSERTCHAR");
        Bit(0x4000, "CS_NOMOVECARET");

        return parts.Count == 0 ? "none" : string.Join("|", parts);
    }
}

/// <summary>Raw window messages, before ImeSharp interprets any of them.</summary>
[HarmonyPatch]
internal static class KoreanImeWin32DiagnosticsPatch
{
    private static bool Prepare() => KoreanImeDiagnostics.Enabled;

    private static MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(KoreanImeDiagnostics.Imm32ManagerType, "ProcessMessage")
               ?? throw new MissingMethodException(
                   "ImeSharp.Imm32Manager.ProcessMessage was not found.");
    }

    private static void Prefix(uint msg, ref nint wParam, ref nint lParam)
    {
        switch (msg)
        {
            case KoreanImeDiagnostics.WmImeStartComposition:
                KoreanImeDiagnostics.Log("[IME.win32] WM_IME_STARTCOMPOSITION");
                break;
            case KoreanImeDiagnostics.WmImeComposition:
                KoreanImeDiagnostics.Log(
                    "[IME.win32] WM_IME_COMPOSITION flags=" +
                    KoreanImeDiagnostics.DescribeCompositionFlags(lParam));
                break;
            case KoreanImeDiagnostics.WmImeEndComposition:
                KoreanImeDiagnostics.Log("[IME.win32] WM_IME_ENDCOMPOSITION");
                break;
            case KoreanImeDiagnostics.WmImeChar:
                KoreanImeDiagnostics.Log(
                    "[IME.win32] WM_IME_CHAR " + KoreanImeDiagnostics.Show((char)wParam));
                break;
            case KoreanImeDiagnostics.WmChar:
                KoreanImeDiagnostics.Log(
                    "[IME.win32] WM_CHAR " + KoreanImeDiagnostics.Show((char)wParam));
                break;
            case KoreanImeDiagnostics.WmImeNotify:
                KoreanImeDiagnostics.Log("[IME.win32] WM_IME_NOTIFY wParam=" + (long)wParam);
                break;
            case KoreanImeDiagnostics.WmImeSetContext:
                KoreanImeDiagnostics.Log("[IME.win32] WM_IME_SETCONTEXT wParam=" + (long)wParam);
                break;
        }
    }
}

/// <summary>The four callbacks, as RawKeyboard receives them.</summary>
[HarmonyPatch]
internal static class KoreanImeCallbackDiagnosticsPatch
{
    private static bool Prepare() => KoreanImeDiagnostics.Enabled;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return Resolve("OnImeTextCompositionStarted");
        yield return Resolve("OnImeTextComposition");
        yield return Resolve("OnImeTextCompositionEnded");
        yield return Resolve("OnImeTextInput");
    }

    private static MethodBase Resolve(string name)
    {
        return AccessTools.DeclaredMethod(KoreanImeDiagnostics.RawKeyboardType, name)
               ?? throw new MissingMethodException("RawKeyboard." + name + " was not found.");
    }

    private static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        switch (__originalMethod.Name)
        {
            case "OnImeTextCompositionStarted":
                KoreanImeDiagnostics.Log("[IME.cb] CompositionStarted");
                break;
            case "OnImeTextComposition":
                KoreanImeDiagnostics.Log(
                    "[IME.cb] Composition text=" +
                    KoreanImeDiagnostics.Show(__args[0] as string) +
                    " cursor=" + __args[1]);
                break;
            case "OnImeTextCompositionEnded":
                KoreanImeDiagnostics.Log("[IME.cb] CompositionEnded");
                break;
            case "OnImeTextInput":
                KoreanImeDiagnostics.Log(
                    "[IME.cb] TextInput (commit) " +
                    KoreanImeDiagnostics.Show((char)__args[0]!));
                break;
        }
    }
}

/// <summary>
/// What the text field actually receives. A null character with a non-zero
/// offset is a cursor move; a backspace is RawKeyboard erasing its own live
/// preview.
/// </summary>
[HarmonyPatch]
internal static class KoreanImeTypedCharDiagnosticsPatch
{
    private static bool Prepare() => KoreanImeDiagnostics.Enabled;

    private static MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(
                   KoreanImeDiagnostics.RawKeyboardType,
                   "OnCharTyped",
                   [typeof(char?), typeof(int)])
               ?? throw new MissingMethodException(
                   "RawKeyboard.OnCharTyped(char?, int) was not found.");
    }

    private static void Prefix(char? c, int cursorOffset)
    {
        KoreanImeDiagnostics.Log(
            "[IME.char] -> field " + KoreanImeDiagnostics.Show(c) +
            (cursorOffset == 0 ? "" : " cursorOffset=" + cursorOffset));
    }
}
