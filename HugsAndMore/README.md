# Hugs and More

Emotes for two players who both have the mod: **hug**, **high five** and **kiss**, with more to come.

*This mod was created with the assistance of AI.*

Walk up to another player who has Hugs and More: a small **F  Interact** tag (in the Wolfpark HUD's glass
style) floats above them. Press **F** to open a small menu and pick an emote. They get an invitation,
"<name> wants a hug!", and join in with **Y** or the button (or "No thanks").

- **Hug**: you turn to each other, step in and put your arms around each other, sway a little with hearts floating up, and let go
  after a few seconds, or as soon as either of you walks away. A small player hugs a big one around the
  middle, a big one hugs a small one around the shoulders.
- **High five**: you face each other an arm's length apart and slap your right hands together between you,
  no higher than the shorter one can reach.
- **Kiss**: you lean in to each other (the taller one leans down further), pucker up, and the game's kiss
  sound and a puff of its hearts play.

Everyone else with the mod sees it too. Players without it see nothing unusual. Works on any server.

## Install
Needs [GDWeave](https://thunderstore.io/c/webfishing/p/NotNet/GDWeave/). Copy the `HugsAndMore` folder from
`package/` (with `manifest.json` and `HugsAndMore.pck`) into `WEBFISHING/GDWeave/mods/`.

## How it works
- No game interactable is used (its bubble is big); the mod draws its own small prompt over the nearest
  player with the mod within 2 m, and F opens the picker (the game ignores its keys while it is open).
- Each player writes their state into the Steam lobby member data `hugsandmore`:
  `1;<seq>;<ask|yes>;<emote>;<target steam id>;<the seq it answers>`, empty when idle, and
  `hugsandmore_v` = the mod version once per lobby so others know to offer the prompt.
- A hug is on while one player asks and the other says yes to that same invitation (or both ask each other).
  Every client then draws it with bone overrides on both players: the body (pelvis) is turned and stepped
  into place, the arms reach with a two-bone reach. Nobody's real position changes. The high five plays the
  game's punch sound, pitched up, at the slap.
- Adding an emote: an entry in `EMOTES` / `EMOTE_ORDER` and a `_pose_<name>` function.

## Building
`source/mods/HugsAndMore/` holds the scripts; `pack.gd` packs the `.pck` with Godot 3.5:
`godot --no-window -s pack.gd`.
