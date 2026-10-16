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
    ///
    /// After a drive at night the headlights stay on for a minute, or until the car is locked, whichever comes first.
    /// That is Tesla's "Headlights After Exit": one minute after parking in low light, off at once on a lock from the
    /// app. A summoned car that arrives counts as a drive that ended.
    /// </summary>
    internal static class AutoLights
    {
        private const int OnFrom = 1730, OffFrom = 600;
        private const float CheckEvery = 1f;
        /// <summary>An empty car moving faster than this is being driven by Summon.</summary>
        private const float MovingKmh = 3f;
        /// <summary>Seconds the headlights stay on after a drive ends at night.</summary>
        internal const float AfterglowSeconds = 60f;

        private static readonly Dictionary<IntPtr, bool> Wanted = new Dictionary<IntPtr, bool>();
        private static readonly Dictionary<IntPtr, bool> WasDriven = new Dictionary<IntPtr, bool>();
        /// <summary>When the afterglow of a car ends, in <c>Time.time</c>.</summary>
        private static readonly Dictionary<IntPtr, float> GlowUntil = new Dictionary<IntPtr, float>();
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
                bool glow = Afterglow(car, now, night, driven);
                bool want = night && (driven || glow);
                if (Wanted.TryGetValue(car.Pointer, out bool was) && was == want) continue;
                Wanted[car.Pointer] = want;
                Apply(car, want, night, driven, glow);
            }
            if (Wanted.Count > Seen.Count)
            {
                var gone = new List<IntPtr>();
                foreach (IntPtr p in Wanted.Keys) if (!Seen.Contains(p)) gone.Add(p);
                foreach (IntPtr p in gone) { Wanted.Remove(p); WasDriven.Remove(p); GlowUntil.Remove(p); }
            }
        }

        /// <summary>Whether <paramref name="car"/> is in its afterglow: a drive ended at night less than a minute ago
        /// and nobody has locked it since.</summary>
        private static bool Afterglow(LandVehicle car, float now, bool night, bool driven)
        {
            WasDriven.TryGetValue(car.Pointer, out bool wasDriven);
            WasDriven[car.Pointer] = driven;
            if (driven) { GlowUntil.Remove(car.Pointer); return false; }
            if (wasDriven && night)
            {
                GlowUntil[car.Pointer] = now + AfterglowSeconds;
                Core.Log.Msg("[Lights] " + car.VehicleName + " #" + Fleet.NetId(car) + " parked at night - headlights stay on for "
                             + AfterglowSeconds.ToString("0") + " s or until it is locked.");
            }
            if (!GlowUntil.TryGetValue(car.Pointer, out float until)) return false;

            Fleet.Rec rec = Fleet.For(car);
            string end = !night ? "the day broke" : rec != null && rec.Locked ? "it was locked" : now >= until ? "the minute is up" : null;
            if (end == null) return true;
            GlowUntil.Remove(car.Pointer);
            Core.Log.Msg("[Lights] " + car.VehicleName + " #" + Fleet.NetId(car) + " afterglow ends: " + end + ".");
            return false;
        }

        private static void Apply(LandVehicle car, bool on, bool night, bool driven, bool glow)
        {
            VehicleLights lights = car.lights;
            if (lights == null) { Core.Log.Warning("[Lights] " + car.VehicleName + " #" + Fleet.NetId(car) + " has no lights to switch."); return; }
            if (lights.HeadlightsOn == on) return;
            try
            {
                lights.HeadlightsOn = on;
                Core.Log.Msg("[Lights] " + car.VehicleName + " #" + Fleet.NetId(car) + " headlights " + (on ? "on" : "off")
                             + " (" + (night ? "night" : "day") + ", " + (driven ? "driven" : glow ? "afterglow" : "parked") + ").");
            }
            catch (Exception e) { Core.Log.Error("[Lights] switching the headlights of " + car.VehicleName + " failed: " + e.Message); }
        }

        internal static void Reset()
        {
            Wanted.Clear();
            WasDriven.Clear();
            GlowUntil.Clear();
            _next = 0f;
            _warned = false;
        }
    }
}
