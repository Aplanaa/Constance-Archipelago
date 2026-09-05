from __future__ import annotations

from typing import TYPE_CHECKING

from BaseClasses import Item, ItemClassification

if TYPE_CHECKING:
    from .world import ConstanceWorld

# ---------------------------------------------------------------------------
# Item IDs. Base offset chosen arbitrarily but must be globally unique across
# all Archipelago worlds -- this range is unclaimed as of this writing but
# should be double-checked / reserved properly before any real release.
# ---------------------------------------------------------------------------
BASE_ID = 771_000

# Every persistence ID here comes directly from Constance.Core.dll and the
# Addressables bundles (see constance_persistence_catalog.json), not guessed.
ITEM_NAME_TO_ID: dict[str, int] = {
    # --- Abilities (progression). ps_unlock_Ability_* in the save format. ---
    "Paintbrush": BASE_ID + 1,          # ps_unlock_Ability_Brush
    "Paint Dive": BASE_ID + 2,          # ps_unlock_Ability_Dash
    "Paint Stab": BASE_ID + 3,          # ps_unlock_Ability_Stab
    "Paint Stroke": BASE_ID + 4,        # ps_unlock_Ability_Slice
    "Plunge": BASE_ID + 5,              # ps_unlock_Ability_Pogo
    "Wall Dive": BASE_ID + 6,           # ps_unlock_Ability_WallDive
    "Somersault": BASE_ID + 7,          # ps_unlock_Ability_DoubleJump
    "Paint Clone": BASE_ID + 8,         # ps_unlock_Ability_BombClone

    # --- Tears (progression). Required to reach the true ending / Void. ---
    "Tear of Floral Foundry": BASE_ID + 9,     # ps_unlock_Tear_Foundry
    "Tear of Astral Academy": BASE_ID + 10,    # ps_unlock_Tear_Academy
    "Tear of Chaotic Carnival": BASE_ID + 11,  # ps_unlock_Tear_Carnival
    "Tear of Vanishing Vaults": BASE_ID + 12,  # ps_unlock_Tear_Vaults

    # --- Key items (useful, not strictly required by logic here). ---
    # World Map (BASE_ID + 14) removed -- real testing found it softlocks
    # the game into a stuck dialogue when randomized. Not fully understood
    # why (whether it's specifically our suppression patch, or the game's
    # own vanilla grant reacting badly to being set outside its normal
    # trigger context), so removed entirely rather than guess at a partial
    # fix for a genuine softlock. The id gap is left as-is intentionally.
    "Camera": BASE_ID + 13,        # ps_unlock_Camera
    "Map Icons": BASE_ID + 15,     # ps_unlock_MapIcons
    "Frida Mask": BASE_ID + 16,    # ps_unlock_FridaMask
    # "Persevere Charm" removed entirely, per explicit request.

    # Health Vial -- a real, discoverable key item, just purchased via an
    # in-game shop rather than touched at an altar (confirmed from a real
    # screenshot: "When at full health, excess healing is stored for
    # later use", two purchase tiers). Real persistence IDs confirmed in
    # save data (ps_shopItem_Potion1/2, both simple I1 one-time-purchase
    # flags, not the fluctuating banked amount shown in the shop UI).
    "Health Vial": BASE_ID + 38,
    "Health Vial Upgrade": BASE_ID + 39,
    "Eraser": BASE_ID + 40,

    # --- Traps. Pure mod-side effects, no real persistence backing --
    # same "None" pattern as filler below, handled by name via
    # apply_filler on the mod side. ---
    "1 Hit Constance Trap": BASE_ID + 41,
    "Checkpoint Trap": BASE_ID + 42,
    "Perseverance Trap": BASE_ID + 43,

    # --- Filler. Purely flavor, except Glimmer/Lightstone amounts, which
    # the mod applies as real currency/resource top-ups (see get_filler_item_name). ---
    "Handful of Glimmer": BASE_ID + 18,
    "Handful of Lightstones": BASE_ID + 21,

    # Paint Flask -- see the note on FIXED_ITEM_POOL below for why this is
    # a single named item created many times, not 12 separate item types.
    "Paint Flask": BASE_ID + 19,

    # Heart Piece -- same repeatable-item pattern as Paint Flask (grants
    # via a sentinel that increments the real aggregate counter), but
    # simpler: check-detection for the 6 real heart locations is handled
    # cleanly by the mod's DropCollectPatch, so no counter-watching or
    # reconciliation is needed on the receiving side.
    "Heart Piece": BASE_ID + 20,

    # Real, confirmed inspiration names (the complete canonical set, found
    # in the game's own asset catalog, not just what one save file happened
    # to have). Added as real items -- unlike abilities, these are optional
    # bonus content (nothing in the game requires a *specific* inspiration
    # to progress), so unlike abilities they're safe to leave in the normal
    # shuffle rather than locking each to its own altar.
    "Inspiration: Copycat": BASE_ID + 22,
    "Inspiration: Dark Embrace": BASE_ID + 23,
    "Inspiration: Dark Revival": BASE_ID + 24,
    "Inspiration: Milkshake Remix": BASE_ID + 25,
    "Inspiration: Precision Brush": BASE_ID + 26,
    "Inspiration: Paint Overload": BASE_ID + 27,
    "Inspiration: Ripple Effect": BASE_ID + 28,
    "Inspiration: Potent Potion": BASE_ID + 29,
    "Inspiration: Extra Beat": BASE_ID + 30,
    "Inspiration: Saw Jump": BASE_ID + 31,
    "Inspiration: Paint Conductor": BASE_ID + 32,
    "Inspiration: Lifeline": BASE_ID + 33,
    "Inspiration: Master Spear": BASE_ID + 34,
    "Inspiration: Warrior Pose": BASE_ID + 35,
    "Inspiration: Tax Refund": BASE_ID + 36,
    "Inspiration: Whiplash": BASE_ID + 37,
}

