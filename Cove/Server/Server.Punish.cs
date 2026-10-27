/*
   Copyright 2024 DrMeepso

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cove.Server.Actor;
using Cove.Server.Plugins;
using Steamworks;

namespace Cove.Server
{
    public partial class CoveServer
    {
        
        private static readonly object BanLock = new();
        
        public void banPlayer(CSteamID id, bool saveToFile = false, string banReason = "")
        {
            // every ban, whoever or whatever made it (a command, a plugin, Packet Guard), goes into packetguard.log
            GuardLog($"BAN       {id.m_SteamID} ({GuardSafe(GuardNameOf(id), 40)}): {(string.IsNullOrEmpty(banReason) ? "(no reason given)" : banReason)}{(saveToFile ? "" : " [not written to bans.txt]")}");
            GuardWriteTally(id.m_SteamID, "at the ban");
            var player = new WFPlayer(id, new SteamNetworkingIdentity());

            Dictionary<string, object> banPacket = new();
            banPacket["type"] = "client_was_banned";
            sendPacketToPlayer(banPacket, id);

            Dictionary<string, object> leftPacket = new();
            leftPacket["type"] = "peer_was_banned";
            leftPacket["user_id"] = (long)id.m_SteamID;
            sendPacketToPlayers(leftPacket);


            // a plugin that throws here must not stop the ban itself from being applied below
            foreach (PluginInstance plugin in loadedPlugins.ToList())
            {
                SafePluginCall(plugin, () => plugin.plugin.onPlayerBanned(player, banReason));
            }

            if (saveToFile)
                writeToBansFile(id, banReason);

            sendBlacklistPacketToAll(id.m_SteamID.ToString());
        }
        public void unbanPlayer(CSteamID id)
        {
            // var player = new WFPlayer(
            //     id,
            //     Steamworks.SteamFriends.GetFriendPersonaName(id),
            //     new SteamNetworkingIdentity()
            // );
            // plugin.plugin.onPlayerUnbanned(player);
            GuardLog($"UNBAN     {id.m_SteamID} ({GuardSafe(GuardNameOf(id), 40)})");
            unwriteFromBansFile(id);
        }

        // In-memory copy of bans.txt. This check runs for every single incoming packet (dozens per second per
        // player), and it used to read and scan the whole file from disk each time, under a global lock.
        private readonly HashSet<ulong> bannedIds = new();
        private DateTime bansLoadedWriteTime = DateTime.MinValue;
        private long bansLastCheckTicks = 0;
        private static readonly TimeSpan BanRecheckInterval = TimeSpan.FromSeconds(2);

        private void reloadBansIfChanged()
        {
            // only look at the file at most every couple of seconds, so manual edits to bans.txt are still picked up
            long now = Environment.TickCount64;
            if (bansLastCheckTicks != 0 && now - bansLastCheckTicks < BanRecheckInterval.TotalMilliseconds)
                return;
            bansLastCheckTicks = now;

            string fileDir = $"{AppDomain.CurrentDomain.BaseDirectory}bans.txt";

            // check if the file exists
            if (!File.Exists(fileDir))
            {
                File.Create(fileDir).Close();
                bannedIds.Clear();
                bansLoadedWriteTime = File.GetLastWriteTimeUtc(fileDir);
                return; // no one is banned yet
            }

            DateTime writeTime = File.GetLastWriteTimeUtc(fileDir);
            if (writeTime == bansLoadedWriteTime)
                return; // unchanged since the last load

            try
            {
                var loaded = new HashSet<ulong>();
                foreach (string rawLine in File.ReadAllLines(fileDir))
                {
                    // lines look like "<steamid> # <name> - <reason>", only the leading id counts. Matching the whole
                    // line used to ban people whose id happened to appear inside someone else's name or reason.
                    string line = rawLine.Trim();
                    int comment = line.IndexOf('#');
                    string idPart = (comment >= 0 ? line.Substring(0, comment) : line).Trim();

                    if (ulong.TryParse(idPart, out ulong banned))
                        loaded.Add(banned);
                }

                bannedIds.Clear();
                foreach (ulong banned in loaded)
                    bannedIds.Add(banned);

                bansLoadedWriteTime = writeTime;
            }
            catch (IOException e)
            {
                // the file is busy (being written), keep the previous list and try again on the next check
                logger.Warning($"Could not read bans.txt: {e.Message}");
            }
        }

        public bool isPlayerBanned(CSteamID id)
        {
            lock (BanLock)
            {
                reloadBansIfChanged();
                return bannedIds.Contains(id.m_SteamID);
            }
        }

        private void writeToBansFile(CSteamID id, string reason)
        {
            string entry = $"{id.m_SteamID} # ";
            string username = PreviousPlayers.Find(p => p.SteamId == id)?.Username ?? "Unknown";
            entry += username;
            if (!string.IsNullOrEmpty(reason))
                entry += $" - {reason}";
            // check if the file is already opened by another process
            lock (BanLock)
            {
                try
                {
                    File.AppendAllLines(
                        $"{AppDomain.CurrentDomain.BaseDirectory}bans.txt",
                        [entry]
                    );

                    // apply right away, don't wait for the next file check
                    bannedIds.Add(id.m_SteamID);
                    bansLoadedWriteTime = File.GetLastWriteTimeUtc($"{AppDomain.CurrentDomain.BaseDirectory}bans.txt");
                }
                catch (IOException e)
                {
                    logger.Warning($"Error banning player: {e.ToString()}");
                }
            }
        }

        private void unwriteFromBansFile(CSteamID id)
        {
            string username = PreviousPlayers.Find(p => p.SteamId == id)?.Username ?? "Unknown";
            lock (BanLock)
            {
                try
                {
                    var fileContent = File.ReadAllLines(
                        $"{AppDomain.CurrentDomain.BaseDirectory}bans.txt"
                    );
                    string idString = id.m_SteamID.ToString();
                    File.WriteAllLines(
                        $"{AppDomain.CurrentDomain.BaseDirectory}bans.txt",
                        fileContent.Where(line => line.Split('#')[0].Trim() != idString).ToArray()
                    );

                    bannedIds.Remove(id.m_SteamID);
                    bannedNotified.TryRemove(id.m_SteamID, out _); // allow a future re-ban to be announced again
                    bansLoadedWriteTime = File.GetLastWriteTimeUtc($"{AppDomain.CurrentDomain.BaseDirectory}bans.txt");
                }
                catch (IOException e)
                {
                    logger.Warning($"Error banning player: {e.ToString()}");
                }
            }
        }

        public void kickPlayer(CSteamID id)
        {
            GuardLog($"KICK      {id.m_SteamID} ({GuardSafe(GuardNameOf(id), 40)})");
            GuardWriteTally(id.m_SteamID, "at the kick");
            Dictionary<string, object> leftPacket = new();
            leftPacket["type"] = "peer_was_kicked";
            leftPacket["user_id"] = (long)id.m_SteamID;
            sendPacketToPlayers(leftPacket);

            Dictionary<string, object> kickPacket = new();
            kickPacket["type"] = "client_was_kicked";
            sendPacketToPlayer(kickPacket, id);
        }
    }
}
