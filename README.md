# Constance Archipelago Randomizer

An [Archipelago](https://archipelago.gg/) multiworld randomizer integration for **Constance**, the 2025 Unity/Mono metroidvania.

This repository contains the two source components that make the integration work together:

- **`apworld/`** — the Archipelago world implementation (generation logic, item/location tables, options, and the live client that talks to the game).
- **`mod/`** — the BepInEx/Harmony mod that hooks into Constance itself, applies received items, reports checks, and bridges to the Python client over a local TCP connection.

## Repository structure

```
apworld/constance/          Python world source
    client/                 The live Archipelago client (runs from inside the installed apworld)
mod/ConstanceAP/            C# mod source (BepInEx plugin)
```

Compiled build artifacts (`constance.apworld`, `ConstanceAP.dll`) are **not** committed here — see [Releases](../../releases) for ready-to-install builds, or build from source below.

## Installing a release (recommended for players)

Grab the latest `constance.apworld` and `ConstanceAP_mod.zip` from the [Releases page](../../releases) and follow the install guide included there. In short:

1. Install [Archipelago](https://github.com/ArchipelagoMW/Archipelago/releases).
2. Install `constance.apworld` (double-click it, or use the Archipelago Launcher's "Install APWorld" button).
3. Install [BepInEx 5.4.23](https://github.com/BepInEx/BepInEx/releases) (the Mono/x64 build) into your Constance install folder.
4. Extract `ConstanceAP_mod.zip`'s `BepInEx` folder into your Constance install folder, merging with the one BepInEx just created.
5. Launch the "Constance Client" from the Archipelago Launcher, connect to your room, then launch Constance and start a **new** save file.

## Building from source

### The apworld

The `apworld/constance` folder *is* the world source. To package it as an installable `.apworld`:

```bash
cd apworld
zip -r constance.apworld constance -x "*.pyc" -x "__pycache__/*" -x "*/__pycache__/*"
```

### The mod

The mod is a BepInEx 5 plugin targeting Constance's Mono runtime. You'll need:

- The C# compiler (`mcs`, via Mono) or Visual Studio/Rider with a .NET Framework-compatible toolchain.
- Constance's own `Managed` folder (`<Constance install>/CONSTANCE_Data/Managed/`) as reference assemblies.
- BepInEx's `core` folder (`BepInEx.dll`, `0Harmony.dll`) as reference assemblies.

Example build command (Linux/Mono, adjust paths to your own setup):

```bash
cd mod/ConstanceAP
mcs -noconfig -nostdlib -target:library -out:ConstanceAP.dll \
  -r:"$MANAGED/mscorlib.dll" -r:"$MANAGED/System.dll" -r:"$MANAGED/System.Core.dll" \
  -r:"$MANAGED/netstandard.dll" -r:"$MANAGED/UnityEngine.dll" -r:"$MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED/UnityEngine.IMGUIModule.dll" -r:"$MANAGED/UnityEngine.InputLegacyModule.dll" \
  -r:"$MANAGED/UnityEngine.UI.dll" -r:"$MANAGED/UnityEngine.UIModule.dll" \
  -r:"$MANAGED/UnityEngine.ImageConversionModule.dll" -r:"$MANAGED/UnityEngine.TextRenderingModule.dll" \
  -r:"$MANAGED/Unity.InputSystem.dll" -r:"$MANAGED/Newtonsoft.Json.dll" -r:"$MANAGED/Leo.Scripts.dll" \
  -r:"$MANAGED/LDtkUnity.Runtime.dll" -r:"$MANAGED/Constance.Core.dll" -r:"$MANAGED/Constance.Entities.dll" \
  -r:"$MANAGED/Constance.Characters.Player.dll" -r:"$MANAGED/Constance.World.dll" -r:"$MANAGED/Constance.Ui.dll" \
  -r:"$BEPINEX/BepInEx.dll" -r:"$BEPINEX/0Harmony.dll" \
  -resource:ap_check_icon.png,ap_check_icon.png -resource:shrine_icon.png,shrine_icon.png \
  Plugin.cs ConBridgeServer.cs HarmonyPatches.cs MapIconInjector.cs PositionBasedItemTracker.cs
```

Where `$MANAGED` points to Constance's `Managed` folder and `$BEPINEX` to BepInEx's `core` folder.

## How it works, briefly

- The mod suppresses Constance's own vanilla item/check grants via Harmony patches, and instead reports each check to the Python client over a local TCP bridge (`127.0.0.1:24242`).
- The Python client (bundled inside the apworld, launched via the Archipelago Launcher) talks to the Archipelago server, sends checks, and relays received items back to the mod to apply.
- A save file is permanently linked to whichever Archipelago seed it's first started under, to prevent a save from silently drifting onto the wrong seed.

## License

_Add your preferred license here (e.g. MIT) before publishing._