# Maps an AP item name straight to the exact persistence ID string the
# client needs to write into the save file's World.Data array to grant it.
# None means "no direct save-file effect defined yet" (currently: filler).
ITEM_NAME_TO_PERSISTENCE_ID: dict[str, str | None] = {
    "Paintbrush": "ps_unlock_Ability_Brush",
    "Paint Dive": "ps_unlock_Ability_Dash",
    "Paint Stab": "ps_unlock_Ability_Stab",
    "Paint Stroke": "ps_unlock_Ability_Slice",
    "Plunge": "ps_unlock_Ability_Pogo",
    "Wall Dive": "ps_unlock_Ability_WallDive",
    "Somersault": "ps_unlock_Ability_DoubleJump",
    "Paint Clone": "ps_unlock_Ability_BombClone",
    "Tear of Floral Foundry": "ps_unlock_Tear_Foundry",
    "Tear of Astral Academy": "ps_unlock_Tear_Academy",
    "Tear of Chaotic Carnival": "ps_unlock_Tear_Carnival",
    "Tear of Vanishing Vaults": "ps_unlock_Tear_Vaults",
    "Camera": "ps_unlock_Camera",
    "Map Icons": "ps_unlock_MapIcons",
    "Frida Mask": "ps_unlock_FridaMask",
    "Health Vial": "ps_shopItem_Potion1",
    "Health Vial Upgrade": "ps_shopItem_Potion2",
    "Eraser": "ps_ap_EraserGrant",
    "Handful of Glimmer": None,
    "Handful of Lightstones": None,
    "1 Hit Constance Trap": None,
    "Checkpoint Trap": None,
    "Perseverance Trap": None,
    # Not a real persistence id -- a sentinel the mod recognizes specially
    # to mean "bank one more Paint Flask", since there's no per-instance
    # id to write directly (see the locations.py note on why Paint Flasks
    # work differently from everything else).
    "Paint Flask": "ps_ap_PaintFlaskGrant",
    "Heart Piece": "ps_ap_HeartPieceGrant",
    "Inspiration: Copycat": "ps_inspDrawing_Copycat",
    "Inspiration: Dark Embrace": "ps_inspDrawing_DarkLightning",
    "Inspiration: Dark Revival": "ps_inspDrawing_DarkRevival",
    "Inspiration: Milkshake Remix": "ps_inspDrawing_MilkshakeRemix",
    "Inspiration: Precision Brush": "ps_inspDrawing_NaturalTwenty",
    "Inspiration: Paint Overload": "ps_inspDrawing_PaintOverload",
    "Inspiration: Ripple Effect": "ps_inspDrawing_PogoWave",
    "Inspiration: Potent Potion": "ps_inspDrawing_PotentPotion",
    "Inspiration: Extra Beat": "ps_inspDrawing_PotionSynthesizer",
    "Inspiration: Saw Jump": "ps_inspDrawing_ScrewJump",
    "Inspiration: Paint Conductor": "ps_inspDrawing_SliceConductor",
    "Inspiration: Lifeline": "ps_inspDrawing_StabLifesteal",
    "Inspiration: Master Spear": "ps_inspDrawing_StabProjectile",
    "Inspiration: Warrior Pose": "ps_inspDrawing_SturdyKnight",
    "Inspiration: Tax Refund": "ps_inspDrawing_TaxRefund",
    "Inspiration: Whiplash": "ps_inspDrawing_TeleportClone",
}

