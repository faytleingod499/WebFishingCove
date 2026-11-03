using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Steamworks;

namespace Cove.Server
{
    public partial class CoveServer
    {
        // The ban list for the Wolfpark HUD's admin panel (and for any admin who types it): !banlist answers with the
        // entries of bans.txt, newest first, and !banreason writes or changes the reason of a ban. Both are admin only.
        // bans.txt lines look like "<steamid> # <name> - <reason>" (the reason part is optional).
        private const int BanListMax = 200;
        private const int BanReasonMax = 120;
        private static readonly Regex SteamIdPattern = new(@"^7656119\d{10}$");

        private void RegisterBanListCommands()
        {
            RegisterCommand(
                "banlist",
                (player, args) =>
                {
                    if (!isPlayerAdmin(player.SteamId)) return;
                    messagePlayer(BuildBanListText(), player.SteamId);
                }
            );
            SetCommandDescription("banlist", "Shows the bans from bans.txt, newest first");
            SetCommandAccess("banlist", CommandAccess.Admin);

            RegisterCommand(
                "banreason",
                (player, args) =>
                {
                    if (!isPlayerAdmin(player.SteamId)) return;
                    string reason = args.Length > 1 ? CleanBanText(string.Join(" ", args.Skip(1)), BanReasonMax) : "";
                    if (args.Length < 2 || !SteamIdPattern.IsMatch(args[0]) || reason == "")
                    {
                        messagePlayer("Command usage: !banreason STEAM_ID reason", player.SteamId);
                        return;
                    }
                    CSteamID id = new CSteamID(Convert.ToUInt64(args[0]));
                    if (SetBanReason(id, reason))
                        messagePlayer($"Reason saved for {args[0]}: {reason}", player.SteamId);
                    else
                        messagePlayer("That player isn't in the ban list!", player.SteamId);
                }
            );
            SetCommandDescription("banreason", "Sets the reason shown next to a ban. Usage: !banreason <steam id> <reason>");
            SetCommandAccess("banreason", CommandAccess.Admin);
        }

        /// <summary>One line of text for a file or a chat message: no line breaks or control characters, cut to max.</summary>
        private static string CleanBanText(string? text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            StringBuilder sb = new();
            foreach (char c in text)
                sb.Append(char.IsControl(c) ? ' ' : c);
            string clean = sb.ToString().Trim();
            return clean.Length > max ? clean.Substring(0, max).TrimEnd() : clean;
        }

        /// <summary>Splits a bans.txt line. False for blank lines, comments and anything without a leading Steam ID.</summary>
        private bool TryParseBanLine(string rawLine, out ulong id, out string name, out string reason)
        {
            id = 0;
            name = "";
            reason = "";
            string line = rawLine.Trim();
            int hash = line.IndexOf('#');
            string idPart = (hash >= 0 ? line.Substring(0, hash) : line).Trim();
            if (!ulong.TryParse(idPart, out id))
                return false;

            string rest = hash >= 0 ? line.Substring(hash + 1).Trim() : "";
            // a name can contain " - " itself: when the player is known, use the name we have to find the split
            string? known = null;
            try
            {
                ulong lookFor = id;
                known = PreviousPlayers.ToList().Find(p => p.SteamId.m_SteamID == lookFor)?.Username;
            }
            catch
            {
                // the list changed while we looked, the plain split below is good enough
            }
            if (!string.IsNullOrEmpty(known) && rest == known)
            {
                name = known;
                return true;
            }
            if (!string.IsNullOrEmpty(known) && rest.StartsWith(known + " - "))
            {
                name = known;
                reason = rest.Substring(known.Length + 3).Trim();
                return true;
            }
            int cut = rest.IndexOf(" - ", StringComparison.Ordinal);
            if (cut >= 0)
            {
                name = rest.Substring(0, cut).Trim();
                reason = rest.Substring(cut + 3).Trim();
            }
            else
            {
                name = rest;
            }
            return true;
        }

        /// <summary>"Bans: N" and then one "steamid|name|reason" line per ban, newest first.</summary>
        private string BuildBanListText()
        {
            string[] lines = Array.Empty<string>();
            lock (BanLock)
            {
                try
                {
                    string path = $"{AppDomain.CurrentDomain.BaseDirectory}bans.txt";
                    if (File.Exists(path))
                        lines = File.ReadAllLines(path);
                }
                catch (IOException e)
                {
                    logger.Warning($"Could not read bans.txt: {e.Message}");
                }
            }

            var seen = new HashSet<ulong>();
            var entries = new List<string>();
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!TryParseBanLine(lines[i], out ulong id, out string name, out string reason))
                    continue;
                if (!seen.Add(id))
                    continue;
                entries.Add($"{id}|{ForChat(name, 40)}|{ForChat(reason, BanReasonMax)}");
            }

            StringBuilder text = new();
            text.Append($"Bans: {entries.Count}");
            foreach (string entry in entries.Take(BanListMax))
                text.Append('\n').Append(entry);
            return text.ToString();
        }

        // the HUD cuts the answer on "|" and new lines, and chat shows [ ] as formatting
        private static string ForChat(string text, int max)
        {
            return CleanBanText(text, max).Replace('|', '/').Replace('[', '(').Replace(']', ')');
        }

        /// <summary>Writes (or replaces) the reason of a ban in bans.txt. False when the ID is not in the file.</summary>
        internal bool SetBanReason(CSteamID id, string reason)
        {
            reason = CleanBanText(reason, BanReasonMax);
            lock (BanLock)
            {
                try
                {
                    string path = $"{AppDomain.CurrentDomain.BaseDirectory}bans.txt";
                    if (!File.Exists(path))
                        return false;

                    string[] lines = File.ReadAllLines(path);
                    bool found = false;
                    string shownName = "";
                    string before = "";
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (!TryParseBanLine(lines[i], out ulong lineId, out string name, out string oldReason) || lineId != id.m_SteamID)
                            continue;
                        found = true;
                        shownName = name;
                        before = oldReason;
                        lines[i] = $"{lineId} # {(name == "" ? "Unknown" : name)} - {reason}";
                    }
                    if (!found)
                        return false;

                    File.WriteAllLines(path, lines);
                    // the list of banned IDs did not change, only note the new write time so the file is not read again
                    bansLoadedWriteTime = File.GetLastWriteTimeUtc(path);
                    GuardLog($"BANREASON {id.m_SteamID} ({GuardSafe(shownName, 40)}): {(before == "" ? "(none)" : GuardSafe(before, 120))} -> {GuardSafe(reason, 120)}");
                    return true;
                }
                catch (IOException e)
                {
                    logger.Warning($"Error changing a ban reason: {e}");
                    return false;
                }
            }
        }
    }
}
