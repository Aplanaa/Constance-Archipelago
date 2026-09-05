"""
Constance Archipelago LIVE Client
===================================

Talks to the ConstanceAP BepInEx mod over a local TCP socket
(127.0.0.1:24242) instead of watching the save file. This means:
  - Checks are reported the instant they happen in-game (no save needed)
  - Received items apply instantly, live, no reload needed
  - The mod itself draws a small in-game log overlay

The Archipelago network protocol handling here is copied from the already
fully-tested ConstanceClient.py (same CommonClient-based approach, same
item/location tables) -- only the "how do we know what happened in-game"
mechanism changes, from save-file polling to a live socket.

Protocol with the mod (newline-delimited JSON, one line per message):
  mod -> client:  {"type":"check","id":"ps_..."}
  client -> mod:  {"type":"apply","id":"ps_..."}
  mod -> client:  {"type":"death"}
  client -> mod:  {"type":"kill_player"}

HONEST STATUS: the C# mod side compiles cleanly against the real game and
BepInEx assemblies (see the mod's own README), but has not been loaded
into a running Constance process. This Python side reuses the exact
network/protocol code already validated end-to-end against a real local
Archipelago server in ConstanceClient.py -- only the bridge_watcher
function below is new and specifically untested against a live mod.
"""

from __future__ import annotations

import asyncio
import copy
import hashlib
import json
import os
import socket
import time
from typing import Optional

import ModuleUpdate
ModuleUpdate.update()

import Utils

if __name__ == "__main__":
    Utils.init_logging("ConstanceLiveClient", exception_logger="Client")

from NetUtils import ClientStatus, JSONtoTextParser
from CommonClient import (
    gui_enabled, logger, get_base_parser,
    CommonContext, server_loop,
)
from Utils import async_start

BRIDGE_HOST = "127.0.0.1"
BRIDGE_PORT = 24242

# "Aplana found their Handful of Glimmer (75) (Vanishing
# Vaults - Chest 4)" -- the granted quantity has to appear inside the
# normal AP found/received message itself, not as a separate log line.
#
# The amount used to be rolled purely mod-side (System.Random at grant
# time), which made it impossible to ever print here: the message is
# built when the item is FOUND, potentially long before the mod applies
# it, and for a resend/resync the mod would have rolled a different
# number anyway. So the roll moved here instead, made DETERMINISTIC --
# derived by hashing (seed, item name, location, finding player).
#
# Being deterministic wasn't
# enough on its own -- this was still being CALLED separately from two
# different places (once to decide what to send the mod, again
# independently to build the announcement text), and two independent
# calls are still two things that can end up disagreeing, whatever the
# reason on a given day. Now called exactly once, as early as possible
# (on_package's "ReceivedItems" handling, synchronously, the moment raw
# item data arrives from the server), with the result cached in
# pending_filler_amounts -- every other place that needs this amount
# reads that one cached value back instead of ever calling this function
# a second time for the same item.
#
# These ranges must stay in sync with Plugin.cs's own
# Min/MaxGlimmerAmount and Min/MaxLightstoneAmount constants -- the mod
# still falls back to rolling in-range itself if an older client sends
# no amount at all.
FILLER_AMOUNT_RANGES: dict[str, tuple[int, int]] = {
    "Handful of Glimmer": (20, 100),
    "Handful of Lightstones": (15, 40),
}


def resolve_item_name(ctx: "ConstanceLiveContext", item_id: int, player: int) -> str:
    """lookup_in_slot(item_id, player), but safe for the two real sentinel
    cases AP's own server uses when there's no genuine "finding player":
    precollected items (NetworkItem(item_code, -2, 0)) and /send'd items
    (NetworkItem(item_code, -1, 0)) BOTH carry player=0 -- not a real
    slot (slots start at 1), so looking it up directly threw/returned
    "Unknown item" and got silently skipped by the safety check meant for
    genuine data-package mismatches (see on_package's "ReceivedItems"
    handling). Real, confirmed bug found by real testing: this silently
    broke BOTH precollected Map Icons AND every single /send'd item,
    since both hit this exact same slot=0 case. 0 (or any player not in
    slot_info at all) falls back to this player's own slot instead --
    correct for both cases, since a precollected item is unconditionally
    from this player's own world, and /send explicitly looks the item
    name up in the RECEIVING player's own game (MultiServer.py's
    _cmd_send_multiple: names = item_names_for_game(games[slot]), where
    slot is the receiving player, not source 0).
    """
    if player == 0 or player not in ctx.slot_info:
        player = ctx.slot
    return ctx.item_names.lookup_in_slot(item_id, player)


def filler_amount_for(seed_name: str, item_name: str, location: int, player: int, multiplier: int = 1) -> Optional[int]:
    """Deterministic per-instance quantity for a quantity-bearing filler item.

    Returns None for any item that doesn't grant a countable amount (traps,
    non-filler items), which is the signal not to annotate its message.

    Direct request: quantities now land in steps of 5 (25, 30, 35, ...)
    instead of any integer in range -- both FILLER_AMOUNT_RANGES bounds
    are already multiples of 5 and their spans divide evenly by 5, so
    this just picks which 5-sized step within the range rather than any
    individual integer.

    Direct request: multiplier scales the final, already-quantized result
    -- the underlying roll (which specific step within the base range)
    stays exactly the same regardless of multiplier, so changing this
    option doesn't change the RELATIVE spread of amounts a player sees,
    just the overall scale of all of them together.
    """
    bounds = FILLER_AMOUNT_RANGES.get(item_name)
    if bounds is None:
        return None
    low, high = bounds
    key = f"{seed_name or 'unknown_seed'}|{item_name}|{location}|{player}".encode("utf-8")
    digest = hashlib.sha256(key).digest()
    step_count = (high - low) // 5 + 1
    base_amount = low + 5 * (int.from_bytes(digest[:8], "big") % step_count)
    return base_amount * multiplier

