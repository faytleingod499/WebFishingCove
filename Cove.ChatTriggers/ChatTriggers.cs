using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Watches chat for words or phrases and answers with a message you choose, for example somebody saying
/// "discord" gets your invite link. A rule can also kick or ban whoever says it (never admins), and it can count
/// how many times a player has set it off and only act on the Nth time, warning them before that. Rules are written
/// in chattriggers.txt in the server's root directory (next to server.cfg), which is created on first start and
/// reloads about 10 seconds after it is saved. The strike counts are kept in chattriggers-strikes.json.
/// </summary>
public class ChatTriggers : CovePlugin
{
    private const string FileName = "chattriggers.txt";
    private const int MaxRules = 200;
    private const int MaxReplies = 5;
    private const int DefaultCooldownSeconds = 30;
    private const long ReloadCheckMs = 10_000;
    private const string StrikesFileName = "chattriggers-strikes.json";
    private const int MaxStrikes = 100;

    private enum Scope { All, Global, Local }

    private enum RuleAction { Reply, Kick, Ban }

    private class Rule
    {
        public int Number;
        public Regex Pattern = null!;
        public string Phrases = "";
        public List<string> Replies = new();
        public bool ToEveryone;
        public int CooldownSeconds = DefaultCooldownSeconds;
        public Scope Scope;
        public RuleAction Action;
        public string Reason = "";

        // strikes: the action only happens on the Nth time, before that the replies are warnings
        public string Key = "";
        public int Strikes = 1;
        public List<string> FinalReplies = new();
        public int ExpiresMinutes;
        public bool KeepAfterAction;
    }

    private sealed class StrikeRecord
    {
        public int Count { get; set; }
        public DateTime Last { get; set; } = DateTime.UtcNow;
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
    private readonly Dictionary<(string Rule, ulong Player), StrikeRecord> strikes = new();
    private string strikesPath = "";
    private string filePath = "";
    private DateTime loadedWriteTime = DateTime.MinValue;
    private long nextReloadCheckTick;
    private bool running;

