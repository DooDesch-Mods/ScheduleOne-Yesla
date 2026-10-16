#if DEBUG
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;

namespace Yesla.Debugging
{
    /// <summary>
    /// 'yesla jitter [seconds]': watches the car the player sits in, frame by frame, and tells a stutter from a jump.
    /// A stutter is a long frame: the car moves its usual speed times a long time. A jump is a frame where the shown
    /// position moves further, or less far, than speed times frame time says it should. Also counts the physics steps
    /// per frame, the rigidbody's interpolation and the garbage collections, because each of those shows up as one of
    /// the two.
    /// </summary>
    internal static class JitterProbe
    {
        private const float JumpMetres = 0.15f;

        /// <summary>Yesla's own Update and LateUpdate time this frame; Core starts and stops it around its ticks.</summary>
        internal static readonly System.Diagnostics.Stopwatch Own = new System.Diagnostics.Stopwatch();

        private sealed class Frame
        {
            internal float Time, Dt, Err, Kmh, CamErr, OwnMs;
            internal int Steps, Gc;
        }

        private static LandVehicle _car;
        private static float _until, _start;
        private static Vector3 _lastPos, _lastCamOffset;
        private static float _lastFixed;
        private static bool _have;
        private static int _lastGc, _lastIl2CppGc;
        private static string _lastMode = string.Empty;
        private static readonly List<Frame> Frames = new List<Frame>();
        private static readonly StringBuilder Modes = new StringBuilder();

        public static string Start(LandVehicle car, float seconds)
        {
            if (car == null) return "jitter: not in a car";
            _car = car;
            _start = Time.time;
            _until = Time.time + Mathf.Clamp(seconds, 2f, 120f);
            _have = false;
            Frames.Clear();
            Modes.Clear();
            _lastMode = string.Empty;
            return "jitter: watching " + car.VehicleName + " for " + (_until - _start).ToString("F0") + " s";
        }

        private static int Il2CppGcCount()
        {
            try { return Il2CppSystem.GC.CollectionCount(0); }
            catch (System.Exception e) { Core.Log.Warning("[Jitter] IL2CPP GC count unreadable, counted as 0: " + e.Message); return 0; }
        }

        /// <summary>Runs in LateUpdate, where the transform already holds the interpolated pose the frame shows.</summary>
        public static void LateTick()
        {
            float ownMs = (float)Own.Elapsed.TotalMilliseconds;
            Own.Reset();
            if (_car == null) return;
            if (_car.Rb == null) { Core.Log.Warning("[Jitter] the car lost its rigidbody - stopped watching."); _car = null; return; }
            Rigidbody rb = _car.Rb;
            Vector3 pos = _car.transform.position;
            Camera cam = Camera.main;
            Vector3 camOffset = cam != null ? cam.transform.position - pos : Vector3.zero;
            string mode = rb.interpolation + (rb.isKinematic ? " kinematic" : " simulated");
            if (mode != _lastMode)
            {
                Modes.Append(' ').Append((Time.time - _start).ToString("F1", CultureInfo.InvariantCulture)).Append("s=").Append(mode);
                _lastMode = mode;
            }
            int gc = System.GC.CollectionCount(0);
            int il2gc = Il2CppGcCount();
            if (_have)
            {
                float dt = Time.deltaTime;
                Vector3 moved = pos - _lastPos;
                Vector3 expected = rb.velocity * dt;
                Frames.Add(new Frame
                {
                    Time = Time.time - _start,
                    Dt = dt,
                    Err = (moved - expected).magnitude,
                    Kmh = rb.velocity.magnitude * 3.6f,
                    CamErr = (camOffset - _lastCamOffset).magnitude,
                    Steps = Mathf.RoundToInt((Time.fixedTime - _lastFixed) / Mathf.Max(0.001f, Time.fixedDeltaTime)),
                    Gc = (gc - _lastGc) + (il2gc - _lastIl2CppGc),
                    OwnMs = ownMs,
                });
            }
            _have = true;
            _lastPos = pos;
            _lastCamOffset = camOffset;
            _lastFixed = Time.fixedTime;
            _lastGc = gc;
            _lastIl2CppGc = il2gc;
            if (Time.time >= _until) Report();
        }