# Generated directly from the apworld's own locations.py
# (ALL_LOCATIONS) -- a hand-maintained copy of this exact table had
# drifted out of sync before (missing a location count reduction), the
# same failure mode fixed by generating it directly instead.
LOCATION_PERSISTENCE_IDS: dict[str, str] = {
    'Ability Altar: Paintbrush': 'ps_unlock_Ability_Brush',
    'Ability Altar: Paint Dive': 'ps_unlock_Ability_Dash',
    'Ability Altar: Paint Stab': 'ps_unlock_Ability_Stab',
    'Ability Altar: Paint Stroke': 'ps_unlock_Ability_Slice',
    'Ability Altar: Plunge': 'ps_unlock_Ability_Pogo',
    'Ability Altar: Wall Dive': 'ps_unlock_Ability_WallDive',
    'Ability Altar: Somersault': 'ps_unlock_Ability_DoubleJump',
    'Ability Altar: Paint Clone': 'ps_unlock_Ability_BombClone',
    'Tear: Floral Foundry': 'ps_unlock_Tear_Foundry',
    'Tear: Astral Academy': 'ps_unlock_Tear_Academy',
    'Tear: Chaotic Carnival': 'ps_unlock_Tear_Carnival',
    'Tear: Vanishing Vaults': 'ps_unlock_Tear_Vaults',
    'Camera Pickup': 'ps_unlock_Camera',
    'Map Icons Pickup': 'ps_unlock_MapIcons',
    'Frida Mask Pickup': 'ps_unlock_FridaMask',
    'Health Vial Purchase': 'ps_shopItem_Potion1',
    'Health Vial Upgrade Purchase': 'ps_shopItem_Potion2',
    'Eraser: Janky Junction (Bubble Trouble)': 'ps_item_Eraser#instance2',
    'Eraser: Shop (Two cousins)': 'ps_item_Eraser#instance3',
    "Eraser: Chaotic Carnival (Jackie's Gacha)": 'ps_item_Eraser#instance4',
    'Eraser: Vanishing Vaults': 'ps_item_Eraser#instance5',
    'Heart Piece: Floral Foundry': 'ps_item_HeartPiece#instance1',
    "Heart Piece: Chaotic Carnival 2 (Jackie's Gacha)": 'ps_item_HeartPiece#instance3',
    'Heart Piece: Astral Academy 1': 'ps_item_HeartPiece#instance4',
    'Heart Piece: Janky Junction (Shop)': 'ps_item_HeartPiece#instance5',
    'Heart Piece: Vanishing Vaults 2': 'ps_item_HeartPiece#instance6',
    'Heart Piece: Astral Academy 2': 'ps_item_HeartPiece#instance7',
    'Heart Piece: Vanishing Vaults 1': 'ps_item_HeartPiece#instance8',
    'Heart Piece: Chaotic Carnival 1': 'ps_item_HeartPiece#instance9',
    'Vanishing Vaults - Chest 1': 'ps_Prod_V18_d336557d-21de-494b-9252-3249efd8a329',
    'Vanishing Vaults - Chest 2': 'ps_Prod_V25_85f4c4a9-2607-4e0b-a8cf-880b870b5547',
    'Vanishing Vaults - Chest 3': 'ps_Prod_V08_47add804-cdd9-43b4-8d80-eabcfc5787b8',
    'Vanishing Vaults - Chest 4': 'ps_Prod_V04_1323ea94-0f95-dc12-c4ce-691e45cfd61c',
    'Chaotic Carnival - Chest 1': 'ps_Prod_C90_3a191403-d505-9a9d-10ce-6be0167f13d2',
    'Chaotic Carnival - Chest 2': 'ps_Prod_C96_49252065-b059-02ba-e69e-edaa30f89d14',
    'Chaotic Carnival - Chest 3': 'ps_Prod_C93_0d28e8d6-a001-436e-9ec1-9c525217f192',
    'Chaotic Carnival - Chest 4': 'ps_Prod_C95_e7442277-a767-7428-c17d-05c9b94ec518',
    'Chaotic Carnival - Chest 5': 'ps_Prod_C94_1feb337b-de88-b0db-92e5-c948661aa51d',
    'Floral Foundry - Chest 1': 'ps_Prod_F21_6a1b3f09-be2b-4645-a74f-75174bace78d',
    'Floral Foundry - Chest 2': 'ps_Prod_F13_c5e9a808-8a12-5dbe-943b-9e50cadf6f18',
    'Floral Foundry - Chest 3': 'ps_Prod_F02_cc06e08a-23b9-4cc9-ab1e-e4795e51419e',
    'Floral Foundry - Chest 4': 'ps_Prod_F16_a046b9c0-14bd-4053-b0ad-cef476f54daa',
    'Floral Foundry - Chest 5': 'ps_Prod_F20_6713e14b-6a29-4871-8ac6-7f04b594bd57',
    'Floral Foundry - Chest 6': 'ps_Prod_F03_9c79f624-ecb2-4be2-a979-dae5fd90f875',
    'Floral Foundry - Chest 7': 'ps_Prod_F25_2a26ffc7-0a98-1ced-4f63-ac3ee2224f5c',
    'Painterly Peak - Chest 1': 'ps_Prod_P03_73cee3e6-0f17-4b15-817f-476c28071c80',
    'Janky Junction - Chest 1': 'ps_Prod_J07_a115468a-2a96-4b67-9ebd-37e0be846c63',
    'Janky Junction - Chest 2': 'ps_Prod_J19_dacbb823-357f-b229-30ec-3a5f372e265a',
    'Janky Junction - Chest 3': 'ps_Prod_J08_05ab593c-d503-41cf-b3eb-0eb89726594f',
    'Janky Junction - Chest 4': 'ps_Prod_J13_2176d0ba-6fff-4407-417a-ecbe13c97576',
    'Astral Academy - Chest 1': 'ps_Prod_A16_a8a6a17b-df53-495e-9c7a-8b9b828ccdc7',
    'Astral Academy - Chest 2': 'ps_Prod_A20_d51fea7c-0552-d01f-4c9c-439cafe52c8a',
    'Astral Academy - Chest 3': 'ps_Prod_A27_2d28c2b8-aeea-4fd1-b695-0faae8b91057',
    'Boss: Awe King': 'ps_Boss_AweKing_Beaten',
    'Boss: The Jester': 'ps_Boss_BossJoker_Beaten',
    'Boss: Brian': 'ps_Boss_BrainStoker_Beaten',
    'Boss: Chase Nemesis': 'ps_Boss_ChaseNemesis_Beaten',
    'Boss: Cornelis': 'ps_Boss_CornelisBoss_Beaten',
    'Boss: Jester, Encore': 'ps_Boss_JokerInvisible_Beaten',
    'Boss: The Manipulator': 'ps_Boss_JugglerBalloons_Beaten',
    'Boss: The Manipulator, Encore': 'ps_Boss_JugglerBalls_Beaten',
    'Boss: High Patia': 'ps_Boss_MothQueen_Beaten',
    'Boss: Palettus': 'ps_Boss_Palettus_Beaten',
    'Boss: Sir Barfalot': 'ps_Boss_PukeyBoy_Beaten',
    'Boss: Corrupted Mind': 'ps_Boss_PuppetHandCorruption_Beaten',
    'Boss: Wounded Vessel': 'ps_Boss_PuppetHandKungfu_Beaten',
    'Boss: Forsaken Will': 'ps_Boss_PuppetHandStrings_Beaten',
    'Boss: Constance': 'ps_Boss_PuppetMaster_Beaten',
    'Boss: Lord Korba': 'ps_Boss_SlimeNemesis_Beaten',
    'Boss: Cubicus': 'ps_Boss_Smasher_Beaten',
    'Inspiration: Copycat': 'ps_inspDrawing_Copycat',
    'Inspiration: Dark Embrace': 'ps_inspDrawing_DarkLightning',
    'Inspiration: Dark Revival': 'ps_inspDrawing_DarkRevival',
    'Inspiration: Milkshake Remix': 'ps_inspDrawing_MilkshakeRemix',
    'Inspiration: Precision Brush': 'ps_inspDrawing_NaturalTwenty',
    'Inspiration: Paint Overload': 'ps_inspDrawing_PaintOverload',
    'Inspiration: Ripple Effect': 'ps_inspDrawing_PogoWave',
    'Inspiration: Potent Potion': 'ps_inspDrawing_PotentPotion',
    'Inspiration: Extra Beat': 'ps_inspDrawing_PotionSynthesizer',
    'Inspiration: Saw Jump': 'ps_inspDrawing_ScrewJump',
    'Inspiration: Paint Conductor': 'ps_inspDrawing_SliceConductor',
    'Inspiration: Lifeline': 'ps_inspDrawing_StabLifesteal',
    'Inspiration: Master Spear': 'ps_inspDrawing_StabProjectile',
    'Inspiration: Warrior Pose': 'ps_inspDrawing_SturdyKnight',
    'Inspiration: Tax Refund': 'ps_inspDrawing_TaxRefund',
    'Inspiration: Whiplash': 'ps_inspDrawing_TeleportClone',
    'Paint Flask: Floral Foundry 1': 'ps_item_PaintPiece#instance1',
    'Paint Flask: Floral Foundry 2': 'ps_item_PaintPiece#instance2',
    'Paint Flask: Chaotic Carnival 1': 'ps_item_PaintPiece#instance4',
    'Paint Flask: Astral Academy 1': 'ps_item_PaintPiece#instance5',
    'Paint Flask: Janky Junction (Shop)': 'ps_item_PaintPiece#instance6',
    'Paint Flask: Vanishing Vaults 1': 'ps_item_PaintPiece#instance7',
    'Paint Flask: Vanishing Vaults 3': 'ps_item_PaintPiece#instance8',
    'Paint Flask: Vanishing Vaults 2': 'ps_item_PaintPiece#instance9',
    'Paint Flask: Astral Academy 2': 'ps_item_PaintPiece#instance10',
    "Paint Flask: Chaotic Carnival 2 (Jackie's Gacha)": 'ps_item_PaintPiece#instance11',
}

# Generated directly from the apworld's own items.py
# (ITEM_NAME_TO_PERSISTENCE_ID) -- a hand-maintained copy of this exact
# table had drifted out of sync (one entry pointed at the wrong
# persistence id entirely, from before a rename), the same failure mode
# already hit once with LOCATION_PERSISTENCE_IDS.
ITEM_PERSISTENCE_IDS: dict[str, str | None] = {
    'Paintbrush': 'ps_unlock_Ability_Brush',
    'Paint Dive': 'ps_unlock_Ability_Dash',
    'Paint Stab': 'ps_unlock_Ability_Stab',
    'Paint Stroke': 'ps_unlock_Ability_Slice',
    'Plunge': 'ps_unlock_Ability_Pogo',
    'Wall Dive': 'ps_unlock_Ability_WallDive',
    'Somersault': 'ps_unlock_Ability_DoubleJump',
    'Paint Clone': 'ps_unlock_Ability_BombClone',
    'Tear of Floral Foundry': 'ps_unlock_Tear_Foundry',
    'Tear of Astral Academy': 'ps_unlock_Tear_Academy',
    'Tear of Chaotic Carnival': 'ps_unlock_Tear_Carnival',
    'Tear of Vanishing Vaults': 'ps_unlock_Tear_Vaults',
    'Camera': 'ps_unlock_Camera',
    'Map Icons': 'ps_unlock_MapIcons',
    'Frida Mask': 'ps_unlock_FridaMask',
    'Health Vial': 'ps_shopItem_Potion1',
    'Health Vial Upgrade': 'ps_shopItem_Potion2',
    'Eraser': 'ps_ap_EraserGrant',
    'Handful of Glimmer': None,
    'Handful of Lightstones': None,
    '1 Hit Constance Trap': None,
    'Checkpoint Trap': None,
    'Perseverance Trap': None,
    'Paint Flask': 'ps_ap_PaintFlaskGrant',
    'Heart Piece': 'ps_ap_HeartPieceGrant',
    'Inspiration: Copycat': 'ps_inspDrawing_Copycat',
    'Inspiration: Dark Embrace': 'ps_inspDrawing_DarkLightning',
    'Inspiration: Dark Revival': 'ps_inspDrawing_DarkRevival',
    'Inspiration: Milkshake Remix': 'ps_inspDrawing_MilkshakeRemix',
    'Inspiration: Precision Brush': 'ps_inspDrawing_NaturalTwenty',
    'Inspiration: Paint Overload': 'ps_inspDrawing_PaintOverload',
    'Inspiration: Ripple Effect': 'ps_inspDrawing_PogoWave',
    'Inspiration: Potent Potion': 'ps_inspDrawing_PotentPotion',
    'Inspiration: Extra Beat': 'ps_inspDrawing_PotionSynthesizer',
    'Inspiration: Saw Jump': 'ps_inspDrawing_ScrewJump',
    'Inspiration: Paint Conductor': 'ps_inspDrawing_SliceConductor',
    'Inspiration: Lifeline': 'ps_inspDrawing_StabLifesteal',
    'Inspiration: Master Spear': 'ps_inspDrawing_StabProjectile',
    'Inspiration: Warrior Pose': 'ps_inspDrawing_SturdyKnight',
    'Inspiration: Tax Refund': 'ps_inspDrawing_TaxRefund',
    'Inspiration: Whiplash': 'ps_inspDrawing_TeleportClone',
}
PAINT_FLASK_GRANT_SENTINEL = "ps_ap_PaintFlaskGrant"
HEART_PIECE_GRANT_SENTINEL = "ps_ap_HeartPieceGrant"
ERASER_GRANT_SENTINEL = "ps_ap_EraserGrant"

