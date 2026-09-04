from dataclasses import dataclass

from Options import Choice, OptionGroup, PerGameCommonOptions, NamedRange, Range, Toggle

class Deathlink(Choice):
    """
    Whether you want to play with deathlink on
    can always be changed in the client
    unchanged means it wont change the setting in the client upon first connection
    """

    display_name = "Deathlink"

    option_unchanged = -1
    option_off = 0
    option_on = 1
    default = -1

class Deathlink_Amnesty(NamedRange):
    """
    How many deathlinks you need to RECIEVE before you actually die
    unchanged means it wont change the setting in the client upon first connection
    """

    display_name = "Deathlink"
    special_range_names = {
    "unchanged": 0,
    }
    range_start = 0
    range_end = 100
    default = 0

@dataclass
class WKOptions(PerGameCommonOptions):
    deathlink: Deathlink
    deathlink_amnesty: Deathlink_Amnesty

option_groups = [
    OptionGroup(
        "Client_Side",
        [Deathlink, Deathlink_Amnesty],
    ),
]