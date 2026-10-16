using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Plugins;

/// <summary>
/// Adds the command  !feed  for everybody. The player gets a chat message saying they fed the wolves.
/// To change the wording, edit the Replies list below and rebuild.
/// </summary>
public class Feed : CovePlugin
{
    private static readonly string[] Replies =
    [
        "You fed the wolves.",
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
                    SendPlayerChatMessage(player, Replies[Random.Shared.Next(Replies.Length)]);
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
