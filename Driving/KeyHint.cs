using Chassis.Api;
using Yesla.Config;

namespace Yesla.Driving
{
    /// <summary>
    /// Adds the Yesla keys to the Chassis key hints at the bottom left: "Assisted Driving" for the owner at the wheel,
    /// "Visualisation" in every seat. Chassis lists its own keys (First Person, Horn, Indicators) above them. The keys
    /// come from the preferences and are read every frame, so a changed key shows at once.
    /// </summary>
    internal static class KeyHint
    {
        private const string DriveId = "yesla.assisted_driving";
        private const string VizId = "yesla.visualisation";

        /// <summary>Adds the entries; the same ids again replace them, so every scene load may call it.</summary>
        internal static void Register()
        {
            // Assisted Driving answers only its owner, so only the owner at the wheel is shown the key.
            ChassisApi.AddKeyHint(DriveId, "Assisted Driving",
                c => !c.Pushing && c.Driver && Phone.YeslaApp.IsYesla(c.Vehicle) && Vehicle.Fleet.IsMine(c.Vehicle),
                () => Preferences.EngageKey);
            ChassisApi.AddKeyHint(VizId, "Visualisation", c => !c.Pushing && Phone.YeslaApp.IsYesla(c.Vehicle), () => Preferences.VizKey);
            Core.Log.Msg("[KeyHint] Assisted Driving and Visualisation added to the Chassis key hints.");
        }
    }
}
