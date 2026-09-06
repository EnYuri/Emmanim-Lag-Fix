using Cosmoteer;
using Cosmoteer.Game.Multiplayer;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Puts the identity of a desync into the host's log.
///
/// Only the client detects a desync. <c>MPClientManager.ValidateIntegrityHashes</c>
/// dequeues the two integrity-hash queues pairwise, and on the first mismatch it
/// logs <c>Out of sync!</c> together with both hashes and fires
/// <c>_onOutOfSyncRpc()</c>. That RPC is a bare <c>RpcActionInvoker</c> - it
/// carries no payload - so the host is told only *that* the game diverged, never
/// where. The host's log therefore records a resync with no cause, and the one
/// line that names the diverging <see cref="FixedUpdateBuckets"/> sits in the
/// other player's log file.
///
/// Asking that player to find and send a log is the step that does not happen,
/// and turning on <c>EnableDesyncDebugging</c> needs both machines and makes
/// every bucket hash at full rate. So the client reports it itself, over the
/// diagnostics chat relay that is already there.
///
/// The bucket is the whole point of the report: <c>Resources</c> or <c>Jobs</c>
/// would implicate this mod's sink-job sharding, while <c>Physics</c>,
/// <c>Statuses</c> or a <c>TickStart</c> phase would exonerate it. One report is
/// worth more than any amount of reasoning about which patch could have done it.
///
/// Nothing here touches the simulation. The mismatching pair is captured by
/// value in a postfix on <c>IntegrityHash.Equals</c> - the hashes themselves are
/// pooled and released immediately after the comparison, so a reference would
/// dangle - and it is flushed from a postfix on the validation loop, which is
/// the client's own thread and after vanilla has already acted on it.
/// </summary>
internal static class DesyncReportRelayPatch
{
    /// <summary>
    /// Number of reports relayed this session, for the smoke test.
    /// </summary>
    internal static int Reported { get; private set; }

    private static string? _pending;

    private static readonly object Gate = new();

    /// <summary>
    /// Phase names shortened so the whole report fits the game's 200-character
    /// chat limit with room to spare.
    /// </summary>
    private static string Short(IntegrityHashPhase phase) => phase switch
    {
        IntegrityHashPhase.TickStart => "TS",
        IntegrityHashPhase.FixedUpdate => "FU",
        _ => "N",
    };

    private static string Describe(IntegrityHash hash)
    {
        var bucket = hash.Phase == IntegrityHashPhase.FixedUpdate
            ? FixedUpdateBuckets.GetBucketName(hash.Bucket)
            : "-";
        return $"{hash.Tick}/{hash.InputTick} {Short(hash.Phase)} {bucket}({hash.Bucket}) h={hash.Hash}";
    }

    /// <summary>
    /// Records a mismatching pair. Every comparison the client makes runs
    /// through here, so this must stay to a reference compare in the common
    /// case; only an inequality does any work, and only the last one before the
    /// validation loop returns is kept - which is the pair vanilla logged and
    /// resynced on.
    /// </summary>
    [HarmonyPatch(typeof(IntegrityHash), nameof(IntegrityHash.Equals), new[] { typeof(IntegrityHash) })]
    internal static class Capture
    {
        private static void Postfix(IntegrityHash __instance, IntegrityHash? other, bool __result)
        {
            if (__result || other is null)
            {
                return;
            }

            var text = $"DESYNC our={Describe(__instance)} their={Describe(other)}";
            lock (Gate)
            {
                _pending = text;
            }
        }
    }

    /// <summary>
    /// Sends whatever the comparison captured. Runs on the client immediately
    /// after vanilla logged the pair and fired the out-of-sync RPC, and covers
    /// both call sites of the validation loop.
    /// </summary>
    [HarmonyPatch(typeof(MPClientManager), "ValidateIntegrityHashes")]
    internal static class Flush
    {
        /// <summary>
        /// Drops anything a comparison outside this loop happened to record, so
        /// only a mismatch the loop itself found - which is always a desync -
        /// can be reported.
        /// </summary>
        private static void Prefix()
        {
            lock (Gate)
            {
                _pending = null;
            }
        }

        private static void Postfix(MPClientManager __instance)
        {
            string? text;
            lock (Gate)
            {
                text = _pending;
                _pending = null;
            }

            if (text is null)
            {
                return;
            }

            Reported++;
            PeerDiagnosticsRelayPatch.MaybeSend(__instance, text);
        }
    }
}
