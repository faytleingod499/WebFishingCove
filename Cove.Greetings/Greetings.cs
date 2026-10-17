using System.Text.RegularExpressions;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Welcome message sent once a player has really loaded in, plus replies to the game's own event
/// messages (rank ups, "caught them all" and so on). Everything is set in greetings.txt in the server's
/// root directory (next to server.cfg), which is created on first start and reloads when saved.
///
/// "Loaded in" means the player's first position update after joining. The join packet and the moment
/// their character is created both arrive while the game is still loading, so messages sent then are missed.
/// </summary>
public class Greetings : CovePlugin
{
    private const string FileName = "greetings.txt";
    private const int WelcomeLines = 3;
    private const int EventRules = 6;
    private const long ReloadCheckMs = 10_000;
    private const int MaxDelaySeconds = 60;

    private class Settings
    {
        public List<string> Welcome = new();
        public bool WelcomeToEveryone;
        public int WelcomeDelaySeconds = 2;
        public string LevelUp = "";
        public string LevelUpMatch = "ranked up to rank";
        public bool LevelUpToPlayer;
        public List<EventRule> Events = new();
        public bool LogEvents = true;
    }

    private class EventRule
    {
        public int Slot;
        public string Match = "";
        public string Message = "";
        public bool ToPlayer;
    }

    private class Outgoing
    {
        public long DueTick;
        public string Text = "";
        public ulong? OnlyTo; // SteamID of the one player who gets it, null = everybody
    }

    // The hooks run on different threads, so all state below is guarded by this lock.
    private readonly object sync = new();
    private Settings settings = new();
    private readonly HashSet<ulong> waitingForFirstMove = new();
    private readonly List<Outgoing> queue = new();
    private string filePath = "";
    private DateTime loadedWriteTime = DateTime.MinValue;
    private long nextReloadCheckTick;
    private bool running;

    public Greetings(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();
        filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        lock (sync)
        {
            try
            {
                EnsureFileExists();
                Reload();
            }
            catch (Exception e)
            {
                Log($"Could not load {FileName}: {e.Message}");
            }
            nextReloadCheckTick = Environment.TickCount64 + ReloadCheckMs;
            running = true;
        }
    }

    public override void onEnd()
    {
        base.onEnd();
        lock (sync)
        {
            running = false;
            queue.Clear();
            waitingForFirstMove.Clear();
        }
    }

    public override void onPlayerJoin(WFPlayer player)
    {
        base.onPlayerJoin(player);
        lock (sync)
        {
            if (running)
                waitingForFirstMove.Add(player.SteamId.m_SteamID);
        }
    }

    public override void onPlayerLeave(WFPlayer player)
    {
        base.onPlayerLeave(player);
        lock (sync)
        {
            waitingForFirstMove.Remove(player.SteamId.m_SteamID);
        }
    }

    public override void onNetworkPacket(WFPlayer sender, Dictionary<string, object> packet)
    {
        base.onNetworkPacket(sender, packet);

        if (!packet.TryGetValue("type", out object? rawType) || rawType is not string type)
            return;

        if (type == "actor_update")
            HandleMove(sender, packet);
        else if (type == "message")
            HandleMessage(sender, packet);
    }

    // the player's own position updates start once they are in the world
    private void HandleMove(WFPlayer sender, Dictionary<string, object> packet)
    {
        lock (sync)
        {
            if (!running || !waitingForFirstMove.Contains(sender.SteamId.m_SteamID))
                return;

            if (!packet.TryGetValue("actor_id", out object? rawId) || !TryToLong(rawId, out long actorId)
                || actorId != sender.InstanceID)
                return; // an update for something else the player owns

            waitingForFirstMove.Remove(sender.SteamId.m_SteamID);

            Settings s = settings;
            long due = Environment.TickCount64 + s.WelcomeDelaySeconds * 1000L;
            foreach (string line in s.Welcome)
            {
                queue.Add(new Outgoing
                {
                    DueTick = due,
                    Text = Fill(line, sender.Username),
                    OnlyTo = s.WelcomeToEveryone ? null : sender.SteamId.m_SteamID,
                });
            }

            if (s.LogEvents)
                Log($"{sender.Username} finished loading in, welcome queued.");
        }
    }

