using Cove.Server.Actor;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;



namespace Cove.Server
{

    /// <summary>Who may use a command. Only used to decide which commands !help shows, a command still checks for itself.</summary>
    public enum CommandAccess
    {
        Everyone,
        Admin,
        Host,
    }

    public class RegisteredCommand
    {
        public CommandAccess Access = CommandAccess.Everyone;
        public string Command;
        public string Description;
        public Action<WFPlayer, string[]> Callback;
        public string[] Aliases;
        /// <summary>The plugin that added this command, or null for the server's own commands.</summary>
        public object? Owner;

        public RegisteredCommand(string command, string description, Action<WFPlayer, string[]> callback, string[]? aliases = null)
        {
            Command = command.ToLower(); // make sure its lower case to not mess anything up
            Description = description;
            Callback = callback;
            Aliases = aliases ?? [];
            Aliases = [.. Aliases.Select(alias => alias.ToLower())];
        }

        public void Invoke(WFPlayer player, string[] args)
        {
            Callback(player, args);
        }

    }

    public partial class CoveServer
    {
        List<RegisteredCommand> Commands = [];

        public WFPlayer GetPlayer(string playerIdent)
        {
            var selectedPlayer = AllPlayers.ToList().Find(p => p.Username.Equals(playerIdent, StringComparison.OrdinalIgnoreCase));
            // if there is no player with the username try to find someone with that fisher ID
            if (selectedPlayer == null)
                selectedPlayer = AllPlayers.ToList().Find(p => p.FisherID.Equals(playerIdent, StringComparison.OrdinalIgnoreCase));

            return selectedPlayer;
        }

