from dataclasses import dataclass

from Options import PerGameCommonOptions, DeathLink, Toggle, Range, Choice


# RandomizeChests removed entirely -- chests
# can no longer be excluded from randomization at all, always randomized
# now, same treatment as Frida Mask before it.


class RandomizeFlasks(Toggle):
    """Whether Paint Flask locations are randomized through Archipelago.
    If disabled, Paint Flasks work exactly like vanilla"""
    display_name = "Randomize Paint Flasks"
    default = 1


class RandomizeHearts(Toggle):
    """Whether Heart Piece locations are randomized through Archipelago.
    If disabled, Heart Pieces work exactly like vanilla."""
    display_name = "Randomize Heart Pieces"
    default = 1


class RandomizeErasers(Toggle):
    """Whether Eraser locations are randomized through Archipelago.
    If disabled, Erasers work exactly like vanilla"""
    display_name = "Randomize Erasers"
    default = 1


class RandomizeBosses(Toggle):
    """Whether defeating bosses is tracked as an Archipelago check.
    If disabled, bosses can still be fought and defeated as normal, but
    doing so has no effect on Archipelago"""
    display_name = "Bosses Checks"
    default = 1


class RandomizeAbilities(Toggle):
    """Whether Paint Stroke, Plunge, Wall Dive, Somersault, and Paint Clone
    are randomized through Archipelago. Paintbrush and Paint Dive are never
    affected by this, both are always guaranteed at their own altars
    regardless. Paint Stab has its own separate toggle rather than 
    being part of this group. If disabled, these 5 altars just grant 
    their ability directly and normally"""
    display_name = "Randomize Abilities"
    default = 1


class RandomizePaintStab(Toggle):
    """Whether Paint Stab specifically is randomized through Archipelago.
    Paint Stab is required to reach almost everything else in this world's own
    logic, so in a multiworld it can end up placed deep in another
    player's game with little to do to speed it along. Disabling this
    keeps its own altar as a real check with Paint Stab."""
    display_name = "Randomize Paint Stab"
    default = 0


class RandomizeTears(Toggle):
    """Whether the 4 Tear locations are randomized through Archipelago.
    If disabled, tears work exactly like vanilla"""
    display_name = "Randomize Tears"
    default = 1


class RandomizeInspirations(Toggle):
    """Whether the 16 Inspiration locations are randomized through
    Archipelago. If disabled, they work exactly like vanilla"""
    display_name = "Randomize Inspirations"
    default = 1


class RandomizeHealthVials(Toggle):
    """Whether the Health Vials purchases are randomized through Archipelago. 
    If disabled, they work exactly like vanilla"""
    display_name = "Randomize Health Vials"
    default = 1


class FillerMultiplier(Choice):
    """Multiplies the amount of filler given. In x1 the default is: 
    Handful of Glimmer: 20 to 100 and Handful of Lightstones: 15 to 40"""
    display_name = "Filler Amount Multiplier"
    option_x1 = 1
    option_x2 = 2
    option_x3 = 3
    option_x5 = 5
    default = 1


class NoAbilitiesInShops(Toggle):
    """If enabled, none of the abilities will
    ever be placed at one of the shop-purchase locations. 
    Avoids a glimmer grind bottleneck early on if an essential
    ability ends up gated behind a purchase you can't yet afford."""
    display_name = "No Abilities In Shops"
    default = 1


class TrapPercentage(Range):
    """What percentage of filler items (the ones that would otherwise be
    Handful of Glimmer/Lightstones) are replaced with traps instead. 0
    means no traps at all; 100 means every filler item is a trap."""
    display_name = "Trap Percentage"
    range_start = 0
    range_end = 100
    default = 30


class OneHitTrapWeight(Range):
    """Relative weight for the "1 Hit Constance" trap (sets current
    health to 1) among traps, when Trap Percentage is above 0. Only the
    relative proportion between the three trap weights matters. E.g.
    1/1/1 is an even split, 2/1/1 makes this trap twice as likely as
    either of the other two."""
    display_name = "1 Hit Constance Trap Weight"
    range_start = 0
    range_end = 100
    default = 1


class CheckpointTrapWeight(Range):
    """Relative weight for the Checkpoint Trap (respawns the player at
    their last visited shrine)
    among traps, when Trap Percentage is above 0."""
    display_name = "Checkpoint Trap Weight"
    range_start = 0
    range_end = 100
    default = 1


class PerseveranceTrapWeight(Range):
    """Relative weight for the Perseverance Trap (applies the Perseverance
    effect the real game gives for choosing to
    Persevere at the death screen) among traps, when Trap Percentage is
    above 0."""
    display_name = "Perseverance Trap Weight"
    range_start = 0
    range_end = 100
    default = 1


@dataclass
class ConstanceOptions(PerGameCommonOptions):
    death_link: DeathLink
    randomize_flasks: RandomizeFlasks
    randomize_hearts: RandomizeHearts
    randomize_erasers: RandomizeErasers
    randomize_bosses: RandomizeBosses
    randomize_abilities: RandomizeAbilities
    randomize_paint_stab: RandomizePaintStab
    randomize_tears: RandomizeTears
    randomize_inspirations: RandomizeInspirations
    randomize_health_vials: RandomizeHealthVials
    filler_multiplier: FillerMultiplier
    no_abilities_in_shops: NoAbilitiesInShops
    trap_percentage: TrapPercentage
    one_hit_trap_weight: OneHitTrapWeight
    checkpoint_trap_weight: CheckpointTrapWeight
    perseverance_trap_weight: PerseveranceTrapWeight
