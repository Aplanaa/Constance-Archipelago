from __future__ import annotations

from typing import TYPE_CHECKING

from BaseClasses import Location

if TYPE_CHECKING:
    from .world import ConstanceWorld

BASE_ID = 771_000

# ---------------------------------------------------------------------------
# "Named" locations: one per ability / tear / key item, each tied to the
# exact real persistence ID confirmed from Constance.Core.dll + a real save.
# region here refers to our AP Region name (see regions.py).
# ---------------------------------------------------------------------------
NAMED_LOCATIONS: list[tuple[str, str, str]] = [
    # (AP location name, AP region name, real ps_ persistence ID)
    ("Ability Altar: Paintbrush", "Janky Junction", "ps_unlock_Ability_Brush"),
    ("Ability Altar: Paint Dive", "Floral Foundry", "ps_unlock_Ability_Dash"),
    ("Ability Altar: Paint Stab", "Chaotic Carnival", "ps_unlock_Ability_Stab"),
    ("Ability Altar: Paint Stroke", "Astral Academy", "ps_unlock_Ability_Slice"),
    ("Ability Altar: Plunge", "Vanishing Vaults", "ps_unlock_Ability_Pogo"),
    ("Ability Altar: Wall Dive", "Vanishing Vaults", "ps_unlock_Ability_WallDive"),
    ("Ability Altar: Somersault", "Painterly Peak", "ps_unlock_Ability_DoubleJump"),
    ("Ability Altar: Paint Clone", "Floral Foundry", "ps_unlock_Ability_BombClone"),
    ("Tear: Floral Foundry", "Floral Foundry", "ps_unlock_Tear_Foundry"),
    ("Tear: Astral Academy", "Astral Academy", "ps_unlock_Tear_Academy"),
    ("Tear: Chaotic Carnival", "Chaotic Carnival", "ps_unlock_Tear_Carnival"),
    ("Tear: Vanishing Vaults", "Vanishing Vaults", "ps_unlock_Tear_Vaults"),
    ("Camera Pickup", "Janky Junction", "ps_unlock_Camera"),
    # ("World Map Pickup" removed -- see items.py for why: real testing
    # found it softlocks the game into a stuck dialogue when randomized.)
    ("Map Icons Pickup", "Janky Junction", "ps_unlock_MapIcons"),
    ("Frida Mask Pickup", "Astral Academy", "ps_unlock_FridaMask"),
    # "Persevere Charm Pickup" removed entirely, per explicit request
    # ("Delete it") -- likely tied to the known edge case flagged much
    # earlier: it appears to auto-grant via a checkpoint mechanic rather
    # than through Collect(), meaning our suppression may never have
    # correctly intercepted it in the first place.
    # Health Vial: real key item, purchased via shop rather than found at
    # an altar (confirmed via a real screenshot: "excess healing stored
    # for later use", two purchase tiers). Real ids confirmed in save
    # data. HONEST CAVEAT: unlike abilities/chests/hearts, this doesn't go
    # through either patched code path (CConPlayerInventoryManager.Collect
    # or AConEntityDropBehaviour.Collect) -- shop purchases use their own,
    # not-yet-investigated code path, so there's currently no suppression
    # for this specific pair. If bought normally in-game, the player would
    # also separately receive whatever AP sends for it (a milder version
    # of the double-grant issue found for chests, since it at least costs
    # real in-game currency rather than being free).
    ("Health Vial Purchase", "Janky Junction", "ps_shopItem_Potion1"),
    ("Health Vial Upgrade Purchase", "Janky Junction", "ps_shopItem_Potion2"),
    # Eraser: switched to the full Paint-Flask-style pure counter system
    # (no per-instance location at all) after only 1 of an expected 5
    # real locations could ever be confirmed -- matches the real
    # aggregate counter (ps_item_Eraser) crossing each of 5 thresholds.
] + [
    # Each instance now has real, distinct identity (own
    # name, region, logic) -- made possible by the position-based
    # identity tracker in the mod, which can now tell physically distinct
    # pickups apart instead of just counting. Instance 1 is the one
    # permanently absorbed by a chest (excluded, per earlier reasoning);
    # the surviving numbers below are exactly what the position-based
    # system's own bootstrap data confirmed as real, distinct locations.
    ("Eraser: Janky Junction (Bubble Trouble)", "Janky Junction", "ps_item_Eraser#instance2"),
    ("Eraser: Shop (Two cousins)", "Janky Junction", "ps_item_Eraser#instance3"),
    ("Eraser: Chaotic Carnival (Jackie's Gacha)", "Chaotic Carnival", "ps_item_Eraser#instance4"),
    ("Eraser: Vanishing Vaults", "Vanishing Vaults", "ps_item_Eraser#instance5"),
] + [
    # Heart Piece: same reasoning as Eraser above. Instance 2 is the one
    # permanently absorbed by a chest.
    ("Heart Piece: Floral Foundry", "Floral Foundry", "ps_item_HeartPiece#instance1"),
    ("Heart Piece: Chaotic Carnival 2 (Jackie's Gacha)", "Chaotic Carnival", "ps_item_HeartPiece#instance3"),
    ("Heart Piece: Astral Academy 1", "Astral Academy", "ps_item_HeartPiece#instance4"),
    ("Heart Piece: Janky Junction (Shop)", "Janky Junction", "ps_item_HeartPiece#instance5"),
    ("Heart Piece: Vanishing Vaults 2", "Vanishing Vaults", "ps_item_HeartPiece#instance6"),
    ("Heart Piece: Astral Academy 2", "Astral Academy", "ps_item_HeartPiece#instance7"),
    ("Heart Piece: Vanishing Vaults 1", "Vanishing Vaults", "ps_item_HeartPiece#instance8"),
    ("Heart Piece: Chaotic Carnival 1", "Chaotic Carnival", "ps_item_HeartPiece#instance9"),
]

