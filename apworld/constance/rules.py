from __future__ import annotations

from typing import TYPE_CHECKING

from worlds.generic.Rules import add_rule, forbid_items_for_player

if TYPE_CHECKING:
    from .world import ConstanceWorld


# Requirement data transcribed from real user playtesting/research notes,
# per-location. AND logic only (no OR cases were given). Paint Stab is
# implicit in almost every entry via the user's own "AfterPaintStabLock"
# shorthand; everywhere else already requires Paintbrush via the flat
# rule below, so that part needs no separate table.
#
# EXCLUDED, and flagged back to the user: a self-referencing requirement
# on 6 of the 8 ability altars (e.g. Paint Stab's own altar apparently
# "needing" Paint Stab). Design update from the user mid-conversation:
# abilities are now fully, disconnectedly randomized (no live safety
# net), so a literal self-requirement here would be a real,
# generation-breaking self-lock the moment that ability's own item
# landed on its own altar -- not just a softlock risk, an unsolvable
# seed. Only the other, non-self-referencing parts of those entries are
# kept below.
_PS = "Paint Stab"
_PST = "Paint Stroke"
_PL = "Plunge"
_WD = "Wall Dive"
_SS = "Somersault"
_PC = "Paint Clone"
_PD = "Paint Dive"
_FM = "Frida Mask"
_ALL_ABILITIES = [_PS, _PST, _PL, _WD, _SS, _PC]
_ALL_8_ABILITIES = ["Paintbrush", "Paint Dive"] + _ALL_ABILITIES
_ALL_TEARS = [
    "Tear of Floral Foundry", "Tear of Astral Academy",
    "Tear of Chaotic Carnival", "Tear of Vanishing Vaults",
]

