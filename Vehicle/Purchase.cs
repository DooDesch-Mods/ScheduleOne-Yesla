using System;
using HarmonyLib;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.Money;
using Yesla.Net;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Tells the host who just bought a Yesla, so the new car gets the right owner.
    ///
    /// The purchase runs on the buyer's machine (<c>DialogueHandler_VehicleSalesman.ChoiceCallback</c>) and ends in
    /// <c>VehicleManager.SpawnVehicle</c>, a ServerRpc that carries no connection - the host sees a new car and
    /// nothing else. So the buyer announces itself right before the purchase; <see cref="Fleet"/> on the host
    /// matches the announcement to the next new Yesla.
    /// </summary>
    [HarmonyPatch(typeof(DialogueHandler_VehicleSalesman), nameof(DialogueHandler_VehicleSalesman.ChoiceCallback))]
    internal static class Purchase
    {
        private static void Prefix(DialogueHandler_VehicleSalesman __instance, string choiceLabel)
        {
            try
            {
                if (choiceLabel != "BUY_CASH" && choiceLabel != "BUY_ONLINE") return;
                var listing = __instance.selectedVehicle;
                if (listing == null || !string.Equals(listing.vehicleCode, YeslaVehicleType.Code, StringComparison.OrdinalIgnoreCase)) return;

                // The same balance check vanilla makes next; a purchase that will not happen is not announced.
                var money = NetworkSingleton<MoneyManager>.Instance;
                float balance = choiceLabel == "BUY_CASH" ? money.cashBalance : money.onlineBalance;
                if (balance < listing.price)
                {
                    Core.Log.Msg("[Purchase] Yesla purchase with " + choiceLabel + " will fail (balance " + balance + " < " + listing.price + ") - not announced.");
                    return;
                }

                string me = Fleet.MyCode();
                if (me.Length == 0) { Core.Log.Warning("[Purchase] no local player code - the new Yesla will belong to the host."); return; }
                YeslaNet.ToHost("req.bought", me);
                Core.Log.Msg("[Purchase] announced a Yesla purchase by " + me + " (" + choiceLabel + ").");
            }
            catch (Exception e) { Core.Log.Error("[Purchase] could not announce the Yesla purchase - it will belong to the host: " + e); }
        }
    }
}
