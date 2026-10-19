using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Cove.Server
{
    /// <summary>
    /// Crash Watch: a flight recorder for the server, everything goes into the "crashwatch" folder next to it.
    ///
    ///  - blackbox.bin  the last few thousand log lines and notes, plus what every loop (network, Steam callbacks,
    ///                  the 12 Hz tick, console) is doing at this moment. It is a memory mapped file, so Windows keeps
    ///                  what was written even when the process dies in a way .NET can't catch (stack overflow, a crash
    ///                  inside steam_api64.dll, the process being killed).
    ///  - stats.csv     memory, threads, handles, CPU, players, actors, packets and errors, every 30 seconds, to spot
    ///                  leaks and storms that build up before a crash.
    ///  - crash-*.txt   written the moment an unhandled exception happens, or at the next start when the last run
    ///                  ended without a clean stop (with the exit code run-server.bat recorded and what it means).
    ///  - hang-*.txt    when a loop has been stuck on the same thing for too long (a freeze, a deadlock), with a
    ///                  memory dump in dumps/ and, if set, an automatic restart.
    /// Settings are in crashwatch.txt.
    /// </summary>
    public static class CrashWatch
    {
        private const string FolderName = "crashwatch";
        private const string SettingsFileName = "crashwatch.txt";

        // blackbox.bin layout: [header][one slot per loop][ring of recent lines]
        private const int HeaderSize = 64;
        private const int SlotSize = 256;
        private const int LoopSlots = 8;
        private const int RingSlots = 4000;
        private const long FileSize = HeaderSize + (long)(LoopSlots + RingSlots) * SlotSize;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CWBOX001");

        private const int ReportLines = 300;
        private const int StatsKept = 40;
        private const int ReportsKept = 30;
        private const int DumpsKept = 3;

        // settings
        private static int hangSeconds = 30;
        private static int restartAfterHangSeconds = 120;
        private static bool dumpOnHang = true;
        private static int statsEverySeconds = 30;

        private static string folder = "";
        private static string flagPath = "";
        private static MemoryMappedFile? map;
        private static MemoryMappedViewAccessor? view;
        private static readonly object writeLock = new();
        private static long nextLine;
        private static bool started;
        private static DateTime startedAt = DateTime.Now;

        private sealed class Loop
        {
            public string Name = "";
            public int Index;
            public string Doing = "starting";
            public long Since = Environment.TickCount64;
            public long LastAlive = Environment.TickCount64;
            public bool Hung;
            public long HungSince;
        }

        private static readonly ConcurrentDictionary<string, Loop> loops = new();
        private static int loopCount;

        private static readonly ConcurrentDictionary<string, long> packetCounts = new();
        private static readonly ConcurrentDictionary<string, long> exceptionCounts = new();
        private static long packetsTotal;
        private static long errorsTotal;
        private static long exceptionsTotal;

        private static readonly LinkedList<string> recentStats = new();
        private const string StatsColumns = "Time,UptimeMin,WorkingSetMB,GcHeapMB,Gen2,Threads,Handles,CpuPercent,Players,Actors,ServerActors,ChalkCells,Packets,Errors,Exceptions";
        private static bool crashReported;
        private static Timer? statsTimer;
        private static Thread? watchdog;
        private static TimeSpan lastCpu;
        private static long lastCpuTick;
        private static long lastPackets;
        private static long lastErrors;
        private static long lastExceptions;

        /// <summary>The server, for player and actor counts in the stats. Optional.</summary>
        public static CoveServer? Server;

        // ---- starting and stopping ----

        public static void Start()
        {
            if (started)
                return;
            started = true;
            startedAt = DateTime.Now;

            try
            {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                folder = Path.Combine(root, FolderName);
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(Path.Combine(folder, "dumps"));
                flagPath = Path.Combine(folder, "running.flag");

                ReadSettings(Path.Combine(root, SettingsFileName));

                // the last run never said goodbye: find out what it was doing
                if (File.Exists(flagPath))
                    ReportPreviousRun();

                OpenBlackbox();
                File.WriteAllText(flagPath, $"started {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid {Environment.ProcessId}");
                Prune("crash-*.txt", ReportsKept);
                Prune("hang-*.txt", ReportsKept);

                AppDomain.CurrentDomain.FirstChanceException += (sender, args) =>
                {
                    // runs for every exception thrown, even caught ones: only count them, it must be cheap and never throw
                    Interlocked.Increment(ref exceptionsTotal);
                    exceptionCounts.AddOrUpdate(args.Exception.GetType().Name, 1, (_, v) => v + 1);
                };

                using (Process me = Process.GetCurrentProcess())
                    lastCpu = me.TotalProcessorTime;
                lastCpuTick = Environment.TickCount64;
                statsTimer = new Timer(_ => WriteStats(), null, TimeSpan.FromSeconds(statsEverySeconds), TimeSpan.FromSeconds(statsEverySeconds));

                watchdog = new Thread(Watch) { IsBackground = true, Name = "Crash Watch" };
                watchdog.Start();

                Note("watch", $"Crash Watch started, pid {Environment.ProcessId}, .NET {Environment.Version}, {RuntimeInformation.OSDescription}");
            }
            catch (Exception e)
            {
                // the watch is a helper, it must never stop the server from starting
                Console.WriteLine($"Crash Watch could not start: {e.Message}");
            }
        }

        /// <summary>A normal stop: the next start won't report it as a crash.</summary>
        public static void CleanExit()
        {
            try
            {
                Note("watch", "Clean stop.");
                FlushBlackbox();
                if (flagPath.Length > 0 && File.Exists(flagPath))
                    File.Delete(flagPath);
            }
            catch { }
        }

        /// <summary>The server is stopping because something went wrong (exit code not 0).</summary>
        public static void FailedExit(int exitCode)
        {
            if (crashReported)
                return; // the unhandled exception handler already wrote one
            ReportCrash(null, $"The server stopped itself with exit code {exitCode}. The last lines below say why.");
        }

        // ---- what the server is doing ----

        /// <summary>Remembers one line in the blackbox (the log lines arrive here through CrashWatchSink).</summary>
        public static void Note(string tag, string text)
        {
            if (view == null)
                return;
            try
            {
                string[] lines = text.Replace("\r", "").Split('\n');
                int max = Math.Min(lines.Length, 14); // a stack trace is useful, a wall of it would push everything else out
                for (int i = 0; i < max; i++)
                {
                    if (lines[i].Length == 0)
                        continue;
                    string line = i == 0
                        ? $"{DateTime.Now:HH:mm:ss.fff} t{Environment.CurrentManagedThreadId,-3} [{tag}] {lines[i]}"
                        : $"                 [{tag}]   {lines[i]}";
                    WriteLine(line);
                }
                if (lines.Length > max)
                    WriteLine($"                 [{tag}]   ... {lines.Length - max} more lines in the log file");
            }
            catch { }
        }

        /// <summary>A loop is about to do something that should take a moment, like handling one packet.</summary>
        public static void Busy(string loop, string what)
        {
            Loop l = Get(loop);
            long now = Environment.TickCount64;
            l.Doing = what;
            Interlocked.Exchange(ref l.Since, now);
            Interlocked.Exchange(ref l.LastAlive, now);
            WriteLoopSlot(l);
        }

        /// <summary>The loop finished what it was doing.</summary>
        public static void Idle(string loop)
        {
            Loop l = Get(loop);
            long now = Environment.TickCount64;
            if (l.Doing != "idle")
            {
                l.Doing = "idle";
                WriteLoopSlot(l);
            }
            Interlocked.Exchange(ref l.Since, now);
            Interlocked.Exchange(ref l.LastAlive, now);
        }

        public static void CountPacket(string type)
        {
            Interlocked.Increment(ref packetsTotal);
            packetCounts.AddOrUpdate(type, 1, (_, v) => v + 1);
        }

        public static void CountError() => Interlocked.Increment(ref errorsTotal);

        private static Loop Get(string name)
        {
            return loops.GetOrAdd(name, n =>
            {
                int index = Interlocked.Increment(ref loopCount) - 1;
                return new Loop { Name = n, Index = index };
            });
        }

        // ---- the blackbox file ----

        private static void OpenBlackbox()
        {
            string path = Path.Combine(folder, "blackbox.bin");
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
                file.SetLength(FileSize);
            map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, FileSize, MemoryMappedFileAccess.ReadWrite);
            view = map.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.ReadWrite);
            view.WriteArray(0, Magic, 0, Magic.Length);
            view.Write(8, 0L);
            view.Write(24, DateTime.UtcNow.Ticks);
            view.Write(32, (long)Environment.ProcessId);
            Heartbeat();
        }

        private static void Heartbeat()
        {
            try
            {
                view?.Write(16, DateTime.UtcNow.Ticks);
            }
            catch { }
        }

        private static void FlushBlackbox()
        {
            try
            {
                view?.Flush();
            }
            catch { }
        }

        private static void WriteLine(string line)
        {
            if (view == null)
                return;
            lock (writeLock)
            {
                long index = nextLine++;
                WriteSlot(HeaderSize + (long)(LoopSlots + index % RingSlots) * SlotSize, line);
                view.Write(8, nextLine);
            }
        }

        private static void WriteLoopSlot(Loop l)
        {
            if (view == null || l.Index >= LoopSlots)
                return;
            try
            {
                string text = $"{l.Name}|{DateTime.UtcNow.Ticks}|{l.Doing}";
                lock (writeLock)
                    WriteSlot(HeaderSize + (long)l.Index * SlotSize, text);
            }
            catch { }
        }

        // a slot: 2 bytes length, then the text as UTF-8
        private static void WriteSlot(long offset, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int length = Math.Min(bytes.Length, SlotSize - 2);
            view!.Write(offset, (ushort)length);
            view.WriteArray(offset + 2, bytes, 0, length);
        }

        private static string ReadSlot(byte[] data, long offset)
        {
            if (offset + 2 > data.Length)
                return "";
            int length = BitConverter.ToUInt16(data, (int)offset);
            length = Math.Min(length, SlotSize - 2);
            if (offset + 2 + length > data.Length)
                return "";
            return Encoding.UTF8.GetString(data, (int)offset + 2, length);
        }

        private static List<string> RecentLines(byte[] data, int count)
        {
            var lines = new List<string>();
            long next = BitConverter.ToInt64(data, 8);
            long first = Math.Max(0, Math.Max(next - RingSlots, next - count));
            for (long i = first; i < next; i++)
                lines.Add(ReadSlot(data, HeaderSize + (LoopSlots + i % RingSlots) * SlotSize));
            return lines;
        }

        private static List<string> LoopLines(byte[] data, DateTime endedUtc)
        {
            var lines = new List<string>();
            for (int i = 0; i < LoopSlots; i++)
            {
                string slot = ReadSlot(data, HeaderSize + (long)i * SlotSize);
                string[] bits = slot.Split('|', 3);
                if (bits.Length < 3 || !long.TryParse(bits[1], out long ticks))
                    continue;
                double seconds = (endedUtc - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
                lines.Add($"  {bits[0],-10} {bits[2]}   (for {Math.Max(0, seconds):0.0} s)");
            }
            return lines;
        }

        private static byte[] Snapshot()
        {
            var data = new byte[FileSize];
            if (view != null)
                lock (writeLock)
                    view.ReadArray(0, data, 0, data.Length);
            return data;
        }

        // ---- the watchdog ----

        private static void Watch()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                    Heartbeat();
                    CheckLoops();
                }
                catch (Exception e)
                {
                    Note("watch", "Watchdog error: " + e.Message);
                }
            }
        }

        private static void CheckLoops()
        {
            long now = Environment.TickCount64;
            long limit = hangSeconds * 1000L;
            var stuck = new List<Loop>();

            foreach (Loop l in loops.Values)
            {
                long busyFor = l.Doing == "idle" ? 0 : now - Interlocked.Read(ref l.Since);
                long silentFor = now - Interlocked.Read(ref l.LastAlive);
                bool hung = busyFor > limit || silentFor > limit;

                if (hung && !l.Hung)
                {
                    l.Hung = true;
                    l.HungSince = now;
                    stuck.Add(l);
                }
                else if (!hung && l.Hung)
                {
                    l.Hung = false;
                    Note("watch", $"The {l.Name} loop is moving again after {(now - l.HungSince) / 1000 + hangSeconds} s.");
                }
            }

            if (stuck.Count > 0)
                ReportHang(stuck);

            // still stuck long after the report: restart, the server is no use to anyone like this
            if (restartAfterHangSeconds > 0)
            {
                foreach (Loop l in loops.Values)
                {
                    if (l.Hung && now - l.HungSince > restartAfterHangSeconds * 1000L)
                    {
                        Note("watch", $"The {l.Name} loop has been stuck for {(now - l.HungSince) / 1000 + hangSeconds} s, restarting the server.");
                        try
                        {
                            File.WriteAllText(flagPath, $"restarted by Crash Watch after a hang at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        }
                        catch { }
                        FlushBlackbox();
                        // a normal stop could wait forever on the same stuck lock, so end the process right away.
                        // run-server.bat sees the non-zero exit code and starts the server again.
                        Process.GetCurrentProcess().Kill();
                    }
                }
            }
        }

        private static void ReportHang(List<Loop> stuck)
        {
            var report = new StringBuilder();
            report.AppendLine("CRASH WATCH: HANG");
            report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   Up for: {Uptime()}");
            report.AppendLine();
            report.AppendLine("A loop has been stuck on the same thing for too long. If one loop is \"waiting for the state lock\",");
            report.AppendLine("look at which other loop is busy with something: that one is holding the lock.");
            report.AppendLine();
            report.AppendLine("Stuck:");
            long now = Environment.TickCount64;
            foreach (Loop l in stuck)
            {
                long busy = now - Interlocked.Read(ref l.Since);
                long silent = now - Interlocked.Read(ref l.LastAlive);
                report.AppendLine(l.Doing == "idle"
                    ? $"  {l.Name}: has not run at all for {silent / 1000} s"
                    : $"  {l.Name}: {l.Doing}   (for {busy / 1000} s)");
            }
            AppendCurrentState(report);

            string dump = "";
            if (dumpOnHang)
                dump = WriteDump("hang");
            report.AppendLine();
            report.AppendLine(dump.Length > 0
                ? $"Memory dump: {dump}  (open it with Visual Studio or \"dotnet-dump analyze\", then \"clrstack -all\" shows every thread)"
                : "No memory dump was written (dumpOnHang is off, or createdump was not found).");
            if (restartAfterHangSeconds > 0)
                report.AppendLine($"If it is still stuck {restartAfterHangSeconds} s from now, Crash Watch restarts the server.");

            string path = Path.Combine(folder, $"hang-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            WriteFile(path, report.ToString());
            Note("watch", $"Hang detected, report written to {Path.GetFileName(path)}");
            Server?.logger?.Warning($"Crash Watch: a server loop is stuck, see crashwatch/{Path.GetFileName(path)}");
        }

        // ---- crash reports ----

        /// <summary>Writes a crash report right now. Safe to call from the unhandled exception handler.</summary>
        public static void ReportCrash(Exception? exception, string? reason = null)
        {
            if (folder.Length == 0)
                return;
            crashReported = true;
            try
            {
                var report = new StringBuilder();
                report.AppendLine("CRASH WATCH: CRASH");
                report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   Up for: {Uptime()}");
                report.AppendLine();
                if (reason != null)
                    report.AppendLine(reason);
                if (exception != null)
                {
                    report.AppendLine($"Unhandled exception on thread \"{Thread.CurrentThread.Name ?? "?"}\" ({Environment.CurrentManagedThreadId}):");
                    report.AppendLine(exception.ToString());
                    report.AppendLine();
                    report.AppendLine(ExplainException(exception));
                }
                AppendCurrentState(report);

                string path = Path.Combine(folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                WriteFile(path, report.ToString());
                File.WriteAllText(flagPath, $"crash reported in {Path.GetFileName(path)}");
                FlushBlackbox();
            }
            catch { }
        }

        private static void AppendCurrentState(StringBuilder report)
        {
            byte[] data = Snapshot();

            report.AppendLine();
            report.AppendLine("What every loop was doing:");
            foreach (string line in LoopLines(data, DateTime.UtcNow))
                report.AppendLine(line);

            report.AppendLine();
            report.AppendLine(StatsNow());
            List<string> stats;
            lock (recentStats)
                stats = recentStats.ToList();
            AppendStatsHistory(report, stats);

            report.AppendLine();
            report.AppendLine("Packets by type since the start: " + Top(packetCounts, 12));
            report.AppendLine("Exceptions thrown (caught or not) since the start: " + Top(exceptionCounts, 12));
            report.AppendLine(PlayersAndPlugins());

            report.AppendLine();
            report.AppendLine($"Last {ReportLines} lines:");
            foreach (string line in RecentLines(data, ReportLines))
                report.AppendLine(line);
        }

        // At the start: the last run left its flag behind, so it ended without a clean stop.
        private static void ReportPreviousRun()
        {
            string flag = File.ReadAllText(flagPath).Trim();
            File.Delete(flagPath);
            if (flag.StartsWith("crash reported"))
                return; // that one already has its report

            var report = new StringBuilder();
            bool restarted = flag.StartsWith("restarted by Crash Watch");
            report.AppendLine(restarted ? "CRASH WATCH: RESTARTED AFTER A HANG" : "CRASH WATCH: THE LAST RUN ENDED WITHOUT WARNING");
            report.AppendLine($"Found at the start of the next run: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Last run: {flag}");
            report.AppendLine();
            if (!restarted)
            {
                report.AppendLine("The server ended without going through its normal stop and without an exception .NET could catch.");
                report.AppendLine("That leaves: a stack overflow, a crash in native code (steam_api64.dll), running out of memory,");
                report.AppendLine("the window being closed, or the process being killed. The exit code below tells them apart.");
            }

            DateTime lastRunStart = DateTime.MinValue;
            string blackbox = Path.Combine(folder, "blackbox.bin");
            byte[]? data = null;
            if (File.Exists(blackbox))
            {
                data = File.ReadAllBytes(blackbox);
                if (data.Length >= FileSize && data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                {
                    lastRunStart = new DateTime(BitConverter.ToInt64(data, 24), DateTimeKind.Utc).ToLocalTime();
                    var lastAlive = new DateTime(BitConverter.ToInt64(data, 16), DateTimeKind.Utc);
                    report.AppendLine();
                    report.AppendLine($"Last sign of life: {lastAlive.ToLocalTime():yyyy-MM-dd HH:mm:ss} (the watchdog writes one every second)");
                    report.AppendLine();
                    report.AppendLine("What every loop was doing at that moment:");
                    foreach (string line in LoopLines(data, lastAlive))
                        report.AppendLine(line);
                }
                else
                {
                    data = null;
                }
            }

            // what run-server.bat saw when the process ended
            string exitFile = Path.Combine(folder, "last-exit.txt");
            if (File.Exists(exitFile) && File.GetLastWriteTime(exitFile) >= lastRunStart)
            {
                string exit = File.ReadAllText(exitFile).Trim();
                report.AppendLine();
                report.AppendLine($"run-server.bat: {exit}");
                int at = exit.IndexOf("code ");
                if (at >= 0 && int.TryParse(exit.Substring(at + 5).Trim().Split(' ')[0], out int code))
                    report.AppendLine("Meaning: " + ExplainExitCode(code));
            }
            else
            {
                report.AppendLine();
                report.AppendLine("No exit code recorded (start the server with run-server.bat to get one).");
            }

            string eventsFile = Path.Combine(folder, "last-events.txt");
            if (File.Exists(eventsFile) && File.GetLastWriteTime(eventsFile) >= lastRunStart)
            {
                string events = File.ReadAllText(eventsFile).Trim();
                if (events.Length > 0)
                {
                    report.AppendLine();
                    report.AppendLine("Windows event log (Application) around the crash:");
                    report.AppendLine(events.Length > 6000 ? events.Substring(0, 6000) + "\n..." : events);
                }
            }

            string? dump = NewestDump(lastRunStart);
            if (dump != null)
            {
                report.AppendLine();
                report.AppendLine($"Memory dump from the crash: dumps/{Path.GetFileName(dump)}");
            }

            AppendStatsHistory(report, TailStats(StatsKept));

            if (data != null)
            {
                report.AppendLine();
                report.AppendLine($"Last {ReportLines} lines:");
                foreach (string line in RecentLines(data, ReportLines))
                    report.AppendLine(line);
            }

            string path = Path.Combine(folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            WriteFile(path, report.ToString());
            Console.WriteLine($"Crash Watch: the last run did not stop cleanly, see crashwatch/{Path.GetFileName(path)}");
        }

        private static string ExplainExitCode(int code)
        {
            return unchecked((uint)code) switch
            {
                0 => "a normal stop.",
                1 => "the server stopped itself after a fatal error (the crash report written at that moment has the details).",
                0xFFFFFFFF => "the process was killed (by Crash Watch after a hang, by Task Manager, or by Windows).",
                0xC00000FD => "STACK OVERFLOW: endless recursion somewhere, the thread that overflowed is in the dump.",
                0xC0000005 => "ACCESS VIOLATION in native code: almost always inside steam_api64.dll (Steam restarted, logged out or updated), rarely corrupted memory.",
                0xC0000374 => "HEAP CORRUPTION in native code (steam_api64.dll or another native library).",
                0xC0000409 => "FAIL FAST: .NET ended the process on purpose (Environment.FailFast or a fatal runtime error).",
                0xE0434352 => "an unhandled .NET exception.",
                0x80131506 => ".NET runtime internal error (ExecutionEngineException), usually native code corrupting memory.",
                0xC000013A => "the console window was closed or Ctrl+C / Ctrl+Break was pressed.",
                0x8007000E or 0xE0000002 => "OUT OF MEMORY: check stats.csv for memory growing over time.",
                0xC0000094 => "integer division by zero in native code.",
                0xC000001D => "illegal instruction in native code.",
                _ => $"unknown (0x{unchecked((uint)code):X8}), search for that code online.",
            };
        }

        private static string ExplainException(Exception e)
        {
            Exception inner = e;
            while (inner.InnerException != null)
                inner = inner.InnerException;
            return inner switch
            {
                OutOfMemoryException => "Hint: out of memory, look at the WorkingSetMB and GcHeapMB columns in stats.csv.",
                InvalidOperationException ioe when ioe.Message.Contains("Collection was modified") =>
                    "Hint: a list was changed by one thread while another went through it, some code is missing a lock.",
                NullReferenceException => "Hint: something used a value that was null, the first line of the stack trace with Cove or a plugin name is where.",
                _ => "",
            };
        }

        // ---- stats ----

        private static void WriteStats()
        {
            try
            {
                string line = StatsLine();
                lock (recentStats)
                {
                    recentStats.AddLast(line);
                    while (recentStats.Count > StatsKept)
                        recentStats.RemoveFirst();
                }

                string path = Path.Combine(folder, "stats.csv");
                if (File.Exists(path) && new FileInfo(path).Length > 5_000_000)
                    File.Move(path, Path.Combine(folder, "stats-old.csv"), overwrite: true);
                if (!File.Exists(path))
                    File.WriteAllText(path, StatsColumns + Environment.NewLine);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception e)
            {
                Note("watch", "Could not write the stats: " + e.Message);
            }
        }

        private static string StatsLine()
        {
            using Process me = Process.GetCurrentProcess();
            long now = Environment.TickCount64;
            TimeSpan cpu = me.TotalProcessorTime;
            double cpuPercent = now > lastCpuTick
                ? (cpu - lastCpu).TotalMilliseconds / (now - lastCpuTick) / Environment.ProcessorCount * 100.0
                : 0;
            lastCpu = cpu;
            lastCpuTick = now;

            long packets = Interlocked.Read(ref packetsTotal);
            long errors = Interlocked.Read(ref errorsTotal);
            long exceptions = Interlocked.Read(ref exceptionsTotal);
            long newPackets = packets - lastPackets;
            long newErrors = errors - lastErrors;
            long newExceptions = exceptions - lastExceptions;
            lastPackets = packets;
            lastErrors = errors;
            lastExceptions = exceptions;

            int players = -1, actors = -1, serverActors = -1;
            long chalkCells = -1;
            try
            {
                if (Server != null)
                {
                    players = Server.AllPlayers.Count;
                    actors = Server.allActors.Count;
                    serverActors = Server.serverOwnedInstances.Count;
                    chalkCells = 0;
                    foreach (var canvas in Server.chalkCanvas.ToList())
                        chalkCells += canvas.chalkImage.Count;
                }
            }
            catch { }

            return string.Join(",",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ((int)(DateTime.Now - startedAt).TotalMinutes).ToString(),
                (me.WorkingSet64 / 1048576).ToString(),
                (GC.GetTotalMemory(false) / 1048576).ToString(),
                GC.CollectionCount(2).ToString(),
                me.Threads.Count.ToString(),
                me.HandleCount.ToString(),
                cpuPercent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                players.ToString(),
                actors.ToString(),
                serverActors.ToString(),
                chalkCells.ToString(),
                newPackets.ToString(),
                newErrors.ToString(),
                newExceptions.ToString());
        }

        private static string StatsNow()
        {
            using Process me = Process.GetCurrentProcess();
            return $"Now: memory {me.WorkingSet64 / 1048576} MB (GC heap {GC.GetTotalMemory(false) / 1048576} MB), " +
                   $"{me.Threads.Count} threads, {me.HandleCount} handles, " +
                   $"{Interlocked.Read(ref packetsTotal)} packets, {Interlocked.Read(ref errorsTotal)} errors logged, " +
                   $"{Interlocked.Read(ref exceptionsTotal)} exceptions thrown since the start";
        }

        private static void AppendStatsHistory(StringBuilder report, List<string> lines)
        {
            if (lines.Count == 0)
                return;
            report.AppendLine();
            report.AppendLine($"Stats, every {statsEverySeconds} s (the counts are per period). Look for memory, threads or handles that only grow:");
            report.AppendLine("  " + StatsColumns);
            foreach (string line in lines)
                report.AppendLine("  " + line);
        }

        private static List<string> TailStats(int count)
        {
            string path = Path.Combine(folder, "stats.csv");
            if (!File.Exists(path))
                return new List<string>();
            return File.ReadLines(path).Skip(1).TakeLast(count).ToList();
        }

        private static string PlayersAndPlugins()
        {
            try
            {
                if (Server == null)
                    return "";
                string players = string.Join(", ", Server.AllPlayers.ToList().Select(p => $"{p.Username} ({p.SteamId.m_SteamID})"));
                string plugins = string.Join(", ", Server.loadedPlugins.ToList().Select(p => p.pluginName));
                return $"Players online: {(players.Length > 0 ? players : "none")}\nPlugins: {(plugins.Length > 0 ? plugins : "none")}";
            }
            catch
            {
                return "";
            }
        }

        private static string Top(ConcurrentDictionary<string, long> counts, int count)
        {
            var top = counts.ToArray().OrderByDescending(p => p.Value).Take(count).Select(p => $"{p.Key} {p.Value}").ToList();
            return top.Count > 0 ? string.Join(", ", top) : "none";
        }

        private static string Uptime()
        {
            TimeSpan up = DateTime.Now - startedAt;
            return $"{(int)up.TotalHours} h {up.Minutes} min";
        }

        // ---- memory dumps ----

        /// <summary>
        /// A full memory dump of the server, written by createdump (it comes with the .NET runtime). It holds the stack of
        /// every thread and every object, so it shows exactly where a stuck thread is waiting.
        /// </summary>
        private static string WriteDump(string why)
        {
            try
            {
                string exe = OperatingSystem.IsWindows() ? "createdump.exe" : "createdump";
                string createdump = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), exe);
                if (!File.Exists(createdump))
                {
                    Note("watch", $"No memory dump: {createdump} not found.");
                    return "";
                }

                string file = Path.Combine(folder, "dumps", $"{why}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
                var start = new ProcessStartInfo(createdump, $"--withheap -f \"{file}\" {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using Process? process = Process.Start(start);
                if (process == null)
                    return "";
                process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();
                process.WaitForExit(120_000);
                PruneDumps();
                return File.Exists(file) ? Path.Combine("dumps", Path.GetFileName(file)) : "";
            }
            catch (Exception e)
            {
                Note("watch", "Could not write a memory dump: " + e.Message);
                return "";
            }
        }

        private static string? NewestDump(DateTime since)
        {
            string dir = Path.Combine(folder, "dumps");
            if (!Directory.Exists(dir))
                return null;
            return new DirectoryInfo(dir).GetFiles("*.dmp")
                .Where(f => f.LastWriteTime >= since)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        private static void PruneDumps()
        {
            try
            {
                foreach (FileInfo old in new DirectoryInfo(Path.Combine(folder, "dumps")).GetFiles("*.dmp")
                             .OrderByDescending(f => f.LastWriteTime).Skip(DumpsKept))
                    old.Delete();
            }
            catch { }
        }

        private static void Prune(string pattern, int keep)
        {
            try
            {
                foreach (FileInfo old in new DirectoryInfo(folder).GetFiles(pattern).OrderByDescending(f => f.LastWriteTime).Skip(keep))
                    old.Delete();
            }
            catch { }
            PruneDumps();
        }

        private static void WriteFile(string path, string text)
        {
            try
            {
                File.WriteAllText(path, text);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Crash Watch could not write {path}: {e.Message}");
            }
        }

        /// <summary>For the !health command: one line about how the server is doing, and a snapshot report on disk.</summary>
        public static string Health(bool writeSnapshot)
        {
            string text = $"Up {Uptime()}. {StatsNow()}.";
            var stuck = loops.Values.Where(l => l.Hung).Select(l => l.Name).ToList();
            text += stuck.Count > 0 ? $" Stuck: {string.Join(", ", stuck)}." : " No loop is stuck.";

            if (writeSnapshot && folder.Length > 0)
            {
                var report = new StringBuilder();
                report.AppendLine("CRASH WATCH: SNAPSHOT (asked for with !health)");
                report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   Up for: {Uptime()}");
                AppendCurrentState(report);
                string path = Path.Combine(folder, $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                WriteFile(path, report.ToString());
                Prune("snapshot-*.txt", 10);
                text += $" Snapshot: crashwatch/{Path.GetFileName(path)}";
            }
            return text;
        }

        // ---- settings ----

        private static void ReadSettings(string path)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, string.Join(Environment.NewLine, new[]
                {
                    "# ======================================================================",
                    "#  Crash Watch",
                    "# ======================================================================",
                    "#",
                    "#  Records what the server is doing so a crash or a freeze can be traced.",
                    "#  Everything goes into the crashwatch folder:",
                    "#    crash-*.txt    a crash, written when it happens or at the next start",
                    "#    hang-*.txt     a loop stuck for hangSeconds (a freeze or deadlock)",
                    "#    stats.csv      memory, threads, players, packets ... over time",
                    "#    dumps/         memory dumps of crashes and hangs (the newest 3)",
                    "#  Admins can type !health for a quick check and a snapshot report.",
                    "#",
                    "#  hangSeconds              = how long a loop may be stuck before it counts",
                    "#                             as a hang, default 30",
                    "#  restartAfterHangSeconds  = restart the server when it is still stuck",
                    "#                             this long after the hang report, 0 = never",
                    "#                             (run-server.bat starts it again), default 120",
                    "#  dumpOnHang               = true or false, write a memory dump on a hang",
                    "#                             (a few hundred MB, only the newest 3 are kept)",
                    "#  statsEverySeconds        = how often a line goes into stats.csv, default 30",
                    "#",
                    "#  Changes are read when the server starts.",
                    "# ======================================================================",
                    "",
                    "hangSeconds = 30",
                    "restartAfterHangSeconds = 120",
                    "dumpOnHang = true",
                    "statsEverySeconds = 30",
                    "",
                }));
            }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;
                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                string key = line.Substring(0, separator).Trim().ToLowerInvariant();
                string value = line.Substring(separator + 1).Trim();
                switch (key)
                {
                    case "hangseconds":
                        if (int.TryParse(value, out int hang) && hang >= 5)
                            hangSeconds = Math.Min(hang, 3600);
                        break;
                    case "restartafterhangseconds":
                        if (int.TryParse(value, out int restart) && restart >= 0)
                            restartAfterHangSeconds = Math.Min(restart, 86400);
                        break;
                    case "dumponhang":
                        dumpOnHang = !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "statseveryseconds":
                        if (int.TryParse(value, out int every) && every >= 5)
                            statsEverySeconds = Math.Min(every, 3600);
                        break;
                }
            }
        }
    }

    /// <summary>Copies every log line into the Crash Watch blackbox.</summary>
    public sealed class CrashWatchSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            string tag = logEvent.Level switch
            {
                LogEventLevel.Fatal => "FTL",
                LogEventLevel.Error => "ERR",
                LogEventLevel.Warning => "WRN",
                LogEventLevel.Information => "INF",
                _ => "DBG",
            };
            if (logEvent.Level >= LogEventLevel.Error)
                CrashWatch.CountError();
            CrashWatch.Note(tag, logEvent.RenderMessage());
            if (logEvent.Exception != null)
                CrashWatch.Note(tag, logEvent.Exception.ToString());
        }
    }
}
