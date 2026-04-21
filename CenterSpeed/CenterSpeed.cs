using Sharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Interfaces;

namespace CenterSpeed;

public class CenterSpeed : IModSharpModule, IGameListener, IClientListener
{
    string IModSharpModule.DisplayName => "Center Speed";
    string IModSharpModule.DisplayAuthor => "Lethal & Retro";

    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    int IClientListener.ListenerVersion => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

        private readonly string _sharpPath;
    private readonly ISharedSystem _sharedSystem;
    private readonly IClientManager _clientManager;
    private readonly ITransmitManager _transmitManager;
    private readonly ILogger<CenterSpeed> _logger;
    private readonly IModSharp _modSharp;
    private readonly IEntityManager _entityManager;
    private readonly IHookManager _hookManager;
    private readonly ISharpModuleManager _modules;
    private IModSharpModuleInterface<IClientPreference>? _cachedClientPrefInterface;
    private IDisposable? _clientPrefCallback;
    private IModSharpModuleInterface<ITimerHudFeed>? _cachedTimerInterface;

    // --- Per-player HUD state ---
    private readonly PlayerHudState?[] _huds = new PlayerHudState?[64];
    private readonly PlayerHudSettings?[] _playerSettings = new PlayerHudSettings?[64];
    private float[] _lastSpeed = new float[64];
    private IBaseEntity? _sharedTarget;
    private IConVar? _particleConVar;

        // Character to particle frame mapping
    // Frame 0 = blank, Frames 1-10 = 0-9, Frames 11-35 = A-Z
    private readonly Dictionary<char, int> _charMap = new()
    {
        // Blank/Space
        [' '] = 0,
        
        // Digits 0-9 -> Frames 1-10
        ['0'] = 1, ['1'] = 2, ['2'] = 3, ['3'] = 4, ['4'] = 5,
        ['5'] = 6, ['6'] = 7, ['7'] = 8, ['8'] = 9, ['9'] = 10,
        
        // Letters A-Z -> Frames 11-35
        ['A'] = 11, ['B'] = 12, ['C'] = 13, ['D'] = 14, ['E'] = 15,
        ['F'] = 16, ['G'] = 17, ['H'] = 18, ['I'] = 19, ['J'] = 20,
        ['K'] = 21, ['L'] = 22, ['M'] = 23, ['N'] = 24, ['O'] = 25,
        ['P'] = 26, ['Q'] = 27, ['R'] = 28, ['S'] = 29, ['T'] = 30,
        ['U'] = 31, ['V'] = 32, ['W'] = 33, ['X'] = 34, ['Y'] = 35, ['Z'] = 36,
        
        // Symbols (frames after Z - adjust as needed)
        ['%'] = 37, ['/'] = 38, [':'] = 39, ['-'] = 40, ['+'] = 41, ['.'] = 42
    };

        private class PlayerHudSettings
    {
        // Legacy: kept for backward compatibility
        public float[] DigitOffsets = { -1.4f, -0.45f, 0.45f, 1.4f };
        public float HudScale = 0.04f;
        public float YOffset = -1f;
        public bool Enabled = false;

        // Per-widget settings
        public Dictionary<TimerWidgetType, WidgetConfig> Widgets = new()
        {
            [TimerWidgetType.Time] = new() { XOffset = 0f, YOffset = 1.5f, Scale = 0.05f, Visible = true },
            [TimerWidgetType.Sync] = new() { XOffset = 0f, YOffset = 0.5f, Scale = 0.04f, Visible = true },
            [TimerWidgetType.Jumps] = new() { XOffset = -2f, YOffset = -0.5f, Scale = 0.035f, Visible = true },
            [TimerWidgetType.Strafes] = new() { XOffset = 2f, YOffset = -0.5f, Scale = 0.035f, Visible = true },
            [TimerWidgetType.Checkpoint] = new() { XOffset = 0f, YOffset = -1.5f, Scale = 0.035f, Visible = true },
            [TimerWidgetType.Status] = new() { XOffset = 0f, YOffset = 2.5f, Scale = 0.04f, Visible = true },
            [TimerWidgetType.Track] = new() { XOffset = 0f, YOffset = 3.5f, Scale = 0.04f, Visible = true },
            [TimerWidgetType.PbTime] = new() { XOffset = -2.5f, YOffset = -2.5f, Scale = 0.03f, Visible = true },
            [TimerWidgetType.WrTime] = new() { XOffset = 2.5f, YOffset = -2.5f, Scale = 0.03f, Visible = true }
        };
    }

