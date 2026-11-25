using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Chalk;
using Cove.Server.Plugins;

/// <summary>
/// The chalk history: a log of every change made to the canvases (who, when, where, old colour, new colour) kept for
/// 48 hours (historyHours in chalkobserver.txt), so the chalk of one player can be rolled back.
///
///   !chalkhistory [time]                                   who changed the chalk lately
///   !chalkrollback &lt;player|steamid|server&gt; &lt;time|all&gt; [0-3|all] [check]   put one player's chalk back
///
/// The log is kept in ChalkObserver/chalkhistory.bin (and the names in chalkhistory-names.txt) so it survives a restart.
/// </summary>
public partial class ChalkObserver
{
    private const string HistoryFileName = "chalkhistory.bin";
    private const string HistoryNamesFileName = "chalkhistory-names.txt";

    // "CLH1", written at the start of the file so a wrong file is not read as a log
    private const uint HistoryMagic = 0x31484C43;
    private const int HistoryRecordBytes = 37; // time 4, canvas 1, x 4, y 4, old 8, new 8, actor 8

    private const int DefaultHistoryHours = 48;
    private const int MaxHistoryHours = 168;
    private const int DefaultHistoryMaxEntries = 1_000_000;

    // rollbacks send the cells to the players in pieces of this size
    private const int RollbackChunk = 2000;

    private struct HistoryEntry
    {
        public uint Time; // unix seconds
        public byte Canvas;
        public float X, Y;
        public long Old, New;
        public ulong Actor; // Steam ID, 0 = the server itself (admin commands, plugins, a rollback)
    }

    private sealed class PendingBatch
    {
        public uint Time;
        public byte Canvas;
        public ulong Actor;
        public ChalkChange[] Changes = Array.Empty<ChalkChange>();
    }

    private readonly record struct CellKey(byte Canvas, float X, float Y);

    private sealed class CellState
    {
        public bool LastIsTarget;
        public long RunOld;
        public long LastNew;
    }

    // Changes arrive on the thread that changed the chalk and are only queued there. onUpdate moves them into the log.
    private readonly ConcurrentQueue<PendingBatch> pendingBatches = new();
    private readonly object drainLock = new();
    private readonly object historyLock = new();
    private readonly object historyFileLock = new();
    private readonly List<HistoryEntry> history = new();
    private List<HistoryEntry> unflushed = new();
    private readonly Dictionary<ulong, string> historyNames = new();
    private bool namesDirty;
    private long fileRecords;
    private bool historyRewriteNeeded;
    private int flushing;
    private bool historyHooked;
    private long nextHistoryFlushTick;
    private long nextHistoryPruneTick;
    private long lastHistoryCapLogTick;

    // read by the thread that changes the chalk, so they are not behind the main lock
    private volatile bool historyOn = true;
    private volatile int historyHours = DefaultHistoryHours;
    private volatile int historyMaxEntries = DefaultHistoryMaxEntries;

    private string historyPath = "";
    private string historyNamesPath = "";

    private static uint NowUnix() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- setup and shutdown ----

    private void HistoryInit()
    {
        historyPath = Path.Combine(folderPath, HistoryFileName);
        historyNamesPath = Path.Combine(folderPath, HistoryNamesFileName);

        try
        {
            LoadHistoryNames();
            LoadHistory();
        }
        catch (Exception e)
        {
            Log($"The chalk history could not be loaded, starting with an empty one: {e.Message}");
        }

        if (!historyHooked)
        {
            ChalkCanvas.CellsChanged += OnChalkCellsChanged;
            historyHooked = true;
        }

        RegisterHistoryCommands();

        long now = Environment.TickCount64;
        nextHistoryFlushTick = now + 5_000;
        nextHistoryPruneTick = now + 60_000;
    }

