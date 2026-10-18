# Wolf Pets

A little wolf that follows you around in WEBFISHING. It walks after you, waits beside you and sits
down when you stand still for a while. Works on any server, not just Wolf Park.

## Install

Needs [GDWeave](https://github.com/NotNite/GDWeave). Copy the `WolfPets` folder (with
`manifest.json` and `WolfPets.pck`) into `WEBFISHING/GDWeave/mods/`.

## Using it

- Press **P** (or click the paw button) to open the pet menu.
- A small **paw button** sits on the left of the screen. Drag it anywhere, it remembers where.
- Click it to open the pet menu:
  - **Show my wolf** turns your wolf on or off.
  - **Size** makes your wolf anywhere from 50% to 150% of its normal size (others see it too).
  - **Grey / White / Black** are ready-made looks.
  - Pick your own colour for the fur, back, belly & paws, eyes, nose & mouth, and ears & tongue.
  - Type a name and press **Save** to keep the current colours as a palette (up to 12). Click a saved
    palette to put it back on, or **x** to delete it. Saving under the same name updates it.
  - The name of the palette your wolf wears floats above it, so saving or picking a palette called
    "Bluey" names your wolf Bluey. The Grey / White / Black looks have no name.
- Your choice is saved and shared through the Steam lobby, so other players with this mod see your
  wolf in your colours. Players without the mod do not see pets.

## Petting

Walk up to any wolf (yours or someone else's) and press the game's interact key when it shows
**Pet wolf**. The wolf comes to stand in front of you, wags its tail, and you lean in and stroke its
head. The pose fits your size: the wolf raises its head for a big player and lowers it, or lies down,
for a small one, and you lean forward, straighten up or reach up as needed. Moving or sitting stops
it. Other players with the mod see the petting too.

Petting makes it give a happy whine (the game's own dog whine, pitched up). When you bark or meow it
answers with a short howl, nose to the sky. Bigger wolves sound deeper, smaller ones higher.
`model/make_howl.py` makes the howl sound.

When you sit down, your wolf walks to your side and lies down beside you, facing the same way.

## Building

`source/mods/WolfPets/` holds the script and the wolf scene. `model/wolf.glb` is the Blender export,
`model/convert.gd` turns an imported copy of it into `wolf_pet.scn` (Godot 3.5). `pack.gd` packs the
`.pck` with Godot 3.5's own packer.
