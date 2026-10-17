using System.Text.RegularExpressions;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Watches chat for words or phrases and answers with a message you choose, for example somebody saying
/// "discord" gets your invite link. Rules are written in chattriggers.txt in the server's root directory
/// (next to server.cfg), which is created on first start and reloads about 10 seconds after it is saved.
/// </summary>
public class ChatTriggers : CovePlugin
{
    private const string FileName = "chattriggers.txt";
    private const int MaxRules = 200;
    private const int MaxReplies = 5;
    private const int DefaultCooldownSeconds = 30;
    private const long ReloadCheckMs = 10_000;

    private enum Scope { All, Global, Local }

    private class Rule
    {
        public int Number;
        public Regex Pattern = null!;
        public string Phrases = "";
        public List<string> Replies = new();
        public bool ToEveryone;
        public int CooldownSeconds = DefaultCooldownSeconds;
        public Scope Scope;
    }

    private class Outgoing
    {
        public string Text = "";
        public ulong? OnlyTo; // SteamID of the one player who gets it, null = everybody
    }

    // Chat arrives on the network thread, onUpdate on a timer thread, so all state is guarded by this lock.
    private readonly object sync = new();
    private List<Rule> rules = new();
    private readonly Dictionary<(int Rule, ulong Player), long> lastFired = new();
    private readonly List<Outgoing> queue = new();
    private string filePath = "";
    private DateTime loadedWriteTime = DateTime.MinValue;
    private long nextReloadCheckTick;
    private bool running;

