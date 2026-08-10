# Plan: `.map` / `.rcon` / everyone-admin plugin for the retakes server

Handover document. Written 2026-08-10, decisions filled in after review.
Everything below was verified on this machine unless explicitly marked
**UNVERIFIED**.

---

## 1. Existing environment

Two CS2 servers in `/home/cs2/cs2-servers`, one shared `docker-compose.yml` +
`pre.sh`, parameterised per instance by an env file:

| Instance | Env file | Port | Password | Rcon | Plugin stack |
|---|---|---|---|---|---|
| `cs2-pcw` | `pcw.env` | 27015 | `pcw` | `fcbayern` | MatchZy 0.8.15 |
| `cs2-retakes` | `retakes.env` | 27016 | `retakes` | `fcbayern` | cs2-retakes 3.1.0, cs2-instadefuse 2.0.0, RetakesAllocator 2.0.0 |

Run with `docker compose --env-file <file> up -d` (each env file sets
`COMPOSE_PROJECT_NAME`, so they are independent compose projects).

Both run Metamod `2.0.0-git1410` and **CounterStrikeSharp 1.0.371**.
Data dirs: `data/pcw`, `data/retakes` (~66 GiB each). Container runs as uid 1000;
host dirs are owned `1000:1001` with setgid, server runs `umask 002`.

`pre.sh` installs Metamod + CSSharp + the per-instance plugin set on every boot,
keyed off version markers in `<data>/.plugins/*.version`. It is idempotent.

---

## 2. Goal — DECIDED

Build a CounterStrikeSharp plugin for the **retakes server** providing:

1. `.map <mapname>` — change level from chat.
2. `.rcon <command…>` — run a server command from chat.
3. A MatchZy-style everyone-is-admin switch.

The prebuilt `cs2-fake-rcon` alternative was considered and **not chosen** (kept in
section 10 as a fallback only).

---

## 3. Permission model — DECIDED, and verified against v1.0.371

Both mechanisms the user asked about are available, and they compose cleanly.

### 3.1 Named admins via CounterStrikeSharp

Standard CSSharp: SteamID64 → flags in
`<data>/game/csgo/addons/counterstrikesharp/configs/admins.json`, with groups in
`admin_groups.json`. Grant `@css/root`. Format is in the shipped
`admins.example.json`.

`@css/root` is special-cased: `AdminData.DomainHasRootFlag()` treats
`@<domain>/root` or `@<domain>/*` as granting the whole domain, so `@css/root`
satisfies every `@css/*` check. Verified in `AdminPermissions.cs:40-46`.

Reload at runtime without restarting — CSSharp registers `css_admins_reload`,
`css_groups_reload`, `css_admins_list` (each requires `@css/generic`). Verified in
`AdminManager.AddCommands()`.

**This admin file works for MatchZy too**, so both servers can share one admin model.
MatchZy's `IsPlayerAdmin` (`Utility.cs:124`) resolves in this order:

```csharp
if (everyoneIsAdmin.Value) return true;                       // matchzy_everyone_is_admin
string[] updatedPermissions = permissions.Concat(new[] { "@css/root" }).ToArray();
RequiresPermissionsOr attr = new(updatedPermissions) { Command = command };
if (attr.CanExecuteCommand(player)) return true;              // CSSharp admins.json
if (player == null) return true;                              // server console
if (loadedAdmins.ContainsKey(player.SteamID.ToString())) return true;  // MatchZy admins.json
return false;
```

Note it appends `@css/root` to whatever specific flag a command needs — so a single
`@css/root` entry in CSSharp's `admins.json` covers every MatchZy command as well as
everything on the retakes side.

### 3.2 Everyone-is-admin, MatchZy style

**This is a real global grant, not a plugin-local flag.** CSSharp exposes a public
runtime API, confirmed present at tag `v1.0.371` in
`managed/CounterStrikeSharp.API/Modules/Admin/AdminPermissions.cs:382`:

```csharp
public static void AddPlayerPermissions(CCSPlayerController? player, params string[] flags)
public static void AddPlayerPermissions(SteamID? steamId, params string[] flags)
public static void ClearPlayerPermissions(CCSPlayerController? player)
```

Its own doc comment: *"Temporarily adds a permission flag to the player. These flags
are **not saved** to configs/admins.json."* So the plugin hooks player connect and
grants `@css/root` in memory, re-applied every connect. Nothing on disk changes.

**Why this beats a plugin-local flag:** the grant is global to CSSharp, so it also
unlocks **cs2-retakes' own 27 admin commands** — `css_scramble`, `css_forcebombsite`,
and the whole spawn editor (`!addspawn`, `!removespawn`, …), which are gated behind
`@css/root` and are otherwise unreachable. That removes the separate outstanding
task of hand-populating `admins.json` just to edit spawns.

