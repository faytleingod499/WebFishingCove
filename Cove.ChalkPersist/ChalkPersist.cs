using System.Text;
using System.Text.Json;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Chalk;
using Cove.Server.Plugins;

/// <summary>
/// Keeps the chalk canvases when the server restarts.
///
/// The canvases are written to chalk_save.json in the server's root directory every little while and when the
/// server stops, and read back when the server starts. Players who join afterwards are sent the restored chalk
/// like any other.
///
/// Optional settings go in chalkpersist.txt (created on first start).
/// </summary>
public class ChalkPersist : CovePlugin
{
    private const string SaveFileName = "chalk_save.json";
    private const string SettingsFileName = "chalkpersist.txt";

    private const int CanvasCount = 4;
    private const int DefaultSaveSeconds = 30;
    private const int MinSaveSeconds = 5;

    // same limits the server uses, a hand edited or damaged file can't flood the server
    private const int MaxCellsPerCanvas = 250_000;
    private const int MaxCoordinate = 4096;
    private const long MaxColor = 255;

    private readonly record struct Cell(int X, int Y, long Color);

    private string savePath = "";
    private string settingsPath = "";
    private int saveSeconds = DefaultSaveSeconds;
    private bool restore = true;

    private long nextSaveTick;

    // only one save at a time. saveGate also covers lastSavedSignature and loadFailed.
    private readonly object saveGate = new();
    private int saving;
    private ulong lastSavedSignature;
    private bool haveSavedSignature;

    // set when a save file exists but could not be read. Until real chalk shows up, an empty server must not
    // overwrite that file, it may still be repairable.
    private bool loadFailed;

    private volatile bool running;

    public ChalkPersist(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        string root = AppDomain.CurrentDomain.BaseDirectory;
        savePath = Path.Combine(root, SaveFileName);
        settingsPath = Path.Combine(root, SettingsFileName);

        try
        {
            EnsureSettingsFileExists();
            ReadSettings();
        }
        catch (Exception e)
        {
            Log($"Could not read {SettingsFileName}, using the defaults: {e.Message}");
        }

        if (restore)
            RestoreFromFile();
        else
            Log("restore = false, the saved chalk will not be loaded.");

        nextSaveTick = Environment.TickCount64 + saveSeconds * 1000L;
        running = true;
        Log($"Saving the chalk to {SaveFileName} every {saveSeconds} seconds.");
    }

    public override void onEnd()
    {
        base.onEnd();
        running = false;

        // the server is closing, so this one is done right here instead of on another thread
        try
        {
            Save(final: true);
        }
        catch (Exception e)
        {
            Log($"Could not save the chalk when stopping: {e.Message}");
        }
    }

    // triggered 12 times a second by the server
    public override void onUpdate()
    {
        if (!running || Environment.TickCount64 < nextSaveTick)
            return;

        nextSaveTick = Environment.TickCount64 + saveSeconds * 1000L;

        // saving touches the disk, the server's timer must not wait for that
        if (Interlocked.CompareExchange(ref saving, 1, 0) != 0)
            return;

        Task.Run(() =>
        {
            try
            {
                Save(final: false);
            }
            catch (Exception e)
            {
                Log($"Could not save the chalk: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref saving, 0);
            }
        });
    }

    // ---- saving ----

    private void Save(bool final)
    {
        // When the server stops, the thread calling this may already hold the server's lock while a background save
        // waits for it. Waiting forever here could freeze the shutdown, so give up after a few seconds.
        if (!Monitor.TryEnter(saveGate, final ? 3000 : Timeout.Infinite))
        {
            Log("The chalk could not be saved because another save was still running.");
            return;
        }

        try
        {
            List<Cell>[] canvases = TakeSnapshot();
            int total = canvases.Sum(c => c.Count);

            if (total == 0 && loadFailed)
                return; // keep the unreadable file for repair instead of replacing it with nothing

            ulong signature = Signature(canvases);
            if (haveSavedSignature && signature == lastSavedSignature && File.Exists(savePath))
                return; // nothing changed since the last save

            string json = Serialize(canvases);
            WriteFileSafely(json);

            lastSavedSignature = signature;
            haveSavedSignature = true;
            if (total > 0)
                loadFailed = false;

            if (final)
                Log($"Saved {total} chalk cells.");
        }
        finally
        {
            Monitor.Exit(saveGate);
        }
    }

    private List<Cell>[] TakeSnapshot()
    {
        var result = new List<Cell>[CanvasCount];
        for (int i = 0; i < CanvasCount; i++)
            result[i] = new List<Cell>();

        // the server adds canvases from its network thread, copy the list under the server's lock
        ChalkCanvas[] canvases;
        lock (ParentServer.stateLock)
            canvases = ParentServer.chalkCanvas.ToArray();

        foreach (ChalkCanvas canvas in canvases)
        {
            if (canvas.canvasID < 0 || canvas.canvasID >= CanvasCount)
                continue;

            List<Cell> cells = result[(int)canvas.canvasID];

            lock (canvas.chalkImage)
            {
                foreach (var entry in canvas.chalkImage)
                {
                    // erased cells stay in the server's list with this colour, there is nothing to keep
                    if (entry.Value == (long)COLOR.NONE)
                        continue;

                    float x = entry.Key.x;
                    float y = entry.Key.y;
                    if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate)
                        continue;

                    cells.Add(new Cell((int)MathF.Truncate(x), (int)MathF.Truncate(y), entry.Value));
                }
            }
        }

        return result;
    }

