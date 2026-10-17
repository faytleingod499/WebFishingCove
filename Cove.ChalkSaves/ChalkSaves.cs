using System.Text;
using System.Text.Json;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Chalk;
using Cove.Server.Plugins;

/// <summary>
/// Lets players save a chalk canvas under a name and put it back on any canvas later.
///
///   !chalksave &lt;name&gt; &lt;0-3&gt;     saves that canvas
///   !chalkload &lt;name&gt; &lt;0-3&gt;     replaces that canvas with a saved one
///   !chalklist                  lists the saved canvases
///   !chalkdelete &lt;name&gt;         deletes a save (its owner, or an admin)
///
/// Saves are files in the ChalkSaves folder next to the server. Before a canvas is replaced, what was on it
/// is kept in ChalkSaves/_backups so a load can be undone by hand. Settings are in chalksaves.txt.
/// </summary>
public class ChalkSaves : CovePlugin
{
    private const string FolderName = "ChalkSaves";
    private const string BackupFolderName = "_backups";
    private const string SettingsFileName = "chalksaves.txt";

    private const int CanvasCount = 4;
    private const int MaxNameLength = 32;
    private const int MaxBackups = 20;
    private const int MaxListed = 30;

    // same limits the server uses, a hand edited or damaged file can't flood the server
    private const int MaxCells = 250_000;
    private const int MaxCoordinate = 4096;
    private const long MaxColor = 255;

    private readonly record struct Cell(int X, int Y, long Color);

    private sealed class SaveFile
    {
        public ulong Owner;
        public string OwnerName = "";
        public string Saved = "";
        public List<Cell> Cells = new();
    }

    // settings
    private bool saveAdminsOnly;
    private bool loadAdminsOnly;
    private int maxSaves = 100;
    private int loadCooldownSeconds = 30;

    private string folder = "";
    private string backupFolder = "";
    private string settingsPath = "";

    // commands run on the server's network thread, one at a time per packet, but not necessarily one at a time overall
    private readonly object fileLock = new();
    private readonly Dictionary<ulong, long> lastLoad = new();

    public ChalkSaves(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        string root = AppDomain.CurrentDomain.BaseDirectory;
        folder = Path.Combine(root, FolderName);
        backupFolder = Path.Combine(folder, BackupFolderName);
        settingsPath = Path.Combine(root, SettingsFileName);

        try
        {
            Directory.CreateDirectory(backupFolder);
            EnsureSettingsFileExists();
            ReadSettings();
        }
        catch (Exception e)
        {
            Log($"Could not set up {FolderName} or {SettingsFileName}, using the defaults: {e.Message}");
        }

        Register("chalksave", "Saves a chalk canvas | Usage: !chalksave <name> <0-3>", SaveCanvas);
        Register("chalkload", "Puts a saved chalk canvas on a canvas | Usage: !chalkload <name> <0-3>", LoadCanvas);
        Register("chalklist", "Lists the saved chalk canvases", ListSaves);
        Register("chalkdelete", "Deletes a saved chalk canvas | Usage: !chalkdelete <name>", DeleteSave);
    }

    public override void onEnd()
    {
        base.onEnd();
        foreach (string command in new[] { "chalksave", "chalkload", "chalklist", "chalkdelete" })
            ParentServer.UnregisterCommand(command);
    }

    private void Register(string command, string description, Action<WFPlayer, string[]> run)
    {
        RegisterCommand(
            command: command,
            aliases: [],
            callback: (player, args) =>
            {
                try
                {
                    run(player, args);
                }
                catch (Exception e)
                {
                    Log($"!{command} failed: {e.Message}");
                    SendPlayerChatMessage(player, "Something went wrong, see the server log.");
                }
            }
        );
        SetCommandDescription(command, description);
    }

    // ---- commands ----

