
// Written by:
// 
// ███╗   ██╗ ██████╗  ██████╗██╗  ██╗ █████╗ ██╗      █████╗ 
// ████╗  ██║██╔═══██╗██╔════╝██║  ██║██╔══██╗██║     ██╔══██╗
// ██╔██╗ ██║██║   ██║██║     ███████║███████║██║     ███████║
// ██║╚██╗██║██║   ██║██║     ██╔══██║██╔══██║██║     ██╔══██║
// ██║ ╚████║╚██████╔╝╚██████╗██║  ██║██║  ██║███████╗██║  ██║
// ╚═╝  ╚═══╝ ╚═════╝  ╚═════╝╚═╝  ╚═╝╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝
//
//          ░░▒▒▓▓ https://github.com/Nochala ▓▓▒▒░░

using GTA;
using GTA.Native;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using GTAControl = GTA.Control;

using EngineStateManager;
public sealed class EngineStateControl : Script
{
    private enum EngineOverrideState
    {
        None = 0,
        ForceOn = 1,
        ForceOff = 2
    }


    // Stable model hashing across SHVDN builds (avoids obsolete Game.GenerateHash).
    private static int HashModel(string modelName)
    {
        return Function.Call<int>(Hash.GET_HASH_KEY, modelName);
    }

    // INI
    private static bool _enabled = true;
    private static bool _animationsEnabled = true;
    private static int _toggleVk = 0x5A; // Z
    private static Keys _toggleKey = (Keys)0x5A;

    private static bool _controllerEnabled = false;
    private static GTAControl _controllerMain = GTAControl.VehicleDuck;
    // Mod load notification (logo overlay)
    private readonly ModLoadNotification _loadNotification = new ModLoadNotification();
    private EngineOverrideState _override = EngineOverrideState.None;
    private int _targetVehicleHandle = 0;
    private int _blockRestartUntilGameTime = 0;
    private bool _keyWasDown = false;
    private bool _controllerWasDown = false;
    private int _lastToggleGameTime = 0;
    private int _queuedEngineStateWaitUntilGameTime = 0;

    private bool _helmetSuppressed = false;
    private int _helmetSuppressUntilGameTime = 0;

    private bool _tickFaulted = false;

    private const string BusOwner = "EngineStateControl";

    private const string AnimDict = "veh@std@ds@base";
    private const string AnimName = "change_station";

    // ---------- Vehicle-specific animation profiles (model-hash based) ----------
    private struct AnimProfile
    {
        public string StartDict;
        public string StartName;
        public int StartDuration;

        public string StopDict;
        public string StopName;
        public int StopDuration;

        public AnimProfile(string startDict, string startName, int startDur, string stopDict, string stopName, int stopDur)
        {
            StartDict = startDict;
            StartName = startName;
            StartDuration = startDur;
            StopDict = stopDict;
            StopName = stopName;
            StopDuration = stopDur;
        }
    }

    private static bool _bikeProfilesReady = false;
    private static Dictionary<int, AnimProfile> _bikeProfiles;