    // Order independent fingerprint, the order the server's dictionary lists cells in doesn't matter.
    private static ulong Signature(List<Cell>[] canvases)
    {
        unchecked
        {
            ulong total = 0;

            for (int canvas = 0; canvas < canvases.Length; canvas++)
            {
                foreach (Cell cell in canvases[canvas])
                {
                    ulong h = 14695981039346656037UL;
                    h = (h ^ (ulong)canvas) * 1099511628211UL;
                    h = (h ^ (ulong)(uint)cell.X) * 1099511628211UL;
                    h = (h ^ (ulong)(uint)cell.Y) * 1099511628211UL;
                    h = (h ^ (ulong)cell.Color) * 1099511628211UL;

                    h ^= h >> 33;
                    h *= 0xFF51AFD7ED558CCDUL;
                    h ^= h >> 33;

                    total += h;
                }
            }

            return total;
        }
    }

    // {"version":1,"saved":"...","canvases":{"0":[[x,y,colour],...],"1":[...],...}}
    private static string Serialize(List<Cell>[] canvases)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("saved", DateTime.UtcNow.ToString("o"));

            writer.WriteStartObject("canvases");
            for (int canvas = 0; canvas < canvases.Length; canvas++)
            {
                writer.WriteStartArray(canvas.ToString());
                foreach (Cell cell in canvases[canvas])
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(cell.X);
                    writer.WriteNumberValue(cell.Y);
                    writer.WriteNumberValue(cell.Color);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // The new data is written next to the old file first and only then swapped in, so a crash or power cut in the
    // middle of saving can never leave a half written chalk_save.json. The file it replaces is kept as .bak.
    private void WriteFileSafely(string json)
    {
        string temp = savePath + ".tmp";
        string backup = savePath + ".bak";

        File.WriteAllText(temp, json, new UTF8Encoding(false));

        if (File.Exists(savePath))
            File.Replace(temp, savePath, backup);
        else
            File.Move(temp, savePath);
    }

    // ---- loading ----

    private void RestoreFromFile()
    {
        if (!File.Exists(savePath) && !File.Exists(savePath + ".bak"))
        {
            Log($"No {SaveFileName} yet, starting with empty canvases.");
            return;
        }

        List<Cell>[]? loaded = null;
        string source = SaveFileName;

        try
        {
            if (File.Exists(savePath))
                loaded = Parse(File.ReadAllText(savePath));
        }
        catch (Exception e)
        {
            Log($"{SaveFileName} could not be read ({e.Message}), trying the backup.");
        }

        if (loaded == null && File.Exists(savePath + ".bak"))
        {
            try
            {
                loaded = Parse(File.ReadAllText(savePath + ".bak"));
                source = SaveFileName + ".bak";
            }
            catch (Exception e)
            {
                Log($"The backup could not be read either: {e.Message}");
            }
        }

        if (loaded == null)
        {
            lock (saveGate)
                loadFailed = true;
            Log($"The saved chalk could not be loaded. {SaveFileName} is left as it is until new chalk is drawn.");
            return;
        }

        int restored = 0;
        for (int id = 0; id < CanvasCount; id++)
        {
            if (loaded[id].Count == 0)
                continue;

            ChalkCanvas canvas;
            lock (ParentServer.stateLock)
            {
                canvas = ParentServer.chalkCanvas.FirstOrDefault(c => c.canvasID == id)!;
                if (canvas == null)
                {
                    canvas = new ChalkCanvas(id, ParentServer);
                    ParentServer.chalkCanvas.Add(canvas);
                }
            }

            // chalk that is already on the canvas (a plugin that loaded earlier, say) is not overwritten
            lock (canvas.chalkImage)
            {
                if (canvas.chalkImage.Count > 0)
                {
                    Log($"Canvas {id} already has chalk on it, the saved chalk for it is skipped.");
                    continue;
                }
            }

            foreach (Cell cell in loaded[id])
                canvas.chalkUpdate(new Vector2(cell.X, cell.Y), cell.Color);

            restored += loaded[id].Count;
        }

        Log($"Restored {restored} chalk cells from {source}.");
    }

    private static List<Cell>[] Parse(string json)
    {
        var result = new List<Cell>[CanvasCount];
        for (int i = 0; i < CanvasCount; i++)
            result[i] = new List<Cell>();

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("canvases", out JsonElement canvases)
            || canvases.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("This is not a chalk save file.");
        }

        for (int id = 0; id < CanvasCount; id++)
        {
            if (!canvases.TryGetProperty(id.ToString(), out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (result[id].Count >= MaxCellsPerCanvas)
                    break;

                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 3)
                    continue;

                JsonElement x = entry[0], y = entry[1], color = entry[2];
                if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number || color.ValueKind != JsonValueKind.Number)
                    continue;
                if (!x.TryGetInt32(out int cx) || !y.TryGetInt32(out int cy) || !color.TryGetInt64(out long c))
                    continue;
                if (Math.Abs(cx) > MaxCoordinate || Math.Abs(cy) > MaxCoordinate || c < 0 || c > MaxColor)
                    continue;

                result[id].Add(new Cell(cx, cy, c));
            }
        }

