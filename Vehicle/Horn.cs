using System;
using System.Collections;
using MelonLoader;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Honk and flash from the phone, to find the car in a crowd.
    ///
    /// Vanilla cars have no horn, so a recorded one (<see cref="Clips"/>) plays at the car on every peer when the host
    /// broadcasts it, with a synthesised tone as the stand-in if the recording cannot be read. The flash toggles <c>VehicleLights.HeadlightsOn</c>, a SyncVar that replicates at about 4 Hz,
    /// on the host only; each step is held longer than that so remote peers see every one.
    /// </summary>
    internal static class Horn
    {
        private const int Rate = 44100;
        private const float FlashStep = 0.35f;
        private const int Flashes = 2;

        private static AudioClip _clip;

        /// <summary>Everyone: sound the horn at the car.</summary>
        internal static void Play(LandVehicle car)
        {
            if (car == null) { Core.Log.Warning("[Horn] honk for a car that is gone."); return; }
            try
            {
                AudioClip recorded = Clips.Get("horn");
                AudioSource source = SourceOn(car);
                source.PlayOneShot(recorded != null ? recorded : Clip(), 0.8f * Clips.Volume());
                Core.Log.Msg("[Horn] honked " + car.VehicleName + " #" + Fleet.NetId(car) + " at " + car.transform.position.ToString("F0") + ".");
            }
            catch (Exception e) { Core.Log.Error("[Horn] could not play the horn: " + e); }
        }

        /// <summary>Everyone: one chirp when the car locks, two when it unlocks.</summary>
        internal static void Chirp(LandVehicle car, bool locked)
        {
            if (car == null) { Core.Log.Warning("[Horn] lock chirp for a car that is gone."); return; }
            if (!Clips.PlayAt(car, "chirp", "YeslaHorn", 0.7f)) return;
            if (!locked) MelonCoroutines.Start(SecondChirp(car));
            Core.Log.Msg("[Horn] " + (locked ? "lock" : "unlock") + " chirp on " + car.VehicleName + " #" + Fleet.NetId(car) + ".");
        }

        private static IEnumerator SecondChirp(LandVehicle car)
        {
            yield return new WaitForSeconds(0.22f);
            if (car != null) Clips.PlayAt(car, "chirp", "YeslaHorn", 0.7f);
        }

        /// <summary>Host: flash the headlights twice and leave them as they were.</summary>
        internal static void Flash(LandVehicle car)
        {
            if (car == null || car.lights == null) { Core.Log.Warning("[Horn] no lights to flash."); return; }
            MelonCoroutines.Start(FlashRoutine(car));
        }

        private static IEnumerator FlashRoutine(LandVehicle car)
        {
            VehicleLights lights = car.lights;
            bool was = lights.HeadlightsOn;
            for (int i = 0; i < Flashes * 2; i++)
            {
                if (car == null || lights == null) yield break;
                try { lights.HeadlightsOn = (i % 2 == 0) != was; }
                catch (Exception e) { Core.Log.Error("[Horn] flashing the lights failed: " + e.Message); yield break; }
                yield return new WaitForSeconds(FlashStep);
            }
            if (car == null || lights == null) yield break;
            lights.HeadlightsOn = was;
        }

        private static AudioSource SourceOn(LandVehicle car) => Clips.On(car, "YeslaHorn", new Vector3(0f, 0.6f, 1.8f), 6f, 120f);

        /// <summary>A two-tone car horn: two detuned square-ish tones a minor third apart, 0.55 s with soft edges.</summary>
        private static AudioClip Clip()
        {
            if (_clip != null) return _clip;
            const float length = 0.55f;
            int count = Mathf.RoundToInt(length * Rate);
            var data = new float[count];
            float tau = 2f * Mathf.PI;
            for (int i = 0; i < count; i++)
            {
                float t = (float)i / Rate;
                float env = Mathf.Clamp01(t / 0.02f) * Mathf.Clamp01((length - t) / 0.06f);
                float s = 0f;
                foreach (float f in new[] { 415f, 494f })
                {
                    // Odd harmonics give the reedy edge a horn has; 1/n keeps it from turning harsh.
                    for (int n = 1; n <= 7; n += 2) s += Mathf.Sin(tau * f * n * t) / n;
                }
                data[i] = 0.22f * env * s;
            }
            _clip = AudioClip.Create("YeslaHorn", count, 1, Rate, false);
            _clip.SetData(data, 0);
            _clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _clip;
        }
    }
}
