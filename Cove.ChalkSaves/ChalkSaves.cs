using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.GodotFormat;
using Cove.Server;
using Cove.Server.Actor;
using Cove.Server.Chalk;
using Cove.Server.Plugins;
using Steamworks;

/// <summary>
/// Lets players save a chalk canvas under a name and put it back on any canvas later.
/// Every player has their own folder of saves, so a list only shows their own and names never clash.
///
///   !chalksave &lt;name&gt; &lt;0-3&gt;     saves that canvas
///   !chalkload &lt;name&gt; &lt;0-3&gt;     replaces that canvas with one of your saves
///   !chalklist                  lists your saves
///   !chalkdelete &lt;name&gt;         deletes one of your saves
///   !chalkshare &lt;name&gt; &lt;player&gt;  offers a copy of one of your saves to a player who is online
///   !chalkaccept [new name]     takes the copy that was offered to you
///   !chalkdecline               turns the offer down
///   !chalkowners [off], !chalkbrowse &lt;Steam ID&gt;   admins only, used quietly by the Wolfpark HUD's Chalk menu
///
/// Admins can add a player (name or Steam ID) to the end of any of these to use that player's saves.
///
/// Saves are files in ChalkSaves/&lt;Steam ID&gt;/ next to the server. Before a canvas is replaced, what was on it
/// is kept in ChalkSaves/_backups so a load can be undone by hand. Settings are in chalksaves.txt.
/// </summary>
public class ChalkSaves : CovePlugin
{
    private const string FolderName = "ChalkSaves";
    private const string BackupFolderName = "_backups";
    private const string SettingsFileName = "chalksaves.txt";

    private const int CanvasCount = 4;
    private const int MaxNameLength = 32;
    private const int MaxBackups = 20;
    private const int MaxSavesDefault = 20;
    private const long OfferLifetimeMs = 2 * 60_000;
    private const long ShareCooldownMs = 10_000;
    private const int MaxListed = 30;

    // same limits the server uses, a hand edited or damaged file can't flood the server
    private const int MaxCells = 250_000;
    private const int MaxCoordinate = 4096;
    private const long MaxColor = 255;

    private readonly record struct Cell(int X, int Y, long Color);

    private sealed class SaveFile
    {
        public ulong Owner;
        public string OwnerName = "";
        public string Saved = "";
        public List<Cell> Cells = new();
    }

    // settings
    private bool saveAdminsOnly;
    private bool loadAdminsOnly;
    private int maxSaves = MaxSavesDefault; // per player
    private int loadCooldownSeconds = 30;

    private string folder = "";
    private string backupFolder = "";
    private string settingsPath = "";

    // commands run on the server's network thread, one at a time per packet, but not necessarily one at a time overall
    private readonly object fileLock = new();
    private readonly Dictionary<ulong, long> lastLoad = new();

    // A share is an offer the other player has to accept, so nobody's folder can be filled by somebody else.
    // One waiting offer per player, it is kept in memory and runs out after two minutes.
    private sealed record Offer(string FromName, string Name, SaveFile Save, long ExpiresTick);
    private readonly Dictionary<ulong, Offer> offers = new();
    private readonly Dictionary<ulong, long> lastShare = new();

    // what the Wolfpark HUD mod reads to show the saves in a menu (see PublishLobbyData)
    private const long PublishEveryMs = 3000;
    private volatile bool publishDirty = true;
    private long nextPublishTick;
    private ulong publishedLobby;
    private string publishedInfo = "";
    private readonly Dictionary<ulong, string> publishedLists = new();
    private readonly Dictionary<ulong, string> publishedOffers = new();

    // admins looking at everyone's saves in the HUD: admin -> the player whose saves they opened (0 for none)
    private readonly Dictionary<ulong, ulong> adminViews = new();
    private readonly Dictionary<ulong, string> publishedOwners = new();
    private readonly Dictionary<ulong, string> publishedBrowse = new();
    private readonly Dictionary<ulong, string> ownerNames = new(); // Steam ID -> name, read once from a save
    private const int MaxOwnersListed = 120;

    public ChalkSaves(CoveServer server) : base(server) { }