PERSISTENCE_ID_TO_LOCATION_NAME = {pid: name for name, pid in LOCATION_PERSISTENCE_IDS.items()}

# Generated directly from the apworld's rules.py LOCATION_REQUIREMENTS,
# for computing live in-game logic state (reachable/not yet/hinted) for
# the map icon overlay. Paintbrush is implicit for every location except
# its own altar, matching rules.py's own base rule -- not repeated here.
# NOTE: 'Vanishing Vaults - Chest 3', 'Eraser: Shop (Two cousins)', and
# 'Heart Piece: Floral Foundry' are genuine exceptions -- real OR-logic
# rules that this AND-only dict cannot express. Handled as special cases
# directly in compute_location_states and their tooltip text, not here.
LOCATION_LOGIC_REQUIREMENTS: dict[str, list[str]] = {
    'Ability Altar: Paint Clone': ['Paint Stab', 'Paint Stroke'],
    'Ability Altar: Paint Stroke': ['Paint Stab'],
    'Ability Altar: Plunge': ['Paint Stab'],
    'Ability Altar: Somersault': ['Paint Stab', 'Plunge'],
    'Ability Altar: Wall Dive': ['Paint Stab', 'Paint Stroke'],
    'Astral Academy - Chest 1': ['Paint Stab', 'Paint Stroke'],
    'Astral Academy - Chest 2': ['Paint Stab', 'Paint Stroke'],
    'Astral Academy - Chest 3': ['Paint Stab', 'Paint Stroke'],
    'Boss: Awe King': ['Paint Stab', 'Paint Stroke'],
    'Boss: The Jester': ['Paint Stab'],
    'Boss: Brian': ['Paint Stab'],
    'Boss: Chase Nemesis': ['Paint Stab', 'Paint Stroke'],
    'Boss: Cornelis': ['Paint Stab', 'Plunge'],
    'Boss: Jester, Encore': ['Paint Stab', 'Plunge'],
    'Boss: The Manipulator': ['Paint Stab', 'Plunge'],
    'Boss: The Manipulator, Encore': ['Paint Stab', 'Plunge'],
    'Boss: High Patia': ['Paint Stab', 'Paint Stroke'],
    'Boss: Palettus': ['Paint Stab', 'Plunge'],
    'Boss: Sir Barfalot': ['Paint Stab', 'Wall Dive'],
    'Boss: Corrupted Mind': ['Paint Stab', 'Paint Dive', 'Paint Stroke', 'Plunge', 'Wall Dive', 'Somersault', 'Frida Mask'],
    'Boss: Wounded Vessel': ['Paint Stab', 'Paint Dive', 'Paint Stroke', 'Plunge', 'Wall Dive', 'Somersault', 'Frida Mask'],
    'Boss: Forsaken Will': ['Paint Stab', 'Paint Dive', 'Paint Stroke', 'Plunge', 'Wall Dive', 'Somersault', 'Frida Mask'],
    'Boss: Constance': ['Paintbrush', 'Paint Stab', 'Paint Dive', 'Paint Stroke', 'Plunge', 'Wall Dive', 'Somersault', 'Frida Mask'],
    'Boss: Lord Korba': ['Paint Stab', 'Wall Dive'],
    'Boss: Cubicus': ['Paint Stab'],
    'Chaotic Carnival - Chest 1': ['Paint Stab', 'Plunge'],
    'Chaotic Carnival - Chest 2': ['Paint Stab', 'Plunge'],
    'Chaotic Carnival - Chest 3': ['Paint Stab', 'Plunge'],
    'Chaotic Carnival - Chest 4': ['Paint Stab', 'Paint Stroke', 'Wall Dive'],
    'Chaotic Carnival - Chest 5': ['Paint Stab', 'Plunge'],
    "Eraser: Chaotic Carnival (Jackie's Gacha)": ['Paint Dive', 'Paint Stab', 'Plunge'],
    'Eraser: Janky Junction (Bubble Trouble)': ['Paint Dive', 'Paint Stab', 'Paint Stroke', 'Wall Dive', 'Paint Clone'],
    'Eraser: Shop (Two cousins)': ['Paint Dive', 'Paint Stab'],
    'Eraser: Vanishing Vaults': ['Paint Dive', 'Paint Stab', 'Wall Dive', 'Paint Clone'],
    'Floral Foundry - Chest 1': ['Paint Stab'],
    'Floral Foundry - Chest 2': ['Paint Stab'],
    'Floral Foundry - Chest 3': ['Paint Stab'],
    'Floral Foundry - Chest 4': ['Paint Stab'],
    'Floral Foundry - Chest 5': ['Paint Stab', 'Somersault', 'Plunge'],
    'Floral Foundry - Chest 6': ['Paint Stab', 'Somersault'],
    'Floral Foundry - Chest 7': ['Paint Stab', 'Plunge'],
    'Frida Mask Pickup': ['Paint Stab', 'Wall Dive', 'Tear of Floral Foundry', 'Tear of Astral Academy', 'Tear of Chaotic Carnival', 'Tear of Vanishing Vaults'],
    'Health Vial Purchase': ['Paint Dive'],
    'Health Vial Upgrade Purchase': ['Paint Dive', 'Paint Stab', 'Paint Stroke', 'Wall Dive', 'Paint Clone'],
    'Heart Piece: Astral Academy 1': ['Paint Dive', 'Paint Stab', 'Paint Stroke'],
    'Heart Piece: Astral Academy 2': ['Paint Dive', 'Paint Stab', 'Paint Stroke', 'Wall Dive'],
    'Heart Piece: Chaotic Carnival 1': ['Paint Dive', 'Paint Stab', 'Plunge', 'Somersault'],
    "Heart Piece: Chaotic Carnival 2 (Jackie's Gacha)": ['Paint Dive', 'Paint Stab', 'Plunge'],
    'Heart Piece: Floral Foundry': ['Paint Dive'],
    'Heart Piece: Janky Junction (Shop)': ['Paint Dive', 'Paint Stab'],
    'Heart Piece: Vanishing Vaults 1': ['Paint Dive', 'Paint Stroke', 'Plunge'],
    'Heart Piece: Vanishing Vaults 2': ['Paint Dive', 'Paint Stab', 'Wall Dive'],
    'Inspiration: Copycat': ['Paint Stab', 'Paint Stroke'],
    'Inspiration: Dark Embrace': ['Paint Stab'],
    'Inspiration: Dark Revival': ['Paint Stab', 'Paint Stroke', 'Plunge', 'Wall Dive'],
    'Inspiration: Extra Beat': ['Paint Stab', 'Paint Stroke', 'Wall Dive', 'Paint Clone'],
    'Inspiration: Lifeline': ['Paint Stab'],
    'Inspiration: Master Spear': ['Paint Stab', 'Paint Stroke', 'Plunge', 'Paint Clone'],
    'Inspiration: Milkshake Remix': ['Paint Stab', 'Somersault', 'Plunge'],
    'Inspiration: Paint Conductor': ['Paint Stab', 'Plunge', 'Paint Stroke'],
    'Inspiration: Paint Overload': ['Paint Stab', 'Paint Stroke', 'Wall Dive'],
    'Inspiration: Potent Potion': ['Paint Stab', 'Paint Stroke', 'Paint Clone'],
    'Inspiration: Ripple Effect': ['Paint Stab', 'Plunge', 'Wall Dive'],
    'Inspiration: Saw Jump': ['Paint Stab', 'Somersault', 'Wall Dive', 'Plunge'],
    'Inspiration: Tax Refund': ['Paint Stab'],
    'Inspiration: Warrior Pose': ['Paint Stab', 'Paint Stroke'],
    'Inspiration: Whiplash': ['Paint Stab', 'Paint Stroke', 'Paint Clone', 'Wall Dive'],
    'Janky Junction - Chest 1': ['Paint Stab'],
    'Janky Junction - Chest 2': ['Paint Stab', 'Paint Stroke'],
    'Janky Junction - Chest 3': ['Paint Stab'],
    'Janky Junction - Chest 4': ['Paint Stab'],
    'Map Icons Pickup': ['Paint Dive'],
    'Paint Flask: Astral Academy 1': ['Paint Dive', 'Paint Stab', 'Paint Stroke'],
    'Paint Flask: Astral Academy 2': ['Paint Dive', 'Paint Stab', 'Paint Stroke', 'Wall Dive'],
    'Paint Flask: Chaotic Carnival 1': ['Paint Dive', 'Paint Stab', 'Plunge'],
    "Paint Flask: Chaotic Carnival 2 (Jackie's Gacha)": ['Paint Dive', 'Paint Stab', 'Plunge'],
    'Paint Flask: Floral Foundry 1': ['Paint Dive', 'Paint Stab'],
    'Paint Flask: Floral Foundry 2': ['Paint Dive', 'Paint Stab', 'Plunge'],
    'Paint Flask: Janky Junction (Shop)': ['Paint Dive'],
    'Paint Flask: Vanishing Vaults 1': ['Paint Dive', 'Paint Stab', 'Paint Stroke', 'Wall Dive'],
    'Paint Flask: Vanishing Vaults 2': ['Paint Dive', 'Paint Stab', 'Wall Dive', 'Somersault'],
    'Paint Flask: Vanishing Vaults 3': ['Paint Dive', 'Paint Stab', 'Wall Dive'],
    'Painterly Peak - Chest 1': ['Paint Stab', 'Plunge', 'Paint Stroke', 'Wall Dive'],
    'Tear: Astral Academy': ['Paint Stab', 'Paint Stroke'],
    'Tear: Chaotic Carnival': ['Paint Stab', 'Plunge'],
    'Tear: Floral Foundry': ['Paint Stab'],
    'Tear: Vanishing Vaults': ['Paint Stab', 'Wall Dive'],
    'Vanishing Vaults - Chest 1': ['Paint Stab', 'Wall Dive'],
    'Vanishing Vaults - Chest 2': ['Paint Stab', 'Wall Dive'],
    'Vanishing Vaults - Chest 3': [],  # unused directly -- special-cased below, kept empty here for clarity/correctness
}

