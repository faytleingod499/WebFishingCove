using System.Text.Json;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Adds the command  !feed  for everybody. Everybody sees a chat message saying who fed the wolves and how many
/// times they have done it, followed by a howl. Counts are kept by Steam ID in feed-counts.json, so they survive
/// restarts and name changes.
///
///   !feed     feed the wolves
///   !wolves   shows the top feeders and the server total
///
/// The wording, the cooldown and the size of the top list are set in feed.txt in the server's root directory
/// (next to server.cfg), which is created on first start and reloads about 10 seconds after it is saved.
/// </summary>
public class Feed : CovePlugin
{
    private const string CountsFileName = "feed-counts.json";
    private const string SettingsFileName = "feed.txt";
    private const long ReloadCheckMs = 10_000;
    private const int MaxReplies = 20;

    private sealed class Settings
    {
        public List<string> Replies = new() { "%player% fed the wolves. That's %times% so far!" };
        public string Howl = "Awooooooooo!";
        public int CooldownSeconds = 3;
        public int TopCount = 5;
        public string CooldownMessage = "The wolves are still eating, try again in a moment.";
        public string HungryMessage = "The wolves are getting hungry again...";
        public bool HungryToEveryone;
        public string WolvesSummary = "The wolves have been fed %total% times. You have fed them %count%.";
        public string NobodyFed = "Nobody has fed the wolves yet. Type !feed to be the first!";
    }

    private sealed class Record
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private readonly object sync = new();
    private readonly Dictionary<ulong, Record> counts = new();
    private readonly Dictionary<ulong, long> lastFed = new();
    private readonly Dictionary<ulong, long> hungryAt = new(); // when to tell a player the wolves are hungry again
    private string countsPath = "";
    private string settingsPath = "";
    private Settings settings = new();
    private DateTime loadedWriteTime = DateTime.MinValue;
    private long nextReloadCheckTick;
    private bool running;

    public Feed(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        string root = AppDomain.CurrentDomain.BaseDirectory;
        countsPath = Path.Combine(root, CountsFileName);
        settingsPath = Path.Combine(root, SettingsFileName);
        LoadCounts();

        lock (sync)
        {
            try
            {
                EnsureSettingsFileExists();
                ReloadSettings();
            }
            catch (Exception e)
            {
                Log($"Could not load {SettingsFileName}, using the defaults: {e.Message}");
            }
            nextReloadCheckTick = Environment.TickCount64 + ReloadCheckMs;
            running = true;
        }

        RegisterCommand(
            command: "feed",
            aliases: [],
            callback: (player, args) =>
            {
                try
                {
                    FeedWolves(player);
                }
                catch (Exception e)
                {
                    Log($"!feed failed: {e.Message}");
                }
            }
        );
        SetCommandDescription("feed", "Feed the wolves");

        RegisterCommand(
            command: "wolves",
            aliases: [],
            callback: (player, args) =>
            {
                try
                {
                    ShowTop(player);
                }
                catch (Exception e)
                {
                    Log($"!wolves failed: {e.Message}");
                }
            }
        );
        SetCommandDescription("wolves", "Shows who has fed the wolves the most");
    }

    public override void onEnd()
    {
        base.onEnd();
        lock (sync)
        {
            running = false;
            hungryAt.Clear();
        }
        ParentServer.UnregisterCommand("feed");
        ParentServer.UnregisterCommand("wolves");
    }