    // Event messages from the game are sent by the client without the "%u:" chat prefix,
    // for example "%u ranked up to rank 67!". Normal chat always starts with "%u:" and /me with "(%u".
    private void HandleMessage(WFPlayer sender, Dictionary<string, object> packet)
    {
        if (!packet.TryGetValue("message", out object? rawText) || rawText is not string raw)
            return;
        if (raw.StartsWith("%u:") || raw.StartsWith("(%u") || !raw.Contains("%u"))
            return;

        string text = raw.Replace("%u", sender.Username);

        lock (sync)
        {
            if (!running)
                return;

            Settings s = settings;
            if (s.LogEvents)
                Log($"Game event from {sender.Username}: {text}");

            if (s.LevelUp.Length > 0 && s.LevelUpMatch.Length > 0
                && text.Contains(s.LevelUpMatch, StringComparison.OrdinalIgnoreCase))
            {
                Match rank = Regex.Match(text, @"rank\s+(\d+)", RegexOptions.IgnoreCase);
                string message = Fill(s.LevelUp, sender.Username)
                    .Replace("%rank%", rank.Success ? rank.Groups[1].Value : "");
                Enqueue(message, s.LevelUpToPlayer ? sender : null);
            }

            foreach (EventRule rule in s.Events)
            {
                if (text.Contains(rule.Match, StringComparison.OrdinalIgnoreCase))
                    Enqueue(Fill(rule.Message, sender.Username).Replace("%event%", text), rule.ToPlayer ? sender : null);
            }
        }
    }

    private void Enqueue(string text, WFPlayer? onlyTo)
    {
        if (text.Trim().Length == 0)
            return;
        queue.Add(new Outgoing
        {
            DueTick = Environment.TickCount64,
            Text = text,
            OnlyTo = onlyTo?.SteamId.m_SteamID,
        });
    }

    // triggered 12 times a second by the server
    public override void onUpdate()
    {
        var due = new List<Outgoing>();

        lock (sync)
        {
            if (!running)
                return;

            long now = Environment.TickCount64;

            if (now >= nextReloadCheckTick)
            {
                nextReloadCheckTick = now + ReloadCheckMs;
                ReloadIfChanged();
            }

            // keep the order messages were queued in
            foreach (Outgoing o in queue.ToList())
            {
                if (o.DueTick > now)
                    continue;
                due.Add(o);
                queue.Remove(o);
            }
        }

        // send outside of our lock
        foreach (Outgoing o in due)
        {
            try
            {
                if (o.OnlyTo == null)
                {
                    SendGlobalChatMessage(o.Text);
                }
                else
                {
                    WFPlayer? target = GetAllPlayers().FirstOrDefault(p => p.SteamId.m_SteamID == o.OnlyTo);
                    if (target != null) // they may have left in the meantime
                        SendPlayerChatMessage(target, o.Text);
                }
            }
            catch (Exception e)
            {
                Log($"Could not send a message: {e.Message}");
            }
        }
    }

    private string Fill(string text, string player)
    {
        return text.Replace("%player%", player)
                   .Replace("%count%", GetAllPlayers().Length.ToString());
    }