# ---------------------------------------------------------------------------
# "Extra" locations: real per-instance scene objects (chests, heart pieces)
# pulled directly from constance_persistence_catalog.json. These pad out the
# check count and give filler items somewhere to live. Their exact persistence
# IDs are real per-instance GUIDs confirmed present in an actual save file.
# ---------------------------------------------------------------------------
EXTRA_LOCATIONS: list[tuple[str, str, str]] = [
    ("Vanishing Vaults - Chest 1", "Vanishing Vaults", "ps_Prod_V18_d336557d-21de-494b-9252-3249efd8a329"),
    ("Vanishing Vaults - Chest 2", "Vanishing Vaults", "ps_Prod_V25_85f4c4a9-2607-4e0b-a8cf-880b870b5547"),
    ("Vanishing Vaults - Chest 3", "Vanishing Vaults", "ps_Prod_V08_47add804-cdd9-43b4-8d80-eabcfc5787b8"),
    ("Vanishing Vaults - Chest 4", "Vanishing Vaults", "ps_Prod_V04_1323ea94-0f95-dc12-c4ce-691e45cfd61c"),
    ("Chaotic Carnival - Chest 1", "Chaotic Carnival", "ps_Prod_C90_3a191403-d505-9a9d-10ce-6be0167f13d2"),
    ("Chaotic Carnival - Chest 2", "Chaotic Carnival", "ps_Prod_C96_49252065-b059-02ba-e69e-edaa30f89d14"),
    ("Chaotic Carnival - Chest 3", "Chaotic Carnival", "ps_Prod_C93_0d28e8d6-a001-436e-9ec1-9c525217f192"),
    ("Chaotic Carnival - Chest 4", "Chaotic Carnival", "ps_Prod_C95_e7442277-a767-7428-c17d-05c9b94ec518"),
    ("Chaotic Carnival - Chest 5", "Chaotic Carnival", "ps_Prod_C94_1feb337b-de88-b0db-92e5-c948661aa51d"),
    ("Floral Foundry - Chest 1", "Floral Foundry", "ps_Prod_F21_6a1b3f09-be2b-4645-a74f-75174bace78d"),
    ("Floral Foundry - Chest 2", "Floral Foundry", "ps_Prod_F13_c5e9a808-8a12-5dbe-943b-9e50cadf6f18"),
    ("Floral Foundry - Chest 3", "Floral Foundry", "ps_Prod_F02_cc06e08a-23b9-4cc9-ab1e-e4795e51419e"),
    ("Floral Foundry - Chest 4", "Floral Foundry", "ps_Prod_F16_a046b9c0-14bd-4053-b0ad-cef476f54daa"),
    ("Floral Foundry - Chest 5", "Floral Foundry", "ps_Prod_F20_6713e14b-6a29-4871-8ac6-7f04b594bd57"),
    ("Floral Foundry - Chest 6", "Floral Foundry", "ps_Prod_F03_9c79f624-ecb2-4be2-a979-dae5fd90f875"),
    ("Floral Foundry - Chest 7", "Floral Foundry", "ps_Prod_F25_2a26ffc7-0a98-1ced-4f63-ac3ee2224f5c"),
    ("Painterly Peak - Chest 1", "Painterly Peak", "ps_Prod_P03_73cee3e6-0f17-4b15-817f-476c28071c80"),
    ("Janky Junction - Chest 1", "Janky Junction", "ps_Prod_J07_a115468a-2a96-4b67-9ebd-37e0be846c63"),
    ("Janky Junction - Chest 2", "Janky Junction", "ps_Prod_J19_dacbb823-357f-b229-30ec-3a5f372e265a"),
    ("Janky Junction - Chest 3", "Janky Junction", "ps_Prod_J08_05ab593c-d503-41cf-b3eb-0eb89726594f"),
    ("Janky Junction - Chest 4", "Janky Junction", "ps_Prod_J13_2176d0ba-6fff-4407-417a-ecbe13c97576"),
    ("Astral Academy - Chest 1", "Astral Academy", "ps_Prod_A16_a8a6a17b-df53-495e-9c7a-8b9b828ccdc7"),
    ("Astral Academy - Chest 2", "Astral Academy", "ps_Prod_A20_d51fea7c-0552-d01f-4c9c-439cafe52c8a"),
    ("Astral Academy - Chest 3", "Astral Academy", "ps_Prod_A27_2d28c2b8-aeea-4fd1-b695-0faae8b91057"),
]


