# HP Share

Slay the Spire 2 multiplayer mod for a shared player HP pool and shared Block pool.

Target game build: `v0.111.0` (2026-08-13).

## Rules

- Shared player HP and maximum HP are the sums of the hidden per-player contribution ledgers.
- Shared Block is the sum of each player's Block contribution.
- Buffs and debuffs remain personal.
- Block granted to another player is credited to that recipient.
- Effects referring to “your Block” use only that player's Block contribution.
- Percentage healing uses the shared maximum HP; fixed healing is unchanged.
- Osty's visible HP is shared, while Osty card effects use the owner's contribution.
- Enemy attack damage is multiplied by the configurable coefficient (default `1.10`). An all-player attack therefore naturally deals roughly `players × 1.10` of its single-player total.

The contribution labels beside Block and Osty HP have localized Japanese/English hover explanations. The multiplayer roster keeps character/name/hand/energy UI but hides its redundant HP/Block strip.

## Build

The project has no package dependencies and references the DLLs already installed with the game. Build with:

```powershell
dotnet restore --configfile NuGet.Config
dotnet build --no-restore -c Release
```

After building, copy the generated `dist/HPShare` folder into the game's local mod directory.
