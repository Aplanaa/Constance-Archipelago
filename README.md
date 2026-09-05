# Constance Archipelago Randomizer

An Archipelago for Constance.

It randomizes the main items of Constance into a multiworld, which you can share with many other people. The randomizer includes randomization for the abilities, the main items, filler, traps, and in game icons to track all the checks.

## What does the randomizer have?

- Many randomized items: abilities, chests, heart pieces, flasks, health vials, erasers, and boss checks.
- Different traps to get in the multiworld
- It includes an in game tracker with information and logic
- Warp between the different shrines after getting them
- Death link between players
- Almost all of these changes can be modified in yaml.

## Requirements

- Constance on Steam
- The Archipelago launcher already set up

## Installation

### Step 1: Install the Constance apworld

Just double-click `constance.apworld`. If Archipelago is installed, this installs it automatically.

If that doesn't work: open the Archipelago Launcher, click "Install APWorld", and select the file.

Once installed, open the Archipelago Launcher and check the list of clients, you should now see "Constance Client" in there. If you see it, this step worked.

### Step 2: Install BepInEx into the game

Find your Constance install folder. In Steam: right-click Constance → Manage → Browse local files. This opens something like: `...\steamapps\common\Constance\`

Download BepInEx 5.4.23 (the x64 build for Windows) from https://github.com/BepInEx/BepInEx/releases, look for a file named like `BepInEx_win_x64_5.4.23.x.zip`

Extract the contents of that zip directly into your Constance folder, so you end up with these sitting next to `CONSTANCE.exe`:
- `BepInEx\` (folder)
- `Winhttp.dll`
- `doorstop_config.ini`

Launch Constance once, then close it (The game may crash one or twice before opening). This lets BepInEx create the rest of its folder structure, you should now see `BepInEx\plugins\` exists.

### Step 3: Install the ConstanceAP mod

Open `ConstanceAP_mod.zip`.

Inside, there's a `BepInEx` folder. Copy that folder's contents into your Constance install folder, merging with the `BepInEx` folder already there from Step 2.

When done, this exact file should exist: `...\Constance\BepInEx\plugins\ConstanceAP\ConstanceAP.dll`

### Step 4: Multiworld Setup

If you already know this you can skip it.

To generate a multiworld you will need all the yaml you want to include. To generate a yaml go to the Archipelago Launcher → Tools → Options Creator. If you have the apworld installed then the Constance options will appear, here is a short description of each of the options:

- **RandomizeFlasks**: Randomizes the 12 Flasks in the multiworld.
- **RandomizeHearts**: Randomizes the 9 Hearts in the multiworld.
- **RandomizeErasers**: Randomizes the 5 Erasers in the multiworld.
- **RandomizeBosses**: Gives each boss a check.
- **RandomizeAbilities**: Randomizes each Ability in the multiworld, Paintbrush and Paint Dive will never be randomized because of their importance, Paint Stab can be adjusted in another option.
- **RandomizePaintStab**: Randomizes Paint Stab in the multiworld. I recommend disabling this if you are playing with other people. If it gets placed deep into another player world you may not have checks for a long time.
- **RandomizeTears**: Randomizes the 4 Tears in the multiworld
- **RandomizeInspirations**: Randomizes the 16 inspirations in the multiworld
- **RandomizeHealthVials**: Randomizes the 2 health vial purchases in the multiworld
- **FillerMultiplier**: Multiplies the amount of filler given. In x1 the default is: Handful of Glimmer: 20 to 100 and Handful of Lightstones: 15 to 40
- **NoAbilitiesInShops**: If enabled, none of the abilities will ever be placed at one of the shop-purchase locations. Avoids a glimmer grind bottleneck early on if an essential ability ends up gated behind a purchase you can't yet afford. I recommend enabling this option.
- **TrapPercentage**: What percentage of filler items (the ones that would otherwise be Handful of Glimmer/Lightstones) are replaced with traps instead.
- **OneHitTrapWeight**: Relative weight for the "1 Hit Constance" trap (sets current health to 1)
- **CheckpointTrapWeight**: Relative weight for the Checkpoint Trap (respawns the player at their last visited shrine)
- **PerseveranceTrapWeight**: Relative weight for the Perseverance Trap (applies the Perseverance effect the real game gives for choosing to Persevere at the death screen)

When you have the yaml, place it in the players folder and execute the ArchipelagoGenerate.exe. It will give you a file in the output folder, you can use this file in the archipelago website which will give you the address you will use.

### Step 5: Launch the Constance Client

Open the Archipelago Launcher.

Find "Constance Client" in the list and click it. A window should open.

In that window, enter the server address and connect. It will ask for your slot name if it isn't remembered automatically. You should see it say "Connected" somewhere in the window.

Leave this window open, it needs to stay running the whole time you play.

### Step 6: Launch Constance and start a NEW file

Launch Constance normally through Steam.

**Important:** you must start a brand new save file, not an existing one. The mod permanently links each save file to the seed it's first loaded with, and an old save with existing progress will be blocked from connecting to a new seed.

Play normally. Checks should report automatically, and items sent to you should apply automatically, as long as the Constance Client window (Step 5) stays open and connected.

## Troubleshooting

- **"Constance Client" doesn't appear in the Launcher** → the apworld isn't installed correctly. Redo Step 2, and confirm `constance.apworld` actually landed in the Archipelago install's `custom_worlds` folder (default: `%programdata%\Archipelago\custom_worlds`, or wherever you installed Archipelago).
- **Game seems to work, but nothing is happening (no items, no checks)** → make sure the Constance Client window from Step 5 is open and shows "Connected", and that BepInEx actually loaded the mod. To check the latter: after launching the game once, look for a file at `...\Constance\BepInEx\LogOutput.log` and confirm it mentions Constance Archipelago Client loading near the top.
- **A banner appears in-game saying to pick a different file** → this means the save file you loaded doesn't belong to the currently connected seed. Start a genuinely new save file while connected to the correct seed.

## AI Disclosure

I want to be clear, AI has been used to create this Archipelago. This just started as a project for personal use and grew much more than I expected, and I thought that someone else might also enjoy it. I did the full logic, design and debugging of the Archipelago, but I did not directly write the code. I totally understand that for some people this is something they don't want to use, and I respect it. This still took many hours so I hope you can give it a try and enjoy it, thank you!

## Feedback and bug reports

Please report any bugs in logic or gameplay that you may encounter in the Issues tab, with enough information to replicate it, if you can, attach the file at `...\Constance\BepInEx\LogOutput.log`. Please expect some level of error in this Archipelago.
