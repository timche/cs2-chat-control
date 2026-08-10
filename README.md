# ChatControl

A [CounterStrikeSharp](https://docs.cssharp.dev/) plugin for CS2 that exposes
server control through chat: change the map, run a server command, and fire
config-defined batches of commands ("presets").

The `map`/`rcon` behaviour and the everyone-is-admin switch are modelled on
[MatchZy](https://github.com/shobhit-pathak/MatchZy) — see
[Acknowledgements](#acknowledgements).

## Requirements

- Counter-Strike 2 dedicated server
- Metamod:Source
- CounterStrikeSharp >= 1.0.371

## Install

1. Download `ChatControl-<version>.zip` from the
   [latest release](https://github.com/timche/cs2-chat-control/releases/latest).
2. Extract it into `game/csgo/addons/counterstrikesharp/plugins/` — the zip
   contains a `ChatControl/` folder with everything needed.
3. Restart the server (or run `css_plugins load ChatControl` in the server
   console).

On first load the plugin generates its config file; see
[Configuration](#configuration) to set up map allowlists and presets, then
apply changes with `css_plugins reload ChatControl`.

## Commands

Every command works with two chat triggers — `/map` (silent, the message is
hidden from chat) and `!map` (public). Both are CounterStrikeSharp's built-in
triggers for every registered command and are always available. From the server
console or RCON, use the `css_` name (`css_map de_dust2`).

`ChatPrefix` in the config can *add* a third, plugin-handled trigger, such as
`.` for MatchZy-style dot commands or `$`. The default (`/`) deliberately adds
none, so ChatControl does not clash with plugins that own the `.` namespace —
MatchZy above all. See [Configuration](#configuration).

| Chat | Console | Permission | Description |
| --- | --- | --- | --- |
| `/map <name>` | `css_map <name>` | `@css/map` | Change level. A name without an underscore gets the `de_` prefix, so `/map dust2` means `de_dust2`. |
| `/map <workshop id>` | `css_map <workshop id>` | `@css/map` | Load a workshop map by numeric ID (`host_workshop_map`). |
| `/map <workshop URL>` | `css_map <workshop URL>` | `@css/map` | Same, with the ID parsed out of a `steamcommunity.com` URL. |
| `/rcon <command>` | `css_rcon <command>` | `@css/rcon` | Run any server command. Unfiltered — access control is the only gate. |
| `/<preset>` | `css_<preset>` | `@css/config` | Run the commands configured under that preset name. |

The table shows the default names. `map` and `rcon` are themselves configurable
through `MapCommandName` and `RconCommandName`, so `"MapCommandName": "wmap"`
turns the first three rows into `/wmap`, `!wmap` and `css_wmap`; an empty name
switches the command off entirely.

Preset commands are registered from the config, so a preset named `aim` gives
you `/aim`, `!aim` and `css_aim`. The names `map` and `rcon` are reserved (along
with the configured command names, if renamed), and preset names may only
contain `a-z`, `0-9` and `_`.

## Permissions

Grant flags in CounterStrikeSharp's
`addons/counterstrikesharp/configs/admins.json`. `@css/root` satisfies every
`@css/*` check, so a single root entry covers all three commands. The server
console and RCON always pass.

### `chatcontrol_everyone_is_admin`

A convar (default `false`) that bypasses the permission checks on this plugin's
commands. It can only be changed from the server console or RCON, not from chat.

> **Warning:** `chatcontrol_everyone_is_admin 1` combined with `/rcon` hands the
> server console to every connected player. Only enable it on private or
> passworded servers.

## Configuration

CounterStrikeSharp generates and loads
`addons/counterstrikesharp/configs/plugins/ChatControl/ChatControl.json` on
first load. `AllowedMaps` defaults to empty, so any valid map is accepted. The
plugin ships with two ready-to-use presets, `aim` and `aimpistol`: they appear
in the generated config and can be edited or removed there like any other
preset.

### Example

```json
{
  "ChatPrefix": "/",
  "MapCommandName": "map",
  "RconCommandName": "rcon",
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
      "mp_maxrounds 9999",
      "mp_freezetime 0",
      "mp_free_armor 2",
      "mp_death_drop_gun 1",
      "mp_match_restart_delay 2",
      "mp_limitteams 0",
      "mp_warmup_end",
      "mp_restartgame 1"
    ],
    "aimpistol": [
      "mp_maxrounds 9999",
      "mp_freezetime 0",
      "mp_free_armor 1",
      "mp_death_drop_gun 1",
      "mp_match_restart_delay 2",
      "mp_limitteams 0",
      "mp_warmup_end",
      "mp_restartgame 1"
    ]
  },
  "ConfigVersion": 1
}
```

The `AllowedMaps` list above is an **example to adapt**, not a recommended
value:

- `ChatPrefix` — an extra chat trigger handled by this plugin, on top of the
  built-in `/` and `!`. The default `"/"` (or `"!"`) means *built-in triggers
  only*: no extra trigger is added, which keeps ChatControl clear of other
  plugins' chat commands — MatchZy's `.ready` and `.map`, for instance. Set it to
  `"."` for MatchZy-style dot commands, or to any other symbol such as `"$"`,
  which gives you `$map`, `$rcon` and `$<preset>`. Values that mix `!` or `/`
  with other characters (`"//"`, `"!x"`), contain whitespace, or are empty log a
  warning and fall back to `"/"`. `/` and `!` keep working whatever you set here.
- `MapCommandName` / `RconCommandName` — the names these two commands are
  registered under, defaults `"map"` and `"rcon"`. Renaming moves every trigger
  at once, so `"MapCommandName": "wmap"` gives `/wmap`, `!wmap` and `css_wmap` —
  which is how you keep ChatControl's map command on a server where another
  plugin already owns `css_map`, see
  [Running alongside MatchZy](#running-alongside-matchzy). An empty string
  disables the command: it is not registered at all, so nothing answers it.
  Names may only contain `a-z`, `0-9` and `_`; anything else logs a warning and
  falls back to the default name, as does an `RconCommandName` that collides with
  the map command's.
- `AllowedMaps` — when non-empty, `/map` accepts only these entries. Map names
  are matched case-insensitively *after* the `de_` prefix is applied; workshop
  maps are matched against the bare numeric ID, so add the ID string to the list
  to allow one. The list shown is a plausible retakes rotation. Leave it empty
  to allow any map the server considers valid.
- `Presets` — preset name to server commands, executed in order. `aim` and
  `aimpistol` ship as defaults; edit them, or delete them and add your own. A
  leading `.` on the key is stripped, and keys are lowercased.

All commands — map, rcon and presets — are registered from the config and
re-registered whenever it is parsed, so editing the file and reloading the plugin
picks up changes without duplicating commands.

## Running alongside MatchZy

MatchZy provides `map` and `rcon` too, and CounterStrikeSharp dispatches a shared
command name (`css_map`) to *every* plugin that registered it. Running both
plugins stock therefore means `/map` and `!map` each execute twice — once per
plugin. For `rcon` that is not just noise: the server command runs twice, which
is genuinely harmful for anything that isn't idempotent.

The fix is to rename rather than to disable. ChatControl's map command
understands workshop URLs, which MatchZy's does not, so it is worth keeping:

- `"MapCommandName": "wmap"` — ChatControl registers `css_wmap`, MatchZy keeps
  `css_map`. `/map` and `!map` stay MatchZy's; `/wmap <workshop URL>` is
  ChatControl's, with no shared name and nothing running twice.
- `"RconCommandName": ""` — disables ChatControl's rcon entirely. MatchZy's
  `.rcon` already does the same job, and two rcon commands under one name would
  send every server command twice.

The default `ChatPrefix` leaves MatchZy's `.` namespace untouched, so preset
names only collide if you set `ChatPrefix` to `"."`; if you do and a preset
shares a name with a MatchZy dot-command — a preset named `ready`, say — pick
another prefix such as `$`.

## Building from source

No local .NET install needed — build inside the SDK image:

```bash
docker run --rm -u 1000:1000 -e DOTNET_CLI_HOME=/tmp -e XDG_DATA_HOME=/tmp \
  -v "$PWD:/src" -w /src/ChatControl \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish -c Release -o dist
```

CounterStrikeSharp.API 1.0.371 ships a `net10.0` assembly, so the plugin targets
`net10.0` and needs the .NET 10 SDK image to build.

To install a source build, copy **only** `ChatControl.dll` and
`ChatControl.deps.json` from `ChatControl/dist` into
`game/csgo/addons/counterstrikesharp/plugins/ChatControl/`. Do not deploy
`CounterStrikeSharp.API.dll` — the server provides it, and a second copy breaks
plugin loading. The build is configured not to emit it.

## Acknowledgements

ChatControl's `map` and `rcon` behaviour and its everyone-is-admin switch are
modelled on [MatchZy](https://github.com/shobhit-pathak/MatchZy) by
[shobhit-pathak](https://github.com/shobhit-pathak) (MIT licensed), so servers
running both feel the same. This is a reimplementation against the
CounterStrikeSharp API, not copied code.

Thanks to the MatchZy authors for the plugin and for setting the conventions this
one follows.
