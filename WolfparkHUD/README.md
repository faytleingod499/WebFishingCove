# Wolfpark HUD

A client mod (for [GDWeave](https://thunderstore.io/c/webfishing/p/NotNet/GDWeave/)) that adds a small panel with
your wolf coin balance, a **Feed Wolves** button, a **Buy** menu (everything the server sells) and a bet box with a **Gamble** button.

On such a server anything you type that starts with `!` is sent to the server only and does not show in chat, and the buttons do the same. The heading (default "Ether's Wolf Park", cyan) comes from `hudTitle` / `hudTitleColor` in `feed.txt`. Server messages (chat lines with no player name) are shown in sky blue; change or disable it with `hudChatColor` in `feed.txt`. A small clipboard icon next to the heading (tooltip "Leaderboard") runs `!wolves`.

It only appears on servers that run the Cove `Feed` plugin from this repository (the plugin marks the server through
the Steam lobby data). On every other server the mod stays hidden and does nothing.

## Install (players)
1. Install GDWeave if you do not have it yet.
2. Copy the `WolfparkHUD` folder from `package/` into `WEBFISHING/GDWeave/mods/`.
   It must contain `manifest.json` and `WolfparkHUD.pck`.
3. Start the game and join the server. The panel appears at the top left. Drag the `::` handle to move it, the
   position is remembered.

## Server
Replace `Cove.Feed.dll` in `plugins/` with the new build and run `!reloadplugins feed` (or restart). Nothing to
configure; what the Buy menu lists comes from `shop.txt` (created next to `feed.txt`), the heading and gambling odds from `feed.txt`.

## How it works
The Feed plugin writes `wp_hud=1`, `wp_meteor_cost` and `wp_shop` (what the Buy menu lists), `wp_gamble` (bet limits) and one `wp_p<SteamID>` = `coins,feeds[,roll,multiplier,change]` entry per online player
into the lobby data. The mod reads its own entry once or twice a second. The buttons type `!feed` / `!meteor` for
you through the game's own chat, so the server needs no extra packet.

## Rebuilding the .pck
`source/mods/WolfparkHUD/main.gd` is the whole mod. To rebuild the pack, run `godot --no-window -s pack.gd`
with Godot 3.5 from this folder.

## Titles

The mod adds a title, **Lonely**, to the game's title list (Cosmetics, Misc tab) while you are on the server. The game itself never sees it as your equipped title (players without the mod would see you as a default character), so it stays your real title for them. Players with the mod see "Lonely" under your name, passed along through the Steam lobby. Pick any other title to switch it off.