    private void SaveCanvas(WFPlayer player, string[] args)
    {
        if (saveAdminsOnly && !IsPlayerAdmin(player))
        {
            SendPlayerChatMessage(player, "Only admins can save canvases.");
            return;
        }
        if (args.Length != 2 || !TryName(args[0], out string name) || !TryCanvas(args[1], out int canvasId))
        {
            SendPlayerChatMessage(player, "Usage: !chalksave <name> <0-3>. Names can use letters, numbers, - and _.");
            return;
        }

        List<Cell> cells = Snapshot(canvasId);
        if (cells.Count == 0)
        {
            SendPlayerChatMessage(player, $"Canvas {canvasId} is empty, there is nothing to save.");
            return;
        }

        lock (fileLock)
        {
            string path = PathFor(name);
            bool exists = File.Exists(path);

            if (exists)
            {
                SaveFile? old = Read(path);
                if (old != null && old.Owner != player.SteamId.m_SteamID && !IsPlayerAdmin(player))
                {
                    SendPlayerChatMessage(player, $"\"{name}\" was saved by {old.OwnerName}, pick another name.");
                    return;
                }
            }
            else if (Directory.GetFiles(folder, "*.json").Length >= maxSaves)
            {
                SendPlayerChatMessage(player, $"There are already {maxSaves} saves, delete one first.");
                return;
            }

            WriteSave(path, new SaveFile
            {
                Owner = player.SteamId.m_SteamID,
                OwnerName = player.Username,
                Saved = DateTime.UtcNow.ToString("o"),
                Cells = cells,
            });

            Log($"{player.Username} saved canvas {canvasId} as \"{name}\" ({cells.Count} cells){(exists ? ", replacing the old save" : "")}.");
            SendPlayerChatMessage(player, $"Saved canvas {canvasId} as \"{name}\"{(exists ? " (replaced the old save)" : "")}.");
        }
    }

    private void LoadCanvas(WFPlayer player, string[] args)
    {
        bool admin = IsPlayerAdmin(player);
        if (loadAdminsOnly && !admin)
        {
            SendPlayerChatMessage(player, "Only admins can load canvases.");
            return;
        }
        if (args.Length != 2 || !TryName(args[0], out string name) || !TryCanvas(args[1], out int canvasId))
        {
            SendPlayerChatMessage(player, "Usage: !chalkload <name> <0-3>");
            return;
        }

        ulong id = player.SteamId.m_SteamID;
        long now = Environment.TickCount64;
        lock (fileLock)
        {
            if (!admin && lastLoad.TryGetValue(id, out long last) && now - last < loadCooldownSeconds * 1000L)
            {
                int wait = (int)Math.Ceiling((loadCooldownSeconds * 1000L - (now - last)) / 1000.0);
                SendPlayerChatMessage(player, $"Please wait {wait} more second(s) before loading another canvas.");
                return;
            }
        }

        string path = PathFor(name);
        SaveFile? save;
        lock (fileLock)
            save = File.Exists(path) ? Read(path) : null;

        if (save == null)
        {
            SendPlayerChatMessage(player, File.Exists(path) ? $"\"{name}\" could not be read." : $"There is no save called \"{name}\". Try !chalklist.");
            return;
        }

        ChalkCanvas canvas = GetOrCreateCanvas(canvasId);

        // keep what is on the canvas now, in case this was a mistake
        List<Cell> before = Snapshot(canvasId);
        if (before.Count > 0)
        {
            lock (fileLock)
            {
                WriteSave(Path.Combine(backupFolder, $"canvas{canvasId}-{DateTime.Now:yyyyMMdd-HHmmss}-{player.SteamId.m_SteamID}.json"), new SaveFile
                {
                    Owner = player.SteamId.m_SteamID,
                    OwnerName = player.Username,
                    Saved = DateTime.UtcNow.ToString("o"),
                    Cells = before,
                });
                PruneBackups();
            }
        }

        // every cell that is on the canvas now but not in the save is erased, then the saved cells are drawn
        var wanted = new Dictionary<(int, int), long>();
        foreach (Cell c in save.Cells)
            wanted[(c.X, c.Y)] = c.Color;

        var changes = new Dictionary<int, object>();
        int n = 0;
        foreach (Cell c in before)
        {
            if (!wanted.ContainsKey((c.X, c.Y)))
                changes[n++] = new Dictionary<int, object> { { 0, new Vector2(c.X, c.Y) }, { 1, (long)COLOR.NONE } };
        }
        foreach (var w in wanted)
            changes[n++] = new Dictionary<int, object> { { 0, new Vector2(w.Key.Item1, w.Key.Item2) }, { 1, w.Value } };

        // updates the server's copy and tells every player, like the chalk clearing does
        canvas.drawChalk(changes);

        lock (fileLock)
            lastLoad[id] = now;

        Log($"{player.Username} loaded \"{name}\" ({save.Cells.Count} cells) onto canvas {canvasId}, replacing {before.Count} cells.");
        SendPlayerChatMessage(player, $"Loaded \"{name}\" onto canvas {canvasId}.");
    }

