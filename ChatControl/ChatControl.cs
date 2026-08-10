// ChatControl — chat-driven server control for CounterStrikeSharp (CS2).
//
// Provides:
//   .map <name | workshop id | workshop URL>  — change the level from chat
//   .rcon <command>                           — run a server command from chat
//   .<preset>                                 — run a config-defined batch of commands
//
// Each also works as !command and /command, CounterStrikeSharp's own triggers.
//
// Plus a "everyone is admin" convar (chatcontrol_everyone_is_admin) that bypasses
// the permission checks on this plugin's commands, for private/passworded servers.
//
// The behaviour of map, rcon and the everyone-is-admin gating is modelled on
// MatchZy (MIT, https://github.com/shobhit-pathak/MatchZy), but this is a
// reimplementation against the CounterStrikeSharp API, not copied code.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ChatControl;

public class ChatControlConfig : BasePluginConfig
{
    // The extra chat trigger this plugin's own listener answers to, on top of
    // CounterStrikeSharp's built-in "!" and "/". A bare "/" or "!" means "built-in
    // triggers only": those already dispatch /command and !command to the
    // registered css_ commands, so the listener adds nothing there.
    [JsonPropertyName("ChatPrefix")]
    public string ChatPrefix { get; set; } = ".";

    // The name the map command is registered under, i.e. "map" gives /map and
    // css_map. Rename it to sit next to a plugin that owns css_map, or set it to
    // an empty string to switch the command off. Same for the rcon command.
    [JsonPropertyName("MapCommandName")]
    public string MapCommandName { get; set; } = "map";

    [JsonPropertyName("RconCommandName")]
    public string RconCommandName { get; set; } = "rcon";

    // When non-empty, map changes only accept these entries: map names are compared
    // after the de_ prefix has been applied, workshop IDs as the bare number.
    [JsonPropertyName("AllowedMaps")]
    public List<string> AllowedMaps { get; set; } = new();

    // Preset name -> server commands, executed in order. These are ready-to-use
    // defaults that land in the generated config; edit or remove them there.
    [JsonPropertyName("Presets")]
    public Dictionary<string, List<string>> Presets { get; set; } = new()
    {
        ["aim"] = new()
        {
            "mp_maxrounds 9999",
            "mp_freezetime 0",
            "mp_free_armor 2",
            "mp_death_drop_gun 1",
            "mp_match_restart_delay 2",
            "mp_limitteams 0",
            "mp_warmup_end",
            "mp_restartgame 1",
        },
        ["aimpistol"] = new()
        {
            "mp_maxrounds 9999",
            "mp_freezetime 0",
            "mp_free_armor 1",
            "mp_death_drop_gun 1",
            "mp_match_restart_delay 2",
            "mp_limitteams 0",
            "mp_warmup_end",
            "mp_restartgame 1",
        },
    };
}

public class ChatControl : BasePlugin, IPluginConfig<ChatControlConfig>
{
    public override string ModuleName => "ChatControl";
    public override string ModuleVersion => "1.3.0";
    public override string ModuleDescription => "Chat-driven server control: .map, .rcon and config-defined presets";

    // Must be a public field: CounterStrikeSharp discovers convars via GetFields.
    // Only the server console / RCON can change it.
    public FakeConVar<bool> EveryoneIsAdmin = new("chatcontrol_everyone_is_admin", "Bypass permission checks for ChatControl commands", false);

    public ChatControlConfig Config { get; set; } = new();

    private static readonly string Prefix = $"[{ChatColors.Green}ChatControl{ChatColors.Default}]";

    // CounterStrikeSharp's own chat triggers. As a ChatPrefix either one means
    // "built-in triggers only" and leaves this plugin's chat listener inactive.
    private const string BuiltInTriggerSlash = "/";
    private const string BuiltInTriggerBang = "!";

    // Fallback for an unusable configured prefix.
    private const string DefaultChatPrefix = ".";

    // Fallbacks for an unusable configured name, and reserved preset names.
    private const string DefaultMapCommandName = "map";
    private const string DefaultRconCommandName = "rcon";

    // An empty active name means the command is not registered at all.
    private const string DisabledCommandName = "";

    private static readonly Regex WorkshopIdInUrlPattern = new(@"id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WorkshopIdPattern = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex SafeMapNamePattern = new(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);
    private static readonly Regex SafeCommandNamePattern = new(@"^[a-z0-9_]+$", RegexOptions.Compiled);

    // Every command this plugin registers — map, rcon and presets alike — so a
    // config re-parse can take them all back down before registering again.
    private readonly List<(string CommandWord, string ConsoleCommandName, CommandInfo.CommandCallback Callback)> registeredCommands = new();

    private string activeChatPrefix = DefaultChatPrefix;
    private string activeMapCommandName = DefaultMapCommandName;
    private string activeRconCommandName = DefaultRconCommandName;

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
    }