LOCATION_REQUIREMENTS: dict[str, list[str]] = {
    # --- Abilities (self-referencing parts excluded, see note above) ---
    "Ability Altar: Paintbrush": [],
    "Ability Altar: Paint Dive": [],
    "Ability Altar: Paint Stab": [],
    "Ability Altar: Paint Stroke": [_PS],
    "Ability Altar: Plunge": [_PS],
    "Ability Altar: Wall Dive": [_PS, _PST],
    "Ability Altar: Somersault": [_PS, _PL],
    "Ability Altar: Paint Clone": [_PS, _PST],

    # --- Tears ---
    "Tear: Floral Foundry": [_PS],
    "Tear: Astral Academy": [_PS, _PST],
    "Tear: Chaotic Carnival": [_PS, _PL],
    "Tear: Vanishing Vaults": [_PS, _WD],

    # --- Other key items ---
    "Camera Pickup": [],
    "Map Icons Pickup": [_PD],
    "Frida Mask Pickup": [_PS, _WD] + _ALL_TEARS,
    # "Persevere Charm Pickup" removed entirely -- explicit "(Delete it)".

    # --- Shop purchases ---
    # These previously had an unconditional
    # forbid_items_for_player suppressing all 8 abilities (see the old
    # _ABILITY_FREE_LOCATIONS below), based on an earlier, incorrect
    # requirement of "all 8 abilities". The real reference data has much
    # lower actual requirements -- Health Vial Purchase only needs Paint
    # Dive, Upgrade needs 5 of the 8 (missing Plunge and Somersault). The
    # "no abilities in shops" behavior is now a proper, optional toggle
    # (see set_no_abilities_in_shops below) instead of baked in here.
    "Health Vial Purchase": [_PD],
    "Health Vial Upgrade Purchase": [_PD, _PS, _PST, _WD, _PC],

    # --- Chests: explicit per-chest annotation where given, else the
    # region fallback (Carnival->Plunge, Academy->PaintStroke,
    # Vaults->PaintStroke+WallDive) layered on top of needing Paint Stab.
    "Vanishing Vaults - Chest 1": [_PS, _WD],
    "Vanishing Vaults - Chest 2": [_PS, _WD],
    "Vanishing Vaults - Chest 3": [],  # OR-only: Wall Dive or Somersault, no Paint Stab (per the updated reference -- header rule says only Paintbrush is implicit, and unlike Eraser: Shop/Heart Piece: Floral Foundry, this line lists no separate AND-part)
    "Vanishing Vaults - Chest 4": [],
    "Chaotic Carnival - Chest 1": [_PS, _PL],
    "Chaotic Carnival - Chest 2": [_PS, _PL],
    "Chaotic Carnival - Chest 3": [_PS, _PL],
    "Chaotic Carnival - Chest 4": [_PS, _PST, _WD],  # explicit override, noted as "was Painterly Peak"
    "Chaotic Carnival - Chest 5": [_PS, _PL],
    "Floral Foundry - Chest 1": [_PS],  # no fallback rule given for Foundry
    "Floral Foundry - Chest 2": [_PS],
    "Floral Foundry - Chest 3": [_PS],
    "Floral Foundry - Chest 4": [_PS],
    "Floral Foundry - Chest 5": [_PS, _SS, _PL],
    "Floral Foundry - Chest 6": [_PS, _SS],
    "Floral Foundry - Chest 7": [_PS, _PL],
    "Painterly Peak - Chest 1": [_PS, _PL, _PST, _WD],
    "Janky Junction - Chest 1": [_PS],  # no fallback rule for Janky Junction
    "Janky Junction - Chest 2": [_PS, _PST],
    "Janky Junction - Chest 3": [_PS],
    "Janky Junction - Chest 4": [_PS],
    "Astral Academy - Chest 1": [_PS, _PST],
    "Astral Academy - Chest 2": [_PS, _PST],
    "Astral Academy - Chest 3": [_PS, _PST],

    # --- Bosses ---
    "Boss: Awe King": [_PS, _PST],
    "Boss: The Jester": [_PS],
    "Boss: Brian": [_PS],
    "Boss: Chase Nemesis": [_PS, _PST],
    "Boss: Cornelis": [_PS, _PL],
    "Boss: Jester, Encore": [_PS, _PL],
    "Boss: The Manipulator": [_PS, _PL],
    "Boss: The Manipulator, Encore": [_PS, _PL],
    "Boss: High Patia": [_PS, _PST],
    "Boss: Palettus": [_PS, _PL],
    "Boss: Sir Barfalot": [_PS, _WD],
    # The 4 tears are no longer listed for
    # these 4 finale bosses -- redundant now that Frida Mask (also
    # required here) already requires all 4 tears itself to obtain in the
    # first place, per the updated reference.
    "Boss: Corrupted Mind": [_PS, _PD, _PST, _PL, _WD, _SS] + [_FM],
    "Boss: Wounded Vessel": [_PS, _PD, _PST, _PL, _WD, _SS] + [_FM],
    "Boss: Forsaken Will": [_PS, _PD, _PST, _PL, _WD, _SS] + [_FM],
    "Boss: Constance": ["Paintbrush", _PS, _PD, _PST, _PL, _WD, _SS] + [_FM],
    "Boss: Lord Korba": [_PS, _WD],
    "Boss: Cubicus": [_PS],

    # --- Inspirations ---
    "Inspiration: Copycat": [_PS, _PST],
    "Inspiration: Dark Embrace": [_PS],
    "Inspiration: Dark Revival": [_PS, _PST, _PL, _WD],
    "Inspiration: Milkshake Remix": [_PS, _SS, _PL],
    "Inspiration: Precision Brush": [],
    "Inspiration: Paint Overload": [_PS, _PST, _WD],
    "Inspiration: Ripple Effect": [_PS, _PL, _WD],
    "Inspiration: Potent Potion": [_PS, _PST, _PC],
    "Inspiration: Extra Beat": [_PS, _PST, _WD, _PC],
    "Inspiration: Saw Jump": [_PS, _SS, _WD, _PL],
    "Inspiration: Paint Conductor": [_PS, _PL, _PST],
    "Inspiration: Lifeline": [_PS],
    "Inspiration: Master Spear": [_PS, _PST, _PL, _PC],
    "Inspiration: Warrior Pose": [_PS, _PST],
    "Inspiration: Tax Refund": [_PS],
    "Inspiration: Whiplash": [_PS, _PST, _PC, _WD],

    # --- Paint Flask / Heart Piece / Eraser ---
    # These now have real, distinct per-location logic
    # (made possible by the position-based identity tracker in the mod),
    # replacing the old blanket "_ALL_8_ABILITIES" requirement that used
    # to apply uniformly to every instance regardless of where it
    # actually was. Paintbrush omitted throughout -- implicit via the
    # base rule, same convention as everywhere else in this dict.
    "Eraser: Janky Junction (Bubble Trouble)": [_PD, _PS, _PST, _WD, _PC],
    # "Eraser: Shop (Two cousins)" has a genuine OR requirement (Wall Dive
    # or Paint Stroke) -- handled as a special-case add_rule in
    # set_all_location_rules, same pattern as Vanishing Vaults - Chest 3.
    # Listed here with just its AND-only part so forbid_items_for_player
    # (below) still protects Paint Dive and Paint Stab from self-locking
    # it; the OR-specific items get their own explicit protection too.
    "Eraser: Shop (Two cousins)": [_PD, _PS],
    "Eraser: Chaotic Carnival (Jackie's Gacha)": [_PD, _PS, _PL],
    "Eraser: Vanishing Vaults": [_PD, _PS, _WD, _PC],

    "Heart Piece: Floral Foundry": [_PD, _PS],
    "Heart Piece: Chaotic Carnival 2 (Jackie's Gacha)": [_PD, _PS, _PL],
    "Heart Piece: Astral Academy 1": [_PD, _PS, _PST],
    "Heart Piece: Janky Junction (Shop)": [_PD, _PS],
    "Heart Piece: Vanishing Vaults 2": [_PD, _PS, _WD],
    "Heart Piece: Astral Academy 2": [_PD, _PS, _PST, _WD],
    "Heart Piece: Vanishing Vaults 1": [_PD, _PST, _PL],
    "Heart Piece: Chaotic Carnival 1": [_PD, _PS, _PL, _SS],

    "Paint Flask: Floral Foundry 1": [_PD, _PS],
    "Paint Flask: Floral Foundry 2": [_PD, _PS, _PL],
    "Paint Flask: Chaotic Carnival 1": [_PD, _PS, _PL],
    "Paint Flask: Astral Academy 1": [_PD, _PS, _PST],
    "Paint Flask: Janky Junction (Shop)": [_PD],
    "Paint Flask: Vanishing Vaults 1": [_PD, _PS, _PST, _WD],
    "Paint Flask: Vanishing Vaults 3": [_PD, _PS, _WD],
    "Paint Flask: Vanishing Vaults 2": [_PD, _PS, _WD, _SS],
    "Paint Flask: Astral Academy 2": [_PD, _PS, _PST, _WD],
    "Paint Flask: Chaotic Carnival 2 (Jackie's Gacha)": [_PD, _PS, _PL],
}

