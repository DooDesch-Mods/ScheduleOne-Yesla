using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The pedestrian warning sound an electric car plays outside because its motor is near silent: audible
    /// while reversing, a quiet hum while rolling forward below 32 km/h, nothing above that or at a standstill.
    ///
    /// Every player runs it for every Yesla. <c>LandVehicle.Speed_Kmh</c> is worked out on each peer from the car's
    /// movement, signed along its forward axis, so a car another player drives backs up audibly here too. Inside the
    /// car the sound is muffled, the way an outside speaker sounds through the cabin.
    /// </summary>
    internal static class Pws
    {
        private const float ForwardLimit = 32f;
        /// <summary>The hum fades out over this many km/h below <see cref="ForwardLimit"/>.</summary>
        private const float FadeBand = 4f;
        private const float ReverseVolume = 0.3f, ForwardVolume = 0.08f, CabinFactor = 0.05f;
        private const float Rescan = 1f;

        private sealed class Entry
        {
            internal LandVehicle Car;
            internal AudioSource Source;
            internal bool Reversing;
        }

        private static readonly List<Entry> Cars = new List<Entry>();
        private static readonly HashSet<IntPtr> Known = new HashSet<IntPtr>();
        private static float _nextScan;
        private static bool _noClip;

        internal static void Tick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.Pws.Tick");
#endif
            if (_noClip) return;
            if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + Rescan; Scan(); }
            for (int i = Cars.Count - 1; i >= 0; i--)
            {
                Entry e = Cars[i];
                if (e.Car == null || e.Source == null) { Cars.RemoveAt(i); continue; }
                try { Update(e); }
                catch (Exception ex) { Core.Log.Error("[Sound] pedestrian warning failed for " + e.Car.VehicleName + ", dropping it: " + ex.Message); Cars.RemoveAt(i); }
            }
        }

        private static void Update(Entry e)
        {
            float kmh = e.Car.Speed_Kmh;
            bool back = kmh < -0.5f;
            float volume, pitch;
            if (back) { volume = ReverseVolume; pitch = 1f; }
            else if (kmh > 0.5f && kmh < ForwardLimit)
            {
                volume = ForwardVolume * Mathf.Clamp01((ForwardLimit - kmh) / FadeBand);
                pitch = 0.8f + 0.3f * kmh / ForwardLimit;
            }
            else { volume = 0f; pitch = e.Source.pitch; }
            if (e.Car.LocalPlayerIsInVehicle) volume *= CabinFactor;
            // Read every frame, so moving the game's volume sliders takes effect on a running sound.
            volume *= Clips.Volume();

            if (back != e.Reversing)
            {
                e.Reversing = back;
                Core.Log.Msg("[Sound] " + e.Car.VehicleName + " #" + Fleet.NetId(e.Car) + (back ? " reverses - warning sound loud." : " stops reversing."));
            }
            AudioSource s = e.Source;
            s.volume = Mathf.MoveTowards(s.volume, volume, Time.deltaTime * 3f);
            s.pitch = Mathf.MoveTowards(s.pitch, pitch, Time.deltaTime);
            if (s.volume > 0.001f) { if (!s.isPlaying) s.Play(); }
            else if (s.isPlaying) s.Stop();
        }

        private static void Scan()
        {
            var all = Fleet.AllVehicles();
            if (all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                LandVehicle car = all[i];
                if (car == null || Known.Contains(car.Pointer) || !Phone.YeslaApp.IsYesla(car)) continue;
                AudioClip clip = Clips.Get("pws");
                if (clip == null) { _noClip = true; Core.Log.Error("[Sound] no pedestrian warning sound - Yeslas stay silent outside."); return; }
                Known.Add(car.Pointer);
                AudioSource s = Clips.On(car, "YeslaPws", new Vector3(0f, 0.5f, 0f), 3f, 20f);
                s.clip = clip;
                s.loop = true;
                s.volume = 0f;
                Cars.Add(new Entry { Car = car, Source = s });
                Core.Log.Msg("[Sound] pedestrian warning on " + car.VehicleName + " #" + Fleet.NetId(car) + ".");
            }
        }

        /// <summary>One line per Yesla: speed, and the warning sound's volume, pitch and whether it plays.</summary>
        internal static string Describe()
        {
            if (Cars.Count == 0) return "no Yesla has a warning sound yet" + (_noClip ? " (the clip is missing)" : string.Empty);
            var sb = new System.Text.StringBuilder();
            foreach (Entry e in Cars)
            {
                if (e.Car == null || e.Source == null) continue;
                sb.Append('#').Append(Fleet.NetId(e.Car)).Append(' ').Append(e.Car.Speed_Kmh.ToString("F1")).Append(" km/h vol ")
                  .Append(e.Source.volume.ToString("F2")).Append(" pitch ").Append(e.Source.pitch.ToString("F2"))
                  .Append(e.Source.isPlaying ? " playing" : " silent").Append(e.Car.LocalPlayerIsInVehicle ? " (inside)" : string.Empty).Append(" | ");
            }
            return sb.ToString();
        }

        internal static void Reset()
        {
            Cars.Clear();
            Known.Clear();
            _nextScan = 0f;
        }
    }
}
