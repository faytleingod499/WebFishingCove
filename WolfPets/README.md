# Wolf Pets

A little wolf that follows you around in WEBFISHING. It walks after you, waits beside you and sits
down when you stand still for a while. Works on any server, not just Wolf Park.

## Install

Needs [GDWeave](https://github.com/NotNite/GDWeave). Copy the `WolfPets` folder (with
`manifest.json` and `WolfPets.pck`) into `WEBFISHING/GDWeave/mods/`.

## Using it

- A small **paw button** sits on the left of the screen. Drag it anywhere, it remembers where.
- Click it to open the pet menu:
  - **Show my wolf** turns your wolf on or off.
  - **Grey / White / Black** are ready-made looks.
  - Pick your own colour for the fur, back, belly & paws, eyes, nose & mouth, and ears & tongue.
- Your choice is saved and shared through the Steam lobby, so other players with this mod see your
  wolf in your colours. Players without the mod do not see pets.

## Building

`source/mods/WolfPets/` holds the script and the wolf scene. `model/wolf.glb` is the Blender export,
`model/convert.gd` turns an imported copy of it into `wolf_pet.scn` (Godot 3.5). `pack.gd` packs the
`.pck` with Godot 3.5's own packer.
