# Tail Size Fix

When a player grows or shrinks, everything about them changes size except their tail. Tiny players end up with a huge tail and big players with a tiny one.

This mod makes every tail grow and shrink with its player.

- Fixes every player you can see, whether or not they have the mod.
- Works with any size, including sizes set by other mods or by servers.
- Client side only: nothing is sent to other players, and it works on any server.
- The tail still swings and wags exactly as before.

## Why it happens

The game's tail script detaches the swinging part of the tail from the body so it can sway freely. That also cuts it off from the player's size, so it stays at the size it had when the player spawned. This mod copies the player's size back onto it every frame.
