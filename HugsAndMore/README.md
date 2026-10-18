# Hugs and More

Emotes for two players who both have the mod. The first one: **a hug**.

Walk up to another player who has Hugs and More and press the game's interact key at **Hug <name>**. They get
an invitation, "<name> wants a hug!", and hug back with **Y** or the **Hug** button (or "No thanks").
You both turn to each other, step in and put your arms around each other, sway a little, and let go after
a few seconds, or as soon as either of you walks away. It's sized to fit both of you: a small player hugs a
big one around the middle, a big one hugs a small one around the shoulders.

Everyone else with Hugs and More sees the hug too. Players without it see nothing unusual. Works on any server.

## Install
Needs [GDWeave](https://thunderstore.io/c/webfishing/p/NotNet/GDWeave/). Copy the `HugsAndMore` folder from
`package/` (with `manifest.json` and `HugsAndMore.pck`) into `WEBFISHING/GDWeave/mods/`.

## How it works
- Each player writes their state into the Steam lobby member data `hugsandmore`:
  `1;<seq>;<ask|yes>;<emote>;<target steam id>;<the seq it answers>`, empty when idle, and
  `hugsandmore_v` = the mod version once per lobby so others know to offer the prompt.
- A hug is on while one player asks and the other says yes to that same invitation (or both ask each other).
  Every client then draws it with bone overrides on both players: the body (pelvis) is turned and stepped
  in, the arms reach around with a two-bone reach, the heads tip to opposite sides. Nobody's real position
  changes.

## Building
`source/mods/HugsAndMore/` holds the scripts; `pack.gd` packs the `.pck` with Godot 3.5:
`godot --no-window -s pack.gd`.
