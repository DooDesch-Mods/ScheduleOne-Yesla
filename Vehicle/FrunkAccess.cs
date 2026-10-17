using System;
using HarmonyLib;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Vehicles;
using Yesla.Net;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Tells everyone at which end a Yesla's storage was opened, so the lid there opens: "Access frunk" at the nose,
    /// "Access trunk" at the back. Both access points open the same storage.
    /// </summary>
    [HarmonyPatch(typeof(StorageEntityInteractable), nameof(StorageEntityInteractable.StartInteract))]
    internal static class FrunkAccess
    {
        private static void Prefix(StorageEntityInteractable __instance)
        {
            try
            {
                LandVehicle car = __instance.GetComponentInParent<LandVehicle>();
                if (car == null || !Phone.YeslaApp.IsYesla(car)) return;
                bool front = __instance.gameObject.name == MovingParts.FrunkName;
                Core.Log.Msg("[Parts] " + car.VehicleName + " #" + Fleet.NetId(car) + ": storage opened at the " + (front ? "frunk" : "trunk") + " by this player.");
                Remote.StorageSide(car, front);
            }
            catch (Exception e)
            {
                Core.Log.Error("[Parts] reading which end of the car was opened failed, the lid stays as it is: " + e.Message);
            }
        }
    }
}