    // triggered 12 times a second by the server
    public override void onUpdate()
    {
        var hungry = new List<ulong>();
        Settings s;

        lock (sync)
        {
            if (!running)
                return;

            long now = Environment.TickCount64;
            s = settings;

            if (now >= nextReloadCheckTick)
            {
                nextReloadCheckTick = now + ReloadCheckMs;
                try
                {
                    if (File.Exists(settingsPath) && File.GetLastWriteTimeUtc(settingsPath) != loadedWriteTime)
                    {
                        ReloadSettings();
                        s = settings;
                    }
                }
                catch (Exception e)
                {
                    loadedWriteTime = DateTime.MinValue; // probably mid-save, try again at the next check
                    Log($"Could not reload {SettingsFileName}: {e.Message}");
                }
            }

            // players whose cooldown has just ended
            foreach (var pair in hungryAt.ToList())
            {
                if (pair.Value > now)
                    continue;
                hungry.Add(pair.Key);
                hungryAt.Remove(pair.Key);
            }
        }

        if (hungry.Count == 0 || s.HungryMessage.Length == 0)
            return;

        // sent outside of our lock
        try
        {
            WFPlayer[] online = GetAllPlayers();
            foreach (ulong id in hungry)
            {
                WFPlayer? who = online.FirstOrDefault(p => p.SteamId.m_SteamID == id);
                if (who == null)
                    continue; // they left in the meantime

                string text = s.HungryMessage.Replace("%player%", who.Username);
                if (s.HungryToEveryone)
                    SendGlobalChatMessage(text);
                else
                    SendPlayerChatMessage(who, text);
            }
        }
        catch (Exception e)
        {
            Log($"Could not send the hungry message: {e.Message}");
        }
    }

    private Settings GetSettings()
    {
        lock (sync)
            return settings;
    }

    private static string Fill(string text, string player, string times, int count, long total)
    {
        return text.Replace("%player%", player)
                   .Replace("%times%", times)
                   .Replace("%count%", count.ToString())
                   .Replace("%total%", total.ToString());
    }

    private void FeedWolves(WFPlayer player)
    {
        ulong id = player.SteamId.m_SteamID;
        int mine;
        long total;
        Settings s = GetSettings();

        lock (sync)
        {
            long now = Environment.TickCount64;
            if (s.CooldownSeconds > 0 && lastFed.TryGetValue(id, out long last) && now - last < s.CooldownSeconds * 1000L)
            {
                if (s.CooldownMessage.Length > 0)
                    SendPlayerChatMessage(player, s.CooldownMessage);
                return;
            }
            lastFed[id] = now;
            if (s.CooldownSeconds > 0)
                hungryAt[id] = now + s.CooldownSeconds * 1000L;

            if (!counts.TryGetValue(id, out Record? record))
                counts[id] = record = new Record();
            record.Name = player.Username;
            record.Count++;
            mine = record.Count;
            total = counts.Values.Sum(r => (long)r.Count);

            SaveCounts();
        }

        string times = mine == 1 ? "1 time" : $"{mine} times";
        if (s.Replies.Count > 0)
            SendGlobalChatMessage(Fill(s.Replies[Random.Shared.Next(s.Replies.Count)], player.Username, times, mine, total));
        if (s.Howl.Length > 0)
            SendGlobalChatMessage(Fill(s.Howl, player.Username, times, mine, total));
    }

    private void ShowTop(WFPlayer player)
    {
        List<Record> top;
        long total;
        int mine;
        Settings s = GetSettings();

        lock (sync)
        {
            top = counts.Values.OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(s.TopCount).ToList();
            total = counts.Values.Sum(r => (long)r.Count);
            mine = counts.TryGetValue(player.SteamId.m_SteamID, out Record? own) ? own.Count : 0;
        }

        if (total == 0)
        {
            SendPlayerChatMessage(player, s.NobodyFed);
            return;
        }

        string mineText = mine == 1 ? "1 time" : $"{mine} times";
        var lines = new List<string> { Fill(s.WolvesSummary, player.Username, mineText, mine, total) };
        for (int i = 0; i < top.Count; i++)
            lines.Add($"{i + 1}. {top[i].Name} - {top[i].Count}");

        SendPlayerChatMessage(player, string.Join("\n", lines));
    }

    // ---- settings ----

