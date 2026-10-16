using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Chalk;
using Cove.Server.Plugins;

/// <summary>
/// Watches the four chalk canvases. When anything on them has changed since the last check, all four are drawn into
/// one picture, saved in the ChalkObserver folder next to server.cfg, and optionally posted to a Discord webhook.
/// Configured through chalkobserver.txt in the server's root directory.
///
/// Picture layout: canvas 0 top left, canvas 1 top right, canvas 2 bottom left, canvas 3 bottom right.
/// </summary>
public class ChalkObserver : CovePlugin
{
    private const string FileName = "chalkobserver.txt";
    private const string FolderName = "ChalkObserver";

    private const int CanvasCount = 4;

    // The game's canvases are about this big. The picture grows if the server ever sees cells outside of it.
    private const int BaseCanvasSize = 200;

    // Cells further out than this are ignored, a client can send any coordinate it likes and the picture
    // has to stay a sane size.
    private const int MaxSpan = 512;
    private const int MaxImageSide = 4000;

    private const int DefaultIntervalSeconds = 60;
    private const int MinIntervalSeconds = 10;
    private const int DefaultScale = 3;
    private const int DefaultMaxSavedImages = 500;
    private const string DefaultMessage = "The chalk canvases changed.";

    // How often the config file is checked for edits. Saving the file while the server runs reloads it.
    private const long ConfigCheckMs = 10_000;

    // Discord rejects webhook uploads larger than this
    private const long MaxUploadBytes = 8L * 1024 * 1024;

    private static readonly byte[] DefaultBackground = { 0x6E, 0x6E, 0x6E };
    private static readonly byte[] DividerColor = { 0x1E, 0x1E, 0x1E };
    // drawn for a chalk colour number that has no colour set (see "colorN" in chalkobserver.txt)
    private static readonly byte[] UnknownColor = { 0xFF, 0xFF, 0xFF };

    // The highest colour number that can be set in the config file (color0 to color255)
    private const int MaxPaletteSize = 256;

    // Position in this list = the chalk colour number sent by the game, so colour 0 is the first entry.
    private static readonly string[] DefaultPaletteHex =
    {
        "ffe7c5", "2a3844", "d70b5d", "0db39e", "f4c009", "ff00ff",
        "bac357", "a3b2d2", "d6cec2", "bfded8", "a9c484", "5d937b",
        "a2a6a9", "777f8f", "eab281", "ea7286", "f4a4bf", "a07ca7",
        "bf796d", "f5d1b6", "e3e19f", "ffdf00", "ffbf00", "c4b454",
        "f5deb3", "f4c430", "00ffff", "89cff0", "4d4dff", "00008b",
        "4169e1", "006742", "4cbb17", "2e6f40", "2e8b57", "c0c0c0",
        "818589", "899499", "708090", "ffa500", "ff8c00", "d7942d",
        "ff5f1f", "cc7722", "ff69b4", "ff10f0", "aa336a", "f4b4c4",
        "953553", "d8bfd8", "7f00ff", "800080", "ff2400", "ff4433",
        "a52a2a", "913831", "ff0000", "3b2219", "a16e4b", "d4aa78",
        "e6bc98", "ffe7d1",
    };

    // must be declared after DefaultPaletteHex, static fields are set up in the order they appear
    private static readonly byte[][] DefaultPalette =
        DefaultPaletteHex.Select(hex => Convert.FromHexString(hex)).ToArray();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly record struct Cell(int X, int Y, long Color);

    private sealed class Settings
    {
        public int IntervalSeconds = DefaultIntervalSeconds;
        public int Scale = DefaultScale;
        public bool SaveImages = true;
        public bool Debug;
        public int MaxSavedImages = DefaultMaxSavedImages;
        public string WebhookUrl = "";
        public string DiscordMessage = DefaultMessage;
        public byte[] Background = (byte[])DefaultBackground.Clone();
        public byte[][] Palette = DefaultPalette.Select(color => (byte[])color.Clone()).ToArray();
    }