DEFAULT_ITEM_CLASSIFICATIONS: dict[str, ItemClassification] = {
    "Paintbrush": ItemClassification.progression,
    "Paint Dive": ItemClassification.progression,
    "Paint Stab": ItemClassification.progression,
    "Paint Stroke": ItemClassification.progression,
    "Plunge": ItemClassification.progression,
    "Wall Dive": ItemClassification.progression,
    "Somersault": ItemClassification.progression,
    "Paint Clone": ItemClassification.progression,
    "Tear of Floral Foundry": ItemClassification.progression,
    "Tear of Astral Academy": ItemClassification.progression,
    "Tear of Chaotic Carnival": ItemClassification.progression,
    "Tear of Vanishing Vaults": ItemClassification.progression,
    "Camera": ItemClassification.useful,
    "Map Icons": ItemClassification.useful,
    # Real, root-cause fix found by real testing: AP's own CollectionState
    # only tracks progression-classified items for state.has() at all --
    # useful items are simply never recorded for that purpose, regardless
    # of whether they were actually collected. Frida Mask genuinely gates
    # reaching the Puppet Hand / Puppet Master bosses now (per the updated
    # logic), so it MUST be progression, or every rule referencing it
    # becomes permanently unsatisfiable by the generator's own logic
    # engine, independent of anything happening in the real game.
    "Frida Mask": ItemClassification.progression,
    "Health Vial": ItemClassification.useful,
    "Health Vial Upgrade": ItemClassification.useful,
    "Eraser": ItemClassification.useful,
    "Handful of Glimmer": ItemClassification.filler,
    "Handful of Lightstones": ItemClassification.filler,
    "1 Hit Constance Trap": ItemClassification.trap,
    "Checkpoint Trap": ItemClassification.trap,
    "Perseverance Trap": ItemClassification.trap,
    "Paint Flask": ItemClassification.useful,
    "Heart Piece": ItemClassification.useful,
    "Inspiration: Copycat": ItemClassification.useful,
    "Inspiration: Dark Embrace": ItemClassification.useful,
    "Inspiration: Dark Revival": ItemClassification.useful,
    "Inspiration: Milkshake Remix": ItemClassification.useful,
    "Inspiration: Precision Brush": ItemClassification.useful,
    "Inspiration: Paint Overload": ItemClassification.useful,
    "Inspiration: Ripple Effect": ItemClassification.useful,
    "Inspiration: Potent Potion": ItemClassification.useful,
    "Inspiration: Extra Beat": ItemClassification.useful,
    "Inspiration: Saw Jump": ItemClassification.useful,
    "Inspiration: Paint Conductor": ItemClassification.useful,
    "Inspiration: Lifeline": ItemClassification.useful,
    "Inspiration: Master Spear": ItemClassification.useful,
    "Inspiration: Warrior Pose": ItemClassification.useful,
    "Inspiration: Tax Refund": ItemClassification.useful,
    "Inspiration: Whiplash": ItemClassification.useful,
}

