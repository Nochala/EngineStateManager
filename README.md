# EngineStateManager
### [Watch Showcase Video Here](https://www.youtube.com/watch?v=lPuOtOzJlYk)
GTA V ScriptHookVDotNet mod that gives players full control over vehicle engine behavior across all vehicle types.

By default, GTA V  auto-starts and auto-shuts engines during enter/exit animations, breaking immersion and realism. EngineStateManager overrides these behaviors using native-level logic, allowing engines to persist while giving the player manual authority over engine state.

Aircraft, helicopters, and ground vehicles are all supported.

Every feature is modular and fully configurable via INI.

## **[ FEATURES ]**
#### **-** Prevents engines from shutting off when exiting vehicles
#### **-** Blocks GTA V’s automatic engine startup that works across all supported vehicle classes
#### **-** Eliminates RPM/audio dips during enter/exit animations
#### **-** Toggle vehicle engines on/off via hotkey that optionally includes animations
#### **-** Does not interfere with GTA’s default controls unless enabled
#### **-** Includes native fail-safe handling 
#### **-** Optional in-game notification when the mod loads
#### **-** Tracks vehicle engine state per entity
#### **-** Troubleshooting debug logging (if enabled)
#### **-** Designed for future GTA updates
#### **-** Disable aircraft stalling when the aircraft is flying too slow (if enabled)
#### **-** Disable aircraft stalling if the aircraft is damaged. (if enabled)
#### **-** Controller Support

### **[ Configuration Options ]**
All features are configurable via: **EngineStateManager.ini**

You can fully customize behavior without touching code.

Changes only apply on next script reload.

### [ Requirements ]
#### **-** [Latest ScriptHookV](https://www.dev-c.com/gtav/scripthookv/)
#### **-** Latest **ScriptHookVDotNet v3** for [**Enhanced**](https://www.gta5-mods.com/tools/script-hook-v-net-enhanced) or [**Legacy**](https://www.gta5-mods.com/tools/scripthookv-net)

### [ Known Issues ]
- **Jet planes don't idle at they're idle speed when exiting them.**
- **Natively prop planes are impossible to keep running when exiting them (unless a prop plane rotor hash exists but has been hidden by rockstar), so I've decided to do a temporary work around by spawning an
invisible ped in the prop plane the player was last in to keep the engine idling.**
#### **If you encounter any other issues, please include your EngineStateManager.log when reporting.**

### **[ Planned Upcoming Features ]**
#### **-** _Separate aircraft logic modes_
#### **-** _Advanced idle RPM control_
#### **-** _Additional realism options_
#### **-** _Configurable engine options that are only present in online_

### [ Development Notes ]
This mod was built with a strong focus on:

Native-level compatibility

Realism-oriented behavior

Modular, configurable design

Stability across GTA updates

All logic, persistence systems, and engine handling were custom-developed specifically for EngineStateManager.


## **[ Credits & Acknowledgements ]**
#### **-** Alexander Blade — for ScriptHookV
#### **-** crosire — for ScriptHookVDotNet
#### **-** Chiheb-Bacha — for ScriptHookVDotNet Enhanced
#### Without their foundational tools, this mod would not be possible.

#### **Additional thanks to:**
#### **-** The GTA V modding community for documentation, shared research, and reverse-engineering efforts
#### **-** Everyone who reports bugs or provides feedback — your input directly improves future updates


### [ Mod Mirrors ]
  [**GTA5-Mods**](https://www.gta5-mods.com/tools/script-hook-v-net-enhanced)
  
  [NexusMods Enhanced Page](https://www.nexusmods.com/gta5enhanced/mods/443)
  
  [NexusMods Legacy Page](https://www.nexusmods.com/gta5/mods/1656)
