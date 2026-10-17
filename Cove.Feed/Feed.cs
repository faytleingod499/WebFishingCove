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
/// To change the wording, edit Replies and Howl below and rebuild.
/// </summary>
public class Feed : CovePlugin
{
    private const string CountsFileName = "feed-counts.json";
    private const long CooldownMs = 3000;
    private const int TopCount = 5;

    // {0} = the player's name, {1} = how many times they have fed the wolves ("1 time", "12 times"),
    // {2} = how many times everybody together has
    private static readonly string[] Replies =
    [
        "{0} fed the wolves. That's {1} so far!",
    ];

    // sent as a second line right after the reply
    private const string Howl = "Awooooooooo!";

    private sealed class Record
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    private readonly object sync = new();
    private readonly Dictionary<ulong, Record> counts = new();
    private readonly Dictionary<ulong, long> lastFed = new();
    private string countsPath = "";

    public Feed(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        countsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CountsFileName);
        LoadCounts();

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
        ParentServer.UnregisterCommand("feed");
        ParentServer.UnregisterCommand("wolves");
    }

    private void FeedWolves(WFPlayer player)
    {
        ulong id = player.SteamId.m_SteamID;
        int mine;
        long total;

        lock (sync)
        {
            long now = Environment.TickCount64;
            if (lastFed.TryGetValue(id, out long last) && now - last < CooldownMs)
            {
                SendPlayerChatMessage(player, "The wolves are still eating, try again in a moment.");
                return;
            }
            lastFed[id] = now;

            if (!counts.TryGetValue(id, out Record? record))
                counts[id] = record = new Record();
            record.Name = player.Username;
            record.Count++;
            mine = record.Count;
            total = counts.Values.Sum(r => (long)r.Count);

            SaveCounts();
        }

        string times = mine == 1 ? "1 time" : $"{mine} times";
        SendGlobalChatMessage(string.Format(Replies[Random.Shared.Next(Replies.Length)], player.Username, times, total));
        SendGlobalChatMessage(Howl);
    }

    private void ShowTop(WFPlayer player)
    {
        List<Record> top;
        long total;
        int mine;

        lock (sync)
        {
            top = counts.Values.OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(TopCount).ToList();
            total = counts.Values.Sum(r => (long)r.Count);
            mine = counts.TryGetValue(player.SteamId.m_SteamID, out Record? own) ? own.Count : 0;
        }

        if (total == 0)
        {
            SendPlayerChatMessage(player, "Nobody has fed the wolves yet. Type !feed to be the first!");
            return;
        }

        var lines = new List<string> { $"The wolves have been fed {total} times. You have fed them {mine}." };
        for (int i = 0; i < top.Count; i++)
            lines.Add($"{i + 1}. {top[i].Name} - {top[i].Count}");

        SendPlayerChatMessage(player, string.Join("\n", lines));
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
