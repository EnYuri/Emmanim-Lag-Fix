using System.Runtime.CompilerServices;
using Cosmoteer;
using Cosmoteer.Gui.Multiplayer;
using Cosmoteer.Multiplayer;
using HarmonyLib;

namespace EmmanimLagFix.Code;

/// <summary>
/// Writes each visible player chat message to Cosmoteer's normal game log.
/// The game can have more than one ChatBox subscribed to the same provider, so
/// messages are deduplicated by their received ChatMessage object identity.
/// </summary>
[HarmonyPatch(typeof(ChatBox), "OnChatReceived")]
internal static class ChatLogPatch
{
    private static readonly ConditionalWeakTable<ChatMessage, object> LoggedMessages = new();
    private static readonly object LoggedMarker = new();

    private static void Prefix(ChatMessage msg, Func<int>? ____getTeam)
    {
        if (!IsVisibleToThisChatBox(msg, ____getTeam) || !TryMarkLogged(msg))
        {
            return;
        }

        string channel = msg.Team.HasValue ? "Team" : "Global";
        string playerName = MakeSingleLine(msg.PlayerName);
        string text = MakeSingleLine(msg.Text);
        Halfling.Logging.Logger.Log($"[Chat][{channel}] {playerName}: {text}");
    }

    private static bool IsVisibleToThisChatBox(ChatMessage msg, Func<int>? getTeam)
    {
        bool senderIsVisible = msg.UniquePlayerKey == null
            || (!Settings.MutedPlayers.Contains(msg.UniquePlayerKey)
                && !GlobalBanMuteListManager.IsPlayerMuted(msg.UniquePlayerKey));
        bool channelIsVisible = !msg.Team.HasValue
            || (getTeam != null && getTeam() == msg.Team.Value);

        return senderIsVisible && channelIsVisible && msg.Text.Length > 0;
    }

    private static bool TryMarkLogged(ChatMessage msg)
    {
        lock (LoggedMessages)
        {
            if (LoggedMessages.TryGetValue(msg, out _))
            {
                return false;
            }

            LoggedMessages.Add(msg, LoggedMarker);
            return true;
        }
    }

    private static string MakeSingleLine(string text)
    {
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
