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

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Unicode;
using System.Threading.Channels;
using Cove.Server.Actor;
using Cove.Server.HostedServices;
using Cove.Server.Plugins;
using Cove.Server.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Steamworks;
using Vector3 = Cove.GodotFormat.Vector3;

namespace Cove.Server
{
    public partial class CoveServer
    {
        public Serilog.Core.Logger logger;

        public string WebFishingGameVersion = "1.12"; // make sure to update this when the game updates!
        public int MaxPlayers = 20;
        public string ServerName = "A Cove Dedicated Server";
        public string LobbyCode = new string(Enumerable.Range(0, 5).Select(_ => "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"[new Random().Next(36)]).ToArray());
        public bool codeOnly = true;
        public bool ageRestricted = false;
        public bool maskMaxPlayers = false;

        public string joinMessage = "This is a Cove dedicated server!\nPlease report any issues to the github (xr0.xyz/cove)";
        public bool displayJoinMessage = true;

        public float rainMultiplyer = 1f;
        public bool shouldSpawnMeteor = true;
        public bool shouldSpawnMetal = true;
        public bool shouldSpawnPortal = true;

        public bool showErrorMessages = true;
        public bool showBotRejoins = true;
        public bool friendsOnly = false;

        public bool playersCanSpawnCanvas = false;

        List<string> Admins = new();
        public CSteamID SteamLobby;

        public List<CSteamID> connectionsQueued = new();
        public List<WFPlayer> AllPlayers = new();
        public List<WFActor> serverOwnedInstances = new();
        public List<WFActor> allActors = new();

        private HSteamListenSocket listenSocket;

        public WFPlayer serverPlayer;

        Thread cbThread;
        Thread networkThread;

        public List<Vector3> fish_points;
        public List<Vector3> trash_points;
        public List<Vector3> shoreline_points;
        public List<Vector3> hidden_spot;

        Dictionary<string, IHostedService> services = new();

        /// <summary>
        /// Single lock guarding all shared server state (players, actors, chalk canvases).
        /// Steam callbacks, the network thread and the timer services all run on different threads, and the
        /// state they share is made of plain Lists. One lock (instead of several) means no lock-ordering deadlocks.
        /// Monitor locks are re-entrant, so nested use on the same thread is fine.
        /// </summary>
        public readonly object stateLock = new();

        // kept for plugin / service compatibility, it is the same lock as stateLock
        public object serverActorListLock => stateLock;

        public List<string> WantedTags = new();

        public List<PreviousPlayer> PreviousPlayers = new();
        
        // Init used to be `async void`, which meant any exception during startup escaped the caller's try/catch
        // and killed the process. It is now a normal blocking call so failures reach Program.cs.
        public void Init()
        {
            InitAsync().GetAwaiter().GetResult();
        }