        public void RegisterDefaultCommands()
        {
            RegisterCommand(
                "help",
                (player, args) =>
                {
                    // only the commands this player can use
                    bool admin = isPlayerAdmin(player.SteamId);
                    bool host = player.SteamId.m_SteamID == serverPlayer.SteamId.m_SteamID;
                    string commands = "Commands:";
                    foreach (var cmd in Commands.ToList())
                    {
                        if (cmd.Access == CommandAccess.Admin && !admin)
                            continue;
                        if (cmd.Access == CommandAccess.Host && !host)
                            continue;
                        commands += "\n" + $"{cmd.Command} - {cmd.Description}";
                    }
                    messagePlayer(commands, player.SteamId);
                }
            );
            SetCommandDescription("help", "Shows all commands");

            RegisterCommand(
                "reloadplugins",
                (player, args) =>
                {
                    if (!isPlayerAdmin(player.SteamId)) return;
                    // !reloadplugins reloads all of them, !reloadplugins <id> only the plugin with that id
                    messagePlayer("Reloading plugins...", player.SteamId);
                    messagePlayer(reloadPlugins(args.Length > 0 ? args[0] : null), player.SteamId);
                },
                ["reload"]
            );
            SetCommandDescription("reloadplugins", "Reloads the plugins from the plugins folder, or one plugin by its id");
            SetCommandAccess("reloadplugins", CommandAccess.Admin);

            RegisterCommand(command: "exit", aliases: ["shutdown"], cb: (player, args) =>
            {
                // make sure the player is the host
                if (player.SteamId != serverPlayer.SteamId)
                {
                    messagePlayer("You are not the host!", player.SteamId);
                    return;
                }
                messagePlayer("Server is shutting down!", player.SteamId);

                Stop(); // stop the server

            });
            SetCommandDescription("exit", "Shuts down the server (host only)");
            SetCommandAccess("exit", CommandAccess.Host);

            RegisterCommand("health", (player, args) =>
            {
                if (!isPlayerAdmin(player.SteamId) && player.SteamId != serverPlayer.SteamId) return;
                messagePlayer(CrashWatch.Health(writeSnapshot: true), player.SteamId);
            });
            SetCommandDescription("health", "Shows memory, threads and stuck loops, and writes a Crash Watch snapshot");
            SetCommandAccess("health", CommandAccess.Admin);

            RegisterCommand("kick", (player, args) =>
            {
                if (!isPlayerAdmin(player.SteamId)) return;
                string playerIdent = string.Join(" ", args);
                // try find a user with the username first
                var kickedplayer = GetPlayer(playerIdent);

                if (kickedplayer == null && System.Text.RegularExpressions.Regex.IsMatch(playerIdent, @"^7656119\d{10}$"))
                {
                    // if it is a steam ID, try to find the player by steam ID
                    CSteamID steamId = new CSteamID(Convert.ToUInt64(playerIdent));
                    messagePlayer($"Kicked {playerIdent}", player.SteamId);
                    kickPlayer(steamId);
                    return;
                }

                if (kickedplayer == null)
                {
                    messagePlayer("That's not a player!", player.SteamId);
                }
                else
                {
                    messagePlayer($"Kicked {kickedplayer.Username}", player.SteamId);
                    kickPlayer(kickedplayer.SteamId);
                }
            });
            SetCommandDescription("kick", "Kicks a player from the server");
            SetCommandAccess("kick", CommandAccess.Admin);

            RegisterCommand("ban", (player, args) =>
            {
                if (!isPlayerAdmin(player.SteamId)) return;
                // hacky fix,
                // Extract player name from the command message
                string playerIdent;// = string.Join(" ", args);
                
                string rawArgs = string.Join(" ", args);
                string banReason = string.Empty;

                var numQuotesInArgs = rawArgs.Count(c => c == '"');
                var hasBanReason = numQuotesInArgs >= 2;
                // While we'd hope admins use delimiters properly, it's actually totally fine for this case if they
                // e.g. use quotes inside quotes to quote the target's offending message within the banReason
                if (hasBanReason)
                {
                    var firstQuoteIndex = rawArgs.IndexOf('"');
                    var lastQuoteIndex = rawArgs.LastIndexOf('"');
                    banReason = rawArgs
                        .Substring(firstQuoteIndex + 1, lastQuoteIndex - firstQuoteIndex - 1)
                        .Trim();
                    rawArgs = rawArgs.Remove(
                        firstQuoteIndex,
                        lastQuoteIndex - firstQuoteIndex + 1
                    );
                }
                else if (rawArgs.Length > 2)
                {
                    messagePlayer("Error banning player: If you want to add a reason, please wrap it in quotes.", player.SteamId);
                }
                playerIdent = rawArgs.Trim();
                
                // try to find a user with the username first
                var playerToBan = GetPlayer(playerIdent);
                
                var previousPlayer = PreviousPlayers.ToList().Find(p => p.FisherID.Equals(playerIdent, StringComparison.OrdinalIgnoreCase));
                if (previousPlayer != null && playerToBan == null)
                {
                    messagePlayer($"There is a previous player with that name, if you meant to ban them add a # before the ID: #{playerIdent}", player.SteamId);
                    return;
                }
                    
                previousPlayer = PreviousPlayers.ToList().Find(p => $"#{p.FisherID}".Equals(playerIdent, StringComparison.OrdinalIgnoreCase));
                if (previousPlayer != null)
                {
                    playerToBan = new WFPlayer(previousPlayer.SteamId, new SteamNetworkingIdentity())
                    {
                        FisherID = previousPlayer.FisherID,
                        Username = previousPlayer.Username,
                    };
                }
                
                // use regex to check if its a steam ID
                if (playerToBan == null && System.Text.RegularExpressions.Regex.IsMatch(playerIdent, @"^7656119\d{10}$"))
                {
                    // if it is a steam ID, try to find the player by steam ID
                    CSteamID steamId = new CSteamID(Convert.ToUInt64(playerIdent));
                    if (isPlayerBanned(steamId))
                        banPlayer(steamId, false, banReason);
                    else
                        banPlayer(steamId, true, banReason); // save to file if they are not already in there!
                    
                    messagePlayer($"Banned player with Steam ID {playerIdent}", player.SteamId);
                    return;
                }
                
                if (playerToBan == null)
                {
                    messagePlayer("Player not found!", player.SteamId);
                }
                else
                {

                    if (isPlayerBanned(playerToBan.SteamId))
                        banPlayer(playerToBan.SteamId, false, banReason);
                    else
                        banPlayer(playerToBan.SteamId, true, banReason); // save to file if they are not already in there!

                    messagePlayer($"Banned {playerToBan.Username}", player.SteamId);
                    messageGlobal($"{playerToBan.Username} has been banned from the server.");
                }
                }
            );
            SetCommandDescription("ban", "Bans a player from the server");
            SetCommandAccess("ban", CommandAccess.Admin);

            RegisterCommand(
                "unban",
                (sender, args) =>
                {
                    if (!isPlayerAdmin(sender.SteamId)) return;
                    if (args.Length < 1)
                    {
                        messagePlayer("Command usage: !unban STEAM_ID", sender.SteamId);
                        return;
                    }
                    var forgivenPlayer = args[0];
                    if (
                        !System.Text.RegularExpressions.Regex.IsMatch(
                            forgivenPlayer,
                            @"^7656119\d{10}$"
                        )
                    )
                    {
                        messagePlayer($"Invalid Steam ID: {forgivenPlayer}", sender.SteamId);
                    }
                    else
                    {
                        CSteamID steamId = new CSteamID(Convert.ToUInt64(forgivenPlayer));
                        var player = new WFPlayer(
                            steamId,
                            new SteamNetworkingIdentity()
                        );
                        if (isPlayerBanned(steamId))
                        {
                            unbanPlayer(steamId);
                            messageGlobal($"{player.Username} is forgiven and no longer banned.");
                        }
                        else
                        {
                            messagePlayer("That player isn't banned!", sender.SteamId);
                        }
                    }
                }
            );
            SetCommandDescription("unban", "Removes a player (by Steam ID) from the ban list");
            SetCommandAccess("unban", CommandAccess.Admin);

            RegisterCommand(
                command: "prev",
                aliases: ["recent"],
                cb: (player, args) =>
            {
                    if (!isPlayerAdmin(player.SteamId))
                        return;
                    string players = "Previous Players:";
                foreach (var prevPlayer in PreviousPlayers)
                {
                        if (prevPlayer.State == PlayerState.InGame)
                            continue;

                    // we dont want to show players that left more than 10 minutes ago
                        if ((DateTime.UtcNow - DateTimeOffset .FromUnixTimeSeconds(prevPlayer.leftTimestamp) .UtcDateTime).TotalMinutes > 10)
                    {
                        continue;
                    }

                    string timeLeft =
                        $"{Math.Round((DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(prevPlayer.leftTimestamp).UtcDateTime).TotalMinutes)} minutes ago";
                        players +=
                            "\n"
                            + (
                                $"{prevPlayer.Username} [{prevPlayer.SteamId.ToString()}] ({prevPlayer.FisherID}) - Left: {timeLeft}\n"
                            );
                }
                    messagePlayer(players, player.SteamId);
                }
            );
            SetCommandDescription(
                "prev",
                "Shows a list of previous players that were connected to the server"
            );
            SetCommandAccess("prev", CommandAccess.Admin);
        }

