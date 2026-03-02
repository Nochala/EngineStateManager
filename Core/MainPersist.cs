
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

using System;
using System.IO;
using System.Collections.Generic;
using GTA;
using GTA.Native;
namespace EngineStateManager
{

    public sealed class MainPersist : Script
    {
        private const string IniFileName = "EngineStateManager.ini";

        // [Settings]
        private bool _disableAutoStart = true;
        private bool _disableAutoShutdown = true;

        private int _entryEnforceMs = 900;
        private int _exitEnforceMs = 1400;


        // Tracking limits (feet). Semantics:
        //   -1 = infinite tracking
        //    0 = disable tracking/persistence entirely
        //   >0 = distance cutoff in feet
        private float _maxDistanceToTrackFeet = -1f;
        private const float FeetPerMeter = 3.28084f;

        // LRU
        private int _maxTrackedVehicle = 1;

        private readonly LinkedList<int> _persistedLru = new LinkedList<int>();
        private readonly HashSet<int> _persistedSet = new HashSet<int>();

        private bool _entryArmed;
        private int _entryVehicleHandle;
        private int _entryEnforceUntilGameTime;

        private bool _exitArmed;
        private int _exitVehicleHandle;
        private int _exitEnforceUntilGameTime;

        private bool _wasInVehicle;
        private int _lastVehicleHandle;
        private bool _lastVehicleEngineOn;
        private bool _lastVehicleWasAircraft;
        private int _lastEngineOnGameTime;

        private int _keepEngineOnVehicleHandle;

        // Blocked-driver-door edge case: player can shuffle to passenger seat before exiting.
        // If they were the driver very recently, keep the engine-on flag alive long enough to cover the exit.
        private int _recentDriverVehicleHandle;
        private int _recentDriverGameTime;
        private bool _recentDriverEngineWasOn;
        private const int RecentDriverGraceMs = 2500;