    private static bool TryToLong(object value, out long result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case double d: result = (long)d; return true;
            case float f: result = (long)f; return true;
            default: result = 0; return false;
        }
    }

    private void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(filePath) || File.GetLastWriteTimeUtc(filePath) == loadedWriteTime)
                return;
            Reload();
        }
        catch (Exception e)
        {
            loadedWriteTime = DateTime.MinValue; // probably mid-save, try again at the next check
            Log($"Could not reload {FileName}: {e.Message}");
        }
    }

    private void Reload()
    {
        loadedWriteTime = File.GetLastWriteTimeUtc(filePath);
        Dictionary<string, string> v = ReadValues();
        var next = new Settings();

        for (int i = 1; i <= WelcomeLines; i++)
        {
            string line = Text(v, $"welcomeMessage{i}");
            if (line.Length > 0)
                next.Welcome.Add(line);
        }
        next.WelcomeToEveryone = Text(v, "welcomeTo").Equals("everyone", StringComparison.OrdinalIgnoreCase);

        if (int.TryParse(Text(v, "welcomeDelay"), out int delay))
            next.WelcomeDelaySeconds = Math.Clamp(delay, 0, MaxDelaySeconds);

        next.LevelUp = Text(v, "levelUpMessage");
        if (v.ContainsKey("levelUpMatch"))
            next.LevelUpMatch = Text(v, "levelUpMatch");
        next.LevelUpToPlayer = Text(v, "levelUpTo").Equals("player", StringComparison.OrdinalIgnoreCase);

        for (int i = 1; i <= EventRules; i++)
        {
            string match = Text(v, $"event{i}Match");
            string message = Text(v, $"event{i}Message");
            if (match.Length == 0 || message.Length == 0)
                continue; // slot is off
            next.Events.Add(new EventRule
            {
                Slot = i,
                Match = match,
                Message = message,
                ToPlayer = Text(v, $"event{i}To").Equals("player", StringComparison.OrdinalIgnoreCase),
            });
        }

        if (v.TryGetValue("logEvents", out string? log))
            next.LogEvents = !log.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

        settings = next;
        Log($"Loaded {FileName}: {next.Welcome.Count} welcome line(s), {next.Events.Count} event rule(s).");
    }

    private static string Text(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? raw) ? raw.Replace("\\n", " ").Trim() : "";
    }

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
            "#  Greetings",
            "# ======================================================================",
            "#",
            "#  You can edit and save this file while the server is running, it is",
            "#  picked up within about 10 seconds. Lines starting with # are ignored.",
            "#",
            "#  In any message you can use:",
            "#    %player%   the player's name",
            "#    %count%    how many players are online",
            "#",
            "#  WELCOME",
            "#  Sent when a player has finished loading in (their first position",
            "#  update), so they actually see it. Up to 3 lines, sent in order.",
            "#  welcomeTo     = player (only the new player) or everyone",
            "#  welcomeDelay  = extra seconds to wait after they load, 0 to 60",
            "#",
            "welcomeMessage1 = Welcome to the server, %player%!",
            "welcomeMessage2 =",
            "welcomeMessage3 =",
            "welcomeTo = player",
            "welcomeDelay = 2",
            "",
            "# LEVEL UP",
            "#  Sent when the game announces a rank up. %rank% is the new rank.",
            "#  levelUpMatch is the text to look for in the game's message.",
            "#  levelUpTo     = everyone or player",
            "#  Leave levelUpMessage empty to turn it off.",
            "#",
            "levelUpMessage = Congratulations %player%, you reached rank %rank%!",
            "levelUpMatch = ranked up to rank",
            "levelUpTo = everyone",
            "",
            "# OTHER GAME MESSAGES (for example catching every fish of a quality)",
            "#  With logEvents = true, every message the game sends for a player is written to",
            "#  the server log as  Game event from <name>: <text>. Trigger one in the game,",
            "#  copy a piece of its text and put it in eventNMatch. The match ignores capital",
            "#  letters and only has to be part of the message. Up to 6 rules.",
            "#  %event% in a reply is the full game message.",
            "#",
            "#  Example:",
            "#    event1Match   = caught all",
            "#    event1Message = Wow %player%, what a collection!",
            "#    event1To      = everyone",
            "#",
            "logEvents = true",
            "",
            "event1Match =",
            "event1Message =",
            "event1To = everyone",
            "",
            "event2Match =",
            "event2Message =",
            "event2To = everyone",
            "",
            "event3Match =",
            "event3Message =",
            "event3To = everyone",
            "",
            "event4Match =",
            "event4Message =",
            "event4To = everyone",
            "",
            "event5Match =",
            "event5Message =",
            "event5To = everyone",
            "",
            "event6Match =",
            "event6Message =",
            "event6To = everyone",
            "",
        };

        File.WriteAllText(filePath, string.Join(Environment.NewLine, lines));
        Log($"Created {FileName}, edit it to change the messages.");
    }
}
