using System;
using HarmonyLib;
using Il2CppScheduleOne.Dialogue;
using Il2CppScheduleOne.NPCs.CharacterClasses;
using ChoiceList = Il2CppSystem.Collections.Generic.List<Il2CppScheduleOne.Dialogue.DialogueChoiceData>;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Puts the Yesla Model 3 on Jeremy's list at the dealership.
    ///
    /// The vehicle choices are data in the salesman's conversation: one choice per car, its label the vehicle code,
    /// all linked to the same confirm node. Vanilla resolves name and price from the prefab by that code
    /// (<c>Jeremy.DealershipListing</c>) and buys through <c>Dealership.SpawnVehicle(code)</c>, so the car only needs
    /// a listing, a choice next to the Cheetah, and a temporary link to the Cheetah's target node.
    /// </summary>
    [HarmonyPatch(typeof(ControlledDialogueHandler), nameof(ControlledDialogueHandler.ModifyChoiceList))]
    internal static class DealerListing
    {
        private const string AnchorCode = "cheetah";
        private const string ChoiceGuid = "yesla_model3_dealer_choice";

        private static bool _reportedMissingAnchor;
        private static bool _reportedListing;

        private static void Postfix(ControlledDialogueHandler __instance, string dialogueLabel, ref ChoiceList existingChoices)
        {
            try
            {
                var salesman = __instance.TryCast<DialogueHandler_VehicleSalesman>();
                if (salesman == null || existingChoices == null) return;

                int anchor = -1;
                for (int i = 0; i < existingChoices.Count; i++)
                {
                    var c = existingChoices[i];
                    if (c == null) continue;
                    if (string.Equals(c.ChoiceLabel, YeslaVehicleType.Code, StringComparison.OrdinalIgnoreCase)) return;
                    if (string.Equals(c.ChoiceLabel, AnchorCode, StringComparison.OrdinalIgnoreCase)) anchor = i;
                }
                if (anchor < 0) return;   // a different page of the conversation

                EnsureListing(salesman.Salesman);

                DialogueChoiceData cheetah = existingChoices[anchor];
                NodeLinkData link = DialogueHandler.ActiveDialogue != null ? DialogueHandler.ActiveDialogue.GetLink(cheetah.Guid) : null;
                if (link == null)
                {
                    if (!_reportedMissingAnchor) Core.Log.Error("[Dealer] the Cheetah choice on '" + dialogueLabel + "' has no link - the Yesla Model 3 is not offered.");
                    _reportedMissingAnchor = true;
                    return;
                }

                var choice = new DialogueChoiceData
                {
                    Guid = ChoiceGuid,
                    ChoiceLabel = YeslaVehicleType.Code,
                    ChoiceText = "The " + YeslaVehicleType.Name + " (<PRICE>)",
                    ShowWorldspaceDialogue = cheetah.ShowWorldspaceDialogue,
                };
                existingChoices.Insert(anchor + 1, choice);
                salesman.CreateTempLink(link.BaseDialogueOrBranchNodeGuid, ChoiceGuid, link.TargetNodeGuid);
                Core.Log.Msg("[Dealer] Yesla Model 3 offered on '" + dialogueLabel + "' after the Cheetah, linked to node " + link.TargetNodeGuid + ".");
            }
            catch (Exception e) { Core.Log.Error("[Dealer] adding the Yesla Model 3 to Jeremy's list failed: " + e); }
        }

        private static void EnsureListing(Jeremy jeremy)
        {
            if (jeremy == null) { Core.Log.Error("[Dealer] the salesman handler has no Jeremy - the Yesla Model 3 cannot be priced."); return; }
            var listings = jeremy.Listings;
            for (int i = 0; i < listings.Count; i++)
                if (listings[i] != null && string.Equals(listings[i].vehicleCode, YeslaVehicleType.Code, StringComparison.OrdinalIgnoreCase)) return;

            listings.Add(new Jeremy.DealershipListing { vehicleCode = YeslaVehicleType.Code });
            if (!_reportedListing) Core.Log.Msg("[Dealer] added '" + YeslaVehicleType.Code + "' to Jeremy's listings (" + listings.Count + " cars).");
            _reportedListing = true;
        }
    }
}