    private void ListSaves(WFPlayer player, string[] args)
    {
        string[] files;
        lock (fileLock)
            files = Directory.GetFiles(folder, "*.json");

        if (files.Length == 0)
        {
            SendPlayerChatMessage(player, "No canvases have been saved yet. Use !chalksave <name> <0-3>.");
            return;
        }

        var names = files.Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        string shown = string.Join(", ", names.Take(MaxListed));
        string more = names.Count > MaxListed ? $" and {names.Count - MaxListed} more" : "";
        SendPlayerChatMessage(player, $"Saved canvases ({names.Count}): {shown}{more}");
    }

    private void DeleteSave(WFPlayer player, string[] args)
    {
        if (args.Length != 1 || !TryName(args[0], out string name))
        {
            SendPlayerChatMessage(player, "Usage: !chalkdelete <name>");
            return;
        }

        lock (fileLock)
        {
            string path = PathFor(name);
            if (!File.Exists(path))
            {
                SendPlayerChatMessage(player, $"There is no save called \"{name}\".");
                return;
            }

            SaveFile? save = Read(path);
            if (save != null && save.Owner != player.SteamId.m_SteamID && !IsPlayerAdmin(player))
            {
                SendPlayerChatMessage(player, $"\"{name}\" was saved by {save.OwnerName}, only they or an admin can delete it.");
                return;
            }

            File.Delete(path);
            Log($"{player.Username} deleted the save \"{name}\".");
            SendPlayerChatMessage(player, $"Deleted \"{name}\".");
        }
    }

    // ---- canvases ----

    private ChalkCanvas GetOrCreateCanvas(int id)
    {
        lock (ParentServer.stateLock)
        {
            ChalkCanvas? canvas = ParentServer.chalkCanvas.FirstOrDefault(c => c.canvasID == id);
            if (canvas == null)
            {
                canvas = new ChalkCanvas(id, ParentServer);
                ParentServer.chalkCanvas.Add(canvas);
            }
            return canvas;
        }
    }

    private List<Cell> Snapshot(int id)
    {
        var cells = new List<Cell>();
        ChalkCanvas? canvas;
        lock (ParentServer.stateLock)
            canvas = ParentServer.chalkCanvas.FirstOrDefault(c => c.canvasID == id);
        if (canvas == null)
            return cells;

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
        return cells;
    }

    // ---- files ----

    private string PathFor(string name) => Path.Combine(folder, name + ".json");

    private void WriteSave(string path, SaveFile save)
    {
        var json = new StringBuilder();
        json.Append("{\"version\":1,\"owner\":\"").Append(save.Owner).Append("\",\"ownerName\":")
            .Append(JsonSerializer.Serialize(save.OwnerName)).Append(",\"saved\":\"").Append(save.Saved).Append("\",\"cells\":[");
        for (int i = 0; i < save.Cells.Count; i++)
        {
            Cell c = save.Cells[i];
            if (i > 0)
                json.Append(',');
            json.Append('[').Append(c.X).Append(',').Append(c.Y).Append(',').Append(c.Color).Append(']');
        }
        json.Append("]}");

        string temp = path + ".tmp";
        File.WriteAllText(temp, json.ToString());
        File.Move(temp, path, overwrite: true);
    }