    // onUpdate runs on a timer thread, onInit/onEnd on others, so the state below is guarded by this lock.
    private readonly object sync = new();
    private Settings settings = new();
    private string filePath = "";
    private string folderPath = "";
    private DateTime loadedWriteTime = DateTime.MinValue;
    private long nextConfigCheckTick;
    private long nextObserveTick;
    private bool running;
    private CancellationTokenSource? cancel;

    // Only one check runs at a time, and only that check touches lastSignature.
    private int busy;
    private ulong lastSignature;

    // Debug only: a copy of everything the clients sent, built straight from the incoming packets. Comparing it with
    // what the server stored (stats.txt) shows whether cells are lost before or after they reach the server.
    private volatile bool debugOn;
    private readonly object mirrorLock = new();
    private readonly Dictionary<long, Dictionary<(float, float), long>> mirror = new();
    private readonly Dictionary<long, long[]> received = new(); // per canvas: packets, cells, malformed cells

    public ChalkObserver(CoveServer server) : base(server) { }

    public override void onChalkUpdate(WFPlayer? sender, long canvasID, Dictionary<int, object> packet)
    {
        base.onChalkUpdate(sender, canvasID, packet);

        if (!debugOn)
            return;

        try
        {
            long cells = 0, malformed = 0;
            string bad = "";

            lock (mirrorLock)
            {
                if (!mirror.TryGetValue(canvasID, out var image))
                    mirror[canvasID] = image = new Dictionary<(float, float), long>();
                if (!received.TryGetValue(canvasID, out long[]? totals))
                    received[canvasID] = totals = new long[3];

                foreach (object? entry in packet.Values)
                {
                    cells++;
                    if (entry is Dictionary<int, object> cell
                        && cell.TryGetValue(0, out object? rawPosition) && rawPosition is Vector2 position
                        && cell.TryGetValue(1, out object? rawColor)
                        && (rawColor is long || rawColor is int))
                    {
                        image[(position.x, position.y)] = Convert.ToInt64(rawColor);
                    }
                    else
                    {
                        malformed++;
                        if (bad.Length == 0)
                            bad = DescribeEntry(entry);
                    }
                }

                totals[0]++;
                totals[1] += cells;
                totals[2] += malformed;
            }

            string who = sender?.Username ?? "unknown";
            Log($"[chalk debug] canvas {canvasID}: {cells} cells from {who}" + (malformed > 0 ? $", {malformed} could not be read, first one was {bad}" : ""));
        }
        catch (Exception e)
        {
            Log($"[chalk debug] failed: {e.Message}");
        }
    }

    private static string DescribeEntry(object? entry)
    {
        if (entry is Dictionary<int, object> cell)
        {
            string parts = string.Join(", ", cell.Select(p => $"{p.Key}={p.Value?.GetType().Name ?? "null"}"));
            return $"a cell with [{parts}]";
        }

        return entry?.GetType().Name ?? "null";
    }

    public override void onInit()
    {
        base.onInit();

        string root = AppDomain.CurrentDomain.BaseDirectory;
        filePath = Path.Combine(root, FileName);
        folderPath = Path.Combine(root, FolderName);

        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(folderPath);
                EnsureFileExists();
                Reload();
            }
            catch (Exception e)
            {
                // forget the write time so the next config check tries to read the file again
                loadedWriteTime = DateTime.MinValue;
                Log($"Could not load {FileName}, using the defaults: {e.Message}");
            }

