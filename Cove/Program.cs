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

using Cove.Server;
using Cove.Server.Actor;
using Steamworks;
using Serilog;


long epoch = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

// Crash Watch first, so even a crash during startup is recorded (and a crash of the last run gets its report now)
CrashWatch.Start();

// a second copy started from the same folder (twice-clicked, or the startup task while the server is up) would fight the
// running server over the Steam lobby and its files, so it stops here. Exit code 0, so run-server.bat doesn't restart it.
if (CrashWatch.AnotherServerRunning)
{
    Console.WriteLine("The server is already running from this folder, this copy won't start. Closing in 10 seconds.");
    Thread.Sleep(10_000);
    Environment.Exit(0);
}

var serverLogger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File($"logs/log-{epoch}.txt") // Daily rolling logs
    .WriteTo.Sink(new CrashWatchSink()) // every log line also goes into the Crash Watch blackbox
    .CreateLogger();

//Console.SetOut(new SerilogTextWriter(serverLogger));
//Console.SetError(new SerilogTextWriter(serverLogger, true));  

CoveServer webfishingServer = new CoveServer();
webfishingServer.logger = serverLogger;
CrashWatch.Server = webfishingServer;

void Log(string message)
{
    serverLogger.Information(message);
}

// Everything that can end the process is hooked up BEFORE the server starts. These used to be registered after
// Init(), so a crash during startup was never logged and never shut Steam down cleanly.
AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
{
    serverLogger.Fatal("Unhandled exception occured");
    serverLogger.Fatal(args.ExceptionObject.ToString());
    CrashWatch.ReportCrash(args.ExceptionObject as Exception, "The server crashed with an unhandled exception.");
    webfishingServer.Stop(1);
};

// exceptions from fire-and-forget tasks are not fatal, but they should never vanish silently
TaskScheduler.UnobservedTaskException += (sender, args) =>
{
    serverLogger.Error("Unobserved task exception");
    serverLogger.Error(args.Exception.ToString());
    args.SetObserved();
};

Console.CancelKeyPress += Console_CancelKeyPress;
void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true; // we exit ourselves once the shutdown has finished
    Log("Server closed from input");
    webfishingServer.Stop();
}

// `docker stop` / `systemctl stop` send SIGTERM, which previously killed the server without telling the lobby or Steam
using var sigTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        Log("Server received SIGTERM");
        webfishingServer.Stop();
    });

try
{
    serverLogger.Information("Starting server...");
    webfishingServer.Init(); // start the server
} catch (Exception e)
{
    serverLogger.Fatal("Error occored on main thread");
    serverLogger.Fatal(e.ToString());
    webfishingServer.Stop(1);
}

// Init() returns without starting anything when setup failed (Steam not running, missing world file ...).
// There is nothing to keep alive in that case, so exit instead of idling forever as a dead server.
if (webfishingServer.serverPlayer == null)
{
    serverLogger.Fatal("The server failed to start, see the messages above.");
    webfishingServer.Stop(1);
}

while (true)
{
    var input = Console.ReadLine();

    // ReadLine returns null when stdin is closed or not attached (docker without -i, systemd, nohup). The old
    // `continue` turned that into a loop burning an entire CPU core. The server doesn't need console input to run,
    // so just park this thread, the network/callback threads do all the work.
    if (input == null)
    {
        Thread.Sleep(Timeout.Infinite);
        continue;
    }

    if (string.IsNullOrWhiteSpace(input))
        continue;

    string command = input.Split(' ')[0];
    CrashWatch.Note("console", input);

    try
    {
        if (webfishingServer.DoseCommandExist(command))
        {
            string[] commandArgs = input.Split(' ').Skip(1).ToArray();
            webfishingServer.InvokeCommand(webfishingServer.serverPlayer, command, commandArgs);
        }
        else
            Log("Command not found!");
    }
    catch (Exception e)
    {
        // a typo in a console command must not take the server down
        serverLogger.Error($"Command \"{command}\" failed:");
        serverLogger.Error(e.ToString());
    }
}
