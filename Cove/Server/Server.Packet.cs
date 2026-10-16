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

using Cove.GodotFormat;
using Cove.Server.Actor;
using Cove.Server.Plugins;
using Cove.Server.Utils;
using Steamworks;

namespace Cove.Server
{
    partial class CoveServer
    {
        // Players that have already been told they are banned, so a banned client that keeps sending packets
        // doesn't trigger a network wide ban broadcast for every single one of them.
        // Concurrent because it is touched from both the network thread (under stateLock) and the unban path
        // (under BanLock), taking either lock from the other side could deadlock.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> bannedNotified = new();

        // Players that currently have a chalk sync running, stops a client from spamming new_player_join
        // to spawn an unlimited number of sender threads.
        private readonly HashSet<ulong> chalkSyncRunning = new();

        // Only canvases 0-3 exist in the game, anything else is ignored instead of allocating a canvas for it.
        private const long MaxCanvasID = 3;

        // Safety valve for the per canvas cell dictionary, vanilla canvases are far smaller than this.
        private const int MaxChalkCellsPerPacket = 250_000;

        // when each player was last named in the log for sending chalk outside the canvas
        private readonly Dictionary<ulong, long> lastOutsideChalkLog = new();

        // ---- safe readers for attacker controlled packet data ----

        private static bool TryGetDict(Dictionary<string, object> source, string key, out Dictionary<string, object> value)
        {
            value = null;
            if (source != null && source.TryGetValue(key, out object raw) && raw is Dictionary<string, object> d)
            {
                value = d;
                return true;
            }
            return false;
        }

        private static bool TryGetLong(Dictionary<string, object> source, string key, out long value)
        {
            value = 0;
            return source != null && source.TryGetValue(key, out object raw) && TryToLong(raw, out value);
        }

        private static bool TryToLong(object raw, out long value)
        {
            switch (raw)
            {
                case long l: value = l; return true;
                case int i: value = i; return true;
                default: value = 0; return false;
            }
        }

        private static bool TryGetString(Dictionary<string, object> source, string key, out string value)
        {
            value = null;
            if (source != null && source.TryGetValue(key, out object raw) && raw is string s)
            {
                value = s;
                return true;
            }
            return false;
        }

        private static bool TryGetVector3(Dictionary<string, object> source, string key, out Vector3 value)
        {
            value = null;
            if (source != null && source.TryGetValue(key, out object raw) && raw is Vector3 v)
            {
                value = v;
                return true;
            }
            return false;
        }

        // TODO: Make this a switch statement
        void OnNetworkPacket(byte[] packet, CSteamID sender)
        {
            if (packet == null || packet.Length < 8)
                return; // nothing a game client would ever send

            // just in case!
            if (isPlayerBanned(sender))
            {
                if (bannedNotified.TryAdd(sender.m_SteamID, 0))
                    banPlayer(sender);
                return; // never process anything from a banned player
            }

            Dictionary<string, object> packetInfo;
            try
            {
                packetInfo = readPacket(GzipHelper.DecompressGzip(packet));
            }
            catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is IOException)
            {
                // not gzip, truncated, or a decompression bomb. Drop it quietly, a client can send this at will.
                return;
            }

            if (packetInfo == null || !TryGetString(packetInfo, "type", out string packetType))
                return; // unreadable packet, or no type to dispatch on

            var fromPlayer = AllPlayers.Find(p => p.SteamId.m_SteamID == sender.m_SteamID);
            if (fromPlayer != null)
            {
                foreach (var loadedPlugin in loadedPlugins.ToList())
                {
                    SafePluginCall(loadedPlugin, () => loadedPlugin.plugin.onNetworkPacket(fromPlayer, packetInfo));
                }
            }