### 3.3 Resulting design

One convar, MatchZy-style, so it can be flipped live:

```
repeek_everyone_is_admin  true|false     (default: true)
```

- `true`  → on connect, `AdminManager.AddPlayerPermissions(player, "@css/root")`.
- `false` → falls through to normal `admins.json`.

Mirror MatchZy's flag convention so the two servers stay consistent — verified from
its call sites: `css_map` → `@css/map`, `css_rcon` → `@css/rcon` (others it uses:
`@css/chat` for `css_asay`, `@css/config` for match commands). Since `@css/root`
covers the whole `@css/*` domain (3.1), a root grant satisfies both.

MatchZy already implements `.map` and `.rcon` on the PCW server — this plugin is
essentially porting those two commands to the retakes server, which has no equivalent.

### 3.4 Security note — state once, then proceed

With `repeek_everyone_is_admin true`, any player who joins gets `@css/root` and can
run `.rcon <anything>`: `quit`, `sv_cheats 1`, `sv_password …`. This is a wider grant
than MatchZy's, whose everyone-admin only covers MatchZy's own match commands.

The server has a join password (`retakes`), so this is a legitimate choice for a
private server, and it is what was asked for. Just don't remove the join password
while this is on. If it ever needs tightening, set `repeek_everyone_is_admin false`
and add real admins per 3.1 — no rebuild required.

---

## 4. Prerequisites (all verified)

- **MatchZy is MIT licensed** → reusing its behaviour is fine; preserve attribution.
  Reimplement against the CSSharp API rather than copying MatchZy source, to avoid
  pulling in its match-state machinery.
- **`CounterStrikeSharp.API` 1.0.371 is on NuGet** — matches the runtime exactly.
  Do not build against a different version; see gotcha 7.1.
- **`mcr.microsoft.com/dotnet/sdk:8.0` is already pulled locally.** There is no
  .NET SDK on the host — build inside the container.

---

## 5. Build

Suggested source location (project dir, not the data dir, so it survives a reseed):
`/home/cs2/cs2-servers/plugins/src/RepeekTools/`

```bash
docker run --rm -v /home/cs2/cs2-servers/plugins/src:/src -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c 'dotnet new classlib -n RepeekTools -f net8.0 && cd RepeekTools &&
           dotnet add package CounterStrikeSharp.API --version 1.0.371 &&
           dotnet build -c Release'
```

Build output lands in `bin/Release/net8.0/`. Deploy **only** `RepeekTools.dll` (plus
`RepeekTools.deps.json` if present) — do **not** ship `CounterStrikeSharp.API.dll`,
which the runtime already provides.

The container writes as root; `chown 1000:1001` the outputs afterwards (gotcha 7.3).

### Source sketch — **UNVERIFIED**, confirm against the 1.0.371 API

`AdminManager.AddPlayerPermissions` / `PlayerHasPermissions` signatures *are*
verified (section 3.2). The rest — attribute names, `BasePlugin` members, event
hook shape, convar registration — is idiomatic CSSharp but was **not compiled or
run** in this session. Check <https://docs.cssharp.dev> before trusting it.

```csharp
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

// .map / .rcon / everyone-is-admin behaviour modelled on MatchZy (MIT,
// https://github.com/shobhit-pathak/MatchZy) — reimplemented, not copied.
public class RepeekTools : BasePlugin
{
    public override string ModuleName => "RepeekTools";
    public override string ModuleVersion => "1.0.0";

    private bool _everyoneIsAdmin = true;   // back this with the convar in 3.3

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
        {
            var player = @event.Userid;
            if (_everyoneIsAdmin && player != null && player.IsValid && !player.IsBot)
                AdminManager.AddPlayerPermissions(player, "@css/root");
            return HookResult.Continue;
        });

        // On hot reload, players are already connected — grant to everyone present.
        if (hotReload && _everyoneIsAdmin)
            foreach (var p in Utilities.GetPlayers())
                if (p.IsValid && !p.IsBot) AdminManager.AddPlayerPermissions(p, "@css/root");
    }

    // NOTE: do NOT use the [RequiresPermissions] attribute here — see gotcha 7.8.
    // Check inside the handler, exactly as MatchZy's IsPlayerAdmin does.
    private bool IsPlayerAdmin(CCSPlayerController? player, string command, params string[] permissions)
    {
        if (_everyoneIsAdmin) return true;
        if (player == null) return true;   // server console
        var attr = new RequiresPermissionsOr(permissions.Concat(new[] { "@css/root" }).ToArray())
                   { Command = command };
        return attr.CanExecuteCommand(player);
    }

    [ConsoleCommand("css_map", "Change the map")]
    public void OnMap(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsPlayerAdmin(player, "css_map", "@css/map")) { info.ReplyToCommand("No access."); return; }
        if (info.ArgCount < 2) { info.ReplyToCommand("Usage: .map <mapname>"); return; }
        var map = info.GetArg(1);
        if (!AllowedMaps.Contains(map)) { info.ReplyToCommand($"No retakes spawns for {map}."); return; }
        Server.ExecuteCommand($"changelevel {map}");   // workshop maps need host_workshop_map <id>
    }

    [ConsoleCommand("css_rcon", "Run a server command")]
    public void OnRcon(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsPlayerAdmin(player, "css_rcon", "@css/rcon")) { info.ReplyToCommand("No access."); return; }
        if (info.ArgCount < 2) { info.ReplyToCommand("Usage: .rcon <command>"); return; }
        Server.ExecuteCommand(info.ArgString);   // strip the leading "css_rcon " first
    }
}
```

