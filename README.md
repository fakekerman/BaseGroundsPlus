WARNİNG!!!!

This project was made almost entirely by claude and gemini.I am not a coder nor a developer im a man with some ideas.So don't expect anything professional from this mod and if you are not okay with this please don't download nor comment.I made this for my playtrough at first but then realised there may be people that could use it so im uploading these here.

# BaseGrounds+
A KSP 1.12 mod that stops landed vessels and bases from jumping, sinking into the ground or getting thrown into the sky when they load. It also lets you lock a base to the ground with one click.

Works on every body with a surface: stock, Kopernicus planet packs and **Real Solar System / RO / RP-1**.

**Requires:** ModuleManager and Harmony (`000_Harmony`, also needed by KSPCommunityFixes).

## Why landed vessels get thrown around

1. When a landed vessel loads (scene change, switching vessels, or another vessel coming within about 200 m), KSP waits a **fixed 75 frames** before turning physics on.
2. The terrain under the vessel is built progressively. On large terrain (especially RSS) it is often not finished after 75 frames, so parts end up *inside* the ground.
3. When physics turns on, PhysX pushes those parts out of the ground with no speed limit. That is the "kraken" that throws bases into the sky.
4. KSP's own re-positioning uses the smooth analytic terrain height instead of the real ground mesh and can tilt the vessel.

## What the mod does

For every protected landed vessel (all landed vessels by default):

| Stage | What happens |
|---|---|
| **Hold** | The vessel stays on rails until the terrain colliders under it have stopped changing. For the vessel you are flying, it also waits for full terrain detail. Max wait is 20 s (40 s with RSS). |
| **Release** | Every part is tested against the real ground (`Physics.ComputePenetration`). If anything is inside the terrain, a building or a Kerbal Konstructs static, the vessel is lifted out before physics starts. |
| **Settle** (4 s) | No crash damage, wheels can't break, PhysX may only push parts out of the ground slowly, and speed relative to the ground is capped. |
| **Guard** | A resting vessel you aren't flying that suddenly gets a kraken impulse is stopped. Vessels that are already moving (falling stages, debris, kOS rovers) are left to normal physics. If the ground under a resting vessel is rebuilt at higher detail, it is lifted out gently. |

## Using it

Click the **anchor button on the stock toolbar**. The window opens next to it (flight, Space Center and Tracking Station). Every option has a short explanation in the window.

- **This is a base**: Base icon on the map, plus your base settings (locks it to the ground by default).
- **Lock to the ground**: pins the vessel to its exact spot (stored in double precision, so it holds on Earth-sized bodies). Tick it in the air and it locks once the vessel has landed and stopped. If another mod or the cheat menu moves it on purpose, it re-locks where it comes to rest instead of being dragged back.
- **Freeze when I'm not flying it**: stays on rails while you fly something else, so nothing can hit it. It wakes up while your kerbal is next to it on EVA. You can't dock to a frozen vessel.
- **Kraken protection**: the hold / release / settle / guard steps above. On by default.
- **Quick fixes**: *Lift out of ground*, *Stabilize*, *Re-lock here*.
- **My bases**: all marked vessels, sorted by planet, with *Freeze all* / *Unfreeze all*.
- **Settings**: on/off switches and a few sliders. More options are in `GameData/BaseGroundsPlus/Settings.cfg`.

## Compatibility

- **Breaking Ground**: robotics and deployed science work.
- **Launch clamps / vessels on the pad**: left alone.
- **Making History**: its Desert/Woomerang mobile launch pads teleport vessels parked on them; a locked vessel re-locks where it comes to rest. Don't park bases on those pads.
- **RSS / RP-1 / Kopernicus**: longer hold limit via `Patches/PlanetPacks.cfg`.
- **RSS ground positioner**: runs after the hold, on final terrain; locked bases are restored to their exact locked pose afterwards.
- **KSPCommunityFixes, Kerbal Joint Reinforcement**: complementary.
- **ParkingBrake, VesselMover**: detected; the ground lock pauses while they are in control.
- **Kerbal Konstructs / KSC statics**: count as ground.
- **PhysicsRangeExtender, World Stabilizer**: not compatible (they do overlapping jobs).
- Bases deliberately sunk into the terrain will be lifted out on load.

## Extra fixes included

- **Realism Overhaul + ReStock LT-2 landing strut** (`Patches/Fix_RO_ReStock_LT2.cfg`): RO's KerbalFoundries patch uses transform names from an older ReStock model, which makes KerbalFoundries throw an error every frame. The patch corrects the names only when the old ones are present. Delete the file to undo it.

## Install / uninstall

Unzip the release into your KSP folder so you get `GameData/BaseGroundsPlus`.

To uninstall, delete `GameData/BaseGroundsPlus`. Saves stay valid. Vessels marked as bases keep the *Base* vessel type; change it back with the stock rename dialog.

## Building

`build.ps1` compiles with the C# compiler that ships with Windows (no SDK needed). Pass `-KspPath` to point it at your KSP install, and `-Install` to copy the result into it.

| File | Purpose |
|---|---|
| `Source/GroundGuard.cs` | per-vessel hold / release / settle / guard / lock / freeze logic |
| `Source/GroundUtil.cs` | terrain raycasts and penetration-based lift |
| `Source/BaseRegistry.cs` | saved per-vessel choices, follows parts through docking |
| `Source/BaseGroundsUI.cs` | toolbar button and window |
| `Source/PartMenuCacheFix.cs`, `Source/PartMenuGuard.cs` | right-click menu fixes |
| `Source/Core.cs`, `Source/Settings.cs`, `Source/Compat.cs` | events, settings, mod detection |

Turn on *Detailed log* in Settings to write every hold, release and lift to `KSP.log` (lines start with `[BaseGrounds+]`).

## Credits and license

Fake_kerman (ideas, design and testing); built with the AI assistant Claude (Anthropic).

Licensed under the **GNU General Public License v3.0**; see [LICENSE](LICENSE).
