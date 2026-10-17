using System;
using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Weather;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The Yesla parts that move: the steering wheel follows the front wheels, the brake calipers steer with the wheels
    /// without spinning, the wipers run while it rains and the car is on, and the frunk and boot lids open.
    ///
    /// <see cref="YeslaBody"/> hangs every part from a pivot at its hinge or axle; this class only turns the pivots.
    /// Lids open for the phone app's Frunk and Trunk buttons (every player sees them, through the host) and while
    /// someone has the car's storage open: at the front for "Access frunk", at the back for "Access trunk". Both
    /// lead to the same five slots.
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

        // Steering wheel degrees per degree of road wheel, and its stop. Vanilla turns the road wheels 30 degrees at
        // full lock; a real ratio near 12 would make that a full turn, which looks the same as no turn at all.
        internal static float SteerRatio = 5f, SteerMax = 170f, SteerSign = 1f;
        // Wiper sweep in degrees, and the time of one stroke up and back in light and in heavy rain.
        internal static float WipeSweep = 72f, WipeSign = 1f, WipeSlow = 1.6f, WipeFast = 0.8f;
        // Lid angles when open, and the time a lid takes to open or close.
        internal static float BonnetOpen = 55f, BootOpen = 70f, LidSign = 1f, LidSeconds = 1.2f;

        /// <summary>Wipers, steering and calipers are not posed beyond this distance from the camera.</summary>
        private const float PoseRange = 80f;
        private const float RainPollSeconds = 1f;
        private const float OnAboveKmh = 2f;

        /// <summary>Debug: forces rain on (true) or off (false) for every Yesla; null reads the weather.</summary>
        internal static bool? RainOverride;

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
            internal bool LoggedTail;
            internal Transform Steering, WiperL, WiperR, Bonnet, Boot;
            // Turning direction per part, worked out from its shape: a lid and a wiper turn the way that lifts them,
            // the steering wheel the way that moves its top to the right for a right turn.
            internal float SteerDir = 1f, WipeLDir = 1f, WipeRDir = 1f, BonnetDir = 1f, BootDir = 1f;
            internal readonly List<Caliper> Calipers = new List<Caliper>();
            internal float BonnetT, BootT;
            internal float WipePhase;
            internal bool Wiping;
            internal float Rain;
            internal float NextRain;
            internal bool LoggedBonnet, LoggedBoot;
        }

        /// <summary>Lid state from the phone and which end the storage was opened at, by car net id.</summary>
        private sealed class Lids
        {
            internal bool Frunk, Trunk;
            internal bool StorageFront;
        }

        private static readonly Dictionary<IntPtr, Car> Cars = new Dictionary<IntPtr, Car>();
        private static readonly Dictionary<int, Lids> LidState = new Dictionary<int, Lids>();
        private static readonly List<LandVehicle> Yeslas = new List<LandVehicle>();
        private static float _nextList;
        private static bool _rainBroken;

        // ---- network state -----------------------------------------------------------------------------------------

        /// <summary>Every peer: the host says a lid was opened or closed from the phone.</summary>
        internal static void SetLid(int carId, string which, bool open)
        {
            Lids l = LidsOf(carId);
            if (which == "frunk") l.Frunk = open;
            else if (which == "trunk") l.Trunk = open;
            else { Core.Log.Warning("[Parts] unknown lid '" + which + "' for car #" + carId + "."); return; }
            Core.Log.Msg("[Parts] car #" + carId + ": " + which + (open ? " opened" : " closed") + " from the phone.");
        }

        /// <summary>Every peer: the host says which end of the car its storage was opened at.</summary>
        internal static void SetStorageSide(int carId, bool front)
        {
            LidsOf(carId).StorageFront = front;
            Core.Log.Msg("[Parts] car #" + carId + ": storage opened at the " + (front ? "frunk" : "trunk") + ".");
        }

        /// <summary>Whether the phone holds this lid open, for the app's tiles.</summary>
        internal static bool LidOpen(LandVehicle car, string which)
        {
            if (car == null || !LidState.TryGetValue(Fleet.NetId(car), out Lids l)) return false;
            return which == "frunk" ? l.Frunk : l.Trunk;
        }

        private static Lids LidsOf(int carId)
        {
            if (!LidState.TryGetValue(carId, out Lids l)) LidState[carId] = l = new Lids();
            return l;
        }

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
            float dt = Time.deltaTime;

            for (int i = 0; i < Yeslas.Count; i++)
            {
                LandVehicle v = Yeslas[i];
                if (v == null) continue;
                Car c = For(v);
                if (c == null) continue;
                MoveLids(c, dt);
                Tail(c);
                if (cam != null && (v.transform.position - eye).sqrMagnitude > PoseRange * PoseRange) continue;
                Steer(c);
                Wipe(c, now, dt);
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
            Transform model = Visual.VizScene.FindDeep(v.transform, "YeslaModel");
            if (model == null) { Core.Log.Warning("[Parts] " + v.VehicleName + " has no Yesla model - nothing moves on it."); Cars[v.Pointer] = null; return null; }
            c.Steering = model.Find(PivotPrefix + "Steering");
            c.WiperL = model.Find(PivotPrefix + "Wiper_L");
            c.WiperR = model.Find(PivotPrefix + "Wiper_R");
            c.Bonnet = model.Find(PivotPrefix + "Bonnet");
            c.Boot = model.Find(PivotPrefix + "Boot");
            foreach (Wheel w in v.GetComponentsInChildren<Wheel>(true))
            {
                Transform t = w.transform.Find(CaliperName);
                if (t != null) c.Calipers.Add(new Caliper { T = t, W = w, Front = w.transform.localPosition.z > 0f });
            }
            c.SteerDir = TopRight(c.Steering, "Steering");
            c.WipeLDir = Lifts(c.WiperL, "Wiper_L");
            c.WipeRDir = Lifts(c.WiperR, "Wiper_R");
            c.BonnetDir = Lifts(c.Bonnet, "Bonnet");
            c.BootDir = Lifts(c.Boot, "Boot");
            var missing = new List<string>();
            if (c.Steering == null) missing.Add("steering wheel");
            if (c.WiperL == null || c.WiperR == null) missing.Add("wipers");
            if (c.Bonnet == null) missing.Add("bonnet");
            if (c.Boot == null) missing.Add("boot lid");
            if (c.Calipers.Count == 0) missing.Add("calipers");
            if (missing.Count > 0) Core.Log.Warning("[Parts] " + v.VehicleName + " #" + Fleet.NetId(v) + " has no " + string.Join(", ", missing) + " - those stay still.");
            else Core.Log.Msg("[Parts] " + v.VehicleName + " #" + Fleet.NetId(v) + ": steering wheel, wipers, both lids and " + c.Calipers.Count + " calipers found.");
            Cars[v.Pointer] = c;
            return c;
        }

        private static void Steer(Car c)
        {
            float steer = 0f;
            foreach (Caliper k in c.Calipers)
            {
                if (k.W == null || k.W.wheelCollider == null || k.W.wheelModel == null) continue;
                float a = k.W.wheelCollider.steerAngle;
                if (k.Front && Mathf.Abs(a) > Mathf.Abs(steer)) steer = a;
                // The caliper sits at the wheel centre and turns with the steering, but not with the spin.
                k.T.position = k.W.wheelModel.position;
                k.T.rotation = c.V.transform.rotation * Quaternion.Euler(0f, a, 0f);
            }
            if (c.Steering != null && Axes.TryGetValue("Steering", out Vector3 axis))
                c.Steering.localRotation = Quaternion.AngleAxis(Mathf.Clamp(steer * SteerRatio, -SteerMax, SteerMax) * SteerSign * c.SteerDir, axis);
        }

        private static void Wipe(Car c, float now, float dt)
        {
            if (c.WiperL == null || c.WiperR == null) return;
            if (now >= c.NextRain)
            {
                c.NextRain = now + RainPollSeconds;
                float rain = RainAt(c.V);
                bool on = c.V.IsOccupied || Mathf.Abs(c.V.Speed_Kmh) > OnAboveKmh;
                bool raining = rain > 0f && !c.V.IsUnderCover;
                bool wiping = raining && on;
                if (wiping != c.Wiping)
                    Core.Log.Msg("[Parts] " + c.V.VehicleName + " #" + Fleet.NetId(c.V) + " wipers " + (wiping ? "on (rain " + rain.ToString("F2") + ")" : "off (" + (!raining ? (c.V.IsUnderCover ? "under cover" : "no rain") : "car is off") + ")") + ".");
                c.Wiping = wiping;
                c.Rain = rain;
            }
            // A stroke in progress finishes, so the wipers always come to rest at the bottom.
            if (!c.Wiping && c.WipePhase == 0f) return;
            float period = Mathf.Lerp(WipeSlow, WipeFast, Mathf.Clamp01(c.Rain));
            c.WipePhase += dt / period;
            if (c.WipePhase >= 1f) c.WipePhase = c.Wiping ? c.WipePhase - 1f : 0f;
            float angle = WipeSweep * (0.5f - 0.5f * Mathf.Cos(c.WipePhase * 2f * Mathf.PI)) * WipeSign;
            if (Axes.TryGetValue("Wiper_L", out Vector3 al)) c.WiperL.localRotation = Quaternion.AngleAxis(angle * c.WipeLDir, al);
            if (Axes.TryGetValue("Wiper_R", out Vector3 ar)) c.WiperR.localRotation = Quaternion.AngleAxis(angle * c.WipeRDir, ar);
        }

        private static float RainAt(LandVehicle v)
        {
            if (RainOverride.HasValue) return RainOverride.Value ? 1f : 0f;
            if (_rainBroken) return 0f;
            try
            {
                if (!NetworkSingleton<EnvironmentManager>.InstanceExists) return 0f;
                EnvironmentManager env = NetworkSingleton<EnvironmentManager>.Instance;
                if (!env.HasActiveWeatherVolumes()) return 0f;
                var profile = env.GetWeatherProfileFromPosition(v.transform.position);
                return profile != null && profile.Conditions != null ? profile.Conditions.Rainy : 0f;
            }
            catch (Exception e)
            {
                _rainBroken = true;
                Core.Log.Error("[Parts] reading the weather failed, the wipers stay still from now on: " + e.Message);
                return 0f;
            }
        }

        private static void MoveLids(Car c, float dt)
        {
            LidState.TryGetValue(Fleet.NetId(c.V), out Lids l);
            bool storage = c.V.Storage != null && c.V.Storage.IsOpened;
            bool front = l != null && l.StorageFront;
            bool bonnet = (l != null && l.Frunk) || (storage && front);
            bool boot = (l != null && l.Trunk) || (storage && !front);
            float step = dt / Mathf.Max(0.05f, LidSeconds);
            c.BonnetT = Mathf.MoveTowards(c.BonnetT, bonnet ? 1f : 0f, step);
            c.BootT = Mathf.MoveTowards(c.BootT, boot ? 1f : 0f, step);
            if (bonnet != c.LoggedBonnet) { c.LoggedBonnet = bonnet; Core.Log.Msg("[Parts] " + c.V.VehicleName + " #" + Fleet.NetId(c.V) + " bonnet " + (bonnet ? "opens" : "closes") + "."); }
            if (boot != c.LoggedBoot) { c.LoggedBoot = boot; Core.Log.Msg("[Parts] " + c.V.VehicleName + " #" + Fleet.NetId(c.V) + " boot " + (boot ? "opens" : "closes") + "."); }
            Pose(c.Bonnet, "Bonnet", BonnetOpen * Ease(c.BonnetT) * c.BonnetDir);
            Pose(c.Boot, "Boot", BootOpen * Ease(c.BootT) * c.BootDir);
        }

        /// <summary>The centre of a part's meshes in its pivot's space; the meshes sit at the pivot with no offset.</summary>
        private static bool Centre(Transform pivot, out Vector3 centre)
        {
            centre = Vector3.zero;
            if (pivot == null) return false;
            int n = 0;
            foreach (MeshFilter mf in pivot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                centre += mf.sharedMesh.bounds.center;
                n++;
            }
            if (n == 0) return false;
            centre /= n;
            return true;
        }

        /// <summary>+1 when a positive turn about the part's axis lifts its centre, -1 when it lowers it.</summary>
        private static float Lifts(Transform pivot, string part)
        {
            if (!Centre(pivot, out Vector3 c) || !Axes.TryGetValue(part, out Vector3 axis)) return 1f;
            return (Quaternion.AngleAxis(10f, axis) * c).y >= c.y ? 1f : -1f;
        }

        /// <summary>+1 when a positive turn about the column moves the top of the wheel to the right (+x).</summary>
        private static float TopRight(Transform pivot, string part)
        {
            if (pivot == null || !Axes.TryGetValue(part, out Vector3 axis)) return 1f;
            Vector3 top = Vector3.ProjectOnPlane(Vector3.up, axis).normalized * 0.18f;
            return (Quaternion.AngleAxis(10f, axis) * top).x >= top.x ? 1f : -1f;
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

        private static float Ease(float t) => t * t * (3f - 2f * t);

        private static void Pose(Transform pivot, string part, float degrees)
        {
            if (pivot == null || !Axes.TryGetValue(part, out Vector3 axis)) return;
            pivot.localRotation = Quaternion.AngleAxis(degrees * LidSign, axis);
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
            string Rot(Transform t) => t == null ? "-" : t.localEulerAngles.ToString("F0");
            sb.Append("wheel ").Append(Rot(c.Steering)).Append(" dir ").Append(c.SteerDir)
              .Append(" | wipers ").Append(c.Wiping ? "on" : "off").Append(" L ").Append(Rot(c.WiperL)).Append(" R ").Append(Rot(c.WiperR))
              .Append(" | bonnet ").Append(c.BonnetT.ToString("F2")).Append(' ').Append(Rot(c.Bonnet))
              .Append(" boot ").Append(c.BootT.ToString("F2")).Append(' ').Append(Rot(c.Boot))
              .Append(" | tail ").Append(c.LoggedTail ? "on" : "off");
            return sb.ToString();
        }

        internal static void Reset()
        {
            Cars.Clear();
            LidState.Clear();
            Yeslas.Clear();
            _nextList = 0f;
            _rainBroken = false;
            RainOverride = null;
        }
    }
}