# The 5 real shop-purchase locations (a bought item, not
# a found one) -- used both for the new no_abilities_in_shops toggle
# below and available for anything else that needs to reason about
# "which locations are actually a shop transaction".
SHOP_LOCATIONS: list[str] = [
    "Map Icons Pickup", "Health Vial Purchase", "Health Vial Upgrade Purchase",
    "Heart Piece: Janky Junction (Shop)", "Paint Flask: Janky Junction (Shop)",
]


def set_all_rules(world: ConstanceWorld) -> None:
    set_all_location_rules(world)
    set_completion_condition(world)


def set_all_location_rules(world: ConstanceWorld) -> None:
    # Per-category randomize toggles mean a location this
    # dict/logic references might simply not exist this seed (its whole
    # category was disabled). get_location would raise otherwise --
    # this small helper makes every lookup below tolerate that instead of
    # crashing generation.
    def _try_get_location(name: str):
        try:
            return world.get_location(name)
        except KeyError:
            return None

    # Base rule: everything except the Paintbrush altar itself requires
    # having Paintbrush. Safe by construction -- Paintbrush is the only
    # ability still locked directly to its own altar in locations.py,
    # never part of the randomized pool, so it can never end up placed
    # behind a rule that requires it.
    paintbrush_altar = world.get_location("Ability Altar: Paintbrush")
    for location in world.multiworld.get_locations(world.player):
        if location is paintbrush_altar:
            continue
        world.set_rule(location, lambda state: state.has("Paintbrush", world.player))

    # Layered on top: real, detailed per-location requirements from real
    # playtesting/research. The other 6 abilities are genuinely
    # randomized (no live safety net) -- these rules keep generation from
    # ever placing one somewhere that requires it, for locations where
    # that item IS actually load-bearing for the goal (verified safe:
    # all 6 are ItemClassification.progression, and reachability rules
    # work correctly for anything actually on the critical path to the
    # completion condition).
    for location_name, required_items in LOCATION_REQUIREMENTS.items():
        if not required_items:
            continue
        location = _try_get_location(location_name)
        if location is None:
            continue
        # Captured by value (required_items itself, not the loop variable)
        # to avoid the classic late-binding closure bug.
        add_rule(
            location,
            lambda state, items=required_items: all(
                state.has(item, world.player) for item in items
            ),
        )

    # The one location in the whole logic set that needs
    # OR logic rather than AND -- either Wall Dive or Somersault, not
    # both. LOCATION_REQUIREMENTS only expresses AND (every entry above
    # requires ALL its listed items), so this is layered on separately
    # rather than trying to generalize the whole dict format for one
    # location. Direct request, revised: applies unconditionally now,
    # regardless of randomize_abilities -- Wall Dive/Somersault are always
    # trackable via AP whether genuinely randomized or locked-guaranteed
    # to their own altar (see place_locked_guaranteed_abilities), so this
    # rule stays correct and enforceable either way.
    vv_chest_3 = _try_get_location("Vanishing Vaults - Chest 3")
    if vv_chest_3 is not None:
        add_rule(
            vv_chest_3,
            lambda state: state.has(_WD, world.player) or state.has(_SS, world.player),
        )

    # Second genuine OR-logic exception: Eraser: Shop (Two cousins) needs
    # Wall Dive or Paint Stroke, not both. Same pattern as above.
    eraser_shop = _try_get_location("Eraser: Shop (Two cousins)")
    if eraser_shop is not None:
        add_rule(
            eraser_shop,
            lambda state: state.has(_WD, world.player) or state.has(_PST, world.player),
        )

    # Third genuine OR-logic exception, direct request/correction: Heart
    # Piece: Floral Foundry needs Wall Dive or Somersault, not both. Same
    # pattern as above. Replaces an earlier, incorrect requirement for
    # this same location (all 7 abilities) that the direct correction
    # here supersedes.
    heart_floral_foundry = _try_get_location("Heart Piece: Floral Foundry")
    if heart_floral_foundry is not None:
        add_rule(
            heart_floral_foundry,
            lambda state: state.has(_WD, world.player) or state.has(_SS, world.player),
        )

    # Optional toggle (no_abilities_in_shops) -- when on,
    # guarantees none of the 8 abilities can ever be placed at any of the
    # 5 real shop-purchase locations (a bought item, not a found one),
    # to avoid a currency-grind bottleneck early on if an essential
    # ability ends up gated behind a purchase. forbid_items_for_player
    # operates on item PLACEMENT directly during fill, not on location
    # reachability, so it isn't subject to the "only enforced if load-
    # bearing for the goal" gap that made an access_rule-only version
    # ineffective (see the note on the broader, always-on version of this
    # same fix just below, which covers every location, not just shops).
    if world.options.no_abilities_in_shops.value:
        for location_name in SHOP_LOCATIONS:
            location = _try_get_location(location_name)
            if location is None:
                continue
            forbid_items_for_player(location, set(_ALL_8_ABILITIES), world.player)

    # This same
    # gap isn't unique to the 28 locations above -- it applies to EVERY
    # location anywhere in LOCATION_REQUIREMENTS that lists an ability.
    # Concrete case that surfaced it: "Inspiration: Whiplash"
    # requires Paint Stab, Paint Stroke, and Paint Clone to reach -- and
    # Paint Stab itself got placed there, a genuine, permanent self-lock
    # (not a softlock risk, an item that can never be collected, full
    # stop). The access_rule requirement alone never prevented this,
    # since AP's generator only forbids an unreachable placement when
    # that specific item is provably necessary on every possible
    # completion path, which isn't guaranteed here for the same reason as
    # the 28 locations above. Fixed the same way, applied everywhere a
    # requirement exists: whatever ability(ies) a location's own
    # requirement list names, that same location must forbid placing
    # those exact abilities -- eliminating this class of self-lock
    # structurally, for every location, not just the ones already caught.
    #
    # Also includes Frida Mask now -- the updated logic makes it a real
    # requirement for the 4 Puppet Hand / Puppet Master bosses. Frida Mask
    # is classified useful, not progression, so it doesn't get the same
    # strict reachability guarantee abilities do from the generator --
    # exactly the same category of risk, so it gets the same protection,
    # not just the 8 abilities.
    _forbid_pool = set(_ALL_8_ABILITIES) | {_FM}
    for location_name, required_items in LOCATION_REQUIREMENTS.items():
        required_here = set(required_items) & _forbid_pool
        if not required_here:
            continue
        location = _try_get_location(location_name)
        if location is None:
            continue
        forbid_items_for_player(location, required_here, world.player)

    # Same protection for the OR-logic locations above -- conservative but
    # safe: forbidding both means neither can ever be the thing you need
    # itself to open in the first place, regardless of the OR nuance
    # (technically only a problem if the OTHER of the two isn't
    # independently guaranteed reachable, but this costs nothing to apply
    # unconditionally and removes the need to reason about that). Applied
    # unconditionally -- harmless even when Wall Dive/Somersault/Paint
    # Stroke happen to be locked-guaranteed rather than in the shuffled
    # pool, since there's nothing to forbid placing in that case anyway.
    if vv_chest_3 is not None:
        forbid_items_for_player(vv_chest_3, {_WD, _SS}, world.player)
    if eraser_shop is not None:
        forbid_items_for_player(eraser_shop, {_WD, _PST}, world.player)
    if heart_floral_foundry is not None:
        forbid_items_for_player(heart_floral_foundry, {_WD, _SS}, world.player)

    # "Vanishing Vaults - Chest 4" has no requirements at
    # all (reachable with just Paintbrush, per the reference logic) --
    # unconditionally forbidding every ability there avoids a specific,
    # known softlock risk, not a self-lock in the usual sense covered
    # above (this location needs nothing to reach, so nothing here is
    # about "the ability required to open this is inside it"). Always on,
    # not tied to any toggle -- this is a correctness fix, not a
    # preference.
    vv_chest_4 = _try_get_location("Vanishing Vaults - Chest 4")
    if vv_chest_4 is not None:
        forbid_items_for_player(vv_chest_4, set(_ALL_8_ABILITIES), world.player)