    public void OnConfigParsed(ChatControlConfig config)
    {
        Config = config;

        activeChatPrefix = ValidateChatPrefix(config.ChatPrefix);
        activeMapCommandName = ValidateCommandName(config.MapCommandName, "MapCommandName", DefaultMapCommandName);
        activeRconCommandName = ValidateCommandName(config.RconCommandName, "RconCommandName", DefaultRconCommandName);

        if (activeRconCommandName.Length > 0 && activeRconCommandName == activeMapCommandName)
        {
            Logger.LogWarning("Ignoring RconCommandName '{Name}': the map command already uses that name. Falling back to '{Fallback}'.", activeRconCommandName, DefaultRconCommandName);
            activeRconCommandName = DefaultRconCommandName;

            // Only reachable when MapCommandName is itself "rcon", which leaves the
            // fallback taken as well and no free name to register under.
            if (activeRconCommandName == activeMapCommandName)
            {
                Logger.LogWarning("Disabling the rcon command: MapCommandName is '{Name}'.", activeMapCommandName);
                activeRconCommandName = DisabledCommandName;
            }
        }

        UnregisterCommands();
        RegisterMapAndRconCommands();
        RegisterPresetCommands();
    }

    // An empty name switches the command off; anything unusable falls back to the
    // default name rather than leaving the server without the command.
    private string ValidateCommandName(string configuredName, string configKey, string defaultName)
    {
        var commandName = NormaliseCommandName(configuredName);

        if (commandName.Length == 0)
        {
            Logger.LogInformation("{Default} command disabled by config.", defaultName);
            return DisabledCommandName;
        }

        if (!SafeCommandNamePattern.IsMatch(commandName))
        {
            Logger.LogWarning("Ignoring {Key} '{Name}': names may only contain a-z, 0-9 and underscores. Falling back to '{Fallback}'.", configKey, configuredName, defaultName);
            return defaultName;
        }

        return commandName;
    }

    private string ValidateChatPrefix(string configuredPrefix)
    {
        var chatPrefix = configuredPrefix.Trim();

        // Kept as-is: on its own, either character selects the built-in triggers and
        // switches this plugin's listener off.
        if (chatPrefix is BuiltInTriggerSlash or BuiltInTriggerBang)
        {
            return chatPrefix;
        }

        if (chatPrefix.Length == 0)
        {
            Logger.LogWarning("Ignoring ChatPrefix '{Prefix}': it is empty. Falling back to '{Fallback}'.", configuredPrefix, DefaultChatPrefix);
            return DefaultChatPrefix;
        }

        if (chatPrefix.Any(char.IsWhiteSpace))
        {
            Logger.LogWarning("Ignoring ChatPrefix '{Prefix}': it contains whitespace. Falling back to '{Fallback}'.", configuredPrefix, DefaultChatPrefix);
            return DefaultChatPrefix;
        }

        // A longer string containing '!' or '/' ("//", "!x") is neither a built-in
        // trigger nor a usable listener prefix: CounterStrikeSharp would dispatch the
        // command from the leading character and the listener would run it again.
        if (chatPrefix.Contains('!') || chatPrefix.Contains('/'))
        {
            Logger.LogWarning("Ignoring ChatPrefix '{Prefix}': '!' and '/' may only be used on their own. Falling back to '{Fallback}'.", configuredPrefix, DefaultChatPrefix);
            return DefaultChatPrefix;
        }

        return chatPrefix;
    }

