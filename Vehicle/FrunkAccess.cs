using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Vehicles;
using Yesla.Net;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Tells everyone at which end a Yesla's storage was opened, so the lid there opens: "Access frunk" at the nose,
    /// "Access trunk" at the back. Both access points open the same storage; the storage menu is titled "Frunk" at the
    /// nose and keeps the storage's own name at the back.
    /// </summary>
    [HarmonyPatch(typeof(StorageEntityInteractable), nameof(StorageEntityInteractable.StartInteract))]
    internal static class FrunkAccess
    {
        private const string FrunkTitle = "Frunk";

        /// <summary>The storage's own name, by storage instance, so the rear title comes back after a frunk visit.</summary>
        private static readonly Dictionary<int, string> RearTitle = new Dictionary<int, string>();

        private static void Prefix(StorageEntityInteractable __instance)
        {
            try
            {
                LandVehicle car = __instance.GetComponentInParent<LandVehicle>();
                if (car == null || !Phone.YeslaApp.IsYesla(car)) return;
                bool front = __instance.gameObject.name == MovingParts.FrunkName;
                Core.Log.Msg("[Parts] " + car.VehicleName + " #" + Fleet.NetId(car) + ": storage opened at the " + (front ? "frunk" : "trunk") + " by this player.");
                Title(__instance.GetComponentInParent<StorageEntity>(), front);
                Remote.StorageSide(car, front);
            }
            catch (Exception e)
            {
                Core.Log.Error("[Parts] reading which end of the car was opened failed, the lid stays as it is: " + e.Message);
            }
        }

        /// <summary>The storage menu reads its title from the storage's name when it opens, right after this prefix.</summary>
        private static void Title(StorageEntity storage, bool front)
        {
            if (storage == null) { Core.Log.Warning("[Parts] the car's storage access point has no storage above it - the menu keeps its title."); return; }
            int id = storage.GetInstanceID();
            if (!RearTitle.TryGetValue(id, out string rear))
            {
                rear = storage.StorageEntityName == FrunkTitle ? "Trunk" : storage.StorageEntityName;
                RearTitle[id] = rear;
            }
            storage.StorageEntityName = front ? FrunkTitle : rear;
            Core.Log.Msg("[Parts] storage menu titled '" + storage.StorageEntityName + "'.");
        }
    }
}
