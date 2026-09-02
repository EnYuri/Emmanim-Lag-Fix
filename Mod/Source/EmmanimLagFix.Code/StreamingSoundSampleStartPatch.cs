using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Halfling's XAudio2 streaming path can hand <c>XA2StreamingSound.ReadSamples</c>
/// a negative start sample and crash the whole game.
///
/// <c>XA2StreamingSoundInstance.UpdateBuffers</c> computes
/// <c>num5 = (int)(totalSubmittedSamples - samplesPlayed)</c> from a
/// <c>_totalSubmittedSamples</c> snapshot taken before the release loop and a
/// <c>samplesPlayed</c> value read from the live voice, then derives
/// <c>sampleStart = (num2 + num5) % TotalSamples</c>. When the audio updater
/// thread is starved long enough for the voice to play past everything that was
/// submitted, <c>num5</c> goes negative, <c>sampleStart</c> follows, and
/// <c>ReadSamples</c> throws <see cref="ArgumentOutOfRangeException"/> on its own
/// unhandled audio thread. That is a hard process crash, not a dropped sound —
/// observed on a starved 4-core client after 2h43m of multiplayer, which then
/// deadlocked in <c>XA2AudioManager.Dispose</c>'s <c>Thread.Join</c> during
/// shutdown and only left the multiplayer session when the host's ack timeout
/// expired.
///
/// This prefix maps an out-of-range start back into the sound with the
/// wrap-around the caller already intended (<c>%</c> keeps the sign of its
/// dividend in C#, which is the entire bug). In range, it changes nothing;
/// out of range, one buffer's worth of audio is read from the wrapped position
/// instead of terminating the process. It touches no simulation, network or
/// lockstep state.
/// </summary>
[HarmonyPatch]
internal static class StreamingSoundSampleStartPatch
{
    /// <summary>Set only once every shape guard passed.</summary>
    internal static bool Applied;

    /// <summary>Number of out-of-range starts corrected so far.</summary>
    internal static long Corrections;

    private static Type? _soundType;
    private static MethodBase? _readSamples;
    private static Func<object, long>? _totalSamples;

    private static bool Prepare()
    {
        if (Applied)
        {
            return true;
        }

        _soundType = AccessTools.TypeByName("Halfling.Audio.XA2.XA2StreamingSound");
        if (_soundType == null)
        {
            Halfling.Logging.Logger.Log(
                "Emmanim Lag Fix: XA2StreamingSound was not found; "
                + "streaming-sound start clamping left at vanilla behaviour.");
            return false;
        }

        _readSamples = AccessTools.Method(_soundType, "ReadSamples");
        var parameters = _readSamples?.GetParameters();
        if (_readSamples == null
            || _readSamples.IsStatic
            || parameters is not { Length: 3 }
            || parameters[1].ParameterType != typeof(long)
            || parameters[1].Name != "sampleStart"
            || parameters[2].ParameterType != typeof(int)
            || (_readSamples as MethodInfo)?.ReturnType != typeof(int))
        {
            Halfling.Logging.Logger.Log(
                "Emmanim Lag Fix: XA2StreamingSound.ReadSamples did not have the expected "
                + "shape; streaming-sound start clamping left at vanilla behaviour.");
            return false;
        }

        _totalSamples = BuildTotalSamplesGetter(_soundType);
        if (_totalSamples == null)
        {
            Halfling.Logging.Logger.Log(
                "Emmanim Lag Fix: XA2StreamingSound.TotalSamples was not readable; "
                + "streaming-sound start clamping left at vanilla behaviour.");
            return false;
        }

        Applied = true;
        return true;
    }

    private static Func<object, long>? BuildTotalSamplesGetter(Type soundType)
    {
        var getter = AccessTools.PropertyGetter(soundType, "TotalSamples");
        if (getter == null || getter.ReturnType != typeof(long) || getter.GetParameters().Length != 0)
        {
            return null;
        }

        var method = new DynamicMethod(
            "EmmanimLagFix_XA2TotalSamples",
            typeof(long),
            [typeof(object)],
            soundType.Module,
            skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, soundType);
        il.Emit(OpCodes.Callvirt, getter);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<Func<object, long>>();
    }

    /// <summary>
    /// Maps a start sample back into <paramref name="total"/> using the
    /// wrap-around the caller intended. In-range values, including the
    /// end-of-sound <paramref name="total"/> that vanilla accepts, are returned
    /// unchanged.
    /// </summary>
    internal static long InRange(long sampleStart, long total)
    {
        if (sampleStart >= 0 && sampleStart <= total)
        {
            return sampleStart;
        }

        return total > 0 ? ((sampleStart % total) + total) % total : 0;
    }

    private static MethodBase TargetMethod() =>
        _readSamples ?? throw new MissingMethodException("Halfling.Audio.XA2.XA2StreamingSound", "ReadSamples");

    private static void Prefix(object __instance, ref long sampleStart)
    {
        var total = _totalSamples!(__instance);
        var original = sampleStart;
        sampleStart = InRange(original, total);
        if (sampleStart == original)
        {
            return;
        }

        if (Interlocked.Increment(ref Corrections) == 1)
        {
            Halfling.Logging.Logger.Log(
                "Emmanim Lag Fix: corrected an out-of-range streaming-sound start sample ("
                + original.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " of "
                + total.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "). Vanilla would have crashed the game here; further corrections are silent.");
        }
    }
}