FINAL_BOSS_ID = "ps_Boss_PuppetMaster_Beaten"

# Real in-game notification overlay, per direct request: reuses AP's own
# JSONtoTextParser (the same class that decides "self player = magenta,
# other player = yellow, item color by classification, location = green")
# rather than reimplementing that classification logic -- only the output
# format changes, from ANSI terminal codes to Unity rich-text <color> tags,
# using the exact same hex values JSONtoTextParser.color_codes already
# defines (the same ones the real AP GUI itself uses), so the in-game
# overlay's colors genuinely match what the Text Client shows.
class UnityRichTextParser(JSONtoTextParser):
    def _handle_color(self, node):
        codes = node["color"].split(";")
        hex_colors = [self.color_codes[code] for code in codes if code in self.color_codes]
        hex_color = hex_colors[0] if hex_colors else "FFFFFF"
        text = self._handle_text(node).replace("<", "&lt;").replace(">", "&gt;")
        return f"<color=#{hex_color}>{text}</color>"

# Real bug, found by real testing: ctx.applied_persistence_ids (what
# stops the same item being re-applied every cycle) only ever lived in
# memory. Restarting the CLIENT (not just the game) loses that memory
# entirely -- and since AP replays the player's full ReceivedItems history
# on reconnect, a fresh client naturally tries to re-apply *everything*
# again. For simple flag-based items this is harmless (the mod's own
# IsTruthy check no-ops it), but for repeatable items (Paint Flask, Heart
# Piece, Eraser) there's no such check, so a restart could genuinely
# re-grant them beyond what was ever actually received -- matching a real
# report of "all the checks are given again" after restarting. Persisting
# this set to a small local file, keyed by the room's seed name (so
# different multiworlds never share state), fixes this at the source.
# Uses AP's own standard persistent-user-data location (the same
# mechanism other clients use for their own settings/cache), not a path
# relative to this script's own file -- since once this runs as part of
# the packaged apworld/Launcher, __file__ would resolve somewhere inside
# the apworld's internal extracted cache, not a stable place for
# persistent state to survive apworld updates.
STATE_DIR = Utils.user_path("constance_ap_state")


def get_state_file_path(seed_name: str, slot_name: str) -> str:
    os.makedirs(STATE_DIR, exist_ok=True)
    safe_seed = "".join(c if c.isalnum() else "_" for c in (seed_name or "unknown_seed"))
    safe_slot = "".join(c if c.isalnum() else "_" for c in (slot_name or "unknown_slot"))
    return os.path.join(STATE_DIR, f"{safe_seed}_{safe_slot}.json")


def load_applied_state(path: str) -> set[str]:
    if not os.path.exists(path):
        return set()
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return set(data.get("applied_persistence_ids", []))
    except Exception:
        logger.exception(f"Could not load saved state from {path} -- starting fresh.")
        return set()


def save_applied_state(path: str, applied_ids: set[str]) -> None:
    try:
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"applied_persistence_ids": sorted(applied_ids)}, f)
    except Exception:
        logger.exception(f"Could not save state to {path}.")