# Items that always exist, exactly once each, regardless of location count.
# (Locations beyond this fixed set are filled with filler -- see world.create_items.)
FIXED_ITEM_POOL: list[str] = [
    "Tear of Floral Foundry", "Tear of Astral Academy",
    "Tear of Chaotic Carnival", "Tear of Vanishing Vaults",
    # Map Icons is no longer randomized -- always granted
    # at the start instead (see create_all_items' push_precollected call).
    # Its own location ("Map Icons Pickup") still exists and gives
    # something else, same as any other location.
    # Camera removed from the item pool entirely -- it is
    # no longer something you can receive from Archipelago. Its own
    # location ("Camera Pickup") deliberately STAYS a real check, and the
    # mod no longer suppresses ps_unlock_Camera, so picking it up in-game
    # both grants the real camera immediately (vanilla behaviour) and
    # sends the check. Kept in ITEM_NAME_TO_ID above so existing item ids
    # stay stable and nothing else shifts.
    "Frida Mask", "Health Vial", "Health Vial Upgrade",
    "Paint Stab", "Paint Stroke", "Plunge", "Wall Dive", "Somersault", "Paint Clone",
] + ["Eraser"] * 5 + ["Paint Flask"] * 12 + ["Heart Piece"] * 9 + [
    "Inspiration: Copycat", "Inspiration: Dark Embrace", "Inspiration: Dark Revival", "Inspiration: Milkshake Remix", "Inspiration: Precision Brush", "Inspiration: Paint Overload", "Inspiration: Ripple Effect", "Inspiration: Potent Potion", "Inspiration: Extra Beat", "Inspiration: Saw Jump", "Inspiration: Paint Conductor", "Inspiration: Lifeline", "Inspiration: Master Spear", "Inspiration: Warrior Pose", "Inspiration: Tax Refund", "Inspiration: Whiplash"
]
# Heart Piece: 9 copies, matching the 9 real "Heart Piece: N" locations
# (converted to the pure counter-based approach -- see locations.py --
# after only 6 of the originally hoped-for 9 real per-instance locations
# could ever be confirmed). Same repeatable-item pattern as Paint Flask.
# Inspirations: 16 distinct items, one each, matching the 16 real
# locations 1:1 (like abilities, but NOT locked -- see the note below).
# Paint Flask is a single named item created 12 times, not 12 separate
# item types -- matches the 12 real "Paint Flask: N" locations, and lets
# the mod bank one more (up to 12) each time any copy is received,
# regardless of which of the 12 locations it came from (normal AP
# decoupling between which check you did and what you get back).
#
# Paintbrush and Paint Dive are deliberately NOT in this pool -- both
# locked directly to their own altars in locations.py, per direct
# request that both be equally guaranteed regardless of AP round-trip
# timing (matching the live mod's safety net for these two specifically).
# Paintbrush's lock is also a real correctness requirement on top of
# that: the logic rule "everything requires Paint Stab" doesn't touch
# it, but nothing in this world ever requires Paint Dive either, so its
# lock here is purely the requested guarantee, not a self-lock
# prevention need the way Paintbrush's is.
#
# The other 6 abilities remain genuinely, disconnectedly randomized --
# an ability altar is just another check location, not tied to the
# ability it happens to give in vanilla (no live safety net for these;
# reachability is a logic problem, solved by rules.py instead).


class ConstanceItem(Item):
    game = "Constance"


def get_filler_item_name(world: ConstanceWorld) -> str:
    # Real feedback: filler was always the same "Handful of Glimmer" at a
    # fixed amount, and Lightstones (removed as locations earlier over
    # completeness concerns) were specifically requested back as a
    # receive-only filler option. The exact granted amounts (varied for
    # Glimmer, fixed per Lightstone) are decided on the mod side, not
    # here -- this only decides which of the two filler item types gets
    # picked, weighted so Glimmer is more common (matches it being the
    # more plentiful, minor resource in the real game).
    #
    # Traps. trap_percentage decides what fraction of
    # filler rolls become a trap instead of Glimmer/Lightstones; when one
    # does, the three individual trap weight options decide which trap.
    # Rolled independently each time this is called (once per filler slot
    # in the pool), rather than as a single upfront trap COUNT -- simpler,
    # and statistically equivalent for a percentage-based option.
    opts = world.options
    if opts.trap_percentage.value > 0 and world.random.randint(1, 100) <= opts.trap_percentage.value:
        weights = [
            opts.one_hit_trap_weight.value,
            opts.checkpoint_trap_weight.value,
            opts.perseverance_trap_weight.value,
        ]
        if sum(weights) > 0:
            return world.random.choices(
                ["1 Hit Constance Trap", "Checkpoint Trap", "Perseverance Trap"],
                weights=weights,
            )[0]
        # All three trap weights are 0 -- no valid trap to pick even
        # though trap_percentage said to roll one. Falls through to
        # normal filler rather than raising, since a 0/0/0 weight
        # configuration is a reasonable (if pointless) way to express
        # "don't actually generate any traps" without also having to set
        # trap_percentage back to 0.

    return world.random.choices(
        ["Handful of Glimmer", "Handful of Lightstones"],
        weights=[3, 1],
    )[0]


