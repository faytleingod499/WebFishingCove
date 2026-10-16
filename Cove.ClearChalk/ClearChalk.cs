using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Chalk;
using Cove.Server.Plugins;

/// <summary>
/// Adds the admin command  !clearchalk  that wipes one canvas, or all of them, for everybody.
///
///   !clearchalk 1      clears canvas 1
///   !clearchalk all    clears every canvas
///
/// Canvas numbers are the ones used in the ChalkObserver picture and in its stats.txt: 0 is top left,
/// 1 top right, 2 bottom left and 3 bottom right.
/// </summary>
public class ClearChalk : CovePlugin
{
    private const int CanvasCount = 4;
    private const string Usage = "Usage: !clearchalk <0-3 or all>";

    public ClearChalk(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        RegisterCommand(
            command: "clearchalk",
            aliases: ["chalkclear"],
            callback: (player, args) =>
            {
                try
                {
                    Run(player, args);
                }
                catch (Exception e)
                {
                    Log($"!clearchalk failed: {e.Message}");
                    SendPlayerChatMessage(player, "Something went wrong while clearing the chalk, see the server log.");
                }
            }
        );
        SetCommandDescription("clearchalk", "Clears a chalk canvas (0-3) or all of them");
    }

    public override void onEnd()
    {
        base.onEnd();
        ParentServer.UnregisterCommand("clearchalk");
    }

    private void Run(WFPlayer player, string[] args)
    {
        if (!IsPlayerAdmin(player))
            return; // same as the built-in admin commands, non-admins get no answer

        if (args.Length != 1)
        {
            SendPlayerChatMessage(player, Usage);
            return;
        }

        string what = args[0].Trim().ToLowerInvariant();
        var ids = new List<int>();

        if (what == "all")
        {
            for (int i = 0; i < CanvasCount; i++)
                ids.Add(i);
        }
        else if (int.TryParse(what, out int parsed) && parsed >= 0 && parsed < CanvasCount)
        {
            ids.Add(parsed);
        }
        else
        {
            SendPlayerChatMessage(player, Usage);
            return;
        }

        int clearedCells = 0;
        var clearedCanvases = new List<int>();

        foreach (int id in ids)
        {
            ChalkCanvas? canvas;
            lock (ParentServer.stateLock)
                canvas = ParentServer.chalkCanvas.FirstOrDefault(c => c.canvasID == id);

            int drawn = canvas == null ? 0 : CountDrawn(canvas);
            if (canvas == null || drawn == 0)
                continue;

            // sets every cell to "empty" and tells all players
            canvas.clearCanvas(true);

            clearedCells += drawn;
            clearedCanvases.Add(id);
        }

        if (clearedCanvases.Count == 0)
        {
            SendPlayerChatMessage(player, what == "all" ? "All the canvases are already empty." : $"Canvas {ids[0]} is already empty.");
            return;
        }

        string list = string.Join(", ", clearedCanvases);
        Log($"{player.Username} cleared canvas {list} ({clearedCells} chalk cells).");
        SendPlayerChatMessage(player, $"Cleared canvas {list} ({clearedCells} chalk cells).");
    }

    private static int CountDrawn(ChalkCanvas canvas)
    {
        int count = 0;
        lock (canvas.chalkImage)
        {
            foreach (long color in canvas.chalkImage.Values)
            {
                if (color != (long)COLOR.NONE)
                    count++;
            }
        }
        return count;
    }
}