class ConstanceLiveContext(CommonContext):
    game = "Constance"
    items_handling = 0b111

    def make_gui(self):
        # Window title. base_title is exactly the
        # documented override point for this (see CommonContext.make_gui's
        # own docstring) -- everything else about the GUI stays the
        # default kvui.GameManager.
        ui = super().make_gui()
        ui.base_title = "Constance Client"
        # Window/taskbar icon, same override pattern as base_title just
        # above -- GameManager's own __init__ sets self.icon to AP's
        # generic default (data/icon.png) before this runs, and Kivy only
        # actually reads it once the window itself opens, so overriding
        # it here still takes effect. icon.png ships as a sibling file
        # right next to this script inside the apworld's own client/
        # folder, so it's always found regardless of where the apworld
        # itself ends up installed.
        icon_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icon.png")
        if os.path.isfile(icon_path):
            ui.icon = icon_path
        return ui

    def __init__(self, server_address, password):
        # Real, subtle bug found by real testing: CommonContext.__init__
        # itself calls self.update_data_package(...) -- AP's own locally
        # bundled snapshot of every installed world's name->id mapping,
        # taken at import time -- which correctly triggers OUR overridden
        # update_game() for "Constance" DURING super().__init__() below,
        # before this constructor has otherwise run at all. Initializing
        # server_location_name_to_id AFTER the super() call (the natural
        # place to put it) meant this line unconditionally wiped out
        # whatever that early, valid call had just populated -- a classic
        # subclass pitfall: the parent constructor calls an overridable
        # method before the subclass's own initialization has completed.
        # Setting it here, BEFORE super().__init__() runs, means the
        # early update_game() call has something to correctly write into,
        # and nothing after it clobbers that value.
        self.server_location_name_to_id: dict[str, int] = {}
        # Live logic-state overlay on the map (reachable /
        # not yet / hinted). hinted_locations maps a location's numeric id
        # to the item name hinted there -- only ever populated for hints
        # targeting OUR OWN world (finding_player == our slot), since
        # that's the only hint data that's actually about revealing what's
        # at one of our own locations.
        self.hinted_locations: dict[int, str] = {}

        super().__init__(server_address, password)
        self.applied_persistence_ids: set[str] = set()
        self.death_link_enabled = False
        self.death_link_pending_kill = False
        self.state_file_path: Optional[str] = None
        self.reset_state = False
        self.unity_text_parser = UnityRichTextParser(self)
        self.pending_notifications: list[str] = []
        # Single source of truth for a filler
        # item's granted quantity, computed exactly once in on_package's
        # "ReceivedItems" handling, keyed by (item_name, location,
        # player) -- both the bridge_watcher send loop and on_print_json
        # read the SAME cached value back here rather than each computing
        # their own, independent (and previously, occasionally
        # disagreeing) copy.
        self.pending_filler_amounts: dict[tuple[str, int, int], int] = {}
        #         # captured directly from "RoomInfo" itself (see on_package) --
        # not relying on the base CommonContext class's own
        # self.server_seed_name, which a real test showed staying empty
        # despite the documented ordering checking out correctly.
        self._captured_seed_name: Optional[str] = None
        # Multiplies the amount granted by every
        # quantity-bearing filler item. Read from slot_data once
        # "Connected" arrives (see on_package below); 1 until then, which
        # matches the option's own default and is a safe no-op multiplier
        # for anything that fires before slot_data is available.
        self.filler_multiplier: int = 1
        # Dedup set so the "unresolvable item" warning
        # above logs once per item, not once per bridge_watcher tick
        # forever -- the underlying data-package mismatch isn't going to
        # fix itself mid-session, so repeating the warning every loop
        # iteration would just be log spam with no new information.
        self._warned_unresolvable_item_keys: set[str] = set()
        # Per-category randomize toggles, sent to the mod
        # once per bridge connection (not a repeating queue like the two
        # above -- a single settings snapshot, resent whenever the bridge
        # connects since the mod may restart/reconnect independently of
        # this client staying up).
        self.category_settings: dict[str, bool] = {}
        self.pending_settings_send = False
        # Real bug, found by real testing: send_msgs (in AP's own
        # CommonClient) silently no-ops if the AP SERVER connection isn't
        # fully established yet -- a completely separate connection from
        # the mod bridge, which can easily still be connecting/
        # authenticating when a check arrives very early in a session
        # (e.g. touching the Paintbrush altar immediately on load). The
        # old code marked a location "checked" locally BEFORE confirming
        # the send actually succeeded, so a check arriving during that
        # window was silently, permanently lost -- our own dedup then
        # refused to ever retry it, even though the server never
        # actually learned about it, explaining why no item ever came
        # back to apply. Queuing here and only marking checked once the
        # send actually goes out fixes this at the real source.
        self.pending_location_checks: list[str] = []  # location NAMES awaiting resolution+send
        self.queued_or_sent_location_names: set[str] = set()  # name-based dedup guard
        self.pending_goal_status = False

    def update_game(self, game_package: dict, game: str):
        if game == self.game:
            self.server_location_name_to_id = dict(game_package.get("location_name_to_id", {}))
        super().update_game(game_package, game)

    async def server_auth(self, password_requested: bool = False):
        if password_requested and not self.password:
            await super().server_auth(password_requested)
        await self.get_username()
        await self.send_connect()

    def save_state(self) -> None:
        if self.state_file_path:
            save_applied_state(self.state_file_path, self.applied_persistence_ids)

    def resync_non_repeatable_items(self) -> None:
        # Real bug, found by real testing: marking an item "applied"
        # happens the moment the write is QUEUED to the bridge socket,
        # not once the mod actually confirms receiving/processing it. If
        # that message is ever lost, the item stays permanently marked
        # "applied" locally -- persisted to disk too, so not even a
        # restart fixes it. Same bug class as the location-check one
        # already fixed. The mod's own grant logic is confirmed
        # idempotent for regular items (IsTruthy/_appliedItems checks),
        # so forgetting these specific "applied" records and letting them
        # resend is safe and self-healing, at the cost of a few harmless
        # redundant messages.
        #
        # Repeatable items (Paint Flask/Heart Piece/Eraser) and filler
        # (Handful of Glimmer/Lightstones) USED to be deliberately
        # excluded here, since their mod-side grants weren't idempotent
        # per-instance (they just increment a real counter by a random
        # amount) -- blindly resending could over-grant currency or
        # lightstones. Real correction: filler was excluded for the WRONG
        # reason at the time ("zero in-game state effect, purely
        # cosmetic") -- that's false, currency and lightstones are real,
        # persisted resources, so a lost filler grant was a genuinely
        # missing reward, not just a missing notification. Both are now
        # fixed at the mod side the same way: every repeatable/filler
        # "apply" message carries its own unique instance_key, and the
        # mod persists a marker per key so a resend is safely recognized
        # and skipped rather than re-applied. Nothing needs to stay
        # excluded from resync anymore.
        #
        # Called from TWO places, since real testing found either one
        # alone can miss the actual stale state: right when a fresh mod
        # bridge connection is established, AND right after loading saved
        # state from disk -- the mod bridge (a local connection) can
        # legitimately connect before the AP server's "Connected" packet
        # (which is what triggers loading the saved state) ever arrives,
        # meaning a resync tied only to the bridge connecting could fire
        # too early, before there was anything stale to actually find.
        before_count = len(self.applied_persistence_ids)
        cleared = before_count
        self.applied_persistence_ids = set()
        if cleared:
            logger.info(f"Resyncing {cleared} item(s) with the mod.")

    def server_ready(self) -> bool:
        # Real gap found: socket.open only confirms raw connectivity, not
        # full authentication. self.slot only gets set inside CommonClient's
        # own "Connected" packet handler -- checking it too means checks
        # can't be sent to a connection that's open but hasn't actually
        # finished the auth handshake yet (which the server would likely
        # just silently ignore anyway, since it wouldn't know which slot
        # the message was even for).
        return (
            self.server is not None
            and self.server.socket.open
            and not self.server.socket.closed
            and self.slot is not None
        )

    def _annotate_filler_quantity(self, args: dict) -> None:
        # Put the granted quantity INSIDE the normal AP
        # message rather than logging it separately, i.e.
        #   "Aplana found their Handful of Glimmer (75) (Vanishing Vaults - Chest 4)"
        #
        # Done by inserting one extra plain-text node immediately after
        # the item node in args["data"], BEFORE either parser runs. That
        # single insertion covers both outputs at once -- the console
        # line (produced by CommonClient's own super().on_print_json) and
        # the in-game overlay text (built from the same data below) --
        # instead of string-patching two separately-rendered results and
        # hoping they stay in sync.
        #
        # Deliberately limited to items being received by THIS slot: the
        # amount is only knowable for our own Constance items (see
        # filler_amount_for), and guessing at another game's item would
        # be inventing a number.
        if args.get("type") != "ItemSend":
            return
        if self.slot is None or args.get("receiving") != self.slot:
            return

        data = args.get("data")
        if not isinstance(data, list):
            return

        # Real bug: the finding player has to come from the LOCATION
        # node, not the item node. AP fills the item node's "player" with
        # the RECEIVING player and the location node's with the player
        # whose world the location sits in -- and it's that second one
        # that matches NetworkItem.player in items_received, which is what
        # bridge_watcher hashes when it sends the amount to the mod.
        # Taking it from the item node made the two hashes disagree
        # whenever finder and receiver differ, so the number announced in
        # the message was not the number granted in-game.
        item_index = None
        item_id = None
        location_id = None
        finding_player = None
        for index, node in enumerate(data):
            if not isinstance(node, dict):
                continue
            node_type = node.get("type")
            if node_type == "item_id" and item_index is None:
                item_index = index
                item_id = node.get("text")
            elif node_type == "location_id" and location_id is None:
                location_id = node.get("text")
                finding_player = node.get("player")

        if item_index is None or item_id is None:
            return

        try:
            item_id = int(item_id)
            location_id = int(location_id) if location_id is not None else 0
            finding_player = int(finding_player) if finding_player is not None else 0
        except (TypeError, ValueError):
            return

        item_name = resolve_item_name(self, item_id, finding_player)
        amount = self.pending_filler_amounts.get((item_name, location_id, finding_player))
        if amount is None:
            #             # the cache (populated in on_package's "ReceivedItems"
            # handling) is the preferred source, guaranteed consistent
            # with whatever amount was actually sent to the mod -- but
            # there's no hard guarantee "ReceivedItems" is always fully
            # processed before this "ItemSend" print_json message runs
            # for the same grant; if it isn't yet, the cache genuinely
            # has nothing there. filler_amount_for is pure/deterministic
            # given the same inputs, so falling back to computing it
            # directly here still produces the exact same number the
            # cache would have held -- guarantees a number is always
            # shown instead of silently doing nothing on a cache miss.
            amount = filler_amount_for(
                self._captured_seed_name or getattr(self, "server_seed_name", None),
                item_name, location_id, finding_player, multiplier=self.filler_multiplier)
        if amount is None:
            return

        data.insert(item_index + 1, {"type": "text", "text": f" ({amount})"})

    def on_print_json(self, args: dict):
        # Real in-game notification overlay: capture the same messages
        # the Text Client shows ("Aplana found their X (Y)"), rendered
        # with the same colors, for a small overlay in the actual game --
        # in addition to (not instead of) the normal console/log output.
        try:
            self._annotate_filler_quantity(args)
        except Exception:
            logger.exception("Failed to annotate a print_json message with its filler quantity.")

        try:
            # Real bug, found by real testing: JSONtoTextParser's node
            # handlers mutate each message node IN PLACE (e.g.
            # _handle_player_id replaces the numeric id with the player's
            # name string, right inside the same dict). A shallow copy
            # here left the underlying dicts shared with args["data"] --
            # so this call corrupted them before super().on_print_json()
            # ran its own parsers on the "same" data, which then tried
            # int("Aplana") and threw, propagating an exception out of
            # message processing and (very plausibly) causing the
            # observed disconnect/reconnect on every single item
            # received. A real, independent deep copy avoids this
            # entirely, matching the same defensive pattern the real
            # on_print_json already uses for its own parser calls.
            rich_text = self.unity_text_parser(copy.deepcopy(args["data"]))
            self.pending_notifications.append(rich_text)
        except Exception:
            logger.exception("Failed to build in-game notification text for a print_json message.")
        super().on_print_json(args)

    def on_deathlink(self, data: dict):
        # Called automatically by CommonClient when a linked death arrives.
        # Just flag it -- the actual "kill the player" bridge message is
        # sent from bridge_watcher's main loop, not from here, since this
        # runs as part of incoming-network-message handling, not
        # necessarily on a turn where the bridge socket is in a safe state
        # to write to.
        logger.info("DeathLink: received a linked death, will kill local player once mod bridge is available.")
        self.death_link_pending_kill = True
        super().on_deathlink(data)

    def on_package(self, cmd: str, args: dict):
        if cmd == "RoomInfo":
            # Relying on the base CommonContext class's
            # own self.server_seed_name (set from this same "RoomInfo"
            # packet, before "Connected" ever arrives, per
            # CommonClient.py's own process_server_cmd) still left
            # seed_id empty on a real test, despite that ordering
            # checking out exactly right against this project's own AP
            # checkout. Rather than keep trusting a base-class field
            # whose exact behavior in whatever AP client library version
            # is actually installed can't be fully verified from here,
            # this captures seed_name directly, itself, the moment this
            # exact packet arrives -- no dependency on the base class's
            # own attribute, its timing, or its exact name surviving
            # unchanged across versions.
            seed_name = args.get("seed_name")
            if seed_name:
                self._captured_seed_name = seed_name
                logger.info(f"RoomInfo: captured seed_name directly: {seed_name!r}")
            else:
                logger.warning(f"RoomInfo: package arrived with no usable seed_name field at all. Full args: {args!r}")

        if cmd == "Connected":
            slot_data = args.get("slot_data") or {}
            self.death_link_enabled = bool(slot_data.get("death_link", False))
            logger.info(f"DeathLink option for this slot: {self.death_link_enabled}")

            # The 5 per-category randomize toggles.
            # Stored here and sent to the mod once the bridge connects
            # (see bridge_watcher's own "Connected" handling below) --
            # the mod needs these to know which categories to actually
            # suppress vs leave completely vanilla.
            self.category_settings = {
                "randomize_flasks": bool(slot_data.get("randomize_flasks", True)),
                "randomize_hearts": bool(slot_data.get("randomize_hearts", True)),
                "randomize_erasers": bool(slot_data.get("randomize_erasers", True)),
                "randomize_bosses": bool(slot_data.get("randomize_bosses", True)),
                "randomize_abilities": bool(slot_data.get("randomize_abilities", True)),
                "randomize_tears": bool(slot_data.get("randomize_tears", True)),
                "randomize_inspirations": bool(slot_data.get("randomize_inspirations", True)),
                "randomize_health_vials": bool(slot_data.get("randomize_health_vials", True)),
            }
            logger.info(f"Category randomize settings: {self.category_settings}")
            self.pending_settings_send = True

            # Not part of category_settings/the mod's own
            # settings message at all -- filler_amount_for is purely
            # client-side, this client's own value is all that's ever
            # needed for it.
            self.filler_multiplier = int(slot_data.get("filler_multiplier", 1)) or 1
            logger.info(f"Filler amount multiplier: x{self.filler_multiplier}")

            #             # shrine warp links (which shrine icon on the map is enabled
            # for /warp) were persisting even across a brand new save file
            # and a different Archipelago seed entirely -- wrong, since
            # "has this shrine been visited" is playthrough-specific
            # progress, not fixed game geometry (unlike check_positions.
            # jsonl's recorded positions, which genuinely are the same
            # regardless of seed and are deliberately left alone here).
            #
            #             # this used to read args.get("seed_name") directly off the
            # "Connected" package -- but confirmed straight from AP's own
            # server source (MultiServer.py), the "Connected" packet never
            # contains a "seed_name" field at all; that field belongs to
            # the EARLIER "RoomInfo" packet instead, sent during the
            # initial handshake before "Connected" ever arrives.
            #
            #             # switching to self.server_seed_name (the base CommonContext
            # class's own field, populated from that same "RoomInfo"
            # packet before "Connected" ever arrives, per CommonClient.py's
            # own process_server_cmd -- an ordering guarantee verified
            # directly against this project's own AP checkout) STILL left
            # seed_id empty on a real, repeated test. Rather than keep
            # trusting exactly how/when a base-class field gets populated
            # in whatever AP client library version is actually installed
            # -- something that can't be fully verified from here --
            # _captured_seed_name is now captured directly, by this class
            # itself, the moment "RoomInfo" arrives (see on_package's own
            # handling of it above), with self.server_seed_name kept only
            # as a last-resort fallback in case that capture somehow
            # didn't happen for this specific connection.
            self.category_settings["seed_id"] = self._captured_seed_name or getattr(self, "server_seed_name", None) or ""
            if not self.category_settings["seed_id"]:
                logger.warning("Connected: seed_id is STILL empty after trying both _captured_seed_name and server_seed_name -- shrine persistence/save-seed-link will not work this session. This needs a fresh log with the 'RoomInfo:' line included to diagnose further.")

            # Real bug, found by real testing: this used to be applied
            # lazily inside bridge_watcher's loop, gated behind the mod
            # bridge connection succeeding first -- coupling two entirely
            # independent systems together. If the mod bridge was slow or
            # never connected, the DeathLink tag silently never got set,
            # meaning the server never knew to route incoming deaths to
            # us. Applying it here, immediately, as soon as we know the
            # slot's setting, decouples it correctly.
            async_start(self.update_death_link(self.death_link_enabled), name="ApplyDeathLinkTag")

            # Real bug, found by real testing: server_seed_name isn't
            # reliably present on every AP client version -- direct
            # attribute access threw AttributeError and dropped the
            # connection entirely. get_state_file_path already falls back
            # to "unknown_seed" for a None value, so getattr with a None
            # default degrades gracefully here regardless of which AP
            # version is actually running, instead of crashing outright.
            seed_name = getattr(self, "server_seed_name", None)
            self.state_file_path = get_state_file_path(seed_name, self.auth)
            if self.reset_state and os.path.exists(self.state_file_path):
                os.remove(self.state_file_path)
                logger.info(f"--reset-state: cleared {self.state_file_path}")
            loaded = load_applied_state(self.state_file_path)
            if loaded:
                self.applied_persistence_ids |= loaded
                logger.info(f"Loaded {len(loaded)} previously-applied item record(s) from {self.state_file_path}.")
            self.resync_non_repeatable_items()

            # Hover tooltip needs to know which locations
            # are hinted. _read_hints_{team}_{slot} is AP's own standard
            # DataStorage key for a player's full, current hint list
            # (confirmed via the real server source, MultiServer.py) --
            # explicitly requesting it here guarantees we get hints made
            # BEFORE this session started too, not just ones that happen
            # to arrive while connected.
            hint_key = f"_read_hints_{self.team}_{self.slot}"
            async_start(self.send_msgs([{"cmd": "Get", "keys": [hint_key]}]), name="RequestHints")

        elif cmd == "ReceivedItems":
            # Real, confirmed bug found by
            # real testing -- the displayed quantity ("found their Handful
            # of Glimmer (21)") and the amount actually granted in-game
            # could disagree, because they were computed via two SEPARATE
            # calls to filler_amount_for -- one here (well, previously in
            # bridge_watcher's send loop) when sending the amount to the
            # mod, another independently in on_print_json when building
            # the announcement text. Both calls were meant to be
            # deterministic and agree given identical inputs, but two
            # independent computations are still two things that can
            # silently drift apart from each other (subtle input
            # differences, timing, a future edit to one call site and not
            # the other) in a way a single, shared computation simply
            # cannot. Computed exactly ONCE now, as early as possible --
            # right here, synchronously, the moment the raw item data
            # arrives from the server, strictly before either
            # bridge_watcher's send loop or on_print_json's announcement
            # text could possibly run for it -- and cached in
            # pending_filler_amounts for both of them to read back
            # verbatim instead of recomputing anything.
            for raw_item in args.get("items", []):
                try:
                    item_id, location, player = raw_item[0], raw_item[1], raw_item[2]
                except (IndexError, TypeError):
                    continue
                item_name = resolve_item_name(self, item_id, player)
                amount = filler_amount_for(
                    self._captured_seed_name or getattr(self, "server_seed_name", None),
                    item_name, location, player, multiplier=self.filler_multiplier)
                if amount is None:
                    continue
                self.pending_filler_amounts[(item_name, location, player)] = amount

        elif cmd in ("Retrieved", "SetReply"):
            self._handle_hint_data_package(args)

    def _handle_hint_data_package(self, args: dict) -> None:
        hint_key = f"_read_hints_{self.team}_{self.slot}"
        if "keys" in args:  # Retrieved: {"keys": {key: value, ...}}
            value = args["keys"].get(hint_key)
        elif args.get("key") == hint_key:  # SetReply: {"key": ..., "value": ...}
            value = args.get("value")
        else:
            return

        if value is None:
            return

        try:
            new_hints: dict[int, str] = {}
            for hint in value:
                if hint.get("finding_player") != self.slot:
                    continue  # only hints about OUR OWN locations matter here
                location_id = hint.get("location")
                item_id = hint.get("item")
                if location_id is None or item_id is None:
                    continue
                item_name = self.item_names.lookup_in_slot(item_id)
                new_hints[location_id] = item_name
            self.hinted_locations = new_hints
            logger.info(f"Hint data updated: {len(new_hints)} hint(s) for this world.")
        except Exception:
            logger.exception("Error parsing hint data package.")