        public MainPersist()
        {
            Interval = 50;

            LoadIniOnce();

            Tick += OnTick;
            Aborted += OnAborted;

            // Initialize transition tracking
            var p = Game.Player.Character;
            _wasInVehicle = p != null && p.Exists() && p.IsInVehicle();
            if (_wasInVehicle && p.CurrentVehicle != null && p.CurrentVehicle.Exists())
            {
                _lastVehicleHandle = p.CurrentVehicle.Handle;
                _lastVehicleEngineOn = NativeCompat.IsVehicleEngineOn(_lastVehicleHandle);
                if (_lastVehicleEngineOn) _lastEngineOnGameTime = Game.GameTime;
                _lastVehicleWasAircraft = IsAircraft(p.CurrentVehicle);
            }

            LogInfo($"MainPersist loaded. DisableAutoStart={_disableAutoStart}, DisableAutoShutdown={_disableAutoShutdown}, EntryEnforceMs={_entryEnforceMs}, ExitEnforceMs={_exitEnforceMs}");
        }

        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                if (_exitVehicleHandle != 0)
                    NativeCompat.SetVehicleKeepEngineOn(_exitVehicleHandle, false);
            }
            catch { }
        }

        private void OnTick(object sender, EventArgs e)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            int desiredInterval = _exitArmed ? 0 : 50;
            if (Interval != desiredInterval) Interval = desiredInterval;

            bool inVehicle = player.IsInVehicle();
            Vehicle currentVeh = inVehicle ? player.CurrentVehicle : null;

            bool carPersistEnabled = IsCarPersistenceEnabled();
            if (carPersistEnabled)
                MaintainPersistedVehicles(player);
            else
                ClearAllPersistedVehicles();

            if (_disableAutoShutdown)
                MaintainKeepEngineOnFlag(player, currentVeh);
            else
                ClearKeepEngineOnFlagIfAny();

            if (inVehicle && currentVeh != null && currentVeh.Exists())
            {
                // Track recent driver status (covers seat-shuffle exits when driver door is blocked).
                if (currentVeh.Driver == player)
                {
                    _recentDriverVehicleHandle = currentVeh.Handle;
                    _recentDriverGameTime = Game.GameTime;
                    _recentDriverEngineWasOn = NativeCompat.IsVehicleEngineOn(currentVeh.Handle);
                }


                // If the driver door is blocked, GTA can shuffle the player to the passenger seat before the actual exit animation.
                // Arm exit enforcement early in that shuffle window so the engine doesn't get killed at the final "step out" moment.
                if (!BusForcesOff()
                    && currentVeh.Driver != player
                    && currentVeh.Handle == _recentDriverVehicleHandle
                    && (Game.GameTime - _recentDriverGameTime) <= RecentDriverGraceMs
                    && _recentDriverEngineWasOn
                    && !IsAircraft(currentVeh))
                {
                    // Shuffle+exit can exceed the normal exit window; extend enforcement for this edge case only.
                    int enforceMs = Math.Max(_exitEnforceMs, 4500);
                    ArmExit(currentVeh.Handle, source: "shuffle", enforceMsOverride: enforceMs);
                }

                int prevHandle = _lastVehicleHandle;
                _lastVehicleHandle = currentVeh.Handle;
                if (_lastVehicleHandle != prevHandle) _lastEngineOnGameTime = 0;
                _lastVehicleWasAircraft = IsAircraft(currentVeh);

                _lastVehicleEngineOn = NativeCompat.IsVehicleEngineOn(_lastVehicleHandle);
                if (_lastVehicleEngineOn) _lastEngineOnGameTime = Game.GameTime;
            }

            if (_disableAutoStart)
                TryArmEntry(player);

            if (_disableAutoStart)
                ApplyEntryEnforcement();

            if (_disableAutoShutdown)
            {

                TryArmExitIntent(player, currentVeh);

                TryArmExitEarly(player, currentVeh);


                if (_wasInVehicle && !inVehicle)
                    ArmExitFromTransition();
            }

            if (_disableAutoShutdown)
                ApplyExitEnforcement();

            _wasInVehicle = inVehicle;
        }

        private void TryArmEntry(Ped player)
        {
            if (_entryArmed)
                return;

            if (!NativeCompat.IsPedGettingIntoAnyVehicle(player.Handle))
                return;

            int targetVeh = NativeCompat.GetVehiclePedIsTryingToEnter(player.Handle);
            if (targetVeh == 0 || !NativeCompat.DoesEntityExist(targetVeh))
                return;

            if (NativeCompat.IsVehicleEngineOn(targetVeh))
                return;

            _entryArmed = true;
            _entryVehicleHandle = targetVeh;
            _entryEnforceUntilGameTime = Game.GameTime + _entryEnforceMs;

            LogInfoThrottled("mp_entry_arm", $"Entry armed. veh={targetVeh} engine was OFF -> enforcing OFF for {_entryEnforceMs}ms.", 500);
        }

        private void ApplyEntryEnforcement()
        {
            if (!_entryArmed)
                return;

            if (Game.GameTime > _entryEnforceUntilGameTime)
            {
                _entryArmed = false;
                _entryVehicleHandle = 0;
                return;
            }

            if (_entryVehicleHandle == 0 || !NativeCompat.DoesEntityExist(_entryVehicleHandle))
            {
                _entryArmed = false;
                _entryVehicleHandle = 0;
                return;
            }

            if (NativeCompat.IsVehicleEngineOn(_entryVehicleHandle))
            {
                ModLogger.Info("[EntryGuard] Vehicle engine already running � canceling DisableAutoStart enforcement.");
                _entryArmed = false;
                _entryVehicleHandle = 0;
                return;
            }

            NativeCompat.ForceVehicleEngineOff_NoAutoStart(_entryVehicleHandle);
        }


        private void TryArmExitIntent(Ped player, Vehicle currentVeh)
        {
            if (_exitArmed)
                return;

            if (currentVeh == null || !currentVeh.Exists())
                return;

            // Only for the player's driver seat exit intent.
            if (currentVeh.Driver != player)
                return;

            // Skip aircraft by design.
            if (IsAircraft(currentVeh))
                return;

            // Respect cooperative overrides (EngineStateControl / other mods).
            if (BusForcesOff())
                return;

            // Exit intent: this fires at button-press time, before GTA may kill the engine during the exit animation.
            bool exitPressed =
                Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)Control.VehicleExit) ||
                Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 2, (int)Control.VehicleExit) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)Control.VehicleExit) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 2, (int)Control.VehicleExit);

            bool exitHeld =
                Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, (int)Control.VehicleExit) ||
                Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 2, (int)Control.VehicleExit) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)Control.VehicleExit) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 2, (int)Control.VehicleExit);

            // With Interval=50ms, we can miss JUST_PRESSED; latch if the key is held and the get-out task has begun.
            if (!exitPressed && !(exitHeld && NativeCompat.IsPedGettingOutOfVehicle(player.Handle)))
                return;


            if (!exitPressed)
                return;

            int vehHandle = currentVeh.Handle;

            // Only protect exits where the engine was already running.
            bool engineOn = NativeCompat.IsVehicleEngineOn(vehHandle);
            if (!engineOn)
                return;

            // Blocked-driver-door can include a seat-shuffle + exit; extend the window for this path.
            int enforceMs = Math.Max(_exitEnforceMs, 6500);
            ArmExit(vehHandle, source: "exit_intent", enforceMsOverride: enforceMs);
        }

        private void TryArmExitEarly(Ped player, Vehicle currentVeh)
        {
            if (_exitArmed)
                return;

            if (!NativeCompat.IsPedGettingOutOfVehicle(player.Handle))
                return;

            if (currentVeh == null || !currentVeh.Exists())
                return;

            if (IsAircraft(currentVeh))
            {
                LogInfoThrottled("mp_exit_skip_aircraft", $"Exit detected for aircraft veh={currentVeh.Handle}; skipping DisableAutoShutdown enforcement.", 1500);
                return;
            }

            int vehHandle = currentVeh.Handle;

            // Snapshot from current state
            bool engineOn = NativeCompat.IsVehicleEngineOn(vehHandle);
            if (!engineOn)
                return;

            ArmExit(vehHandle, source: "native");
        }


        private void ArmExitFromTransition()
        {
            if (_exitArmed)
                return;

            if (_lastVehicleHandle == 0 || !NativeCompat.DoesEntityExist(_lastVehicleHandle))
                return;

            if (_lastVehicleWasAircraft)
            {
                LogInfoThrottled("mp_exit_skip_aircraft", $"Exit transition for aircraft veh={_lastVehicleHandle}; skipping DisableAutoShutdown enforcement.", 1500);
                return;
            }

            bool engineWasOnForExit = _lastVehicleEngineOn || (Game.GameTime - _lastEngineOnGameTime <= 1500);
            if (!engineWasOnForExit)
                return;

            ArmExit(_lastVehicleHandle, source: "transition");
        }

        private void ArmExit(int vehHandle, string source, int enforceMsOverride = -1)
        {
            _exitArmed = true;
            _exitVehicleHandle = vehHandle;
            _exitEnforceUntilGameTime = Game.GameTime + ((enforceMsOverride > 0) ? enforceMsOverride : _exitEnforceMs);


            // LRU
            TouchPersistedVehicle(vehHandle);
            // Start immediately
            // Respect cooperative overrides: if someone is forcing the engine OFF, do not fight it.
            var busIntent = EngineOverrideBus.GetCurrent(out _, out _, out _);
            if (busIntent == EngineIntent.ForceOff)
            {
                NativeCompat.SetVehicleKeepEngineOn(_exitVehicleHandle, false);
                _exitArmed = false;
                _exitVehicleHandle = 0;
                return;
            }

            NativeCompat.SetVehicleKeepEngineOn(_exitVehicleHandle, true);

            LogInfoThrottled("mp_exit_arm", $"Exit armed ({source}). veh={vehHandle} engine was ON -> enforcing ON for {_exitEnforceMs}ms.", 500);
        }

        private void ApplyExitEnforcement()
        {
            if (!_exitArmed)
                return;

            // Respect explicit ForceOff intent from any script (including EngineStateControl / other mods).
            if (BusForcesOff())
            {
                if (_exitVehicleHandle != 0 && NativeCompat.DoesEntityExist(_exitVehicleHandle))
                    NativeCompat.SetVehicleKeepEngineOn(_exitVehicleHandle, false);
                _exitArmed = false;
                _exitVehicleHandle = 0;
                return;
            }
            if (Game.GameTime > _exitEnforceUntilGameTime)
            {
                _exitArmed = false;
                _exitVehicleHandle = 0;
                return;
            }

            if (_exitVehicleHandle == 0 || !NativeCompat.DoesEntityExist(_exitVehicleHandle))
            {
                _exitArmed = false;
                _exitVehicleHandle = 0;
                return;
            }

            if (_maxDistanceToTrackFeet > 0f)
            {
                try
                {
                    Ped p = Game.Player.Character;
                    Vehicle v = VehicleFromHandle(_exitVehicleHandle);
                    if (p != null && p.Exists() && v != null && v.Exists())
                    {
                        float distFeet = p.Position.DistanceTo(v.Position) * FeetPerMeter;
                        if (distFeet > _maxDistanceToTrackFeet)
                        {
                            NativeCompat.SetVehicleKeepEngineOn(_exitVehicleHandle, false);
                            LogInfoThrottled("mp_exit_cancel_dist", $"Exit enforcement canceled (distance). veh={_exitVehicleHandle} distFeet={distFeet:0} > max={_maxDistanceToTrackFeet:0}.", 750);

                            _exitArmed = false;
                            _exitVehicleHandle = 0;
                            return;
                        }
                    }
                }
                catch { }
            }

            NativeCompat.SetVehicleKeepEngineOn(_exitVehicleHandle, true);

            if (!NativeCompat.IsVehicleEngineOn(_exitVehicleHandle))
                ForceEngineOnHard(_exitVehicleHandle);
        }


        private static bool BusForcesOff()
        {
            try
            {
                return EngineOverrideBus.GetCurrent(out _, out _, out _) == EngineIntent.ForceOff;
            }
            catch
            {
                return false;
            }
        }


        private static void ForceEngineOnHard(int vehHandle)
        {
            // Stronger than some wrappers: instantly turn engine on and allow autostart logic.
            // (veh, on, instantly, disableAutoStart)
            try { Function.Call(Hash.SET_VEHICLE_UNDRIVEABLE, vehHandle, false); } catch { }
            try { Function.Call(Hash.SET_VEHICLE_ENGINE_ON, vehHandle, true, true, false); } catch { }
        }

        private void MaintainKeepEngineOnFlag(Ped player, Vehicle currentVeh)
        {
            // Respect explicit ForceOff intent from any script (including EngineStateControl / other mods).
            if (BusForcesOff())
            {
                ClearKeepEngineOnFlagIfAny();
                return;
            }

            if (currentVeh == null || !currentVeh.Exists())
            {
                ClearKeepEngineOnFlagIfAny();
                return;
            }

            // Driver only (passengers shouldn't force this).
            // Exception: if the player's driver door is blocked, the game can shuffle them to the passenger seat
            // right before exiting. In that short window, treat them as "still the driver" so the engine doesn't auto-shut off.
            bool treatAsDriver =
                (currentVeh.Driver == player) ||
                (currentVeh.Handle == _recentDriverVehicleHandle && (Game.GameTime - _recentDriverGameTime) <= RecentDriverGraceMs);

            if (!treatAsDriver)
            {
                ClearKeepEngineOnFlagIfAny();
                return;
            }

            // Skip aircraft by design
            if (IsAircraft(currentVeh))
            {
                ClearKeepEngineOnFlagIfAny();
                return;
            }

            int h = currentVeh.Handle;

            // Respect cooperative overrides: if someone is forcing the engine OFF, do not keep engine-on flags alive.
            var busIntent = EngineOverrideBus.GetCurrent(out _, out _, out _);
            if (busIntent == EngineIntent.ForceOff)
            {
                ClearKeepEngineOnFlagIfAny();
                return;
            }

            // Only bother if the engine is currently on, OR if we're in the seat-shuffle grace window and the engine was on very recently.
            bool engineOnNow = NativeCompat.IsVehicleEngineOn(h);
            bool engineWasOnVeryRecently = (_lastVehicleHandle == h) && (Game.GameTime - _lastEngineOnGameTime) <= 1500;

            bool inSeatShuffleGrace =
                (currentVeh.Driver != player) &&
                (h == _recentDriverVehicleHandle) &&
                (Game.GameTime - _recentDriverGameTime) <= RecentDriverGraceMs;

            bool shouldKeep =
                engineOnNow ||
                (inSeatShuffleGrace && engineWasOnVeryRecently);

            if (!shouldKeep)
            {
                ClearKeepEngineOnFlagIfAny();
                return;
            }

            if (_keepEngineOnVehicleHandle != 0 && _keepEngineOnVehicleHandle != h && NativeCompat.DoesEntityExist(_keepEngineOnVehicleHandle))
            {
                NativeCompat.SetVehicleKeepEngineOn(_keepEngineOnVehicleHandle, false);
            }

            _keepEngineOnVehicleHandle = h;

            // Keep engine running on exit (when it was already running). Safe to apply while driving too.
            NativeCompat.SetVehicleKeepEngineOn(h, true);

            // Blocked-driver-door shuffle edge case:
            // If GTA kills the engine during the driver->passenger shuffle, restore it ONLY in the grace window,
            // and only if it was running very recently, and no one is forcing it off via the override bus.
            if (!engineOnNow && inSeatShuffleGrace && engineWasOnVeryRecently)
            {
                ForceEngineOnHard(h);
            }
        }

        private void ClearKeepEngineOnFlagIfAny()
        {
            if (_keepEngineOnVehicleHandle == 0)
                return;

            if (NativeCompat.DoesEntityExist(_keepEngineOnVehicleHandle))
                NativeCompat.SetVehicleKeepEngineOn(_keepEngineOnVehicleHandle, false);

            _keepEngineOnVehicleHandle = 0;
        }

        private void LoadIniOnce()
        {
            try
            {
                // Centralized INI load (Main.cs)
                LogInfo($"INI: LoadIniOnce path={MainConfig.IniPathUsed} exists={MainConfig.IniExists}");

                string sec = "(Main.cs)";
                string raw = "(shared)";

                _disableAutoStart = MainConfig.DisableAutoStart;
                LogInfo($"INI: DisableAutoStart section={sec} raw='{raw}' -> {_disableAutoStart}");

                _disableAutoShutdown = MainConfig.DisableAutoShutdown;
                LogInfo($"INI: DisableAutoShutdown section={sec} raw='{raw}' -> {_disableAutoShutdown}");

                _entryEnforceMs = MainConfig.EntryEnforceMs;
                LogInfo($"INI: EntryEnforceMs section={sec} raw='{raw}' -> {_entryEnforceMs}");

                _exitEnforceMs = MainConfig.ExitEnforceMs;
                LogInfo($"INI: ExitEnforceMs section={sec} raw='{raw}' -> {_exitEnforceMs}");

                _maxDistanceToTrackFeet = MainConfig.MaxDistanceToTrack;
                LogInfo($"INI: MaxDistanceToTrack section={sec} raw='{raw}' -> {_maxDistanceToTrackFeet}");

                _maxTrackedVehicle = MainConfig.MaxTrackedVehicle;
                LogInfo($"INI: MaxTrackedVehicle section={sec} raw='{raw}' -> {_maxTrackedVehicle}");
            }
            catch (Exception ex)
            {
                LogError("Failed to load EngineStateManager.ini (using defaults).", ex);
            }
        }

        private static string GetScriptsDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            baseDir = Path.GetFullPath(baseDir);

            string trimmed = baseDir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(trimmed);
            if (string.Equals(leaf, "scripts", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            return Path.Combine(baseDir, "scripts");
        }


        private static readonly string[] IniSections = new[]
        {
            "Settings",
            "EngineStateManager",
            "MainPersist",
            "General",
        };

        private static bool ReadBoolFromAnySection(ScriptSettings ini, string key, bool defaultValue, out string foundSection, out string rawValue)
        {
            foundSection = "";
            rawValue = null;

            foreach (var section in IniSections)
            {
                // tolerate inline comments / extra text.
                string raw = ini.GetValue(section, key, (string)null);
                if (raw == null)
                    continue;

                rawValue = raw;
                foundSection = section;

                if (TryParseBoolLoose(raw, out bool b))
                    return b;

                // If key exists but parse failed, fall back to default.
                return defaultValue;
            }

            return defaultValue;
        }

        private static int ReadIntFromAnySection(ScriptSettings ini, string key, int defaultValue, int min, int max, out string foundSection, out string rawValue)
        {
            foundSection = "";
            rawValue = null;

            foreach (var section in IniSections)
            {
                string raw = ini.GetValue(section, key, (string)null);
                if (raw == null)
                    continue;

                rawValue = raw;
                foundSection = section;

                if (TryParseIntLoose(raw, out int v))
                    return Clamp(v, min, max);

                return Clamp(defaultValue, min, max);
            }

            return Clamp(defaultValue, min, max);
        }

        private static bool TryParseBoolLoose(string raw, out bool value)
        {
            value = false;
            if (raw == null) return false;

            string token = StripInlineJunk(raw);
            if (token.Length == 0) return false;

            token = token.ToLowerInvariant();

            if (token == "true" || token == "1" || token == "yes" || token == "y" || token == "on")
            {
                value = true;
                return true;
            }

            if (token == "false" || token == "0" || token == "no" || token == "n" || token == "off")
            {
                value = false;
                return true;
            }

            return false;
        }

        private static bool TryParseIntLoose(string raw, out int value)
        {
            value = 0;
            if (raw == null) return false;

            string token = StripInlineJunk(raw);
            if (token.Length == 0) return false;

            return int.TryParse(token, out value);
        }

        private static string StripInlineJunk(string raw)
        {
            string s = raw.Trim();

            int cut = s.Length;

            int idx;
            idx = s.IndexOf(';'); if (idx >= 0 && idx < cut) cut = idx;
            idx = s.IndexOf('#'); if (idx >= 0 && idx < cut) cut = idx;
            idx = s.IndexOf('('); if (idx >= 0 && idx < cut) cut = idx;

            idx = s.IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0 && idx < cut) cut = idx;

            s = s.Substring(0, cut).Trim();

            int sp = s.IndexOfAny(new[] { ' ', '\t' });
            if (sp > 0)
                s = s.Substring(0, sp).Trim();

            return s;
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static bool IsAircraft(Vehicle v)
        {
            try
            {
                if (v == null || !v.Exists())
                    return false;

                var m = v.Model;
                return m.IsPlane || m.IsHelicopter;
            }
            catch
            {
                return false;
            }
        }

        private bool IsCarPersistenceEnabled()
        {
            if (!_disableAutoShutdown)
                return false;

            // Either setting can disable the whole tracking system.
            if (_maxDistanceToTrackFeet == 0f)
                return false;

            if (_maxTrackedVehicle <= 0)
                return false;

            return true;
        }

        private void TouchPersistedVehicle(int vehHandle)
        {
            if (vehHandle == 0)
                return;

            // If the entity doesn't exist, don't track it.
            if (!NativeCompat.DoesEntityExist(vehHandle))
                return;

            Vehicle v = VehicleFromHandle(vehHandle);
            if (v == null || !v.Exists())
                return;

            // Cars only; do not track aircraft here.
            if (IsAircraft(v))
                return;

            if (_persistedSet.Contains(vehHandle))
            {
                // Move to most-recent end
                var node = _persistedLru.Find(vehHandle);
                if (node != null)
                {
                    _persistedLru.Remove(node);
                    _persistedLru.AddLast(node);
                }
            }
            else
            {
                _persistedSet.Add(vehHandle);
                _persistedLru.AddLast(vehHandle);
            }

            // Enforce cap
            EvictIfOverLimit();
        }

        private void MaintainPersistedVehicles(Ped player)
        {
            if (player == null || !player.Exists())
                return;

            // If disabled at runtime, clear everything.
            if (!IsCarPersistenceEnabled())
            {
                ClearAllPersistedVehicles();
                return;
            }

            // Iterate a copy of handles in LRU order (oldest -> newest)
            var node = _persistedLru.First;
            while (node != null)
            {
                var next = node.Next;
                int handle = node.Value;

                bool remove = false;

                if (handle == 0 || !NativeCompat.DoesEntityExist(handle))
                {
                    remove = true;
                }
                else
                {
                    Vehicle v = VehicleFromHandle(handle);
                    if (v == null || !v.Exists() || IsAircraft(v))
                    {
                        remove = true;
                    }
                    else
                    {
                        if (_maxDistanceToTrackFeet > 0f)
                        {
                            try
                            {
                                float distFeet = player.Position.DistanceTo(v.Position) * FeetPerMeter;
                                if (distFeet > _maxDistanceToTrackFeet)
                                {
                                    // Too far: stop persisting this car.
                                    NativeCompat.SetVehicleKeepEngineOn(handle, false);
                                    LogInfoThrottled("mp_car_cancel_dist",
                                        $"Car untracked (distance). veh={handle} distFeet={distFeet:0} > max={_maxDistanceToTrackFeet:0}.", 1000);
                                    remove = true;
                                }
                            }
                            catch
                            {
                                // If distance calc fails, keep tracking.
                            }
                        }
                    }
                }

                if (remove)
                {
                    _persistedSet.Remove(handle);
                    _persistedLru.Remove(node);
                }

                node = next;
            }

            // Enforce cap after cleanup
            EvictIfOverLimit();
        }

        private void ClearAllPersistedVehicles()
        {
            try
            {
                foreach (int handle in _persistedSet)
                {
                    try
                    {
                        if (handle != 0 && NativeCompat.DoesEntityExist(handle))
                            NativeCompat.SetVehicleKeepEngineOn(handle, false);
                    }
                    catch { }
                }
            }
            catch { }

            _persistedSet.Clear();
            _persistedLru.Clear();
        }

        private void EvictIfOverLimit()
        {
            int cap = _maxTrackedVehicle;
            if (cap < 1)
                return;

            while (_persistedSet.Count > cap && _persistedLru.First != null)
            {
                int oldest = _persistedLru.First.Value;
                _persistedLru.RemoveFirst();
                _persistedSet.Remove(oldest);

                try
                {
                    if (oldest != 0 && NativeCompat.DoesEntityExist(oldest))
                        NativeCompat.SetVehicleKeepEngineOn(oldest, false);
                }
                catch { }

                LogInfoThrottled("mp_car_evict",
                    $"Car evicted (LRU cap={cap}). veh={oldest}.", 1000);
            }
        }

        // Logging wrappers 
        private static void LogInfo(string msg)
        {
            try
            {
                if (EngineStateManager.ModLogger.Enabled)
                    EngineStateManager.ModLogger.Info(msg);
            }
            catch { }
        }

        private static void LogInfoThrottled(string key, string msg, int ms)
        {
            try
            {
                if (EngineStateManager.ModLogger.Enabled)
                    EngineStateManager.ModLogger.InfoThrottled(key, msg, ms);
            }
            catch { }
        }


        private static void LogError(string msg, Exception ex = null)
        {
            try
            {
                if (!EngineStateManager.ModLogger.Enabled)
                    return;

                if (ex != null) EngineStateManager.ModLogger.Error(msg, ex);
                else EngineStateManager.ModLogger.Error(msg);
            }
            catch { }
        }

        private static Vehicle VehicleFromHandle(int handle)
        {
            if (handle == 0) return null;
            try { return Entity.FromHandle(handle) as Vehicle; }
            catch { return null; }
        }
    }
}
