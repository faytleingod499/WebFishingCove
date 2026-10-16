using Cove.Server;
using Cove.Server.Plugins;

/// <summary>
/// Sends up to five configurable chat messages to every player, each on its own interval.
/// Configured through automessages.txt in the server's root directory (next to server.cfg).
/// </summary>
public class AutoMessages : CovePlugin
{
    private const string FileName = "automessages.txt";
    private const int MaxMessages = 5;

    // Anything faster than this is just spam, and a typo like "interval1 = 1" would flood the chat.
    private const int MinIntervalSeconds = 5;

    // How often the config file is checked for edits. Saving the file while the server runs reloads it.
    private const long ReloadCheckMs = 10_000;

    private class Entry
    {
        public int Slot;
        public string Text = "";
        public int IntervalSeconds;
        public long NextSendTick;
    }

    // onUpdate runs on a timer thread, onInit/onEnd on others, so all state below is guarded by this lock.
    private readonly object sync = new();
    private List<Entry> entries = new();
    private string filePath = "";
    private DateTime loadedWriteTime = DateTime.MinValue;
    private long nextReloadCheckTick;
    private bool running;

    public AutoMessages(CoveServer server) : base(server) { }

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
            entries = new List<Entry>();
        }
    }

    // triggered 12 times a second by the server
    public override void onUpdate()
    {
        var due = new List<string>();

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

            foreach (Entry entry in entries)
            {
                if (now >= entry.NextSendTick)
                {
                    due.Add(entry.Text);
                    entry.NextSendTick = now + entry.IntervalSeconds * 1000L;
                }
            }
        }

        // send outside of our lock so a slow send can never block the config reload
        foreach (string text in due)
        {
            SendGlobalChatMessage(text);
        }
    }

    private void ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(filePath))
                return; // keep running with the messages we already have

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
        var loaded = new List<Entry>();
        long now = Environment.TickCount64;

        for (int slot = 1; slot <= MaxMessages; slot++)
        {
            values.TryGetValue($"message{slot}", out string? text);
            text = (text ?? "").Replace("\\n", "\n").Trim();

            if (text.Length == 0)
                continue; // an empty message means this slot is turned off

            values.TryGetValue($"interval{slot}", out string? rawInterval);
            if (!int.TryParse(rawInterval, out int seconds) || seconds <= 0)
            {
                Log($"message{slot} needs interval{slot} to be a number of seconds above 0, so it is turned off.");
                continue;
            }

            if (seconds < MinIntervalSeconds)
            {
                Log($"interval{slot} is below the minimum, using {MinIntervalSeconds} seconds.");
                seconds = MinIntervalSeconds;
            }

            loaded.Add(new Entry
            {
                Slot = slot,
                Text = text,
                IntervalSeconds = seconds,
                // the first send happens after one full interval, not the moment the server starts
                NextSendTick = now + seconds * 1000L,
            });
        }

        entries = loaded;
        Log($"Loaded {loaded.Count} auto message(s) from {FileName}.");
    }

    // simple "key = value" lines, lines starting with # are comments.
    // only the FIRST '=' splits the line, so messages can contain '=' themselves.
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
            "#  Auto Messages",
            "# ======================================================================",
            "#",
            "#  Up to 5 chat messages, each sent to every player on its own timer.",
            "#",
            "#  messageN   = the text to send.  Leave it empty to turn that slot off.",
            "#  intervalN  = seconds between sends (minimum 5).",
            "#               600 = every 10 minutes, 1800 = every 30 minutes.",
            "#",
            "#  Use \\n inside a message for a line break.",
            "#  Lines starting with # are ignored.",
            "#",
            "#  You can edit and save this file while the server is running,",
            "#  it is picked up within about 10 seconds. Changing it restarts",
            "#  every timer, so each message waits one full interval again.",
            "#",
            "#  Example:",
            "#    message1  = Remember to be kind to other fishers!",
            "#    interval1 = 600",
            "# ======================================================================",
            "",
            "message1 =",
            "interval1 = 600",
            "",
            "message2 =",
            "interval2 = 900",
            "",
            "message3 =",
            "interval3 = 1200",
            "",
            "message4 =",
            "interval4 = 1800",
            "",
            "message5 =",
            "interval5 = 3600",
            "",
        };

        File.WriteAllText(filePath, string.Join(Environment.NewLine, lines));
        Log($"Created {FileName}, add your messages to it.");
    }
}