    public override void onInit()
    {
        base.onInit();

        string root = AppDomain.CurrentDomain.BaseDirectory;
        folder = Path.Combine(root, FolderName);
        backupFolder = Path.Combine(folder, BackupFolderName);
        settingsPath = Path.Combine(root, SettingsFileName);

        try
        {
            Directory.CreateDirectory(backupFolder);
            EnsureSettingsFileExists();
            ReadSettings();
            MoveOldSaves();
        }
        catch (Exception e)
        {
            Log($"Could not set up {FolderName} or {SettingsFileName}, using the defaults: {e.Message}");
        }

        Register("chalksave", "Saves a chalk canvas | Usage: !chalksave <name> <0-3>", SaveCanvas);
        Register("chalkload", "Puts a saved chalk canvas on a canvas | Usage: !chalkload <name> <0-3>", LoadCanvas);
        Register("chalklist", "Lists the saved chalk canvases", ListSaves);
        Register("chalkdelete", "Deletes a saved chalk canvas | Usage: !chalkdelete <name>", DeleteSave);
        Register("chalkshare", "Offers a copy of a save to a player | Usage: !chalkshare <name> <player>", ShareSave);
        Register("chalkaccept", "Takes the chalk save somebody offered you | Usage: !chalkaccept [new name]", AcceptOffer);
        Register("chalkdecline", "Turns down the chalk save somebody offered you", DeclineOffer);
        // used quietly by the HUD's admin list, they answer through the lobby data and not in chat
        Register("chalkowners", "Shows every player's chalk saves in the HUD | Usage: !chalkowners [off]", ShowOwners);
        Register("chalkbrowse", "Opens one player's chalk saves in the HUD | Usage: !chalkbrowse <Steam ID>", BrowseOwner);
        SetCommandAccess("chalkowners", CommandAccess.Admin);
        SetCommandAccess("chalkbrowse", CommandAccess.Admin);

        // with admin only access set in chalksaves.txt, the other players don't see these in !help
        if (saveAdminsOnly)
            SetCommandAccess("chalksave", CommandAccess.Admin);
        if (loadAdminsOnly)
            SetCommandAccess("chalkload", CommandAccess.Admin);
    }

    public override void onEnd()
    {
        base.onEnd();
        lock (fileLock)
            offers.Clear();
        foreach (string command in new[] { "chalksave", "chalkload", "chalklist", "chalkdelete", "chalkshare", "chalkaccept", "chalkdecline", "chalkowners", "chalkbrowse" })
            ParentServer.UnregisterCommand(command);
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

    public override void onUpdate()
    {
        PublishLobbyData();
    }

    // ---- the Wolfpark HUD mod ----

    // The HUD mod has a Chalk menu. It finds what to show in the Steam lobby data:
    //   wp_chalk         = "saveAll,loadAll,maxSaves,loadCooldown"   (1 = everyone may, 0 = admins only)
    //   wp_cs<SteamID>   = that player's save names, "name,name,name"
    //   wp_co<SteamID>   = a share waiting for that player, "from~name", or empty
    // The mod then uses the normal commands, so every check above still applies.
    private void PublishLobbyData()
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
                publishedLists.Clear();
                publishedOffers.Clear();
                publishedOwners.Clear();
                publishedBrowse.Clear();
                publishedInfo = "";
                publishedLobby = lobby.m_SteamID;
            }

            string info = $"{(saveAdminsOnly ? 0 : 1)},{(loadAdminsOnly ? 0 : 1)},{maxSaves},{loadCooldownSeconds}";
            if (info != publishedInfo)
            {
                SteamMatchmaking.SetLobbyData(lobby, "wp_chalk", info);
                publishedInfo = info;
            }

            var lists = new Dictionary<ulong, string>();
            var offerTexts = new Dictionary<ulong, string>();
            lock (fileLock)
            {
                foreach (WFPlayer player in GetAllPlayers())
                {
                    ulong id = player.SteamId.m_SteamID;
                    string dir = FolderFor(id);
                    var names = Directory.Exists(dir)
                        ? Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f))
                            .Where(n => TryName(n, out string clean) && clean == n)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string>();
                    lists[id] = string.Join(",", names);

