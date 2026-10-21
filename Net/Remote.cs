using System;
using System.Globalization;
using Chassis.Api;
using Il2CppScheduleOne.Vehicles;
using Yesla.Driving;
using Yesla.Vehicle;

namespace Yesla.Net
{
    /// <summary>
    /// What the phone buttons do across the network: requests a player sends to the host, and the state the host
    /// sends back to everyone. The host checks ownership on every request; a client only ever asks. Horn, lock, lids
    /// and indicators go through Chassis, which carries them to the host and back the same way.
    /// </summary>
    internal static class Remote
    {
        internal static void Init()
        {
            YeslaNet.OnRequest = OnRequest;
            YeslaNet.OnState = OnState;
        }

        // ---- the asking player -------------------------------------------------------------------------------------

        internal static string Honk(LandVehicle car)
        {
            if (car == null) return "No Yesla.";
            if (!Fleet.IsMine(car)) return "This Yesla is not yours.";
            ChassisApi.Honk(car, flash: true);
            return null;
        }

        internal static string ToggleLock(LandVehicle car)
        {
            if (car == null) return "No Yesla.";
            if (!Fleet.IsMine(car)) return "This Yesla is not yours.";
            ChassisApi.SetLocked(car, !ChassisApi.IsLocked(car));
            return null;
        }

        internal static string ToggleSentry(LandVehicle car)
        {
            if (car == null) return "No Yesla.";
            Fleet.Rec r = Fleet.For(car);
            if (r == null || !Fleet.IsMine(car)) return "This Yesla is not yours.";
            YeslaNet.ToHost("req.sentry", Id(car), Fleet.MyCode(), r.Sentry ? "0" : "1");
            return null;
        }

        /// <summary>Ask the host for a new plate; empty text asks for the factory plate back.</summary>
        internal static string SetPlate(LandVehicle car, string text)
        {
            if (car == null) return "No Yesla.";
            if (!Fleet.IsMine(car)) return "This Yesla is not yours.";
            YeslaNet.ToHost("req.plate", Id(car), Fleet.MyCode(), Plate.Clean(text));
            return null;
        }

        /// <summary>Opens or closes the frunk or the boot lid from the phone; every player sees it.</summary>
        internal static string ToggleLid(LandVehicle car, string which)
        {
            if (car == null) return "No Yesla.";
            if (which != "frunk" && which != "trunk") return "Unknown lid.";
            if (!Fleet.IsMine(car)) return "This Yesla is not yours.";
            ChassisApi.SetPart(car, which, !ChassisApi.PartOpen(car, which));
            return null;
        }

        /// <summary>A player opened the car's storage at the frunk (front) or the trunk; the lid at that end opens.</summary>
        internal static void StorageSide(LandVehicle car, bool front)
        {
            if (car == null) return;
            ChassisApi.SetStorageEnd(car, front);
        }

        /// <summary>Tell the host this car now stands in a lot spot, so no NPC car is parked into it.</summary>
        internal static void TakeSpot(LandVehicle car, ParkTarget spot)
        {
            if (car == null || spot == null || spot.Lot == null) return;
            YeslaNet.ToHost("req.park", Id(car), Fleet.MyCode(), spot.LotGuid, spot.Index.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The turn signal of the car this player's Assisted Driving drives: -1 left, 1 right, 0 off.</summary>
        internal static void Blink(LandVehicle car, int side)
        {
            if (car == null) return;
            ChassisApi.SetIndicator(car, side);
        }

        private static string Id(LandVehicle car) => Fleet.NetId(car).ToString(CultureInfo.InvariantCulture);

        // ---- host --------------------------------------------------------------------------------------------------

        private static void OnRequest(YeslaNet.Msg m)
        {
            Core.Log.Msg("[Remote] request " + m + ".");
            switch (m.Op)
            {
                case "req.sync":
                    Fleet.Broadcast(force: true);
                    return;
                case "req.summon":
                    if (!int.TryParse(m.Arg(0), out int sid) || !Summon.TryVec(m.Arg(2), out Vector3 at)) { Bad(m); return; }
                    Summon.HostStart(sid, m.Arg(1), at, m.Arg(3));
                    return;
                case "req.goto":
                    if (!int.TryParse(m.Arg(0), out int gid) || !Summon.TryVec(m.Arg(2), out Vector3 to)) { Bad(m); return; }
                    Summon.HostStart(gid, m.Arg(1), to, m.Arg(3), goTo: true, label: m.Arg(4));
                    return;
                case "req.unsummon":
                    if (!int.TryParse(m.Arg(0), out int uid)) { Bad(m); return; }
                    Summon.HostCancel(uid, m.Arg(1));
                    return;
                case "req.sentry":
                    {
                        LandVehicle car = OwnedCar(m, out string why);
                        if (car == null) { Core.Log.Warning("[Remote] " + m.Op + " refused: " + why + "."); return; }
                        string refused = Fleet.SetSentry(car, m.Arg(1), m.Arg(2) == "1");
                        if (refused != null) Core.Log.Warning("[Remote] " + m.Op + " refused: " + refused + ".");
                        return;
                    }
                case "req.plate":
                    {
                        LandVehicle car = OwnedCar(m, out string why);
                        if (car == null) { Core.Log.Warning("[Remote] plate refused: " + why + "."); return; }
                        string refused = Fleet.SetPlate(car, m.Arg(1), m.Arg(2));
                        if (refused != null) Core.Log.Warning("[Remote] plate refused: " + refused + ".");
                        return;
                    }
                case "req.park":
                    {
                        LandVehicle car = OwnedCar(m, out string why);
                        if (car == null) { Core.Log.Warning("[Remote] park refused: " + why + "."); return; }
                        Parking.HostTake(car, m.Arg(2), m.Arg(3));
                        return;
                    }
                default:
                    Core.Log.Warning("[Remote] unknown request " + m + " - is every player on the same Yesla version?");
                    return;
            }
        }

        /// <summary>The car a request names, if the requester owns it.</summary>
        private static LandVehicle OwnedCar(YeslaNet.Msg m, out string why)
        {
            if (!int.TryParse(m.Arg(0), out int id)) { why = "unreadable car id '" + m.Arg(0) + "'"; return null; }
            LandVehicle car = Fleet.FindById(id);
            if (car == null) { why = "no car #" + id; return null; }
            string owner = ChassisApi.OwnerOf(car);
            if (owner != m.Arg(1)) { why = m.Arg(1) + " does not own #" + id + " (owner " + (owner.Length > 0 ? owner : "unknown") + ")"; return null; }
            why = null;
            return car;
        }

        private static void Bad(YeslaNet.Msg m) => Core.Log.Warning("[Remote] unreadable request " + m + ".");

        // ---- everyone ----------------------------------------------------------------------------------------------

        private static void OnState(YeslaNet.Msg m)
        {
            switch (m.Op)
            {
                case "cars": Fleet.ApplyState(m.Arg(0)); return;
                case "summon": Summon.ApplyState(m.Arg(0), m.Arg(1), m.Arg(2)); return;
                case "sentry": Sentry.ApplyState(m.Arg(0), m.Arg(1), m.Arg(2)); return;
                default:
                    Core.Log.Warning("[Remote] unknown state " + m + " - is every player on the same Yesla version?");
                    return;
            }
        }
    }
}
