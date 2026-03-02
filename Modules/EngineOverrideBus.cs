
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
using GTA;

namespace EngineStateManager
{
    internal enum EngineIntent
    {
        None = 0,
        ForceOff = 1,
        ForceOn = 2
    }

    internal enum EngineIntentPriority
    {
        // Higher wins
        Low = 10,
        Normal = 50,
        High = 90,
        Critical = 100,
    }

    internal static class EngineOverrideBus
    {
        private struct IntentState
        {
            public EngineIntent Intent;
            public EngineIntentPriority Priority;
            public int ExpiresAt;
            public int SetAt;
        }

        // Cooperative bus: multiple owners can register intents.
        // Resolution: highest priority wins; on tie, most recently set wins.
        private static readonly System.Collections.Generic.Dictionary<string, IntentState> _states
            = new System.Collections.Generic.Dictionary<string, IntentState>(StringComparer.Ordinal);

        public static void Set(EngineIntent intent, EngineIntentPriority priority, int durationMs, string owner)
        {
            if (string.IsNullOrEmpty(owner))
                owner = "";

            int now = Game.GameTime;
            int expires = durationMs <= 0 ? int.MaxValue : checked(now + durationMs);

            // If intent is None, treat as Clear (but keep it silent).
            if (intent == EngineIntent.None)
            {
                Clear(owner);
                return;
            }

            _states[owner] = new IntentState
            {
                Intent = intent,
                Priority = priority,
                ExpiresAt = expires,
                SetAt = now
            };

            if (ModLogger.Enabled)
                ModLogger.Info($"EngineOverrideBus.Set: Intent={intent}, Pri={priority}, DurMs={durationMs}, Owner={owner}");
        }

        public static void Clear(string owner)
        {
            if (string.IsNullOrEmpty(owner))
                owner = "";

            if (_states.Remove(owner) && ModLogger.Enabled)
                ModLogger.Info($"EngineOverrideBus.Clear: Owner={owner}");
        }

        public static EngineIntent GetCurrent(out string owner, out EngineIntentPriority pri, out int expiresAt)
        {
            CleanupExpired();

            owner = "";
            pri = EngineIntentPriority.Low;
            expiresAt = 0;

            if (_states.Count == 0)
                return EngineIntent.None;

            // Resolve winner.
            string bestOwner = null;
            IntentState best = default;
            bool hasBest = false;

            foreach (var kv in _states)
            {
                var st = kv.Value;
                if (!hasBest)
                {
                    bestOwner = kv.Key;
                    best = st;
                    hasBest = true;
                    continue;
                }

                if (st.Priority > best.Priority || (st.Priority == best.Priority && st.SetAt > best.SetAt))
                {
                    bestOwner = kv.Key;
                    best = st;
                }
            }

            owner = bestOwner ?? "";
            pri = best.Priority;
            expiresAt = best.ExpiresAt;
            return best.Intent;
        }

        private static void CleanupExpired()
        {
            if (_states == null || _states.Count == 0)
                return;

            int now = Game.GameTime;

            var deadKeys = new System.Collections.Generic.List<string>();

            foreach (var kv in _states)
            {
                if (now >= kv.Value.ExpiresAt)
                    deadKeys.Add(kv.Key);
            }

            for (int i = 0; i < deadKeys.Count; i++)
            {
                _states.Remove(deadKeys[i]);
            }
        }
    }
}