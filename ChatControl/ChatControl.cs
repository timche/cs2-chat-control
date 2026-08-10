// ChatControl — chat-driven server control for CounterStrikeSharp (CS2).
//
// Provides:
//   .map <name | workshop id | workshop URL>  — change the level from chat
//   .rcon <command>                           — run a server command from chat
//   .<preset>                                 — run a config-defined batch of commands
//
// Plus a "everyone is admin" convar (chatcontrol_everyone_is_admin) that bypasses
// the permission checks on this plugin's commands, for private/passworded servers.
//
// The behaviour of .map, .rcon and the everyone-is-admin gating is modelled on
// MatchZy (MIT, https://github.com/shobhit-pathak/MatchZy), but this is a
// reimplementation against the CounterStrikeSharp API, not copied code.

using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ChatControl;

public class ChatControlConfig : BasePluginConfig
{
    // When non-empty, .map only accepts these entries: map names are compared
    // after the de_ prefix has been applied, workshop IDs as the bare number.
    [JsonPropertyName("AllowedMaps")]
    public List<string> AllowedMaps { get; set; } = new();

    // Preset name -> server commands, executed in order.
    [JsonPropertyName("Presets")]
    public Dictionary<string, List<string>> Presets { get; set; } = new();
}

public class ChatControl : BasePlugin, IPluginConfig<ChatControlConfig>
{
    public override string ModuleName => "ChatControl";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Chat-driven server control: .map, .rcon and config-defined presets";

    // Must be a public field: CounterStrikeSharp discovers convars via GetFields.
    // Only the server console / RCON can change it.
    public FakeConVar<bool> EveryoneIsAdmin = new("chatcontrol_everyone_is_admin", "Bypass permission checks for ChatControl commands", false);

    public ChatControlConfig Config { get; set; } = new();

    private static readonly string Prefix = $"[{ChatColors.Green}ChatControl{ChatColors.Default}]";

    private static readonly Regex WorkshopIdInUrlPattern = new(@"id=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WorkshopIdPattern = new(@"^\d+$", RegexOptions.Compiled);
    private static readonly Regex SafeMapNamePattern = new(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);
    private static readonly Regex SafePresetNamePattern = new(@"^[a-z0-9_]+$", RegexOptions.Compiled);

    private readonly List<(string PresetName, string CommandName, CommandInfo.CommandCallback Callback)> registeredPresetCommands = new();

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
    }

    public void OnConfigParsed(ChatControlConfig config)
    {
        Config = config;

        UnregisterPresetCommands();
        RegisterPresetCommands();
    }

    // Chat listener. CounterStrikeSharp already handles the ! and / triggers for
    // registered commands; this adds the . trigger MatchZy users expect.
    private HookResult OnPlayerChat(EventPlayerChat chatEvent, GameEventInfo eventInfo)
    {
        var text = chatEvent.Text.Trim();

        if (text.Length <= 1 || !text.StartsWith('.'))
        {
            return HookResult.Continue;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return HookResult.Continue;
        }

        var commandWord = tokens[0].Substring(1).ToLowerInvariant();

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

        switch (commandWord)
        {
            case "map":
                HandleMapCommand(player, string.Join(' ', tokens.Skip(1)));
                break;

            case "rcon":
                // Not re-joined from the split tokens: the raw remainder keeps the
                // caller's original spacing and quoting intact.
                HandleRconCommand(player, text.Substring(tokens[0].Length).Trim());
                break;

            default:
                if (registeredPresetCommands.Any(preset => preset.PresetName == commandWord))
                {
                    HandlePresetCommand(player, commandWord);
                }

                break;
        }

        return HookResult.Continue;
    }

    [ConsoleCommand("css_map", "Change the map")]
    public void OnMapCommand(CCSPlayerController? player, CommandInfo command)
    {
        HandleMapCommand(player, command.GetArg(1).Trim());
    }

    [ConsoleCommand("css_rcon", "Run a server command")]
    public void OnRconCommand(CCSPlayerController? player, CommandInfo command)
    {
        // ArgString excludes the command name and is otherwise unmodified.
        HandleRconCommand(player, command.ArgString);
    }

    private void HandleMapCommand(CCSPlayerController? player, string mapArgument)
    {
        if (!CanUseCommand(player, "css_map", "@css/map"))
        {
            ReplyToPlayer(player, "You do not have permission to use this command.");
            return;
        }

        var requestedMap = mapArgument.Trim();

        if (string.IsNullOrEmpty(requestedMap))
        {
            ReplyToPlayer(player, "Usage: .map <name | workshop id | workshop URL>");
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
        // so `.map dust2` means de_dust2.
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
        if (!CanUseCommand(player, "css_rcon", "@css/rcon"))
        {
            ReplyToPlayer(player, "You do not have permission to use this command.");
            return;
        }

        if (string.IsNullOrWhiteSpace(serverCommand))
        {
            ReplyToPlayer(player, "Usage: .rcon <command>");
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
            if (NormalisePresetName(presetEntry.Key) == presetName)
            {
                return presetEntry.Value;
            }
        }

        return null;
    }

    private void RegisterPresetCommands()
    {
        foreach (var presetEntry in Config.Presets)
        {
            var presetName = NormalisePresetName(presetEntry.Key);

            if (presetName is "map" or "rcon")
            {
                Logger.LogWarning("Ignoring preset '{Preset}': the name is reserved by ChatControl.", presetEntry.Key);
                continue;
            }

            if (!SafePresetNamePattern.IsMatch(presetName))
            {
                Logger.LogWarning("Ignoring preset '{Preset}': names may only contain a-z, 0-9 and underscores.", presetEntry.Key);
                continue;
            }

            if (presetEntry.Value.Count == 0)
            {
                Logger.LogWarning("Ignoring preset '{Preset}': it has no commands.", presetEntry.Key);
                continue;
            }

            if (registeredPresetCommands.Any(registered => registered.PresetName == presetName))
            {
                Logger.LogWarning("Ignoring preset '{Preset}': '{Name}' is already registered.", presetEntry.Key, presetName);
                continue;
            }

            var commandName = $"css_{presetName}";
            CommandInfo.CommandCallback callback = (callingPlayer, command) => HandlePresetCommand(callingPlayer, presetName);

            AddCommand(commandName, $"ChatControl preset '{presetName}'", callback);
            registeredPresetCommands.Add((presetName, commandName, callback));
        }
    }

    // Config re-parses would otherwise register every preset a second time.
    private void UnregisterPresetCommands()
    {
        foreach (var registeredPreset in registeredPresetCommands)
        {
            RemoveCommand(registeredPreset.CommandName, registeredPreset.Callback);
        }

        registeredPresetCommands.Clear();
    }

    private static string NormalisePresetName(string configuredName)
    {
        var presetName = configuredName.Trim().ToLowerInvariant();

        if (presetName.StartsWith('.'))
        {
            presetName = presetName.Substring(1);
        }

        return presetName;
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