    // Chat listener for the configured prefix, e.g. the . trigger MatchZy users
    // expect. CounterStrikeSharp already dispatches / and ! to registered commands,
    // so on those the listener stays out of the way: "!" messages still reach this
    // event (they are broadcast), and matching them would run the command twice.
    private HookResult OnPlayerChat(EventPlayerChat chatEvent, GameEventInfo eventInfo)
    {
        if (activeChatPrefix is BuiltInTriggerSlash or BuiltInTriggerBang)
        {
            return HookResult.Continue;
        }

        var text = chatEvent.Text.Trim();

        if (text.Length <= activeChatPrefix.Length || !text.StartsWith(activeChatPrefix, StringComparison.Ordinal))
        {
            return HookResult.Continue;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return HookResult.Continue;
        }

        var commandWord = tokens[0].Substring(activeChatPrefix.Length).ToLowerInvariant();

        if (commandWord.Length == 0)
        {
            return HookResult.Continue;
        }

        // EventPlayerChat.Userid carries a slot index, not a userid.
        var userid = NativeAPI.GetUseridFromIndex(chatEvent.Userid + 1);
        var player = Utilities.GetPlayerFromUserid(userid);

        if (player == null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        // commandWord is never empty here, so a disabled command's empty name cannot
        // be matched by accident.
        if (commandWord == activeMapCommandName)
        {
            HandleMapCommand(player, string.Join(' ', tokens.Skip(1)));
        }
        else if (commandWord == activeRconCommandName)
        {
            // Not re-joined from the split tokens: the raw remainder keeps the
            // caller's original spacing and quoting intact.
            HandleRconCommand(player, text.Substring(tokens[0].Length).Trim());
        }
        else if (registeredCommands.Any(registered => registered.CommandWord == commandWord))
        {
            // Only presets are left: a disabled map or rcon command is not in the
            // list either, and neither name can be taken by a preset.
            HandlePresetCommand(player, commandWord);
        }

        return HookResult.Continue;
    }

    private void HandleMapCommand(CCSPlayerController? player, string mapArgument)
    {
        if (!CanUseCommand(player, $"css_{activeMapCommandName}", "@css/map"))
        {
            ReplyToPlayer(player, "You do not have permission to use this command.");
            return;
        }

        var requestedMap = mapArgument.Trim();

        if (string.IsNullOrEmpty(requestedMap))
        {
            ReplyToPlayer(player, $"Usage: {activeChatPrefix}{activeMapCommandName} <name | workshop id | workshop URL>");
            return;
        }

        var looksLikeUrl = requestedMap.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || requestedMap.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        if (looksLikeUrl)
        {
            var workshopIdMatch = WorkshopIdInUrlPattern.Match(requestedMap);

            if (!workshopIdMatch.Success)
            {
                ReplyToPlayer(player, "Could not find a workshop ID in that URL.");
                return;
            }

            ChangeToWorkshopMap(player, workshopIdMatch.Groups[1].Value);
            return;
        }

        if (WorkshopIdPattern.IsMatch(requestedMap))
        {
            ChangeToWorkshopMap(player, requestedMap);
            return;
        }

        ChangeToNamedMap(player, requestedMap);
    }

    // workshopId is always digits-only here (regex matched), so interpolating it
    // into the console command cannot smuggle a second command.
    private void ChangeToWorkshopMap(CCSPlayerController? player, string workshopId)
    {
        if (!IsMapAllowed(workshopId))
        {
            ReplyToPlayer(player, $"Map not allowed. Allowed: {string.Join(", ", Config.AllowedMaps)}");
            return;
        }

        Server.ExecuteCommand($"host_workshop_map {workshopId}");
    }

    private void ChangeToNamedMap(CCSPlayerController? player, string requestedMapName)
    {
        // MatchZy convention: a bare name without an underscore gets the de_ prefix,
        // so `/map dust2` means de_dust2.
        var mapName = requestedMapName.Contains('_') ? requestedMapName : $"de_{requestedMapName}";

        if (!IsMapAllowed(mapName))
        {
            ReplyToPlayer(player, $"Map not allowed. Allowed: {string.Join(", ", Config.AllowedMaps)}");
            return;
        }

        // The name is interpolated into a console command, so reject anything that
        // could carry quotes or semicolons before asking the engine about it.
        if (!SafeMapNamePattern.IsMatch(mapName) || !Server.IsMapValid(mapName))
        {
            ReplyToPlayer(player, "Invalid map name!");
            return;
        }

        Server.ExecuteCommand($"changelevel {mapName}");
    }

    private bool IsMapAllowed(string mapName)
    {
        if (Config.AllowedMaps.Count == 0)
        {
            return true;
        }

        return Config.AllowedMaps.Any(allowedMap => string.Equals(allowedMap.Trim(), mapName, StringComparison.OrdinalIgnoreCase));
    }

    // Deliberately an unfiltered passthrough, same as MatchZy: the permission check
    // is the gate, not a command blocklist.
    private void HandleRconCommand(CCSPlayerController? player, string serverCommand)
    {
        if (!CanUseCommand(player, $"css_{activeRconCommandName}", "@css/rcon"))
        {
            ReplyToPlayer(player, "You do not have permission to use this command.");
            return;
        }

        if (string.IsNullOrWhiteSpace(serverCommand))
        {
            ReplyToPlayer(player, $"Usage: {activeChatPrefix}{activeRconCommandName} <command>");
            return;
        }

        Server.ExecuteCommand(serverCommand);
        ReplyToPlayer(player, "Command sent successfully!");
    }

    private void HandlePresetCommand(CCSPlayerController? player, string presetName)
    {
        if (!CanUseCommand(player, $"css_{presetName}", "@css/config"))
        {
            ReplyToPlayer(player, "You do not have permission to use this command.");
            return;
        }

        // Looked up fresh rather than captured at registration time: the config may
        // have been re-parsed since this command was registered.
        var presetCommands = FindPresetCommands(presetName);

        if (presetCommands == null)
        {
            ReplyToPlayer(player, $"Preset '{presetName}' is no longer configured.");
            return;
        }

        foreach (var presetCommand in presetCommands)
        {
            Server.ExecuteCommand(presetCommand);
        }

        ReplyToPlayer(player, $"Executed preset '{presetName}' ({presetCommands.Count} commands).");
    }

    private List<string>? FindPresetCommands(string presetName)
    {
        foreach (var presetEntry in Config.Presets)
        {
            if (NormaliseCommandName(presetEntry.Key) == presetName)
            {
                return presetEntry.Value;
            }
        }

        return null;
    }

    private void RegisterMapAndRconCommands()
    {
        if (activeMapCommandName.Length > 0)
        {
            RegisterCommand(activeMapCommandName, "Change the map", (callingPlayer, command) => HandleMapCommand(callingPlayer, command.GetArg(1).Trim()));
        }

        if (activeRconCommandName.Length > 0)
        {
            // ArgString excludes the command name and is otherwise unmodified.
            RegisterCommand(activeRconCommandName, "Run a server command", (callingPlayer, command) => HandleRconCommand(callingPlayer, command.ArgString));
        }
    }

    private void RegisterPresetCommands()
    {
        foreach (var presetEntry in Config.Presets)
        {
            var presetName = NormaliseCommandName(presetEntry.Key);

            if (!SafeCommandNamePattern.IsMatch(presetName))
            {
                Logger.LogWarning("Ignoring preset '{Preset}': names may only contain a-z, 0-9 and underscores.", presetEntry.Key);
                continue;
            }

            if (IsReservedCommandName(presetName))
            {
                Logger.LogWarning("Ignoring preset '{Preset}': the name is reserved by ChatControl.", presetEntry.Key);
                continue;
            }

            if (presetEntry.Value.Count == 0)
            {
                Logger.LogWarning("Ignoring preset '{Preset}': it has no commands.", presetEntry.Key);
                continue;
            }

            if (registeredCommands.Any(registered => registered.CommandWord == presetName))
            {
                Logger.LogWarning("Ignoring preset '{Preset}': '{Name}' is already registered.", presetEntry.Key, presetName);
                continue;
            }

            RegisterCommand(presetName, $"ChatControl preset '{presetName}'", (callingPlayer, command) => HandlePresetCommand(callingPlayer, presetName));
        }
    }

    // "map" and "rcon" stay reserved even after a rename, so a config can be moved
    // to a server that runs the commands under their default names without its
    // presets suddenly colliding.
    private bool IsReservedCommandName(string commandName)
    {
        return commandName == DefaultMapCommandName
            || commandName == DefaultRconCommandName
            || commandName == activeMapCommandName
            || commandName == activeRconCommandName;
    }

    private void RegisterCommand(string commandWord, string description, CommandInfo.CommandCallback callback)
    {
        var consoleCommandName = $"css_{commandWord}";

        AddCommand(consoleCommandName, description, callback);
        registeredCommands.Add((commandWord, consoleCommandName, callback));
    }

    // Config re-parses would otherwise register every command a second time.
    private void UnregisterCommands()
    {
        foreach (var registeredCommand in registeredCommands)
        {
            RemoveCommand(registeredCommand.ConsoleCommandName, registeredCommand.Callback);
        }

        registeredCommands.Clear();
    }

    private static string NormaliseCommandName(string configuredName)
    {
        var commandName = configuredName.Trim().ToLowerInvariant();

        if (commandName.StartsWith('.'))
        {
            commandName = commandName.Substring(1);
        }

        return commandName;
    }

    private bool CanUseCommand(CCSPlayerController? player, string commandName, string permissionFlag)
    {
        if (EveryoneIsAdmin.Value)
        {
            return true;
        }

        // Server console / RCON.
        if (player == null)
        {
            return true;
        }

        // A fresh instance per check: CanExecuteCommand mutates the instance's own
        // permission set. @css/root satisfies every @css/* flag.
        var permissionCheck = new RequiresPermissionsOr(permissionFlag, "@css/root") { Command = commandName };

        return permissionCheck.CanExecuteCommand(player);
    }

    private void ReplyToPlayer(CCSPlayerController? player, string message)
    {
        if (player == null)
        {
            Server.PrintToConsole($"{Prefix} {message}");
            return;
        }

        player.PrintToChat($"{Prefix} {message}");
    }
}
