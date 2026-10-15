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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Steamworks;
namespace Cove.Server.HostedServices
{
    public class HostSpawnMetalService : IHostedService, IDisposable
    {
        private readonly ILogger<HostSpawnMetalService> _logger;
        private Timer _timer;
        private CoveServer server;

        public HostSpawnMetalService(ILogger<HostSpawnMetalService> logger, CoveServer server)
        {
            _logger = logger;
            this.server = server;
        }

        // This method is called when the service is starting.
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Host_Spawn_Metal_Service is up.");

            // Setup a timer to trigger the task periodically.
            _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(8));

            return Task.CompletedTask;
        }

        // This is the method that will be triggered periodically by the timer.
        private void DoWork(object state)
        {
            try
            {
                // still got no idea
                //server.gameLobby.SetData("server_browser_value", "0");
                SteamMatchmaking.SetLobbyData(server.SteamLobby, "server_browser_value", "0");

                int metalCount;
                lock (server.stateLock) // the list is modified from other threads
                    metalCount = server.serverOwnedInstances.FindAll(a => a.Type == "metal_spawn").Count;
                if (metalCount > 7)
                    return;

                if (server.shouldSpawnMetal)
                    server.spawnMetal();
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
            }
        }

        // This method is called when the service is stopping.
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("HostSpawnMetalService is stopping.");

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