    private class WidgetConfig
    {
        public float XOffset = 0f;
        public float YOffset = 0f;
        public float Scale = 0.04f;
        public bool Visible = true;
    }

    private class PlayerHudState
    {
        public bool IsDisposed = false;
        public Dictionary<TimerWidgetType, WidgetParticleState> Widgets = new();
    }

    private class WidgetParticleState
    {
        public List<IBaseParticle?> Particles = new();
    }

    public CenterSpeed(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration configuration,
        bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _clientManager = sharedSystem.GetClientManager();
        _entityManager = sharedSystem.GetEntityManager();
        _modSharp = sharedSystem.GetModSharp();
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<CenterSpeed>();
        _transmitManager = sharedSystem.GetTransmitManager();
        _hookManager = sharedSystem.GetHookManager();
                                _modules = sharedSystem.GetSharpModuleManager();
        _sharpPath = sharpPath;
    }

    public bool Init()
    {
        _clientManager.InstallClientListener(this);
        _sharedSystem.GetModSharp().InstallGameListener(this);

        var convarManager = _sharedSystem.GetConVarManager();
        _particleConVar = convarManager.CreateConVar("ms_cspeed_particle", "particles/digits_x/digits_x.vpcf");

        _clientManager.InstallCommandCallback("hud", OnHudSettingsCommand);

        _logger.LogInformation("CenterSpeed loaded");

        _hookManager.PlayerRunCommand.InstallHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawned);
        _hookManager.PlayerKilledPost.InstallForward(OnPlayerKilled);
        _hookManager.HandleCommandJoinTeam.InstallHookPost(OnPlayerTeamChanged);

