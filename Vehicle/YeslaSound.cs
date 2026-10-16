using Il2CppScheduleOne.Vehicles.Sound;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Replaces the donor's combustion engine with an electric drive: a soft whir when the car is switched on, near
    /// silence at a standstill, and a motor whine whose pitch climbs with speed.
    ///
    /// The sounds are synthesised here rather than shipped, so there is no audio licence to carry. Vanilla
    /// <c>VehicleSound</c> keeps doing the mixing - it fades idle and loop in and out and drives their pitch from
    /// <c>EngineLoopPitchCurve</c> over speed / top speed - so only the clips and the two curves change.
    /// </summary>
    internal static class YeslaSound
    {
        private const int Rate = 44100;

        private static AudioClip _loop;
        private static AudioClip _idle;
        private static AudioClip _start;

        internal static bool Apply(GameObject car)
        {
            VehicleSound sound = car.GetComponentInChildren<VehicleSound>(true);
            if (sound == null) { Core.Log.Error("[Sound] the donor has no VehicleSound - the Yesla keeps no engine sound change."); return false; }

            EnsureClips();
            int set = 0;
            set += SetClip(sound.EngineLoopSource, _loop, "loop");
            set += SetClip(sound.EngineIdleSource, _idle, "idle");
            set += SetClip(sound.EngineStartSource, _start, "start");

            // Pitch 0.7 at a standstill to 1.9 at top speed; the clip's fundamental is 110 Hz, so ~77 to ~210 Hz.
            sound.EngineLoopPitchCurve = AnimationCurve.EaseInOut(0f, 0.7f, 1f, 1.9f);
            // Quiet when rolling slowly, never louder than the vanilla engines.
            sound.EngineLoopVolumeCurve = AnimationCurve.EaseInOut(0f, 0.12f, 1f, 0.55f);

            Core.Log.Msg("[Sound] electric drive sound on: " + set + " of 3 engine sources replaced.");
            return set == 3;
        }

        private static int SetClip(Il2CppScheduleOne.Audio.AudioSourceController controller, AudioClip clip, string what)
        {
            if (controller == null) { Core.Log.Warning("[Sound] the donor has no " + what + " source."); return 0; }
            var source = controller.GetComponent<AudioSource>();
            if (source == null) { Core.Log.Warning("[Sound] the " + what + " source has no AudioSource."); return 0; }
            source.clip = clip;
            return 1;
        }

        private static void EnsureClips()
        {
            if (_loop != null) return;
            _loop = Make("YeslaDriveLoop", 1f, MotorWhine);
            _idle = Make("YeslaIdleLoop", 1f, t => 0.12f * Standby(t));
            _start = Make("YeslaStart", 0.7f, StartWhir);
        }

        /// <summary>
        /// One second, every partial an integer number of hertz, so the loop closes without a click.
        /// A motor fundamental with two harmonics, plus the thin inverter whine an EV is known for.
        /// </summary>
        private static float MotorWhine(float t)
        {
            float tau = 2f * Mathf.PI;
            float s = 0.55f * Mathf.Sin(tau * 110f * t)
                    + 0.22f * Mathf.Sin(tau * 220f * t)
                    + 0.08f * Mathf.Sin(tau * 330f * t)
                    + 0.07f * Mathf.Sin(tau * 1320f * t);
            // A slow 2 Hz swell, so the tone breathes instead of sounding like a test signal.
            return 0.6f * s * (0.85f + 0.15f * Mathf.Sin(tau * 2f * t));
        }

        private static float Standby(float t)
        {
            float tau = 2f * Mathf.PI;
            return 0.6f * Mathf.Sin(tau * 60f * t) + 0.4f * Mathf.Sin(tau * 880f * t);
        }

        /// <summary>A short rising whir with a soft attack and release: the car waking up.</summary>
        private static float StartWhir(float t)
        {
            const float length = 0.7f;
            float env = Mathf.Clamp01(t / 0.08f) * Mathf.Clamp01((length - t) / 0.25f);
            // Frequency sweeps 300 -> 700 Hz; the phase is the integral of that sweep.
            float phase = 2f * Mathf.PI * (300f * t + 0.5f * (400f / length) * t * t);
            return 0.35f * env * (Mathf.Sin(phase) + 0.3f * Mathf.Sin(2f * phase));
        }

        private static AudioClip Make(string name, float seconds, Func<float, float> wave)
        {
            int count = Mathf.RoundToInt(seconds * Rate);
            var data = new float[count];
            for (int i = 0; i < count; i++) data[i] = wave((float)i / Rate);

            AudioClip clip = AudioClip.Create(name, count, 1, Rate, false);
            clip.SetData(data, 0);
            // The prefab outlives scenes; so must its clips.
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return clip;
        }
    }
}