        private async Task InitAsync()
        {
            cbThread = new(runSteamworksUpdate);
            cbThread.Name = "Steamworks Callback Thread";

            networkThread = new(RunNetwork);
            networkThread.Name = "Network Thread";

            Log("Loading world!");
            string worldFile = $"{AppDomain.CurrentDomain.BaseDirectory}worlds/main_zone.tscn";
            // a previous failed download used to leave an empty file behind, which then passed the Exists check forever
            if (File.Exists(worldFile) && new FileInfo(worldFile).Length == 0)
                File.Delete(worldFile);

            if (!File.Exists(worldFile))
            {
                var res = await DownloadWorldFile();
                if (!res)
                {
                    Error("Failed to download main_zone.tscn!");
                    Error("Place main_zone.tscn in the worlds folder next to the server and start it again.");
                    return;
                }
            }

            string banFile = $"{AppDomain.CurrentDomain.BaseDirectory}bans.txt";
            if (!File.Exists(banFile))
            {
                FileStream f = File.Create(banFile);
                f.Close(); // close the file
            }

            // get all the spawn points for fish!
            string mapFile = File.ReadAllText(worldFile);
            fish_points = WorldFile.readPoints("fish_spawn", mapFile);
            trash_points = WorldFile.readPoints("trash_point", mapFile);
            shoreline_points = WorldFile.readPoints("shoreline_point", mapFile);
            hidden_spot = WorldFile.readPoints("hidden_spot", mapFile);

            // spawning picks random entries from these, an empty list would throw on every spawn tick
            if (fish_points.Count == 0 || trash_points.Count == 0 || shoreline_points.Count == 0 || hidden_spot.Count == 0)
            {
                Error("main_zone.tscn is missing spawn points (fish_spawn, trash_point, shoreline_point or hidden_spot).");
                Error("The file may be corrupt, delete the worlds folder to download it again.");
                return;
            }

            Log("World Loaded!");

            Log("Reading server.cfg");

            Dictionary<string, string> config = ConfigReader.ReadConfig("server.cfg");
            foreach (string key in config.Keys)
            {
                switch (key)
                {
                    case "serverName":
                        ServerName = config[key];
                        break;

                    case "maxPlayers":
                        if (int.TryParse(config[key], out int parsedMax) && parsedMax > 0)
                            MaxPlayers = parsedMax;
                        else
                            Log($"\"{config[key]}\" is not a valid maxPlayers value, using {MaxPlayers}.");
                        break;

                    case "chalkCanvasSize":
                        if (int.TryParse(config[key], out int parsedChalkSize) && parsedChalkSize >= 0)
                            Chalk.ChalkCanvas.CanvasSize = parsedChalkSize;
                        else
                            Log($"\"{config[key]}\" is not a valid chalkCanvasSize value, using {Chalk.ChalkCanvas.CanvasSize}.");
                        break;

                    case "code":
                        LobbyCode = config[key].ToUpper();
                        break;

                    case "rainSpawnMultiplyer":
                        // invariant culture so "1.5" works on machines that use a decimal comma
                        if (float.TryParse(config[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsedRain))
                            rainMultiplyer = parsedRain;
                        else
                            Log($"\"{config[key]}\" is not a valid rainSpawnMultiplyer value, using {rainMultiplyer}.");
                        break;

                    case "codeOnly":
                        codeOnly = getBoolFromString(config[key]);
                        break;

                    case "gameVersion":
                        WebFishingGameVersion = config[key];
                        break;

                    case "ageRestricted":
                        ageRestricted = getBoolFromString(config[key]);
                        break;

                    case "pluginsEnabled":
                        arePluginsEnabled = getBoolFromString(config[key]);
                        break;

                    case "joinMessage":
                        joinMessage = config[key].Replace("\\n", "\n");
                        break;

                    case "spawnMeteor":
                        shouldSpawnMeteor = getBoolFromString(config[key]);
                        break;

                    case "spawnMetal":
                        shouldSpawnMetal = getBoolFromString(config[key]);
                        break;

                    case "spawnPortal":
                        shouldSpawnPortal = getBoolFromString(config[key]);
                        break;

                    case "showErrors":
                        showErrorMessages = getBoolFromString(config[key]);
                        break;

                    case "friendsOnly":
                        friendsOnly = getBoolFromString(config[key]);
                        break;

                    case "hideJoinMessage":
                        displayJoinMessage = !getBoolFromString(config[key]);
                        break;

                    case "showBotRejoins":
                        showBotRejoins = getBoolFromString(config[key]);
                        break;

                    case "tags":
                        var tags = config[key].Split(',');
                        // remove trailing whitespace from the tags
                        for (int i = 0; i < tags.Length; i++)
                        {
                            tags[i] = tags[i].Trim().ToLower();
                        }
                        WantedTags = tags.ToList();
                        break;

                    case "skibidi":
                        Log("Dop dop dop, yes yes");
                        break;

                    case "maskMaxPlayers":
                        maskMaxPlayers = getBoolFromString(config[key]);
                        break;

                    case "playersCanSpawnCanvas":
                        playersCanSpawnCanvas = getBoolFromString(config[key]);
                        break;

                    default:
                        Log($"\"{key}\" is not a supported config option!");
                        continue;
                }

                Log($"Set \"{key}\" to \"{config[key]}\"");

            }

            Log("Server setup based on config!");

            Log("Reading admins.cfg");
            readAdmins();
            LoadFamilyShareSettings();
            Log("Setup finished, starting server!");

            RegisterDefaultCommands(); // register the default commands

            if (Directory.Exists($"{AppDomain.CurrentDomain.BaseDirectory}plugins"))
            {
                loadAllPlugins();
            }
            else
            {
                Directory.CreateDirectory($"{AppDomain.CurrentDomain.BaseDirectory}plugins");
                Log("Created plugins folder!");
            }

            if (!SteamAPI.Init())
            {
                Log("SteamAPI_Init() failed.");
                Log("Is Steam running?");
                return;
            }

            serverPlayer = new WFPlayer(SteamUser.GetSteamID(), new SteamNetworkingIdentity()) { Username = SteamFriends.GetPersonaName() };

            // thread for running steamworks callbacks
            cbThread.IsBackground = true;
            cbThread.Start();

            // thread for getting network packets from steam
            // i wish this could be a service, but when i tried it the packets got buffered and it was a mess
            // like 10 minutes of delay within 30 seconds
            networkThread.IsBackground = true;
            networkThread.Start();

            // Create a logger for the server
            Serilog.Log.Logger = logger;

            bool LogServices = false;
            ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            {
                if (LogServices)
                    builder.AddConsole();

                builder.AddSerilog(logger);
            });

            // Create a logger for each service that we need to run.
            Logger<ActorUpdateService> actorServiceLogger = new Logger<ActorUpdateService>(loggerFactory);
            Logger<HostSpawnService> hostSpawnServiceLogger = new Logger<HostSpawnService>(loggerFactory);
            Logger<HostSpawnMetalService> hostSpawnMetalServiceLogger = new Logger<HostSpawnMetalService>(loggerFactory);

            // Create the services that we need to run.
            IHostedService actorUpdateService = new ActorUpdateService(actorServiceLogger, this);
            IHostedService hostSpawnService = new HostSpawnService(hostSpawnServiceLogger, this);
            IHostedService hostSpawnMetalService = new HostSpawnMetalService(hostSpawnMetalServiceLogger, this);

            // add them to the services dictionary so we can access them later if needed
            services["actor_update"] = actorUpdateService;
            services["host_spawn"] = hostSpawnService;
            services["host_spawn_metal"] = hostSpawnMetalService;

            Callback<LobbyCreated_t>.Create(Guard<LobbyCreated_t>("LobbyCreated", (LobbyCreated_t param) =>
            {
                SteamLobby = new CSteamID(param.m_ulSteamIDLobby);
                SteamMatchmaking.SetLobbyJoinable(SteamLobby, true);
                SteamMatchmaking.SetLobbyData(SteamLobby, "ref", "webfishing_gamelobby");
                SteamMatchmaking.SetLobbyData(SteamLobby, "version", WebFishingGameVersion);
                SteamMatchmaking.SetLobbyData(SteamLobby, "code", LobbyCode);
                SteamMatchmaking.SetLobbyData(SteamLobby, "type", "0");
                SteamMatchmaking.SetLobbyData(SteamLobby, "public", codeOnly ? "false" : "true");
                SteamMatchmaking.SetLobbyData(SteamLobby, "request", "false"); // make this a option later down the line
                if (maskMaxPlayers && MaxPlayers > 12)
                    SteamMatchmaking.SetLobbyData(SteamLobby, "cap", $"12");
                else
                    SteamMatchmaking.SetLobbyData(SteamLobby, "cap", $"{MaxPlayers}");

                Log("Lobby Created!");
                Log($"Lobby Code: {LobbyCode}");
                // set the player count in the title
                updatePlayercount();

                // Start the services.
                actorUpdateService.StartAsync(CancellationToken.None);
                hostSpawnService.StartAsync(CancellationToken.None);
                hostSpawnMetalService.StartAsync(CancellationToken.None);

                SteamMatchmaking.SetLobbyData(SteamLobby, "server_browser_value", "0");

                string[] LOBBY_TAGS = ["talkative", "quiet", "grinding", "chill", "silly", "hardcore", "mature", "modded"];
                for (int i = 0; i < LOBBY_TAGS.Length; i++)
                {
                    bool wantedTag = WantedTags.Contains(LOBBY_TAGS[i]);
                    SteamMatchmaking.SetLobbyData(SteamLobby, $"tag_{LOBBY_TAGS[i]}", wantedTag ? "1" : "0");
                    if (wantedTag)
                        Log($"Added tag: {LOBBY_TAGS[i]}");
                }

                ulong epoch = (ulong)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                SteamMatchmaking.SetLobbyData(SteamLobby, "timestamp", epoch.ToString());

                /*
                int dataCount = SteamMatchmaking.GetLobbyDataCount(SteamLobby);
                for (int j = 0; j < dataCount; j++)
                {
                    string key, value;
                    SteamMatchmaking.GetLobbyDataByIndex(SteamLobby, j, out key, 100, out value, 100);
                    Log($"{key}: {value}");
                }
                */

            }));

            Callback<LobbyChatUpdate_t>.Create(Guard<LobbyChatUpdate_t>("LobbyChatUpdate", (LobbyChatUpdate_t param) =>
            {
                CSteamID lobbyID = new CSteamID(param.m_ulSteamIDLobby);

                CSteamID userChanged = new CSteamID(param.m_ulSteamIDUserChanged);
                CSteamID userMakingChange = new CSteamID(param.m_ulSteamIDMakingChange);

                EChatMemberStateChange stateChange = (EChatMemberStateChange)param.m_rgfChatMemberStateChange;
                // Player joined the lobby
                if (stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeEntered))
                {
                    string Username = SteamFriends.GetFriendPersonaName(userChanged);
                    ulong steamID = param.m_ulSteamIDMakingChange;

                    Log($"[{steamID}] {Username} is attempting to join the lobby.");

                    connectionsQueued.Add(userChanged);
                }

                // Player left the lobby
                if (stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeLeft) || stateChange.HasFlag(EChatMemberStateChange.k_EChatMemberStateChangeDisconnected))
                {
                    string Username = SteamFriends.GetFriendPersonaName(userChanged);

                    // if player is in connectionsQueued, remove them
                    if (connectionsQueued.Contains(userChanged))
                        connectionsQueued.Remove(userChanged);

                    // if player is in AllPlayers, remove them
                    if (!connectionsQueued.Contains(userChanged))
                    {
                        var player = AllPlayers.Find(p => p.SteamId == userChanged);
                        if (player != null)
                        {
                            AllPlayers.Remove(player);
                            GuardLog($"LEFT      {player.SteamId.m_SteamID} ({GuardSafe(player.Username, 40)})");
                            GuardWriteTally(player.SteamId.m_SteamID, "this visit");
                            allActors.Remove(player); // players are actors too, leaving them here leaked an entry per join
                            Log($"[{player.FisherID}] {player.Username} left. [{AllPlayers.Count}/{MaxPlayers}]");

                            Dictionary<string, object> leftPacket = new();
                            leftPacket["type"] = "user_left_weblobby";
                            leftPacket["user_id"] = (long)player.SteamId.m_SteamID;
                            leftPacket["reason"] = (int)0;
                            sendPacketToPlayers(leftPacket);

                            SteamNetworkingMessages.CloseSessionWithUser(ref player.identity);
                            updatePlayercount();

                            // tell all plugins that the player left
                            foreach (var plugin in loadedPlugins.ToList())
                                SafePluginCall(plugin, () => plugin.plugin.onPlayerLeave(player));

                            // find the previous player and update their state
                            var previousPlayer = PreviousPlayers.Find(p => p.SteamId == player.SteamId);
                            if (previousPlayer != null)
                            {
                                previousPlayer.leftTimestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                previousPlayer.State = PlayerState.Left;
                            }
                            else
                            {
                                // if the player is not in the previous players list, add them
                                var p = PreviousPlayer.FromWFPlayer(player);
                                PreviousPlayers.Add(p);
                                p.leftTimestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                p.State = PlayerState.Left;
                            }
                        }
                    }
                }
            }));

            listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, []);

            Callback<SteamNetworkingMessagesSessionRequest_t>.Create(Guard<SteamNetworkingMessagesSessionRequest_t>("SessionRequest", (SteamNetworkingMessagesSessionRequest_t param) =>
            {

                // if the player is banned reject the session request
                if (isPlayerBanned(new CSteamID(param.m_identityRemote.GetSteamID64())))
                {
                    SteamNetworkingMessages.CloseSessionWithUser(ref param.m_identityRemote);
                    return;
                }

                // get the players WFPlayer object
                WFPlayer player = AllPlayers.Find(p => p.SteamId == param.m_identityRemote.GetSteamID());
                if (player == null)
                {
                    //Log("Player not found!");
                    SteamNetworkingMessages.CloseSessionWithUser(ref param.m_identityRemote);
                    return;
                }

                SteamNetworkingMessages.AcceptSessionWithUser(ref param.m_identityRemote);
                sendWebLobbyPacket(new CSteamID(param.m_identityRemote.GetSteamID64()));

                player.identity = param.m_identityRemote; // update the players identity
            }));

            Callback<LobbyChatMsg_t>.Create(Guard<LobbyChatMsg_t>("LobbyChatMsg", (LobbyChatMsg_t callback) =>
            {

                CSteamID userId = (CSteamID)callback.m_ulSteamIDUser;
                byte[] data = new byte[4096]; // Max size for a message
                EChatEntryType chatEntryType;

                // Retrieve the message
                int messageLength = SteamMatchmaking.GetLobbyChatEntry(
                    (CSteamID)callback.m_ulSteamIDLobby,
                    (int)callback.m_iChatID,
                    out userId,
                    data,
                    data.Length,
                    out chatEntryType
                );

                if (messageLength > 0)
                {
                    string lobbyMessage = Encoding.UTF8.GetString(data, 0, messageLength);

                    // man i dont fucking know anymore
                    if (String.Compare("$weblobby_join_request", lobbyMessage) == 0 || lobbyMessage.Trim() == "$weblobby_join_request")
                    {
                        if (AllPlayers.Contains(AllPlayers.Find(p => p.SteamId == userId)))
                        {
                            sendWebLobbyPacket(userId);
                            //Log("User: " + userId + " is already in the lobby!");
                            //Log("If player is stuck on loading screen please tell me (Meepso)");
                            return;
                        }

                        // check if the user is banned 
                        if (isPlayerBanned(userId))
                        {
                            //Log($"Player {userId} tried to join, but they are banned!");
                            // send a steamlobby chat message to the player
                            var rejectMessage = $"$weblobby_request_denied_deny-{userId.m_SteamID}";
                            var rejectData = Encoding.UTF8.GetBytes(rejectMessage);
                            SteamMatchmaking.SendLobbyChatMsg(new CSteamID(callback.m_ulSteamIDLobby), rejectData, rejectData.Count());
                            return;
                        }

                        // Steam Family Sharing (a borrowed copy of the game) is refused. While Steam is being asked
                        // nothing is sent; the player's game repeats the request about once a second.
                        ShareVerdict shareVerdict = CheckFamilySharing(userId);
                        if (shareVerdict == ShareVerdict.Wait)
                            return;
                        if (shareVerdict == ShareVerdict.Deny)
                        {
                            var shareRejectData = Encoding.UTF8.GetBytes($"$weblobby_request_denied_deny-{userId.m_SteamID}");
                            SteamMatchmaking.SendLobbyChatMsg(new CSteamID(callback.m_ulSteamIDLobby), shareRejectData, shareRejectData.Count());
                            return;
                        }

                        var acceptMessage = $"$weblobby_request_accepted-{userId.m_SteamID}";
                        var acceptData = Encoding.UTF8.GetBytes(acceptMessage);
                        bool suc = SteamMatchmaking.SendLobbyChatMsg(new CSteamID(callback.m_ulSteamIDLobby), acceptData, acceptData.Count());

                        // make the player a wfplayer
                        WFPlayer player = new WFPlayer(userId, new SteamNetworkingIdentity());
                        AllPlayers.Add(player);
                        GuardLog($"ACCEPTED  {userId.m_SteamID} ({GuardSafe(player.Username, 40)}): lobby join accepted (the welcome message only follows when their game finishes joining)");
                        
                        // if there is already a player with the same FisherID, remove them from the previous players list to prevent duplicates
                        var sharedIDPrev = PreviousPlayers.Find(p => p.FisherID == player.FisherID);
                        if (sharedIDPrev != null)
                        {
                            PreviousPlayers.Remove(sharedIDPrev); // remove the previous player with the same FisherID
                        }
                        
                        var prev = PreviousPlayers.Find(p => p.SteamId == userId);
                        if (prev != null)
                        {
                            PreviousPlayers.Remove(prev); // remove the previous player if they are already in the list
                        }
                        
                        PreviousPlayers.Add( PreviousPlayer.FromWFPlayer(player) ); // add the player to the previous players list

                        Dictionary<string, object> joinedPacket = new();
                        joinedPacket["type"] = "user_joined_weblobby";
                        joinedPacket["user_id"] = (long)userId.m_SteamID;
                        sendPacketToPlayers(joinedPacket);

                        Dictionary<string, object> membersPacket = new();
                        membersPacket["type"] = "receive_weblobby";
                        Dictionary<int, object> members = new();

                        members[0] = (long)serverPlayer.SteamId.m_SteamID;
                        for (int i = 0; i < AllPlayers.Count; i++)
                        {
                            members[i + 1] = (long)AllPlayers[i].SteamId.m_SteamID;
                        }

                        membersPacket["weblobby"] = members;
                        sendPacketToPlayers(membersPacket);
                    }
                }
            }));

            // add two to the max because the server counts as player and add one overflow player
            if (friendsOnly)
                SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxPlayers + 2);
            else
                SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, MaxPlayers + 2);
        }

        private bool getBoolFromString(string str)
        {
            if (str.ToLower() == "true")
                return true;
            else if (str.ToLower() == "false")
                return false;
            else
                return false;
        }

        /// <summary>
        /// Wraps a Steam callback so it runs under the shared state lock and can never throw.
        /// An exception escaping a callback would otherwise propagate out of SteamAPI.RunCallbacks and kill the
        /// callback thread, after which the server stops seeing joins, leaves and session requests.
        /// </summary>
        private Callback<T>.DispatchDelegate Guard<T>(string name, Action<T> callback)
        {
            // Steamworks.NET's Callback<T>.Create wants its own nested delegate type, an Action<T> doesn't convert to it
            return (T param) =>
            {
                try
                {
                    CrashWatch.Busy("steam", $"waiting for the state lock (Steam callback {name})");
                    lock (stateLock)
                    {
                        CrashWatch.Busy("steam", $"Steam callback {name}");
                        callback(param);
                    }
                }
                catch (Exception e)
                {
                    logger.Error($"-- Error in Steam callback {name}! --");
                    logger.Error(e.ToString());
                }
            };
        }

        /// <summary>
        /// Runs a plugin hook, a plugin that throws must not be able to take the server down or stop other plugins from running.
        /// </summary>
        internal void SafePluginCall(PluginInstance plugin, Action call)
        {
            try
            {
                call();
            }
            catch (Exception e)
            {
                logger.Error($"Plugin \"{plugin.pluginName}\" threw an exception:");
                logger.Error(e.ToString());
            }
        }

        void runSteamworksUpdate()
        {
            while (true)
            {
                Thread.Sleep(1000/24); // 24hz
                try
                {
                    CrashWatch.Busy("steam", "SteamAPI.RunCallbacks");
                    SteamAPI.RunCallbacks();
                    CrashWatch.Idle("steam");
                }
                catch (Exception e)
                {
                    // never let this thread die, without it the server can no longer see players join or leave
                    logger.Error("-- Error running Steam callbacks! --");
                    logger.Error(e.ToString());
                }
            }
        }

        void RunNetwork()
        {
            while (true)
            {
                bool didWork = false;
                try
                {
                    for (int i = 0; i < 7; i++)
                    {
                        List<NetworkingMessage> messages = ReceiveMessagesOnChannel(i, 50);
                        if (messages.Count == 0)
                            // Skip processing this channel if no messages were received.
                            continue;

                        didWork = true;
                        for (int j = 0; j < messages.Count; j++)
                        {
                            // every packet is handled on its own, so one malformed packet can't
                            // cause the rest of the batch to be dropped
                            try
                            {
                                CrashWatch.Busy("network", $"waiting for the state lock (packet from {messages[j].identity}, channel {i}, {messages[j].size} bytes)");
                                if (i == 3 && messages[j].size > 50000)
                                {
                                    string UserName = SteamFriends.GetFriendPersonaName(new CSteamID(messages[j].identity));
                                    Log($"[{UserName}] Sent a chalk packet of size {messages[j].size} bytes");
                                    Log($"Due to the size of this packet, there is a chance it will not be processed correctly.");
                                }

                                lock (stateLock)
                                {
                                    CrashWatch.Busy("network", $"packet from {messages[j].identity}, channel {i}, {messages[j].size} bytes");
                                    OnNetworkPacket(messages[j].payload, new CSteamID(messages[j].identity));
                                }
                            }
                            catch (Exception e)
                            {
                                // even with the error messages off, Crash Watch keeps a short note of it
                                CrashWatch.CountError();
                                CrashWatch.Note("network", $"Packet from {messages[j].identity} on channel {i} failed: {e.GetType().Name}: {e.Message}");
                                if (showErrorMessages)
                                {
                                    Log($"-- Error responding to packet from {messages[j].identity} on channel {i}! --");
                                    Log(e.ToString());
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    // This used to `return` when showErrors was off, which silently ended the network thread
                    // and left a running server that never answered anyone again. Always keep the loop alive.
                    if (showErrorMessages)
                    {
                        Log("-- Error receiving packets! --");
                        Log(e.ToString());
                    }

                    // avoid a hot loop if the failure repeats every iteration
                    Thread.Sleep(100);
                }

                CrashWatch.Idle("network");
                if (!didWork)
                    Thread.Sleep(10);
            }
        }

        private int stopping = 0;

        // exitCode 0 means a normal stop. Fatal errors pass 1 so a restart script can tell a crash from a deliberate stop.
        public void Stop(int exitCode = 0)
        {
            // Stop can be reached from the console, Ctrl+C, SIGTERM, the unhandled exception handler and the
            // lobby ownership check at the same time, only the first caller should run the shutdown.
            if (Interlocked.Exchange(ref stopping, 1) != 0)
                return;

            Log("Server was told to stop.");

            // every step is isolated, a failure in one (e.g. Steam never initialised) must not skip the rest
            try
            {
                Dictionary<string, object> closePacket = new();
                closePacket["type"] = "server_close";
                sendPacketToPlayers(closePacket);
            }
            catch (Exception e) { logger.Error(e.ToString()); }

            try
            {
                foreach (var plugin in loadedPlugins.ToList())
                    SafePluginCall(plugin, () => plugin.plugin.onEnd()); // tell all plugins that the server is closing!
            }
            catch (Exception e) { logger.Error(e.ToString()); }

            try
            {
                disconnectAllPlayers();
                if (SteamLobby.m_SteamID != 0)
                    SteamMatchmaking.LeaveLobby(SteamLobby);
                SteamAPI.Shutdown();
            }
            catch (Exception e) { logger.Error(e.ToString()); }

            // a normal stop clears the Crash Watch flag, anything else gets a crash report
            if (exitCode == 0)
                CrashWatch.CleanExit();
            else
                CrashWatch.FailedExit(exitCode);

            Environment.Exit(exitCode);
        }

        /// <summary>
        ///  Handler & parser for player chat messages, calling plugins' onChatMessage hooks.
        /// </summary>
        /// <param name="message">
        /// Chat contents
        /// </param>
        /// <param name="from">
        /// SteamID of the sender
        /// </param>
        /// <param name="local">
        /// Whether the message was marked as by sender
        /// </param>
        /// <param name="position">
        /// Vector3 Position reported by sender
        /// </param>
        /// <param name="zone">Name of zone reported by sender</param>
        /// <returns></returns>
        /// <summary>
        ///  True for the chat commands the Wolfpark HUD mod sends by itself (its hello and its catch reports). They are
        ///  not logged and not relayed anywhere, they would drown out everything else.
        /// </summary>
        public static bool IsQuietCommand(string message)
        {
            if (message == null || !message.StartsWith("!"))
                return false;
            string word = message.Split(' ')[0].Substring(1).ToLowerInvariant();
            return word is "wphud" or "catchcoins";
        }

        void OnPlayerChat(
            string message,
            CSteamID from,
            bool local = false,
            Vector3? position = null,
            string zone = "global"
        )
        {
            WFPlayer sender = AllPlayers.Find(p => p.SteamId == from);
            if (sender == null)
            {
                Log($"[UNKNOWN] {from}: {message}");
                // should probbaly kick the player here
                return;
            }

            // Announcements like "%u ranked up to rank 2" carry %u where the game puts the player's name: fill it in for
            // the console (plugins still get the original text, the Discord bridge looks for the %u itself)
            if (IsQuietCommand(message))
            {
                // the Wolfpark HUD's own housekeeping, sent over and over: not worth a console line
            }
            else if (message.Contains("%u"))
                Log($"[{(local ? "Local" : "Global")}] [{sender.FisherID}] {message.Replace("%u", sender.Username)}");
            else
                Log($"[{(local ? "Local" : "Global")}] [{sender.FisherID}] {sender.Username}: {message}");

            // check if the first char is !, if so its a command
            if (message.StartsWith("!"))
            {
                string command = message.Split(' ')[0].Substring(1);
                string[] args = message.Split(' ').Skip(1).ToArray();

                if (DoseCommandExist(command))
                {
                    try
                    {
                        InvokeCommand(sender, command, args);
                    }
                    catch (Exception e)
                    {
                        // a command with bad arguments must not stop the chat message reaching plugins
                        logger.Error($"Command \"{command}\" threw an exception:");
                        logger.Error(e.ToString());
                        messagePlayer("That command failed, check the arguments and try again.", sender.SteamId);
                    }
                }
                else
                {
                    messagePlayer("Command not found!", sender.SteamId);
                    Log("Command not found!");
                }
            }

            foreach (PluginInstance p in loadedPlugins.ToList())
            {
                SafePluginCall(p, () => p.plugin.handleChatMessage(sender, message, local, position, zone));
            }
        }

        async Task<bool> DownloadWorldFile()
        {
            
            Log("Downloading main_zone.tscn file");

            string downloadURL =
                "https://raw.githubusercontent.com/Blizzardfur-Maxxx/Webfishing-Decompiled/refs/heads/main/Scenes/Map/Zones/main_zone.tscn";
            string worldDir = $"{AppDomain.CurrentDomain.BaseDirectory}worlds";
            string worldPath = worldDir + "/main_zone.tscn";
            string tempPath = worldPath + ".download";

            Directory.CreateDirectory(worldDir); // make the directory just in case.

            try
            {
                // without a timeout a stalled connection hangs startup forever
                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };

                // download first and only touch the disk once we have a good body. The old code created the
                // file before reading the response, so any failure left an empty main_zone.tscn behind.
                using var resp = await client.GetAsync(downloadURL);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(body))
                {
                    Error("The downloaded zone file was empty.");
                    return false;
                }

                // write to a temp file then move it into place, so a crash can't leave a half written file
                await File.WriteAllTextAsync(tempPath, body, new UTF8Encoding(false));
                File.Move(tempPath, worldPath, true);

                Log("zone file downloaded!");
                return true;
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
            {
                Error(e.ToString());
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return false;
            }
        }
        
        void Log(string value)
        {
            logger.Information(value);
        }

        void Error(string value)
        {
            logger.Error(value);
        }
        
        
    }
}