def extract_id_if_check(line: str) -> Optional[str]:
    if '"check"' not in line:
        return None
    return _extract_id(line)


def _extract_id(line: str) -> Optional[str]:
    key_index = line.find('"id"')
    if key_index < 0:
        return None
    colon_index = line.find(':', key_index)
    if colon_index < 0:
        return None
    first_quote = line.find('"', colon_index + 1)
    second_quote = line.find('"', first_quote + 1)
    if first_quote < 0 or second_quote < 0:
        return None
    return line[first_quote + 1:second_quote]


def extract_text_if_client_message(line: str) -> Optional[str]:
    # The mod's own progress messages (e.g. "Paint Flask
    # found (3/12)") -- previously only ever visible in the mod's own
    # in-game overlay, never in this client's own log, which is what was
    # actually asked for. Mirrors _extract_id's approach but for the
    # "text" field specifically, and has to handle escaped characters --
    # the mod's SendClientMessage escapes backslashes and quotes before
    # sending (ConBridgeServer.cs), so a naive find-the-next-quote would
    # truncate on the first escaped quote inside the text itself.
    if '"client_message"' not in line:
        return None
    key_index = line.find('"text"')
    if key_index < 0:
        return None
    colon_index = line.find(':', key_index)
    if colon_index < 0:
        return None
    first_quote = line.find('"', colon_index + 1)
    if first_quote < 0:
        return None
    i = first_quote + 1
    result_chars = []
    while i < len(line):
        ch = line[i]
        if ch == '\\' and i + 1 < len(line):
            result_chars.append(line[i + 1])
            i += 2
            continue
        if ch == '"':
            return ''.join(result_chars)
        result_chars.append(ch)
        i += 1
    return None


