# Wolfpark HUD

A client mod (for [GDWeave](https://thunderstore.io/c/webfishing/p/NotNet/GDWeave/)) that adds a small panel with
your wolf coin balance and **Feed Wolves** / **Meteor** buttons.

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
configure; the coin price of the meteor shown on the button is the `meteorCost` from `feed.txt`.

## How it works
The Feed plugin writes `wp_hud=1`, `wp_meteor_cost` and one `wp_p<SteamID>` = `coins,feeds` entry per online player
into the lobby data. The mod reads its own entry once or twice a second. The buttons type `!feed` / `!meteor` for
you through the game's own chat, so the server needs no extra packet.

## Rebuilding the .pck
`source/mods/WolfparkHUD/main.gd` is the whole mod. To rebuild the pack, run `godot --no-window -s pack.gd`
with Godot 3.5 from this folder.
