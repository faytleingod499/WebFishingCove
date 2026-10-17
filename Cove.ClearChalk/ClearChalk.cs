using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Chalk;
using Cove.Server.Plugins;

/// <summary>
/// Adds the admin command  !clearchalk  that wipes one canvas, or all of them, for everybody.
///
///   !clearchalk 1      clears canvas 1
///   !clearchalk all    clears every canvas
///   !clearoutside      clears only the chalk that sits outside the canvases, a drawing on them is left alone
///   !clearoutside 2    the same for canvas 2 only
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
        SetCommandAccess("clearchalk", CommandAccess.Admin);

        RegisterCommand(
            command: "clearoutside",
            aliases: ["chalkoutside"],
            callback: (player, args) =>
            {
                try
                {
                    RunOutside(player, args);
                }
                catch (Exception e)
                {
                    Log($"!clearoutside failed: {e.Message}");
                    SendPlayerChatMessage(player, "Something went wrong while clearing the chalk, see the server log.");
                }
            }
        );
        SetCommandDescription("clearoutside", "Clears only the chalk outside the canvases (optionally one canvas 0-3)");
        SetCommandAccess("clearoutside", CommandAccess.Admin);
    }

    public override void onEnd()
    {
        base.onEnd();
        ParentServer.UnregisterCommand("clearchalk");
        ParentServer.UnregisterCommand("clearoutside");
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

    // Chalk that sits outside the canvas, which only a modified game can put there. The drawing on the canvas itself
    // is not touched.
    private void RunOutside(WFPlayer player, string[] args)
    {
        if (!IsPlayerAdmin(player))
            return;

        string what = args.Length == 0 ? "all" : args[0].Trim().ToLowerInvariant();
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
            SendPlayerChatMessage(player, "Usage: !clearoutside [0-3 or all]");
            return;
        }

        // the same limit the server uses to refuse chalk, chalkCanvasSize in server.cfg. 0 there turns the check
        // off, then the normal canvas size is used.
        int size = ChalkCanvas.CanvasSize > 0 ? ChalkCanvas.CanvasSize : 200;

        int removed = 0;
        var cleaned = new List<int>();

        foreach (int id in ids)
        {
            ChalkCanvas? canvas;
            lock (ParentServer.stateLock)
                canvas = ParentServer.chalkCanvas.FirstOrDefault(c => c.canvasID == id);
            if (canvas == null)
                continue;

            var erase = new Dictionary<int, object>();
            lock (canvas.chalkImage)
            {
                foreach (var entry in canvas.chalkImage)
                {
                    if (entry.Value == (long)COLOR.NONE)
                        continue;

                    Vector2 at = entry.Key;
                    bool outside = !float.IsFinite(at.x) || !float.IsFinite(at.y)
                        || at.x < 0 || at.y < 0 || at.x > size || at.y > size;
                    if (outside)
                        erase[erase.Count] = new Dictionary<int, object> { { 0, at }, { 1, (long)COLOR.NONE } };
                }
            }

            if (erase.Count == 0)
                continue;

            // removes them from the server's copy and tells every player to erase them
            canvas.drawChalk(erase);

            removed += erase.Count;
            cleaned.Add(id);
        }

        if (removed == 0)
        {
            SendPlayerChatMessage(player, what == "all" ? "There is no chalk outside the canvases." : $"There is no chalk outside canvas {ids[0]}.");
            return;
        }

        string list = string.Join(", ", cleaned);
        Log($"{player.Username} cleared {removed} chalk cells outside canvas {list}.");
        SendPlayerChatMessage(player, $"Cleared {removed} chalk cells outside canvas {list}.");
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
