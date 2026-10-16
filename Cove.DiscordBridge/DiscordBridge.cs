using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Two way chat bridge between the game and one Discord channel.
///
/// Game to Discord: chat messages (and optionally joins, leaves and server start/stop) are posted to the channel.
/// Discord to game: messages typed in the channel show up in the game chat.
///
/// Discord only lets bots read a channel, webhooks can only send, so this needs a bot token.
/// Configured through discordbridge.txt in the server's root directory.
/// </summary>
public class DiscordBridge : CovePlugin
{
    private const string FileName = "discordbridge.txt";
    private const string ApiBase = "https://discord.com/api/v10";
    private const string UserAgent = "DiscordBot (https://github.com/faytleingod499/WebFishingCove, 1.0)";

    // How often the config file is checked for edits. Saving the file while the server runs reloads it.
    private const long ConfigCheckMs = 10_000;

    // Chat lines waiting to go to Discord are sent together at most this often
    private const long FlushIntervalMs = 1_000;

    private const int DefaultPollSeconds = 3;
    private const int MinPollSeconds = 2;

    private const int MaxQueuedLines = 500;
    private const int MaxLineLength = 500;               // longest chat line sent to Discord
    private const int MaxDiscordMessageLength = 1900;    // Discord's limit is 2000
    private const int MaxGameTextLength = 300;           // longest Discord message shown in game
    private const int MaxFromDiscordPerPoll = 10;       // protects the game chat from a flood

    // The same problem is only written to the log this often
    private const long LogThrottleMs = 5 * 60_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly Regex CustomEmoji = new(@"<a?:(\w+):\d+>", RegexOptions.Compiled);
    private static readonly Regex RoleMention = new(@"<@&\d+>", RegexOptions.Compiled);
    private static readonly Regex ChannelMention = new(@"<#\d+>", RegexOptions.Compiled);
    private static readonly Regex UserMention = new(@"<@!?(\d+)>", RegexOptions.Compiled);

    private sealed record Incoming(ulong Id, string Author, string Text);

    private sealed class Settings
    {
        public string Token = "";
        public ulong ChannelId;
        public int PollSeconds = DefaultPollSeconds;
        public bool DiscordToGame = true;
        public bool RelayJoinLeave = true;
        public bool RelayServerEvents = true;
        public bool RelayCommands;
        public bool RelayLocalChat = true;
        public string WebhookUrl = "";
        public bool Debug;
        public string GamePrefix = "";
        public string GameColor = "ffffff";

        public bool Ready => Token.Length > 0 && ChannelId != 0;

        // changes whenever the bot or channel is switched, so the bridge knows to start over
        public string Credentials => Token + ":" + ChannelId;
    }

    // settings are replaced as a whole on reload, so the lock only protects the reference
    private readonly object sync = new();
    private Settings settings = new();
    private string filePath = "";
    private DateTime loadedWriteTime = DateTime.MinValue;

    // chat lines from the game, waiting for the worker to send them
    private readonly ConcurrentQueue<(string? Name, string Text)> outgoing = new();
    private int queued;

    private readonly ConcurrentDictionary<string, long> lastLogged = new();

    private CancellationTokenSource? cancel;
    private Task? worker;

    // Only the worker touches these (onEnd uses rateLimitedUntil after the worker has stopped).
    private string activeCredentials = "";
    private bool baselineDone;
    private bool authFailed;
    private ulong lastMessageId;
    private long rateLimitedUntil;

    public DiscordBridge(CoveServer server) : base(server) { }

    // ---- plugin events ----

    public override void onInit()
    {
        base.onInit();

        filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName);

        try
        {
            EnsureFileExists();
            Reload();
        }
        catch (Exception e)
        {
            loadedWriteTime = DateTime.MinValue; // try again on the next config check
            Log($"Could not load {FileName}: {e.Message}");
        }

        Settings s = GetSettings();
        if (!s.Ready)
            Log($"The bridge is off, put a botToken and channelId in {FileName}.");
        else if (s.RelayServerEvents)
            Enqueue("**Server started.**");