    private static void EnsureBikeProfiles()
    {
        if (_bikeProfilesReady)
            return;

        _bikeProfilesReady = true;
        _bikeProfiles = new Dictionary<int, AnimProfile>(64);

        void Add(string modelName, AnimProfile p) => _bikeProfiles[HashModel(modelName)] = p;

        // Dirt / offroad (kick-start)
        var dirt = new AnimProfile(
            "veh@bike@dirt@front@base", "pov_start_engine", 1100,
            "veh@bike@dirt@front@base", "stop_engine", 800
        );
        Add("sanchez", dirt);
        Add("sanchez2", dirt);
        Add("manchez", dirt);
        Add("manchez2", dirt);
        Add("enduro", dirt);
        Add("bf400", dirt);
        Add("cliffhanger", dirt);

        // Sport / street
        var sport = new AnimProfile(
            "veh@bike@sport@front@base", "start_engine", 900,
            "veh@bike@sport@front@base", "stop_engine", 800
        );
        Add("bati", sport);
        Add("bati2", sport);
        Add("akuma", sport);
        Add("double", sport);
        Add("carbonrs", sport);
        Add("nemesis", sport);
        Add("ruffian", sport);
        Add("vader", sport);
        Add("hakuchou", sport);
        Add("hakuchou2", sport);
        Add("shotaro", sport);
        Add("lectro", sport);
        Add("diablous", sport);
        Add("diablous2", sport);
        Add("fcr", sport);
        Add("fcr2", sport);

        // Choppers / cruisers
        var chopper = new AnimProfile(
            "veh@bike@chopper@front@base", "start_engine", 900,
            "veh@bike@chopper@front@base", "stop_engine", 800
        );
        Add("daemon", chopper);
        Add("daemon2", chopper);
        Add("hexer", chopper);
        Add("innovation", chopper);
        Add("nightblade", chopper);
        Add("zombiea", chopper);
        Add("zombieb", chopper);
        Add("wolfsbane", chopper);
        Add("gargoyle", chopper);
        Add("avarus", chopper);
        Add("bagger", chopper);
        Add("chimera", chopper);

        // Scooters / mopeds
        var scooter = new AnimProfile(
            "veh@bike@scooter@front@base", "start_engine", 900,
            "veh@bike@scooter@front@base", "stop_engine", 800
        );
        Add("faggio", scooter);
        Add("faggio2", scooter);
        Add("faggio3", scooter);

        // If you want per-model tuning later, extend the map above. Unknown models fall back to generic motorcycle clips.
    }

    private static bool TrySelectMotorcycleAnimByModel(Vehicle veh, bool turnOff, out string dict, out string name, out int duration)
    {
        dict = null;
        name = null;
        duration = 0;

        if (veh == null || !veh.Exists())
            return false;

        EnsureBikeProfiles();

        if (_bikeProfiles != null && _bikeProfiles.TryGetValue(veh.Model.Hash, out AnimProfile p))
        {
            if (turnOff)
            {
                dict = p.StopDict;
                name = p.StopName;
                duration = p.StopDuration;
            }
            else
            {
                dict = p.StartDict;
                name = p.StartName;
                duration = p.StartDuration;
            }
            return true;
        }

        // Generic motorcycle fallback (no max-speed heuristics).
        if (turnOff)
        {
            dict = "veh@bike@sport@front@base";
            name = "stop_engine";
            duration = 800;
        }
        else
        {
            dict = "veh@bike@sport@front@base";
            name = "start_engine";
            duration = 900;
        }
        return true;
    }
    private bool _pendingAnim = false;
    private int _animRequestUntilGameTime = 0;

    // Stronger "no flip" guards: tie a queued anim to a specific request + intended engine state.
    private int _nextAnimRequestId = 1;
    private int _queuedAnimRequestId = 0;
    private bool _queuedTargetEngineOn = false;

    private int _queuedPedHandle = 0;
    private int _queuedVehicleHandle = 0;
    private bool _queuedTurnOff = false;
    private int _queuedAnimDuration = 650;
    private string _queuedAnimDict = AnimDict;
    private string _queuedAnimName = AnimName;

    public EngineStateControl()
    {
        LoadIni();


        _loadNotification.Initialize();
        Tick += OnTick;
        KeyDown += OnKeyDown;

        Interval = 0;

        LogInfo($"EngineStateControl loaded. Enabled={_enabled} KeyVK=0x{_toggleVk:X2} ({_toggleKey}) Animations={_animationsEnabled} ControllerEnabled={_controllerEnabled} ControllerMain={_controllerMain}");
    }

    private static void LoadIni()
    {
        _enabled = MainConfig.EngineToggleEnabled;
        _animationsEnabled = MainConfig.EngineToggleAnimations;

        // Keyboard binding (Virtual-Key)
        _toggleVk = MainConfig.EngineToggleMainVk;
        _toggleKey = (Keys)_toggleVk;

        if (_toggleVk == 0)
        {
            string keyString = MainConfig.EngineToggleKeyString ?? "Z";
            if (!Enum.TryParse(keyString, true, out Keys parsed))
                parsed = Keys.Z;

            _toggleVk = (int)parsed;
            _toggleKey = parsed;
        }

        // Controller binding (optional)
        _controllerEnabled = MainConfig.EngineToggleControllerEnabled;
        _controllerMain = MainConfig.EngineToggleControllerMain;
    }

