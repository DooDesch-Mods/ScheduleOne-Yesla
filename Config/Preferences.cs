using System;
using MelonLoader;

namespace Yesla.Config
{
    /// <summary>MelonPreferences for Yesla: the player's in-car keys and drive profile.</summary>
    internal static class Preferences
    {
        private static MelonPreferences_Category _cat;
        private static MelonPreferences_Entry<string> _engageKey;
        private static MelonPreferences_Entry<string> _vizKey;
        private static MelonPreferences_Entry<string> _driveProfile;
        private static MelonPreferences_Entry<string> _parkDirection;

        /// <summary>Starts and stops Yesla Assisted Driving while sitting in the driver seat.</summary>
        internal static KeyCode EngageKey => Parse(_engageKey?.Value, KeyCode.Y);

        /// <summary>Cycles the driving visualisation inside a Yesla: panel, full screen, off.</summary>
        internal static KeyCode VizKey => Parse(_vizKey?.Value, KeyCode.N);

        /// <summary>The drive profile Assisted Driving and Summon use: chill, standard or madmax. Set from the phone.
        /// Only the value is set: MelonPreferences writes it on shutdown, and an explicit save fires every mod's
        /// preference listeners mid-session.</summary>
        internal static string DriveProfileId
        {
            get => _driveProfile?.Value ?? "standard";
            set { if (_driveProfile != null) _driveProfile.Value = value; else Core.Log.Warning("[Preferences] drive profile set before the preferences exist - not stored."); }
        }

        /// <summary>
        /// How Autopark enters a lot spot: "auto" picks per spot and backs in unless driving in nose first is
        /// clearly simpler, "reverse" always backs in, "forward" always parks nose first. Set from the phone,
        /// stored like the drive profile.
        /// </summary>
        internal static string ParkDirection
        {
            get
            {
                string v = _parkDirection?.Value;
                return v == "reverse" || v == "forward" ? v : "auto";
            }
            set { if (_parkDirection != null) _parkDirection.Value = value; else Core.Log.Warning("[Preferences] parking direction set before the preferences exist - not stored."); }
        }

        internal static void Initialize()
        {
            _cat = MelonPreferences.CreateCategory("Yesla", "Yesla");
            _engageKey = _cat.CreateEntry("EngageKey", "Y", "Assisted Driving key",
                "Starts and stops Yesla Assisted Driving. Unity KeyCode name, e.g. Y, T, F2.");
            _vizKey = _cat.CreateEntry("VisualisationKey", "N", "Visualisation key",
                "Cycles the driving visualisation inside a Yesla: panel, full screen, off. Unity KeyCode name, e.g. N, F4.");
            _driveProfile = _cat.CreateEntry("DriveProfile", "standard", "Drive profile",
                "How Assisted Driving and Summon drive: chill, standard or madmax. Also set in the Yesla phone app.");
            _parkDirection = _cat.CreateEntry("ParkDirection", "auto", "Parking direction",
                "How Autopark enters a parking spot: auto, reverse or forward. Auto backs in unless the car already faces the spot. Also set in the Yesla phone app.");
        }

        private static KeyCode Parse(string value, KeyCode fallback)
        {
            if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value.Trim(), true, out KeyCode key)) return key;
            Core.Log.Warning("[Preferences] '" + value + "' is not a key name, using " + fallback);
            return fallback;
        }
    }
}
