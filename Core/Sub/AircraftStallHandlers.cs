
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
using GTA.Math;
using GTA.Native;
using System;

namespace EngineStateManager
{
    internal sealed class AircraftStallHandlers
    {
        private int _nextAt;
        private const int UpdateEveryMs = 50;

        private const Control BrakeControl = Control.VehicleBrake;

        private const float EngineHealthFloor = 1000.0f;
        private const float TankHealthFloor = 1000.0f;     // keep

        public void Tick()
        {
            if (!MainConfig.DisableAircraftStallingWhenSlow && !MainConfig.DisableDamagedAircraftStalling)
                return;

            int now = Game.GameTime;
            if (now < _nextAt)
                return;
            _nextAt = now + UpdateEveryMs;

            Ped p = Game.Player.Character;
            if (p == null || !p.Exists() || !p.IsInVehicle())
                return;

            Vehicle v = p.CurrentVehicle;
            if (v == null || !v.Exists())
                return;

            bool isPlane = v.Model.IsPlane || v.ClassType == VehicleClass.Planes;
            bool isHeli = v.Model.IsHelicopter || v.ClassType == VehicleClass.Helicopters;
            if (!isPlane && !isHeli)
                return;

            bool onAllWheels = IsOnAllWheelsSafe(v);
            float hag = GetHeightAboveGroundSafe(v);

            bool airborne = !onAllWheels || hag > 2.5f;

            if (MainConfig.DisableAircraftStallingWhenSlow && airborne)
            {
                if (IsEngineForceOffByBus())
                    return;

                if (Game.IsControlPressed(BrakeControl) && !v.IsEngineRunning)
                {
                    // Stall prevention requests engine ON briefly (lower priority than user toggle).
                    EngineOverrideBus.Set(EngineIntent.ForceOn, EngineIntentPriority.Normal, 800, "AircraftStallHandlers");
                    ForceEngineOn(v);

                    if (ModLogger.Enabled)
                    {
                        ModLogger.InfoThrottled(
                            "stall.fix.engineon",
                            "[AircraftStallHandlers] Forced engine on while braking airborne.",
                            800
                        );
                    }
                }

                // NOT immersive
                if (!v.IsEngineRunning)
                {
                    EngineOverrideBus.Set(EngineIntent.ForceOn, EngineIntentPriority.Normal, 1200, "AircraftStallHandlers");
                    ForceEngineOn(v);

                    if (ModLogger.Enabled)
                    {
                        ModLogger.InfoThrottled(
                            "stall.fix.engineon2",
                            "[AircraftStallHandlers] Forced engine on while airborne.",
                            1200
                        );
                    }
                }
            }

            if (MainConfig.DisableDamagedAircraftStalling && airborne)
            {
                bool changed = false;

                if (v.EngineHealth < EngineHealthFloor)
                {
                    v.EngineHealth = EngineHealthFloor;
                    changed = true;
                }

                if (v.PetrolTankHealth < TankHealthFloor)
                {
                    v.PetrolTankHealth = TankHealthFloor;
                    changed = true;
                }

                TrySetEngineDegrade(v, false);

                if (changed && ModLogger.Enabled)
                {
                    ModLogger.InfoThrottled(
                        "stall.fix.health",
                        $"[AircraftStallHandlers] Floored health (eng={v.EngineHealth:0}, tank={v.PetrolTankHealth:0}).",
                        1500
                    );
                }
            }
        }

        private static void ForceEngineOn(Vehicle v)
        {
            // Final guard
            if (IsEngineForceOffByBus())
                return;

            try
            {
                v.IsEngineRunning = true;
            }
            catch { }

            try
            {
                NativeCompat.SetVehicleEngineOn(v.Handle, true, true, false);
            }
            catch
            {
                try { Function.Call(Hash.SET_VEHICLE_ENGINE_ON, v, true, true, false); } catch { }
            }
        }

        private static void TrySetEngineDegrade(Vehicle v, bool canDegrade)
        {
            try { Function.Call(Hash.SET_VEHICLE_ENGINE_CAN_DEGRADE, v, canDegrade); }
            catch { }
        }

        private static bool IsOnAllWheelsSafe(Vehicle v)
        {
            try { return v.IsOnAllWheels; }
            catch
            {
                try { return Function.Call<bool>(Hash.IS_VEHICLE_ON_ALL_WHEELS, v); }
                catch { return true; }
            }
        }

        private static float GetHeightAboveGroundSafe(Vehicle v)
        {
            try { return Function.Call<float>(Hash.GET_ENTITY_HEIGHT_ABOVE_GROUND, v); }
            catch { return 0f; }
        }

        private static bool IsEngineForceOffByBus()
        {
            try
            {
                EngineIntent intent = EngineOverrideBus.GetCurrent(out _, out _, out _);
                return intent == EngineIntent.ForceOff;
            }
            catch
            {
                return false;
            }
        }
    }
}