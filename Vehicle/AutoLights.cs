using System;
using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Automatic headlights: a Yesla that is driven, by a player or by Assisted Driving, has its headlights on from
    /// 17:30 to 06:00, the hours vanilla's NPC drivers switch theirs on (<c>NPC.cs:687</c>). Vanilla cars have no such
    /// switch; only the motorbike does.
    ///
    /// It switches only when the wish changes: night falls, the day breaks, a drive starts or ends. A player who
    /// presses the light key in between keeps that choice until the next change. Runs on the host, which writes the
    /// lights' SyncVar for every player.
    /// </summary>
    internal static class AutoLights
    {
        private const int OnFrom = 1730, OffFrom = 600;
        private const float CheckEvery = 1f;
        /// <summary>An empty car moving faster than this is being driven by Summon.</summary>
        private const float MovingKmh = 3f;

        private static readonly Dictionary<IntPtr, bool> Wanted = new Dictionary<IntPtr, bool>();
        private static readonly List<IntPtr> Seen = new List<IntPtr>();
        private static float _next;
        private static bool _warned;

        internal static void Tick()
        {
            float now = Time.time;
            if (now < _next) return;
            _next = now + CheckEvery;
            if (!NetworkSingleton<TimeManager>.InstanceExists || !NetworkSingleton<VehicleManager>.InstanceExists)
            {
                if (!_warned) Core.Log.Warning("[Lights] no clock or vehicle manager yet - automatic headlights wait.");
                _warned = true;
                return;
            }
            _warned = false;
            bool night = NetworkSingleton<TimeManager>.Instance.IsCurrentTimeWithinRange(OnFrom, OffFrom);
            var all = NetworkSingleton<VehicleManager>.Instance.AllVehicles;
            Seen.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                LandVehicle car = all[i];
                if (car == null || !Phone.YeslaApp.IsYesla(car)) continue;
                Seen.Add(car.Pointer);
                bool driven = car.IsOccupied || (car.Agent != null && car.Agent.AutoDriving) || Mathf.Abs(car.Speed_Kmh) > MovingKmh;
                bool want = night && driven;
                if (Wanted.TryGetValue(car.Pointer, out bool was) && was == want) continue;
                Wanted[car.Pointer] = want;
                Apply(car, want, night, driven);
            }
            if (Wanted.Count > Seen.Count)
            {
                var gone = new List<IntPtr>();
                foreach (IntPtr p in Wanted.Keys) if (!Seen.Contains(p)) gone.Add(p);
                foreach (IntPtr p in gone) Wanted.Remove(p);
            }
        }

        private static void Apply(LandVehicle car, bool on, bool night, bool driven)
        {
            VehicleLights lights = car.lights;
            if (lights == null) { Core.Log.Warning("[Lights] " + car.VehicleName + " #" + Fleet.NetId(car) + " has no lights to switch."); return; }
            if (lights.HeadlightsOn == on) return;
            try
            {
                lights.HeadlightsOn = on;
                Core.Log.Msg("[Lights] " + car.VehicleName + " #" + Fleet.NetId(car) + " headlights " + (on ? "on" : "off")
                             + " (" + (night ? "night" : "day") + ", " + (driven ? "driven" : "parked") + ").");
            }
            catch (Exception e) { Core.Log.Error("[Lights] switching the headlights of " + car.VehicleName + " failed: " + e.Message); }
        }

        internal static void Reset()
        {
            Wanted.Clear();
            _next = 0f;
            _warned = false;
        }
    }
}