    private SaveFile? Read(string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            var save = new SaveFile();

            if (root.TryGetProperty("owner", out JsonElement owner) && ulong.TryParse(owner.GetString(), out ulong ownerId))
                save.Owner = ownerId;
            if (root.TryGetProperty("ownerName", out JsonElement ownerName))
                save.OwnerName = ownerName.GetString() ?? "";
            if (root.TryGetProperty("saved", out JsonElement saved))
                save.Saved = saved.GetString() ?? "";

            if (!root.TryGetProperty("cells", out JsonElement cells) || cells.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement cell in cells.EnumerateArray())
            {
                if (save.Cells.Count >= MaxCells)
                    break;
                if (cell.ValueKind != JsonValueKind.Array || cell.GetArrayLength() != 3)
                    continue;

                if (!cell[0].TryGetInt32(out int x) || !cell[1].TryGetInt32(out int y) || !cell[2].TryGetInt64(out long color))
                    continue;
                if (Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate || color < 0 || color > MaxColor)
                    continue;

                save.Cells.Add(new Cell(x, y, color));
            }
            return save;
        }
        catch (Exception e)
        {
            Log($"Could not read {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    private void PruneBackups()
    {
        try
        {
            foreach (FileInfo old in new DirectoryInfo(backupFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxBackups))
                old.Delete();
        }
        catch (Exception e)
        {
            Log($"Could not clean up the old backups: {e.Message}");
        }
    }

    // ---- input ----

    // names become file names, so only a safe set of characters is allowed
    private static bool TryName(string raw, out string name)
    {
        name = raw.Trim().ToLowerInvariant();
        if (name.Length == 0 || name.Length > MaxNameLength || name[0] == '_' || name[0] == '-')
            return false;
        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
                return false;
        }
        return true;
    }

    private static bool TryCanvas(string raw, out int id)
    {
        return int.TryParse(raw, out id) && id >= 0 && id < CanvasCount;
    }

    // ---- settings ----

    private void ReadSettings()
    {
        foreach (string raw in File.ReadAllLines(settingsPath))
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
                case "saveaccess":
                    saveAdminsOnly = value.Equals("admins", StringComparison.OrdinalIgnoreCase);
                    break;
                case "loadaccess":
                    loadAdminsOnly = value.Equals("admins", StringComparison.OrdinalIgnoreCase);
                    break;
                case "maxsaves":
                    if (int.TryParse(value, out int saves) && saves > 0)
                        maxSaves = Math.Min(saves, 10_000);
                    break;
                case "loadcooldown":
                    if (int.TryParse(value, out int cooldown) && cooldown >= 0)
                        loadCooldownSeconds = Math.Min(cooldown, 3600);
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
            "#  Chalk Saves",
            "# ======================================================================",
            "#",
            "#  Players save a chalk canvas under a name and put it back later:",
            "#    !chalksave <name> <0-3>     saves that canvas",
            "#    !chalkload <name> <0-3>     replaces that canvas with a save",
            "#    !chalklist                  lists the saves",
            "#    !chalkdelete <name>         deletes a save (its owner or an admin)",
            "#",
            "#  Saves are files in the ChalkSaves folder next to the server. Before a",
            "#  canvas is replaced, what was on it is kept in ChalkSaves/_backups",
            "#  (the newest 20). To undo a load, copy that file up into ChalkSaves,",
            "#  rename it to something like  undo.json  and load it.",
            "#",
            "#  Changes here are read when the plugin starts, use !reloadplugins chalksaves",
            "#  after editing.",
            "#",
            "#  saveAccess    = everyone or admins. Who can save and replace their own saves.",
            "#  loadAccess    = everyone or admins. Who can put a save on a canvas.",
            "#                  Loading wipes whatever is drawn there, so admins may be safer.",
            "#  maxSaves      = how many saves can exist, default 100.",
            "#  loadCooldown  = seconds a player must wait between loads, default 30.",
            "#                  Admins are not limited.",
            "# ======================================================================",
            "",
            "saveAccess = everyone",
            "loadAccess = everyone",
            "maxSaves = 100",
            "loadCooldown = 30",
            "",
        };

        File.WriteAllText(settingsPath, string.Join(Environment.NewLine, lines));
        Log($"Created {SettingsFileName}.");
    }
}
