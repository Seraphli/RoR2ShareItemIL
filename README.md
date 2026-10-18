# RoR2ShareItemMod

Gives a copy of each picked up item to **every** player.

- Only shares non-lunar and non-void items.
- You can get items even after you die.
- **Only the host needs this.**

---

## Status

This project was archived because the manual [dnSpy](https://github.com/dnSpyEx/dnSpy) workflow
stopped working in game version `1.4.0#840` — dnSpy could no longer recompile
`RoR2.dll` even with no edits at all.

That limitation is specific to dnSpy's *decompile → edit C# → recompile* round trip.
Rewriting the IL directly does not need a recompile, so the same feature is
implemented here as an **automated IL patch** using [Mono.Cecil](https://github.com/jbevain/cecil).

| | |
|---|---|
| Verified against | Risk of Rain 2 `1.5.0` (Unity `2021.3.45f2`) |
| Target assembly | `Risk of Rain 2_Data/Managed/RoR2.dll` |
| Output | a **single** patched `RoR2.dll` |

---

## Install

```bash
# 1. build (auto-detects the Steam install)
./build.sh

# 2. copy the result over the original
cp "$GAME_DIR/RoR2.dll" "$GAME_DIR/RoR2.dll.bak"   # backup once
cp out/RoR2.dll "$GAME_DIR/RoR2.dll"
```

or let the script do both:

```bash
./build.sh --install
```

If the game is installed somewhere unusual:

```bash
GAME_DIR="/path/to/Risk of Rain 2_Data/Managed" ./build.sh
```

Steam's "verify integrity of game files" will restore the original `RoR2.dll`,
undoing the patch.

---

## Why not BepInEx + Harmony?

Installing any BepInEx mod changes how the game presents you to other players:

1. **Matchmaking is separated by a mod hash.**
   `RoR2.NetworkModCompatibilityHelper` computes a `networkModHash` from the loaded mod list
   and the server browser filters on it
   (`SteamworksServerRemoteGameProvider::BuildFilter`, `SteamworksLobbyRemoteGameProvider::BuildFilter`,
   `EOSLobbyRemoteGameProvider::BuildFilter`). Nothing inside `RoR2.dll` populates that list —
   it is filled in by an external assembly (the BepInEx pack) — so once it is non-empty you only
   see other modded lobbies.
2. **`RoR2Application.isModded` gets turned on** when `maxPlayers != 4` (or when Unity's DRM
   genuine check fails), which adds a `"mod"` server tag and disables Prismatic Trials
   (`RoR2Application::Awake`, `ServerManagerBase::UpdateTags`, `DisableIfGameModded::OnEnable`).

Patching `RoR2.dll` directly avoids both: the mod list stays empty and `maxPlayers` is untouched,
so the game still matches against vanilla players.

---

## How it works

The original game code in `RoR2.ItemDef.AttemptGrant` grants a picked-up item to the picker only:

```csharp
body.inventory.GiveItemPermanent(itemIndex, 1);
```

The patcher replaces that call with a call to a share routine:

```csharp
ShareItemPatch.Patcher.Grant(pickerInventory, itemIndex);
```

which grants the item to every `PlayerCharacterMasterController.instances` entry, except for
Lunar / Void tiers which are still granted to the picker only (same rule as the original mod).
See [`src/ShareItemPatch/Patcher.cs`](src/ShareItemPatch/Patcher.cs).

The share routine is written in plain C#, so the compiler type-checks it against the game's
public API. The patcher then:

1. injects the compiled `ShareItemPatch.Patcher` type into `RoR2.dll`, and
2. rewrites the call site inside `AttemptGrant`,

so the released artifact is a **single `RoR2.dll`** with no extra assembly to install.

It also runs an IL stack verifier on both the rewritten method and the injected method, and
refuses to write the output if either is malformed.

### Layout

```
src/ShareItemPatch/     payload (C#, compiled against RoR2.dll)
src/ShareItemPatcher/   Cecil patcher (type injection + call-site rewrite)
build.sh                one-command build
out/RoR2.dll            the patched, shippable DLL
```

---

## Adapting to a new game version

After a game update, re-run `./build.sh`. Two things can break:

1. **The payload does not compile** — a public API it uses was renamed or changed
   (`ItemCatalog.GetItemDef`, `ItemDef.tier`, `Inventory.GiveItemPermanent`,
   `PlayerCharacterMasterController.instances`, `CharacterMaster.inventory`).
   The compiler will point at the exact line to fix.

2. **The injection point moved** — the patcher stops with:

   ```
   ERROR: Could not find 'Inventory.GiveItemPermanent(ItemIndex, Int32)' inside
          System.Void RoR2.ItemDef::AttemptGrant(RoR2.PickupDef/GrantContext&)
   ```

   In that case, inspect the new method and find where the "normal, permanent" item grant
   happens. Any IL dumper works; no Windows or dnSpy needed. Then adjust the locator in
   `src/ShareItemPatcher/Program.cs` (`Patch`, step 2).

In `1.5.0` the call site looks like this — the patcher expects a `ldloc` of the `RoR2.ItemIndex`
local followed by `ldc.i4.1`:

```
0152: ldarg.0
0153: ldfld   RoR2.CharacterBody RoR2.PickupDef/GrantContext::body
0158: callvirt RoR2.Inventory RoR2.CharacterBody::get_inventory()
015D: ldloc.0            // itemIndex
015E: ldc.i4.1
015F: callvirt System.Void RoR2.Inventory::GiveItemPermanent(RoR2.ItemIndex,System.Int32)
```

---

## Requirements

- .NET SDK (the patcher targets `net10.0`; change the `TargetFramework` in
  `src/ShareItemPatcher/ShareItemPatcher.csproj` if you use an older SDK)
- Linux / macOS (bash). On Windows, run the three commands in `build.sh` manually.

## Thanks

Thanks to [SharedItemPickup](https://github.com/Solst1c3/SharedItemPickup) created by Solst1c3.