                    string offerText = "";
                    if (offers.TryGetValue(id, out Offer? offer))
                    {
                        if (now > offer.ExpiresTick)
                            offers.Remove(id);
                        else
                            offerText = $"{offer.FromName.Replace('~', ' ').Replace(',', ' ')}~{offer.Name}";
                    }
                    offerTexts[id] = offerText;
                }
            }

            Publish(lobby, "wp_cs", lists, publishedLists);
            Publish(lobby, "wp_co", offerTexts, publishedOffers);

            // for admins with the HUD's "All players" list open:
            //   wp_ca<admin> = id~name~count;...   wp_cb<admin> = "id~name,name" for the player they opened
            var ownersFor = new Dictionary<ulong, string>();
            var browseFor = new Dictionary<ulong, string>();
            lock (fileLock)
            {
                var onlineAdmins = GetAllPlayers().Where(p => IsPlayerAdmin(p)).Select(p => p.SteamId.m_SteamID).ToHashSet();
                foreach (ulong gone in adminViews.Keys.Where(id => !onlineAdmins.Contains(id)).ToList())
                    adminViews.Remove(gone);

                string owners = adminViews.Count > 0 ? OwnersText() : "";
                foreach (var view in adminViews)
                {
                    ownersFor[view.Key] = owners;
                    if (view.Value == 0)
                        continue;
                    string dir = FolderFor(view.Value);
                    var names = Directory.Exists(dir)
                        ? Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f))
                            .Where(n => TryName(n, out string clean) && clean == n)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string>();
                    browseFor[view.Key] = $"{view.Value}~{string.Join(",", names)}";
                }
            }
            Publish(lobby, "wp_ca", ownersFor, publishedOwners);
            Publish(lobby, "wp_cb", browseFor, publishedBrowse);
        }
        catch (Exception e)
        {
            Log($"Could not update the HUD data: {e.Message}");
        }
    }

    private static void Publish(CSteamID lobby, string prefix, Dictionary<ulong, string> wanted, Dictionary<ulong, string> published)
    {
        foreach (var pair in wanted)
        {
            if (published.TryGetValue(pair.Key, out string? old) && old == pair.Value)
                continue;
            SteamMatchmaking.SetLobbyData(lobby, prefix + pair.Key, pair.Value);
            published[pair.Key] = pair.Value;
        }

        // players who left, so the lobby data does not grow forever
        foreach (ulong gone in published.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            SteamMatchmaking.SetLobbyData(lobby, prefix + gone, "");
            published.Remove(gone);
        }
    }

    private void ClearLobbyData()
    {
        try
        {
            CSteamID lobby = ParentServer.SteamLobby;
            if (lobby.IsValid() && publishedLobby == lobby.m_SteamID)
            {
                SteamMatchmaking.SetLobbyData(lobby, "wp_chalk", "");
                foreach (ulong id in publishedLists.Keys)
                    SteamMatchmaking.SetLobbyData(lobby, "wp_cs" + id, "");
                foreach (ulong id in publishedOffers.Keys)
                    SteamMatchmaking.SetLobbyData(lobby, "wp_co" + id, "");
                foreach (ulong id in publishedOwners.Keys)
                    SteamMatchmaking.SetLobbyData(lobby, "wp_ca" + id, "");
                foreach (ulong id in publishedBrowse.Keys)
                    SteamMatchmaking.SetLobbyData(lobby, "wp_cb" + id, "");
            }
        }
        catch (Exception e)
        {
            Log($"Could not clear the HUD data: {e.Message}");
        }
        publishedLists.Clear();
        publishedOffers.Clear();
        publishedOwners.Clear();
        publishedBrowse.Clear();
        publishedInfo = "";
        publishedLobby = 0;
    }

    // saves from the first version of this plugin sat directly in ChalkSaves, move them into their owner's folder
    private void MoveOldSaves()
    {
        int moved = 0;
        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            SaveFile? save = Read(file);
            if (save == null || save.Owner == 0)
                continue; // leave anything we can't place where it is

            string target = Path.Combine(FolderFor(save.Owner), Path.GetFileName(file));
            if (File.Exists(target))
                continue;

            Directory.CreateDirectory(FolderFor(save.Owner));
            File.Move(file, target);
            moved++;
        }
        if (moved > 0)
            Log($"Moved {moved} older save(s) into their owner's folder.");
    }

    private void Register(string command, string description, Action<WFPlayer, string[]> run)
    {
        RegisterCommand(
            command: command,
            aliases: [],
            callback: (player, args) =>
            {
                try
                {
                    run(player, args);
                    publishDirty = true; // the HUD menu shows the change right away
                }
                catch (Exception e)
                {
                    Log($"!{command} failed: {e.Message}");
                    SendPlayerChatMessage(player, "Something went wrong, see the server log.");
                }
            }
        );
        SetCommandDescription(command, description);
    }

    // ---- commands ----

    // Works out whose saves a command uses. Normally the player's own. An admin can add a player (name or Steam ID)
    // after the normal arguments to use that player's saves.
    private bool TryTarget(WFPlayer player, string[] args, int normalArgs, out ulong target, out string targetName)
    {
        target = player.SteamId.m_SteamID;
        targetName = player.Username;

        if (args.Length < normalArgs)
            return false;

        // everything after the normal arguments is the player, a name can have spaces in it like "Fayt | Ether"
        string ident = string.Join(" ", args.Skip(normalArgs)).Trim();
        if (ident.Length == 0)
            return true;

        if (!IsPlayerAdmin(player))
            return false;

        if (Regex.IsMatch(ident, @"^\d{17}$") && ulong.TryParse(ident, out ulong steamId))
        {
            target = steamId;
            targetName = ident;
            return true;
        }

        WFPlayer? found = ParentServer.GetPlayer(ident);
        if (found == null)
        {
            SendPlayerChatMessage(player, $"No player called \"{ident}\" is online. Use their Steam ID instead.");
            targetName = "";
            return true; // already answered, the caller checks targetName
        }

        target = found.SteamId.m_SteamID;
        targetName = found.Username;
        return true;
    }

    private static string Of(WFPlayer player, string targetName) => targetName == player.Username ? "" : $" for {targetName}";

    private void SaveCanvas(WFPlayer player, string[] args)
    {
        if (saveAdminsOnly && !IsPlayerAdmin(player))
        {
            SendPlayerChatMessage(player, "Only admins can save canvases.");
            return;
        }
        if (args.Length < 2 || !TryName(args[0], out string name) || !TryCanvas(args[1], out int canvasId)
            || !TryTarget(player, args, 2, out ulong owner, out string ownerName))
        {
            SendPlayerChatMessage(player, "Usage: !chalksave <name> <0-3>. Names can use letters, numbers, - and _.");
            return;
        }
        if (ownerName.Length == 0)
            return;

        List<Cell> cells = Snapshot(canvasId);
        if (cells.Count == 0)
        {
            SendPlayerChatMessage(player, $"Canvas {canvasId} is empty, there is nothing to save.");
            return;
        }

        lock (fileLock)
        {
            string dir = FolderFor(owner);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".json");
            bool exists = File.Exists(path);

            if (!exists && Directory.GetFiles(dir, "*.json").Length >= maxSaves)
            {
                SendPlayerChatMessage(player, $"That is already {maxSaves} saves, delete one first with !chalkdelete <name>.");
                return;
            }

            WriteSave(path, new SaveFile
            {
                Owner = owner,
                OwnerName = ownerName,
                Saved = DateTime.UtcNow.ToString("o"),
                Cells = cells,
            });

            Log($"{player.Username} saved canvas {canvasId} as \"{name}\"{Of(player, ownerName)} ({cells.Count} cells){(exists ? ", replacing the old save" : "")}.");
            SendPlayerChatMessage(player, $"Saved canvas {canvasId} as \"{name}\"{Of(player, ownerName)}{(exists ? " (replaced the old save)" : "")}.");
        }
    }

    private void LoadCanvas(WFPlayer player, string[] args)
    {
        bool admin = IsPlayerAdmin(player);
        if (loadAdminsOnly && !admin)
        {
            SendPlayerChatMessage(player, "Only admins can load canvases.");
            return;
        }
        if (args.Length < 2 || !TryName(args[0], out string name) || !TryCanvas(args[1], out int canvasId)
            || !TryTarget(player, args, 2, out ulong owner, out string ownerName))
        {
            SendPlayerChatMessage(player, "Usage: !chalkload <name> <0-3>");
            return;
        }
        if (ownerName.Length == 0)
            return;

        ulong id = player.SteamId.m_SteamID;
        long now = Environment.TickCount64;
        lock (fileLock)
        {
            if (!admin && lastLoad.TryGetValue(id, out long last) && now - last < loadCooldownSeconds * 1000L)
            {
                int wait = (int)Math.Ceiling((loadCooldownSeconds * 1000L - (now - last)) / 1000.0);
                SendPlayerChatMessage(player, $"Please wait {wait} more second(s) before loading another canvas.");
                return;
            }
        }

        string path = Path.Combine(FolderFor(owner), name + ".json");
        SaveFile? save;
        bool exists;
        lock (fileLock)
        {
            exists = File.Exists(path);
            save = exists ? Read(path) : null;
        }

        if (save == null)
        {
            SendPlayerChatMessage(player, exists ? $"\"{name}\" could not be read." : $"You have no save called \"{name}\". Try !chalklist.");
            return;
        }

        ChalkCanvas canvas = GetOrCreateCanvas(canvasId);

        // keep what is on the canvas now, in case this was a mistake
        List<Cell> before = Snapshot(canvasId);
        if (before.Count > 0)
        {
            lock (fileLock)
            {
                Directory.CreateDirectory(backupFolder);
                WriteSave(Path.Combine(backupFolder, $"canvas{canvasId}-{DateTime.Now:yyyyMMdd-HHmmss}-{player.SteamId.m_SteamID}.json"), new SaveFile
                {
                    Owner = player.SteamId.m_SteamID,
                    OwnerName = player.Username,
                    Saved = DateTime.UtcNow.ToString("o"),
                    Cells = before,
                });
                PruneBackups();
            }
        }

        // every cell that is on the canvas now but not in the save is erased, then the saved cells are drawn
        var wanted = new Dictionary<(int, int), long>();
        foreach (Cell c in save.Cells)
            wanted[(c.X, c.Y)] = c.Color;

        var changes = new Dictionary<int, object>();
        int n = 0;
        foreach (Cell c in before)
        {
            if (!wanted.ContainsKey((c.X, c.Y)))
                changes[n++] = new Dictionary<int, object> { { 0, new Vector2(c.X, c.Y) }, { 1, (long)COLOR.NONE } };
        }
        foreach (var w in wanted)
            changes[n++] = new Dictionary<int, object> { { 0, new Vector2(w.Key.Item1, w.Key.Item2) }, { 1, w.Value } };

        // updates the server's copy and tells every player, like the chalk clearing does
        canvas.drawChalk(changes);

        lock (fileLock)
            lastLoad[id] = now;

        Log($"{player.Username} loaded \"{name}\"{Of(player, ownerName)} ({save.Cells.Count} cells) onto canvas {canvasId}, replacing {before.Count} cells.");
        SendPlayerChatMessage(player, $"Loaded \"{name}\"{Of(player, ownerName)} onto canvas {canvasId}.");
    }

    private void ListSaves(WFPlayer player, string[] args)
    {
        if (!TryTarget(player, args, 0, out ulong owner, out string ownerName))
        {
            SendPlayerChatMessage(player, "Usage: !chalklist");
            return;
        }
        if (ownerName.Length == 0)
            return;

        string[] files;
        lock (fileLock)
        {
            string dir = FolderFor(owner);
            files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>();
        }

        string whose = Of(player, ownerName).Length == 0 ? "You have" : $"{ownerName} has";
        if (files.Length == 0)
        {
            SendPlayerChatMessage(player, $"{whose} no saved canvases. Use !chalksave <name> <0-3>.");
            return;
        }

        var names = files.Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        string shown = string.Join(", ", names.Take(MaxListed));
        string more = names.Count > MaxListed ? $" and {names.Count - MaxListed} more" : "";
        SendPlayerChatMessage(player, $"{whose} {names.Count} saved canvas(es): {shown}{more}");
    }

    private void DeleteSave(WFPlayer player, string[] args)
    {
        if (args.Length < 1 || !TryName(args[0], out string name) || !TryTarget(player, args, 1, out ulong owner, out string ownerName))
        {
            SendPlayerChatMessage(player, "Usage: !chalkdelete <name>");
            return;
        }
        if (ownerName.Length == 0)
            return;

        lock (fileLock)
        {
            string path = Path.Combine(FolderFor(owner), name + ".json");
            if (!File.Exists(path))
            {
                SendPlayerChatMessage(player, $"There is no save called \"{name}\"{Of(player, ownerName)}.");
                return;
            }

            File.Delete(path);
            Log($"{player.Username} deleted the save \"{name}\"{Of(player, ownerName)}.");
            SendPlayerChatMessage(player, $"Deleted \"{name}\"{Of(player, ownerName)}.");
        }
    }

    private void ShareSave(WFPlayer player, string[] args)
    {
        if (args.Length < 2 || !TryName(args[0], out string name))
        {
            SendPlayerChatMessage(player, "Usage: !chalkshare <name> <player>");
            return;
        }

        string ident = string.Join(" ", args.Skip(1)).Trim();
        WFPlayer? to = FindOnline(ident);
        if (to == null)
        {
            SendPlayerChatMessage(player, $"No player called \"{ident}\" is online.");
            return;
        }
        if (to.SteamId.m_SteamID == player.SteamId.m_SteamID)
        {
            SendPlayerChatMessage(player, "You already have that save.");
            return;
        }

        long now = Environment.TickCount64;
        lock (fileLock)
        {
            if (lastShare.TryGetValue(player.SteamId.m_SteamID, out long last) && now - last < ShareCooldownMs)
            {
                SendPlayerChatMessage(player, "Please wait a few seconds before sharing again.");
                return;
            }

            string path = Path.Combine(FolderFor(player.SteamId.m_SteamID), name + ".json");
            SaveFile? save = File.Exists(path) ? Read(path) : null;
            if (save == null)
            {
                SendPlayerChatMessage(player, $"You have no save called \"{name}\". Try !chalklist.");
                return;
            }

            lastShare[player.SteamId.m_SteamID] = now;
            offers[to.SteamId.m_SteamID] = new Offer(player.Username, name, save, now + OfferLifetimeMs);
        }

        SendPlayerChatMessage(player, $"Offered \"{name}\" to {to.Username}. They have 2 minutes to accept.");
        SendPlayerChatMessage(to, $"{player.Username} wants to share the chalk save \"{name}\" with you. Type !chalkaccept to keep a copy, or !chalkdecline.");
        Log($"{player.Username} offered \"{name}\" to {to.Username}.");
    }

    private void AcceptOffer(WFPlayer player, string[] args)
    {
        ulong id = player.SteamId.m_SteamID;
        Offer? offer;
        lock (fileLock)
        {
            offers.TryGetValue(id, out offer);
            if (offer != null && Environment.TickCount64 > offer.ExpiresTick)
            {
                offers.Remove(id);
                offer = null;
            }
        }

        if (offer == null)
        {
            SendPlayerChatMessage(player, "Nobody has offered you a chalk save, or the offer ran out.");
            return;
        }

        string name = offer.Name;
        bool renamed = args.Length > 0;
        if (renamed && !TryName(args[0], out name))
        {
            SendPlayerChatMessage(player, "Usage: !chalkaccept [new name]. Names can use letters, numbers, - and _.");
            return;
        }

        lock (fileLock)
        {
            string dir = FolderFor(id);
            Directory.CreateDirectory(dir);

            if (Directory.GetFiles(dir, "*.json").Length >= maxSaves)
            {
                SendPlayerChatMessage(player, $"You already have {maxSaves} saves, delete one first with !chalkdelete <name>.");
                return;
            }

            // never replace one of your own saves by accident: with no new name given, a free one is picked
            if (File.Exists(Path.Combine(dir, name + ".json")))
            {
                if (renamed)
                {
                    SendPlayerChatMessage(player, $"You already have a save called \"{name}\", pick another name.");
                    return;
                }

                string baseName = name.Length > MaxNameLength - 4 ? name.Substring(0, MaxNameLength - 4) : name;
                for (int i = 2; i < 1000; i++)
                {
                    name = $"{baseName}-{i}";
                    if (!File.Exists(Path.Combine(dir, name + ".json")))
                        break;
                }
            }

            WriteSave(Path.Combine(dir, name + ".json"), new SaveFile
            {
                Owner = id,
                OwnerName = player.Username,
                Saved = DateTime.UtcNow.ToString("o"),
                Cells = offer.Save.Cells,
            });
            offers.Remove(id);
        }

        Log($"{player.Username} accepted \"{offer.Name}\" from {offer.FromName} as \"{name}\".");
        SendPlayerChatMessage(player, $"Saved a copy from {offer.FromName} as \"{name}\". Use !chalkload {name} <0-3> to put it on a canvas.");
    }

    private void DeclineOffer(WFPlayer player, string[] args)
    {
        bool had;
        lock (fileLock)
            had = offers.Remove(player.SteamId.m_SteamID);

        SendPlayerChatMessage(player, had ? "Offer declined." : "Nobody has offered you a chalk save.");
    }

    private void ShowOwners(WFPlayer player, string[] args)
    {
        if (!IsPlayerAdmin(player))
            return;
        ulong id = player.SteamId.m_SteamID;
        lock (fileLock)
        {
            if (args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
                adminViews.Remove(id);
            else if (!adminViews.ContainsKey(id))
                adminViews[id] = 0;
        }
    }

    private void BrowseOwner(WFPlayer player, string[] args)
    {
        if (!IsPlayerAdmin(player))
            return;
        ulong owner = 0;
        if (args.Length > 0 && !ulong.TryParse(args[0], out owner))
            return;
        lock (fileLock)
            adminViews[player.SteamId.m_SteamID] = owner;
    }

    // every player who has a folder of saves:  id~name~count;id~name~count  (most recently saved first)
    private string OwnersText()
    {
        var online = new Dictionary<ulong, string>();
        foreach (WFPlayer p in GetAllPlayers())
            online[p.SteamId.m_SteamID] = p.Username;
        var entries = new List<(ulong Id, string Name, int Count, DateTime Last)>();
        foreach (string dir in Directory.GetDirectories(folder))
        {
            if (!ulong.TryParse(Path.GetFileName(dir), out ulong id))
                continue; // _backups
            string[] files = Directory.GetFiles(dir, "*.json");
            if (files.Length == 0)
                continue;
            if (!online.TryGetValue(id, out string? name))
                name = OwnerName(id, files);
            DateTime last = files.Max(f => File.GetLastWriteTimeUtc(f));
            entries.Add((id, name, files.Length, last));
        }

        var shown = entries.OrderByDescending(e => e.Last).Take(MaxOwnersListed);
        return string.Join(";", shown.Select(e => $"{e.Id}~{CleanName(e.Name)}~{e.Count}"));
    }

    // the name in a save file, which is near its start, so a big save is never read whole
    private string OwnerName(ulong id, string[] files)
    {
        if (ownerNames.TryGetValue(id, out string? known))
            return known;
        string name = id.ToString();
        try
        {
            using var stream = File.OpenRead(files[0]);
            var buffer = new byte[512];
            int read = stream.Read(buffer, 0, buffer.Length);
            Match m = Regex.Match(Encoding.UTF8.GetString(buffer, 0, read), "\"ownerName\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success)
                name = JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\"") ?? name;
        }
        catch
        {
            // keep the Steam ID
        }
        ownerNames[id] = name;
        return name;
    }

    private static string CleanName(string text) => text.Replace('~', ' ').Replace(';', ' ').Replace(',', ' ');

    // an online player by name, fisher ID or Steam ID
    private WFPlayer? FindOnline(string ident)
    {
        WFPlayer? found = ParentServer.GetPlayer(ident);
        if (found != null)
            return found;

        return ulong.TryParse(ident, out ulong steamId)
            ? GetAllPlayers().FirstOrDefault(p => p.SteamId.m_SteamID == steamId)
            : null;
    }

    // ---- canvases ----

    private ChalkCanvas GetOrCreateCanvas(int id)
    {
        lock (ParentServer.stateLock)
        {
            ChalkCanvas? canvas = ParentServer.chalkCanvas.FirstOrDefault(c => c.canvasID == id);
            if (canvas == null)
            {
                canvas = new ChalkCanvas(id, ParentServer);
                ParentServer.chalkCanvas.Add(canvas);
            }
            return canvas;
        }
    }

    private List<Cell> Snapshot(int id)
    {
        var cells = new List<Cell>();
        ChalkCanvas? canvas;
        lock (ParentServer.stateLock)
            canvas = ParentServer.chalkCanvas.FirstOrDefault(c => c.canvasID == id);
        if (canvas == null)
            return cells;

        lock (canvas.chalkImage)
        {
            foreach (var entry in canvas.chalkImage)
            {
                // erased cells stay in the server's list with this colour, there is nothing to keep
                if (entry.Value == (long)COLOR.NONE)
                    continue;

                float x = entry.Key.x;
                float y = entry.Key.y;
                if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate)
                    continue;

                cells.Add(new Cell((int)MathF.Truncate(x), (int)MathF.Truncate(y), entry.Value));
            }
        }
        return cells;
    }

    // ---- files ----

    // one folder per player, named by Steam ID
    private string FolderFor(ulong owner) => Path.Combine(folder, owner.ToString());

    private void WriteSave(string path, SaveFile save)
    {
        var json = new StringBuilder();
        json.Append("{\"version\":1,\"owner\":\"").Append(save.Owner).Append("\",\"ownerName\":")
            .Append(JsonSerializer.Serialize(save.OwnerName)).Append(",\"saved\":\"").Append(save.Saved).Append("\",\"cells\":[");
        for (int i = 0; i < save.Cells.Count; i++)
        {
            Cell c = save.Cells[i];
            if (i > 0)
                json.Append(',');
            json.Append('[').Append(c.X).Append(',').Append(c.Y).Append(',').Append(c.Color).Append(']');
        }
        json.Append("]}");

        string temp = path + ".tmp";
        File.WriteAllText(temp, json.ToString());
        File.Move(temp, path, overwrite: true);
    }

    private SaveFile? Read(string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            var save = new SaveFile();

            if (root.TryGetProperty("owner", out JsonElement owner) && ulong.TryParse(owner.GetString(), out ulong ownerId))
                save.Owner = ownerId;
            if (root.TryGetProperty("ownerName", out JsonElement ownerName))
                save.OwnerName = ownerName.GetString() ?? "";
            if (root.TryGetProperty("saved", out JsonElement saved))
                save.Saved = saved.GetString() ?? "";

            if (!root.TryGetProperty("cells", out JsonElement cells) || cells.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement cell in cells.EnumerateArray())
            {
                if (save.Cells.Count >= MaxCells)
                    break;
                if (cell.ValueKind != JsonValueKind.Array || cell.GetArrayLength() != 3)
                    continue;

                if (!cell[0].TryGetInt32(out int x) || !cell[1].TryGetInt32(out int y) || !cell[2].TryGetInt64(out long color))
                    continue;
                if (Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate || color < 0 || color > MaxColor)
                    continue;

                save.Cells.Add(new Cell(x, y, color));
            }
            return save;
        }
        catch (Exception e)
        {
            Log($"Could not read {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    private void PruneBackups()
    {
        try
        {
            foreach (FileInfo old in new DirectoryInfo(backupFolder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxBackups))
                old.Delete();
        }
        catch (Exception e)
        {
            Log($"Could not clean up the old backups: {e.Message}");
        }
    }

    // ---- input ----

    // names become file names, so only a safe set of characters is allowed
    private static bool TryName(string raw, out string name)
    {
        name = raw.Trim().ToLowerInvariant();
        if (name.Length == 0 || name.Length > MaxNameLength || name[0] == '_' || name[0] == '-')
            return false;
        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
                return false;
        }
        return true;
    }

    private static bool TryCanvas(string raw, out int id)
    {
        return int.TryParse(raw, out id) && id >= 0 && id < CanvasCount;
    }

    // ---- settings ----

    private void ReadSettings()
    {
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
                case "saveaccess":
                    saveAdminsOnly = value.Equals("admins", StringComparison.OrdinalIgnoreCase);
                    break;
                case "loadaccess":
                    loadAdminsOnly = value.Equals("admins", StringComparison.OrdinalIgnoreCase);
                    break;
                case "maxsavesperplayer":
                    if (int.TryParse(value, out int saves) && saves > 0)
                        maxSaves = Math.Min(saves, 1000);
                    break;
                case "loadcooldown":
                    if (int.TryParse(value, out int cooldown) && cooldown >= 0)
                        loadCooldownSeconds = Math.Min(cooldown, 3600);
                    break;
            }
        }
    }

    private void EnsureSettingsFileExists()
    {
        if (File.Exists(settingsPath))
            return;

        string[] lines =
        {
            "# ======================================================================",
            "#  Chalk Saves",
            "# ======================================================================",
            "#",
            "#  Players save a chalk canvas under a name and put it back later:",
            "#    !chalksave <name> <0-3>     saves that canvas",
            "#    !chalkload <name> <0-3>     replaces that canvas with a save",
            "#    !chalklist                  lists your saves",
            "#    !chalkdelete <name>         deletes one of your saves",
            "#    !chalkshare <name> <player> offers a copy of a save to a player who is online",
            "#    !chalkaccept [new name]     takes the copy that was offered to you",
            "#    !chalkdecline               turns the offer down",
            "#",
            "#  The Wolfpark HUD mod shows all of this in its Chalk menu. Admins also see",
            "#  every player's saves there (it uses !chalkowners and !chalkbrowse).",
            "#",
            "#  A shared save is only an offer, it lands in the other player's folder when",
            "#  they type !chalkaccept (within 2 minutes), so nobody can fill your saves.",
            "#  It counts toward their limit and is never saved over one of their own.",
            "#",
            "#  Every player has their own folder of saves, so they only see their own",
            "#  and two players can use the same name. Admins can add a player's name",
            "#  (while online) or Steam ID to the end of any of these to use that",
            "#  player's saves, like  !chalklist 76561190000000000",
            "#",
            "#  Saves are files in ChalkSaves/<Steam ID> next to the server. Before a",
            "#  canvas is replaced, what was on it is kept in ChalkSaves/_backups",
            "#  (the newest 20). To undo a load, copy that file into your own folder",
            "#  under ChalkSaves/<Steam ID>, rename it to something like undo.json",
            "#  and load it.",
            "#",
            "#  Changes here are read when the plugin starts, use !reloadplugins chalksaves",
            "#  after editing.",
            "#",
            "#  saveAccess    = everyone or admins. Who can save and replace their own saves.",
            "#  loadAccess    = everyone or admins. Who can put a save on a canvas.",
            "#                  Loading wipes whatever is drawn there, so admins may be safer.",
            "#  maxSavesPerPlayer = how many saves each player can keep, default 20.",
            "#  loadCooldown  = seconds a player must wait between loads, default 30.",
            "#                  Admins are not limited.",
            "# ======================================================================",
            "",
            "saveAccess = everyone",
            "loadAccess = everyone",
            "maxSavesPerPlayer = 20",
            "loadCooldown = 30",
            "",
        };

        File.WriteAllText(settingsPath, string.Join(Environment.NewLine, lines));
        Log($"Created {SettingsFileName}.");
    }
}
