# EtherPets

A little pet, a **wolf**, a **cat** or a **raccoon**, that follows you around in WEBFISHING. It walks after you
(keeping up at any speed), waits beside you and sits down when you stand still for a while. Works on
any server.

## Install

Needs [GDWeave](https://github.com/NotNite/GDWeave). Copy the `EtherPets` folder (with
`manifest.json` and `EtherPets.pck`) into `WEBFISHING/GDWeave/mods/`. If you had the older
**WolfPets** mod, delete its folder; your settings carry over.

## Using it

- Press **P** or click the **paw button** (drag it anywhere, it remembers where) for the pet menu:
  - The **wolf**, **cat** and **raccoon** face buttons choose your pet.
  - **Show my pet**, **Answer when I bark** (a wolf howls, a cat meows, a raccoon chitters) and **Wait**, how long it
    waits before answering again (15 seconds to 10 minutes, 2 minutes to start with).
  - **Size** from 50% to 150%.
  - Ready-made looks for each kind, and a colour picker for the fur, back, belly & paws, eyes,
    nose & mouth, and ears & tongue.
  - Type a name and press **Save** to keep a palette (up to 12; it remembers the kind of pet too).
    The palette your pet wears is its name, shown above it.
- Everything is shared through the Steam lobby, so other players with the mod see your pet as you
  made it. Players without the mod do not see pets.

## Petting

Walk up to any pet and press the game's interact key at **Pet <name>**. It comes to stand in front of
you and you lean in and stroke its head, sized to fit you (it lowers or raises its head, or lies down
for a very small player). A wolf gives a happy whine, a cat purrs, a raccoon trills.

When you sit down, a wolf lies down beside you, a cat hops into your lap and curls up, and a
raccoon climbs up and flops on your head. A raccoon doesn't walk: it curls into a ball and rolls
after you.

## Building

`source/mods/EtherPets/` holds the script, the pet scenes and the howl. `model/pet_*.glb` are the
Blender exports, `model/convert.gd` turns an imported one into a pet scene (Godot 3.5),
`model/make_howl.py` and `model/make_chitter.py` make the howl and the chitter, and `pack.gd` packs the `.pck`.
