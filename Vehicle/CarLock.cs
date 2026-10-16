using System;
using HarmonyLib;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Vehicle
{
    /// <summary>
    /// A locked Yesla cannot be entered. Vanilla decides entry in <c>LandVehicle.Hovered</c> (the prompt) and
    /// <c>LandVehicle.Interacted</c> (the action) with no owner check at all; both are patched for Yeslas whose
    /// record says locked. No NPC ever gets into a player's car in vanilla, so players are all there is to stop.
    /// Leaving a locked car is always possible.
    /// </summary>
    [HarmonyPatch]
    internal static class CarLock
    {
        private static float _lastRefusalLog = -100f;

        internal static bool IsLocked(LandVehicle car)
        {
            if (car == null || !Phone.YeslaApp.IsYesla(car)) return false;
            Fleet.Rec r = Fleet.For(car);
            return r != null && r.Locked;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LandVehicle), nameof(LandVehicle.Hovered))]
        private static void HoveredPostfix(LandVehicle __instance)
        {
            try
            {
                if (!IsLocked(__instance) || !__instance.IsPlayerOwned) return;
                __instance.intObj.SetMessage("Locked");
                __instance.intObj.SetInteractableState(InteractableObject.EInteractableState.Invalid);
            }
            catch (Exception e) { Core.Log.Error("[Lock] could not show the locked prompt: " + e.Message); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(LandVehicle), nameof(LandVehicle.Interacted))]
        private static bool InteractedPrefix(LandVehicle __instance)
        {
            try
            {
                if (!IsLocked(__instance)) return true;
                if (Time.time - _lastRefusalLog > 2f)
                {
                    _lastRefusalLog = Time.time;
                    Core.Log.Msg("[Lock] " + __instance.VehicleName + " #" + Fleet.NetId(__instance) + " is locked - not entering.");
                }
                return false;
            }
            catch (Exception e)
            {
                Core.Log.Error("[Lock] lock check failed, letting the player in: " + e.Message);
                return true;
            }
        }
    }
}
