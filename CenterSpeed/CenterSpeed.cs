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
    private const int HudParticleCapacity = 64;
    // Wider spacing for world-space particle text readability.
    private const float WidgetCharSpacing = 1.35f;
    private const float WidgetLineSpacing = 1.55f;
    private const int WidgetMaxLines = 5;

    private enum HudDisplayMode
    {
        Speed = 0,
        Timer = 1,
        Widget = 2,
        TimerWidget = 3
    }

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
    private IModSharpModuleInterface<IClientPreference>? _cachedInterface;
    private IModSharpModuleInterface<ITimerHudFeed>? _timerHudFeedInterface;
    private IDisposable? _callback;

    // --- Per-player HUD state ---
    private readonly PlayerHudState?[] _huds = new PlayerHudState?[64];
    private readonly PlayerHudSettings?[] _playerSettings = new PlayerHudSettings?[64];
    private float[] _lastSpeed = new float[64];
    private readonly HudDisplayMode[] _displayModes = new HudDisplayMode[64];
    private readonly string[] _widgetText = new string[64];
    private readonly bool[] _timerRunning = new bool[64];
    private readonly int[] _timerBaseTicks = new int[64];
    private readonly int[] _timerStartTick = new int[64];
    private IBaseEntity? _sharedTarget;
    private IConVar? _particleConVar;
    private IConVar? _testLettersConVar;
    private IConVar? _testLettersStartFrameConVar;
    private IConVar? _testLettersCountConVar;
    private IConVar? _updateTicksConVar;
    private bool _lettersTestEnabled = false;
    private int _lettersStartFrame = 10;
    private int _lettersCount = 26;
    private int _updateTicks = 2;

    private Dictionary<int, int> _digitMap = new()
    {
        [0] = 0,
        [1] = 1,
        [2] = 2,
        [3] = 3,
        [4] = 4,
        [5] = 5,
        [6] = 6,
        [7] = 7,
        [8] = 8,
        [9] = 9,
    };

    private readonly Dictionary<char, int> _glyphMap = new()
    {
        ['.'] = 36,
        [','] = 37,
        [':'] = 38,
        [';'] = 39,
        ['+'] = 40,
        ['-'] = 41,
        ['*'] = 42,
        ['/'] = 43,
        ['='] = 44,
        ['%'] = 45,
        ['('] = 46,
        [')'] = 47,
        ['['] = 48,
        [']'] = 49,
        ['{'] = 50,
        ['}'] = 51,
        ['<'] = 52,
        ['>'] = 53,
        ['!'] = 54,
        ['?'] = 55,
        ['@'] = 56,
        ['#'] = 57,
        ['$'] = 58,
        ['^'] = 59,
        ['&'] = 60,
        ['_'] = 61,
        ['\\'] = 62,
        ['|'] = 63,
        ['"'] = 64,
        ['\''] = 65,
        ['`'] = 66,
        ['~'] = 67
    };

    private class PlayerHudSettings
    {
        public float[] DigitOffsets = { -1.4f, -0.45f, 0.45f, 1.4f };

        public float HudScale = 0.04f;
        public float YOffset = -1f;
        public bool Enabled = false;
    }

    private class PlayerHudState
    {
        public bool IsDisposed = false;
        public IBaseParticle?[] Digits { get; } = new IBaseParticle?[HudParticleCapacity];
        public int[] LastFrames { get; } = Enumerable.Repeat(-1, HudParticleCapacity).ToArray();
        public int LastColorMode { get; set; } = -1;
        public int ActiveGlyphCount { get; set; } = 0;
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
        _testLettersConVar = convarManager.CreateConVar("ms_cspeed_test_letters", "0");
        _testLettersStartFrameConVar = convarManager.CreateConVar("ms_cspeed_test_letters_start", "14");
        _testLettersCountConVar = convarManager.CreateConVar("ms_cspeed_test_letters_count", "9");
        _updateTicksConVar = convarManager.CreateConVar("ms_cspeed_update_ticks", "1");

        _clientManager.InstallCommandCallback("hud", OnHudSettingsCommand);
        _clientManager.InstallCommandCallback("ms_cspeed_widget_set", OnWidgetSetCommand);
        _clientManager.InstallCommandCallback("ms_cspeed_widget_setline", OnWidgetSetLineCommand);
        _clientManager.InstallCommandCallback("ms_cspeed_widget_clear", OnWidgetClearCommand);
        _clientManager.InstallCommandCallback("ms_cspeed_widget_clearline", OnWidgetClearLineCommand);
        _clientManager.InstallCommandCallback("ms_cspeed_widget_mode", OnWidgetModeCommand);

        _logger.LogInformation("CenterSpeed loaded (letters control via !hud letters ...)");

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
        _callback?.Dispose();

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
        _displayModes[client.Slot] = HudDisplayMode.TimerWidget;
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
        _displayModes[client.Slot] = HudDisplayMode.Speed;
        _widgetText[client.Slot] = string.Empty;
        _timerRunning[client.Slot] = false;
        _timerBaseTicks[client.Slot] = 0;
        _timerStartTick[client.Slot] = 0;
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


        // Lazy-init the one shared info_target (never modified after creation).

        var state = new PlayerHudState();
        var settings = _playerSettings[client.Slot];
        if (settings is null)
        {
            settings = new PlayerHudSettings();
            _playerSettings[client.Slot] = settings;
        }

        if (!settings.Enabled) return;

        var particleName = _particleConVar?.GetString() ?? "particles/digits_x/digits_x.vpcf";


        for (var i = 0; i < HudParticleCapacity; i++)
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
                _logger.LogWarning("SpawnPlayerHud: failed to spawn digit {Index} for slot {Slot}", i, slot);
                continue;
            }

            particle.GetControlPointEntities()[17] = _sharedTarget.Handle;

            var hidden = GetHiddenGlyphPosition(settings);
            particle.DataControlPoint = 33;
            particle.DataControlPointValue = hidden;
            SetControlPointValue(particle, 33, hidden);

            SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f)); // digit frame (0)
            SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f)); // scale
            SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f)); // color

            particle.AcceptInput("Start");
            particle.Active = true;

            state.Digits[i] = particle;
            _transmitManager.AddEntityHooks(particle, false);
        }

        // Set visibility: only visible to the owning player
        foreach (var con in _entityManager.GetPlayerControllers(true))
        {
            for (var i = 0; i < HudParticleCapacity; i++)
            {
                var particle = state.Digits[i];
                if (particle == null) continue;
                bool shouldSee = (con.PlayerSlot == slot);
                _transmitManager.SetEntityState(particle.Index, con.Index, shouldSee, -1);
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

        foreach (var particle in state.Digits)
        {
            if (particle == null || !particle.IsValid()) continue;

            foreach (var con in _entityManager.GetPlayerControllers(true))
            {
                _transmitManager.SetEntityState(particle.Index, con.Index, false, -1);
            }

            particle.AcceptInput("Stop");
            particle.AcceptInput("DestroyImmediately");
            particle.Active = false;

            _modSharp.PushTimer(() =>
            {
                if (particle.IsValid())
                    particle.Kill();
            }, 0.1f);
        }
    }

    private int GetPlayerTimerTicks(int slot)
    {
        var ticks = _timerBaseTicks[slot];
        if (_timerRunning[slot])
        {
            var now = _modSharp.GetGlobals().TickCount;
            ticks += Math.Max(0, now - _timerStartTick[slot]);
        }

        return Math.Max(0, ticks);
    }

    private static int[] FormatTimerDigits(int ticks)
    {
        var totalSeconds = Math.Max(0, ticks) / 64;
        var minutes = Math.Min(99, totalSeconds / 60);
        var seconds = totalSeconds % 60;

        return
        [
            minutes / 10,
            minutes % 10,
            seconds / 10,
            seconds % 10
        ];
    }

    // -------------------------------------------------------------------------
    // Update timer — runs every 0.1 s

    private void PlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> retValue)
    {
        SyncRuntimeConVars();

        var slot = param.Client.Slot;
        var mode = _displayModes[slot];

        var speedMode = !_lettersTestEnabled && mode == HudDisplayMode.Speed;
        var updateTicks = speedMode ? 1 : Math.Clamp(_updateTicks, 1, 16);
        if (_modSharp.GetGlobals().TickCount % updateTicks != 0)
            return;

        var client = param.Client;
        var state = _huds[slot];

        if (client.GetPlayerController()?.Team < CStrikeTeam.TE)
        {
            KillPlayerHud(client.Slot);
            return;
        }

        if (state == null || state.IsDisposed) return;

        var controller = client.GetPlayerController();
        if (controller == null || controller.ConnectedState != PlayerConnectedState.PlayerConnected)
            return;

        var frameIndexes = new int[HudParticleCapacity];
        var glyphPositions = new Vector[HudParticleCapacity];
        var glyphCount = 0;
        var speed = 0;
        var settings = _playerSettings[slot] ??= new PlayerHudSettings();
        var lettersTestEnabled = _lettersTestEnabled;

        if (lettersTestEnabled)
        {
            var lettersCount = Math.Max(1, _lettersCount);
            var phase = (_modSharp.GetGlobals().TickCount / 10) % lettersCount;
            glyphCount = 4;
            for (var i = 0; i < 4; i++)
            {
                frameIndexes[i] = _lettersStartFrame + ((phase + i) % lettersCount);
                glyphPositions[i] = GetGlyphPosition(i, glyphCount, HudDisplayMode.Speed, settings);
            }
        }
        else
        {
            switch (mode)
            {
                case HudDisplayMode.Timer:
                {
                    var timerDigits = FormatTimerDigits(GetPlayerTimerTicks(slot));
                    glyphCount = 4;
                    for (var i = 0; i < 4; i++)
                    {
                        frameIndexes[i] = _digitMap.GetValueOrDefault(timerDigits[i], 0);
                        glyphPositions[i] = GetGlyphPosition(i, glyphCount, mode, settings);
                    }

                    break;
                }
                case HudDisplayMode.Widget:
                {
                    BuildWidgetGlyphLayout(_widgetText[slot] ?? string.Empty, settings, frameIndexes, glyphPositions, out glyphCount);

                    break;
                }
                case HudDisplayMode.TimerWidget:
                {
                    if (TryGetTimerWidgetText(slot, out var timerWidgetText))
                    {
                        BuildWidgetGlyphLayout(timerWidgetText, settings, frameIndexes, glyphPositions, out glyphCount);
                    }
                    else
                    {
                        // Timer feed may be temporarily unavailable on join/map change.
                        // Fall back to explicit text so HUD never appears blank.
                        var pawn = controller.GetPlayerPawn();
                        if (pawn != null)
                        {
                            var v = pawn.GetAbsVelocity().Length2D();
                            speed = (int)Math.Clamp(v, 0f, 9999f);
                        }
                        
                        BuildWidgetGlyphLayout($"SPD {speed:0000}", settings, frameIndexes, glyphPositions, out glyphCount);
                    }

                    break;
                }
                default:
                {
                    var pawn = controller.GetPlayerPawn();
                    if (pawn != null)
                    {
                        var v = pawn.GetAbsVelocity().Length2D();
                        speed = (int)Math.Clamp(v, 0f, 9999f);
                    }

                    var digits = new int[4]
                    {
                        speed / 1000,
                        speed / 100  % 10,
                        speed / 10   % 10,
                        speed        % 10
                    };
                    glyphCount = 4;
                    for (var i = 0; i < 4; i++)
                    {
                        frameIndexes[i] = _digitMap.GetValueOrDefault(digits[i], 0);
                        glyphPositions[i] = GetGlyphPosition(i, glyphCount, mode, settings);
                    }

                    break;
                }
            }
        }

        // 0 = down(red), 1 = up(green), 2 = same(white), 3 = forced white (timer/widget/letters test).
        var colorMode = lettersTestEnabled || mode == HudDisplayMode.Timer || mode == HudDisplayMode.Widget || mode == HudDisplayMode.TimerWidget
            ? 3
            : (_lastSpeed[slot] > speed ? 0 : (_lastSpeed[slot] < speed ? 1 : 2));

        // Update visible glyphs.
        for (var i = 0; i < glyphCount; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || state.IsDisposed)
            {
                continue;
            }

            var pos = glyphPositions[i];
            particle.DataControlPoint = 33;
            particle.DataControlPointValue = pos;
            SetControlPointValue(particle, 33, pos);

            if (state.LastFrames[i] != frameIndexes[i])
            {
                SetControlPointValue(particle, 32, new Vector((float)frameIndexes[i], 0f, 0f));
                state.LastFrames[i] = frameIndexes[i];
            }

            if (state.LastColorMode != colorMode)
            {
                switch (colorMode)
                {
                    case 0:
                        SetControlPointValue(particle, 16, new Vector(255f, 0f, 0f));
                        break;
                    case 1:
                        SetControlPointValue(particle, 16, new Vector(0f, 255f, 0f));
                        break;
                    default:
                        SetControlPointValue(particle, 16, new Vector(255f, 255f, 255f));
                        break;
                }
            }
        }

        // Hide old glyphs that are no longer used.
        var hidePos = GetHiddenGlyphPosition(settings);
        for (var i = glyphCount; i < state.ActiveGlyphCount; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || state.IsDisposed)
            {
                continue;
            }

            particle.DataControlPoint = 33;
            particle.DataControlPointValue = hidePos;
            SetControlPointValue(particle, 33, hidePos);

            if (state.LastFrames[i] != 0)
            {
                SetControlPointValue(particle, 32, new Vector(0f, 0f, 0f));
                state.LastFrames[i] = 0;
            }
        }

        state.ActiveGlyphCount = glyphCount;
        state.LastColorMode = colorMode;

        if (!lettersTestEnabled && mode == HudDisplayMode.Speed)
        {
            _lastSpeed[slot] = speed;
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
            ApplySettingsToHud(client.Slot, settings);
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
            ApplySettingsToHud(client.Slot, settings);
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
            ApplySettingsToHud(client.Slot, settings);
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
        else if (sub == "letters")
        {
            if (command.ArgCount < 2)
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud letters <on|off|start <0-255>|count <1-64>|ticks <1-16>|info>");
                return ECommandAction.Stopped;
            }

            var mode = command.GetArg(2).ToLowerInvariant();
            if (mode == "on")
            {
                _lettersTestEnabled = true;
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Letters test enabled (A-Z frames 11-36)");
            }
            else if (mode == "off")
            {
                _lettersTestEnabled = false;
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Letters test disabled");
            }
            else if (mode == "start" && command.ArgCount >= 3 && int.TryParse(command.GetArg(3), out var startFrame))
            {
                _lettersStartFrame = Math.Clamp(startFrame, 0, 255);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Letters start frame set to {_lettersStartFrame}");
            }
            else if (mode == "count" && command.ArgCount >= 3 && int.TryParse(command.GetArg(3), out var count))
            {
                _lettersCount = Math.Clamp(count, 1, 64);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Letters frame count set to {_lettersCount}");
            }
            else if (mode == "ticks" && command.ArgCount >= 3 && int.TryParse(command.GetArg(3), out var ticks))
            {
                _updateTicks = Math.Clamp(ticks, 1, 16);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Update ticks set to {_updateTicks}");
            }
            else if (mode == "info")
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Letters: enabled={_lettersTestEnabled} start={_lettersStartFrame} count={_lettersCount} ticks={_updateTicks}");
            }
            else
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud letters <on|off|start <0-255>|count <1-64>|ticks <1-16>|info>");
            }
        }
        else if (sub == "digitmap")
        {
            if (command.ArgCount < 2)
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud digitmap <0-9> <frame> | !hud digitmap info | !hud digitmap reset");
                return ECommandAction.Stopped;
            }

            var arg2 = command.GetArg(2).ToLowerInvariant();
            if (arg2 == "info")
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] DigitMap: {FormatDigitMap()}");
            }
            else if (arg2 == "reset")
            {
                ResetDigitMap();
                ForceHudRefresh(client.Slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] DigitMap reset: {FormatDigitMap()}");
            }
            else if (command.ArgCount >= 3
                     && int.TryParse(command.GetArg(2), out var digit)
                     && int.TryParse(command.GetArg(3), out var frame))
            {
                digit = Math.Clamp(digit, 0, 9);
                frame = Math.Clamp(frame, 0, 255);
                _digitMap[digit] = frame;
                ForceHudRefresh(client.Slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] DigitMap {digit} -> {frame}");
            }
            else
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud digitmap <0-9> <frame> | !hud digitmap info | !hud digitmap reset");
            }
        }
        else if (sub == "mode")
        {
            if (command.ArgCount < 2)
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud mode <speed|timer|widget|timerwidget>");
                return ECommandAction.Stopped;
            }

            var modeArg = command.GetArg(2).ToLowerInvariant();
            if (modeArg == "speed")
            {
                _displayModes[slot] = HudDisplayMode.Speed;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Mode set to speed");
            }
            else if (modeArg == "timer")
            {
                _displayModes[slot] = HudDisplayMode.Timer;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Mode set to timer (MMSS)");
            }
            else if (modeArg == "widget")
            {
                _displayModes[slot] = HudDisplayMode.Widget;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Mode set to widget");
            }
            else if (modeArg == "timerwidget" || modeArg == "timerhud")
            {
                _displayModes[slot] = HudDisplayMode.TimerWidget;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Mode set to timerwidget");
            }
            else
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud mode <speed|timer|widget|timerwidget>");
            }
        }
        else if (sub == "widget")
        {
            if (command.ArgCount < 2)
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud widget <set <text...>|line <1-5> <text...>|clear|mode|info>");
                return ECommandAction.Stopped;
            }

            var widgetArg = command.GetArg(2).ToLowerInvariant();
            if (widgetArg == "set")
            {
                var text = NormalizeWidgetText(JoinCommandArgs(command, 3));
                if (string.IsNullOrWhiteSpace(text))
                {
                    client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud widget set <text...>");
                    return ECommandAction.Stopped;
                }

                _widgetText[slot] = text.ToUpperInvariant();
                _displayModes[slot] = HudDisplayMode.Widget;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget text set ({GetWidgetVisibleCharCount(_widgetText[slot])} chars)");
            }
            else if (widgetArg == "line")
            {
                if (command.ArgCount < 4 || !int.TryParse(command.GetArg(3), out var line))
                {
                    client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud widget line <1-5> <text...>");
                    return ECommandAction.Stopped;
                }

                var lineText = NormalizeWidgetText(JoinCommandArgs(command, 4));
                if (lineText.Contains('\n'))
                {
                    lineText = lineText.Replace("\n", " ");
                }

                _widgetText[slot] = SetWidgetLine(_widgetText[slot] ?? string.Empty, Math.Clamp(line, 1, WidgetMaxLines) - 1, lineText.ToUpperInvariant());
                _displayModes[slot] = HudDisplayMode.Widget;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget line {Math.Clamp(line, 1, WidgetMaxLines)} set");
            }
            else if (widgetArg == "clear")
            {
                _widgetText[slot] = string.Empty;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Widget text cleared");
            }
            else if (widgetArg == "mode")
            {
                _displayModes[slot] = HudDisplayMode.Widget;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Mode set to widget");
            }
            else if (widgetArg == "info")
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget: chars={GetWidgetVisibleCharCount(_widgetText[slot])} lines={GetWidgetLineCount(_widgetText[slot])} text='{(_widgetText[slot] ?? string.Empty).Replace('\n', '|')}'");
            }
            else
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud widget <set <text...>|line <1-5> <text...>|clear|mode|info>");
            }
        }
        else if (sub == "timer")
        {
            if (command.ArgCount < 2)
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud timer <start|stop|reset|info>");
                return ECommandAction.Stopped;
            }

            var timerArg = command.GetArg(2).ToLowerInvariant();
            if (timerArg == "start")
            {
                if (!_timerRunning[slot])
                {
                    _timerRunning[slot] = true;
                    _timerStartTick[slot] = _modSharp.GetGlobals().TickCount;
                }
                _displayModes[slot] = HudDisplayMode.Timer;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Timer started");
            }
            else if (timerArg == "stop")
            {
                if (_timerRunning[slot])
                {
                    var now = _modSharp.GetGlobals().TickCount;
                    _timerBaseTicks[slot] += Math.Max(0, now - _timerStartTick[slot]);
                    _timerRunning[slot] = false;
                }
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Timer stopped");
            }
            else if (timerArg == "reset")
            {
                _timerBaseTicks[slot] = 0;
                _timerStartTick[slot] = _modSharp.GetGlobals().TickCount;
                ForceHudRefresh(slot);
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Timer reset");
            }
            else if (timerArg == "info")
            {
                var ticks = GetPlayerTimerTicks(slot);
                var digits = FormatTimerDigits(ticks);
                client.GetPlayerController()?.Print(
                    HudPrintChannel.Chat,
                    $" [HUD] Timer: running={_timerRunning[slot]} ticks={ticks} mmss={digits[0]}{digits[1]}{digits[2]}{digits[3]}"
                );
            }
            else
            {
                client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: !hud timer <start|stop|reset|info>");
            }
        }
        else
        {
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Subcommands: toggle | offset <1-4> <-10..10> | scale <0-10> | yoffset <-10-10> | mode <speed|timer|widget|timerwidget> | timer <...> | widget <...> | letters <...> | digitmap <...> | info");
        }
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWidgetSetCommand(IGameClient client, StringCommand command)
    {
        var slot = client.Slot;
        var text = NormalizeWidgetText(JoinCommandArgs(command, 2));
        if (string.IsNullOrWhiteSpace(text))
        {
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: ms_cspeed_widget_set <text...>");
            return ECommandAction.Stopped;
        }

        _widgetText[slot] = text.ToUpperInvariant();
        _displayModes[slot] = HudDisplayMode.Widget;
        ForceHudRefresh(slot);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWidgetSetLineCommand(IGameClient client, StringCommand command)
    {
        var slot = client.Slot;
        if (command.ArgCount < 2 || !int.TryParse(command.GetArg(2), out var line))
        {
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: ms_cspeed_widget_setline <1-5> <text...>");
            return ECommandAction.Stopped;
        }

        var lineText = NormalizeWidgetText(JoinCommandArgs(command, 3));
        if (lineText.Contains('\n'))
        {
            lineText = lineText.Replace("\n", " ");
        }

        _widgetText[slot] = SetWidgetLine(_widgetText[slot] ?? string.Empty, Math.Clamp(line, 1, WidgetMaxLines) - 1, lineText.ToUpperInvariant());
        _displayModes[slot] = HudDisplayMode.Widget;
        ForceHudRefresh(slot);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWidgetClearCommand(IGameClient client, StringCommand command)
    {
        _widgetText[client.Slot] = string.Empty;
        ForceHudRefresh(client.Slot);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWidgetClearLineCommand(IGameClient client, StringCommand command)
    {
        var slot = client.Slot;
        if (command.ArgCount < 2 || !int.TryParse(command.GetArg(2), out var line))
        {
            client.GetPlayerController()?.Print(HudPrintChannel.Chat, " [HUD] Usage: ms_cspeed_widget_clearline <1-5>");
            return ECommandAction.Stopped;
        }

        _widgetText[slot] = SetWidgetLine(_widgetText[slot] ?? string.Empty, Math.Clamp(line, 1, WidgetMaxLines) - 1, string.Empty);
        ForceHudRefresh(slot);
        return ECommandAction.Stopped;
    }

    private ECommandAction OnWidgetModeCommand(IGameClient client, StringCommand command)
    {
        _displayModes[client.Slot] = HudDisplayMode.Widget;
        ForceHudRefresh(client.Slot);
        return ECommandAction.Stopped;
    }

    private void PrintHudSettings(IGameClient client, PlayerHudSettings settings)
    {
        var o = settings.DigitOffsets;
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Offsets: 1={o[0]:F2}  2={o[1]:F2}  3={o[2]:F2}  4={o[3]:F2}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Scale: {settings.HudScale:F4}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Y-Offset: {settings.YOffset:F4}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Mode: {_displayModes[client.Slot]}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Timer: running={_timerRunning[client.Slot]} ticks={GetPlayerTimerTicks(client.Slot)}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Letters: enabled={_lettersTestEnabled} start={_lettersStartFrame} count={_lettersCount} ticks={_updateTicks}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] Widget: chars={GetWidgetVisibleCharCount(_widgetText[client.Slot])} lines={GetWidgetLineCount(_widgetText[client.Slot])} text='{(_widgetText[client.Slot] ?? string.Empty).Replace('\n', '|')}'");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] TimerWidgetFeed: hasText={TryGetTimerWidgetText(client.Slot, out _)}");
        client.GetPlayerController()?.Print(HudPrintChannel.Chat, $" [HUD] DigitMap: {FormatDigitMap()}");
    }

    // -------------------------------------------------------------------------
    // ClientPrefs integration

    public void OnAllModulesLoaded()
    {
        _cachedInterface = _modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
        if (_cachedInterface?.Instance is { } instance)
            _callback = instance.ListenOnLoad(OnCookieLoad);

        _timerHudFeedInterface = _modules.GetOptionalSharpModuleInterface<ITimerHudFeed>(ITimerHudFeed.Identity);
    }

    public void OnLibraryConnected(string name)
    {
        if (name.Equals("ClientPreferences"))
        {
            _cachedInterface = _modules.GetRequiredSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
            if (_cachedInterface?.Instance is { } instance)
                _callback = instance.ListenOnLoad(OnCookieLoad);
        }

        if (name.Equals(ITimerHudFeed.Identity))
            _timerHudFeedInterface = _modules.GetOptionalSharpModuleInterface<ITimerHudFeed>(ITimerHudFeed.Identity);
    }

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals("ClientPreferences"))
            _cachedInterface = null;

        if (name.Equals(ITimerHudFeed.Identity))
            _timerHudFeedInterface = null;
    }

    private IClientPreference? GetInterface()
    {
        if (_cachedInterface?.Instance is null)
        {
            _cachedInterface = _modules.GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity);
            if (_cachedInterface?.Instance is { } instance)
                _callback = instance.ListenOnLoad(OnCookieLoad);
        }
        return _cachedInterface?.Instance;
    }

    private ITimerHudFeed? GetTimerHudFeed()
    {
        if (_timerHudFeedInterface?.Instance is null)
            _timerHudFeedInterface = _modules.GetOptionalSharpModuleInterface<ITimerHudFeed>(ITimerHudFeed.Identity);

        return _timerHudFeedInterface?.Instance;
    }

    private bool TryGetTimerWidgetText(int slot, out string text)
    {
        text = string.Empty;
        var feed = GetTimerHudFeed();
        return feed != null && feed.TryGetWidgetText(slot, out text) && !string.IsNullOrWhiteSpace(text);
    }

    private void OnCookieLoad(IGameClient client)
    {
        if (GetInterface() is not { } cp) return;

        var settings = _playerSettings[client.Slot] ??= new PlayerHudSettings();
        var id = client.SteamId;

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
        if (GetInterface() is not { } cp) return;

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

    private void SyncRuntimeConVars()
    {
        if (_testLettersConVar != null && int.TryParse(_testLettersConVar.GetString(), out var lettersEnabled))
        {
            _lettersTestEnabled = lettersEnabled != 0;
        }

        if (_testLettersStartFrameConVar != null && int.TryParse(_testLettersStartFrameConVar.GetString(), out var startFrame))
        {
            _lettersStartFrame = Math.Clamp(startFrame, 0, 255);
        }

        if (_testLettersCountConVar != null && int.TryParse(_testLettersCountConVar.GetString(), out var count))
        {
            _lettersCount = Math.Clamp(count, 1, 64);
        }

        if (_updateTicksConVar != null && int.TryParse(_updateTicksConVar.GetString(), out var ticks))
        {
            _updateTicks = Math.Clamp(ticks, 1, 16);
        }
    }

    private Vector GetHiddenGlyphPosition(PlayerHudSettings settings)
    {
        return new Vector(0f, settings.YOffset - 20f, 0f);
    }

    private Vector GetGlyphPosition(int index, int glyphCount, HudDisplayMode mode, PlayerHudSettings settings)
    {
        if (mode == HudDisplayMode.Widget)
        {
            var startX = -((glyphCount - 1) * WidgetCharSpacing * 0.5f);
            var x = startX + (index * WidgetCharSpacing);
            return new Vector(x, settings.YOffset, 0f);
        }

        // Speed/timer are fixed to 4-digit offsets for backwards compatibility.
        var offsetIndex = Math.Clamp(index, 0, settings.DigitOffsets.Length - 1);
        return new Vector(settings.DigitOffsets[offsetIndex], settings.YOffset, 0f);
    }

    private void BuildWidgetGlyphLayout(
        string text,
        PlayerHudSettings settings,
        int[] frames,
        Vector[] positions,
        out int glyphCount)
    {
        glyphCount = 0;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var normalized = NormalizeWidgetText(text).ToUpperInvariant();
        var lines = normalized.Split('\n')
            .Take(WidgetMaxLines)
            .Select(l => l.Length > HudParticleCapacity ? l[..HudParticleCapacity] : l)
            .ToArray();

        var nonEmptyLines = lines.Length == 0 ? 1 : lines.Length;
        var topY = settings.YOffset + ((nonEmptyLines - 1) * WidgetLineSpacing * 0.5f);

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Length == 0)
            {
                continue;
            }

            var startX = -((line.Length - 1) * WidgetCharSpacing * 0.5f);
            var lineY = topY - (lineIndex * WidgetLineSpacing);

            for (var charIndex = 0; charIndex < line.Length && glyphCount < HudParticleCapacity; charIndex++)
            {
                if (TryMapGlyphFrame(line[charIndex], out var frame))
                {
                    frames[glyphCount] = frame;
                    positions[glyphCount] = new Vector(startX + (charIndex * WidgetCharSpacing), lineY, 0f);
                    glyphCount++;
                }
            }
        }
    }

    private bool TryMapGlyphFrame(char c, out int frame)
    {
        frame = 0;
        if (c == ' ')
        {
            return false;
        }

        if (char.IsDigit(c))
        {
            frame = _digitMap.GetValueOrDefault(c - '0', 0);
            return true;
        }

        if (char.IsLetter(c))
        {
            var upper = char.ToUpperInvariant(c);
            frame = 10 + (upper - 'A');
            return true;
        }

        if (_glyphMap.TryGetValue(c, out frame))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeWidgetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Replace("\\n", "\n").Replace("\r", string.Empty);
    }

    private static int GetWidgetVisibleCharCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return text.Count(c => c != '\n' && c != '\r');
    }

    private static int GetWidgetLineCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return NormalizeWidgetText(text).Split('\n').Length;
    }

    private static string SetWidgetLine(string source, int lineIndex, string lineValue)
    {
        var lines = NormalizeWidgetText(source).Split('\n').ToList();
        while (lines.Count <= lineIndex)
        {
            lines.Add(string.Empty);
        }

        lines[lineIndex] = lineValue;

        while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines);
    }

    private static string JoinCommandArgs(StringCommand command, int startIndex)
    {
        if (command.ArgCount < startIndex)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (var i = startIndex; i <= command.ArgCount; i++)
        {
            parts.Add(command.GetArg(i));
        }

        return string.Join(" ", parts).Trim();
    }

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

    private void ApplySettingsToHud(int slot, PlayerHudSettings settings)
    {
        var state = _huds[slot];
        if (state == null || state.IsDisposed)
            return;

        var mode = _displayModes[slot];
        var glyphCount = 4;
        var glyphPositions = new Vector[HudParticleCapacity];
        if (mode == HudDisplayMode.Widget)
        {
            var scratchFrames = new int[HudParticleCapacity];
            BuildWidgetGlyphLayout(_widgetText[slot] ?? string.Empty, settings, scratchFrames, glyphPositions, out glyphCount);
        }

        for (var i = 0; i < HudParticleCapacity; i++)
        {
            var particle = state.Digits[i];
            if (particle == null || !particle.IsValid())
                continue;

            var cp33 = i < glyphCount
                ? (mode == HudDisplayMode.Widget ? glyphPositions[i] : GetGlyphPosition(i, glyphCount, mode, settings))
                : GetHiddenGlyphPosition(settings);

            particle.DataControlPoint = 33;
            particle.DataControlPointValue = cp33;
            SetControlPointValue(particle, 33, cp33);
            SetControlPointValue(particle, 34, new Vector(settings.HudScale, 0f, 0f));
        }
    }

    private void ForceHudRefresh(int slot)
    {
        var state = _huds[slot];
        if (state == null || state.IsDisposed)
            return;

        for (var i = 0; i < state.LastFrames.Length; i++)
            state.LastFrames[i] = -1;
        state.LastColorMode = -1;
    }

    private string FormatDigitMap()
    {
        return string.Join(" ", Enumerable.Range(0, 10).Select(d => $"{d}={_digitMap.GetValueOrDefault(d, 0)}"));
    }

    private void ResetDigitMap()
    {
        _digitMap = new Dictionary<int, int>
        {
            [0] = 1,
            [1] = 2,
            [2] = 3,
            [3] = 4,
            [4] = 5,
            [5] = 6,
            [6] = 7,
            [7] = 8,
            [8] = 9,
            [9] = 10,
        };
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