# ---------------------------------------------------------------------------
# "Extra v2" locations: added after the base 47-location version, per user
# request to expand the pool with Bosses, Inspirations, and Lightstones.
# All persistence IDs validated against real save/asset/scene data before
# being added here (see the project's own validation script), not hand-typed
# blind. Bosses and Inspirations use "Janky Junction" as their region since
# no reliable per-boss/per-inspiration region data exists (unlike chests,
# these aren't scene-instance tracked in our data) -- this only affects
# which apworld Region object they nominally belong to, not their display
# name, and has no logic impact since Junction has no gating requirement.
# ---------------------------------------------------------------------------
EXTRA_LOCATIONS_V2: list[tuple[str, str, str]] = [
    ("Boss: Awe King", "Janky Junction", "ps_Boss_AweKing_Beaten"),
    ("Boss: The Jester", "Janky Junction", "ps_Boss_BossJoker_Beaten"),
    ("Boss: Brian", "Janky Junction", "ps_Boss_BrainStoker_Beaten"),
    ("Boss: Chase Nemesis", "Janky Junction", "ps_Boss_ChaseNemesis_Beaten"),
    ("Boss: Cornelis", "Janky Junction", "ps_Boss_CornelisBoss_Beaten"),
    ("Boss: Jester, Encore", "Janky Junction", "ps_Boss_JokerInvisible_Beaten"),
    ("Boss: The Manipulator", "Janky Junction", "ps_Boss_JugglerBalloons_Beaten"),
    ("Boss: The Manipulator, Encore", "Janky Junction", "ps_Boss_JugglerBalls_Beaten"),
    ("Boss: High Patia", "Janky Junction", "ps_Boss_MothQueen_Beaten"),
    ("Boss: Palettus", "Janky Junction", "ps_Boss_Palettus_Beaten"),
    ("Boss: Sir Barfalot", "Janky Junction", "ps_Boss_PukeyBoy_Beaten"),
    ("Boss: Corrupted Mind", "Janky Junction", "ps_Boss_PuppetHandCorruption_Beaten"),
    ("Boss: Wounded Vessel", "Janky Junction", "ps_Boss_PuppetHandKungfu_Beaten"),
    ("Boss: Forsaken Will", "Janky Junction", "ps_Boss_PuppetHandStrings_Beaten"),
    ("Boss: Constance", "Janky Junction", "ps_Boss_PuppetMaster_Beaten"),
    ("Boss: Lord Korba", "Janky Junction", "ps_Boss_SlimeNemesis_Beaten"),
    ("Boss: Cubicus", "Janky Junction", "ps_Boss_Smasher_Beaten"),
    ("Inspiration: Copycat", "Janky Junction", "ps_inspDrawing_Copycat"),
    ("Inspiration: Dark Embrace", "Janky Junction", "ps_inspDrawing_DarkLightning"),
    ("Inspiration: Dark Revival", "Janky Junction", "ps_inspDrawing_DarkRevival"),
    ("Inspiration: Milkshake Remix", "Janky Junction", "ps_inspDrawing_MilkshakeRemix"),
    ("Inspiration: Precision Brush", "Janky Junction", "ps_inspDrawing_NaturalTwenty"),
    ("Inspiration: Paint Overload", "Janky Junction", "ps_inspDrawing_PaintOverload"),
    ("Inspiration: Ripple Effect", "Janky Junction", "ps_inspDrawing_PogoWave"),
    ("Inspiration: Potent Potion", "Janky Junction", "ps_inspDrawing_PotentPotion"),
    ("Inspiration: Extra Beat", "Janky Junction", "ps_inspDrawing_PotionSynthesizer"),
    ("Inspiration: Saw Jump", "Janky Junction", "ps_inspDrawing_ScrewJump"),
    ("Inspiration: Paint Conductor", "Janky Junction", "ps_inspDrawing_SliceConductor"),
    ("Inspiration: Lifeline", "Janky Junction", "ps_inspDrawing_StabLifesteal"),
    ("Inspiration: Master Spear", "Janky Junction", "ps_inspDrawing_StabProjectile"),
    ("Inspiration: Warrior Pose", "Janky Junction", "ps_inspDrawing_SturdyKnight"),
    ("Inspiration: Tax Refund", "Janky Junction", "ps_inspDrawing_TaxRefund"),
    ("Inspiration: Whiplash", "Janky Junction", "ps_inspDrawing_TeleportClone"),
    # Paint Flasks: originally no per-instance identity was possible at
    # all (pure aggregate counter). Now each has real, distinct identity
    # via the position-based tracker, same as Eraser/Heart Piece above.
] + [
    # Instances 3 and 12 are the two permanently absorbed by chests
    # (excluded below).
    ("Paint Flask: Floral Foundry 1", "Floral Foundry", "ps_item_PaintPiece#instance1"),
    ("Paint Flask: Floral Foundry 2", "Floral Foundry", "ps_item_PaintPiece#instance2"),
    ("Paint Flask: Chaotic Carnival 1", "Chaotic Carnival", "ps_item_PaintPiece#instance4"),
    ("Paint Flask: Astral Academy 1", "Astral Academy", "ps_item_PaintPiece#instance5"),
    ("Paint Flask: Janky Junction (Shop)", "Janky Junction", "ps_item_PaintPiece#instance6"),
    ("Paint Flask: Vanishing Vaults 1", "Vanishing Vaults", "ps_item_PaintPiece#instance7"),
    ("Paint Flask: Vanishing Vaults 3", "Vanishing Vaults", "ps_item_PaintPiece#instance8"),
    ("Paint Flask: Vanishing Vaults 2", "Vanishing Vaults", "ps_item_PaintPiece#instance9"),
    ("Paint Flask: Astral Academy 2", "Astral Academy", "ps_item_PaintPiece#instance10"),
    ("Paint Flask: Chaotic Carnival 2 (Jackie's Gacha)", "Chaotic Carnival", "ps_item_PaintPiece#instance11"),
]

