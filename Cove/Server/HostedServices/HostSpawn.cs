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
using System.Threading;
using System.Threading.Tasks;
using Cove.GodotFormat;
using Cove.Server.Actor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Steamworks;



namespace Cove.Server.HostedServices
{
    public class HostSpawnService : IHostedService, IDisposable
    {
        private readonly ILogger<HostSpawnService> _logger;
        private Timer _timer;
        private CoveServer server;

        public HostSpawnService(ILogger<HostSpawnService> logger, CoveServer server)
        {
            _logger = logger;
            this.server = server;
        }

        // This method is called when the service is starting.
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Host_Spawn_Service is up.");

            _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

            return Task.CompletedTask;
        }

        private float rainChance = 0f;

        private int running = 0;

        // Lobby owner has to be wrong on several checks in a row before we shut down. A single read taken while Steam
        // is briefly reconnecting used to be enough to take the whole server down.
        private int ownerMismatches = 0;
        private const int OwnerMismatchesBeforeStop = 3;

        // This is the method that will be triggered periodically by the timer.
        private void DoWork(object state)
        {
            if (Interlocked.Exchange(ref running, 1) != 0)
                return; // previous tick still running

            try
            {
                DoWorkInner();
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
            }
            finally
            {
                Volatile.Write(ref running, 0);
            }
        }

        private void DoWorkInner()
        {
            // check that the host of the lobby is still the server
            if (SteamMatchmaking.GetLobbyOwner(server.SteamLobby).m_SteamID != server.serverPlayer.SteamId.m_SteamID)
            {
                ownerMismatches++;
                if (ownerMismatches >= OwnerMismatchesBeforeStop)
                {
                    // somthing has gone wrong, the server is no longer the host of the lobby
                    server.logger.Fatal("The server is no longer the host of the lobby, shutting down.");
                    server.logger.Fatal("Make sure you have a good connection to steam, this happends when the server disconnects from steam");
                    server.logger.Fatal("If your internet is unstable, this will happen often!");

                    // stop the server. Exit code 2 (not 0) so run-server.bat knows this wasn't a deliberate stop and
                    // starts it again, the server can't host anymore once it has lost its Steam connection.
                    server.Stop(2);
                    return;
                }
            }
            else
            {
                ownerMismatches = 0;
            }

            // remove old instances!
            try
            {

                // if we are in a lobby, update the player count
                if (server.SteamLobby.m_SteamID != 0)
                {
                    ulong epoch = (ulong)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                    SteamMatchmaking.SetLobbyData(server.SteamLobby, "timestamp", epoch.ToString());
                }

                lock (server.serverActorListLock)
                {
                    foreach (WFActor inst in server.serverOwnedInstances.ToList())
                    {
                        float instanceAge = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - inst.SpawnTime.ToUnixTimeSeconds();
                        if (inst.despawn && instanceAge >= inst.despawnTime)
                        {
                            server.removeServerActor(inst);
                        }
                    }
                }

                Random ran = new Random();
                string[] beginningTypes = ["fish", "none"];
                string type = beginningTypes[ran.Next() % 2];

                if (ran.NextSingle() < 0.01 && ran.NextSingle() < 0.4)
                {
                    if (server.shouldSpawnMeteor)
                        type = "meteor";
                }

                if (ran.NextSingle() < rainChance && ran.NextSingle() < .12f)
                {
                    type = "rain";
                    rainChance = 0;
                }
                else
                {
                    if (ran.NextSingle() < .75f)
                        rainChance += .001f * server.rainMultiplyer;
                }

                if (ran.NextSingle() < 0.01 && ran.NextSingle() < 0.25)
                {
                    if (server.shouldSpawnPortal)
                        type = "void_portal";
                }

                switch (type)
                {

                    case "none":
                        break;

                    case "fish":
                        // dont spawn too many because it WILL LAG players!
                        int ownedCount;
                        lock (server.stateLock)
                            ownedCount = server.serverOwnedInstances.Count;
                        if (ownedCount > 15)
                            return;
                        WFActor a = server.spawnFish();
                        break;

                    case "meteor":
                        server.spawnFish("fish_spawn_alien");
                        break;

                    case "rain":
                        server.spawnRainCloud();
                        break;

                    case "void_portal":
                        server.spawnVoidPortal();
                        break;

                }

                // random neumber between 0 and 2 for 3 values
                int random = ran.Next() % 3;
                if (random == 0)
                {
                    spawnBirds();
                }

            }
            catch (Exception e)
            {
                // most of the time this is just going to be an error 
                // because the list was modified while iterating
                // casued by a actorspawn or despawn, nothing huge.
                _logger.LogError(e.ToString());
            }
        }

        void spawnBirds()
        {
            int birdCount;
            lock (server.stateLock) // allActors is modified from the network and callback threads
                birdCount = server.allActors.FindAll(a => a.Type == "ambient_bird").Count;
            if (birdCount > 8)
                return;

            if (server.trash_points == null || server.trash_points.Count == 0)
                return;

            Random ran = new Random();
            int count = ran.Next() % 3 + 1;

            int randomRange(float min, float max)
            {
                return ran.Next() % (int)(max - min) + (int)min;
            }

            Vector3 point = server.trash_points[ran.Next() % server.trash_points.Count];

            for (int i = 0; i < count; i++)
            {
                Vector3 pos = point + new Vector3(randomRange((float)-2.5,(float)2.5), 0, randomRange((float)-2.5, (float)2.5));
                WFActor a = server.spawnGenericActor("ambient_bird", point);
                a.despawnTime = 60;
            }

        }

        // This method is called when the service is stopping.
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("HostSpawnService is stopping.");

            // Stop the timer and dispose of it.
            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

        // This method is called to dispose of the resources.
        public void Dispose()
        {
            _timer?.Dispose();
        }
    }

}
