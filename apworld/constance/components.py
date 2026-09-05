from worlds.LauncherComponents import Component, Type, components, launch


def run_live_client(*args: str) -> None:
    from .client.launch import launch_constance_live_client

    launch(launch_constance_live_client, name="Constance Client", args=args)


components.append(
    Component(
        "Constance Client",
        func=run_live_client,
        game_name="Constance",
        component_type=Type.CLIENT,
        supports_uri=False,  # not tested yet -- see CLIENT_README.md
    )
)