ALL_LOCATIONS = NAMED_LOCATIONS + EXTRA_LOCATIONS + EXTRA_LOCATIONS_V2

LOCATION_NAME_TO_ID: dict[str, int] = {
    name: BASE_ID + 100 + i for i, (name, _region, _pid) in enumerate(ALL_LOCATIONS)
}

# AP location name -> real in-game persistence ID string.
# This is what the (future) client uses to know "has this check been done in-game".
LOCATION_NAME_TO_PERSISTENCE_ID: dict[str, str] = {
    name: pid for name, _region, pid in ALL_LOCATIONS
}

# The 3 "disguised" chests -- Janky Junction -
# Chest 2 and Vanishing Vaults - Chest 3 are really Paint Flasks, Floral
# Foundry - Chest 3 is really a Heart Piece. (Corrected from an earlier,
# wrong mapping that had Floral Foundry - Chest 7 and Vanishing Vaults -
# Chest 2 instead -- confirmed wrong by real testing.) Their true vanilla
# content is a Flask/Heart, not chest loot, so for locking purposes (see
# place_locked_guaranteed_items) they're extra locations for that
# category, not regular chests. Module level since both
# get_active_locations and place_locked_guaranteed_items need this same
# mapping.
SPECIAL_CHEST_DEPENDENCY: dict[str, str] = {
    "Janky Junction - Chest 2": "randomize_flasks",
    "Vanishing Vaults - Chest 3": "randomize_flasks",
    "Floral Foundry - Chest 3": "randomize_hearts",
}