    public ChatTriggers(CoveServer server) : base(server) { }

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
            lastFired.Clear();
        }
    }

    public override void onChatMessage(WFPlayer sender, string message, bool local, Vector3? position, string zone)
    {
        base.onChatMessage(sender, message, local, position, zone);

        // commands have their own handling
        if (message.StartsWith("!") || message.Trim().Length == 0)
            return;

        lock (sync)
        {
            if (!running || rules.Count == 0)
                return;

            long now = Environment.TickCount64;
            ulong id = sender.SteamId.m_SteamID;

            foreach (Rule rule in rules)
            {
                if ((rule.Scope == Scope.Global && local) || (rule.Scope == Scope.Local && !local))
                    continue;

                bool matched;
                try
                {
                    matched = rule.Pattern.IsMatch(message);
                }
                catch (RegexMatchTimeoutException)
                {
                    continue;
                }
                if (!matched)
                    continue;

                // a reply to everyone has one cooldown for the whole server, a private reply one per player,
                // otherwise a few people saying the word at once would flood the chat
                var key = (rule.Number, rule.ToEveryone ? 0UL : id);
                if (lastFired.TryGetValue(key, out long last) && now - last < rule.CooldownSeconds * 1000L)
                    continue;
                lastFired[key] = now;

                foreach (string reply in rule.Replies)
                {
                    queue.Add(new Outgoing
                    {
                        Text = reply.Replace("%player%", sender.Username)
                                    .Replace("%count%", GetAllPlayers().Length.ToString())
                                    .Replace("%message%", message),
                        OnlyTo = rule.ToEveryone ? null : id,
                    });
                }
            }
        }
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

            due.AddRange(queue);
            queue.Clear();
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

        // split the file into blocks, a new one starts at each [rule] line
        var blocks = new List<List<(string Key, string Value)>>();
        List<(string Key, string Value)>? current = null;

        foreach (string raw in File.ReadAllLines(filePath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.Equals("[rule]", StringComparison.OrdinalIgnoreCase))
            {
                current = new List<(string, string)>();
                blocks.Add(current);
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0 || current == null)
                continue; // text outside a [rule] is ignored

            current.Add((line.Substring(0, separator).Trim().ToLowerInvariant(), line.Substring(separator + 1).Trim()));
        }

        var loaded = new List<Rule>();
        for (int i = 0; i < blocks.Count && loaded.Count < MaxRules; i++)
        {
            Rule? rule = BuildRule(blocks[i], i + 1);
            if (rule != null)
                loaded.Add(rule);
        }

        rules = loaded;
        lastFired.Clear();
        Log($"Loaded {loaded.Count} chat trigger(s) from {FileName}.");
    }

    private Rule? BuildRule(List<(string Key, string Value)> entries, int number)
    {
        string Get(string key) => entries.LastOrDefault(e => e.Key == key).Value ?? "";

        var phrases = Get("match").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var replies = entries.Where(e => e.Key == "reply" && e.Value.Length > 0).Select(e => e.Value).Take(MaxReplies).ToList();

        if (phrases.Count == 0 || replies.Count == 0)
        {
            Log($"Rule {number} needs a match and at least one reply, so it is skipped.");
            return null;
        }

        bool wholeWord = !Get("wholeword").Equals("false", StringComparison.OrdinalIgnoreCase);

        string pattern = string.Join("|", phrases.Select(p =>
        {
            string escaped = Regex.Escape(p);
            return wholeWord ? $@"(?<!\w){escaped}(?!\w)" : escaped;
        }));

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException e)
        {
            Log($"Rule {number} has a match that cannot be used ({e.Message}), so it is skipped.");
            return null;
        }

        int cooldown = DefaultCooldownSeconds;
        if (Get("cooldown").Length > 0)
        {
            if (int.TryParse(Get("cooldown"), out int parsed) && parsed >= 0)
                cooldown = Math.Min(parsed, 86400);
            else
                Log($"Rule {number}: cooldown must be a number of seconds, using {DefaultCooldownSeconds}.");
        }

        Scope scope = Get("chat").ToLowerInvariant() switch
        {
            "global" => Scope.Global,
            "local" => Scope.Local,
            _ => Scope.All,
        };

        return new Rule
        {
            Number = number,
            Pattern = regex,
            Phrases = string.Join(" | ", phrases),
            Replies = replies,
            ToEveryone = !Get("to").Equals("player", StringComparison.OrdinalIgnoreCase),
            CooldownSeconds = cooldown,
            Scope = scope,
        };
    }

    private void EnsureFileExists()
    {
        if (File.Exists(filePath))
            return;

        string[] lines =
        {
            "# ======================================================================",
            "#  Chat Triggers",
            "# ======================================================================",
            "#",
            "#  When somebody says one of your words or phrases in chat, the server",
            "#  answers with a message you choose.",
            "#",
            "#  Every rule starts with a line that says [rule] and then has these lines:",
            "#",
            "#    match      = the word or phrase to look for. Put | between several,",
            "#                 like  discord | disc | invite   (any of them triggers it).",
            "#                 Capital letters don't matter.  REQUIRED",
            "#    reply      = what the server says. Add up to 5 reply lines, they are",
            "#                 sent in order.  REQUIRED",
            "#    to         = everyone (default) or player (only the person who said it)",
            "#    cooldown   = seconds before the rule can fire again, default 30.",
            "#                 A reply to everyone has one cooldown for the whole server,",
            "#                 a reply to player has one for each player.",
            "#    wholeWord  = true (default) only matches whole words, so \"disc\" does not",
            "#                 trigger on \"discard\". false matches inside words too.",
            "#    chat       = all (default), global or local. Which kind of chat counts.",
            "#",
            "#  In a reply you can use:",
            "#    %player%   the name of the person who said it",
            "#    %count%    how many players are online",
            "#    %message%  what they said",
            "#",
            "#  To add a rule, copy one of the blocks below to the bottom of the file",
            "#  and remove the # in front of each line. Add as many as you like.",
            "#  You can edit and save this file while the server is running, it is",
            "#  picked up within about 10 seconds.",
            "#",
            "#  Messages starting with ! are commands and never trigger a rule.",
            "# ======================================================================",
            "",
            "# [rule]",
            "# match = discord",
            "# reply = Join our Discord: https://discord.gg/yourcode",
            "# to = everyone",
            "# cooldown = 60",
            "",
            "# [rule]",
            "# match = rules | how do i play",
            "# reply = Hi %player%, please be kind to other fishers!",
            "# to = player",
            "",
        };

        File.WriteAllText(filePath, string.Join(Environment.NewLine, lines));
        Log($"Created {FileName}, add your rules to it.");
    }
}
