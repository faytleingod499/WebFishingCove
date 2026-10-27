using System.Text;
using Cove.Server.Actor;
using Steamworks;

namespace Cove.Server
{
    /// <summary>
    /// The Packet Guard log: one plain text file, "packetguard.log", in the server's folder next to bans.txt.
    /// It holds only security-relevant lines (bans of every kind, joins and leaves with Steam IDs, odd packets, and a
    /// summary of what each player sent), so it can be read directly instead of searching the normal logs.
    /// The file is rotated at 5 MB (the old one is kept as packetguard.old.log). Writing never throws.
    /// </summary>
    public partial class CoveServer
    {
        private const long GuardLogMaxBytes = 5L * 1024 * 1024;
        private static readonly object GuardLogLock = new();
        private static readonly string GuardLogPath = $"{AppDomain.CurrentDomain.BaseDirectory}packetguard.log";
        private static readonly string GuardLogOldPath = $"{AppDomain.CurrentDomain.BaseDirectory}packetguard.old.log";

        // what each player has sent this visit: packet type -> count (written out when they leave or are banned)
        private readonly Dictionary<ulong, Dictionary<string, int>> guardPacketTally = new();
        // one line per (player, kind) every 10 seconds, so a flood can't flood this file too
        private readonly Dictionary<(ulong, string), long> guardNoteTimes = new();

        /// <summary>Appends one timestamped line to packetguard.log.</summary>
        internal void GuardLog(string text)
        {
            try
            {
                lock (GuardLogLock)
                {
                    FileInfo info = new(GuardLogPath);
                    if (info.Exists && info.Length > GuardLogMaxBytes)
                    {
                        File.Copy(GuardLogPath, GuardLogOldPath, true);
                        File.Delete(GuardLogPath);
                    }
                    File.AppendAllText(GuardLogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch
            {
                // a log that can't be written must never stop the server
            }
        }

        /// <summary>Like GuardLog, but at most one line per player and kind every 10 seconds (it says how many were skipped).</summary>
        internal void GuardNote(ulong id, string kind, string text)
        {
            long now = Environment.TickCount64;
            lock (guardNoteTimes)
            {
                if (guardNoteTimes.TryGetValue((id, kind), out long last) && now - last < 10_000)
                    return;
                guardNoteTimes[(id, kind)] = now;
                if (guardNoteTimes.Count > 5000)
                    guardNoteTimes.Clear();
            }
            GuardLog(text);
        }

        /// <summary>Counts one packet of the given type from a player.</summary>
        internal void GuardCount(ulong id, string type)
        {
            lock (guardPacketTally)
            {
                if (!guardPacketTally.TryGetValue(id, out Dictionary<string, int>? tally))
                {
                    if (guardPacketTally.Count > 2000)
                        guardPacketTally.Clear(); // strangers that never joined can't make this grow forever
                    guardPacketTally[id] = tally = new Dictionary<string, int>();
                }
                if (tally.Count < 64 || tally.ContainsKey(type))
                    tally[type] = tally.GetValueOrDefault(type) + 1;
            }
        }

        /// <summary>Writes (and forgets) what a player sent, for example "actor_update=4021 actor_action=88".</summary>
        internal void GuardWriteTally(ulong id, string why)
        {
            string line;
            lock (guardPacketTally)
            {
                if (!guardPacketTally.Remove(id, out Dictionary<string, int>? tally) || tally.Count == 0)
                    return;
                line = string.Join(" ", tally.OrderByDescending(t => t.Value).Select(t => $"{GuardSafe(t.Key)}={t.Value}"));
            }
            GuardLog($"SENT      {id} ({why}): {line}");
        }

        /// <summary>The best name known for a Steam ID (online now, seen earlier, or "unknown").</summary>
        internal string GuardNameOf(CSteamID id)
        {
            try
            {
                WFPlayer? now = AllPlayers.Find(p => p.SteamId == id);
                if (now != null)
                    return now.Username;
                string? before = PreviousPlayers.Find(p => p.SteamId == id)?.Username;
                if (!string.IsNullOrEmpty(before))
                    return before;
                string steam = SteamFriends.GetFriendPersonaName(id);
                return string.IsNullOrEmpty(steam) ? "unknown" : steam;
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>Text from a packet or a player name made safe to put on one line of the log.</summary>
        internal static string GuardSafe(string? text, int max = 80)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            StringBuilder sb = new();
            foreach (char c in text)
            {
                if (sb.Length >= max)
                {
                    sb.Append("...");
                    break;
                }
                sb.Append(char.IsControl(c) ? '?' : c);
            }
            return sb.ToString();
        }
    }
}
