using System.Reflection;
using System.Runtime.CompilerServices;
using Halfling.Network;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Multiplayer client launch and resync receive the complete game payload into
/// a ChannelStream and then copy it into a fresh MemoryStream. Vanilla lets the
/// destination grow geometrically during that copy, repeatedly allocating and
/// copying large backing arrays. Complete-game streams are marked when their
/// exact size arrives, allowing the completed ChannelStream input array to be
/// handed directly to the fresh read buffer without copying. Any unexpected
/// stream/runtime shape falls back to the behavior-preserving preallocated copy.
/// </summary>
[HarmonyPatch(typeof(Stream), nameof(Stream.CopyTo), new[] { typeof(Stream) })]
internal static class MultiplayerStreamCopyCapacityPatch
{
    private static readonly ConditionalWeakTable<ChannelStream, object> CompleteGameStreams = new();

    private static readonly FieldInfo OutputBufferField = AccessTools.Field(typeof(ChannelStream), "_outBuf")
        ?? throw new MissingFieldException(typeof(ChannelStream).FullName, "_outBuf");

    private static readonly FieldInfo InputBufferField = AccessTools.Field(typeof(ChannelStream), "_inBuf")
        ?? throw new MissingFieldException(typeof(ChannelStream).FullName, "_inBuf");

    private static readonly FieldInfo? MemoryBufferField = AccessTools.Field(typeof(MemoryStream), "_buffer");
    private static readonly FieldInfo? MemoryPositionField = AccessTools.Field(typeof(MemoryStream), "_position");
    private static readonly FieldInfo? MemoryLengthField = AccessTools.Field(typeof(MemoryStream), "_length");
    private static readonly FieldInfo? MemoryCapacityField = AccessTools.Field(typeof(MemoryStream), "_capacity");
    private static readonly FieldInfo? MemoryExpandableField = AccessTools.Field(typeof(MemoryStream), "_expandable");
    private static readonly FieldInfo? MemoryWritableField = AccessTools.Field(typeof(MemoryStream), "_writable");
    private static readonly FieldInfo? MemoryOpenField = AccessTools.Field(typeof(MemoryStream), "_isOpen");

    private static int _loggedAdoptionFallback;

    private static bool Prefix(Stream __instance, Stream destination)
    {
        if (__instance is ChannelStream incoming
            && destination is MemoryStream receiveCopy
            && receiveCopy.Length == 0)
        {
            if (CompleteGameStreams.TryGetValue(incoming, out _)
                && TryAdoptIncomingBuffer(incoming, receiveCopy))
            {
                return false;
            }

            EnsureMemoryStreamCapacity(receiveCopy, incoming.UnreadBytes);
            return true;
        }

        if (__instance is MemoryStream serializedGame
            && destination is ChannelStream outgoing)
        {
            var remaining = serializedGame.Length - serializedGame.Position;
            var outputBuffer = GetBuffer(outgoing, OutputBufferField);
            EnsureMemoryStreamCapacity(outputBuffer, outputBuffer.Length + remaining);
        }

        return true;
    }

    internal static void PreallocateIncoming(ChannelStream stream, long totalBytes)
    {
        CompleteGameStreams.GetValue(stream, static _ => new object());
        EnsureMemoryStreamCapacity(GetBuffer(stream, InputBufferField), totalBytes);
    }

    private static bool TryAdoptIncomingBuffer(ChannelStream incoming, MemoryStream destination)
    {
        var input = GetBuffer(incoming, InputBufferField);
        if (destination.Position != 0
            || input.Position != 0
            || !input.TryGetBuffer(out var segment)
            || segment.Array is null
            || segment.Offset != 0
            || segment.Count != input.Length
            || segment.Count != incoming.UnreadBytes
            || MemoryBufferField is null
            || MemoryPositionField is null
            || MemoryLengthField is null
            || MemoryCapacityField is null
            || MemoryExpandableField is null
            || MemoryWritableField is null
            || MemoryOpenField is null)
        {
            LogAdoptionFallback();
            return false;
        }

        try
        {
            MemoryBufferField.SetValue(destination, segment.Array);
            MemoryPositionField.SetValue(destination, 0);
            MemoryLengthField.SetValue(destination, segment.Count);
            MemoryCapacityField.SetValue(destination, segment.Count);
            MemoryExpandableField.SetValue(destination, false);
            MemoryWritableField.SetValue(destination, false);
            MemoryOpenField.SetValue(destination, true);
            input.Position = input.Length;
            return true;
        }
        catch (Exception exception)
        {
            ResetEmptyDestination(destination);
            LogAdoptionFallback(exception);
            return false;
        }
    }

