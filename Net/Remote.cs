using System;
using System.Globalization;
using Il2CppScheduleOne.Vehicles;
using Yesla.Driving;
using Yesla.Vehicle;

namespace Yesla.Net
{
    /// <summary>
    /// What the phone buttons do across the network: requests a player sends to the host, and the state the host
    /// sends back to everyone. The host checks ownership on every request; a client only ever asks.
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
            YeslaNet.ToHost("req.honk", Id(car), Fleet.MyCode());
            return null;
        }

        internal static string ToggleLock(LandVehicle car)
        {
            if (car == null) return "No Yesla.";
            Fleet.Rec r = Fleet.For(car);
            if (r == null || !Fleet.IsMine(car)) return "This Yesla is not yours.";
            YeslaNet.ToHost("req.lock", Id(car), Fleet.MyCode(), r.Locked ? "0" : "1");
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
                case "req.bought":
                    Fleet.Announce(m.Arg(0));
                    return;
                case "req.summon":
                    if (!int.TryParse(m.Arg(0), out int sid) || !Summon.TryVec(m.Arg(2), out Vector3 at)) { Bad(m); return; }
                    Summon.HostStart(sid, m.Arg(1), at);
                    return;
                case "req.unsummon":
                    if (!int.TryParse(m.Arg(0), out int uid)) { Bad(m); return; }
                    Summon.HostCancel(uid, m.Arg(1));
                    return;
                case "req.honk":
                    {
                        LandVehicle car = OwnedCar(m, out string why);
                        if (car == null) { Core.Log.Warning("[Remote] honk refused: " + why + "."); return; }
                        YeslaNet.ToAll("honk", m.Arg(0));
                        Horn.Flash(car);
                        return;
                    }
                case "req.lock":
                case "req.sentry":
                    {
                        LandVehicle car = OwnedCar(m, out string why);
                        if (car == null) { Core.Log.Warning("[Remote] " + m.Op + " refused: " + why + "."); return; }
                        bool on = m.Arg(2) == "1";
                        string refused = m.Op == "req.lock" ? Fleet.Set(car, m.Arg(1), on, null) : Fleet.Set(car, m.Arg(1), null, on);
                        if (refused != null) Core.Log.Warning("[Remote] " + m.Op + " refused: " + refused + ".");
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
            Fleet.Rec rec = Fleet.HostRec(car);
            if (rec == null || rec.Owner != m.Arg(1)) { why = m.Arg(1) + " does not own #" + id + " (owner " + (rec?.Owner ?? "unknown") + ")"; return null; }
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
                case "honk":
                    if (!int.TryParse(m.Arg(0), out int id)) { Core.Log.Warning("[Remote] unreadable honk " + m + "."); return; }
                    Horn.Play(Fleet.FindById(id));
                    return;
                default:
                    Core.Log.Warning("[Remote] unknown state " + m + " - is every player on the same Yesla version?");
                    return;
            }
        }
    }
}
