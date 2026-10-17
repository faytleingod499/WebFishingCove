using System.Globalization;
using System.Text.Json;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;
using Steamworks;

/// <summary>
/// Adds the command  !feed  for everybody. Everybody sees a chat message saying who fed the wolves and how many
/// times they have done it, followed by a howl. Counts are kept by Steam ID in feed-counts.json, so they survive
/// restarts and name changes.
///
///   !feed     feed the wolves, which also earns coins
///   !coins    shows how many coins you have
///   !meteor   spends coins to call down a meteor
///   !shop     lists everything coins can buy (set in shop.txt), !buy <name> buys it
///   !gamble   bets coins on a wolf roll, !gamble <coins>
///   !wolves   shows the top feeders and the server total
///
/// It also announces itself to the optional Wolfpark HUD mod through the Steam lobby data (keys starting with wp_),
/// which shows each player their coins and has buttons for !feed and !meteor. Players without the mod see nothing.
///
/// The wording, the cooldown and the size of the top list are set in feed.txt in the server's root directory
/// (next to server.cfg), which is created on first start and reloads about 10 seconds after it is saved.
/// </summary>
public class Feed : CovePlugin
{
    private const string CountsFileName = "feed-counts.json";
    private const string SettingsFileName = "feed.txt";
    private const string ShopFileName = "shop.txt";
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