    private void HistoryEnd()
    {
        if (historyHooked)
        {
            ChalkCanvas.CellsChanged -= OnChalkCellsChanged;
            historyHooked = false;
        }

        ParentServer.UnregisterCommand("chalkhistory");
        ParentServer.UnregisterCommand("chalkrollback");

        try
        {
            DrainPending();
            FlushHistory(final: true);
        }
        catch (Exception e)
        {
            Log($"Could not save the chalk history when stopping: {e.Message}");
        }
    }

    // called by the server 12 times a second, must stay quick: the disk work runs on another thread
    private void HistoryUpdate()
    {
        if (!historyHooked)
            return;

        DrainPending();

        long now = Environment.TickCount64;

        if (now >= nextHistoryPruneTick)
        {
            nextHistoryPruneTick = now + 60_000;
            PruneHistory();
        }

        if (now >= nextHistoryFlushTick)
        {
            nextHistoryFlushTick = now + 5_000;

            bool work;
            lock (historyLock)
                work = unflushed.Count > 0 || namesDirty || historyRewriteNeeded;

            if (work && Interlocked.CompareExchange(ref flushing, 1, 0) == 0)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        FlushHistory(final: false);
                    }
                    catch (Exception e)
                    {
                        Log($"Could not save the chalk history: {e.Message}");
                    }
                    finally
                    {
                        Volatile.Write(ref flushing, 0);
                    }
                });
            }
        }
    }

    // Existing chalkobserver.txt files get the new settings explained once, the defaults apply without any edit.
    private void AppendHistoryHelp()
    {
        if (!File.Exists(filePath))
            return; // a new file is written with the help included

        string text = File.ReadAllText(filePath);
        if (text.Contains("historyHours", StringComparison.OrdinalIgnoreCase))
            return;

        string add = string.Join(Environment.NewLine, new[]
        {
            "",
            "# ----- chalk history (added later) -----",
            "# history           = true or false. Keep a log of every chalk change (who, where, old and",
            "#                     new colour) so one player's chalk can be rolled back with !chalkrollback.",
            "# historyHours      = how many hours of history to keep (1-168).",
            "# historyMaxEntries = the most changes kept in memory and on disk, the oldest go first.",
            "history = true",
            "historyHours = 48",
            "historyMaxEntries = 1000000",
            "",
        });

        File.AppendAllText(filePath, add);
        Log($"Added the chalk history settings to {FileName}.");
    }

    // ---- recording ----

    // Runs on whatever thread changed the chalk (the network thread, a command): only queue, never lock or write.
    private void OnChalkCellsChanged(long canvasId, ulong actor, ChalkChange[] changes)
    {
        if (!historyOn || canvasId < 0 || canvasId >= CanvasCount || changes.Length == 0)
            return;

        pendingBatches.Enqueue(new PendingBatch
        {
            Time = NowUnix(),
            Canvas = (byte)canvasId,
            Actor = actor,
            Changes = changes,
        });
    }

    // Moves the queued changes into the log. Anything that reads the log calls this first.
    private void DrainPending()
    {
        if (pendingBatches.IsEmpty)
            return;

        lock (drainLock)
        {
            var batches = new List<PendingBatch>();
            while (pendingBatches.TryDequeue(out PendingBatch? batch))
                batches.Add(batch);

            if (batches.Count == 0)
                return;

            // the names of the players who drew, best effort (the player list can change while we look)
            var seen = new Dictionary<ulong, string>();
            try
            {
                WFPlayer[] online = GetAllPlayers();
                foreach (ulong actor in batches.Select(b => b.Actor).Where(a => a != 0).Distinct())
                {
                    WFPlayer? player = online.FirstOrDefault(p => p.SteamId.m_SteamID == actor);
                    if (player != null && !string.IsNullOrWhiteSpace(player.Username))
                        seen[actor] = CleanName(player.Username);
                }
            }
            catch
            {
                // the names are only a convenience
            }

            lock (historyLock)
            {
                foreach (PendingBatch batch in batches)
                {
                    foreach (ChalkChange change in batch.Changes)
                    {
                        var entry = new HistoryEntry
                        {
                            Time = batch.Time,
                            Canvas = batch.Canvas,
                            X = change.X,
                            Y = change.Y,
                            Old = change.Old,
                            New = change.New,
                            Actor = batch.Actor,
                        };
                        history.Add(entry);
                        unflushed.Add(entry);
                    }
                }

                foreach (var pair in seen)
                {
                    if (!historyNames.TryGetValue(pair.Key, out string? known) || known != pair.Value)
                    {
                        historyNames[pair.Key] = pair.Value;
                        namesDirty = true;
                    }
                }
            }
        }
    }

    // Drops what is older than historyHours and, if the log is over its size limit, the oldest of the rest.
    private void PruneHistory()
    {
        lock (historyLock)
        {
            uint cutoff = NowUnix() - (uint)historyHours * 3600u;
            int expired = 0;
            while (expired < history.Count && history[expired].Time < cutoff)
                expired++;

            int over = Math.Max(0, history.Count - expired - historyMaxEntries);
            int remove = expired + over;

            if (over > 0 && Environment.TickCount64 - lastHistoryCapLogTick > 600_000)
            {
                lastHistoryCapLogTick = Environment.TickCount64;
                Log($"The chalk history is over historyMaxEntries ({historyMaxEntries}), the oldest {over} changes were dropped.");
            }

            if (remove > 0)
                history.RemoveRange(0, remove);

            // the file only grows by appending, rewrite it once a good part of it is dead
            if (fileRecords > history.Count * 2L + 10_000)
                historyRewriteNeeded = true;
        }
    }

    // ---- saving and loading ----

    private void FlushHistory(bool final)
    {
        lock (historyFileLock)
        {
            List<HistoryEntry>? append = null;
            HistoryEntry[]? snapshot = null;
            string[]? nameLines = null;

            lock (historyLock)
            {
                if (historyRewriteNeeded)
                {
                    // the snapshot holds everything, so nothing is left to append
                    snapshot = history.ToArray();
                    unflushed = new List<HistoryEntry>();
                    historyRewriteNeeded = false;
                }
                else if (unflushed.Count > 0)
                {
                    append = unflushed;
                    unflushed = new List<HistoryEntry>();
                }

                if (namesDirty)
                {
                    nameLines = historyNames.Select(p => p.Key + "\t" + p.Value).ToArray();
                    namesDirty = false;
                }
            }

            try
            {
                Directory.CreateDirectory(folderPath);

                if (snapshot != null)
                {
                    string temp = historyPath + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new BinaryWriter(stream))
                    {
                        writer.Write(HistoryMagic);
                        foreach (HistoryEntry entry in snapshot)
                            WriteRecord(writer, entry);
                    }

                    File.Move(temp, historyPath, overwrite: true);
                    fileRecords = snapshot.Length;
                }
                else if (append != null)
                {
                    bool fresh = !File.Exists(historyPath) || new FileInfo(historyPath).Length < 4;
                    using var stream = new FileStream(historyPath, fresh ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var writer = new BinaryWriter(stream);
                    if (fresh)
                    {
                        writer.Write(HistoryMagic);
                        fileRecords = 0;
                    }

                    foreach (HistoryEntry entry in append)
                        WriteRecord(writer, entry);
                    fileRecords += append.Count;
                }
            }
            catch
            {
                // keep what was not written for the next try
                lock (historyLock)
                {
                    if (append != null)
                        unflushed.InsertRange(0, append);
                    else if (snapshot != null)
                        historyRewriteNeeded = true;
                    if (nameLines != null)
                        namesDirty = true;
                }

                throw;
            }

            if (nameLines != null)
            {
                try
                {
                    File.WriteAllLines(historyNamesPath, nameLines);
                }
                catch (Exception e)
                {
                    lock (historyLock)
                        namesDirty = true;
                    Log($"Could not save {HistoryNamesFileName}: {e.Message}");
                }
            }
        }
    }

    private static void WriteRecord(BinaryWriter writer, HistoryEntry e)
    {
        writer.Write(e.Time);
        writer.Write(e.Canvas);
        writer.Write(e.X);
        writer.Write(e.Y);
        writer.Write(e.Old);
        writer.Write(e.New);
        writer.Write(e.Actor);
    }

    private void LoadHistory()
    {
        if (!File.Exists(historyPath))
        {
            Log("No chalk history yet, starting a new one.");
            return;
        }

        uint cutoff = NowUnix() - (uint)historyHours * 3600u;
        var loaded = new List<HistoryEntry>();
        long records = 0;
        bool badFile = false;

        using (var stream = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new BinaryReader(new BufferedStream(stream, 1 << 16)))
        {
            if (stream.Length < 4 || reader.ReadUInt32() != HistoryMagic)
            {
                badFile = true;
            }
            else
            {
                long count = (stream.Length - 4) / HistoryRecordBytes;
                for (long i = 0; i < count; i++)
                {
                    var entry = new HistoryEntry
                    {
                        Time = reader.ReadUInt32(),
                        Canvas = reader.ReadByte(),
                        X = reader.ReadSingle(),
                        Y = reader.ReadSingle(),
                        Old = reader.ReadInt64(),
                        New = reader.ReadInt64(),
                        Actor = reader.ReadUInt64(),
                    };
                    records++;

                    if (entry.Time >= cutoff && entry.Canvas < CanvasCount)
                        loaded.Add(entry);
                }
            }
        }

        if (badFile)
        {
            string aside = historyPath + ".bad";
            File.Move(historyPath, aside, overwrite: true);
            Log($"{HistoryFileName} is not a chalk history, it was renamed to {HistoryFileName}.bad and a new history is started.");
            return;
        }

        if (loaded.Count > historyMaxEntries)
            loaded.RemoveRange(0, loaded.Count - historyMaxEntries);

        lock (historyLock)
        {
            history.Clear();
            history.AddRange(loaded);
            fileRecords = records;
            // expired records (or a half written last record) are still in the file
            if (records != loaded.Count || (new FileInfo(historyPath).Length - 4) % HistoryRecordBytes != 0)
                historyRewriteNeeded = true;
        }

        Log($"Loaded the chalk history: {loaded.Count} changes from the last {historyHours} hours.");
    }

    private void LoadHistoryNames()
    {
        if (!File.Exists(historyNamesPath))
            return;

        lock (historyLock)
        {
            foreach (string line in File.ReadAllLines(historyNamesPath))
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && ulong.TryParse(line.AsSpan(0, tab), out ulong id))
                    historyNames[id] = line.Substring(tab + 1);
            }
        }
    }

    private static string CleanName(string name)
    {
        string clean = name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > 64 ? clean.Substring(0, 64) : clean;
    }

    // ---- commands ----

    private void RegisterHistoryCommands()
    {
        RegisterCommand(
            command: "chalkhistory",
            aliases: ["chalklog"],
            callback: (player, args) => RunSafe("!chalkhistory", player, () => RunHistory(player, args))
        );
        SetCommandDescription("chalkhistory", "Shows who changed the chalk lately (optional time like 30m, 2h, all)");
        SetCommandAccess("chalkhistory", CommandAccess.Admin);

        RegisterCommand(
            command: "chalkrollback",
            aliases: ["chalkundo"],
            callback: (player, args) => RunSafe("!chalkrollback", player, () => RunRollback(player, args))
        );
        SetCommandDescription("chalkrollback", "Puts one player's chalk back: <player|steamid|server> <time|all> [canvas] [check]");
        SetCommandAccess("chalkrollback", CommandAccess.Admin);
    }

    private void RunSafe(string name, WFPlayer player, Action run)
    {
        try
        {
            if (!IsPlayerAdmin(player))
                return; // same as the built-in admin commands, non-admins get no answer

            run();
        }
        catch (Exception e)
        {
            Log($"{name} failed: {e.Message}");
            SendPlayerChatMessage(player, "Something went wrong, see the server log.");
        }
    }

    private static readonly Regex TimePattern = new(@"^(\d{1,6})([mhd])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "30m", "2h", "1d" or "all" (the whole history). Returns the number of seconds, capped to the history length.
    private bool TryReadTime(string text, out uint seconds)
    {
        seconds = 0;
        uint max = (uint)historyHours * 3600u;

        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            seconds = max;
            return true;
        }

        Match m = TimePattern.Match(text);
        if (!m.Success || !uint.TryParse(m.Groups[1].Value, out uint amount) || amount == 0)
            return false;

        ulong unit = char.ToLowerInvariant(m.Groups[2].Value[0]) switch { 'm' => 60UL, 'h' => 3600UL, _ => 86400UL };
        seconds = (uint)Math.Min((ulong)amount * unit, max);
        return true;
    }

    private static bool IsTimeWord(string text) => text.Equals("all", StringComparison.OrdinalIgnoreCase) || TimePattern.IsMatch(text);

    private static bool TryReadCanvas(string text, out int canvas)
    {
        canvas = -1; // -1 = all
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;
        return int.TryParse(text, out canvas) && canvas >= 0 && canvas < CanvasCount;
    }

    private static string Ago(uint seconds)
    {
        if (seconds < 90) return $"{seconds}s";
        if (seconds < 5400) return $"{(seconds + 30) / 60}m";
        if (seconds < 172800) return $"{(seconds + 1800) / 3600}h";
        return $"{(seconds + 43200) / 86400}d";
    }

    private string NameOf(ulong actor)
    {
        if (actor == 0)
            return "server (admin commands and restores)";
        lock (historyLock)
            return historyNames.TryGetValue(actor, out string? name) ? $"{name} ({actor})" : actor.ToString();
    }

    private void RunHistory(WFPlayer player, string[] args)
    {
        uint seconds = 3600;
        if (args.Length > 0 && !TryReadTime(args[0], out seconds))
        {
            SendPlayerChatMessage(player, "Usage: !chalkhistory [time]  (time like 30m, 2h, 1d or all, default 1h)");
            return;
        }

        if (!historyOn)
        {
            SendPlayerChatMessage(player, "The chalk history is turned off (history = false in chalkobserver.txt).");
            return;
        }

        DrainPending();

        uint now = NowUnix();
        uint cutoff = now - seconds;
        var perActor = new Dictionary<ulong, (int Changes, int[] PerCanvas, uint Last)>();
        int total = 0;

        lock (historyLock)
        {
            foreach (HistoryEntry e in history)
            {
                if (e.Time < cutoff)
                    continue;

                if (!perActor.TryGetValue(e.Actor, out var row))
                    row = (0, new int[CanvasCount], 0);
                row.Changes++;
                row.PerCanvas[e.Canvas]++;
                row.Last = Math.Max(row.Last, e.Time);
                perActor[e.Actor] = row;
                total++;
            }
        }

        string window = seconds >= (uint)historyHours * 3600u ? $"the last {historyHours}h (all of the history)" : $"the last {Ago(seconds)}";
        if (total == 0)
        {
            SendPlayerChatMessage(player, $"No chalk changes in {window}.");
            return;
        }

        SendPlayerChatMessage(player, $"Chalk changes in {window}: {total} cells changed by {perActor.Count} source(s).");

        const int MaxRows = 8;
        foreach (var pair in perActor.OrderByDescending(p => p.Value.Changes).Take(MaxRows))
        {
            string canvases = string.Join(" ", Enumerable.Range(0, CanvasCount).Where(i => pair.Value.PerCanvas[i] > 0).Select(i => $"c{i}:{pair.Value.PerCanvas[i]}"));
            SendPlayerChatMessage(player, $"{NameOf(pair.Key)}: {pair.Value.Changes} cells ({canvases}), last {Ago(now - pair.Value.Last)} ago");
        }

        if (perActor.Count > MaxRows)
            SendPlayerChatMessage(player, $"... and {perActor.Count - MaxRows} more. Use a shorter time to narrow it down.");

        SendPlayerChatMessage(player, "Undo one with: !chalkrollback <name or steam id> <time> [canvas] [check]");
    }

    // Finds the player to roll back: "server", a Steam ID, an online player, or a name seen in the history.
    private bool TryFindActor(WFPlayer admin, string ident, out ulong actor)
    {
        actor = 0;
        ident = ident.Trim();

        if (ident.Equals("server", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Regex.IsMatch(ident, @"^\d{17}$") && ulong.TryParse(ident, out actor))
            return true;

        try
        {
            WFPlayer? online = ParentServer.GetPlayer(ident);
            if (online != null)
            {
                actor = online.SteamId.m_SteamID;
                return true;
            }
        }
        catch
        {
            // not found, try the names in the history
        }

        List<KeyValuePair<ulong, string>> matches;
        lock (historyLock)
        {
            matches = historyNames.Where(p => p.Value.Equals(ident, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = historyNames.Where(p => p.Value.Contains(ident, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 1)
        {
            actor = matches[0].Key;
            return true;
        }

        if (matches.Count > 1)
        {
            SendPlayerChatMessage(admin, $"More than one player matches \"{ident}\": " + string.Join(", ", matches.Take(5).Select(m => $"{m.Value} ({m.Key})")) + ". Use the Steam ID.");
            return false;
        }

        SendPlayerChatMessage(admin, $"No player called \"{ident}\" in the chalk history. Use their Steam ID, or !chalkhistory to see who drew.");
        return false;
    }

    private void RunRollback(WFPlayer admin, string[] args)
    {
        const string Usage = "Usage: !chalkrollback <player or steam id or server> <time like 30m, 2h, all> [canvas 0-3 or all] [check]";

        if (!historyOn)
        {
            SendPlayerChatMessage(admin, "The chalk history is turned off (history = false in chalkobserver.txt).");
            return;
        }

        var words = args.ToList();

        bool checkOnly = false;
        if (words.Count > 0 && (words[^1].Equals("check", StringComparison.OrdinalIgnoreCase) || words[^1].Equals("preview", StringComparison.OrdinalIgnoreCase)))
        {
            checkOnly = true;
            words.RemoveAt(words.Count - 1);
        }

        int canvasFilter = -1;
        if (words.Count >= 3 && IsTimeWord(words[^2]) && TryReadCanvas(words[^1], out int parsedCanvas))
        {
            canvasFilter = parsedCanvas;
            words.RemoveAt(words.Count - 1);
        }

        if (words.Count < 2 || !TryReadTime(words[^1], out uint seconds))
        {
            SendPlayerChatMessage(admin, Usage);
            return;
        }

        string ident = string.Join(" ", words.Take(words.Count - 1));
        if (!TryFindActor(admin, ident, out ulong actor))
            return;

        // look at the canvases first (the server's lock), the log second, never the other way round
        ChalkCanvas[] canvases;
        lock (ParentServer.stateLock)
            canvases = ParentServer.chalkCanvas.ToArray();

        DrainPending();

        uint cutoff = NowUnix() - seconds;
        var states = new Dictionary<CellKey, CellState>();

        lock (historyLock)
        {
            foreach (HistoryEntry e in history)
            {
                if (canvasFilter >= 0 && e.Canvas != canvasFilter)
                    continue;

                var key = new CellKey(e.Canvas, e.X, e.Y);
                bool mine = e.Actor == actor && e.Time >= cutoff;

                if (mine)
                {
                    if (!states.TryGetValue(key, out CellState? state))
                        states[key] = state = new CellState();

                    if (!state.LastIsTarget)
                    {
                        // the first change of this player's last unbroken run on the cell, what it was before is what to restore
                        state.RunOld = e.Old;
                        state.LastIsTarget = true;
                    }

                    state.LastNew = e.New;
                }
                else if (states.TryGetValue(key, out CellState? other))
                {
                    // someone else (or the server) changed the cell after this player did
                    other.LastIsTarget = false;
                }
            }
        }

        string who = NameOf(actor);
        string window = seconds >= (uint)historyHours * 3600u ? "the whole history" : $"the last {Ago(seconds)}";

        if (states.Count == 0)
        {
            SendPlayerChatMessage(admin, $"{who} has no chalk changes in {window}" + (canvasFilter >= 0 ? $" on canvas {canvasFilter}." : "."));
            return;
        }

        // what to put back, per canvas. A cell is only restored while it still holds this player's last colour.
        var plan = new Dictionary<int, Dictionary<int, object>>();
        int skipped = 0, alreadyBack = 0;

        foreach (var pair in states)
        {
            CellKey key = pair.Key;
            CellState state = pair.Value;

            if (!state.LastIsTarget)
            {
                skipped++; // someone else drew on it afterwards, that is kept
                continue;
            }

            ChalkCanvas? canvas = canvases.FirstOrDefault(c => c.canvasID == key.Canvas);
            long current = (long)COLOR.NONE;
            if (canvas != null)
            {
                lock (canvas.chalkImage)
                    current = canvas.chalkImage.TryGetValue(new Vector2(key.X, key.Y), out long found) ? found : (long)COLOR.NONE;
            }

            if (canvas == null || current != state.LastNew)
            {
                skipped++; // changed in a way the log did not see
                continue;
            }

            if (state.RunOld == current)
            {
                alreadyBack++;
                continue;
            }

            if (!plan.TryGetValue(key.Canvas, out Dictionary<int, object>? cells))
                plan[key.Canvas] = cells = new Dictionary<int, object>();

            cells[cells.Count] = new Dictionary<int, object>
            {
                { 0, new Vector2(key.X, key.Y) },
                { 1, state.RunOld },
            };
        }

        int toRestore = plan.Values.Sum(c => c.Count);
        string canvasText = string.Join(",", plan.Keys.OrderBy(k => k));
        string leftAlone = skipped > 0 ? $" {skipped} cells were left alone because someone else drew there afterwards." : "";

        if (toRestore == 0)
        {
            SendPlayerChatMessage(admin, $"Nothing to roll back for {who} in {window}.{leftAlone}");
            return;
        }

        if (checkOnly)
        {
            SendPlayerChatMessage(admin, $"Check only, nothing changed: this would roll back {toRestore} cells of {who} from {window} on canvas {canvasText}.{leftAlone}");
            return;
        }

        // the changes below are logged as the server's, so the rollback itself can be rolled back with "server"
        ulong savedActor = ChalkCanvas.CurrentActor;
        ChalkCanvas.CurrentActor = 0;
        try
        {
            foreach (var pair in plan)
            {
                ChalkCanvas? canvas = canvases.FirstOrDefault(c => c.canvasID == pair.Key);
                if (canvas == null)
                    continue;

                // sent in pieces so one huge packet does not stall the players
                var piece = new Dictionary<int, object>();
                foreach (object cell in pair.Value.Values)
                {
                    piece[piece.Count] = cell;
                    if (piece.Count >= RollbackChunk)
                    {
                        canvas.drawChalk(piece);
                        piece = new Dictionary<int, object>();
                    }
                }

                if (piece.Count > 0)
                    canvas.drawChalk(piece);
            }
        }
        finally
        {
            ChalkCanvas.CurrentActor = savedActor;
        }

        Log($"{admin.Username} rolled back {toRestore} chalk cells of {who} from {window} (canvas {canvasText}), {skipped} left alone.");
        SendPlayerChatMessage(admin, $"Rolled back {toRestore} cells of {who} from {window} on canvas {canvasText}.{leftAlone} To undo it: !chalkrollback server 2m (a rollback is logged as the server).");
    }
}