    private static void ResetEmptyDestination(MemoryStream destination)
    {
        try
        {
            MemoryBufferField?.SetValue(destination, Array.Empty<byte>());
            MemoryPositionField?.SetValue(destination, 0);
            MemoryLengthField?.SetValue(destination, 0);
            MemoryCapacityField?.SetValue(destination, 0);
            MemoryExpandableField?.SetValue(destination, true);
            MemoryWritableField?.SetValue(destination, true);
            MemoryOpenField?.SetValue(destination, true);
        }
        catch
        {
            // The original exception is logged below. This path only exists
            // for an unexpected runtime shape; the exact .NET 10 shape is
            // covered by the synthetic zero-copy smoke test.
        }
    }

    private static void LogAdoptionFallback(Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _loggedAdoptionFallback, 1) == 0)
        {
            Halfling.Logging.Logger.Log(
                "Emmanim Lag Fix could not adopt the multiplayer initialization receive buffer; " +
                "the safe preallocated copy path was preserved." +
                (exception is null ? string.Empty : $" {exception.GetType().Name}: {exception.Message}"));
        }
    }

    private static MemoryStream GetBuffer(ChannelStream stream, FieldInfo field) =>
        field.GetValue(stream) as MemoryStream
        ?? throw new InvalidOperationException($"ChannelStream has no {field.Name} MemoryStream.");

    private static void EnsureMemoryStreamCapacity(MemoryStream stream, long requiredCapacity)
    {
        if (requiredCapacity > stream.Capacity && requiredCapacity <= int.MaxValue)
        {
            stream.Capacity = (int)requiredCapacity;
        }
    }
}

/// <summary>
/// Both first launch and resync receive the exact complete-game payload size
/// before constructing the client's ChannelStream. Reserve that size in the
/// stream's input buffer before the reliable chunks arrive, avoiding repeated
/// backing-array growth while preserving the unchanged receive path.
/// </summary>
[HarmonyPatch]
internal static class MultiplayerReceiveBufferCapacityPatch
{
    private static readonly Type ClientLaunchFlowType = typeof(Cosmoteer.Game.GameRoot).Assembly
        .GetType("Cosmoteer.Gui.Multiplayer.GameLaunchFlow+ClientLaunchFlow", throwOnError: true)!;

    private static readonly Type ClientResyncFlowType = typeof(Cosmoteer.Game.GameRoot).Assembly
        .GetType("Cosmoteer.Game.Multiplayer.GameResyncFlow+ClientResyncFlow", throwOnError: true)!;

    private static readonly IReadOnlyDictionary<Type, FieldInfo> StreamFields =
        new Dictionary<Type, FieldInfo>
        {
            [ClientLaunchFlowType] = AccessTools.Field(ClientLaunchFlowType, "_stream")
                ?? throw new MissingFieldException(ClientLaunchFlowType.FullName, "_stream"),
            [ClientResyncFlowType] = AccessTools.Field(ClientResyncFlowType, "_stream")
                ?? throw new MissingFieldException(ClientResyncFlowType.FullName, "_stream")
        };

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in StreamFields.Keys)
        {
            yield return AccessTools.Method(type, "StartDataStreamRpc", new[] { typeof(long) })
                ?? throw new MissingMethodException(type.FullName, "StartDataStreamRpc(long)");
        }
    }

    private static void Postfix(object __instance, long totalBytes)
    {
        if (StreamFields[__instance.GetType()].GetValue(__instance) is ChannelStream stream)
        {
            MultiplayerStreamCopyCapacityPatch.PreallocateIncoming(stream, totalBytes);
            if (__instance.GetType() == ClientResyncFlowType)
            {
                Halfling.Logging.Logger.Log(
                    $"Emmanim Lag Fix preallocated the resync receive buffer for {totalBytes:N0} bytes.");
            }
        }
    }
}
