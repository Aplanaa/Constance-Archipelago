from __future__ import annotations

from typing import TYPE_CHECKING

from BaseClasses import Region

if TYPE_CHECKING:
    from .world import ConstanceWorld

# ---------------------------------------------------------------------------
# Region graph. This is deliberately COARSE for a first base version:
# Constance's real map is a metroidvania with per-room traversal gating that
# would need much more detailed mapping (see scene_persistence_catalog.json
# position data for anyone who wants to refine this later).
#
# EARLIER VERSION HAD A REAL BUG WORTH DOCUMENTING: this used to gate each
# middle region behind a single ability (Academy needs Paint Dive, Carnival
# needs Paint Stab, etc). Real generation testing caught two distinct
# failure modes from that:
#   1. A location-specific rule referencing an item created a direct
#      self-lock (the item ended up placed at the very location that
#      required it).
#   2. Even after fixing (1) with per-region item_rule restrictions, a
#      *cross-region* cycle still appeared: Wall Dive (needed to enter
#      Vaults) got placed inside Academy, while Paint Dive (needed to enter
#      Academy) got placed inside Vaults -- a two-region deadlock that a
#      single region's item_rule can't catch, since neither item was placed
#      in its own gated region.
#
# Rather than keep patching individual cases, the middle regions are now
# fully open from the hub (no item requirement), which makes this whole
# class of bug structurally impossible. The only real gate left is the
# Void, which is safe because it contains zero locations of its own -- there
# is nothing for the required Tears to get stuck behind.
# ---------------------------------------------------------------------------

REGION_NAMES = [
    "Janky Junction",
    "Floral Foundry",
    "Astral Academy",
    "Chaotic Carnival",
    "Painterly Peak",
    "Vanishing Vaults",
    "The Void",
]


def create_and_connect_regions(world: ConstanceWorld) -> None:
    create_all_regions(world)
    connect_regions(world)


def create_all_regions(world: ConstanceWorld) -> None:
    regions = [Region(name, world.player, world.multiworld) for name in REGION_NAMES]
    world.multiworld.regions += regions


def connect_regions(world: ConstanceWorld) -> None:
    junction = world.get_region("Janky Junction")

    # All middle regions are freely accessible from the hub for now.
    # Ability requirements to actually *traverse within* a region towards
    # specific checks would be a reasonable next refinement, but region-level
    # gating is where the cross-region cycle risk lives, so we avoid it here.
    for region_name in (
        "Floral Foundry", "Astral Academy", "Chaotic Carnival",
        "Painterly Peak", "Vanishing Vaults",
    ):
        junction.connect(world.get_region(region_name), f"Junction to {region_name}")

    # The Void is the endgame area, gated behind all four Tears.
    # Safe: The Void has no locations of its own, so the Tears can never
    # end up trapped behind this gate.
    junction.connect(
        world.get_region("The Void"), "Junction to the Void",
        lambda state: (
            state.has("Tear of Floral Foundry", world.player)
            and state.has("Tear of Astral Academy", world.player)
            and state.has("Tear of Chaotic Carnival", world.player)
            and state.has("Tear of Vanishing Vaults", world.player)
        ),
    )