        return result;
    }

    // ---- settings ----

    private void ReadSettings()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in File.ReadAllLines(settingsPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        if (values.TryGetValue("saveInterval", out string? rawInterval) && rawInterval.Length > 0)
        {
            if (!int.TryParse(rawInterval, out int seconds))
                Log($"saveInterval must be a whole number, using {DefaultSaveSeconds}.");
            else if (seconds < MinSaveSeconds)
            {
                Log($"saveInterval is below the minimum, using {MinSaveSeconds}.");
                saveSeconds = MinSaveSeconds;
            }
            else
                saveSeconds = Math.Min(seconds, 86400);
        }

        if (values.TryGetValue("restore", out string? rawRestore) && rawRestore.Length > 0)
        {
            switch (rawRestore.ToLowerInvariant())
            {
                case "false":
                case "no":
                case "off":
                case "0":
                    restore = false;
                    break;
                case "true":
                case "yes":
                case "on":
                case "1":
                    restore = true;
                    break;
                default:
                    Log("restore must be true or false, using true.");
                    break;
            }
        }
    }

    private void EnsureSettingsFileExists()
    {
        if (File.Exists(settingsPath))
            return;

        string[] lines =
        {
            "# ======================================================================",
            "#  Chalk Persist",
            "# ======================================================================",
            "#",
            "#  Saves the chalk canvases to chalk_save.json in the server's folder and",
            "#  puts the chalk back when the server starts again.",
            "#",
            "#  saveInterval = seconds between saves (minimum 5). The chalk is also",
            "#                 saved when the server stops normally. If the server",
            "#                 crashes, up to this many seconds of drawing can be lost.",
            "#  restore      = true or false. false starts with empty canvases and",
            "#                 ignores the saved file (the file is still updated).",
            "#",
            "#  Changes here are read when the server starts.",
            "# ======================================================================",
            "",
            "saveInterval = 30",
            "restore = true",
            "",
        };

        File.WriteAllText(settingsPath, string.Join(Environment.NewLine, lines));
    }
}