            long now = Environment.TickCount64;
            nextConfigCheckTick = now + ConfigCheckMs;
            nextObserveTick = now + settings.IntervalSeconds * 1000L;
            cancel = new CancellationTokenSource();
            running = true;
        }
    }

    public override void onEnd()
    {
        base.onEnd();

        lock (sync)
        {
            running = false;
            cancel?.Cancel();
        }
    }

    // triggered 12 times a second by the server
    public override void onUpdate()
    {
        Settings? dueSettings = null;
        CancellationToken token = default;

        lock (sync)
        {
            if (!running)
                return;

            long now = Environment.TickCount64;

            if (now >= nextConfigCheckTick)
            {
                nextConfigCheckTick = now + ConfigCheckMs;
                ReloadIfChanged();
            }

            if (now >= nextObserveTick)
            {
                nextObserveTick = now + settings.IntervalSeconds * 1000L;
                dueSettings = settings;
                token = cancel?.Token ?? default;
            }
        }

        if (dueSettings == null)
            return;

        // the previous check is still busy (slow disk or Discord), skip this one instead of piling up
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return;

        // the work runs off this thread, onUpdate is on the server's 12Hz actor timer and must stay fast
        Settings current = dueSettings;
        _ = Task.Run(() => ObserveSafeAsync(current, token));
    }

    private async Task ObserveSafeAsync(Settings s, CancellationToken token)
    {
        try
        {
            await ObserveAsync(s, token);
        }
        catch (OperationCanceledException)
        {
            // the server is stopping
        }
        catch (Exception e)
        {
            Log($"The chalk check failed: {Scrub(e.Message, s)}");
        }
        finally
        {
            Volatile.Write(ref busy, 0);
        }
    }

    private async Task ObserveAsync(Settings s, CancellationToken token)
    {
        // nothing would happen with the picture, so don't spend time drawing it
        if (!s.SaveImages && s.WebhookUrl.Length == 0)
            return;

        List<Cell>[] canvases = TakeSnapshot();

        ulong signature = Signature(canvases);
        if (signature == lastSignature)
            return; // nothing changed since the last picture

        WarnAboutUnknownColors(canvases, s);

        if (s.Debug)
            WriteStats();

        byte[] png = RenderPng(canvases, s);
        lastSignature = signature;

        string fileName = $"chalk_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";

        if (s.SaveImages)
        {
            try
            {
                Directory.CreateDirectory(folderPath);
                File.WriteAllBytes(Path.Combine(folderPath, fileName), png);
                Log($"The chalk changed, saved {FolderName}/{fileName}");
                PruneOldImages(s.MaxSavedImages);
            }
            catch (Exception e)
            {
                Log($"Could not save the picture: {e.Message}");
            }
        }

        if (s.WebhookUrl.Length > 0)
            await SendToDiscordAsync(png, fileName, s, token);
    }

    // Each colour number is reported once (and only a handful of them, a client can send any number it likes),
    // so the log shows which "colorN" lines are missing without being flooded.
    private readonly HashSet<long> warnedColors = new();

    private void WarnAboutUnknownColors(List<Cell>[] canvases, Settings s)
    {
        foreach (List<Cell> list in canvases)
        {
            foreach (Cell cell in list)
            {
                if (cell.Color >= s.Palette.Length && warnedColors.Count < 20 && warnedColors.Add(cell.Color))
                {
                    Log($"Chalk colour number {cell.Color} has no colour set, so it is drawn white. " +
                        $"Add a line like \"color{cell.Color} = ff8800\" to {FileName} to set it.");
                }
            }
        }
    }

    // ---- debugging ----

    // Writes ChalkObserver/stats.txt with what the server really holds for each canvas: how many cells, how far
    // they reach, whether positions are whole numbers, and how often each colour number is used.
    private void WriteStats()
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine($"Written {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            ChalkCanvas[] canvases;
            lock (ParentServer.stateLock)
                canvases = ParentServer.chalkCanvas.ToArray();

            foreach (ChalkCanvas canvas in canvases.OrderBy(c => c.canvasID))
            {
                int count = 0, erased = 0, fractional = 0;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                var colors = new SortedDictionary<long, int>();

                lock (canvas.chalkImage)
                {
                    foreach (var entry in canvas.chalkImage)
                    {
                        count++;
                        float x = entry.Key.x, y = entry.Key.y;
                        if (x != MathF.Floor(x) || y != MathF.Floor(y))
                            fractional++;
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);

                        if (entry.Value == (long)COLOR.NONE)
                        {
                            erased++;
                            continue;
                        }

                        colors[entry.Value] = colors.TryGetValue(entry.Value, out int n) ? n + 1 : 1;
                    }
                }

                text.AppendLine();
                text.AppendLine($"Canvas {canvas.canvasID}: {count} cells stored, {erased} erased, {fractional} with a position that is not a whole number");
                if (count > 0)
                    text.AppendLine($"  x from {minX} to {maxX}, y from {minY} to {maxY}");
                text.AppendLine("  colour number: how many cells = " + string.Join(", ", colors.Select(c => $"{c.Key}: {c.Value}")));

                lock (mirrorLock)
                {
                    if (mirror.TryGetValue(canvas.canvasID, out var sent) && received.TryGetValue(canvas.canvasID, out long[]? totals))
                    {
                        int onlyReceived = 0, onlyStored = 0, differentColour = 0;
                        lock (canvas.chalkImage)
                        {
                            foreach (var entry in canvas.chalkImage)
                            {
                                if (!sent.TryGetValue((entry.Key.x, entry.Key.y), out long c))
                                    onlyStored++;
                                else if (c != entry.Value)
                                    differentColour++;
                            }

                            foreach (var key in sent.Keys)
                            {
                                if (!canvas.chalkImage.ContainsKey(new Vector2(key.Item1, key.Item2)))
                                    onlyReceived++;
                            }
                        }

                        text.AppendLine($"  since debug was turned on: {totals[0]} packets, {totals[1]} cells received, {totals[2]} could not be read");
                        text.AppendLine($"  {sent.Count} different cells received. Compared with what the server stored: {onlyReceived} received but not stored, {onlyStored} stored but not received, {differentColour} with a different colour");
                    }
                }
            }

            File.WriteAllText(Path.Combine(folderPath, "stats.txt"), text.ToString());
        }
        catch (Exception e)
        {
            Log($"Could not write stats.txt: {e.Message}");
        }
    }

    // ---- reading the canvases ----

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
                    // erased cells stay in the dictionary with this colour, they are simply empty
                    if (entry.Value == (long)COLOR.NONE)
                        continue;

                    float x = entry.Key.x;
                    float y = entry.Key.y;
                    if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 4096 || Math.Abs(y) > 4096)
                        continue;

                    cells.Add(new Cell((int)MathF.Round(x), (int)MathF.Round(y), entry.Value));
                }
            }
        }

        return result;
    }

    // Order independent fingerprint of every drawn cell. Adding the per-cell hashes means the order the dictionary
    // happens to enumerate in doesn't matter, only what is on the canvases.
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

    // ---- drawing the picture ----

    private static byte[] RenderPng(List<Cell>[] canvases, Settings s)
    {
        // work out how big the canvases really are. Cells normally sit in 0-199, but this doesn't assume it.
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (List<Cell> list in canvases)
        {
            foreach (Cell cell in list)
            {
                minX = Math.Min(minX, cell.X);
                minY = Math.Min(minY, cell.Y);
                maxX = Math.Max(maxX, cell.X);
                maxY = Math.Max(maxY, cell.Y);
            }
        }

        int originX = minX;
        int originY = minY;
        int needed = Math.Max(maxX - originX, maxY - originY) + 1;
        int size = Math.Clamp(needed, BaseCanvasSize, MaxSpan);

        // shrink the scale if a big canvas would make the picture enormous
        int scale = s.Scale;
        int gap = Math.Max(4, scale * 2);
        while (scale > 1 && 2 * size * scale + 3 * gap > MaxImageSide)
        {
            scale--;
            gap = Math.Max(4, scale * 2);
        }

        int panel = size * scale;
        int width = 2 * panel + 3 * gap;
        int height = width;

        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = DividerColor[0];
            rgb[i + 1] = DividerColor[1];
            rgb[i + 2] = DividerColor[2];
        }

        for (int canvas = 0; canvas < CanvasCount; canvas++)
        {
            int left = gap + (canvas % 2) * (panel + gap);
            int top = gap + (canvas / 2) * (panel + gap);

            FillRect(rgb, width, left, top, panel, panel, s.Background);

            foreach (Cell cell in canvases[canvas])
            {
                int cx = cell.X - originX;
                int cy = cell.Y - originY;
                if (cx < 0 || cy < 0 || cx >= size || cy >= size)
                    continue;

                FillRect(rgb, width, left + cx * scale, top + cy * scale, scale, scale, ColorFor(s, cell.Color));
            }
        }

        return PngWriter.EncodeRgb(rgb, width, height);
    }

    private static byte[] ColorFor(Settings s, long color)
    {
        if (color >= 0 && color < s.Palette.Length)
            return s.Palette[color];
        return UnknownColor;
    }

    private static void FillRect(byte[] rgb, int imageWidth, int x, int y, int w, int h, byte[] color)
    {
        for (int row = 0; row < h; row++)
        {
            int i = ((y + row) * imageWidth + x) * 3;
            for (int col = 0; col < w; col++)
            {
                rgb[i++] = color[0];
                rgb[i++] = color[1];
                rgb[i++] = color[2];
            }
        }
    }

    // ---- saving and sending ----

    private void PruneOldImages(int keep)
    {
        if (keep <= 0)
            return; // 0 means keep everything

        try
        {
            string[] files = Directory.GetFiles(folderPath, "chalk_*.png");
            if (files.Length <= keep)
                return;

            // the file names start with the date, so name order is oldest first
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length - keep; i++)
                File.Delete(files[i]);
        }
        catch (Exception e)
        {
            Log($"Could not clean up old pictures: {e.Message}");
        }
    }

    private async Task SendToDiscordAsync(byte[] png, string fileName, Settings s, CancellationToken token)
    {
        if (png.Length > MaxUploadBytes)
        {
            Log("The picture is too big for Discord, lower the scale in chalkobserver.txt.");
            return;
        }

        try
        {
            using var form = new MultipartFormDataContent();

            string json = JsonSerializer.Serialize(new { content = s.DiscordMessage });
            form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");

            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "files[0]", fileName);

            using HttpResponseMessage response = await Http.PostAsync(s.WebhookUrl, form, token);

            if (response.IsSuccessStatusCode)
            {
                Log("Sent the chalk picture to Discord.");
            }
            else if ((int)response.StatusCode == 429)
            {
                Log("Discord is rate limiting the webhook, this picture was not sent. Try a longer checkInterval.");
            }
            else
            {
                Log($"Discord did not accept the picture (HTTP {(int)response.StatusCode}). Check discordWebhook.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // the server is stopping
        }
        catch (Exception e)
        {
            Log($"Could not send the picture to Discord: {Scrub(e.Message, s)}");
        }
    }

    // the webhook address is a secret, anyone who has it can post in the channel, so it never goes into the log
    private static string Scrub(string text, Settings s)
    {
        if (s.WebhookUrl.Length == 0)
            return text;
        return text.Replace(s.WebhookUrl, "[webhook]");
    }

    // ---- config file ----

    private void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(filePath))
                return; // keep running with the settings we already have

            if (File.GetLastWriteTimeUtc(filePath) == loadedWriteTime)
                return; // unchanged

            Reload();

            // a shorter interval should take effect now, not after the old one runs out
            long soonest = Environment.TickCount64 + settings.IntervalSeconds * 1000L;
            nextObserveTick = Math.Min(nextObserveTick, soonest);
        }
        catch (Exception e)
        {
            // most likely the file was being saved at that moment, try again on the next check
            loadedWriteTime = DateTime.MinValue;
            Log($"Could not reload {FileName}: {e.Message}");
        }
    }

    private void Reload()
    {
        // remember the write time BEFORE reading, so an edit made while we read gets picked up next time
        loadedWriteTime = File.GetLastWriteTimeUtc(filePath);

        Dictionary<string, string> values = ReadValues();
        var next = new Settings();

        next.IntervalSeconds = ReadInt(values, "checkInterval", DefaultIntervalSeconds, MinIntervalSeconds, 86400);
        next.Scale = ReadInt(values, "scale", DefaultScale, 1, 6);
        next.SaveImages = ReadBool(values, "saveImages", true);
        next.Debug = ReadBool(values, "debug", false);
        debugOn = next.Debug;
        next.MaxSavedImages = ReadInt(values, "maxSavedImages", DefaultMaxSavedImages, 0, 100000);

        if (values.TryGetValue("discordMessage", out string? message))
            next.DiscordMessage = message.Replace("\\n", "\n");

        string hook = values.TryGetValue("discordWebhook", out string? rawHook) ? rawHook.Trim() : "";
        if (hook.Length > 0 && !IsDiscordWebhook(hook))
        {
            Log("discordWebhook is not a Discord webhook address (it should start with https://discord.com/api/webhooks/), so Discord is turned off.");
            hook = "";
        }
        next.WebhookUrl = hook;

        next.Background = ReadColor(values, "background", DefaultBackground);
        next.Palette = ReadPalette(values);

        settings = next;

        string where = next.SaveImages ? $"saving to {FolderName}" : "not saving pictures";
        string discord = next.WebhookUrl.Length > 0 ? "Discord is on" : "Discord is off";
        Log($"Checking the chalk every {next.IntervalSeconds} seconds, {where}, {discord}.");
    }

    private int ReadInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out string? raw) || raw.Length == 0)
            return fallback;

        if (!int.TryParse(raw, out int parsed))
        {
            Log($"{key} must be a whole number, using {fallback}.");
            return fallback;
        }

        if (parsed < min)
        {
            Log($"{key} is below the minimum, using {min}.");
            return min;
        }

        if (parsed > max)
        {
            Log($"{key} is above the maximum, using {max}.");
            return max;
        }

        return parsed;
    }

    private bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out string? raw) || raw.Length == 0)
            return fallback;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "true":
            case "yes":
            case "on":
            case "1":
                return true;

            case "false":
            case "no":
            case "off":
            case "0":
                return false;

            default:
                Log($"{key} must be true or false, using {fallback.ToString().ToLowerInvariant()}.");
                return fallback;
        }
    }

    private byte[] ReadColor(Dictionary<string, string> values, string key, byte[] fallback)
    {
        if (!values.TryGetValue(key, out string? raw) || raw.Length == 0)
            return (byte[])fallback.Clone();

        try
        {
            byte[] bytes = Convert.FromHexString(raw.Trim().TrimStart('#'));
            if (bytes.Length == 3)
                return bytes;
        }
        catch (FormatException)
        {
            // handled below
        }

        Log($"{key} must be a colour like ff8800, using the default.");
        return (byte[])fallback.Clone();
    }

    // Starts from the built in colours and applies every "colorN = rrggbb" line of the config file on top.
    // Lines past the end of the built in list (for example color62) add new colours.
    private byte[][] ReadPalette(Dictionary<string, string> values)
    {
        var palette = new List<byte[]>(DefaultPalette.Select(color => (byte[])color.Clone()));

        foreach (string key in values.Keys)
        {
            if (!key.StartsWith("color", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!int.TryParse(key.AsSpan(5), out int number) || number < 0)
                continue; // some other key that happens to start with "color"

            if (number >= MaxPaletteSize)
            {
                Log($"{key} is ignored, colour numbers go up to {MaxPaletteSize - 1}.");
                continue;
            }

            // leave gaps (for example if only color70 is set) as the "unknown" colour
            while (palette.Count <= number)
                palette.Add((byte[])UnknownColor.Clone());

            byte[] fallback = number < DefaultPalette.Length ? DefaultPalette[number] : UnknownColor;
            palette[number] = ReadColor(values, key, fallback);
        }

        return palette.ToArray();
    }

    private static bool IsDiscordWebhook(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        string host = uri.Host.ToLowerInvariant();
        bool knownHost = host == "discord.com" || host == "ptb.discord.com" || host == "canary.discord.com" || host == "discordapp.com";

        return knownHost && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
    }

    // simple "key = value" lines, lines starting with # are comments.
    // only the FIRST '=' splits the line, so values can contain '=' themselves.
    private Dictionary<string, string> ReadValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in File.ReadAllLines(filePath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }

        return values;
    }

    private void EnsureFileExists()
    {
        if (File.Exists(filePath))
            return;

        string[] lines =
        {
            "# ======================================================================",
            "#  Chalk Observer",
            "# ======================================================================",
            "#",
            "#  Checks the 4 chalk canvases on a timer. If anything changed since the",
            "#  last picture, all 4 are drawn into ONE picture which is saved in the",
            "#  ChalkObserver folder (next to this file) and can be posted to Discord.",
            "#",
            "#  Picture layout:  canvas 0 = top left,     canvas 1 = top right,",
            "#                   canvas 2 = bottom left,  canvas 3 = bottom right.",
            "#",
            "#  checkInterval   = seconds between checks (minimum 10).",
            "#                    60 = every minute, 600 = every 10 minutes.",
            "#  scale           = how many pixels each chalk cell is drawn as (1-6).",
            "#                    Bigger is clearer but makes bigger files.",
            "#  saveImages      = true or false. Keep a copy in the ChalkObserver folder.",
            "#  maxSavedImages  = how many pictures to keep, the oldest are deleted.",
            "#                    0 = never delete anything.",
            "#  discordWebhook  = a Discord webhook address. Leave empty for no Discord.",
            "#                    Channel settings > Integrations > Webhooks > New Webhook",
            "#                    > Copy Webhook URL. KEEP IT PRIVATE, anyone with it can",
            "#                    post in your channel.",
            "#  discordMessage  = the text sent with the picture. \\n makes a line break.",
            "#",
            "#  You can edit and save this file while the server is running, it is",
            "#  picked up within about 10 seconds.",
            "#",
            "#  Colours (hex, like ff8800 or #ff8800). All of these are optional, remove the",
            "#  # in front of a line to change that colour.",
            "#",
            "#  background   = the colour of an empty canvas.",
            "#  colorN       = the colour for chalk colour number N (starts at color0).",
            "#                 Up to color255 works. If the log says a colour number has no",
            "#                 colour set, add a line for it here.",
            "#",
            "#    background = 6e6e6e",
            "@@COLORS@@",
            "# ======================================================================",
            "",
            "checkInterval = 60",
            "scale = 3",
            "saveImages = true",
            "debug = false",
            "maxSavedImages = 500",
            "",
            "discordWebhook =",
            "discordMessage = The chalk canvases changed.",
            "",
        };

        // one commented line per built in colour, written from the same list the plugin uses
        var colorLines = new StringBuilder();
        for (int i = 0; i < DefaultPaletteHex.Length; i++)
        {
            if (i > 0)
                colorLines.Append(Environment.NewLine);
            colorLines.Append($"#    color{i} = {DefaultPaletteHex[i]}");
        }

        string text = string.Join(Environment.NewLine, lines).Replace("@@COLORS@@", colorLines.ToString());

        File.WriteAllText(filePath, text);
        Log($"Created {FileName}, set your Discord webhook and check interval in it.");
    }
}
