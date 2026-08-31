using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Delivers the IMM32 result string, which ImeSharp drops entirely.
///
/// Captured with <see cref="KoreanImeDiagnostics"/> while typing Korean, the
/// real event order is:
///
///   WM_IME_COMPOSITION GCS_COMPSTR   -> "ㅇ"   field shows ㅇ
///   WM_IME_COMPOSITION GCS_COMPSTR   -> "아"   backspace, field shows 아
///   WM_IME_COMPOSITION GCS_COMPSTR   -> "암"   backspace, field shows 암
///   WM_IME_COMPOSITION GCS_RESULTSTR         (no callback at all)
///   WM_IME_CHAR '아'                         (ignored)
///   WM_IME_COMPOSITION GCS_COMPSTR   -> "무"   backspace, field shows 무
///
/// Two independent gaps drop the committed syllable:
///
///   Imm32Manager builds its reader as
///   `new ImmCompositionStringHandler(DefaultImc, 8)` -- 8 is GCS_COMPSTR, so
///   ImmCompositionResultHandler.Update(lParam) returns false for a
///   GCS_RESULTSTR message and no callback is raised; and
///
///   InputMethod.WndProc routes only `case 258u` (WM_CHAR) to OnTextInput, so
///   the WM_IME_CHAR that carries the same character is discarded too.
///
/// So every finished syllable is thrown away, and because the following
/// GCS_COMPSTR backspaces the previous preview before drawing the next one, the
/// caret never advances: the field keeps overwriting one character. That is the
/// reported "한 글자에만 머물러서 계속 덮어씌워진다".
///
/// The fix is a prefix on Imm32Manager.IMEComposition that reads GCS_RESULTSTR
/// itself and hands it to RawKeyboard. It runs before the original, so when a
/// single message carries both flags the commit lands ahead of the new
/// preview, which is the correct order. Nothing else is touched: GCS_COMPSTR
/// handling, candidate lists, WM_IME_CHAR and the TSF path are all left alone,
/// and WM_IME_CHAR staying ignored is what keeps the character from arriving
/// twice.
/// </summary>
[HarmonyPatch]
internal static class KoreanImeResultStringPatch
{
    private const int GcsResultStr = 0x0800;

    /// <summary>Set once if the composition state could not be reached, to avoid log spam.</summary>
    private static bool _reflectionFailureLogged;

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionStringW(nint hIMC, int dwIndex, nint lpBuf, int dwBufLen);

    private static readonly FieldInfo? DefaultImcField =
        AccessTools.Field(KoreanImeDiagnostics.Imm32ManagerType, "_defaultImc");

    private static readonly MethodInfo? OnCharTypedMethod = AccessTools.DeclaredMethod(
        KoreanImeDiagnostics.RawKeyboardType, "OnCharTyped", [typeof(char?), typeof(int)]);

    private static readonly FieldInfo? LastCompositionField =
        AccessTools.Field(KoreanImeDiagnostics.RawKeyboardType, "_lastImeComposition");

    private static readonly FieldInfo? CursorRealField =
        AccessTools.Field(KoreanImeDiagnostics.RawKeyboardType, "_imeCursorRealPosition");

    private static readonly FieldInfo? CursorStartField =
        AccessTools.Field(KoreanImeDiagnostics.RawKeyboardType, "_imeCursorStartPosition");

    /// <summary>
    /// True exactly while RawKeyboard has a live preview drawn in the field.
    /// OnImeTextCompositionStarted sets it and re-types the preview;
    /// OnImeTextCompositionEnded erases the preview and clears it.
    /// </summary>
    private static readonly FieldInfo? IsComposingField =
        AccessTools.Field(KoreanImeDiagnostics.RawKeyboardType, "_isImeComposing");

    private static readonly PropertyInfo? TextInputCallbackProperty = AccessTools.Property(
        AccessTools.TypeByName("ImeSharp.InputMethod") ?? typeof(object), "TextInputCallback");

    /// <summary>True when every member this patch reaches by reflection was found.</summary>
    internal static bool Available =>
        DefaultImcField != null && OnCharTypedMethod != null && LastCompositionField != null
        && CursorRealField != null && CursorStartField != null && IsComposingField != null
        && TextInputCallbackProperty != null;

    private static bool Prepare() => !KoreanImeInputPatch.TsfRequested && Available;