    private void OnTick(object sender, EventArgs e)
    {
        UpdateBikeHelmetSuppression();

        if (_tickFaulted)
            return;

        try
        {
            _loadNotification.OnTick();

            if (!_enabled)
            {
                if (_override != EngineOverrideState.None || _targetVehicleHandle != 0)
                    ClearOverride("Feature disabled by INI.");

                _pendingAnim = false;
                _keyWasDown = false;
                _controllerWasDown = false;
                return;
            }

            bool kbDown = Game.IsKeyPressed(_toggleKey);
            bool padDown = _controllerEnabled && Game.IsControlPressed(_controllerMain);

            // Snapshot previous states
            bool prevKbDown = _keyWasDown;
            bool prevPadDown = _controllerWasDown;

            _keyWasDown = kbDown;
            _controllerWasDown = padDown;

            // Rising-edge trigger
            bool trigger = (kbDown && !prevKbDown) || (padDown && !prevPadDown);

            if (trigger)
            {
                int now = Game.GameTime;
                if (now - _lastToggleGameTime > 150)
                {
                    if (!IsBlockedByUI())
                    {
                        _lastToggleGameTime = now;
                        ToggleForCurrentVehicle();
                    }
                }
            }
            _keyWasDown = kbDown;
            _controllerWasDown = padDown;

            if (_animationsEnabled)
                ProcessPendingAnim();

            EnforceOverrideIfNeeded();
        }
        catch (Exception ex)
        {
            _tickFaulted = true;
            try { LogInfo("FATAL: EngineStateControl Tick exception; disabling script loop. " + ex); } catch { }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_enabled)
            return;

        if (e.KeyCode != _toggleKey)
            return;

        if (IsBlockedByUI())
            return;

        if (Game.GameTime - _lastToggleGameTime <= 150)
            return;

        _lastToggleGameTime = Game.GameTime;
        ToggleForCurrentVehicle();
    }

    private void ToggleForCurrentVehicle()
    {
        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || !ped.IsInVehicle())
            return;

        Vehicle veh = ped.CurrentVehicle;
        if (veh == null || !veh.Exists())
            return;

        _targetVehicleHandle = veh.Handle;

        bool running = IsEngineRunning(veh);

        if (_animationsEnabled)
            QueueToggleAnim(ped, veh, turnOff: running);

        _override = running ? EngineOverrideState.ForceOff : EngineOverrideState.ForceOn;

        // Cooperative override: use High priority so other mods can still take Critical if needed.
        EngineOverrideBus.Set(
            _override == EngineOverrideState.ForceOff ? EngineIntent.ForceOff : EngineIntent.ForceOn,
            EngineIntentPriority.High,
            durationMs: 0, // indefinite until cleared
            owner: BusOwner
        );

        if (_override == EngineOverrideState.ForceOff)
            _blockRestartUntilGameTime = Game.GameTime + 500;
        else
            _blockRestartUntilGameTime = 0;

        ApplyOverrideToVehicle(veh, _override);

