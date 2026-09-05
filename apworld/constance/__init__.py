# Registers the Constance client as a component launchable from the
# Archipelago Launcher -- this is what lets it run without a separate
# Python install, using the frozen build's bundled interpreter.
from . import components as components

from .world import ConstanceWorld as ConstanceWorld
