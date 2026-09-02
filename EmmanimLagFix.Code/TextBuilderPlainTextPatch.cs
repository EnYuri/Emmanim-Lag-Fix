using System.Reflection;
using System.Reflection.Emit;
using Halfling.Graphics.Text;
using Halfling.Logging;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Stops building a full XML reader for UI text that contains no markup.
///
/// <c>TextBuilder.BuildLines</c> is:
///
/// <code>
/// if (!xmlFormatting)
/// {
///     AddTextToLines(list, Text, state, maxWidth, ref prevChar);
/// }
/// else
/// {
///     using XmlReader reader = XmlReader.Create(new StringReader(Text), XML_READER_SETTINGS);
///     ...
///     ParseXmlToLines(list, reader, stack, maxWidth);
/// }
/// </code>
///
/// Almost every widget in the game sets <c>XmlFormatting</c>, so almost every
/// text refresh takes the second branch, and <c>XmlReader.Create</c> builds an
/// <c>XmlTextReaderImpl</c> with its own character and node buffers. A
/// fifteen-second allocation trace on a two-player session attributed 28.6%
/// (199 MiB) of all process allocation to <c>WidgetTextRenderer.OnRefresh</c>,
/// of which <c>XmlTextReaderImpl.FinishInitTextReader</c> was 12.2% (85 MiB)
/// and the reader constructor a further 2.1% (15 MiB). Text refreshes are
/// already gated behind <c>_needsRefresh</c>; there are simply a great many of
/// them, because counters, resource totals and tooltips change constantly.
///
/// For a string with no markup the two branches produce the same lines. The
/// XML branch's loop body reduces to one
/// <c>AddTextToLines(lines, reader.Value, stateStack.Peek(), maxWidth, ref prevChar)</c>
/// — the stack holds exactly the state the plain branch passes, <c>prevChar</c>
/// starts null in both, and nothing runs after the loop. So this replaces only
/// the branch condition: plain text takes vanilla's own plain-text path
/// instead of a parser that would hand back the identical string.
///
/// <see cref="IsPlainText"/> is deliberately narrow, because falling back is
/// free and being wrong is not:
/// <list type="bullet">
/// <item><c>&lt;</c> or <c>&amp;</c> — real markup or an entity to expand.</item>
/// <item><c>\r</c> — XML normalises line endings inside text nodes, so the
/// plain branch would see a carriage return the XML branch never delivers.</item>
/// <item>Characters illegal in XML 1.0, and surrogates. Illegal ones make
/// vanilla throw and fall back to the plain branch anyway, so routing them
/// there directly is the same outcome, but keeping the test conservative
/// leaves that reasoning out of the hot path.</item>
/// <item>Long strings, because <c>XmlTextReaderImpl</c> may split text across
/// buffer boundaries into several nodes. UI labels are far below this.</item>
/// </list>
///
/// Nothing else changes: no caching, no reuse across frames, no change to
/// wrapping, ellipsing, fonts or geometry. If the IL shape does not match, the
/// original instructions are returned and vanilla behaviour stands.
/// <see cref="Applied"/> is set only when the rewrite actually happened, which
/// the smoke test asserts, because a transpiler that fell back to vanilla is
/// still installed.
/// </summary>
[HarmonyPatch]
internal static class TextBuilderPlainTextPatch
{
    /// <summary>
    /// Longest string routed to the plain-text path. Short enough that
    /// XmlTextReaderImpl would have returned it as a single text node.
    /// </summary>
    private const int MaxPlainTextLength = 1024;

    /// <summary>True only when the branch condition was really rewritten.</summary>
    internal static bool Applied;

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(TextBuilder), "BuildLines")
        ?? throw new MissingMethodException(typeof(TextBuilder).FullName, "BuildLines");

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var codes = new List<CodeInstruction>(instructions);
        var parameters = original.GetParameters();
        if (original.IsStatic
            || parameters.Length != 1
            || parameters[0].ParameterType != typeof(bool))
        {
            Logger.Log(
                "[EmmanimLagFix] TextBuilder.BuildLines has an unexpected signature; "
                + "leaving plain text on the XML path.");
            return codes;
        }

        // xmlFormatting is read exactly once, to pick the branch.
        var reads = codes
            .Select((code, index) => (code, index))
            .Where(entry => entry.code.opcode == OpCodes.Ldarg_1)
            .ToArray();
        if (reads.Length != 1
            || reads[0].index + 1 >= codes.Count
            || codes[reads[0].index + 1].opcode != OpCodes.Brtrue
                && codes[reads[0].index + 1].opcode != OpCodes.Brtrue_S)
        {
            Logger.Log(
                "[EmmanimLagFix] TextBuilder.BuildLines reads its xmlFormatting argument "
                + reads.Length + " time(s) and not as a single branch test; "
                + "leaving plain text on the XML path.");
            return codes;
        }

        // Replace `xmlFormatting` with `xmlFormatting && !IsPlainText(this.Text)`.
        codes.Insert(reads[0].index + 1, new CodeInstruction(OpCodes.Ldarg_0));
        codes.Insert(
            reads[0].index + 2,
            new CodeInstruction(
                OpCodes.Call,
                AccessTools.DeclaredMethod(typeof(TextBuilderPlainTextPatch), nameof(NeedsXmlParse))));
        Applied = true;
        return codes;
    }

    private static bool NeedsXmlParse(bool xmlFormatting, TextBuilder builder) =>
        xmlFormatting && !IsPlainText(builder.Text);

    /// <summary>
    /// Whether the XML reader would hand <c>ParseXmlToLines</c> one text node
    /// holding this exact string, so that the plain-text branch produces the
    /// same lines. Conservative: a false answer only costs vanilla's own work.
    /// </summary>
    private static bool IsPlainText(string? text)
    {
        if (text == null)
        {
            return false;
        }

        if (text.Length > MaxPlainTextLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            switch (c)
            {
                case '<':
                case '&':
                case '\r':
                    return false;
                case '\t':
                case '\n':
                    continue;
            }

            if (c < ' ' || char.IsSurrogate(c) || c == '￾' || c == '￿')
            {
                return false;
            }
        }

        return true;
    }
}