    private static MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(
                   KoreanImeDiagnostics.Imm32ManagerType, "IMEComposition", [typeof(int)])
               ?? throw new MissingMethodException(
                   "ImeSharp.Imm32Manager.IMEComposition(int) was not found.");
    }

    private static void Prefix(object __instance, int lParam)
    {
        if ((lParam & GcsResultStr) == 0)
        {
            return;
        }

        var result = ReadResultString(__instance);
        if (string.IsNullOrEmpty(result))
        {
            return;
        }

        if (KoreanImeDiagnostics.Enabled)
        {
            KoreanImeDiagnostics.Log(
                "[IME.commit] result " + KoreanImeDiagnostics.Show(result));
        }

        Commit(result);
    }

    /// <summary>
    /// Reads GCS_RESULTSTR out of the same input context ImeSharp reads
    /// GCS_COMPSTR from. The first call asks for the byte length; the string is
    /// UTF-16, so the character count is half of it.
    /// </summary>
    private static string ReadResultString(object imm32Manager)
    {
        var imc = (nint)(DefaultImcField!.GetValue(imm32Manager) ?? (nint)0);
        if (imc == 0)
        {
            return string.Empty;
        }

        var byteCount = ImmGetCompositionStringW(imc, GcsResultStr, 0, 0);
        if (byteCount <= 0)
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(byteCount);
        try
        {
            var written = ImmGetCompositionStringW(imc, GcsResultStr, buffer, byteCount);
            if (written <= 0)
            {
                return string.Empty;
            }

            var bytes = new byte[written];
            Marshal.Copy(buffer, bytes, 0, written);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Hands the committed text to every RawKeyboard currently listening for
    /// IME text, found through the callback ImeSharp already publishes rather
    /// than by tracking instances separately.
    /// </summary>
    private static void Commit(string result)
    {
        if (TextInputCallbackProperty!.GetValue(null) is not Delegate callback)
        {
            return;
        }

        foreach (var entry in callback.GetInvocationList())
        {
            if (entry.Target is { } keyboard
                && KoreanImeDiagnostics.RawKeyboardType.IsInstanceOfType(keyboard))
            {
                CommitTo(keyboard, result);
            }
        }
    }

    /// <summary>
    /// Replaces the live preview with the committed text on one RawKeyboard.
    ///
    /// This is exactly RawKeyboard.OnImeTextCompositionEnded followed by
    /// OnImeTextInput, which is what the TSF path effectively performs: move the
    /// caret to the end of the preview, erase it, type the committed characters,
    /// then forget the composition so the next GCS_COMPSTR starts from an empty
    /// preview instead of backspacing over what was just committed.
    ///
    /// The preview must only be erased if it is still on screen, and it is not
    /// always. Ending a word sends WM_IME_ENDCOMPOSITION BEFORE the
    /// GCS_RESULTSTR that carries the final syllable:
    ///
    ///   GCS_COMPSTR "이"      -> backspace, type 이      한글채팅이
    ///   WM_IME_ENDCOMPOSITION -> backspace               한글채팅
    ///   GCS_RESULTSTR "이"    -> commit
    ///
    /// OnImeTextCompositionEnded has already erased the preview there, but it
    /// does not clear _lastImeComposition, so trusting that string backspaces a
    /// second time and eats an already-committed character: "한글채이", with 팅
    /// gone. _isImeComposing is the field that actually tracks whether a preview
    /// is drawn, so it gates the erase.
    ///
    /// _isImeComposing itself is deliberately not written. The Korean IME
    /// commits a syllable in the middle of a still-open composition session, so
    /// clearing it here would tell the rest of the game the word had ended.
    /// </summary>
    private static void CommitTo(object keyboard, string result)
    {
        try
        {
            var previewIsDrawn = IsComposingField!.GetValue(keyboard) is true;
            var preview = previewIsDrawn
                ? LastCompositionField!.GetValue(keyboard) as string ?? string.Empty
                : string.Empty;
            var cursor = CursorRealField!.GetValue(keyboard) as int?;

            if (cursor.HasValue && preview.Length > cursor.Value)
            {
                Type(keyboard, null, preview.Length - cursor.Value);
            }

            for (var i = 0; i < preview.Length; i++)
            {
                Type(keyboard, '\b', 0);
            }

            foreach (var c in result)
            {
                Type(keyboard, c, 0);
            }

            LastCompositionField!.SetValue(keyboard, string.Empty);
            CursorRealField!.SetValue(keyboard, null);
            CursorStartField!.SetValue(keyboard, null);
        }
        catch (Exception e)
        {
            if (!_reflectionFailureLogged)
            {
                _reflectionFailureLogged = true;
                Logger.Log("[EmmanimLagFix] Korean IME commit failed: " + e);
            }
        }
    }

    private static void Type(object keyboard, char? c, int cursorOffset)
    {
        OnCharTypedMethod!.Invoke(keyboard, [c, cursorOffset]);
    }
}
