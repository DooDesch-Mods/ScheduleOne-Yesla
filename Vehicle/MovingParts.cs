using System;
using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Weather;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The brake calipers steer with the wheels without spinning. The steering wheel, the wipers and the frunk and
    /// boot lids are Chassis parts (<see cref="YeslaChassis"/>); <see cref="YeslaBody"/> hangs each of them from a pivot
    /// at its hinge and records the axis here.
    ///
    /// It also keeps the tail lamps lit while the headlights are on, which vanilla VehicleLights does not do: the brake
    /// lamps take <see cref="YeslaBody.TailMat"/> whenever the brakes are off.
    /// </summary>
    internal static class MovingParts
    {
        internal const string PivotPrefix = "Yesla_";
        internal const string CaliperName = "YeslaCaliper";
        internal const string FrunkName = "YeslaFrunkInteraction";

        /// <summary>Hinge and axle directions in model space, by part name; filled once by <see cref="YeslaBody"/>.</summary>
        internal static readonly Dictionary<string, Vector3> Axes = new Dictionary<string, Vector3>();

        /// <summary>Calipers are not posed beyond this distance from the camera.</summary>
        private const float PoseRange = 80f;

        private sealed class Caliper
        {
            internal Transform T;
            internal Wheel W;
            internal bool Front;
        }

        private sealed class Car
        {
            internal LandVehicle V;
            internal VehicleLights L;
            internal bool LoggedTail, LoggedReverse;
            internal readonly List<Caliper> Calipers = new List<Caliper>();
        }

        private static readonly Dictionary<IntPtr, Car> Cars = new Dictionary<IntPtr, Car>();
        private static readonly List<LandVehicle> Yeslas = new List<LandVehicle>();
        private static float _nextList;

        // ---- per frame ---------------------------------------------------------------------------------------------

        /// <summary>Every peer, from OnLateUpdate: after the wheels have taken their pose for the frame.</summary>
        internal static void LateTick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.MovingParts.LateTick");
#endif
            float now = Time.time;
            if (now >= _nextList) RefreshList(now);
            if (Yeslas.Count == 0) return;
            Camera cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;

            for (int i = 0; i < Yeslas.Count; i++)
            {
                LandVehicle v = Yeslas[i];
                if (v == null) continue;
                Car c = For(v);
                if (c == null) continue;
                Tail(c);
                ReverseLight(c);
                if (cam != null && (v.transform.position - eye).sqrMagnitude > PoseRange * PoseRange) continue;
                Steer(c);
            }
        }

        private static void RefreshList(float now)
        {
            _nextList = now + 1f;
            Yeslas.Clear();
            if (!NetworkSingleton<VehicleManager>.InstanceExists) return;
            var all = NetworkSingleton<VehicleManager>.Instance.AllVehicles;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && Phone.YeslaApp.IsYesla(all[i])) Yeslas.Add(all[i]);
        }

        private static Car For(LandVehicle v)
        {
            if (Cars.TryGetValue(v.Pointer, out Car c)) return c;
            c = new Car { V = v, L = v.GetComponent<VehicleLights>() };
            foreach (Wheel w in v.GetComponentsInChildren<Wheel>(true))
            {
                Transform t = w.transform.Find(CaliperName);
                if (t != null) c.Calipers.Add(new Caliper { T = t, W = w, Front = w.transform.localPosition.z > 0f });
            }
            if (c.Calipers.Count == 0) Core.Log.Warning("[Parts] " + v.VehicleName + " #" + Fleet.NetId(v) + " has no calipers - those stay still.");
            else Core.Log.Msg("[Parts] " + v.VehicleName + " #" + Fleet.NetId(v) + ": " + c.Calipers.Count + " calipers found.");
            Cars[v.Pointer] = c;
            return c;
        }

        private static void Steer(Car c)
        {
            foreach (Caliper k in c.Calipers)
            {
                if (k.W == null || k.W.wheelCollider == null || k.W.wheelModel == null) continue;
                float a = k.W.wheelCollider.steerAngle;
                // The caliper sits at the wheel centre and turns with the steering, but not with the spin.
                k.T.position = k.W.wheelModel.position;
                k.T.rotation = c.V.transform.rotation * Quaternion.Euler(0f, a, 0f);
            }
        }

        /// <summary>
        /// Vanilla swaps the brake lamps between on and off in Update, only when the brake state changes; this runs
        /// after it each frame and puts the tail material on whenever the headlights are on and the brakes are not.
        /// </summary>
        private static void Tail(Car c)
        {
            VehicleLights l = c.L;
            if (l == null || YeslaBody.TailMat == null || l.brakeLightMeshes == null || l.brakeLightMeshes.Length == 0) return;
            bool tail = l.HeadlightsOn && !c.V.BrakesApplied;
            if (tail != c.LoggedTail) { c.LoggedTail = tail; Core.Log.Msg("[Parts] " + c.V.VehicleName + " #" + Fleet.NetId(c.V) + " tail lamps " + (tail ? "on" : "off") + "."); }
            Material want = tail ? YeslaBody.TailMat : (c.V.BrakesApplied ? null : l.brakeLightMat_Off);
            if (want == null) return;
            var meshes = l.brakeLightMeshes;
            for (int i = 0; i < meshes.Length; i++)
                if (meshes[i] != null && meshes[i].sharedMaterial != want && (tail || meshes[i].sharedMaterial == YeslaBody.TailMat))
                    meshes[i].sharedMaterial = want;
        }

        /// <summary>Vanilla switches the reverse light; this only logs when it does, so the log shows it ran.</summary>
        private static void ReverseLight(Car c)
        {
            var sources = c.L != null ? c.L.reverseLightSources : null;
            if (sources == null || sources.Length == 0 || sources[0] == null) return;
            bool on = sources[0].enabled;
            if (on == c.LoggedReverse) return;
            c.LoggedReverse = on;
            Core.Log.Msg("[Parts] " + c.V.VehicleName + " #" + Fleet.NetId(c.V) + " reverse light " + (on ? "on" : "off") + ".");
        }

        /// <summary>Debug: the pose of every moving part of one car.</summary>
        internal static string Describe(LandVehicle v)
        {
            if (v == null) return "no Yesla";
            Car c = For(v);
            if (c == null) return v.VehicleName + " has no Yesla model";
            var sb = new System.Text.StringBuilder();
            foreach (Caliper k in c.Calipers)
                sb.Append(k.W.name).Append(" steer ").Append(k.W.wheelCollider != null ? k.W.wheelCollider.steerAngle.ToString("F1") : "-").Append("; ");
            sb.Append("tail ").Append(c.LoggedTail ? "on" : "off")
              .Append(" | reverse light ").Append(c.LoggedReverse ? "on" : "off");
            return sb.ToString();
        }

        internal static void Reset()
        {
            Cars.Clear();
            Yeslas.Clear();
            _nextList = 0f;
        }
    }
}