class ConstanceLocation(Location):
    game = "Constance"


def get_active_locations(world: ConstanceWorld) -> list[tuple[str, str, str]]:
    # EVERY category now keeps its location as
    # a real, trackable AP check, even when its "randomize_X" toggle is
    # off -- "not randomized" only changes which item gets LOCKED there
    # (see place_locked_guaranteed_items below), guaranteed to be the
    # exact matching vanilla item, same mechanism Paintbrush/Paint Dive
    # already use. This means state.has() for these items stays
    # meaningful and correct either way, with no special-casing needed
    # anywhere else in rules.py: the item is always guaranteed reachable
    # exactly where the real game already puts it, just via a fixed
    # placement instead of a shuffled one.
    #
    # randomize_chests has been removed entirely -- chests are always
    # real, randomized AP locations now, unconditionally.
    #
    # Bosses are the one deliberate exception left as pure exclusion when
    # randomize_bosses is off: a boss defeat has no natural "vanilla
    # item" concept to lock in, unlike every other category here.
    #
    # "Boss: Constance" is deliberately EXEMPT from randomize_bosses,
    # always present regardless -- it's tied directly to the live "you
    # finished the game" signal (FINAL_BOSS_ID in the live client), and
    # disabling boss randomization shouldn't also disable AP's ability to
    # recognize the seed is done.
    opts = world.options
    active = []
    for name, region, pid in ALL_LOCATIONS:
        if name.startswith("Boss: ") and name != "Boss: Constance" and not opts.randomize_bosses:
            continue
        active.append((name, region, pid))
    return active


def create_all_locations(world: ConstanceWorld) -> None:
    active_locations = get_active_locations(world)

    by_region: dict[str, list[tuple[str, str, str]]] = {}
    for name, region_name, pid in active_locations:
        by_region.setdefault(region_name, []).append((name, region_name, pid))

    for region_name, entries in by_region.items():
        region = world.get_region(region_name)
        names_with_ids = {name: LOCATION_NAME_TO_ID[name] for name, _r, _p in entries}
        region.add_locations(names_with_ids, ConstanceLocation)

    place_locked_guaranteed_abilities(world)


