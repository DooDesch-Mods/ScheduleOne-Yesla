using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Vehicles;
using Yesla.Net;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Sentry Mode: a parked Yesla with Sentry on watches its surroundings and tells its owner's phone when someone
    /// lingers at the car or something hits it.
    ///
    /// Vanilla cars have no health, alarm or theft logic; the only signal is <c>LandVehicle.OnCollisionEnter</c>
    /// (an impact) plus who stands next to the car. The host watches, because it is the one place where every car,
    /// NPC and player is known and the empty car is simulated; the alert goes out as a broadcast that only the
    /// owner's machine turns into a phone notification.
    /// </summary>
    [HarmonyPatch]
    internal static class Sentry
    {
        private const float ScanSeconds = 0.5f;
        /// <summary>Closer than this to the car counts as being at it.</summary>
        private const float Radius = 3.5f;
        /// <summary>Someone has to stay this long before it is worth an alert.</summary>
        private const float Linger = 2f;
        /// <summary>One alert per car at most this often.</summary>
        private const float Cooldown = 30f;
        /// <summary>A hit softer than this (relative speed, m/s) is a nudge, not an alert.</summary>
        private const float HitSpeed = 2f;

        private sealed class Watch { internal float NearSince = -1f; internal float LastAlert = -100f; }
        private static readonly Dictionary<int, Watch> Watches = new Dictionary<int, Watch>();
        private static float _nextScan;

        /// <summary>Fires on the owner's machine: the car, and what happened.</summary>
        internal static event Action<LandVehicle, string> Alerted;

        internal static void Tick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.Sentry.Tick");
#endif
            if (!YeslaNet.IsHost) return;
            float now = Time.time;
            if (now < _nextScan) return;
            _nextScan = now + ScanSeconds;

            var all = NetworkSingleton<VehicleManager>.InstanceExists ? NetworkSingleton<VehicleManager>.Instance.AllVehicles : null;
            if (all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                LandVehicle car = all[i];
                if (car == null || !Phone.YeslaApp.IsYesla(car)) continue;
                Fleet.Rec rec = Fleet.HostRec(car);
                if (rec == null || !rec.Sentry || car.IsOccupied) continue;

                int id = Fleet.NetId(car);
                if (!Watches.TryGetValue(id, out Watch w)) Watches[id] = w = new Watch();

                string who = SomeoneAt(car, rec.Owner);
                if (who == null) { w.NearSince = -1f; continue; }
                if (w.NearSince < 0f) { w.NearSince = now; continue; }
                if (now - w.NearSince < Linger) continue;
                Alert(car, id, w, rec.Owner, "near", who);
            }
        }

        /// <summary>Who is at the car, or null. The owner is not a stranger.</summary>
        private static string SomeoneAt(LandVehicle car, string owner)
        {
            Vector3 at = car.transform.position;
            var players = Player.PlayerList;
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                if (p == null || p.PlayerCode == owner) continue;
                if (Vector3.Distance(p.transform.position, at) < Radius) return "player " + p.PlayerName;
            }
            var npcs = NPCManager.NPCRegistry;
            for (int i = 0; i < npcs.Count; i++)
            {
                NPC n = npcs[i];
                if (n == null || n.IsInVehicle || n.isInBuilding || !n.IsConscious) continue;
                if (Vector3.Distance(n.transform.position, at) < Radius) return "npc " + n.ID;
            }
            return null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LandVehicle), nameof(LandVehicle.OnCollisionEnter))]
        private static void CollisionPostfix(LandVehicle __instance, Collision collision)
        {
            try
            {
                if (!YeslaNet.IsHost || __instance == null || collision == null || !Phone.YeslaApp.IsYesla(__instance)) return;
                if (__instance.IsOccupied) return;
                Fleet.Rec rec = Fleet.HostRec(__instance);
                if (rec == null || !rec.Sentry) return;
                float speed = collision.relativeVelocity.magnitude;
                if (speed < HitSpeed) return;

                int id = Fleet.NetId(__instance);
                if (!Watches.TryGetValue(id, out Watch w)) Watches[id] = w = new Watch();
                string by = collision.collider != null ? collision.collider.name : "something";
                Alert(__instance, id, w, rec.Owner, "hit", by + " at " + speed.ToString("F1") + " m/s");
            }
            catch (Exception e) { Core.Log.Error("[Sentry] collision check failed: " + e.Message); }
        }

        private static void Alert(LandVehicle car, int id, Watch w, string owner, string kind, string detail)
        {
            float now = Time.time;
            if (now - w.LastAlert < Cooldown) return;
            w.LastAlert = now;
            w.NearSince = -1f;
            Core.Log.Msg("[Sentry] " + car.VehicleName + " #" + id + " of " + owner + ": " + kind + " (" + detail + ").");
            YeslaNet.ToAll("sentry", id.ToString(CultureInfo.InvariantCulture), owner, kind);
        }

        /// <summary>Everyone: an alert went out; only the owner reacts.</summary>
        internal static void ApplyState(string carIdText, string owner, string kind)
        {
            if (owner != Fleet.MyCode()) return;
            if (!int.TryParse(carIdText, out int id)) { Core.Log.Warning("[Sentry] unreadable alert for '" + carIdText + "'."); return; }
            LandVehicle car = Fleet.FindById(id);
            Core.Log.Msg("[Sentry] alert for my car #" + id + ": " + kind + ".");
            try { Alerted?.Invoke(car, kind); }
            catch (Exception e) { Core.Log.Error("[Sentry] an alert listener failed: " + e); }
        }

        internal static void Reset()
        {
            Watches.Clear();
            _nextScan = 0f;
        }
    }
}