        cancel = new CancellationTokenSource();
        CancellationToken token = cancel.Token;
        worker = Task.Run(() => RunAsync(token));
    }

    public override void onEnd()
    {
        base.onEnd();

        Settings s = GetSettings();
        if (s.Ready && s.RelayServerEvents)
            Enqueue("**Server stopped.**");

        // stop the worker first, then send what is left ourselves so the stop message actually goes out
        cancel?.Cancel();
        try
        {
            worker?.Wait(2000);
        }
        catch (Exception)
        {
            // the worker handles its own errors
        }

        if (s.Ready && !authFailed)
        {
            try
            {
                using var finalSend = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                for (int i = 0; i < 3 && !outgoing.IsEmpty; i++)
                    FlushOutgoingAsync(s, finalSend.Token).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // the server is closing anyway
            }
        }
    }

    public override void onChatMessage(WFPlayer sender, string message, bool local, Vector3? position, string zone)
    {
        base.onChatMessage(sender, message, local, position, zone);

        Settings s = GetSettings();
        if (!s.Ready)
            return;
        if (local && !s.RelayLocalChat)
            return;
        if (!s.RelayCommands && message.TrimStart().StartsWith('!'))
            return;

        string text = CleanText(message, MaxLineLength, forGame: false);
        if (text.Length == 0)
            return;

        Enqueue(Escape(text), NameOf(sender));
    }

    public override void onPlayerJoin(WFPlayer player)
    {
        base.onPlayerJoin(player);

        Settings s = GetSettings();
        if (!s.Ready || !s.RelayJoinLeave)
            return;

        Enqueue($"*{Escape(NameOf(player))} joined the server* ({ParentServer.AllPlayers.Count}/{ParentServer.MaxPlayers})");
    }

    public override void onPlayerLeave(WFPlayer player)
    {
        base.onPlayerLeave(player);

        Settings s = GetSettings();
        if (!s.Ready || !s.RelayJoinLeave)
            return;

        Enqueue($"*{Escape(NameOf(player))} left the server* ({ParentServer.AllPlayers.Count}/{ParentServer.MaxPlayers})");
    }

    // ---- background worker ----

    private async Task RunAsync(CancellationToken token)
    {
        long now = Environment.TickCount64;
        long nextConfigCheck = now + ConfigCheckMs;
        long nextFlush = now + FlushIntervalMs;
        long nextPoll = now;

        while (!token.IsCancellationRequested)
        {
            try
            {
                now = Environment.TickCount64;

                if (now >= nextConfigCheck)
                {
                    nextConfigCheck = now + ConfigCheckMs;
                    ReloadIfChanged();
                }

                Settings s = GetSettings();

                // a different bot or channel means starting over from the newest message
                if (s.Credentials != activeCredentials)
                {
                    activeCredentials = s.Credentials;
                    baselineDone = false;
                    authFailed = false;
                    nextPoll = now;
                }

                if (s.Ready && !authFailed)
                {
                    if (now >= nextFlush)
                    {
                        nextFlush = now + FlushIntervalMs;
                        await FlushOutgoingAsync(s, token);
                    }

                    if (s.DiscordToGame && now >= nextPoll)
                    {
                        nextPoll = now + s.PollSeconds * 1000L;
                        await PollAsync(s, token);
                    }
                }

                await Task.Delay(250, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // includes timeouts and lost connections, the bridge simply tries again
                LogThrottled("loop", $"The Discord bridge hit a problem: {Scrub(e.Message)}");

                try
                {
                    await Task.Delay(2000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    // ---- game to Discord ----

    // name is the player who chatted, null for server events
    private void Enqueue(string text, string? name = null)
    {
        if (!GetSettings().Ready)
            return;

        // never let a stuck connection grow the queue forever, the oldest lines are dropped first
        while (Volatile.Read(ref queued) >= MaxQueuedLines && outgoing.TryDequeue(out _))
            Interlocked.Decrement(ref queued);

        outgoing.Enqueue((name, text));
        if (GetSettings().Debug)
            Log($"[bridge debug] queued a line for Discord ({Volatile.Read(ref queued) + 1} waiting).");
        Interlocked.Increment(ref queued);
    }

    private async Task FlushOutgoingAsync(Settings s, CancellationToken token)
    {
        if (outgoing.IsEmpty || Environment.TickCount64 < rateLimitedUntil)
            return;

        // With a webhook every player's chat appears under their own name. Without one the bot posts
        // everything, with the name written in bold in front of each line.
        bool useWebhook = s.WebhookUrl.Length > 0;

        // several chat lines go out in one Discord message, that keeps the bridge far below Discord's rate limit
        var batch = new StringBuilder();
        string? batchName = null;
        bool started = false;

        while (outgoing.TryPeek(out var next))
        {
            string line = useWebhook || next.Name == null ? next.Text : $"**{Escape(next.Name)}**: {next.Text}";

            if (started)
            {
                if (useWebhook && next.Name != batchName)
                    break; // a different sender needs its own message
                if (batch.Length + 1 + line.Length > MaxDiscordMessageLength)
                    break;
            }

            if (!outgoing.TryDequeue(out _))
                break;
            Interlocked.Decrement(ref queued);

            if (!started)
            {
                started = true;
                batchName = next.Name;
            }
            else
            {
                batch.Append('\n');
            }
            batch.Append(line);
        }

        if (!started)
            return;

        // allowed_mentions with nothing in it means a player typing @everyone can never ping anyone
        // flags 4 = don't create link previews for links players post
        if (useWebhook)
        {
            string json = JsonSerializer.Serialize(new
            {
                content = batch.ToString(),
                username = WebhookName(batchName),
                allowed_mentions = new { parse = Array.Empty<string>() },
                flags = 4,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, s.WebhookUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

            using HttpResponseMessage hookResponse = await Http.SendAsync(request, token);
            if (!hookResponse.IsSuccessStatusCode)
            {
                string detail = await ReadBodyAsync(hookResponse, token);
                Log($"[bridge] The webhook send failed: HTTP {(int)hookResponse.StatusCode} {Scrub(detail)}");
                HandleWebhookFailure(hookResponse);
            }
            else if (s.Debug)
            {
                Log($"[bridge debug] sent {batch.Length} characters through the webhook (HTTP {(int)hookResponse.StatusCode}).");
            }
            return;
        }

        string botJson = JsonSerializer.Serialize(new
        {
            content = batch.ToString(),
            allowed_mentions = new { parse = Array.Empty<string>() },
            flags = 4,
        });

        using HttpResponseMessage response = await SendAsync(s, HttpMethod.Post, $"/channels/{s.ChannelId}/messages", botJson, token);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await ReadBodyAsync(response, token);
            Log($"[bridge] The bot send failed: HTTP {(int)response.StatusCode} {Scrub(detail)}");
            HandleFailure(response, "sending chat");
        }
        else if (s.Debug)
        {
            Log($"[bridge debug] sent {batch.Length} characters through the bot (HTTP {(int)response.StatusCode}).");
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(token);
            return body.Length > 300 ? body.Substring(0, 300) : body;
        }
        catch (Exception)
        {
            return "";
        }
    }

    // Discord refuses webhook names that contain "discord" or "clyde" and wants 2 to 80 characters
    private static string WebhookName(string? name)
    {
        string result = string.IsNullOrWhiteSpace(name) ? "Server" : name;
        result = Regex.Replace(result, "discord", "disc0rd", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, "clyde", "cl7de", RegexOptions.IgnoreCase);
        if (result.Length > 80)
            result = result.Substring(0, 80);
        if (result.Length < 2)
            result += ".";
        return result;
    }

    private void HandleWebhookFailure(HttpResponseMessage response)
    {
        int code = (int)response.StatusCode;

        if (code == 429)
        {
            double wait = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5;
            rateLimitedUntil = Environment.TickCount64 + (long)((Math.Min(wait, 60) + 1) * 1000);
            LogThrottled("hook429", "Discord asked the bridge to slow down, waiting a moment.");
        }
        else if (code == 401 || code == 404)
        {
            LogThrottled("hook404", $"Discord does not accept the webhookUrl (it may have been deleted). Check webhookUrl in {FileName}.");
        }
        else
        {
            LogThrottled("hook" + code, $"Discord answered HTTP {code} while sending through the webhook.");
        }
    }

    // ---- Discord to game ----

    private async Task PollAsync(Settings s, CancellationToken token)
    {
        if (Environment.TickCount64 < rateLimitedUntil)
            return;

        if (!baselineDone)
        {
            // Remember where the channel is now, so the old history isn't dumped into the game chat.
            using HttpResponseMessage first = await SendAsync(s, HttpMethod.Get, $"/channels/{s.ChannelId}/messages?limit=1", null, token);
            if (!first.IsSuccessStatusCode)
            {
                HandleFailure(first, "reading the channel");
                return;
            }

            ParseMessages(await first.Content.ReadAsStringAsync(token), out ulong newest);
            lastMessageId = newest;
            baselineDone = true;
            Log("Connected to the Discord channel, new messages there will show up in game.");
            return;
        }

        using HttpResponseMessage response = await SendAsync(s, HttpMethod.Get, $"/channels/{s.ChannelId}/messages?limit=50&after={lastMessageId}", null, token);
        if (!response.IsSuccessStatusCode)
        {
            HandleFailure(response, "reading the channel");
            return;
        }

        List<Incoming> incoming = ParseMessages(await response.Content.ReadAsStringAsync(token), out ulong newestId);
        if (newestId > lastMessageId)
            lastMessageId = newestId;

        // Discord doesn't promise an order, the message ids go up with time so sorting by them puts the chat in order
        incoming.Sort((a, b) => a.Id.CompareTo(b.Id));

        int shown = 0;
        foreach (Incoming message in incoming)
        {
            if (shown >= MaxFromDiscordPerPoll)
            {
                LogThrottled("flood", "Messages came in from Discord faster than the game chat should take them, some were skipped.");
                break;
            }

            string tag = s.GamePrefix.Length > 0 ? s.GamePrefix + " " : "";
            ParentServer.messageGlobal($"{tag}{message.Author}: {message.Text}", s.GameColor);
            Log($"Discord {message.Author}: {message.Text}");
            shown++;
        }
    }

    private List<Incoming> ParseMessages(string json, out ulong newestId)
    {
        newestId = 0;
        var result = new List<Incoming>();

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return result;

        foreach (JsonElement message in document.RootElement.EnumerateArray())
        {
            if (!ulong.TryParse(GetString(message, "id"), out ulong id))
                continue;
            if (id > newestId)
                newestId = id;

            // 0 is a normal message and 19 a reply, everything else is things like "X joined" or pins
            int type = 0;
            if (message.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.Number)
                typeElement.TryGetInt32(out type);
            if (type != 0 && type != 19)
                continue;

            // the bot's own messages, other bots and webhooks are skipped, otherwise the bridge would repeat itself forever
            if (message.TryGetProperty("webhook_id", out JsonElement webhook) && webhook.ValueKind != JsonValueKind.Null)
                continue;
            if (!message.TryGetProperty("author", out JsonElement author) || author.ValueKind != JsonValueKind.Object)
                continue;
            if (author.TryGetProperty("bot", out JsonElement bot) && bot.ValueKind == JsonValueKind.True)
                continue;

            string name = GetString(author, "global_name") ?? GetString(author, "username") ?? "someone";
            string content = GetString(message, "content") ?? "";

            int attachments = CountItems(message, "attachments");
            int stickers = CountItems(message, "sticker_items");
            int embeds = CountItems(message, "embeds");

            if (content.Length == 0 && attachments == 0 && stickers == 0 && embeds == 0)
            {
                // this is what Discord sends for people's messages when the bot is not allowed to read their text
                LogThrottled("intent", "Discord sent a message without any text. Turn on \"Message Content Intent\" for the bot " +
                    "(Developer Portal > your application > Bot) and restart the server.");
                continue;
            }

            var mentionNames = new Dictionary<string, string>();
            if (message.TryGetProperty("mentions", out JsonElement mentions) && mentions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement mention in mentions.EnumerateArray())
                {
                    string? mentionId = GetString(mention, "id");
                    if (mentionId != null)
                        mentionNames[mentionId] = GetString(mention, "global_name") ?? GetString(mention, "username") ?? "user";
                }
            }

            string text = CleanDiscordText(content, mentionNames);

            int extras = attachments + stickers;
            if (extras > 0)
            {
                string note = extras == 1 ? "(+1 attachment)" : $"(+{extras} attachments)";
                text = text.Length == 0 ? note : $"{text} {note}";
            }

            if (text.Length == 0)
                continue; // for example a message that only has an embed

            result.Add(new Incoming(id, CleanText(name, 32, forGame: true), text));
        }

        return result;
    }

    private static string CleanDiscordText(string content, Dictionary<string, string> mentionNames)
    {
        string text = CustomEmoji.Replace(content, ":$1:");
        text = RoleMention.Replace(text, "@role");
        text = ChannelMention.Replace(text, "#channel");
        text = UserMention.Replace(text, m => mentionNames.TryGetValue(m.Groups[1].Value, out string? n) ? "@" + n : "@user");

        return CleanText(text, MaxGameTextLength, forGame: true);
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        return null;
    }

    private static int CountItems(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
            return value.GetArrayLength();
        return 0;
    }

    // ---- talking to Discord ----

    private async Task<HttpResponseMessage> SendAsync(Settings s, HttpMethod method, string path, string? json, CancellationToken token)
    {
        var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bot " + s.Token);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        return await Http.SendAsync(request, token);
    }

    private void HandleFailure(HttpResponseMessage response, string doing)
    {
        int code = (int)response.StatusCode;

        if (code == 429)
        {
            double wait = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 5;
            rateLimitedUntil = Environment.TickCount64 + (long)((Math.Min(wait, 60) + 1) * 1000);
            LogThrottled("429", "Discord asked the bridge to slow down, waiting a moment.");
        }
        else if (code == 401)
        {
            authFailed = true;
            LogThrottled("401", $"Discord did not accept the bot token, the bridge is paused until you fix botToken in {FileName}.");
        }
        else if (code == 403)
        {
            LogThrottled("403", $"The bot is not allowed to do that in the channel ({doing}). " +
                "Give it View Channel, Read Message History and Send Messages there.");
        }
        else if (code == 404)
        {
            LogThrottled("404", $"Discord could not find the channel. Check channelId in {FileName} and that the bot was invited to the server.");
        }
        else
        {
            LogThrottled("http" + code, $"Discord answered HTTP {code} while {doing}.");
        }
    }

    // ---- small helpers ----

    private Settings GetSettings()
    {
        lock (sync)
            return settings;
    }

    private void LogThrottled(string key, string message)
    {
        long now = Environment.TickCount64;
        if (lastLogged.TryGetValue(key, out long last) && now - last < LogThrottleMs)
            return;

        lastLogged[key] = now;
        Log(message);
    }

    // the bot token is a secret, it never goes into the log
    private string Scrub(string text)
    {
        Settings s = GetSettings();
        if (s.Token.Length > 0)
            text = text.Replace(s.Token, "[token]");
        if (s.WebhookUrl.Length > 0)
            text = text.Replace(s.WebhookUrl, "[webhook]");
        return text;
    }

    private static string NameOf(WFPlayer player)
    {
        string name = CleanText(player.Username ?? "", 32, forGame: false);
        return name.Length == 0 ? "Unknown" : name;
    }

    // Makes text safe to move between the game and Discord: one line, no control characters,
    // no broken characters, not too long. Text going into the game also loses [ and ] so nobody
    // on Discord can slip formatting tags into the game chat.
    private static string CleanText(string text, int maxLength, bool forGame)
    {
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (clean.Length > 0 && clean[clean.Length - 1] != ' ')
                    clean.Append(' ');
            }
            else if (forGame && c == '[')
            {
                clean.Append('(');
            }
            else if (forGame && c == ']')
            {
                clean.Append(')');
            }
            else
            {
                clean.Append(c);
            }
        }

        string result = clean.ToString().Trim();

        if (result.Length > maxLength)
        {
            int cut = maxLength - 3;
            if (cut > 0 && char.IsHighSurrogate(result[cut - 1]))
                cut--; // don't cut an emoji in half
            result = result.Substring(0, Math.Max(cut, 0)) + "...";
        }

        // swaps any half-broken characters for the replacement character, the JSON writer refuses them
        return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result));
    }

    // stops game chat from turning into Discord formatting, links or fake mentions
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            if (c == '\\' || c == '*' || c == '_' || c == '~' || c == '`' || c == '|'
                || c == '[' || c == ']' || c == '<' || c == '>')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
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

        string token = values.TryGetValue("botToken", out string? rawToken) ? rawToken.Trim() : "";
        if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase))
            token = token.Substring(4).Trim(); // a common copy and paste slip
        if (token.Any(char.IsWhiteSpace))
        {
            Log("botToken must not contain spaces, so the bridge is off.");
            token = "";
        }
        next.Token = token;

        string channel = values.TryGetValue("channelId", out string? rawChannel) ? rawChannel.Trim() : "";
        if (channel.Length > 0)
        {
            if (ulong.TryParse(channel, out ulong channelId) && channelId > 0)
                next.ChannelId = channelId;
            else
                Log("channelId must be the long number you get from \"Copy Channel ID\" in Discord, so the bridge is off.");
        }

        next.PollSeconds = ReadInt(values, "pollInterval", DefaultPollSeconds, MinPollSeconds, 60);
        next.Debug = ReadBool(values, "debug", false);
        next.DiscordToGame = ReadBool(values, "discordToGame", true);
        next.RelayJoinLeave = ReadBool(values, "relayJoinLeave", true);
        next.RelayServerEvents = ReadBool(values, "relayServerEvents", true);
        next.RelayCommands = ReadBool(values, "relayCommands", false);
        next.RelayLocalChat = ReadBool(values, "relayLocalChat", true);

        if (values.TryGetValue("gamePrefix", out string? prefix))
            next.GamePrefix = CleanText(prefix, 30, forGame: false);

        next.GameColor = ReadHex(values, "gameColor", "ffffff");

        if (values.TryGetValue("webhookUrl", out string? hook) && hook.Length > 0)
        {
            string url = hook.Trim();
            bool valid = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && (uri.Host == "discord.com" || uri.Host == "discordapp.com" || uri.Host == "canary.discord.com" || uri.Host == "ptb.discord.com")
                && uri.AbsolutePath.StartsWith("/api/webhooks/");
            if (valid)
                next.WebhookUrl = url;
            else
                Log("webhookUrl is not a Discord webhook link, the bot will post chat instead.");
        }

        lock (sync)
            settings = next;

        string state = next.Ready ? "on" : "off (botToken or channelId is missing)";
        Log($"The Discord bridge is {state}.");
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

    // a 6 digit hex colour like ff8800, the way the server sends chat colours
    private string ReadHex(Dictionary<string, string> values, string key, string fallback)
    {
        if (!values.TryGetValue(key, out string? raw) || raw.Length == 0)
            return fallback;

        string hex = raw.Trim().TrimStart('#').ToLowerInvariant();
        if (hex.Length == 6 && hex.All(Uri.IsHexDigit))
            return hex;

        Log($"{key} must be a colour like ff8800, using {fallback}.");
        return fallback;
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
            "#  Discord Bridge",
            "# ======================================================================",
            "#",
            "#  Shows the game chat in a Discord channel, and lets people in that",
            "#  channel chat back into the game.",
            "#",
            "#  This needs a Discord BOT (a webhook can only send, it can't read).",
            "#  One time setup:",
            "#",
            "#   1. Go to https://discord.com/developers/applications and press",
            "#      \"New Application\".",
            "#   2. Open the \"Bot\" tab. Press \"Reset Token\" and copy it into botToken",
            "#      below. KEEP IT PRIVATE, anyone with it controls your bot.",
            "#   3. On the same tab, turn ON \"Message Content Intent\". Without it the",
            "#      bot can't see what people type, and nothing reaches the game.",
            "#   4. Open \"OAuth2\" > \"URL Generator\". Tick the \"bot\" scope, then the",
            "#      permissions View Channels, Send Messages and Read Message History.",
            "#      Open the link it makes and add the bot to your server.",
            "#   5. In Discord turn on Settings > Advanced > Developer Mode. Then",
            "#      right click your channel and choose \"Copy Channel ID\" for channelId.",
            "#      If the channel is private, give the bot access to it.",
            "#",
            "#  botToken          = the bot's token from step 2.",
            "#  channelId         = the channel to use, from step 5.",
            "#  pollInterval      = seconds between checks for new Discord messages",
            "#                      (minimum 2). Lower is quicker but busier.",
            "#  discordToGame     = true or false. false makes the bridge one way, the game",
            "#                      shows up in Discord but Discord can't talk to the game.",
            "#  relayJoinLeave    = true or false. Post when players join and leave.",
            "#  relayServerEvents = true or false. Post when the server starts and stops.",
            "#  relayCommands     = true or false. Also post chat that starts with !, which",
            "#                      is how in game commands are typed. Normally off.",
            "#  relayLocalChat    = true or false. Also post local chat, the kind only",
            "#                      nearby players can hear in the game.",
            "#  webhookUrl        = optional. A webhook for the SAME channel (Edit Channel >",
            "#                      Integrations > Webhooks > New Webhook > Copy Webhook URL).",
            "#                      With it, game chat appears under each player's name",
            "#                      instead of the bot's. Leave empty to use the bot.",
            "#  debug             = true or false. Logs every line queued and sent, for tracking down problems.",
            "#  gamePrefix        = optional tag shown before the name of Discord messages in the game,",
            "#                      like [Discord]. Leave empty to show only the name.",
            "#  gameColor         = colour of Discord messages in the game, like ff8800.",
            "#",
            "#  You can edit and save this file while the server is running, it is",
            "#  picked up within about 10 seconds.",
            "# ======================================================================",
            "",
            "botToken =",
            "channelId =",
            "",
            "pollInterval = 3",
            "discordToGame = true",
            "relayJoinLeave = true",
            "relayServerEvents = true",
            "relayCommands = false",
            "relayLocalChat = true",
            "",
            "webhookUrl =",
            "debug = false",
            "gamePrefix =",
            "gameColor = ffffff",
            "",
        };

        File.WriteAllText(filePath, string.Join(Environment.NewLine, lines));
        Log($"Created {FileName}, follow the steps in it to connect your Discord bot.");
    }
}
