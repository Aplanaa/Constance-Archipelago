from BaseClasses import Tutorial
from worlds.AutoWorld import WebWorld


class ConstanceWebWorld(WebWorld):
    game = "Constance"

    theme = "grassFlowers"

    setup_en = Tutorial(
        "Multiworld Setup Guide",
        "A guide to setting up Constance for MultiWorld.",
        "English",
        "setup_en.md",
        "setup/en",
        ["community"],
    )

    tutorials = [setup_en]
