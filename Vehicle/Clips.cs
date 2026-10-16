using System;
using System.Collections.Generic;
using System.IO;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.Core.Audio;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Vehicles;
using UnityEngine.Audio;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The recorded sounds the mod ships: horn, lock chirp, the Assisted Driving chimes and the pedestrian warning.
    /// Each is a CC0 recording from freesound.org, cut and stored as 16-bit mono PCM at 44.1 kHz under
    /// <c>Assets/sounds</c>, and turned into an <see cref="AudioClip"/> the first time it is played.
    ///
    /// The game's sound settings apply the way vanilla applies them in <c>AudioSourceController</c>: every source goes
    /// through <c>AudioManager.MainGameMixer</c>, and every volume is multiplied by <c>AudioManager.GetVolume(FX)</c>,
    /// which is the master slider times the effects slider.
    /// </summary>
    internal static class Clips
    {
        private const int Rate = 44100;
        private const string Prefix = "Yesla.Assets.sounds.";

        private static readonly Dictionary<string, AudioClip> Loaded = new Dictionary<string, AudioClip>();
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static AudioMixerGroup _group;
        private static AudioSource _cabin;
        private static bool _noManager;

        /// <summary>The game's master times effects volume, 0 to 1; 1 with a warning when the audio manager is missing.</summary>
        internal static float Volume()
        {
            try
            {
                if (Singleton<AudioManager>.InstanceExists)
                {
                    _noManager = false;
                    return Mathf.Clamp01(Singleton<AudioManager>.Instance.GetVolume(EAudioType.FX, true));
                }
            }
            catch (Exception e) { if (!_noManager) Core.Log.Warning("[Sound] could not read the game's volume: " + e.Message); _noManager = true; return 1f; }
            if (!_noManager) Core.Log.Warning("[Sound] no audio manager - Yesla sounds play at full volume.");
            _noManager = true;
            return 1f;
        }

        /// <summary>The clip, or null when it could not be read (logged once).</summary>
        internal static AudioClip Get(string name)
        {
            if (Loaded.TryGetValue(name, out AudioClip clip) && clip != null) return clip;
            if (Missing.Contains(name)) return null;
            try
            {
                byte[] bytes;
                using (Stream s = typeof(Clips).Assembly.GetManifestResourceStream(Prefix + name + ".pcm"))
                {
                    if (s == null) { Missing.Add(name); Core.Log.Error("[Sound] '" + name + "' is not in the mod."); return null; }
                    bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = s.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                int count = bytes.Length / 2;
                var data = new float[count];
                for (int i = 0; i < count; i++) data[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8)) / 32768f;
                clip = AudioClip.Create("Yesla_" + name, count, 1, Rate, false);
                clip.SetData(data, 0);
                // Cars outlive scenes; so must the clips they play.
                clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Loaded[name] = clip;
                Core.Log.Msg("[Sound] loaded '" + name + "', " + ((float)count / Rate).ToString("F2") + " s.");
                return clip;
            }
            catch (Exception e)
            {
                Missing.Add(name);
                Core.Log.Error("[Sound] could not read '" + name + "': " + e);
                return null;
            }
        }

        /// <summary>A child audio source on the car, made once per name.</summary>
        internal static AudioSource On(LandVehicle car, string name, Vector3 local, float minDistance, float maxDistance)
        {
            Transform t = car.transform.Find(name);
            if (t != null)
            {
                AudioSource existing = t.GetComponent<AudioSource>();
                if (existing != null) return existing;
            }
            var go = t != null ? t.gameObject : new GameObject(name);
            go.transform.SetParent(car.transform, false);
            go.transform.localPosition = local;
            var source = go.AddComponent<AudioSource>();
            source.spatialBlend = 1f;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.playOnAwake = false;
            source.dopplerLevel = 0f;
            source.outputAudioMixerGroup = Group();
            return source;
        }

        /// <summary>Everyone hears it at the car.</summary>
        internal static bool PlayAt(LandVehicle car, string clipName, string sourceName, float volume)
        {
            if (car == null) { Core.Log.Warning("[Sound] '" + clipName + "' for a car that is gone."); return false; }
            AudioClip clip = Get(clipName);
            if (clip == null) return false;
            try
            {
                AudioSource source = On(car, sourceName, new Vector3(0f, 0.6f, 1.8f), 6f, 120f);
                source.PlayOneShot(clip, volume * Volume());
                return true;
            }
            catch (Exception e) { Core.Log.Error("[Sound] could not play '" + clipName + "': " + e.Message); return false; }
        }

        /// <summary>Only this player hears it, inside the car: the chimes of the centre screen.</summary>
        internal static bool PlayCabin(string clipName, float volume)
        {
            AudioClip clip = Get(clipName);
            if (clip == null) return false;
            try
            {
                if (_cabin == null)
                {
                    var go = new GameObject("YeslaCabinSound");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _cabin = go.AddComponent<AudioSource>();
                    _cabin.spatialBlend = 0f;
                    _cabin.playOnAwake = false;
                }
                if (_cabin.outputAudioMixerGroup == null) _cabin.outputAudioMixerGroup = Group();
                _cabin.PlayOneShot(clip, volume * Volume());
                return true;
            }
            catch (Exception e) { Core.Log.Error("[Sound] could not play '" + clipName + "' in the cabin: " + e.Message); return false; }
        }

        /// <summary>The mixer group every in-world game sound plays through; it carries the pause menu's muting.</summary>
        private static AudioMixerGroup Group()
        {
            if (_group != null) return _group;
            try
            {
                if (Singleton<AudioManager>.InstanceExists) _group = Singleton<AudioManager>.Instance.MainGameMixer;
                if (_group == null) Core.Log.Warning("[Sound] no game mixer group yet - this sound skips the mixer.");
            }
            catch (Exception e) { Core.Log.Warning("[Sound] could not read the game mixer group: " + e.Message); }
            return _group;
        }
    }
}