def compute_location_states(ctx: ConstanceLiveContext) -> list[dict]:
    # Live logic-state overlay on the map (normal /
    # greyed-out / outlined). Uses the SAME requirement data the apworld's
    # own rules.py enforces (embedded above as LOCATION_LOGIC_REQUIREMENTS,
    # generated directly from it) -- not a separate, hand-maintained guess
    # at the logic, so it can't drift out of sync with what the seed
    # actually requires.
    try:
        owned = {ctx.item_names.lookup_in_slot(i.item) for i in ctx.items_received}
    except Exception:
        return []

    has_paintbrush = "Paintbrush" in owned
    states = []
    for location_name, pid in LOCATION_PERSISTENCE_IDS.items():
        required = [] if location_name == "Ability Altar: Paintbrush" else LOCATION_LOGIC_REQUIREMENTS.get(location_name, [])
        if location_name == "Ability Altar: Paintbrush":
            reachable = True
        elif location_name == "Vanishing Vaults - Chest 3":
            # Genuine OR-logic exception -- (Wall Dive OR
            # Somersault), not the AND-only semantics every other entry
            # uses. LOCATION_LOGIC_REQUIREMENTS can't express this, so
            # it's special-cased here to match the actual add_rule in
            # rules.py exactly.
            reachable = has_paintbrush and ("Wall Dive" in owned or "Somersault" in owned)
        elif location_name == "Eraser: Shop (Two cousins)":
            # Second genuine OR-logic exception: Wall Dive OR Paint
            # Stroke, on top of the AND-only Paint Dive + Paint Stab
            # already in LOCATION_LOGIC_REQUIREMENTS for this location.
            reachable = (
                has_paintbrush
                and all(item in owned for item in required)
                and ("Wall Dive" in owned or "Paint Stroke" in owned)
            )
        elif location_name == "Heart Piece: Floral Foundry":
            # Third genuine OR-logic exception, direct request/correction:
            # Paint Dive AND (Wall Dive OR Somersault) -- replaces an
            # earlier, incorrect requirement (all 7 non-Paintbrush
            # abilities) that a real reference correction superseded.
            reachable = (
                has_paintbrush
                and "Paint Dive" in owned
                and ("Wall Dive" in owned or "Somersault" in owned)
            )
        else:
            reachable = has_paintbrush and all(item in owned for item in required)

        loc_id = ctx.server_location_name_to_id.get(location_name)
        hinted_item = ctx.hinted_locations.get(loc_id) if loc_id is not None else None
        # Direct clarification: a THIRD state, distinct from both
        # reachable/unreachable -- a check the player has already found
        # should look visually "done" (dimmed/disabled), not just fall
        # back to looking identical to any other reachable location.
        checked = loc_id is not None and loc_id in ctx.locations_checked

        # Human-readable logic text for the hover tooltip -- built here,
        # not on the mod side, since this is the same requirement data
        # (and the same "Paintbrush implicit everywhere but its own
        # altar" rule) rules.py itself enforces.
        #
        # Direct fix: this used to unconditionally prepend "Paintbrush" to
        # every list -- but several locations (Paint Flask/Heart Piece/
        # Eraser/Health Vial, per the source logic file's own format)
        # already list Paintbrush explicitly among their own
        # requirements, so it was showing up twice. Also a direct request
        # on its own merits: Paintbrush is implicitly required almost
        # everywhere, so listing it explicitly is just noise that eats
        # tooltip space without adding information -- filtered out
        # entirely now rather than special-cased around.
        if location_name == "Ability Altar: Paintbrush":
            logic_text = "No requirements"
        elif location_name == "Vanishing Vaults - Chest 3":
            logic_text = "Requires: Wall Dive or Somersault"
        elif location_name == "Eraser: Shop (Two cousins)":
            logic_text = "Requires: Paint Dive, Paint Stab, and (Wall Dive or Paint Stroke)"
        elif location_name == "Heart Piece: Floral Foundry":
            logic_text = "Requires: Paint Dive, and (Wall Dive or Somersault)"
        else:
            display_reqs = [r for r in required if r != "Paintbrush"]
            logic_text = "Requires: " + ", ".join(display_reqs) if display_reqs else "No further requirements"

        states.append({
            "id": pid,
            "reachable": reachable,
            "hinted": hinted_item is not None,
            "checked": checked,
            "item": hinted_item,
            "location_name": location_name,
            "logic": logic_text,
        })
    return states


