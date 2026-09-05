from __future__ import annotations

from worlds.AutoWorld import World

from . import items, locations, regions, rules, web_world
from . import options as constance_options


class ConstanceWorld(World):
    """
    Constance is a 2025 hand-drawn metroidvania.
    This is a base/early Archipelago implementation: 17 named checks
    tied to real ability/tear/key-item unlocks, plus 30 curated chest and
    heart-piece checks, all validated against real decompiled game data
    and a real save file. Region logic is intentionally coarse pending
    real playtesting -- see regions.py and rules.py for details.
    """

    game = "Constance"

    web = web_world.ConstanceWebWorld()

    options_dataclass = constance_options.ConstanceOptions
    options: constance_options.ConstanceOptions

    location_name_to_id = locations.LOCATION_NAME_TO_ID
    item_name_to_id = items.ITEM_NAME_TO_ID

    origin_region_name = "Janky Junction"

    def create_regions(self) -> None:
        regions.create_and_connect_regions(self)
        locations.create_all_locations(self)

    def set_rules(self) -> None:
        rules.set_all_rules(self)

    def create_items(self) -> None:
        items.create_all_items(self)

    def create_item(self, name: str) -> items.ConstanceItem:
        return items.create_item_with_correct_classification(self, name)

    def get_filler_item_name(self) -> str:
        return items.get_filler_item_name(self)

    def fill_slot_data(self) -> dict:
        # Read by ConstanceLiveClient.py from the "Connected" packet's
        # slot_data to decide whether to enable the "DeathLink" connection
        # tag. Only the live (mod-based) client supports this -- the
        # file-based client has no live hook into the running game, so
        # there's no way for it to detect a death or force one.
        #
        # The 5 per-category randomize toggles, relayed
        # the same way -- the live client forwards these to the mod over
        # the bridge, so the mod knows at runtime which categories to
        # actually suppress vs leave completely vanilla.
        return {
            "death_link": self.options.death_link.value,
            "randomize_flasks": bool(self.options.randomize_flasks.value),
            "randomize_hearts": bool(self.options.randomize_hearts.value),
            "randomize_erasers": bool(self.options.randomize_erasers.value),
            "randomize_bosses": bool(self.options.randomize_bosses.value),
            "randomize_abilities": bool(self.options.randomize_abilities.value),
            "randomize_tears": bool(self.options.randomize_tears.value),
            "randomize_inspirations": bool(self.options.randomize_inspirations.value),
            "randomize_health_vials": bool(self.options.randomize_health_vials.value),
            "filler_multiplier": int(self.options.filler_multiplier.value),
        }
