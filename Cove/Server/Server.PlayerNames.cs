using Cove.Server.Actor;
using Cove.Server.Plugins;
using Steamworks;

namespace Cove.Server
{
    /// <summary>
    /// Keeps the names that are saved for players in step with their Steam names.
    ///
    /// Every name the server and its plugins show comes from Steam (WFPlayer.Username reads it live). Two things used
    /// to leave an old name behind after somebody renamed themselves:
    ///   1. Steam's copy of a stranger's name, kept by the Steam client on the server PC, can be old. Steam is now
    ///      asked for a fresh copy when a player enters the lobby, and again about once a minute while they are here.
    ///   2. Names that plugins saved to disk (the Greetings player list, the Feed leaderboard, ChalkSaves) were only
    ///      rewritten when that player next did something. Now the server watches the name of every player who is
    ///      online and, when it differs from the last one seen, corrects its own list and tells every plugin
    ///      (CovePlugin.onPlayerRenamed) so each can find its entry by Steam ID and correct it.
    ///
    /// A player's first name after joining is announced too (with an empty old name), so a saved name that went stale
    /// while they were away is corrected on their return even if nothing changed while they were online.
    /// Every step is written to packetguard.log (NAMEASK, RENAMED) so a stale name can be traced.
    /// </summary>
    public partial class CoveServer
    {
        private const long NameCheckEveryMs = 3000;
        private const long NameAskSteamEveryMs = 60_000;

        private readonly object nameSync = new();
        private readonly Dictionary<ulong, string> seenNames = new();   // Steam ID -> the name last seen, online players only
        private readonly Dictionary<ulong, long> nameAskedAt = new();   // Steam ID -> when Steam was last asked for a fresh copy
        private long nextNameCheckMs;

        /// <summary>Steam answers "" or "[unknown]" while it does not know a name yet. That is never saved.</summary>
        internal static bool IsUsableSteamName(string? name)
        {
            return !string.IsNullOrWhiteSpace(name) && name != "[unknown]";
        }

        /// <summary>
        /// Asks Steam for a fresh copy of a player's name. Steam answers in the background, the name then changes by
        /// itself and CheckPlayerNames notices it.
        /// </summary>
        internal void RequestSteamNameRefresh(CSteamID id, bool logIt)
        {
            try
            {
                bool asked = SteamFriends.RequestUserInformation(id, true);
                if (logIt)
                {
                    GuardLog($"NAMEASK   {id.m_SteamID}: Steam says \"{GuardSafe(SteamFriends.GetFriendPersonaName(id), 40)}\", " +
                             (asked ? "a fresh copy was requested from Steam" : "Steam believes its copy is current"));
                }
            }
            catch (Exception e)
            {
                logger.Warning($"Could not ask Steam for a fresh name for {id.m_SteamID}: {e.Message}");
            }
        }

        /// <summary>
        /// Called from the 12 Hz update tick. Every few seconds, compares the name Steam gives for each online player
        /// with the last one seen and passes any difference on to the player list and the plugins.
        /// </summary>
        internal void CheckPlayerNames()
        {
            long now = Environment.TickCount64;
            if (now < nextNameCheckMs)
                return;
            nextNameCheckMs = now + NameCheckEveryMs;

            WFPlayer[] online;
            lock (stateLock)
                online = AllPlayers.ToArray();

            var changes = new List<(CSteamID Id, string Old, string New)>();
            var onlineIds = new HashSet<ulong>();

            lock (nameSync)
            {
                foreach (WFPlayer player in online)
                {
                    ulong id = player.SteamId.m_SteamID;
                    onlineIds.Add(id);

                    // now and then Steam is asked again, in case it is still handing out an old copy
                    if (!nameAskedAt.TryGetValue(id, out long asked) || now - asked >= NameAskSteamEveryMs)
                    {
                        nameAskedAt[id] = now;
                        RequestSteamNameRefresh(player.SteamId, false);
                    }

                    string current;
                    try
                    {
                        current = player.Username;
                    }
                    catch
                    {
                        continue;
                    }
                    if (!IsUsableSteamName(current))
                        continue; // Steam does not know it yet, try again next time

                    if (!seenNames.TryGetValue(id, out string? before))
                    {
                        seenNames[id] = current;
                        changes.Add((player.SteamId, "", current)); // first sight after joining
                    }
                    else if (before != current)
                    {
                        seenNames[id] = current;
                        changes.Add((player.SteamId, before, current));
                    }
                }

                // players who left: forget them, so their return counts as a first sight again
                foreach (ulong id in seenNames.Keys.Where(k => !onlineIds.Contains(k)).ToList())
                    seenNames.Remove(id);
                foreach (ulong id in nameAskedAt.Keys.Where(k => !onlineIds.Contains(k)).ToList())
                    nameAskedAt.Remove(id);
            }

            foreach (var change in changes)
                ApplyNameChange(change.Id, change.Old, change.New);
        }

        private void ApplyNameChange(CSteamID id, string oldName, string newName)
        {
            // the server's own list of players who were here (!prev), found by Steam ID
            lock (stateLock)
            {
                PreviousPlayer? previous = PreviousPlayers.Find(p => p.SteamId == id);
                if (previous != null)
                    previous.Username = newName;
            }

            if (oldName.Length > 0)
            {
                Log($"[{id.m_SteamID}] changed their Steam name from \"{oldName}\" to \"{newName}\".");
                GuardLog($"RENAMED   {id.m_SteamID}: \"{GuardSafe(oldName, 40)}\" -> \"{GuardSafe(newName, 40)}\"");
            }

            // every plugin that saved this player's name gets to correct its entry
            foreach (PluginInstance plugin in loadedPlugins.ToList())
                SafePluginCall(plugin, () => plugin.plugin.onPlayerRenamed(id.m_SteamID, oldName, newName));
        }
    }
}
