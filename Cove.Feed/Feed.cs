using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Adds the command  !feed  for everybody. Everybody sees a chat message saying who fed the wolves, followed by a howl.
/// To change the wording, edit the Replies list below and rebuild, {0} is the player's name.
/// </summary>
public class Feed : CovePlugin
{
    // sent as a second line right after the reply
    private const string Howl = "Awooooooooo!";

    private static readonly string[] Replies =
    [
        "{0} fed the wolves.",
    ];

    public Feed(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        RegisterCommand(
            command: "feed",
            aliases: [],
            callback: (player, args) =>
            {
                try
                {
                    SendGlobalChatMessage(string.Format(Replies[Random.Shared.Next(Replies.Length)], player.Username));
                    SendGlobalChatMessage(Howl);
                }
                catch (Exception e)
                {
                    Log($"!feed failed: {e.Message}");
                }
            }
        );
        SetCommandDescription("feed", "Feed the wolves");
    }

    public override void onEnd()
    {
        base.onEnd();
        ParentServer.UnregisterCommand("feed");
    }
}
