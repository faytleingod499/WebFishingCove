using System.Text.RegularExpressions;
using Cove.Server.Actor;
using Cove.Server.Utils;
using Steamworks;

namespace Cove.Server
{
    /// <summary>
    /// Packet Guard: bans players who send the packets griefers use to attack other players.
    ///
    /// Players' games send most packets straight to each other, not through the server, so the server can't filter what
    /// reaches the other players. But the attacks are broadcast to everyone, the server included, so the server sees
    /// them, and a ban makes every player's game drop the attacker ("peer_was_banned").
    ///
    /// What is caught (all things the real game never sends):
    ///  - "_play_sfx" with a sound name that is a node path ("/root/...", "../.."). The game looks the name up as a node
    ///    and moves it, which makes the map vanish or the screen go white, and it logs every attempt.
    ///  - very long text in an actor action. The game prints received sounds (and dumps whole action queues) into
    ///    godot.log, so huge text fills the disk; a full disk then wipes the save the game is writing.
    ///  - lots of actions aimed at actors that don't exist: they pile up in every player's game and, past 128, the game
    ///    prints the whole pile into godot.log for every new packet.
    ///  - a flood of actions, or a packet that decompresses to more than 8 MB (a decompression bomb, which crashes the
    ///    players whose games try to unpack it).
    /// </summary>
    public partial class CoveServer
    {
        /// <summary>Raised with a short text whenever Packet Guard bans someone (the Discord Bridge posts it).</summary>
        public event Action<string>? SecurityAlert;

        private const int GuardMaxText = 4096;          // longest text allowed anywhere in an actor action
        private const int GuardMaxActionsPerSecond = 300;
        private const int GuardMaxUnknownPerMinute = 100;
        private const int GuardMaxDepth = 16;

        // a sound is a node name in the player's sound_manager: letters, numbers, _ and -, never a path
        private static readonly Regex SafeSoundName = new(@"^[A-Za-z0-9_\-]{1,48}$", RegexOptions.Compiled);

        private sealed class GuardStats
        {
            public long SecondStart;
            public int ActionsThisSecond;
            public long MinuteStart;
            public int UnknownThisMinute;
        }

        private readonly Dictionary<ulong, GuardStats> guardStats = new();
        private readonly HashSet<ulong> guardBanned = new();

        /// <summary>Called for a packet that failed to decompress; true when it was an attack and the sender is banned.</summary>
        internal bool GuardBadPayload(CSteamID sender, Exception e)
        {
            if (e is InvalidDataException && e.Message.Contains("exceeds"))
            {
                GuardBan(sender, $"a decompression bomb (a packet that unpacks to more than {GzipHelper.MaxDecompressedSize / (1024 * 1024)} MB)");
                return true;
            }
            return false;
        }

        /// <summary>Checks an actor_action packet; true when it was an attack and the sender is banned.</summary>
        internal bool GuardActorAction(CSteamID sender, Dictionary<string, object> packet)
        {
            ulong id = sender.m_SteamID;
            TryGetString(packet, "action", out string action);
            packet.TryGetValue("params", out object? rawParams);

            // a sound that is really a node path
            if (action == "_play_sfx" && rawParams is Dictionary<int, object> sfx && sfx.TryGetValue(0, out object? sound)
                && sound is string name && !SafeSoundName.IsMatch(name))
            {
                GuardBan(sender, $"the sound exploit (_play_sfx with \"{Shorten(name)}\", a node path that moves things in other players' games)");
                return true;
            }

            // huge text anywhere in the action
            int longest = LongestText(rawParams, 0);
            if (longest > GuardMaxText || (action?.Length ?? 0) > 256)
            {
                GuardBan(sender, $"the log flood ({(action ?? "?")} with {Math.Max(longest, action?.Length ?? 0)} characters of text, which fills other players' disks)");
                return true;
            }

            if (isPlayerAdmin(sender))
                return false; // admins can run things that send a lot, the checks above still apply to them

            long now = Environment.TickCount64;
            if (!guardStats.TryGetValue(id, out GuardStats? stats))
                guardStats[id] = stats = new GuardStats { SecondStart = now, MinuteStart = now };

            if (now - stats.SecondStart >= 1000)
            {
                stats.SecondStart = now;
                stats.ActionsThisSecond = 0;
            }
            if (++stats.ActionsThisSecond > GuardMaxActionsPerSecond)
            {
                GuardBan(sender, $"an action flood (more than {GuardMaxActionsPerSecond} actor actions in one second)");
                return true;
            }

            // actions for actors nobody has: they pile up in every player's game
            if (TryGetLong(packet, "actor_id", out long actorId) && !allActors.Any(a => a.InstanceID == actorId)
                && !serverOwnedInstances.Any(a => a.InstanceID == actorId))
            {
                if (now - stats.MinuteStart >= 60_000)
                {
                    stats.MinuteStart = now;
                    stats.UnknownThisMinute = 0;
                }
                if (++stats.UnknownThisMinute > GuardMaxUnknownPerMinute)
                {
                    GuardBan(sender, $"the action queue flood (more than {GuardMaxUnknownPerMinute} actions a minute for actors that don't exist, which makes other players' games dump them into their logs)");
                    return true;
                }
            }
            return false;
        }

        private void GuardBan(CSteamID sender, string reason)
        {
            ulong id = sender.m_SteamID;
            if (!guardBanned.Add(id))
                return;
            guardStats.Remove(id);

            WFPlayer? player = AllPlayers.Find(p => p.SteamId.m_SteamID == id);
            string who = player != null ? $"{player.Username} ({id})" : id.ToString();
            string text = $"Packet Guard banned {who} for {reason}.";

            logger.Warning(text);
            CrashWatch.Note("guard", text);

            try
            {
                banPlayer(sender, true, "Packet Guard: " + reason);
            }
            catch (Exception e)
            {
                logger.Error("Packet Guard could not ban: " + e);
            }

            // the admins who are online hear about it in game
            foreach (WFPlayer admin in AllPlayers.ToList())
            {
                if (admin.SteamId.m_SteamID != id && isPlayerAdmin(admin.SteamId))
                    messagePlayer(text, admin.SteamId, "ff8080");
            }

            try
            {
                SecurityAlert?.Invoke(text);
            }
            catch (Exception e)
            {
                logger.Error("A Packet Guard alert handler failed: " + e.Message);
            }
        }

        // the longest string anywhere inside a value, looking into dictionaries and arrays
        private static int LongestText(object? value, int depth)
        {
            if (value == null || depth > GuardMaxDepth)
                return 0;
            if (value is string s)
                return s.Length;
            int longest = 0;
            if (value is Dictionary<int, object> list)
            {
                foreach (object item in list.Values)
                    longest = Math.Max(longest, LongestText(item, depth + 1));
            }
            else if (value is Dictionary<string, object> dict)
            {
                foreach (var pair in dict)
                    longest = Math.Max(longest, Math.Max(pair.Key.Length, LongestText(pair.Value, depth + 1)));
            }
            return longest;
        }

        private static string Shorten(string text) => text.Length > 60 ? text.Substring(0, 60) + "..." : text;
    }
}
