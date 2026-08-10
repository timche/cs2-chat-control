# ChatControl

A [CounterStrikeSharp](https://docs.cssharp.dev/) plugin for CS2 that exposes
server control through chat: change the map, run a server command, and fire
config-defined batches of commands ("presets").

The behaviour of `.map`, `.rcon` and the everyone-is-admin switch is modelled on
[MatchZy](https://github.com/shobhit-pathak/MatchZy) (MIT), so servers running
both feel the same. This is a reimplementation against the CounterStrikeSharp
API, not copied code.

## Requirements

- Counter-Strike 2 dedicated server
- Metamod:Source
- CounterStrikeSharp >= 1.0.371

## Commands

Every command works with three chat triggers — `.map`, `!map`, `/map`. The `.`
trigger comes from this plugin's own chat listener; `!` and `/` are
CounterStrikeSharp's built-in triggers for registered commands. From the server
console or RCON, use the `css_` name (`css_map de_dust2`).

| Chat | Console | Permission | Description |
| --- | --- | --- | --- |
| `.map <name>` | `css_map <name>` | `@css/map` | Change level. A name without an underscore gets the `de_` prefix, so `.map dust2` means `de_dust2`. |
| `.map <workshop id>` | `css_map <workshop id>` | `@css/map` | Load a workshop map by numeric ID (`host_workshop_map`). |
| `.map <workshop URL>` | `css_map <workshop URL>` | `@css/map` | Same, with the ID parsed out of a `steamcommunity.com` URL. |
| `.rcon <command>` | `css_rcon <command>` | `@css/rcon` | Run any server command. Unfiltered — access control is the only gate. |
| `.<preset>` | `css_<preset>` | `@css/config` | Run the commands configured under that preset name. |

Preset commands are registered from the config, so a preset named `aim` gives
you `.aim`, `!aim`, `/aim` and `css_aim`. The names `map` and `rcon` are
reserved, and preset names may only contain `a-z`, `0-9` and `_`.

## Permissions

Grant flags in CounterStrikeSharp's
`addons/counterstrikesharp/configs/admins.json`. `@css/root` satisfies every
`@css/*` check, so a single root entry covers all three commands. The server
console and RCON always pass.

### `chatcontrol_everyone_is_admin`

A convar (default `false`) that bypasses the permission checks on this plugin's
commands. It can only be changed from the server console or RCON, not from chat.

> **Warning:** `chatcontrol_everyone_is_admin 1` combined with `.rcon` hands the
> server console to every connected player. Only enable it on private or
> passworded servers.

## Configuration

CounterStrikeSharp generates and loads
`addons/counterstrikesharp/configs/plugins/ChatControl/ChatControl.json` on
first load. Both sections default to empty: no allowlist (any valid map) and no
presets.

```json
{
  "AllowedMaps": [
    "de_ancient",
    "de_anubis",
    "de_cache",
    "de_dust2",
    "de_inferno",
    "de_mirage",
    "de_nuke",
    "de_overpass",
    "de_thera",
    "de_train",
    "de_vertigo"
  ],
  "Presets": {
    "aim": [
      "mp_freezetime 1",
      "mp_maxrounds 30",
      "mp_buy_anywhere 1",
      "mp_startmoney 16000",
      "mp_respawn_immunitytime 0",
      "mp_warmup_end"
    ],
    "aimpistols": [
      "mp_freezetime 1",
      "mp_maxrounds 30",
      "mp_ct_default_primary \"\"",
      "mp_t_default_primary \"\"",
      "mp_ct_default_secondary weapon_usp_silencer",
      "mp_t_default_secondary weapon_glock",
      "mp_warmup_end"
    ]
  },
  "ConfigVersion": 1
}
```

Both sections above are **examples to adapt**, not recommended values:

- `AllowedMaps` — when non-empty, `.map` accepts only these entries. Map names
  are matched case-insensitively *after* the `de_` prefix is applied; workshop
  maps are matched against the bare numeric ID, so add the ID string to the list
  to allow one. The list shown is a plausible retakes rotation. Leave it empty
  to allow any map the server considers valid.
- `Presets` — preset name to server commands, executed in order. The convars
  listed are a plausible aim-map setup; use whatever your server actually needs.
  A leading `.` on the key is stripped, and keys are lowercased.

Presets are re-registered whenever the config is parsed, so editing the file and
reloading the plugin picks up changes without duplicating commands.

## Build

No local .NET install needed — build inside the SDK image:

```bash
docker run --rm -u 1000:1000 -e DOTNET_CLI_HOME=/tmp -e XDG_DATA_HOME=/tmp \
  -v "$PWD:/src" -w /src/ChatControl \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish -c Release -o dist
```

CounterStrikeSharp.API 1.0.371 ships a `net10.0` assembly, so the plugin targets
`net10.0` and needs the .NET 10 SDK image to build.

## Install

Create `game/csgo/addons/counterstrikesharp/plugins/ChatControl/` on the server
and copy **only** these two files from `ChatControl/dist`:

- `ChatControl.dll`
- `ChatControl.deps.json`

Do not deploy `CounterStrikeSharp.API.dll` — the server provides it, and a
second copy breaks plugin loading. The build is configured not to emit it.

Restart the server (or `css_plugins load ChatControl`), then edit the generated
config and `css_plugins reload ChatControl`.
