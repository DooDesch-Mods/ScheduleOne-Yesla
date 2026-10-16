#if DEBUG
using System.Globalization;
using System.Text;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;

namespace Yesla.Debugging
{
    /// <summary>
    /// 'yesla accel': full throttle from standstill, straight ahead, and the time to each speed mark. Measures the
    /// car's own drivetrain without the autopilot's speed caps.
    /// </summary>
    internal static class AccelTest
    {
        private static readonly float[] Marks = { 20f, 30f, 50f, 60f, 80f, 100f };
        private const float MaxSeconds = 20f;

        private static LandVehicle _car;
        private static float _start;
        private static int _next;
        private static float _top;
        private static readonly StringBuilder _result = new StringBuilder();
        private static readonly StringBuilder _trace = new StringBuilder();
        private static float _nextTrace;

        public static string Start(LandVehicle car)
        {
            if (car == null) return "accel: not in a car";
            if (Mathf.Abs(car.Speed_Kmh) > 1f) return "accel: stop first (" + car.Speed_Kmh.ToString("F0") + " km/h)";
            _car = car; _start = Time.time; _next = 0; _top = 0f; _result.Clear(); _trace.Clear(); _nextTrace = 1f;
            car.overrideControls = true;
            var ci = CultureInfo.InvariantCulture;
            var keys = new StringBuilder();
            foreach (Keyframe k in car.motorTorque.keys) keys.Append(' ').Append(k.time.ToString("F0", ci)).Append("->").Append(k.value.ToString("F0", ci));
            Rigidbody rb = car.Rb;
            Core.Log.Msg("[Accel] start in " + car.VehicleCode + " top speed setting " + car.TopSpeed.ToString("F0", ci)
                + " torque curve" + keys + " diffGearing " + car.diffGearing.ToString("F2", ci)
                + " mass " + (rb != null ? rb.mass.ToString("F0", ci) : "-") + " drag " + (rb != null ? rb.drag.ToString("F3", ci) : "-")
                + " drive wheels " + (car.driveWheels != null ? car.driveWheels.Length : 0));
            return "accel: running, full throttle for up to " + MaxSeconds + " s";
        }

        /// <summary>Runs after VehicleAgent and the autopilot, so nothing else writes the throttle this frame.</summary>
        public static void LateTick()
        {
            if (_car == null) return;
            float t = Time.time - _start;
            float kmh = Mathf.Abs(_car.Speed_Kmh);
            _top = Mathf.Max(_top, kmh);
            if (t >= _nextTrace) { _trace.Append(' ').Append(kmh.ToString("F0", CultureInfo.InvariantCulture)); _nextTrace += 1f; }
            _car.overrideControls = true;
            _car.throttleOverride = 1f;
            _car.steerOverride = 0f;
            while (_next < Marks.Length && kmh >= Marks[_next])
            {
                _result.Append(" 0-").Append(Marks[_next].ToString("F0")).Append('=').Append(t.ToString("F2", CultureInfo.InvariantCulture)).Append('s');
                _next++;
            }
            if (_next >= Marks.Length || t >= MaxSeconds)
            {
                Core.Log.Msg("[Accel] done after " + t.ToString("F1", CultureInfo.InvariantCulture) + " s:" + _result
                    + " top=" + _top.ToString("F1", CultureInfo.InvariantCulture) + " km/h, per second:" + _trace);
                _car.throttleOverride = 0f;
                _car.overrideControls = false;
                _car = null;
            }
        }
    }
}
#endif