def place_locked_guaranteed_abilities(world: ConstanceWorld) -> None:
    # Paintbrush and Paint Dive both stay locked to their own altars, per
    # direct request -- both guaranteed regardless of AP round-trip
    # timing, matching the live mod's safety net for these two
    # specifically. The other 6 abilities remain genuinely randomized:
    # nothing in this world's logic requires any of THEM specifically
    # (only Paint Stab is required anywhere -- see rules.py), so locking
    # them isn't needed for correctness the way it is for these two.
    #
    # Paintbrush must stay locked regardless: the rule "everything
    # requires Paintbrush" would create a real, genuine self-lock in the
    # generation logic itself if Paintbrush could be placed anywhere else
    # (every other location requires it) -- a correctness requirement,
    # not just a live-gameplay concern. Paint Dive doesn't have this same
    # logical necessity (nothing else requires it), but is locked anyway
    # per direct request, since it's the second thing obtained in vanilla
    # immediately after Paintbrush and should be equally guaranteed.
    from . import items

    for altar_name, item_name in (
        ("Ability Altar: Paintbrush", "Paintbrush"),
        ("Ability Altar: Paint Dive", "Paint Dive"),
    ):
        altar_location = world.get_location(altar_name)
        locked_item = items.create_item_with_correct_classification(world, item_name)
        altar_location.place_locked_item(locked_item)

    # When disabled, DON'T remove the
    # location from AP -- keep it as a real, trackable check, but lock it
    # to always contain its own matching vanilla item, exactly like
    # Paintbrush/Paint Dive above. This is what makes disabling these
    # toggles actually safe for logic: state.has() for a locked item
    # stays just as meaningful and correct as for a genuinely randomized
    # one, since AP guarantees it's reachable exactly where the real game
    # already puts it -- no special-casing needed anywhere else in
    # rules.py for either case.
    opts = world.options
    if not opts.randomize_paint_stab:
        stab_location = world.get_location("Ability Altar: Paint Stab")
        stab_item = items.create_item_with_correct_classification(world, "Paint Stab")
        stab_location.place_locked_item(stab_item)

    if not opts.randomize_abilities:
        for altar_name, item_name in (
            ("Ability Altar: Paint Stroke", "Paint Stroke"),
            ("Ability Altar: Plunge", "Plunge"),
            ("Ability Altar: Wall Dive", "Wall Dive"),
            ("Ability Altar: Somersault", "Somersault"),
            ("Ability Altar: Paint Clone", "Paint Clone"),
        ):
            altar_location = world.get_location(altar_name)
            locked_item = items.create_item_with_correct_classification(world, item_name)
            altar_location.place_locked_item(locked_item)

    if not opts.randomize_tears:
        for tear_location_name, item_name in (
            ("Tear: Floral Foundry", "Tear of Floral Foundry"),
            ("Tear: Astral Academy", "Tear of Astral Academy"),
            ("Tear: Chaotic Carnival", "Tear of Chaotic Carnival"),
            ("Tear: Vanishing Vaults", "Tear of Vanishing Vaults"),
        ):
            tear_location = world.get_location(tear_location_name)
            locked_item = items.create_item_with_correct_classification(world, item_name)
            tear_location.place_locked_item(locked_item)

    # EVERY remaining category now gets this
    # exact same treatment when its toggle is off -- location stays a
    # real AP check, item gets locked to the guaranteed matching vanilla
    # one. Paint Flask/Heart Piece/Eraser all share one repeated item name
    # (12/9/5 copies respectively in FIXED_ITEM_POOL), so every matching
    # location just gets locked to that same name. Inspirations are 1:1
    # instead -- each location's own name IS its item's name already, no
    # separate mapping needed. The 3 disguised chests (SPECIAL_CHEST_
    # DEPENDENCY) are extra locations for Flask/Heart specifically here,
    # since that's their true vanilla content, not chest loot.
    if not opts.randomize_flasks:
        flask_locations = [name for name, _r, _p in ALL_LOCATIONS if name.startswith("Paint Flask: ")]
        flask_locations += [name for name, dep in SPECIAL_CHEST_DEPENDENCY.items() if dep == "randomize_flasks"]
        for location_name in flask_locations:
            location = world.get_location(location_name)
            locked_item = items.create_item_with_correct_classification(world, "Paint Flask")
            location.place_locked_item(locked_item)

    if not opts.randomize_hearts:
        heart_locations = [name for name, _r, _p in ALL_LOCATIONS if name.startswith("Heart Piece: ")]
        heart_locations += [name for name, dep in SPECIAL_CHEST_DEPENDENCY.items() if dep == "randomize_hearts"]
        for location_name in heart_locations:
            location = world.get_location(location_name)
            locked_item = items.create_item_with_correct_classification(world, "Heart Piece")
            location.place_locked_item(locked_item)

    if not opts.randomize_erasers:
        for name, _r, _p in ALL_LOCATIONS:
            if not name.startswith("Eraser: "):
                continue
            location = world.get_location(name)
            locked_item = items.create_item_with_correct_classification(world, "Eraser")
            location.place_locked_item(locked_item)

    if not opts.randomize_inspirations:
        for name, _r, _p in ALL_LOCATIONS:
            if not name.startswith("Inspiration: "):
                continue
            location = world.get_location(name)
            locked_item = items.create_item_with_correct_classification(world, name)
            location.place_locked_item(locked_item)

    if not opts.randomize_health_vials:
        for location_name, item_name in (
            ("Health Vial Purchase", "Health Vial"),
            ("Health Vial Upgrade Purchase", "Health Vial Upgrade"),
        ):
            location = world.get_location(location_name)
            locked_item = items.create_item_with_correct_classification(world, item_name)
            location.place_locked_item(locked_item)

    # Frida Mask can no longer be disabled from
    # randomization at all -- the randomize_frida_mask toggle is removed
    # entirely (see options.py), so Frida Mask is now always a genuine,
    # randomized item like any other, never locked to its own vanilla
    # location.
