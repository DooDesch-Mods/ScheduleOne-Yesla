using System;
using MelonLoader;

namespace Yesla.Config
{
    /// <summary>MelonPreferences for Yesla: the player's in-car keys.</summary>
    internal static class Preferences
    {
        private static MelonPreferences_Category _cat;
        private static MelonPreferences_Entry<string> _engageKey;
        private static MelonPreferences_Entry<string> _viewKey;

        /// <summary>Starts and stops Yesla Assisted Driving while sitting in the driver seat.</summary>
        internal static KeyCode EngageKey => Parse(_engageKey?.Value, KeyCode.Y);

        /// <summary>Switches between the chase camera and the first-person view inside a Yesla.</summary>
        internal static KeyCode ViewKey => Parse(_viewKey?.Value, KeyCode.C);

        internal static void Initialize()
        {
            _cat = MelonPreferences.CreateCategory("Yesla", "Yesla");
            _engageKey = _cat.CreateEntry("EngageKey", "Y", "Assisted Driving key",
                "Starts and stops Yesla Assisted Driving. Unity KeyCode name, e.g. Y, T, F2.");
            _viewKey = _cat.CreateEntry("ViewKey", "C", "First-person view key",
                "Switches between the chase camera and the first-person view inside a Yesla. Unity KeyCode name, e.g. C, F3.");
        }

        private static KeyCode Parse(string value, KeyCode fallback)
        {
            if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value.Trim(), true, out KeyCode key)) return key;
            Core.Log.Warning("[Preferences] '" + value + "' is not a key name, using " + fallback);
            return fallback;
        }
    }
}