Notes for whoever builds this:

- `AddPlayerPermissions(CCSPlayerController)` resolves `player.AuthorizedSteamID` and
  **returns silently** if the player isn't `Connected`, or is a bot/HLTV. Verified at
  `AdminPermissions.cs:382-388`. Hook `EventPlayerConnectFull` rather than an earlier
  connect event, or Steam auth may not have completed and the grant is a no-op.
- `info.ArgString` includes the whole argument string — verify whether it contains the
  command name and strip it if so.
- Validate `.map` against the 11-map list in section 8; otherwise a typo strands the
  server on a map with no retakes spawns.

---

## 6. Deploy

### 6.1 Chat trigger — needed for the `.` prefix

CSSharp binds `!map` and `/map` to `css_map` automatically. MatchZy's `.` prefix comes
from its own chat handling, which this plugin will not have. To get `.map`, add `"."`
to `PublicChatTrigger` in
`data/retakes/game/csgo/addons/counterstrikesharp/configs/core.json`:

```json
"PublicChatTrigger": [ "!", "." ],
```

Current value on the retakes server is `[ "!" ]`. **UNVERIFIED** that CSSharp accepts
`.` as a trigger — confirm, and fall back to `!map` / `!rcon` if not.

### 6.2 Install path

`data/retakes/game/csgo/addons/counterstrikesharp/plugins/RepeekTools/RepeekTools.dll`

### 6.3 Wire into `pre.sh` (do this, don't hand-copy)

Everything else on these servers is installed declaratively by `pre.sh` so it survives
a data-dir reseed. Same pattern here: keep the built DLL in the project dir (e.g.
`plugins/dist/RepeekTools/`), mount that dir into the container alongside `pre.sh` in
`docker-compose.yml`, and add a `p_install_repeektools()` that copies it into place
when a version marker changes. Add the call to the `retakes)` branch of the `case` at
the bottom of `pre.sh`.

Model it on the existing `p_install_*` functions — they all use `p_needs_install` plus
a `<data>/.plugins/<name>.version` marker.

### 6.4 Apply

```bash
cd /home/cs2/cs2-servers
docker compose --env-file retakes.env up -d --force-recreate cs2
```

---

## 7. Gotchas learned the hard way this session

1. **CSSharp must track the CS2 game build, not the plugin.** `counterstrikesharp.so`
   is a native loader compiled against the server binary. Using MatchZy's
   `-with-cssharp` bundle (vendoring a 2025-10-16 build) against the 2026-08-10 game
   binary failed with `undefined symbol: g_bUpdateStringTokenDatabase`. `pre.sh` now
   always installs standalone CSSharp plus the plain plugin zip.
2. **CSSharp's `configs/` ships only `*.example.json`.** It bootstraps `core.json`
   from `core.example.json` on first run. Excluding `configs/` from a *fresh*
   extraction causes `CoreConfig file not found` and `Loaded 0 plugins`. `pre.sh` only
   holds `configs/` back when the directory already exists.
3. **Container is uid 1000 and cannot be changed** (its steamcmd install is owned by
   1000). Files created by the host user (1001) or by a root container must be
   `chown 1000:1001`. The Edit tool rewrites inodes, so re-check ownership after
   editing anything inside `data/`.
4. **Workshop maps reject some cfg commands.** Logs show
   `DISALLOWED WORKSHOP COMMANDS: mp_warmup_start` plus disallowed convars
   (`sv_password`, `tv_*`, `rcon_password`, …); an offending cfg is reported as
   `contains invalid commands`. Relevant if the plugin execs cfgs.
