using System.Runtime.CompilerServices;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Multiplayer;

namespace EmmanimLagFix.Code;

/// <summary>
/// Sends the client's own diagnostics line to the host once a minute so the
/// host's log holds both sides of the session.
///
/// The host's line already names which player is holding the lockstep readiness
/// gate - across two sessions that fraction predicted the host's tick rate with
/// r = -0.97, while ships, parts, memory and GC counts did not agree even on
/// sign. What it cannot say is why that player is late, and only that player's
/// own line can. Collecting it by asking the other player to find and send a log
/// file is the kind of step that does not happen, so the data has to arrive on
/// its own.
///
/// It travels as a chat message, which is a separate reliable channel
/// (<c>ChannelChatProvider</c> opens channel 3) and not the lockstep input path,
/// so no packet format changes and nothing about the simulation is touched. The
/// marker prefix is stripped from the chat window on every peer, so players
/// never see it. Both peers necessarily run this build:
/// <c>Cosmoteer.Multiplayer.ModData.Equals</c> compares mod ID *and* version, so
/// a peer on a different version cannot join at all.
///
/// <c>TextUtils.SanitizeChatText</c> truncates to 200 characters on receipt, so
/// the payload is built to fit well inside that and is truncated before sending
/// rather than being silently cut in the middle by the game.
/// </summary>
internal static class PeerDiagnosticsRelayPatch
{
    /// <summary>
    /// Plain ASCII so it survives <c>RemoveInvalidChars</c>, and distinctive
    /// enough that a player typing it by accident is not a concern.
    /// </summary>
    private const string Marker = "#ELFDIAG#";

    /// <summary>
    /// 200 is the game's own limit; leave room so the marker and payload are
    /// never cut mid-field.
    /// </summary>
    private const int MaxTextLength = 195;

    /// <summary>
    /// A relayed line reaches every ChatBox subscribed to the provider, and the
    /// sender gets its own line back because the host echoes to all peers. Log
    /// each distinct message once.
    /// </summary>
    private static readonly ConditionalWeakTable<ChatMessage, object> Handled = new();
    private static readonly object HandledMarker = new();

    /// <summary>
    /// Number of relayed lines this session, for the smoke test and for telling
    /// "the peer is silent" apart from "the relay never ran".
    /// </summary>
    internal static int Sent { get; private set; }

    internal static int Received { get; private set; }

    /// <summary>
    /// Only the client sends: the host is the one collecting, and its own line
    /// is already in its log.
    /// </summary>
    internal static void MaybeSend(BaseMPManager manager, string compactLine)
    {
        if (manager is not MPClientManager)
        {
            return;
        }

        try
        {
            var provider = manager.ChatBox?._chatProvider;
            if (provider is null || !provider.CanSendChat)
            {
                return;
            }

            var text = Marker + compactLine;
            if (text.Length > MaxTextLength)
            {
                text = text[..MaxTextLength];
            }

            // Team null keeps it on the global channel, which every peer's
            // ChatBox receives regardless of team assignment.
            provider.SendChat(new ChatMessage(LocalPlayerName(manager), text));
            Sent++;
        }
        catch (Exception e)
        {
            // Diagnostics must never be able to break a session. One line, then
            // stop trying to explain it every minute.
            Log($"[EmmanimLagFix.PeerDiagnostics] relay send failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static string LocalPlayerName(BaseMPManager manager)
    {
        var localID = manager.LocalPlayerID;
        foreach (var player in manager._playerInfos.Values)
        {
            if (player.PlayerID == localID)
            {
                return player.Name;
            }
        }

        return "client";
    }

    /// <summary>
    /// Called from the chat receive hook. Returns whether the message was a
    /// relayed diagnostics line, in which case the caller must not display or
    /// log it as chat.
    /// </summary>
    internal static bool TryHandleIncoming(ChatMessage msg)
    {
        if (msg.Text is null || !msg.Text.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        lock (Handled)
        {
            if (Handled.TryGetValue(msg, out _))
            {
                return true;
            }

            Handled.Add(msg, HandledMarker);
        }

        Received++;
        var name = msg.PlayerName.Replace('\r', ' ').Replace('\n', ' ');
        Log($"[EmmanimLagFix.PeerDiagnostics] from={name} {msg.Text[Marker.Length..]}");
        return true;
    }

    /// <summary>
    /// This runs inside the chat receive hook, so a logging failure would take
    /// chat down with it. Losing a diagnostics line is the lesser outcome, and
    /// it also lets the relay be exercised outside a running game, where the
    /// game's logger is not initialized.
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            Halfling.Logging.Logger.Log(message);
        }
        catch
        {
            // Nothing safe to report it to.
        }
    }
}
