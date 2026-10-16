using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Marks every chat message as Global or Local, in the console and in log files.
///
/// Global chat is what everybody on the server sees. Local chat is only shown by the game to players
/// standing close to the sender, but the server still receives it, so it is labelled here.
///
/// Files are written to the  chatlogs  folder next to the server, one pair per day:
///   chatlogs/global-2026-10-16.log
///   chatlogs/local-2026-10-16.log
/// Keeping them in separate files makes it easy to share the global one and keep the local one private.
/// </summary>
public class ChatLog : CovePlugin
{
    private const string Folder = "chatlogs";
    private readonly object fileLock = new();

    public ChatLog(CoveServer server) : base(server) { }

    public override void onChatMessage(WFPlayer sender, string message, bool local, Vector3? position, string zone)
    {
        base.onChatMessage(sender, message, local, position, zone);

        // commands are not chat
        if (message.StartsWith("!"))
            return;

        string label = local ? "Local" : "Global";
        string clean = message.Replace("\r", " ").Replace("\n", " ");
        string where = local && position != null
            ? $" {zone} ({position.x:0}, {position.y:0}, {position.z:0})"
            : "";

        Log($"[{label}] {sender.Username}: {clean}");

        try
        {
            DateTime now = DateTime.Now;
            string file = Path.Combine(Folder, $"{label.ToLowerInvariant()}-{now:yyyy-MM-dd}.log");
            string line = $"{now:HH:mm:ss} [{sender.FisherID}] {sender.Username} ({sender.SteamId.m_SteamID}){where}: {clean}{Environment.NewLine}";
            lock (fileLock)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(file, line);
            }
        }
        catch (Exception e)
        {
            Log($"Could not write the chat log: {e.Message}");
        }
    }
}
