# Server source (AGPL-3.0)

The galaxy runs [SWGEmu Core3](https://github.com/swgemu/Core3) (AGPL-3.0), branch `unstable`, commit
`403893d9b9f8e6fa712fbb3cb1fcd8f0fe45bb79`, built unchanged. The only difference from that commit is
`core3-local-changes.diff`: `MMOCoreORB/bin/scripts/managers/resource_manager_spawns.lua`, the resource-spawn table
the server writes as it runs. Server settings such as passwords and addresses live in a separate, untracked
`config-local.lua`. They aren't source code and are not published.