        public void RegisterCommand(string command, Action<WFPlayer, string[]> cb, string[]? aliases = null)
        {
            aliases ??= [];

            if (Commands.Any(c => c.Command == command))
            {
                Log($"Command '{command}' is already registerd!");
                return;
            }
            else if (aliases.Any(alias => Commands.Find(c => c.Aliases.Contains(alias)) != null))
            {
                Log($"'{command}' has an alias that is already registerd elsewhere!");
                return;
            }

            Commands.Add(new RegisteredCommand(command, "", cb, aliases));

        }

        public void SetCommandAccess(string command, CommandAccess access)
        {
            var cmd = Commands.Find(c => c.Command == command);
            if (cmd == null)
            {
                Log($"Command '{command}' not found!");
                return;
            }
            cmd.Access = access;
        }

        public void UnregisterCommand(string command)
        {
            Commands.RemoveAll(c => c.Command == command);
        }

        /// <summary>
        /// Removes a command only if this owner added it, so a plugin can never take away the server's own
        /// commands (kick, ban, ...) or another plugin's when it stops or is reloaded.
        /// </summary>
        public void UnregisterCommand(string command, object owner)
        {
            int removed = Commands.RemoveAll(c => c.Command == command && ReferenceEquals(c.Owner, owner));
            if (removed == 0 && Commands.Any(c => c.Command == command))
                Log($"A plugin tried to remove the command '{command}', which it did not add. It was left in place.");
        }