    public ChatTriggers(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();
        filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);
        strikesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, StrikesFileName);

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
            LoadStrikes();
            nextReloadCheckTick = Environment.TickCount64 + ReloadCheckMs;
            running = true;
        }

        RegisterCommand(
            command: "strikes",
            aliases: [],
            callback: (player, args) => Safely(player, "strikes", () => ShowStrikes(player, args))
        );
        SetCommandDescription("strikes", "Shows the chat trigger strikes of a player, or of everybody | Usage: !strikes [player]");
        SetCommandAccess("strikes", CommandAccess.Admin);

        RegisterCommand(
            command: "clearstrikes",
            aliases: [],
            callback: (player, args) => Safely(player, "clearstrikes", () => ClearStrikes(player, args))
        );
        SetCommandDescription("clearstrikes", "Resets the chat trigger strikes of a player | Usage: !clearstrikes <player>");
        SetCommandAccess("clearstrikes", CommandAccess.Admin);
    }

    public override void onEnd()
    {
        base.onEnd();
        ParentServer.UnregisterCommand("strikes");
        ParentServer.UnregisterCommand("clearstrikes");
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

        var actions = new List<Rule>();

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

                // a rule with strikes counts every time, and warns instead of using the cooldown
                if (rule.Strikes > 1)
                {
                    CountStrike(rule, sender, message, actions);
                    continue;
                }

                // a reply to everyone has one cooldown for the whole server, a private reply one per player,
                // otherwise a few people saying the word at once would flood the chat
                var key = (rule.Number, rule.ToEveryone ? 0UL : id);
                bool coolingDown = lastFired.TryGetValue(key, out long last) && now - last < rule.CooldownSeconds * 1000L;

                // a kick or ban always happens, the cooldown only keeps the replies from repeating
                if (rule.Action != RuleAction.Reply)
                    actions.Add(rule);
                if (coolingDown)
                    continue;
                lastFired[key] = now;

                foreach (string reply in rule.Replies)
                {
                    queue.Add(new Outgoing
                    {
                        Text = Fill(reply, sender, message),
                        OnlyTo = rule.ToEveryone ? null : id,
                    });
                }
            }
        }

        // done outside our lock, like the built in !kick and !ban commands
        foreach (Rule rule in actions)
            Punish(sender, rule, message);
    }

    private string Fill(string text, WFPlayer sender, string message, int strike = 0, int limit = 0)
    {
        return text.Replace("%player%", sender.Username)
                   .Replace("%count%", GetAllPlayers().Length.ToString())
                   .Replace("%message%", message)
                   .Replace("%strike%", strike.ToString())
                   .Replace("%strikes%", limit.ToString())
                   .Replace("%left%", Math.Max(0, limit - strike).ToString());
    }

    // called with the lock held
    private void CountStrike(Rule rule, WFPlayer sender, string message, List<Rule> actions)
    {
        ulong id = sender.SteamId.m_SteamID;
        DateTime now = DateTime.UtcNow;

        if (!strikes.TryGetValue((rule.Key, id), out StrikeRecord? record))
            strikes[(rule.Key, id)] = record = new StrikeRecord();
        else if (rule.ExpiresMinutes > 0 && now - record.Last > TimeSpan.FromMinutes(rule.ExpiresMinutes))
            record.Count = 0; // old strikes run out

        record.Count++;
        record.Last = now;

        int strike = record.Count;
        bool last = strike >= rule.Strikes;
        List<string> lines = last && rule.FinalReplies.Count > 0 ? rule.FinalReplies : rule.Replies;

        foreach (string line in lines)
        {
            queue.Add(new Outgoing
            {
                Text = Fill(line, sender, message, strike, rule.Strikes),
                OnlyTo = rule.ToEveryone ? null : sender.SteamId.m_SteamID,
            });
        }

        if (last)
        {
            if (rule.Action != RuleAction.Reply)
                actions.Add(rule);
            if (!rule.KeepAfterAction)
                record.Count = 0;
        }

        Log($"{sender.Username} set off \"{rule.Key}\": strike {strike} of {rule.Strikes}{(last ? ", the limit" : "")}.");
        SaveStrikes();
    }

    private void Punish(WFPlayer player, Rule rule, string message)
    {
        try
        {
            // admins and the host are never punished, a typo in a rule must not be able to remove them
            if (ParentServer.isPlayerAdmin(player.SteamId))
                return;

            string reason = rule.Reason.Length > 0 ? rule.Reason : $"Said \"{rule.Phrases}\" (chat trigger {rule.Number})";

            if (rule.Action == RuleAction.Ban)
            {
                bool alreadyBanned = ParentServer.isPlayerBanned(player.SteamId);
                ParentServer.banPlayer(player.SteamId, !alreadyBanned, reason);
                Log($"Banned {player.Username} ({player.SteamId.m_SteamID}) with rule {rule.Number}. They said: {message}");
            }
            else
            {
                ParentServer.kickPlayer(player.SteamId);
                Log($"Kicked {player.Username} ({player.SteamId.m_SteamID}) with rule {rule.Number}. They said: {message}");
            }
        }
        catch (Exception e)
        {
            Log($"Rule {rule.Number} could not {rule.Action.ToString().ToLowerInvariant()} {player.Username}: {e.Message}");
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

        string actionText = Get("action").ToLowerInvariant();
        RuleAction action = actionText switch
        {
            "kick" => RuleAction.Kick,
            "ban" => RuleAction.Ban,
            _ => RuleAction.Reply,
        };
        if (actionText.Length > 0 && actionText != "reply" && actionText != "kick" && actionText != "ban")
            Log($"Rule {number}: action must be reply, kick or ban, using reply.");

        // a kick or ban does not need a reply, a plain reply rule does
        if (phrases.Count == 0 || (replies.Count == 0 && action == RuleAction.Reply))
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

        string name = Get("name").Trim();
        int strikeLimit = 1;
        if (Get("strikes").Length > 0)
        {
            if (int.TryParse(Get("strikes"), out int parsedStrikes) && parsedStrikes >= 1)
                strikeLimit = Math.Min(parsedStrikes, MaxStrikes);
            else
                Log($"Rule {number}: strikes must be a whole number from 1 up, using 1.");
        }
        if (strikeLimit > 1 && name.Length == 0)
            Log($"Rule {number} counts strikes but has no name. Add a  name = ...  line, or its counts will be lost if the rules are moved around.");

        int expires = 0;
        if (Get("strikeexpires").Length > 0)
        {
            if (int.TryParse(Get("strikeexpires"), out int parsedExpires) && parsedExpires >= 0)
                expires = Math.Min(parsedExpires, 525_600);
            else
                Log($"Rule {number}: strikeExpires must be a number of minutes, using 0 (never).");
        }

        return new Rule
        {
            Key = name.Length > 0 ? name.ToLowerInvariant() : $"rule{number}",
            Strikes = strikeLimit,
            FinalReplies = entries.Where(e => e.Key == "finalreply" && e.Value.Length > 0).Select(e => e.Value).Take(MaxReplies).ToList(),
            ExpiresMinutes = expires,
            KeepAfterAction = Get("afteraction").Equals("keep", StringComparison.OrdinalIgnoreCase),
            Number = number,
            Pattern = regex,
            Phrases = string.Join(" | ", phrases),
            Replies = replies,
            ToEveryone = !Get("to").Equals("player", StringComparison.OrdinalIgnoreCase),
            CooldownSeconds = cooldown,
            Scope = scope,
            Action = action,
            Reason = Get("reason"),
        };
    }

    // ---- strikes ----

    private void Safely(WFPlayer player, string command, Action run)
    {
        try
        {
            run();
        }
        catch (Exception e)
        {
            Log($"!{command} failed: {e.Message}");
            SendPlayerChatMessage(player, "Something went wrong, see the server log.");
        }
    }

    // a player who is online by name or fisher ID, or any Steam ID
    private bool TryFindPlayer(string ident, out ulong steamId, out string name)
    {
        steamId = 0;
        name = ident;

        WFPlayer? found = ParentServer.GetPlayer(ident);
        if (found != null)
        {
            steamId = found.SteamId.m_SteamID;
            name = found.Username;
            return true;
        }

        if (ulong.TryParse(ident, out steamId) && steamId > 0)
        {
            name = GetAllPlayers().FirstOrDefault(p => p.SteamId.m_SteamID == steamId)?.Username ?? ident;
            return true;
        }
        return false;
    }

    private void ShowStrikes(WFPlayer player, string[] args)
    {
        string ident = string.Join(" ", args).Trim();
        ulong only = 0;
        string who = "";

        if (ident.Length > 0 && !TryFindPlayer(ident, out only, out who))
        {
            SendPlayerChatMessage(player, $"No player called \"{ident}\" is online. Use their Steam ID instead.");
            return;
        }

        var lines = new List<string>();
        lock (sync)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var entry in strikes.OrderBy(e => e.Key.Player).ThenBy(e => e.Key.Rule))
            {
                if (only != 0 && entry.Key.Player != only)
                    continue;

                Rule? rule = rules.FirstOrDefault(r => r.Key == entry.Key.Rule);
                if (rule == null || entry.Value.Count == 0)
                    continue; // the rule was removed, or the count was reset
                if (rule.ExpiresMinutes > 0 && now - entry.Value.Last > TimeSpan.FromMinutes(rule.ExpiresMinutes))
                    continue; // run out

                string name = GetAllPlayers().FirstOrDefault(p => p.SteamId.m_SteamID == entry.Key.Player)?.Username ?? entry.Key.Player.ToString();
                lines.Add($"{name}: {rule.Key} {entry.Value.Count}/{rule.Strikes}");
            }
        }

        if (lines.Count == 0)
        {
            SendPlayerChatMessage(player, only == 0 ? "Nobody has any strikes." : $"{who} has no strikes.");
            return;
        }
        SendPlayerChatMessage(player, "Strikes:\n" + string.Join("\n", lines.Take(15)) + (lines.Count > 15 ? $"\nand {lines.Count - 15} more" : ""));
    }

    private void ClearStrikes(WFPlayer player, string[] args)
    {
        string ident = string.Join(" ", args).Trim();
        if (ident.Length == 0 || !TryFindPlayer(ident, out ulong steamId, out string who))
        {
            SendPlayerChatMessage(player, ident.Length == 0 ? "Usage: !clearstrikes <player>" : $"No player called \"{ident}\" is online. Use their Steam ID instead.");
            return;
        }

        int cleared;
        lock (sync)
        {
            var keys = strikes.Keys.Where(k => k.Player == steamId).ToList();
            foreach (var key in keys)
                strikes.Remove(key);
            cleared = keys.Count;
            if (cleared > 0)
                SaveStrikes();
        }

        Log($"{player.Username} cleared the strikes of {who} ({steamId}).");
        SendPlayerChatMessage(player, cleared == 0 ? $"{who} has no strikes." : $"Cleared the strikes of {who}.");
    }

    // called with the lock held, the file is small
    private void SaveStrikes()
    {
        try
        {
            var data = new Dictionary<string, Dictionary<ulong, StrikeRecord>>();
            foreach (var entry in strikes)
            {
                if (entry.Value.Count == 0)
                    continue;
                if (!data.TryGetValue(entry.Key.Rule, out var perPlayer))
                    data[entry.Key.Rule] = perPlayer = new Dictionary<ulong, StrikeRecord>();
                perPlayer[entry.Key.Player] = entry.Value;
            }

            string temp = strikesPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, strikesPath, overwrite: true);
        }
        catch (Exception e)
        {
            Log($"Could not save {StrikesFileName}: {e.Message}");
        }
    }

    private void LoadStrikes()
    {
        strikes.Clear();
        try
        {
            if (!File.Exists(strikesPath))
                return;

            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<ulong, StrikeRecord>>>(File.ReadAllText(strikesPath));
            if (data == null)
                return;
            foreach (var rule in data)
                foreach (var player in rule.Value)
                    strikes[(rule.Key, player.Key)] = player.Value;
        }
        catch (Exception e)
        {
            // keep the unreadable file so nobody's strikes are lost, and start fresh
            Log($"Could not read {StrikesFileName}: {e.Message}");
            try { File.Move(strikesPath, strikesPath + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); }
            catch { /* nothing more we can do */ }
        }
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
            "#    action     = reply (default), kick or ban. Kick or ban whoever says it.",
            "#                 Admins and the host are never kicked or banned. A ban is",
            "#                 permanent and saved in bans.txt. With kick or ban the reply",
            "#                 lines are optional, use them to announce it. Try a kick first,",
            "#                 and keep wholeWord = true so a word inside another word",
            "#                 can't catch an innocent player.",
            "#    reason     = optional, written in bans.txt next to a ban.",
            "#",
            "#  STRIKES: count how many times a player sets a rule off, and only act on the Nth",
            "#  time. Before that, the reply lines are warnings.",
            "#    name         = a name for the rule, like  name = swearing . Use it on every rule with",
            "#                   strikes, the counts are saved under it (in chattriggers-strikes.json).",
            "#    strikes      = how many times before the action, default 1 (no counting). With 3,",
            "#                   the first two are warnings and the third kicks or bans. A player's",
            "#                   count is kept across restarts and when they leave and come back.",
            "#                   Rules with strikes ignore cooldown, every time counts.",
            "#    finalReply   = optional. Said on the last strike instead of the reply lines.",
            "#                   Up to 5 lines, like reply.",
            "#    strikeExpires = minutes without a new strike before the count starts over,",
            "#                   default 0 (never).",
            "#    afterAction  = reset (default) or keep. After the action the count starts over, or",
            "#                   with keep the player is acted on again the next time they set it off.",
            "#  In replies you can also use %strike% (which one this is), %strikes% (the limit) and",
            "#  %left% (how many are left before the action).",
            "#  Admins see the warnings and are counted but are never kicked or banned.",
            "#  !strikes [player] shows the counts and !clearstrikes <player> forgives them.",
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
            "# name = swearing",
            "# match = some bad word | another bad word",
            "# strikes = 3",
            "# action = kick",
            "# to = player",
            "# reply = Please watch your language, %player%. Strike %strike% of %strikes%.",
            "# finalReply = That was strike %strike%, you have been removed from the server.",
            "# strikeExpires = 1440",
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