        // coins
        public int CoinsPerFeed = 1;
        // read from shop.txt
        public List<ShopItem> Items = new()
        {
            new ShopItem { Id = "meteor", Label = "Meteor", Price = 10, Action = "meteor", CooldownSeconds = 30, Message = "%player% spent %cost% to call down a meteor!" },
            new ShopItem { Id = "raincloud", Label = "Rain Cloud", Price = 6, Action = "raincloud", CooldownSeconds = 30 },
            new ShopItem { Id = "fish", Label = "Fish Shadow", Price = 3, Action = "fish", CooldownSeconds = 10 },
        };
        public string BuyMessage = "%player% bought a %item% for %cost%!";
        public string BuyNotEnough = "A %item% costs %cost%, you have %coins%. Feed the wolves to earn more!";
        public string BuyWait = "A %item% was only just bought, please wait %seconds% more second(s).";
        public string BuyFailed = "The %item% could not be bought right now, you were not charged.";
        public bool GambleEnabled = true;
        public int GambleMin = 1;
        public int GambleMax = 100;
        public int GambleCooldownSeconds = 3;
        public double GambleAnnounceAt = 10;
        public List<Outcome> Outcomes = new()
        {
            new Outcome { Chance = 63, Multiplier = 0, Label = "The wolves ate your coins." },
            new Outcome { Chance = 27, Multiplier = 2, Label = "Doubled!" },
            new Outcome { Chance = 8.5, Multiplier = 3, Label = "Tripled!" },
            new Outcome { Chance = 1.5, Multiplier = 10, Label = "JACKPOT!" },
        };
        public string GambleResult = "%label% (bet %bet%, %change%, you have %coins%)";
        public string GambleAnnounce = "%player% hit the JACKPOT and won %won%!";
        public string GambleUsage = "Usage: !gamble <coins>, from %min% to %max%.";
        public string GambleNotEnough = "You only have %coins%.";
        public string GambleWait = "Slow down, the wolves are still counting.";
        public int MeteorCost = 10;
        public string HudTitle = "Ether's Wolf Park";
        public string HudTitleColor = "00FFFF";
        public int MeteorCooldownSeconds = 30;
        public string CoinMessage = "+%earned% (you have %coins%). A meteor costs %cost%, type !meteor when you have enough.";
        public string CoinsBalance = "You have %coins%. A meteor costs %cost%, feed the wolves with !feed to earn more.";
        public string MeteorMessage = "%player% spent %cost% to call down a meteor!";
        public string NotEnoughMessage = "A meteor costs %cost%, you have %coins%. Feed the wolves to earn more!";
        public string MeteorWaitMessage = "A meteor was only just called, please wait %seconds% more second(s).";
        public string MeteorFailedMessage = "The meteor could not be called right now, you were not charged.";
    }

    private sealed class ShopItem
    {
        public string Id = "";
        public string Label = "";
        public int Price;
        public string Action = "";
        public int CooldownSeconds;
        public string? Message; // null = the buyMessage of shop.txt, empty = say nothing
    }

    private sealed class Outcome
    {
        public double Chance;
        public double Multiplier;
        public string Label = "";
    }

    private sealed class Record
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public int Coins { get; set; }
    }

    private readonly object sync = new();
    private readonly Dictionary<ulong, Record> counts = new();
    private readonly Dictionary<ulong, long> lastFed = new();
    private readonly Dictionary<string, long> lastBought = new();
    private readonly Dictionary<ulong, long> lastGamble = new();
    private readonly Dictionary<ulong, (int Seq, double Mult, int Net)> gambleResults = new();
    private readonly Dictionary<ulong, long> hungryAt = new(); // when to tell a player the wolves are hungry again
    private string countsPath = "";
    private string settingsPath = "";
    private string shopPath = "";
    private DateTime loadedShopWriteTime = DateTime.MinValue;
    private Settings settings = new();
    private DateTime loadedWriteTime = DateTime.MinValue;
    private long nextReloadCheckTick;
    private bool running;

    // what the HUD mod is told through the lobby data
    private const int PublishEveryMs = 15_000;
    private bool publishDirty = true;
    private long nextPublishTick;
    private ulong publishedLobby;
    private string publishedTitle = "";
    private string publishedShop = "";
    private readonly Dictionary<ulong, string> published = new();

    public Feed(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        string root = AppDomain.CurrentDomain.BaseDirectory;
        countsPath = Path.Combine(root, CountsFileName);
        settingsPath = Path.Combine(root, SettingsFileName);
        shopPath = Path.Combine(root, ShopFileName);
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

        RegisterCommand(
            command: "coins",
            aliases: [],
            callback: (player, args) =>
            {
                try
                {
                    ShowCoins(player);
                }
                catch (Exception e)
                {
                    Log($"!coins failed: {e.Message}");
                }
            }
        );
        SetCommandDescription("coins", "Shows how many wolf coins you have");

        RegisterCommand(
            command: "meteor",
            aliases: [],
            callback: (player, args) =>
            {
                try
                {
                    BuyItem(player, "meteor");
                }
                catch (Exception e)
                {
                    Log($"!meteor failed: {e.Message}");
                }
            }
        );
        SetCommandDescription("meteor", "Spend wolf coins to call down a meteor");

        RegisterCommand(
            command: "shop",
            aliases: [],
            callback: (player, args) => Safely("shop", () => ShowShop(player))
        );
        SetCommandDescription("shop", "Lists what wolf coins can buy");

        RegisterCommand(
            command: "buy",
            aliases: [],
            callback: (player, args) => Safely("buy", () =>
            {
                if (args.Length == 0)
                    ShowShop(player);
                else
                    BuyItem(player, args[0]);
            })
        );
        SetCommandDescription("buy", "Buy something with wolf coins | Usage: !buy <name>");

        RegisterCommand(
            command: "gamble",
            aliases: ["bet"],
            callback: (player, args) => Safely("gamble", () => Gamble(player, args))
        );
        SetCommandDescription("gamble", "Bet wolf coins on a wolf roll | Usage: !gamble <coins>");
    }

    private void Safely(string command, Action run)
    {
        try
        {
            run();
        }
        catch (Exception e)
        {
            Log($"!{command} failed: {e.Message}");
        }
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
        ParentServer.UnregisterCommand("coins");
        ParentServer.UnregisterCommand("meteor");
        ParentServer.UnregisterCommand("shop");
        ParentServer.UnregisterCommand("buy");
        ParentServer.UnregisterCommand("gamble");
        ClearLobbyData();
    }

    public override void onPlayerJoin(WFPlayer player)
    {
        base.onPlayerJoin(player);
        publishDirty = true;
    }

    public override void onPlayerLeave(WFPlayer player)
    {
        base.onPlayerLeave(player);
        publishDirty = true;
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
                    if ((File.Exists(settingsPath) && File.GetLastWriteTimeUtc(settingsPath) != loadedWriteTime)
                        || (File.Exists(shopPath) && File.GetLastWriteTimeUtc(shopPath) != loadedShopWriteTime))
                    {
                        ReloadSettings();
                        s = settings;
                    }
                }
                catch (Exception e)
                {
                    loadedWriteTime = DateTime.MinValue; // probably mid-save, try again at the next check
                    loadedShopWriteTime = DateTime.MinValue;
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

        PublishLobbyData(s);

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

    // "1 coin", "10 coins"
    private static string Coins(int amount) => amount == 1 ? "1 coin" : $"{amount} coins";

    private static string FillCoins(string text, string player, int balance, Settings s, int earned = 0, int seconds = 0)
    {
        return text.Replace("%player%", player)
                   .Replace("%coins%", Coins(balance))
                   .Replace("%earned%", Coins(earned))
                   .Replace("%cost%", Coins((s.Items.FirstOrDefault(i => i.Action == "meteor") ?? s.Items.FirstOrDefault())?.Price ?? s.MeteorCost))
                   .Replace("%seconds%", seconds.ToString());
    }

    private void FeedWolves(WFPlayer player)
    {
        ulong id = player.SteamId.m_SteamID;
        int mine;
        int coins;
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
            record.Coins += s.CoinsPerFeed;
            mine = record.Count;
            coins = record.Coins;
            total = counts.Values.Sum(r => (long)r.Count);

            SaveCounts();
        }

        string times = mine == 1 ? "1 time" : $"{mine} times";
        if (s.Replies.Count > 0)
            SendGlobalChatMessage(Fill(s.Replies[Random.Shared.Next(s.Replies.Count)], player.Username, times, mine, total));
        if (s.Howl.Length > 0)
            SendGlobalChatMessage(Fill(s.Howl, player.Username, times, mine, total));

        if (s.CoinsPerFeed > 0 && s.CoinMessage.Length > 0)
            SendPlayerChatMessage(player, FillCoins(s.CoinMessage, player.Username, coins, s, s.CoinsPerFeed));
    }

    // ---- the HUD mod ----

    // The coins of every player are written into the Steam lobby data as  wp_p<SteamID> = "coins,feeds", next to
    // the marker  wp_hud = 1  that tells the mod this is a server it should show itself on.
    private void PublishLobbyData(Settings s)
    {
        long now = Environment.TickCount64;
        if (!publishDirty && now < nextPublishTick)
            return;
        publishDirty = false;
        nextPublishTick = now + PublishEveryMs;

        try
        {
            CSteamID lobby = ParentServer.SteamLobby;
            if (!lobby.IsValid())
                return;

            if (publishedLobby != lobby.m_SteamID)
            {
                // a new lobby, so nothing has been written to it yet
                published.Clear();
                publishedShop = "";
                publishedTitle = "";
                SteamMatchmaking.SetLobbyData(lobby, "wp_hud", "1");
                publishedLobby = lobby.m_SteamID;
            }

            // what the Buy button lists:  id~Label~price;id~Label~price   and the bet limits of the gamble box
            string shop = string.Join(";", ShopList(s).Select(i => $"{i.Id}~{Clean(i.Label)}~{i.Price}"));
            string gamble = s.GambleEnabled ? $"{s.GambleMin},{s.GambleMax}" : "";
            string feedCooldown = s.CooldownSeconds.ToString();
            if (shop + "|" + gamble + "|" + feedCooldown != publishedShop)
            {
                SteamMatchmaking.SetLobbyData(lobby, "wp_shop", shop);
                SteamMatchmaking.SetLobbyData(lobby, "wp_gamble", gamble);
                SteamMatchmaking.SetLobbyData(lobby, "wp_feed_cd", feedCooldown); // for the cooldown bar of the Feed Wolves button
                publishedShop = shop + "|" + gamble + "|" + feedCooldown;
            }

            string title = s.HudTitle + "|" + s.HudTitleColor;
            if (title != publishedTitle)
            {
                SteamMatchmaking.SetLobbyData(lobby, "wp_title", s.HudTitle);
                SteamMatchmaking.SetLobbyData(lobby, "wp_title_color", s.HudTitleColor);
                publishedTitle = title;
            }

            var wanted = new Dictionary<ulong, string>();
            lock (sync)
            {
                foreach (WFPlayer player in GetAllPlayers())
                {
                    ulong id = player.SteamId.m_SteamID;
                    string basic = counts.TryGetValue(id, out Record? record) ? $"{record.Coins},{record.Count}" : "0,0";
                    // the last gamble, so the mod can show what happened: number of the roll, multiplier, coins won or lost
                    if (gambleResults.TryGetValue(id, out var roll))
                        basic += $",{roll.Seq},{roll.Mult.ToString(CultureInfo.InvariantCulture)},{roll.Net}";
                    wanted[id] = basic;
                }
            }

            foreach (var pair in wanted)
            {
                if (published.TryGetValue(pair.Key, out string? old) && old == pair.Value)
                    continue;
                SteamMatchmaking.SetLobbyData(lobby, "wp_p" + pair.Key, pair.Value);
                published[pair.Key] = pair.Value;
            }

            // players who left, so the lobby data does not grow forever
            foreach (ulong gone in published.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            {
                SteamMatchmaking.SetLobbyData(lobby, "wp_p" + gone, "");
                published.Remove(gone);
            }
        }
        catch (Exception e)
        {
            Log($"Could not update the HUD data: {e.Message}");
        }
    }

    private void ClearLobbyData()
    {
        try
        {
            CSteamID lobby = ParentServer.SteamLobby;
            if (lobby.IsValid() && publishedLobby == lobby.m_SteamID)
            {
                SteamMatchmaking.SetLobbyData(lobby, "wp_hud", "");
                SteamMatchmaking.SetLobbyData(lobby, "wp_shop", "");
                SteamMatchmaking.SetLobbyData(lobby, "wp_gamble", "");
                SteamMatchmaking.SetLobbyData(lobby, "wp_feed_cd", "");
                SteamMatchmaking.SetLobbyData(lobby, "wp_title", "");
                SteamMatchmaking.SetLobbyData(lobby, "wp_title_color", "");
                foreach (ulong id in published.Keys)
                    SteamMatchmaking.SetLobbyData(lobby, "wp_p" + id, "");
            }
        }
        catch (Exception e)
        {
            Log($"Could not clear the HUD data: {e.Message}");
        }
        published.Clear();
        publishedLobby = 0;
        publishedShop = "";
        publishedTitle = "";
    }

    private static string Clean(string text) => text.Replace('~', ' ').Replace(';', ' ').Replace(',', ' ').Replace('|', ' ');

    private static List<ShopItem> ShopList(Settings s) => s.Items;

    private void ShowShop(WFPlayer player)
    {
        Settings s = GetSettings();
        string items = string.Join(", ", ShopList(s).Select(i => $"{i.Id} {i.Price}"));
        SendPlayerChatMessage(player, $"Shop: {items}. Type !buy <name>.");
    }

    private static string FillItem(string text, string player, ShopItem item, int balance, int seconds = 0)
    {
        return text.Replace("%player%", player)
                   .Replace("%item%", item.Label.ToLowerInvariant())
                   .Replace("%cost%", Coins(item.Price))
                   .Replace("%coins%", Coins(balance))
                   .Replace("%seconds%", seconds.ToString());
    }

    private void RunShopAction(string action)
    {
        switch (action)
        {
            case "meteor": ParentServer.spawnFish("fish_spawn_alien"); break;
            case "fish": ParentServer.spawnFish(); break;
            case "raincloud": ParentServer.spawnRainCloud(); break;
            case "voidportal": ParentServer.spawnVoidPortal(); break;
            default: throw new InvalidOperationException($"unknown shop action \"{action}\"");
        }
    }

    private void BuyItem(WFPlayer player, string id)
    {
        id = id.Trim().ToLowerInvariant();

        Settings s = GetSettings();
        ShopItem? item = s.Items.FirstOrDefault(i => i.Id == id);
        if (item == null)
        {
            SendPlayerChatMessage(player, $"There is no \"{id}\" in the shop. Type !shop to see what you can buy.");
            return;
        }

        ulong pid = player.SteamId.m_SteamID;
        int coins;

        lock (sync)
        {
            long now = Environment.TickCount64;
            bool admin = IsPlayerAdmin(player);

            if (!admin && item.CooldownSeconds > 0 && lastBought.TryGetValue(item.Id, out long last) && now - last < item.CooldownSeconds * 1000L)
            {
                int wait = (int)Math.Ceiling((item.CooldownSeconds * 1000L - (now - last)) / 1000.0);
                SendPlayerChatMessage(player, FillItem(s.BuyWait, player.Username, item, 0, wait));
                return;
            }

            counts.TryGetValue(pid, out Record? record);
            coins = record?.Coins ?? 0;
            if (record == null || record.Coins < item.Price)
            {
                SendPlayerChatMessage(player, FillItem(s.BuyNotEnough, player.Username, item, coins));
                return;
            }

            record.Coins -= item.Price;
            coins = record.Coins;
            lastBought[item.Id] = now;
            SaveCounts();
        }

        try
        {
            RunShopAction(item.Action);
        }
        catch (Exception e)
        {
            lock (sync)
            {
                if (counts.TryGetValue(pid, out Record? record))
                {
                    record.Coins += item.Price;
                    coins = record.Coins;
                }
                lastBought.Remove(item.Id);
                SaveCounts();
            }
            Log($"Could not give {player.Username} a {item.Id}: {e.Message}");
            SendPlayerChatMessage(player, FillItem(s.BuyFailed, player.Username, item, coins));
            return;
        }

        Log($"{player.Username} spent {item.Price} coins on {item.Id}.");
        string announce = item.Message ?? s.BuyMessage;
        if (announce.Length > 0)
            SendGlobalChatMessage(FillItem(announce, player.Username, item, coins));
    }

    private string FillGamble(string text, string player, int bet, int won, int net, int balance, string label, Settings s)
    {
        return text.Replace("%player%", player)
                   .Replace("%label%", label)
                   .Replace("%bet%", Coins(bet))
                   .Replace("%won%", Coins(won))
                   .Replace("%change%", net >= 0 ? $"+{net}" : $"-{-net}")
                   .Replace("%coins%", Coins(balance))
                   .Replace("%min%", s.GambleMin.ToString())
                   .Replace("%max%", s.GambleMax.ToString());
    }

    // A wolf roll: the bet is taken, one of the outcomes of feed.txt is picked by its chance, and the bet times
    // that outcome's multiplier is paid back (0 loses everything).
    private void Gamble(WFPlayer player, string[] args)
    {
        Settings s = GetSettings();
        if (!s.GambleEnabled || s.Outcomes.Count == 0)
        {
            SendPlayerChatMessage(player, "The wolves are not taking bets right now.");
            return;
        }

        ulong id = player.SteamId.m_SteamID;
        int bet;
        Outcome outcome;
        int won;
        int coins;

        lock (sync)
        {
            counts.TryGetValue(id, out Record? record);
            int balance = record?.Coins ?? 0;

            string arg = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";
            if (arg == "all")
                bet = Math.Min(balance, s.GambleMax);
            else if (!int.TryParse(arg, out bet))
            {
                SendPlayerChatMessage(player, FillGamble(s.GambleUsage, player.Username, 0, 0, 0, balance, "", s));
                return;
            }

            if (bet < s.GambleMin || bet > s.GambleMax)
            {
                SendPlayerChatMessage(player, FillGamble(s.GambleUsage, player.Username, bet, 0, 0, balance, "", s));
                return;
            }
            if (record == null || record.Coins < bet)
            {
                SendPlayerChatMessage(player, FillGamble(s.GambleNotEnough, player.Username, bet, 0, 0, balance, "", s));
                return;
            }

            long now = Environment.TickCount64;
            if (s.GambleCooldownSeconds > 0 && lastGamble.TryGetValue(id, out long last) && now - last < s.GambleCooldownSeconds * 1000L)
            {
                SendPlayerChatMessage(player, FillGamble(s.GambleWait, player.Username, bet, 0, 0, balance, "", s));
                return;
            }
            lastGamble[id] = now;

            // pick an outcome by its chance, the chances do not have to add up to 100
            double total = s.Outcomes.Sum(o => o.Chance);
            double roll = Random.Shared.NextDouble() * total;
            outcome = s.Outcomes[^1];
            foreach (Outcome candidate in s.Outcomes)
            {
                if (roll < candidate.Chance)
                {
                    outcome = candidate;
                    break;
                }
                roll -= candidate.Chance;
            }

            won = (int)Math.Floor(bet * outcome.Multiplier);
            record.Coins += won - bet;
            coins = record.Coins;

            int seq = gambleResults.TryGetValue(id, out var previous) ? previous.Seq + 1 : 1;
            gambleResults[id] = (seq, outcome.Multiplier, won - bet);
            SaveCounts();
        }

        int net = won - bet;
        Log($"{player.Username} bet {bet} and got x{outcome.Multiplier} ({(net >= 0 ? "+" : "")}{net}), now has {coins}.");
        SendPlayerChatMessage(player, FillGamble(s.GambleResult, player.Username, bet, won, net, coins, FillGamble(outcome.Label, player.Username, bet, won, net, coins, "", s), s));

        if (s.GambleAnnounce.Length > 0 && s.GambleAnnounceAt > 0 && outcome.Multiplier >= s.GambleAnnounceAt)
            SendGlobalChatMessage(FillGamble(s.GambleAnnounce, player.Username, bet, won, net, coins, outcome.Label, s));
    }

    private void ShowCoins(WFPlayer player)
    {
        Settings s = GetSettings();
        int coins;
        lock (sync)
            coins = counts.TryGetValue(player.SteamId.m_SteamID, out Record? record) ? record.Coins : 0;

        SendPlayerChatMessage(player, FillCoins(s.CoinsBalance, player.Username, coins, s));
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
        var outcomes = new List<Outcome>();

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
                case "coinsperfeed":
                    if (int.TryParse(value, out int perFeed) && perFeed >= 0)
                        next.CoinsPerFeed = Math.Min(perFeed, 1000);
                    else
                        Log("coinsPerFeed must be a number, 0 or more, using 1.");
                    break;
                case "gambleoutcome":
                    if (outcomes.Count < 12 && TryParseOutcome(value, out Outcome? parsedOutcome) && parsedOutcome != null)
                        outcomes.Add(parsedOutcome);
                    break;
                case "gamble":
                    next.GambleEnabled = !value.Equals("off", StringComparison.OrdinalIgnoreCase) && !value.Equals("no", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                    break;
                case "gamblemin":
                    if (int.TryParse(value, out int gMin) && gMin >= 1)
                        next.GambleMin = Math.Min(gMin, 1_000_000);
                    else
                        Log("gambleMin must be a number, 1 or more, using 1.");
                    break;
                case "gamblemax":
                    if (int.TryParse(value, out int gMax) && gMax >= 1)
                        next.GambleMax = Math.Min(gMax, 1_000_000);
                    else
                        Log("gambleMax must be a number, 1 or more, using 100.");
                    break;
                case "gamblecooldown":
                    if (int.TryParse(value, out int gWait) && gWait >= 0)
                        next.GambleCooldownSeconds = Math.Min(gWait, 3600);
                    else
                        Log("gambleCooldown must be a number of seconds, using 3.");
                    break;
                case "gambleannounceat":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gAt) && gAt >= 0)
                        next.GambleAnnounceAt = gAt;
                    else
                        Log("gambleAnnounceAt must be a multiplier, 0 or more, using 10.");
                    break;
                case "gambleresult":
                    next.GambleResult = value;
                    break;
                case "gambleannounce":
                    next.GambleAnnounce = value;
                    break;
                case "gambleusage":
                    next.GambleUsage = value;
                    break;
                case "gamblenotenough":
                    next.GambleNotEnough = value;
                    break;
                case "gamblewait":
                    next.GambleWait = value;
                    break;
                case "hudtitle":
                    next.HudTitle = value.Length > 40 ? value.Substring(0, 40) : value;
                    break;
                case "hudtitlecolor":
                    string hex = value.TrimStart('#');
                    if (hex.Length == 6 && hex.All(Uri.IsHexDigit))
                        next.HudTitleColor = hex.ToUpperInvariant();
                    else
                        Log("hudTitleColor must be six hex digits like 00FFFF, using 00FFFF.");
                    break;
                case "meteorcost":
                    if (int.TryParse(value, out int cost) && cost >= 1)
                        next.MeteorCost = Math.Min(cost, 100_000);
                    else
                        Log("meteorCost must be a number, 1 or more, using 10.");
                    break;
                case "meteorcooldown":
                    if (int.TryParse(value, out int meteorWait) && meteorWait >= 0)
                        next.MeteorCooldownSeconds = Math.Min(meteorWait, 86_400);
                    else
                        Log("meteorCooldown must be a number of seconds, using 30.");
                    break;
                case "coinmessage":
                    next.CoinMessage = value;
                    break;
                case "coinsbalance":
                    next.CoinsBalance = value;
                    break;
                case "meteormessage":
                    next.MeteorMessage = value;
                    break;
                case "notenoughmessage":
                    next.NotEnoughMessage = value;
                    break;
                case "meteorwaitmessage":
                    next.MeteorWaitMessage = value;
                    break;
                case "meteorfailedmessage":
                    next.MeteorFailedMessage = value;
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

        if (outcomes.Count > 0 && outcomes.Sum(o => o.Chance) > 0)
        {
            next.Outcomes = outcomes;
            double total = outcomes.Sum(o => o.Chance);
            double expected = outcomes.Sum(o => o.Chance / total * o.Multiplier);
            Log($"Gamble: on average {expected * 100:0.#}% of every bet comes back.");
        }
        else if (outcomes.Count > 0)
            Log("The gambleOutcome lines have no chance above 0, using the default outcomes.");

        ReloadShop(next);

        settings = next;
        Log($"Loaded {SettingsFileName}.");
    }

    // item = id | Label | price | action | cooldown seconds | message
    private bool TryParseShopItem(string value, out ShopItem? item)
    {
        item = null;
        string[] parts = value.Split('|', 6).Select(p => p.Trim()).ToArray();
        if (parts.Length < 4)
        {
            Log($"shop item \"{value}\" needs: id | Label | price | action | cooldown | message");
            return false;
        }

        string id = parts[0].ToLowerInvariant();
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            Log($"shop item \"{value}\": the id must be letters or numbers.");
            return false;
        }
        if (!int.TryParse(parts[2], out int price) || price < 1)
        {
            Log($"shop item \"{value}\": the price must be a number, 1 or more.");
            return false;
        }
        string action = parts[3].ToLowerInvariant();
        if (action != "meteor" && action != "fish" && action != "raincloud" && action != "voidportal")
        {
            Log($"shop item \"{value}\": the action must be meteor, fish, raincloud or voidportal.");
            return false;
        }
        int cooldown = 0;
        if (parts.Length > 4 && parts[4].Length > 0 && (!int.TryParse(parts[4], out cooldown) || cooldown < 0))
        {
            Log($"shop item \"{value}\": the cooldown must be a number of seconds.");
            return false;
        }

        item = new ShopItem
        {
            Id = id,
            Label = parts[1].Length > 0 ? parts[1] : id,
            Price = Math.Min(price, 100_000),
            Action = action,
            CooldownSeconds = Math.Min(cooldown, 86_400),
            Message = parts.Length > 5 ? parts[5] : null,
        };
        return true;
    }

    // ---- shop.txt ----

    private void ReloadShop(Settings next)
    {
        try
        {
            if (!File.Exists(shopPath))
                EnsureShopFileExists(next);

            loadedShopWriteTime = File.GetLastWriteTimeUtc(shopPath);

            var items = new List<ShopItem>();
            foreach (string raw in File.ReadAllLines(shopPath))
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
                    case "item":
                        if (items.Count >= 20)
                            Log("shop.txt: only the first 20 items are used.");
                        else if (TryParseShopItem(value, out ShopItem? parsed) && parsed != null)
                        {
                            if (items.Any(i => i.Id == parsed.Id))
                                Log($"shop.txt: \"{parsed.Id}\" is listed twice, the second one is ignored.");
                            else
                                items.Add(parsed);
                        }
                        break;
                    case "buymessage":
                        next.BuyMessage = value;
                        break;
                    case "buynotenough":
                        next.BuyNotEnough = value;
                        break;
                    case "buywait":
                        next.BuyWait = value;
                        break;
                    case "buyfailed":
                        next.BuyFailed = value;
                        break;
                }
            }

            // a file without items means nothing is for sale
            next.Items = items;
            Log($"Loaded {ShopFileName} ({items.Count} item(s)).");
        }
        catch (Exception e)
        {
            loadedShopWriteTime = DateTime.MinValue; // try again at the next check
            Log($"Could not load {ShopFileName}, keeping the previous items: {e.Message}");
        }
    }

    // The first time, the meteor is carried over from the old meteor settings of feed.txt
    private void EnsureShopFileExists(Settings legacy)
    {
        string meteorMessage = legacy.MeteorMessage;
        string[] lines =
        {
            "# ======================================================================",
            "#  Shop",
            "# ======================================================================",
            "#",
            "#  Everything wolf coins can buy. Players see this list in the HUD mod's Buy menu",
            "#  and with !shop, and buy with !buy <id>. This file reloads about 10 seconds after",
            "#  it is saved. Lines starting with # are ignored.",
            "#",
            "#    item = id | Label | price | action | cooldown | message",
            "#",
            "#    id        what players type after !buy, letters and numbers only.",
            "#    Label     the name shown in the Buy menu.",
            "#    price     coins it costs.",
            "#    action    meteor (an alien fish falls from the sky), fish (a fish shadow),",
            "#              raincloud or voidportal.",
            "#    cooldown  seconds before ANYBODY can buy this again, 0 for none. Admins are not limited.",
            "#    message   optional, said to everyone when it is bought. Leave it out to use buyMessage,",
            "#              or write it empty (a | at the end) to say nothing.",
            "#",
            "#  In messages: %player% (the buyer), %item% (the label), %cost% and %coins% (what they have left).",
            "# ======================================================================",
            "",
            $"item = meteor | Meteor | {legacy.MeteorCost} | meteor | {legacy.MeteorCooldownSeconds} | {meteorMessage}",
            "item = raincloud | Rain Cloud | 6 | raincloud | 30",
            "item = fish | Fish Shadow | 3 | fish | 10",
            "# item = voidportal | Void Portal | 25 | voidportal | 120",
            "",
            "buyMessage = %player% bought a %item% for %cost%!",
            "buyNotEnough = A %item% costs %cost%, you have %coins%. Feed the wolves to earn more!",
            "buyWait = A %item% was only just bought, please wait %seconds% more second(s).",
            "buyFailed = The %item% could not be bought right now, you were not charged.",
            "",
        };

        File.WriteAllText(shopPath, string.Join(Environment.NewLine, lines));
        Log($"Created {ShopFileName}, edit it to change what can be bought.");
    }

    // gambleOutcome = chance | multiplier | message
    private bool TryParseOutcome(string value, out Outcome? outcome)
    {
        outcome = null;
        string[] parts = value.Split('|', 3).Select(p => p.Trim()).ToArray();
        if (parts.Length < 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double chance) || chance < 0
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double multiplier) || multiplier < 0)
        {
            Log($"gambleOutcome line \"{value}\" needs: chance | multiplier | message");
            return false;
        }

        outcome = new Outcome { Chance = chance, Multiplier = Math.Min(multiplier, 1000), Label = parts.Length > 2 ? parts[2] : "" };
        return true;
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
            "#",
            "#  COINS",
            "#  Every feed earns coins. !coins shows a player's balance. The coins are kept by",
            "#  Steam ID in feed-counts.json, next to the feed counts, and spending them does",
            "#  not lower the feed count.",
            "#  coinsPerFeed         = coins earned for each feed, 0 turns coins off.",
            "#  What coins can buy (the meteor and the rest) is in shop.txt.",
            "#",
            "#  GAMBLING: !gamble <coins> (or !gamble all) rolls once. Each gambleOutcome line is",
            "#  chance | multiplier | message. The bet is paid back times the multiplier, 0 loses it all.",
            "#  The chances can be any numbers, they are compared with each other. Put gamble = off to",
            "#  switch it off. In messages: %player% %bet% %won% %change% %coins% %label% %min% %max%.",
            "#  gambleMin / gambleMax = smallest and biggest bet.  gambleCooldown = seconds between bets.",
            "#  gambleAnnounceAt      = multiplier from which everybody is told (0 = never).",
            "#  gambleResult / gambleAnnounce / gambleUsage / gambleNotEnough / gambleWait = wording.",
            "#  hudTitle             = the heading on the Wolfpark HUD mod's panel (up to 40 letters).",
            "#  hudTitleColor        = its colour as six hex digits, like 00FFFF.",
            "#  In the coin messages you can also use  %coins% (balance, like \"3 coins\"),",
            "#  %earned% (coins from this feed) and %cost% (the meteor price). Leave a message",
            "#  empty to send nothing.",
            "#  coinMessage          = told only to the player after each feed.",
            "#  coinsBalance         = the answer to !coins.",
            "# ======================================================================",
            "",
            "reply = %player% fed the wolves. That's %times% so far!",
            "howl = Awooooooooo!",
            "cooldown = 3",
            "cooldownMessage = The wolves are still eating, try again in a moment.",
            "hungryMessage = The wolves are getting hungry again...",
            "hungryTo = player",
            "",
            "coinsPerFeed = 1",
            "gamble = on",
            "gambleMin = 1",
            "gambleMax = 100",
            "gambleCooldown = 3",
            "gambleAnnounceAt = 10",
            "gambleOutcome = 63 | 0 | The wolves ate your coins.",
            "gambleOutcome = 27 | 2 | Doubled!",
            "gambleOutcome = 8.5 | 3 | Tripled!",
            "gambleOutcome = 1.5 | 10 | JACKPOT!",
            "gambleResult = %label% (bet %bet%, %change%, you have %coins%)",
            "gambleAnnounce = %player% hit the JACKPOT and won %won%!",
            "hudTitle = Ether's Wolf Park",
            "hudTitleColor = 00FFFF",
            "coinMessage = +%earned% (you have %coins%). A meteor costs %cost%, type !meteor when you have enough.",
            "coinsBalance = You have %coins%. A meteor costs %cost%, feed the wolves with !feed to earn more.",
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
            publishDirty = true; // the HUD mod shows these numbers
        }
        catch (Exception e)
        {
            Log($"Could not save {CountsFileName}: {e.Message}");
        }
    }
}