            switch (packetType)
            {
                case "handshake_request":
                    {
                        Dictionary<string, object> handshakePacket = new();
                        handshakePacket["type"] = "handshake";
                        handshakePacket["user_id"] = SteamUser.GetSteamID().m_SteamID.ToString();
                        sendPacketToPlayer(handshakePacket, sender);
                    }
                    break;

                case "new_player_join":
                    {
                        if (displayJoinMessage)
                        {
                            messagePlayer(joinMessage, sender);
                        }
                        Dictionary<string, object> hostPacket = new();
                        hostPacket["type"] = "recieve_host";
                        hostPacket["host_id"] = SteamUser.GetSteamID().m_SteamID.ToString();
                        sendPacketToPlayers(hostPacket);

                        if (isPlayerAdmin(sender))
                            messagePlayer("You're an admin on this server!", sender);

                        // only one chalk sync per player at a time
                        if (chalkSyncRunning.Add(sender.m_SteamID))
                        {
                            Thread ChalkInformer = new Thread(() => SendStagedChalkPackets(sender));
                            ChalkInformer.IsBackground = true; // must not keep the process alive on shutdown
                            ChalkInformer.Start(); // send the player all the chalk data
                        }
                    }

                    break;

                case "instance_actor":
                    {
                        if (!TryGetDict(packetInfo, "params", out var actorParams)
                            || !TryGetString(actorParams, "actor_type", out string type)
                            || !TryGetLong(actorParams, "actor_id", out long actorID))
                            break; // malformed

                        // all actor types that should not be spawned by anyone but the server!
                        List<string> illegalTypes = ["fish_spawn_alien", "fish_spawn", "raincloud", "ambient_bird", "void_portal", "metal_spawn"];

                        // if the server owner dosent want players to spawn canvas's then add it to the list
                        if (!playersCanSpawnCanvas)
                            illegalTypes.Add("canvas");

                        if (illegalTypes.Contains(type) && !isPlayerAdmin(sender))
                        {
                            kickPlayer(sender);

                            WFPlayer offendingPlayer = AllPlayers.Find(p => p.SteamId.m_SteamID == sender.m_SteamID);
                            if (offendingPlayer != null)
                                messageGlobal($"{offendingPlayer.Username} was kicked for spawning illegal actors");

                            break; // the actor must not be registered after the player was kicked for spawning it
                        }

                        if (type == "player")
                        {
                            WFPlayer thisPlayer = AllPlayers.Find(p => p.SteamId.m_SteamID == sender.m_SteamID);
                            if (thisPlayer == null)
                                Log("No fisher found for player instance!");
                            else
                            {
                                thisPlayer.InstanceID = actorID;

                                // a client can send this more than once, don't track (or announce) the same player twice
                                bool alreadyTracked = allActors.Contains(thisPlayer);
                                if (!alreadyTracked)
                                    allActors.Add(thisPlayer); // add the player to the actor list

                                if (!alreadyTracked)
                                {
                                    // print out a join message
                                    Log($"[{thisPlayer.FisherID}] {thisPlayer.Username} joined. [{AllPlayers.Count}/{MaxPlayers}]");
                                    sendWebLobbyPacket(thisPlayer.SteamId);
                                    updatePlayercount();

                                    foreach (var p in loadedPlugins.ToList())
                                        SafePluginCall(p, () => p.plugin.onPlayerJoin(thisPlayer));
                                }
                            }

                        } else {
                            WFActor cActor = new WFActor(actorID, type, Vector3.zero, Vector3.zero);
                            cActor.owner = sender;
                            allActors.Add(cActor);
                        }

                    }
                    break;

                case "actor_update":
                    {
                        if (!TryGetLong(packetInfo, "actor_id", out long updatedActorID)
                            || !TryGetVector3(packetInfo, "pos", out Vector3 position))
                            break; // malformed

                        WFPlayer thisPlayer = AllPlayers.Find(p => p.InstanceID == updatedActorID);
                        if (thisPlayer != null)
                        {
                            thisPlayer.pos = position;
                        }
                    }
                    break;

                case "request_ping":
                    {
                        Dictionary<string, object> pongPacket = new();
                        pongPacket["type"] = "send_ping";
                        pongPacket["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                        pongPacket["from"] = SteamUser.GetSteamID().m_SteamID.ToString();

                        sendPacketToPlayer(pongPacket, sender);
                    }
                    break;

                case "actor_action":
                    {
                        if (TryGetString(packetInfo, "action", out string action) && action == "_wipe_actor")
                        {
                            if (!packetInfo.TryGetValue("params", out object rawParams)
                                || rawParams is not Dictionary<int, object> actionParams
                                || !actionParams.TryGetValue(0, out object rawActor)
                                || !TryToLong(rawActor, out long actorToWipe))
                                break; // malformed

                            WFActor serverInst = serverOwnedInstances.Find(i => i.InstanceID == actorToWipe);
                            if (serverInst != null)
                            {
                                // stop players from removing rain clouds
                                if (serverInst.Type == "raincloud")
                                    break;

                                //Log($"Player asked to remove {serverInst.Type} actor");

                                // the sever owns the instance
                                removeServerActor(serverInst);
                            }
                        }
                    }
                    break;

                case "request_actors":
                    {
                        sendPlayerAllServerActors(sender);
                        sendPacketToPlayer(createRequestActorResponce(), sender); // this is empty because otherwise all the server actors are invisible!
                    }
                    break;

                case "chalk_packet":
                    {
                        if (!TryGetLong(packetInfo, "canvas_id", out long canvasID)
                            || !packetInfo.TryGetValue("data", out object rawChalk)
                            || rawChalk is not Dictionary<int, object> chalkData)
                            break; // malformed

                        // only the real canvases are tracked, otherwise a client could allocate canvases forever
                        if (canvasID < 0 || canvasID > MaxCanvasID)
                            break;

                        if (chalkData.Count > MaxChalkCellsPerPacket)
                            break;

                        Chalk.ChalkCanvas? canvas = chalkCanvas.Find(c => c.canvasID == canvasID);
                        if (canvas == null)
                        {
                            canvas = new Chalk.ChalkCanvas(canvasID, this);
                            chalkCanvas.Add(canvas);
                        }

                        int rejectedBefore = canvas.rejectedOutsideCanvas;
                        canvas.chalkUpdate(chalkData);
                        int rejected = canvas.rejectedOutsideCanvas - rejectedBefore;
                        if (rejected > 0)
                        {
                            // say who sent it, but not more than once a minute per player or the log fills up
                            long nowTicks = Environment.TickCount64;
                            if (!lastOutsideChalkLog.TryGetValue(sender.m_SteamID, out long lastLogged) || nowTicks - lastLogged > 60_000)
                            {
                                lastOutsideChalkLog[sender.m_SteamID] = nowTicks;
                                string who = AllPlayers.Find(p => p.SteamId.m_SteamID == sender.m_SteamID)?.Username ?? "unknown";
                                Log($"Ignored {rejected} chalk cells outside canvas {canvasID} sent by {who} ({sender.m_SteamID}).");
                            }
                        }

                        WFPlayer? from = AllPlayers.Find(p =>
                            p.SteamId.m_SteamID == sender.m_SteamID
                        );
                        foreach (PluginInstance p in loadedPlugins.ToList())
                        {
                            SafePluginCall(p, () => p.plugin.onChalkUpdate(from, canvasID, chalkData));
                        }
                    }
                    break;

                case "message":
                    {
                        if (!TryGetString(packetInfo, "message", out string Message)
                            || !packetInfo.TryGetValue("local", out object rawLocal) || rawLocal is not bool local
                            || !TryGetVector3(packetInfo, "position", out Vector3 position)
                            || !TryGetString(packetInfo, "zone", out string zone))
                            break; // malformed

                        if (Message.StartsWith("%u:")) // Format for a normal chat message
                        {
                            Message = Message.Replace("%u: ", "");
                        }
                        else if (Message.StartsWith("(%u")) // Format for a /me message
                        {
                            // Because it's easier I'm just gonna replace the %u with the player's name
                            // and use that as the message content
                            var thisPlayer = AllPlayers.Find(p => p.SteamId.m_SteamID == sender.m_SteamID);
                            if (thisPlayer == null) break;
                            Message = Message.Replace("%u", thisPlayer.Username);
                        }
                        OnPlayerChat(Message, sender, local, position, zone);
                    }
                    break;
            }
        }

        internal void SendStagedChalkPackets(CSteamID recipient)
        {
            try
            {
                // snapshot the canvases so a canvas being added during the sync can't break the enumeration
                Chalk.ChalkCanvas[] canvases;
                lock (stateLock)
                    canvases = chalkCanvas.ToArray();

                // send the player all the canvas data
                foreach (Chalk.ChalkCanvas canvas in canvases)
                {
                    canvas.emitChalk(omitEmptyCells: true, recipient);
                    Thread.Sleep(500);
                }
            }
            catch (Exception e)
            {
                Error(e.ToString());
            }
            finally
            {
                lock (stateLock)
                    chalkSyncRunning.Remove(recipient.m_SteamID);
            }
        }
    }
}