        private static void Report()
        {
            var ci = CultureInfo.InvariantCulture;
            int n = Frames.Count;
            _car = null;
            if (n == 0) { Core.Log.Warning("[Jitter] no frames recorded."); return; }
            var dts = new List<float>(n);
            int jumps = 0, long33 = 0, long50 = 0, gcs = 0;
            float errMax = 0f, errSum = 0f, camMax = 0f, ownSum = 0f, ownMax = 0f;
            var owns = new List<float>(n);
            var steps = new int[4];
            foreach (Frame f in Frames)
            {
                dts.Add(f.Dt);
                if (f.Err > JumpMetres) jumps++;
                if (f.Dt > 1f / 30f) long33++;
                if (f.Dt > 0.05f) long50++;
                gcs += f.Gc;
                errMax = Mathf.Max(errMax, f.Err);
                errSum += f.Err;
                camMax = Mathf.Max(camMax, f.CamErr);
                ownSum += f.OwnMs;
                ownMax = Mathf.Max(ownMax, f.OwnMs);
                owns.Add(f.OwnMs);
                steps[Mathf.Clamp(f.Steps, 0, 3)]++;
            }
            dts.Sort();
            owns.Sort();
            float total = 0f;
            foreach (float d in dts) total += d;
            var sb = new StringBuilder();
            sb.Append("[Jitter] ").Append(n).Append(" frames in ").Append(total.ToString("F1", ci)).Append(" s, ")
              .Append((n / total).ToString("F0", ci)).Append(" fps; frame ms median ").Append((dts[n / 2] * 1000f).ToString("F1", ci))
              .Append(" p99 ").Append((dts[Mathf.Min(n - 1, (int)(n * 0.99f))] * 1000f).ToString("F1", ci))
              .Append(" max ").Append((dts[n - 1] * 1000f).ToString("F1", ci))
              .Append("; over 33 ms ").Append(long33).Append(", over 50 ms ").Append(long50)
              .Append("; physics steps per frame 0/1/2/3+ = ").Append(steps[0]).Append('/').Append(steps[1]).Append('/').Append(steps[2]).Append('/').Append(steps[3])
              .Append(" at ").Append((1f / Time.fixedDeltaTime).ToString("F0", ci)).Append(" Hz")
              .Append("; position off speed x time: mean ").Append((errSum / n * 100f).ToString("F1", ci)).Append(" cm, max ").Append((errMax * 100f).ToString("F1", ci))
              .Append(" cm, ").Append(jumps).Append(" frames over ").Append((JumpMetres * 100f).ToString("F0", ci)).Append(" cm")
              .Append("; camera against the car max ").Append((camMax * 100f).ToString("F1", ci)).Append(" cm")
              .Append("; GC runs ").Append(gcs)
              .Append("; Yesla's own update ms mean ").Append((ownSum / n).ToString("F2", ci))
              .Append(" p99 ").Append(owns[Mathf.Min(n - 1, (int)(n * 0.99f))].ToString("F1", ci))
              .Append(" max ").Append(ownMax.ToString("F1", ci))
              .Append("; rigidbody").Append(Modes);
            Core.Log.Msg(sb.ToString());
            // The worst frames by position error, and by length, with what else happened in them.
            Frames.Sort((a, b) => b.Err.CompareTo(a.Err));
            var worst = new StringBuilder("[Jitter] worst jumps:");
            for (int i = 0; i < Mathf.Min(6, n); i++) Append(worst, Frames[i], ci);
            Core.Log.Msg(worst.ToString());
            Frames.Sort((a, b) => b.Dt.CompareTo(a.Dt));
            var slow = new StringBuilder("[Jitter] longest frames:");
            for (int i = 0; i < Mathf.Min(6, n); i++) Append(slow, Frames[i], ci);
            Core.Log.Msg(slow.ToString());
        }

        private static void Append(StringBuilder sb, Frame f, CultureInfo ci)
        {
            sb.Append(" [").Append(f.Time.ToString("F2", ci)).Append("s ").Append((f.Dt * 1000f).ToString("F0", ci)).Append(" ms ")
              .Append((f.Err * 100f).ToString("F0", ci)).Append(" cm ").Append(f.Steps).Append(" steps ")
              .Append(f.Kmh.ToString("F0", ci)).Append(" km/h cam ").Append((f.CamErr * 100f).ToString("F0", ci)).Append(" cm")
              .Append(" yesla ").Append(f.OwnMs.ToString("F1", ci)).Append(" ms")
              .Append(f.Gc > 0 ? " GC" : "").Append(']');
        }
    }
}
#endif