5. **MatchZy upgrades replace `cfg/MatchZy/config.cfg`.** `pre.sh` re-applies the
   wanted convars every boot from the `P_MZ_CONVARS` array — put persistent settings
   there, not in hand edits.
6. Retakes' own `RetakesPlugin.json` is regenerated by the plugin on load;
   `p_apply_retakes_config()` re-applies `EnableFallbackAllocation=false` each boot
   (needed because a standalone allocator is installed).
7. Runtime permission grants are **in memory only** and vanish on map change or
   restart — that is why the grant hangs off a connect event rather than running once
   at load.
8. **Do not gate these commands with the `[RequiresPermissions]` attribute.** CSSharp
   evaluates the attribute *before* the handler runs, so an everyone-is-admin flag
   checked inside the handler would never be reached and the command would be denied.
   MatchZy sidesteps this by using no attribute and constructing
   `RequiresPermissionsOr(...).CanExecuteCommand(player)` manually inside the handler
   (`Utility.cs:124-135`). Do the same. (The attribute *would* work if relying purely
   on the 3.2 grant, but the belt-and-braces version survives the grant failing —
   e.g. a player whose `AuthorizedSteamID` wasn't ready.)

---

## 8. Verification

```bash
# plugin loaded?
docker logs cs2-retakes 2>&1 | tr -d '\r' | sed 's/\x1b\[[0-9;]*m//g' \
  | grep -iE "Finished loading plugin|Failed to load|undefined symbol"
```

Expect **four** `Finished loading plugin` lines once RepeekTools is added
(Instadefuse, Retakes, Weapons Allocator, RepeekTools).

Functional checks, in this order:

1. Join, type `!map` with no argument → usage message (proves the command registered
   and the permission grant landed).
2. `.map` with no argument → proves the `.` trigger works (6.1).
3. `!map de_mirage` → map changes.
4. `!rcon mp_freezetime` → prints the value.
5. Set `repeek_everyone_is_admin false`, reconnect, retry → should now be denied.
   This is the check that proves the switch actually gates anything.

To run server commands from the host without joining, attach to the console:

```bash
docker attach --sig-proxy=false cs2-retakes    # then type e.g.: mp_freezetime
```

Detach with `Ctrl-P Ctrl-Q`. Plain `Ctrl-C` kills the server.

(RCON also works on both ports with password `fcbayern` if you bring your own
client — `rcon_password` + `rcon <cmd>` from an in-game console is the usual route.)

Retakes ships spawn configs for these 11 maps only; anything else has no spawns:

```
de_ancient  de_ancient_night  de_anubis  de_cache  de_dust2  de_inferno
de_mirage   de_nuke  de_overpass  de_train  de_vertigo
```

---

## 9. Rollback

Delete `addons/counterstrikesharp/plugins/RepeekTools/`, revert the `core.json`
trigger change, remove the `pre.sh` call, recreate the container. Nothing else on
either server depends on this plugin. Runtime permission grants disappear on
restart by themselves.

---

## 10. Fallback, only if the plugin proves not worth it

`Salvatore-Als/cs2-fake-rcon` 1.3.0 — a **Metamod** plugin (not CSSharp), needing no
admin system at all. Console-based (`fake_rcon_password <pw>`, then
`fake_rcon <cmd>`), no `.map` shorthand, no chat integration.

- Asset: `https://github.com/Salvatore-Als/cs2-fake-rcon/releases/download/1.3.0/linux.tar.gz`
- Contains only `addons/metamod/fake_rcon.vdf` and
  `addons/fake_rcon/bin/linuxsteamrt64/fake_rcon.so` → extract into `game/csgo/`.
- Config path, read from the binary's strings: `addons/configs/fake_rcon/config.ini`
  (also probes `config.cfg`, `cache.ini`, `cache.cfg`). Logs
  `Config file not found, creating default` on first run, so start once then edit.
  Password must be ≥4 chars. File *format* is **UNVERIFIED**; README points at
  <https://forums.alliedmods.net/showpost.php?p=2811082&postcount=15>.

---

## 11. Also outstanding (unrelated)

- **`aim.cfg` / `aim_pistol.cfg` live in `data/pcw/`**, which is runtime state. A
  reseed of that directory would lose them. Consider moving them into the project dir
  with a `pre.sh` sync, same pattern as 6.3.
- Populating `admins.json` with real SteamID64s is now **optional** rather than
  required — section 3.2 covers the spawn-editor access it was previously needed for.
  Still worth doing if `repeek_everyone_is_admin` is ever turned off.