async def bridge_watcher(ctx: ConstanceLiveContext):
    """Connects to the mod's local TCP bridge and relays checks/items live."""
    reader: Optional[asyncio.StreamReader] = None
    writer: Optional[asyncio.StreamWriter] = None
    last_states_sent_at = 0.0

    while not ctx.exit_event.is_set():
        # Real bug, found by real testing: this flush used to live inside
        # the "mod bridge is connected" branch below, coupling it to a
        # completely unrelated system -- whether we can send a queued
        # location check to the AP SERVER has nothing to do with whether
        # the local mod bridge happens to be connected at that exact
        # moment. If the mod bridge dropped (or, in testing, a one-shot
        # test script disconnected after sending its batch), this flush
        # would never run again even though the actual blockers (server
        # readiness, the data package) had long since cleared. Runs
        # unconditionally every loop iteration now, independent of mod
        # bridge state entirely -- the same category of bug as the
        # earlier DeathLink tag/mod-bridge coupling fixed previously.
        try:
            if ctx.pending_location_checks and ctx.server_ready():
                still_pending: list[str] = []
                resolved_ids: list[int] = []
                for name in ctx.pending_location_checks:
                    loc_id = ctx.server_location_name_to_id.get(name)
                    if loc_id is not None:
                        resolved_ids.append(loc_id)
                    else:
                        still_pending.append(name)
                if resolved_ids:
                    ctx.locations_checked.update(resolved_ids)
                    await ctx.send_msgs([{"cmd": "LocationChecks", "locations": resolved_ids}])
                    # Log cleanup. The server's own
                    # "found their X" print_json line already reports every
                    # one of these, so this was pure duplication.
                ctx.pending_location_checks = still_pending

            if ctx.pending_goal_status and ctx.server_ready() and not ctx.finished_game:
                await ctx.send_msgs([{"cmd": "StatusUpdate", "status": ClientStatus.CLIENT_GOAL}])
                ctx.finished_game = True
                ctx.pending_goal_status = False
                logger.info("Final boss (Puppet Master) defeated -- sent goal completion.")
        except Exception:
            logger.exception("Unexpected error while flushing pending location checks/goal status.")

        if reader is None:
            try:
                reader, writer = await asyncio.open_connection(BRIDGE_HOST, BRIDGE_PORT)
                logger.info(f"Connected to Constance mod bridge at {BRIDGE_HOST}:{BRIDGE_PORT}")
                ctx.resync_non_repeatable_items()
                # Re-send category settings on every real
                # bridge (re)connection, not just the first one -- the mod
                # can restart independently of this client staying up
                # (e.g. the game itself restarting), and a fresh mod
                # instance has no memory of settings sent to a previous
                # one.
                if ctx.category_settings:
                    ctx.pending_settings_send = True
            except (ConnectionRefusedError, OSError):
                logger.info("Waiting for Constance mod bridge (is the game running with the mod installed?)...")
                await asyncio.sleep(3)
                continue

        try:
            # If a linked death arrived, tell the mod to kill the local player.
            # (Tag application itself now happens immediately in on_package,
            # not here -- see the comment there for why.)
            if ctx.death_link_pending_kill:
                logger.info("DeathLink: sending kill_player to the mod now.")
                writer.write('{"type":"kill_player"}\n'.encode('utf-8'))
                await writer.drain()
                ctx.death_link_pending_kill = False

            # Send any queued in-game notification text to the mod.
            while ctx.pending_notifications:
                rich_text = ctx.pending_notifications.pop(0)
                escaped = rich_text.replace('\\', '\\\\').replace('"', '\\"')
                writer.write(('{"type":"notification","text":"' + escaped + '"}\n').encode('utf-8'))
                await writer.drain()

            # Per-category randomize toggles, sent once
            # per bridge connection (this flag only ever gets set again
            # by on_package's "Connected" handling, not re-armed here).
            if ctx.pending_settings_send:
                ctx.pending_settings_send = False
                settings_json = json.dumps({"type": "settings", **ctx.category_settings})
                writer.write((settings_json + "\n").encode('utf-8'))
                await writer.drain()

            # Keep the map overlay's logic state (normal/
            # greyed-out/outlined) current. Recomputed and resent
            # periodically rather than only on change -- simpler and more
            # robust than tracking exactly what changed, and this is a
            # small enough payload that resending it every ~5s is
            # negligible overhead. Only actually sends once we're fully
            # connected (slot/team known, needed for both the reachability
            # calculation and the hint lookup).
            now = time.monotonic()
            if ctx.slot is not None and (now - last_states_sent_at) >= 5.0:
                last_states_sent_at = now
                try:
                    states = compute_location_states(ctx)
                    if states:
                        payload = json.dumps({"type": "location_states", "states": states})
                        writer.write((payload + "\n").encode('utf-8'))
                        await writer.drain()
                except Exception:
                    logger.exception("Error computing/sending location states.")

            # Apply any newly received items that haven't been sent to the mod yet.
            for i, item in enumerate(ctx.items_received):
                item_name = resolve_item_name(ctx, item.item, item.player)
                pid = ITEM_PERSISTENCE_IDS.get(item_name)
                # Paint Flask and Heart Piece are repeatable (multiple
                # copies), so unlike every other real item, they must be
                # deduped per-received-instance (by location+player), not
                # by pid -- otherwise only the first copy of each would
                # ever get applied, since they'd all share the same
                # sentinel pid.
                is_repeatable = pid in (PAINT_FLASK_GRANT_SENTINEL, HEART_PIECE_GRANT_SENTINEL, ERASER_GRANT_SENTINEL)
                if pid == PAINT_FLASK_GRANT_SENTINEL:
                    item_key = f"paintflask:{item.location}:{item.player}" if item.location >= 0 else f"paintflask:{item.location}:{item.player}:{i}"
                elif pid == HEART_PIECE_GRANT_SENTINEL:
                    item_key = f"heartpiece:{item.location}:{item.player}" if item.location >= 0 else f"heartpiece:{item.location}:{item.player}:{i}"
                elif pid == ERASER_GRANT_SENTINEL:
                    item_key = f"eraser:{item.location}:{item.player}" if item.location >= 0 else f"eraser:{item.location}:{item.player}:{i}"
                else:
                    # Every /send'd filler item shares the exact
                    # same sentinel (location=-1, player=0) AP's own
                    # server uses for a cheat-console grant -- a SECOND
                    # /send'd filler item would have collided on the
                    # identical "filler:-1:0" key and been silently
                    # skipped as "already applied". i (this item's own,
                    # stable position in items_received -- the same every
                    # reconnect, since the server always resends the same
                    # list in the same order) is included specifically
                    # for the sentinel-location case to guarantee
                    # uniqueness; a real location's own id is already
                    # unique on its own, no index needed there.
                    if pid is not None:
                        item_key = pid
                    elif item.location < 0:
                        item_key = f"filler:{item.location}:{item.player}:{i}"
                    else:
                        item_key = f"filler:{item.location}:{item.player}"
                if item_key in ctx.applied_persistence_ids:
                    continue
                # An item name AP itself couldn't resolve (its
                # own fallback text is exactly "Unknown item (ID: N)",
                # from a stale/mismatched data package -- almost always a
                # seed generated with a different apworld version than
                # what's currently installed) doesn't match anything in
                # ITEM_PERSISTENCE_IDS either, so pid ends up None and it
                # silently fell into the SAME path as genuine filler --
                # granting Handful of Glimmer currency for what could
                # have been any real item at all (Map Icons, an ability,
                # anything), with nothing in the log to explain why.
                # Caught here specifically, before that misrouting can
                # happen, with a clear, actionable warning instead.
                if item_name.startswith("Unknown item (ID:") or item_name.startswith("Unknown Item (ID:"):
                    if item_key not in ctx._warned_unresolvable_item_keys:
                        ctx._warned_unresolvable_item_keys.add(item_key)
                        logger.warning(
                            f"Received an item AP itself couldn't name ({item_name!r}, id={item.item}) -- "
                            "this usually means the seed was generated with a different .apworld version than "
                            "the one currently installed. NOT treating this as filler currency. Regenerate the "
                            "seed with the current constance.apworld to fix this properly.")
                    continue
                if pid is None:
                    # Filler -- gives real currency/lightstones, not
                    # purely cosmetic (see the note in
                    # resync_non_repeatable_items for the earlier,
                    # incorrect reasoning this corrects). Sending its own
                    # item_key as instance_key makes this idempotent on
                    # the mod side too, matching the repeatable-grant
                    # fix -- safe to resync/resend now.
                    escaped_name = item_name.replace('"', '\\"')
                    escaped_key = item_key.replace('"', '\\"')
                    # Reads the SAME cached
                    # value on_print_json reads (see on_package's
                    # "ReceivedItems" handling) instead of recomputing it
                    # independently -- the single-source-of-truth fix for
                    # the displayed quantity and the granted quantity
                    # occasionally disagreeing. Omitted entirely for
                    # amount-less filler (traps), where the mod has
                    # nothing to apply a number to anyway.
                    amount = ctx.pending_filler_amounts.get((item_name, item.location, item.player))
                    amount_field = ('"amount":' + str(amount) + ',') if amount is not None else ''
                    writer.write(('{"type":"apply_filler",' + amount_field + '"name":"' + escaped_name + '","instance_key":"' + escaped_key + '"}\n').encode('utf-8'))
                    await writer.drain()
                    ctx.applied_persistence_ids.add(item_key)
                    ctx.save_state()
                    continue
                # Real fix, for real testing: repeatable grants now carry
                # their own item_key as an "instance_key" field, letting
                # the mod itself recognize "I've already processed this
                # exact grant" no matter how many times this gets resent
                # -- this is what makes it safe to include these in the
                # resync-on-reconnect mechanism below, unlike before.
                if is_repeatable:
                    escaped_key = item_key.replace('"', '\\"')
                    writer.write(('{"type":"apply","id":"' + pid + '","instance_key":"' + escaped_key + '"}\n').encode('utf-8'))
                else:
                    writer.write(('{"type":"apply","id":"' + pid + '"}\n').encode('utf-8'))
                await writer.drain()
                ctx.applied_persistence_ids.add(item_key)
                ctx.save_state()
                # Log cleanup -- "Received X" from the
                # server already covers this, and the mod logs its own
                # side of the grant.

            # Read any pending check notifications from the mod (non-blocking-ish via wait_for).
            try:
                line_bytes = await asyncio.wait_for(reader.readline(), timeout=0.5)
            except asyncio.TimeoutError:
                line_bytes = b""

            if line_bytes:
                line = line_bytes.decode('utf-8').strip()

                if '"death"' in line:
                    if ctx.death_link_enabled:
                        await ctx.send_death("Died in Constance.")
                        # "Sent death link." was only ever
                        # visible in this console. Queued as an in-game
                        # notification too, so it shows up where the player
                        # actually is when it happens -- the console line is
                        # kept as well, since it's the only record if the
                        # mod overlay is off.
                        logger.info("Sent death link.")
                        ctx.pending_notifications.append("Sent death link.")
                    else:
                        logger.info("Local death detected by mod, but DeathLink is not enabled for this slot -- not sending.")

                client_message_text = extract_text_if_client_message(line)
                if client_message_text:
                    logger.info(client_message_text)

                pid = extract_id_if_check(line)
                if pid:
                    location_name = PERSISTENCE_ID_TO_LOCATION_NAME.get(pid)
                    if location_name is not None and location_name not in ctx.queued_or_sent_location_names:
                        ctx.queued_or_sent_location_names.add(location_name)
                        ctx.pending_location_checks.append(location_name)

                    if pid == FINAL_BOSS_ID and not ctx.finished_game:
                        ctx.pending_goal_status = True
            elif line_bytes == b"" and reader.at_eof():
                logger.warning("Mod bridge connection closed. Will retry.")
                reader, writer = None, None

        except (ConnectionResetError, BrokenPipeError, OSError) as e:
            logger.warning(f"Bridge connection error: {e}. Will retry.")
            reader, writer = None, None
            await asyncio.sleep(2)
        except Exception:
            logger.exception(
                "Unexpected error in bridge_watcher this cycle -- see traceback above. "
                "Will keep retrying on the next cycle rather than silently stopping."
            )
            await asyncio.sleep(1)


async def main(args):
    ctx = ConstanceLiveContext(args.connect, args.password)
    ctx.auth = args.name
    ctx.reset_state = args.reset_state
    ctx.server_task = asyncio.create_task(server_loop(ctx), name="server loop")
    if gui_enabled:
        ctx.run_gui()
    ctx.run_cli()

    watcher_task = asyncio.create_task(bridge_watcher(ctx), name="ConstanceBridgeWatcher")

    await ctx.exit_event.wait()
    ctx.server_address = None

    await watcher_task
    await ctx.shutdown()


if __name__ == "__main__":
    import colorama

    parser = get_base_parser(description="Constance LIVE Client (talks to the in-game mod, not the save file).")
    parser.add_argument("--name", default=None, help="Slot Name to connect as.")
    parser.add_argument("--reset-state", action="store_true",
                         help="Clear this seed/slot's saved 'already applied' record before connecting "
                              "(useful when re-testing the same seed repeatedly).")
    args, rest = parser.parse_known_args()
    colorama.init()
    asyncio.run(main(args))
    colorama.deinit()