        LogInfo($"Toggle: Veh={_targetVehicleHandle} WasRunning={running} Override={_override}");
    }

    private void EnforceOverrideIfNeeded()
    {
        if (_override == EngineOverrideState.None || _targetVehicleHandle == 0)
            return;

        var intent = EngineOverrideBus.GetCurrent(out string busOwner, out EngineIntentPriority busPri, out int _);
        if (intent != EngineIntent.None && !string.Equals(busOwner, BusOwner, StringComparison.Ordinal))
        {
            ClearOverride("Yielding to another override owner: " + busOwner, clearBus: false);
            return;
        }

        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists())
        {
            ClearOverride("Player ped invalid.");
            return;
        }

        if (!ped.IsInVehicle())
        {
            ClearOverride("Player left vehicle.");
            return;
        }

        Vehicle current = ped.CurrentVehicle;
        if (current == null || !current.Exists())
        {
            ClearOverride("Current vehicle invalid.");
            return;
        }

        if (current.Handle != _targetVehicleHandle)
        {
            ClearOverride("Switched vehicles.");
            return;
        }

        if (_override == EngineOverrideState.ForceOff && Game.GameTime < _blockRestartUntilGameTime)
            return;

        ApplyOverrideToVehicle(current, _override);
    }

    private void ApplyOverrideToVehicle(Vehicle veh, EngineOverrideState state)
    {
        switch (state)
        {
            case EngineOverrideState.ForceOn:
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, true, false, false);
                break;

            case EngineOverrideState.ForceOff:
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, veh.Handle, false, false, false);
                break;
        }
    }

    private void ClearOverride(string reason, bool clearBus = true)
    {
        LogInfo($"ClearOverride: {reason}");

        if (clearBus)
            EngineOverrideBus.Clear(BusOwner);

        _override = EngineOverrideState.None;
        _targetVehicleHandle = 0;
        _blockRestartUntilGameTime = 0;
    }

    private static bool IsEngineRunning(Vehicle veh)
        => Function.Call<bool>(Hash.GET_IS_VEHICLE_ENGINE_RUNNING, veh.Handle);

    private static bool IsBlockedByUI()
    {
        if (Game.IsPaused)
            return true;

        if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE))
            return true;

        int kb = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
        return kb == 0 || kb == 1;
    }

    private static bool TrySelectEngineStartAnim(Vehicle veh, out string animDict, out string animName, out int durationMs)
    {
        // Legacy helper kept for compatibility with older code paths.
        animDict = AnimDict;
        animName = AnimName;
        durationMs = 650;

        if (veh == null || !veh.Exists())
            return false;

        Model m = veh.Model;

        // Bicycles shouldn't play an engine animation.
        try
        {
            if (Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, m.Hash))
                return false;
        }
        catch { }

        // Motorcycles: use per-model selection (startup).
        if (veh.ClassType == VehicleClass.Motorcycles)
        {
            if (TrySelectMotorcycleAnimByModel(veh, turnOff: false, out string d, out string n, out int dur))
            {
                animDict = d;
                animName = n;
                durationMs = dur;
                return true;
            }
        }

        // Helicopters
        if (m.IsHelicopter)
        {
            animDict = "veh@helicopter@frogger@ds@base";
            animName = "pov_start_engine";
            durationMs = 1200;
            return true;
        }

        // Planes
        if (m.IsPlane)
        {
            animDict = "veh@plane@stunt@front@ds@base";
            animName = "start_engine";
            durationMs = 1200;
            return true;
        }

        // Default: keep original car-style clip.
        animDict = AnimDict;
        animName = AnimName;
        durationMs = 650;
        return true;
    }



    // ---------- Helicopter & ATV (quad) animation profiles 
    private static Dictionary<int, AnimProfile> _heliProfiles;

    private static void EnsureHeliProfiles()
    {
        if (_heliProfiles != null)
            return;

        _heliProfiles = new Dictionary<int, AnimProfile>(32);

        void Add(string modelName, AnimProfile p) => _heliProfiles[HashModel(modelName)] = p;

        var frogger = new AnimProfile(
            "veh@helicopter@ds@base", "change_station", 900,
            "veh@helicopter@ds@base", "change_station", 700
        );
        Add("frogger", frogger);

        // Savage has a dedicated startup clip in-game.
        var savage = new AnimProfile(
            "veh@savage@front@ds@base", "pov_start_engine", 1200,
            "veh@helicopter@ds@base", "change_station", 700
        );
        Add("savage", savage);

        // Generic helicopter fallback (for most models)
        _heliProfiles[0] = new AnimProfile(
            "veh@helicopter@ds@base", "change_station", 900,
            "veh@helicopter@ds@base", "change_station", 700
        );
    }

    private static bool TrySelectHeliAnimByModel(Vehicle veh, bool turnOff, out string dict, out string name, out int duration)
    {
        dict = null;
        name = null;
        duration = 0;

        if (veh == null || !veh.Exists())
            return false;

        EnsureHeliProfiles();

        if (_heliProfiles != null && _heliProfiles.TryGetValue(veh.Model.Hash, out AnimProfile p))
        {
            if (turnOff)
            {
                dict = p.StopDict;
                name = p.StopName;
                duration = p.StopDuration;
            }
            else
            {
                dict = p.StartDict;
                name = p.StartName;
                duration = p.StartDuration;
            }

            return !(string.IsNullOrEmpty(dict) || string.IsNullOrEmpty(name) || duration <= 0);
        }

        // Default helicopter profile
        var d = _heliProfiles[0];
        if (turnOff)
        {
            dict = d.StopDict; name = d.StopName; duration = d.StopDuration;
        }
        else
        {
            dict = d.StartDict; name = d.StartName; duration = d.StartDuration;
        }
        return true;
    }

    private static bool IsBlazerQuad(int modelHash)
    {
        // Quad bikes (Blazer variants)
        return modelHash == HashModel("blazer")
            || modelHash == HashModel("blazer2")
            || modelHash == HashModel("blazer3")
            || modelHash == HashModel("blazer4")
            || modelHash == HashModel("blazer5");
    }

    private static bool TrySelectQuadAnim(Vehicle veh, bool turnOff, out string dict, out string name, out int duration)
    {
        dict = null;
        name = null;
        duration = 0;

        if (veh == null || !veh.Exists())
            return false;

        if (!IsBlazerQuad(veh.Model.Hash))
            return false;

        // Quad bikes have their own dict with a proper start clip.
        // For shutdown, use a neutral interaction in the same dict.
        dict = "veh@bike@quad@front@base";
        if (turnOff)
        {
            name = "change_station";
            duration = 650;
        }
        else
        {
            name = "start_engine";
            duration = 900;
        }
        return true;
    }

    private void QueueToggleAnim(Ped ped, Vehicle veh, bool turnOff)
    {
        // Defaults (safe fallback)
        string dict = AnimDict;
        string name = AnimName;
        int duration = 650;

        if (turnOff)
        {
            // =========================
            // ENGINE SHUTDOWN
            // =========================

            if (veh != null && veh.Exists())
            {
                var model = veh.Model;

                // Motorcycles
                if (veh.ClassType == VehicleClass.Motorcycles)
                {
                    if (TrySelectMotorcycleAnimByModel(veh, turnOff: true, out string md, out string mn, out int mdur))
                    {
                        dict = md;
                        name = mn;
                        duration = mdur;
                    }
                }
                // Helicopters
                else if (model.IsHelicopter)
                {
                    if (TrySelectHeliAnimByModel(veh, turnOff: true, out string hd, out string hn, out int hdur))
                    {
                        dict = hd;
                        name = hn;
                        duration = hdur;
                    }
                    else
                    {
                        dict = "veh@helicopter@ds@base";
                        name = "change_station";
                        duration = 700;
                    }
                }
                // Planes
                else if (model.IsPlane)
                {
                    dict = "veh@plane@stunt@front@ds@base";
                    name = "stop_engine";
                    duration = 1000;
                }
                // Default cars / quads
                else
                {
                    if (TrySelectQuadAnim(veh, turnOff: true, out string qd, out string qn, out int qdur))
                    {
                        dict = qd;
                        name = qn;
                        duration = qdur;
                    }
                    else
                    {
                        dict = "veh@std@ds@base";
                        name = "turn_off";
                        duration = 700;
                    }
                }
            }
            else
            {
                duration = 600;
            }
        }
        else
        {
            // =========================
            // ENGINE START
            // =========================

            if (veh != null && veh.Exists())
            {
                var model = veh.Model;

                // Motorcycles
                if (veh.ClassType == VehicleClass.Motorcycles)
                {
                    if (TrySelectMotorcycleAnimByModel(veh, turnOff: false, out string md, out string mn, out int mdur))
                    {
                        dict = md;
                        name = mn;
                        duration = mdur;
                    }
                }
                // Helicopters
                else if (model.IsHelicopter)
                {
                    if (TrySelectHeliAnimByModel(veh, turnOff: false, out string hd, out string hn, out int hdur))
                    {
                        dict = hd;
                        name = hn;
                        duration = hdur;
                    }
                    else
                    {
                        dict = "veh@helicopter@ds@base";
                        name = "change_station";
                        duration = 900;
                    }
                }
                // Planes
                else if (model.IsPlane)
                {
                    dict = "veh@plane@stunt@front@ds@base";
                    name = "start_engine";
                    duration = 1200;
                }
                // Default cars / quads
                else
                {
                    if (TrySelectQuadAnim(veh, turnOff: false, out string qd, out string qn, out int qdur))
                    {
                        dict = qd;
                        name = qn;
                        duration = qdur;
                    }
                    else
                    {
                        dict = "veh@std@ds@base";
                        name = "change_station";
                        duration = 700;
                    }
                }
            }
        }


        Function.Call(Hash.REQUEST_ANIM_DICT, dict);

        _pendingAnim = true;
        _queuedAnimRequestId = _nextAnimRequestId++;
        _queuedTargetEngineOn = !turnOff;
        _queuedPedHandle = ped.Handle;
        _queuedVehicleHandle = veh.Handle;
        _queuedTurnOff = turnOff;
        _queuedAnimDuration = duration;
        _queuedEngineStateWaitUntilGameTime = Game.GameTime + 350; 

        // Prevent GTA auto-helmet animation from overlapping our bike start/stop animation.
        SuppressBikeHelmetIfNeeded(ped, veh, duration);
        _queuedAnimDict = dict;
        _queuedAnimName = name;
        _animRequestUntilGameTime = Game.GameTime + 500;
    }

    private void ProcessPendingAnim()
    {
        if (!_pendingAnim)
            return;

        Ped ped = Game.Player.Character;
        if (ped == null || !ped.Exists() || ped.Handle != _queuedPedHandle)
        {
            _pendingAnim = false;
            return;
        }

        if (!ped.IsInVehicle())
        {
            _pendingAnim = false;
            return;
        }

        Vehicle veh = ped.CurrentVehicle;
        if (veh == null || !veh.Exists() || veh.Handle != _queuedVehicleHandle)
        {
            _pendingAnim = false;
            return;
        }

        bool runningNow = Function.Call<bool>(Hash.GET_IS_VEHICLE_ENGINE_RUNNING, veh.Handle);

        if (runningNow != _queuedTargetEngineOn)
        {
            if (Game.GameTime <= _queuedEngineStateWaitUntilGameTime)
                return; 

            _pendingAnim = false; 
            return;
        }

        if (ped != veh.GetPedOnSeat(VehicleSeat.Driver))
        {
            _pendingAnim = false;
            return;
        }

        if (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, _queuedAnimDict))
        {
            if (Game.GameTime <= _animRequestUntilGameTime)
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, _queuedAnimDict);
                return;
            }

            if (!string.Equals(_queuedAnimDict, AnimDict, StringComparison.Ordinal))
            {
                _queuedAnimDict = AnimDict;
                _queuedAnimName = AnimName;
                _queuedAnimDuration = 650;
                _animRequestUntilGameTime = Game.GameTime + 500;
                Function.Call(Hash.REQUEST_ANIM_DICT, _queuedAnimDict);
                return;
            }

            _pendingAnim = false;
            return;
        }

        // One last guard
        Function.Call(Hash.TASK_PLAY_ANIM,
            ped.Handle,
            _queuedAnimDict,
            _queuedAnimName,
            8.0f,
            1.0f,
            _queuedAnimDuration,
            48,
            0.1f,
            false, false, false);

        _pendingAnim = false;
    }

    // ---------- Logging ----------

    private static void LogInfo(string msg)
    {
        try
        {
            if (EngineStateManager.ModLogger.Enabled)
                EngineStateManager.ModLogger.Info(msg);
        }
        catch { }
    }


    private void SuppressBikeHelmetIfNeeded(Ped ped, Vehicle veh, int ms)
    {
        if (ped == null || !ped.Exists() || veh == null || !veh.Exists())
            return;

        // Only relevant for motorbikes / quads 
        if (veh.ClassType != VehicleClass.Motorcycles)
            return;

        try
        {
            Function.Call(Hash.SET_PED_CONFIG_FLAG, ped.Handle, 35, false); 
                                                                           
            Function.Call((Hash)0xA7B2458D0AD6DED8, ped.Handle, true); 
        }
        catch { /* fail-safe: never break script */ }

        _helmetSuppressed = true;
        _helmetSuppressUntilGameTime = Game.GameTime + Math.Max(250, ms);
    }

    private void UpdateBikeHelmetSuppression()
    {
        if (!_helmetSuppressed)
            return;

        if (Game.GameTime < _helmetSuppressUntilGameTime)
            return;

        var ped = Game.Player.Character;
        if (ped != null && ped.Exists())
        {
            try
            {
                Function.Call(Hash.SET_PED_CONFIG_FLAG, ped.Handle, 35, true);
            }
            catch { /* ignore */ }
        }

        _helmetSuppressed = false;
        _helmetSuppressUntilGameTime = 0;
    }
}