def set_completion_condition(world: ConstanceWorld) -> None:
    # Offline/logic completion condition, used during generation for
    # playthrough calculation and seed-completability checks. This is
    # DELIBERATELY SEPARATE from the real, live "you beat the game" signal
    # a real playthrough uses: the actual goal is "Boss: Constance"
    # defeated (the real final boss -- confirmed by it being the only boss
    # with its own dedicated achievement, ps_Achievement_PuppetMasterDefeated,
    # among all 17 boss encounters), which the client (ConstanceLiveClient.py)
    # detects directly from game state and reports via a StatusUpdate/
    # CLIENT_GOAL message -- not through this rule.
    #
    # Updated, per direct request: now requires all 8 abilities in
    # addition to the 4 tears, matching "Boss: Constance"'s own
    # updated location requirement above. This is the real, root-level
    # fix for the self-lock class of bug found in a live seed (Paint Stab
    # landing on a location that required it): once every ability is
    # provably necessary to even reach the goal, AP's own generator logic
    # will never place one somewhere unreachable in the first place --
    # doing so would make the seed itself uncompletable, which the
    # generator already refuses to allow. The forbid_items_for_player
    # calls above remain as a second, structural layer of protection on
    # top of this, not a replacement for it.
    # Updated again: the logic revision now makes Frida Mask a genuine
    # requirement for reaching the Puppet Hand / Puppet Master bosses too
    # -- included here for the same reason the 8 abilities are: the
    # generator only guarantees an item stays reachable when it's
    # provably necessary for completion, and Frida Mask (classified
    # useful, not progression) doesn't get that guarantee automatically
    # otherwise.
    # Abilities/tears/Frida Mask are ALWAYS
    # checked here now, unconditionally -- with the "keep the check,
    # guarantee the vanilla item" design (place_locked_guaranteed_abilities
    # in locations.py), state.has() for these stays meaningful and correct
    # whether an item is genuinely randomized or locked to its own
    # location, so there's no longer any need to special-case based on
    # the toggle here.
    world.set_completion_rule(
        lambda state: (
            state.has("Tear of Floral Foundry", world.player)
            and state.has("Tear of Astral Academy", world.player)
            and state.has("Tear of Chaotic Carnival", world.player)
            and state.has("Tear of Vanishing Vaults", world.player)
            and all(state.has(ability, world.player) for ability in _ALL_8_ABILITIES)
            and state.has(_FM, world.player)
        )
    )