        /// <summary>Marks who added a command (only the first owner counts).</summary>
        public void SetCommandOwner(string command, object owner)
        {
            var cmd = Commands.Find(c => c.Command == command);
            if (cmd != null && cmd.Owner == null)
                cmd.Owner = owner;
        }

        public void SetCommandDescription(string command, string description)
        {
            var cmd = Commands.Find(c => c.Command == command);
            if (cmd == null)
            {
                Log($"Command '{command}' not found!");
                return;
            }
            cmd.Description = description;
        }

        public void InvokeCommand(WFPlayer player, string command, string[] args)
        {
            var cmd = FindCommand(command);
            if (cmd == null)
            {
                Log($"Command '{command}' not found!");
                return;
            }
            cmd.Invoke(player, args);
        }

        public bool DoseCommandExist(string command)
        {
            var cmd = FindCommand(command);
            if (cmd == null)
                return false;

            return true;
        }

        private string publishedCommands = "";
        private ulong publishedCommandsLobby;

        /// <summary>
        /// Puts the command list into the lobby data for the Wolfpark HUD mod's help button:
        ///   wp_cmds   = the commands everyone can use
        ///   wp_cmds_a = the extra commands for admins
        /// as "command TAB description" lines. Only written when something changed.
        /// </summary>
        public void PublishCommandList()
        {
            if (!SteamLobby.IsValid())
                return;

            var all = Commands.ToList().OrderBy(c => c.Command).ToList();
            string everyone = CommandLines(all.Where(c => c.Access == CommandAccess.Everyone));
            string admins = CommandLines(all.Where(c => c.Access == CommandAccess.Admin));
            string signature = everyone + "\u0001" + admins;
            if (signature == publishedCommands && publishedCommandsLobby == SteamLobby.m_SteamID)
                return;

            SteamMatchmaking.SetLobbyData(SteamLobby, "wp_cmds", everyone);
            SteamMatchmaking.SetLobbyData(SteamLobby, "wp_cmds_a", admins);
            publishedCommands = signature;
            publishedCommandsLobby = SteamLobby.m_SteamID;
        }

        // Steam allows 8 KB per lobby data value, the list stops before that
        private static string CommandLines(IEnumerable<RegisteredCommand> commands)
        {
            var text = new StringBuilder();
            foreach (var cmd in commands)
            {
                string description = (cmd.Description ?? "").Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ');
                string line = cmd.Command + "\t" + description;
                if (Encoding.UTF8.GetByteCount(text.ToString()) + Encoding.UTF8.GetByteCount(line) + 1 > 8000)
                    break;
                if (text.Length > 0)
                    text.Append('\n');
                text.Append(line);
            }
            return text.ToString();
        }

        public RegisteredCommand? FindCommand(string name)
        {
            return Commands.Find(c =>
                c.Command == name.ToLower() || c.Aliases.Contains(name.ToLower())
            );
        }
    }
}