        return true;
    }

    private void OnPlayerTeamChanged(IHandleCommandJoinTeamHookParams param, HookReturnValue<bool> ret)
    {
        KillPlayerHud(param.Client.Slot);
    }

    public void Shutdown()
    {
        _clientManager.RemoveClientListener(this);
        _sharedSystem.GetModSharp().RemoveGameListener(this);

                _hookManager.PlayerRunCommand.RemoveHookPost(PlayerRunCommandPost);
        _hookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawned);
        _clientPrefCallback?.Dispose();

        for (var i = 0; i < 64; i++)
            KillPlayerHud(i);

        _sharedTarget?.AcceptInput("DestroyImmediately");
        _sharedTarget = null;
    }

    // -------------------------------------------------------------------------
    // Game listener

    public void OnGameDeactivate()
    {
        // The game cleans up entities itself — just drop our references.
        for (var i = 0; i < 64; i++)
            _huds[i] = null;

        _sharedTarget = null;
    }

    // -------------------------------------------------------------------------
    // Client listener

    public void OnClientPostAdminCheck(IGameClient client)
    {
        _playerSettings[client.Slot] = new();
    }

    private void OnPlayerSpawned(IPlayerSpawnForwardParams param)
    {
        SpawnPlayerHud(param.Client);
    }

    private void OnPlayerKilled(IPlayerKilledForwardParams param)
    {
        KillPlayerHud(param.Client.Slot);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        KillPlayerHud(client.Slot);
        _playerSettings[client.Slot] = null;
    }

        // -------------------------------------------------------------------------
    // HUD management

    private void SpawnPlayerHud(IGameClient client)
    {
        if (!client.IsValid || client.IsFakeClient)
            return;

        var slot = (byte)client.Slot;
        if (client.GetPlayerController()?.Team < CStrikeTeam.TE)
            return;

        KillPlayerHud(slot); // clear any stale state

        // Create shared info_target if needed
        if (_sharedTarget is null || !_sharedTarget.IsValid())
        {
            var targetKv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["origin"] = "0.0 1.0 0.5"
            };
            var target = _sharedSystem.GetEntityManager()
                .SpawnEntitySync<IBaseEntity>("info_target", targetKv);

            if (target == null)
            {
                _logger.LogWarning("SpawnPlayerHud(target): failed to create shared target");
                return;
            }
            _sharedTarget = target;
        }

        var state = new PlayerHudState();
        var settings = _playerSettings[client.Slot];
        if (settings is null)
        {
            settings = new PlayerHudSettings();
            _playerSettings[client.Slot] = settings;
        }

        var particleName = _particleConVar?.GetString() ?? "particles/numbers/number_x.vpcf";

        // Spawn particles for each visible widget
        foreach (var (widgetType, config) in settings.Widgets)
        {
            if (!config.Visible)
                continue;

            // Estimate max character count for this widget type
            var maxChars = widgetType switch
            {
                TimerWidgetType.Time => 8,        // "MM:SS.cc"
                TimerWidgetType.Sync => 8,        // "SYNC X.X%"
                TimerWidgetType.Jumps => 4,       // "9999"
                TimerWidgetType.Strafes => 4,     // "9999"
                TimerWidgetType.Checkpoint => 6,  // "9/99"
                TimerWidgetType.Status => 6,      // "PAUSE"
                TimerWidgetType.Track => 4,       // "T9999"
                TimerWidgetType.PbTime => 12,     // "PB: MM:SS.cc"
                TimerWidgetType.WrTime => 12,     // "WR: MM:SS.cc"
                _ => 8
            };

            var widgetState = new WidgetParticleState();

            for (var i = 0; i < maxChars; i++)
            {
                var kv = new Dictionary<string, KeyValuesVariantValueItem>
                {
                    ["effect_name"] = particleName,
                    ["start_active"] = "0"
                };

                var particle = _sharedSystem.GetEntityManager()
                                            .SpawnEntitySync<IBaseParticle>("info_particle_system", kv);

                if (particle == null)
                {
                    _logger.LogWarning("SpawnPlayerHud: failed to spawn char {Index} for widget {Widget} slot {Slot}", i, widgetType, slot);
                    continue;
                }

                particle.GetControlPointEntities()[17] = _sharedTarget.Handle;

                // Position particles horizontally within the widget
                var widgetWidth = maxChars * 0.95f;
                var startX = -(widgetWidth / 2f);
                var xPos = startX + (i * 0.95f);

                particle.DataControlPoint = 33;
                particle.DataControlPointValue = new Vector(xPos + config.XOffset, config.YOffset, 0f);

                SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f)); // frame (0 = blank)
                SetControlPointValue(particle, 34, new Vector(config.Scale, 0f, 0f)); // scale
                SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f)); // color

                particle.AcceptInput("Start");
                particle.Active = true;

                widgetState.Particles.Add(particle);
                _transmitManager.AddEntityHooks(particle, false);
            }

            state.Widgets[widgetType] = widgetState;
        }

        // Set visibility: only visible to the owning player
        foreach (var con in _entityManager.GetPlayerControllers(true))
        {
            foreach (var widget in state.Widgets.Values)
            {
                foreach (var particle in widget.Particles)
                {
                    if (particle == null) continue;
                    bool shouldSee = (con.PlayerSlot == slot);
                    _transmitManager.SetEntityState(particle.Index, con.Index, shouldSee, -1);
                }
            }
        }

        _huds[slot] = state;
    }

    private void KillPlayerHud(int slot)
    {
        var state = _huds[slot];
        if (state == null) return;

        state.IsDisposed = true;
        _huds[slot] = null;
        _lastSpeed[slot] = 0;

        foreach (var widget in state.Widgets.Values)
        {
            foreach (var particle in widget.Particles)
            {
                if (particle == null || !particle.IsValid()) continue;

                particle.AcceptInput("Stop");
                particle.AcceptInput("DestroyImmediately");
                particle.Active = false;
            }
        }

        state.Widgets.Clear();
    }

        // -------------------------------------------------------------------------
    // Update timer — runs every 0.1 s

    private void PlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> retValue)
    {
        if (_modSharp.GetGlobals().TickCount % 10 != 0)
            return;

        var client = param.Client;
        var state = _huds[client.Slot];

        if (client.GetPlayerController()?.Team < CStrikeTeam.TE)
        {
            KillPlayerHud(client.Slot);
            return;
        }

        if (state == null || state.IsDisposed) return;

        var controller = client.GetPlayerController();
        if (controller == null || controller.ConnectedState != PlayerConnectedState.PlayerConnected)
            return;

        var timerInterface = GetTimerInterface();
        if (timerInterface == null)
        {
            _logger.LogWarning("Timer interface not available, skipping HUD update");
            return;
        }

        // Update each widget
        foreach (var (widgetType, widgetState) in state.Widgets)
        {
            if (!timerInterface.TryGetWidgetText(client.Slot, widgetType, out var text))
                continue;

            var particles = widgetState.Particles;
            var textLength = text.Length;
            var maxParticles = particles.Count;

            // Calculate starting offset to center the text
            var startX = (textLength - 1) / 2f;

            for (var i = 0; i < maxParticles; i++)
            {
                var particle = particles[i];
                if (particle == null || state.IsDisposed)
                    continue;

                if (i < textLength)
                {
                    // Map character to particle frame
                    var charIndex = i - startX;
                    if (charIndex >= 0 && charIndex < textLength)
                    {
                        var c = text[i];
                        var frame = _charMap.GetValueOrDefault(c, 0);
                        SetControlPointValue(particle, 32, new Vector(frame, 0f, 0f));
                    }
                }
                else
                {
                    // Hide extra particles by setting frame to blank
                    SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f));
                }
            }
        }
    }

        // -------------------------------------------------------------------------
    // !hudsettings command

    private ECommandAction OnHudSettingsCommand(IGameClient client, StringCommand command)
    {
        var slot = client.Slot;
        var settings = _playerSettings[slot] ??= new PlayerHudSettings();

        if (command.ArgCount == 0 || command.GetArg(1).Equals("info", StringComparison.OrdinalIgnoreCase))
        {
            PrintHudSettings(client, settings);
            return ECommandAction.Stopped;
        }

        var sub = command.GetArg(1).ToLowerInvariant();

        if (sub == "widget")
        {
            // !hudsettings widget <Time|Sync|Jumps|Strafes|Checkpoint|Status|Track|PbTime|WrTime> <toggle|offset|scale>
            if (command.ArgCount < 3)
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings widget <Type> <toggle|offset <X>|scale <S>>");
                return ECommandAction.Stopped;
            }

            var widgetSub = command.GetArg(2).ToLowerInvariant();
            
            if (Enum.TryParse<TimerWidgetType>(command.GetArg(2), true, out var widgetType))
            {
                var widget = settings.Widgets[widgetType];
                
                if (command.ArgCount >= 4)
                {
                    var propSub = command.GetArg(3).ToLowerInvariant();
                    
                    if (propSub == "toggle")
                    {
                        widget.Visible = !widget.Visible;
                        SaveSettings(client.SteamId, settings);
                        SpawnPlayerHud(client);
                        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget {widgetType} {(widget.Visible ? "enabled" : "disabled")}");
                    }
                    else if (propSub == "offset")
                    {
                        if (command.ArgCount >= 5 && float.TryParse(command.GetArg(4), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var xOffset))
                        {
                            widget.XOffset = Math.Clamp(xOffset, -10f, 10f);
                            SaveSettings(client.SteamId, settings);
                            SpawnPlayerHud(client);
                            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget {widgetType} X-Offset set to {widget.XOffset:F2}");
                        }
                        else
                        {
                            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings widget <Type> offset <X>");
                        }
                    }
                    else if (propSub == "yoffset")
                    {
                        if (command.ArgCount >= 5 && float.TryParse(command.GetArg(4), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var yOffset))
                        {
                            widget.YOffset = Math.Clamp(yOffset, -10f, 10f);
                            SaveSettings(client.SteamId, settings);
                            SpawnPlayerHud(client);
                            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget {widgetType} Y-Offset set to {widget.YOffset:F2}");
                        }
                        else
                        {
                            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings widget <Type> yoffset <Y>");
                        }
                    }
                    else if (propSub == "scale")
                    {
                        if (command.ArgCount >= 5 && float.TryParse(command.GetArg(4), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var scale))
                        {
                            widget.Scale = Math.Clamp(scale, 0f, 10f);
                            SaveSettings(client.SteamId, settings);
                            SpawnPlayerHud(client);
                            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget {widgetType} Scale set to {widget.Scale:F4}");
                        }
                        else
                        {
                            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings widget <Type> scale <S>");
                        }
                    }
                    else
                    {
                        client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Subcommands: toggle | offset <X> | yoffset <Y> | scale <S>");
                    }
                }
                else
                {
                    client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget {widgetType}: Visible={widget.Visible}, Offset=({widget.XOffset:F2}, {widget.YOffset:F2}), Scale={widget.Scale:F4}");
                }
            }
            else
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Invalid widget type. Use: Time, Sync, Jumps, Strafes, Checkpoint, Status, Track, PbTime, WrTime");
            }
            return ECommandAction.Stopped;
        }

        if (sub == "offset")
        {
            if (command.ArgCount < 3 ||
                !int.TryParse(command.GetArg(2), out var index1) ||
                !float.TryParse(command.GetArg(3), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings offset <1-4> <-10 to 10>");
                return ECommandAction.Stopped;
            }

            index1 = Math.Clamp(index1, 1, 4);
            value = Math.Clamp(value, -10f, 10f);
            var i = index1 - 1;

            settings.DigitOffsets[i] = value;
            SaveSettings(client.SteamId, settings);
            SpawnPlayerHud(client);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Digit {index1} offset set to {value:F2}");
        }
        else if (sub == "scale")
        {
            if (command.ArgCount < 2 ||
                !float.TryParse(command.GetArg(2), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings scale <0-10>");
                return ECommandAction.Stopped;
            }

            value = Math.Clamp(value, 0f, 10f);
            settings.HudScale = value;
            SaveSettings(client.SteamId, settings);
            SpawnPlayerHud(client);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Scale set to {value:F2}");
        }
        else if (sub == "yoffset")
        {
            if (command.ArgCount < 2 ||
                !float.TryParse(command.GetArg(2), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hudsettings yoffset <-10-10>");
                return ECommandAction.Stopped;
            }

            offset = Math.Clamp(offset, -10f, 10f);
            settings.YOffset = offset;
            SaveSettings(client.SteamId, settings);
            SpawnPlayerHud(client);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Y-Offset set to {offset:F2}");
        }
        else if (sub == "toggle")
        {
            settings.Enabled = !settings.Enabled;
            SaveSettings(client.SteamId, settings);
            if (settings.Enabled)
                SpawnPlayerHud(client);
            else
                KillPlayerHud(client.Slot);
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Enabled set to {settings.Enabled}");
        }
        else
        {
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Subcommands: widget <Type> <toggle|offset|yoffset|scale> | offset <1-4> | scale | yoffset | info");
        }
        return ECommandAction.Stopped;
    }

    private void PrintHudSettings(IGameClient client, PlayerHudSettings settings)
    {
        var o = settings.DigitOffsets;
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Legacy Offsets: 1={o[0]:F2}  2={o[1]:F2}  3={o[2]:F2}  4={o[3]:F2}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Legacy Scale: {settings.HudScale:F4}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Legacy Y-Offset: {settings.YOffset:F4}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Widget Settings:");
        foreach (var (widgetType, config) in settings.Widgets)
        {
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, $"   {widgetType}: Visible={config.Visible}, Offset=({config.XOffset:F2}, {config.YOffset:F2}), Scale={config.Scale:F4}");
        }
    }

        // -------------------------------------------------------------------------
    // ClientPrefs integration

    public void OnAllModulesLoaded()
    {
        _cachedClientPrefInterface = _modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
        if (_cachedClientPrefInterface?.Instance is { } instance)
            _clientPrefCallback = instance.ListenOnLoad(OnCookieLoad);

        _cachedTimerInterface = _modules.GetOptionalSharpModuleInterface<ITimerHudFeed>(ITimerHudFeed.Identity);
        _logger.LogInformation("Timer interface bridged: {Bridged}", _cachedTimerInterface != null);
    }

    public void OnLibraryConnected(string name)
    {
        if (name.Equals("ClientPreferences", StringComparison.Ordinal))
        {
            _cachedClientPrefInterface = _modules.GetRequiredSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
            if (_cachedClientPrefInterface?.Instance is { } instance)
                _clientPrefCallback = instance.ListenOnLoad(OnCookieLoad);
        }
        else if (name.Equals("Timer", StringComparison.Ordinal) || name.Equals("SurfTimer", StringComparison.Ordinal))
        {
            _cachedTimerInterface = _modules.GetRequiredSharpModuleInterface<ITimerHudFeed>(ITimerHudFeed.Identity);
            _logger.LogInformation("Timer interface connected via library event");
        }
    }

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals("ClientPreferences", StringComparison.Ordinal))
        {
            _cachedClientPrefInterface = null;
        }
        else if (name.Equals("Timer", StringComparison.Ordinal) || name.Equals("SurfTimer", StringComparison.Ordinal))
        {
            _cachedTimerInterface = null;
        }
    }

    private IClientPreference? GetClientPrefInterface()
    {
        if (_cachedClientPrefInterface?.Instance is null)
        {
            _cachedClientPrefInterface = _modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
            if (_cachedClientPrefInterface?.Instance is { } instance)
                _clientPrefCallback = instance.ListenOnLoad(OnCookieLoad);
        }
        return _cachedClientPrefInterface?.Instance;
    }

    private ITimerHudFeed? GetTimerInterface()
    {
        if (_cachedTimerInterface?.Instance is null)
        {
            _cachedTimerInterface = _modules.GetOptionalSharpModuleInterface<ITimerHudFeed>(ITimerHudFeed.Identity);
        }
        return _cachedTimerInterface?.Instance;
    }

        private void OnCookieLoad(IGameClient client)
    {
        if (GetClientPrefInterface() is not { } cp) return;

        var settings = _playerSettings[client.Slot] ??= new PlayerHudSettings();
        var id = client.SteamId;

        // Load legacy settings for backward compatibility
        for (var i = 0; i < 4; i++)
        {
            if (cp.GetCookie(id, $"hud_d{i}") is { } c &&
                float.TryParse(c.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                settings.DigitOffsets[i] = v;
        }

        if (cp.GetCookie(id, "hud_scale") is { } sc &&
            float.TryParse(sc.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale))
            settings.HudScale = scale;

        if (cp.GetCookie(id, "hud_yoffset") is { } yo &&
            float.TryParse(yo.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var yoffset))
            settings.YOffset = yoffset;

        if (cp.GetCookie(id, "hud_enabled") is { } en)
            settings.Enabled = en.GetString() != "0";
        
        SpawnPlayerHud(client);
    }

    private void SaveSettings(ulong steamId, PlayerHudSettings s)
    {
        if (GetClientPrefInterface() is not { } cp) return;

        // Save legacy settings for backward compatibility
        for (var i = 0; i < 4; i++)
            cp.SetCookie(steamId, $"hud_d{i}",
                s.DigitOffsets[i].ToString("F4", System.Globalization.CultureInfo.InvariantCulture));

        cp.SetCookie(steamId, "hud_scale",
            s.HudScale.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        cp.SetCookie(steamId, "hud_yoffset",
            s.YOffset.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        cp.SetCookie(steamId, "hud_enabled", s.Enabled ? "1" : "0");
    }

    // -------------------------------------------------------------------------
    // Helpers

    private bool SetControlPointValue(IBaseParticle particle, int cpIndex, Vector value)
    {
        var assignments = particle.GetServerControlPointAssignments();
        var controlPoints = particle.GetServerControlPoints();

        for (var i = 0; i < 4; i++)
        {
            if (assignments[i] == cpIndex || assignments[i] == 255)
            {
                assignments[i] = (byte)cpIndex;
                controlPoints[i] = value;
                return true;
            }
        }

        _logger.LogWarning("No free server controlled control points for CP {CpIndex}", cpIndex);
        return false;
    }

    public void OnResourcePrecache()
    {
        var assetPath = Path.Combine(_sharpPath, "assets");

        if (!Directory.Exists(assetPath))
        {
            return;
        }

        var files = Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var relative = file[(assetPath.Length + 1)..].Replace("\\", "/");

            if (!relative.StartsWith("particles/", StringComparison.OrdinalIgnoreCase))
                continue;

            var asset = relative.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                ? relative[..^2]
                : relative;

            _modSharp.PrecacheResource(asset);

        }
    }
}