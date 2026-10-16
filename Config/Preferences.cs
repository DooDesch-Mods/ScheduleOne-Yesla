using System;
using MelonLoader;

namespace Yesla.Config
{
    /// <summary>MelonPreferences for Yesla: the player's in-car keys.</summary>
    internal static class Preferences
    {
        private static MelonPreferences_Category _cat;
        private static MelonPreferences_Entry<string> _engageKey;

        /// <summary>Starts and stops Yesla Assisted Driving while sitting in the driver seat.</summary>
        internal static KeyCode EngageKey => Parse(_engageKey?.Value, KeyCode.Y);

        internal static void Initialize()
        {
            _cat = MelonPreferences.CreateCategory("Yesla", "Yesla");
            _engageKey = _cat.CreateEntry("EngageKey", "Y", "Assisted Driving key",
                "Starts and stops Yesla Assisted Driving. Unity KeyCode name, e.g. Y, T, F2.");
        }

        private static KeyCode Parse(string value, KeyCode fallback)
        {
            if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value.Trim(), true, out KeyCode key)) return key;
            Core.Log.Warning("[Preferences] '" + value + "' is not a key name, using " + fallback);
            return fallback;
        }
    }
}