def create_item_with_correct_classification(world: ConstanceWorld, name: str) -> ConstanceItem:
    classification = DEFAULT_ITEM_CLASSIFICATIONS[name]
    return ConstanceItem(name, classification, ITEM_NAME_TO_ID[name], world.player)


def create_all_items(world: ConstanceWorld) -> None:
    # Per-category randomize toggles. When a category is
    # disabled, its item type is excluded entirely -- not just from
    # having a location, but from the pool altogether, matching "let the
    # game do its own normal thing, nothing suppressed or intercepted".
    # There's no need for a compensating filler count here: create_items'
    # own dynamic "needed_filler = unfilled_locations - len(itempool)"
    # calculation below already handles this correctly regardless of
    # which items get excluded, the same way it already handles the
    # chest-absorbed Flask/Heart/Eraser instances.
    #
    # Unlike Map Icons, these are NOT push_precollected when disabled --
    # the player still has to go get them normally in-game (the altar/
    # pickup just works unsuppressed), only the AP side of it is gone.
    #
    # Revised: for the categories that now keep their locations and get a
    # locked vanilla item instead (flasks/hearts/erasers/inspirations/
    # health vials -- see locations.place_locked_guaranteed_abilities),
    # excluding them here is still exactly right, just for a different
    # reason: the locked copies are created directly at their locations
    # rather than drawn from this pool, so leaving them in would
    # double-count them.
    opts = world.options
    excluded_types: set[str] = set()
    if not opts.randomize_flasks:
        excluded_types.add("Paint Flask")
    if not opts.randomize_hearts:
        excluded_types.add("Heart Piece")
    if not opts.randomize_erasers:
        excluded_types.add("Eraser")
    if not opts.randomize_abilities:
        excluded_types |= {"Paint Stroke", "Plunge", "Wall Dive", "Somersault", "Paint Clone"}
    if not opts.randomize_paint_stab:
        excluded_types.add("Paint Stab")
    if not opts.randomize_tears:
        excluded_types |= {
            "Tear of Floral Foundry", "Tear of Astral Academy",
            "Tear of Chaotic Carnival", "Tear of Vanishing Vaults",
        }
    # Randomize_frida_mask removed entirely --
    # Frida Mask is now always randomized, no exclusion possible.
    if not opts.randomize_health_vials:
        excluded_types |= {"Health Vial", "Health Vial Upgrade"}

    active_pool = [
        name for name in FIXED_ITEM_POOL
        if name not in excluded_types
        and not (name.startswith("Inspiration: ") and not opts.randomize_inspirations)
    ]
    itempool: list[Item] = [world.create_item(name) for name in active_pool]

    # Map Icons always available from the start, not
    # randomized. push_precollected is AP's own standard mechanism for
    # this -- the item is handed to the player immediately at connect
    # time, with no location needed at all (distinct from Paintbrush/
    # Paint Dive, which are locked to their own altars specifically and
    # still require visiting them; this is unconditional from the very
    # start of the seed).
    map_icons_item = world.create_item("Map Icons")
    world.multiworld.push_precollected(map_icons_item)

    # Camera is no longer push_precollected --
    # an earlier round mistook it for the same "always available from the
    # start" pattern as Map Icons right above and gave it this exact same
    # treatment, but that wasn't actually what was wanted. Camera was
    # never meant to be an Archipelago item at all (it's deliberately
    # absent from FIXED_ITEM_POOL, see that list's own comment) --
    # "Camera Pickup" (the location) is a real, ordinary AP check like any
    # other, but the actual Camera is obtained by the real, vanilla game
    # itself, naturally, the moment the player physically reaches that
    # spot -- the mod's own ps_unlock_Camera handling deliberately never
    # suppresses this (see Plugin.cs's UnsuppressedTrackedIds), so nothing
    # here needs to hand it out at all.

    number_of_unfilled_locations = len(world.multiworld.get_unfilled_locations(world.player))
    needed_filler = number_of_unfilled_locations - len(itempool)
    itempool += [world.create_filler() for _ in range(needed_filler)]

    world.multiworld.itempool += itempool