    private void ReloadSettings()
    {
        // remember the write time BEFORE reading, so an edit made while we read gets picked up next time
        loadedWriteTime = File.GetLastWriteTimeUtc(settingsPath);

        var next = new Settings();
        var replies = new List<string>();
        bool anyReplyLine = false;

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
                case "reply":
                    anyReplyLine = true;
                    if (value.Length > 0 && replies.Count < MaxReplies)
                        replies.Add(value);
                    break;
                case "howl":
                    next.Howl = value;
                    break;
                case "cooldown":
                    if (int.TryParse(value, out int cooldown) && cooldown >= 0)
                        next.CooldownSeconds = Math.Min(cooldown, 3600);
                    else
                        Log("cooldown must be a number of seconds, using 3.");
                    break;
                case "topcount":
                    if (int.TryParse(value, out int top) && top >= 1)
                        next.TopCount = Math.Min(top, 10);
                    else
                        Log("topCount must be a number from 1 to 10, using 5.");
                    break;
                case "cooldownmessage":
                    next.CooldownMessage = value;
                    break;
                case "hungrymessage":
                    next.HungryMessage = value;
                    break;
                case "hungryto":
                    next.HungryToEveryone = value.Equals("everyone", StringComparison.OrdinalIgnoreCase);
                    break;
                case "wolvessummary":
                    next.WolvesSummary = value;
                    break;
                case "nobodyfed":
                    next.NobodyFed = value;
                    break;
            }
        }

        // reply lines that are all empty mean nothing is said, no reply lines at all keep the default
        if (anyReplyLine)
            next.Replies = replies;

        settings = next;
        Log($"Loaded {SettingsFileName}.");
    }

    private void EnsureSettingsFileExists()
    {
        if (File.Exists(settingsPath))
            return;

        string[] lines =
        {
            "# ======================================================================",
            "#  Feed The Wolves",
            "# ======================================================================",
            "#",
            "#  Settings for !feed and !wolves. You can edit and save this file while the",
            "#  server is running, it is picked up within about 10 seconds.",
            "#",
            "#  In the messages you can use:",
            "#    %player%   the name of the player who fed the wolves",
            "#    %times%    how many times that player has fed them: \"1 time\", \"12 times\"",
            "#    %count%    the same number on its own: 12",
            "#    %total%    how many times everybody together has fed them",
            "#",
            "#  reply            = what the server says when somebody types !feed. Add as many",
            "#                     reply lines as you like, one is picked at random each time.",
            "#  howl             = a second line sent right after the reply. Leave empty for none.",
            "#  cooldown         = seconds a player must wait between feeds, 0 for none.",
            "#  cooldownMessage  = what a player is told if they feed again too soon.",
            "#  hungryMessage    = sent when a player's cooldown is over and they can feed the wolves",
            "#                     again. Leave empty for none. Needs a cooldown above 0.",
            "#  hungryTo         = player (only the one who fed them) or everyone.",
            "#  topCount         = how many players !wolves lists, 1 to 10.",
            "#  wolvesSummary    = the first line of !wolves, shown only to the player who typed it.",
            "#  nobodyFed        = what !wolves says before anybody has fed the wolves.",
            "# ======================================================================",
            "",
            "reply = %player% fed the wolves. That's %times% so far!",
            "howl = Awooooooooo!",
            "cooldown = 3",
            "cooldownMessage = The wolves are still eating, try again in a moment.",
            "hungryMessage = The wolves are getting hungry again...",
            "hungryTo = player",
            "",
            "topCount = 5",
            "wolvesSummary = The wolves have been fed %total% times. You have fed them %count%.",
            "nobodyFed = Nobody has fed the wolves yet. Type !feed to be the first!",
            "",
        };

        File.WriteAllText(settingsPath, string.Join(Environment.NewLine, lines));
        Log($"Created {SettingsFileName}, edit it to change the messages.");
    }

    // ---- saving ----

    private void LoadCounts()
    {
        lock (sync)
        {
            counts.Clear();
            try
            {
                if (!File.Exists(countsPath))
                    return;

                var loaded = JsonSerializer.Deserialize<Dictionary<ulong, Record>>(File.ReadAllText(countsPath));
                if (loaded != null)
                    foreach (var pair in loaded)
                        if (pair.Value.Count > 0)
                            counts[pair.Key] = pair.Value;

                Log($"Loaded the feed counts of {counts.Count} player(s).");
            }
            catch (Exception e)
            {
                // keep the unreadable file so nobody's count is lost, and start fresh
                Log($"Could not read {CountsFileName}: {e.Message}");
                try { File.Move(countsPath, countsPath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); }
                catch { /* nothing more we can do */ }
            }
        }
    }

    // called with the lock held, the file is tiny
    private void SaveCounts()
    {
        try
        {
            string temp = countsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(counts, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, countsPath, overwrite: true);
        }
        catch (Exception e)
        {
            Log($"Could not save {CountsFileName}: {e.Message}");
        }
    }
}
