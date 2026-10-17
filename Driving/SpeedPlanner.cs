using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;

namespace Yesla.Driving
{
    /// <summary>
    /// The speed a followed car may drive right now, planned along the path ahead.
    ///
    /// Every point ahead gets a highest speed: a bend from its radius (v = sqrt(a_lat * r)), the end of the path, a
    /// lower speed zone, or something standing in the lane. The car may drive as fast as it can still brake from to
    /// each of those in time, with the profile's deceleration: v_now = min over k of sqrt(v_k² + 2 * a_brake * d_k).
    /// That is the backward pass of the speed profiles used in autonomous racing, evaluated at the car only; the
    /// forward pass is the throttle itself.
    /// </summary>
    internal static class SpeedPlanner
    {
        /// <summary>Spacing of the resampled path in metres.</summary>
        private const float Step = 2f;
        /// <summary>Samples on each side of a point used for its bend radius (2 x 2 m chords).</summary>
        private const int CurveWindow = 2;
        private const float MinHorizon = 30f;
        private const float MaxHorizon = 180f;
        /// <summary>Time the throttle needs to turn into braking; the distance covered in it does not count.</summary>
        private const float ReactionSeconds = 0.35f;
        /// <summary>Speed in m/s the car rolls into the last metres of the path with; vanilla brakes it to a stop there.</summary>
        private const float EndSpeed = 2.5f;
        /// <summary>Distance before the path end at which vanilla reports arrival.</summary>
        private const float ArriveWithin = 3f;
        private const float ProbeHeight = 1.0f;
        /// <summary>Half the car's width plus a hand's breadth: a lamp post at the lane edge is in the way.</summary>
        private const float ProbeRadius = 0.95f;
        /// <summary>The swept line starts at the car and joins the path over this many metres, so a car that is not
        /// on the path yet (pulling out from the kerb) sweeps where it will really go.</summary>
        private const float JoinMetres = 8f;
        /// <summary>Cross traffic: another car predicted this close to a point of the path when we get there.</summary>
        private const float CrossGap = 2.4f;
        private const float CrossSeconds = 3f;
        private const float CrossRange = 50f;
        /// <summary>Gap kept to a stop line.</summary>
        private const float StopLineGap = 1.5f;
        /// <summary>Gap between the front bumper and a pedestrian crossing the car waits before.</summary>
        private const float CrosswalkGap = 0.6f;
        /// <summary>Below this speed in m/s the thing ahead counts as standing, and the car waits for it.</summary>
        private const float StandingSpeed = 1f;

        internal const float MinKmh = 8f;
        /// <summary>
        /// Deceleration in m/s² of the car rolling with no throttle and no brake. The planner works out a second, lower
        /// speed from it: above that one the car lifts off and rolls, and only brakes once rolling would not be enough.
        /// </summary>
        internal const float CoastDecel = 1.0f;

        private static readonly List<Vector3> Samples = new List<Vector3>(128);
        /// <summary>Path distances where the car's path enters (x) and leaves (y) a pedestrian crossing.</summary>
        private static readonly List<Vector2> Walks = new List<Vector2>();

        internal struct Plan
        {
            internal float Kmh;
            /// <summary>Above this speed rolling, not braking, already reaches every limit ahead in time.</summary>
            internal float CoastKmh;
            internal string Why;
            /// <summary>The nearest thing in the lane, or null: its collider, distance along the path and speed in m/s.</summary>
            internal Collider Blocker;
            internal float BlockerAt, BlockerSpeed;
            internal bool BlockerIsStopLine;
        }

        /// <summary>The resampled path ahead from the last <see cref="Run"/>, every <see cref="Step"/> metres.</summary>
        internal static List<Vector3> Ahead => Samples;
        internal const float SampleStep = Step;

        /// <param name="shift">Metres to the left of the path the car is moving to (passing something standing in the
        /// lane); the lane is swept there instead of on the path.</param>
        internal static Plan Run(LandVehicle car, VehicleAgent agent, Il2CppSystem.Collections.Generic.List<Vector3> pts,
                                 int seg, float lerp, DriveProfile p, float shift = 0f)
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.SpeedPlanner.Run");
#endif
            float v = Mathf.Abs(car.Speed_Kmh) / 3.6f;
            float a = p.BrakeDecel;
            float reaction = v * ReactionSeconds;
            float horizon = Mathf.Clamp(v * v / (2f * a) + reaction + 40f, MinHorizon, MaxHorizon);

            bool endReached = Resample(pts, seg, lerp, horizon, out float toEnd);

            float best = p.NoLimit ? p.MaxKmh / 3.6f : float.MaxValue;
            float coast = best;
            string why = p.NoLimit ? "open road" : "limit";

            // Bends.
            for (int k = 1; k + 1 < Samples.Count; k++)
            {
                Vector3 a0 = Samples[Mathf.Max(0, k - CurveWindow)], a1 = Samples[k], a2 = Samples[Mathf.Min(Samples.Count - 1, k + CurveWindow)];
                Vector3 inDir = a1 - a0; inDir.y = 0f;
                Vector3 outDir = a2 - a1; outDir.y = 0f;
                if (inDir.sqrMagnitude < 0.01f || outDir.sqrMagnitude < 0.01f) continue;
                float angle = Vector3.Angle(inDir, outDir) * Mathf.Deg2Rad;
                if (angle < 0.02f) continue;
                float span = 0.5f * (inDir.magnitude + outDir.magnitude);
                float radius = span / angle;
                float vk = Mathf.Sqrt(p.LateralAccel * radius);
                float allowed = Reach(vk, k * Step - reaction, a);
                coast = Mathf.Min(coast, Reach(vk, k * Step - reaction, CoastDecel));
                if (allowed < best) { best = allowed; why = "bend r=" + radius.ToString("F0") + " m in " + (k * Step).ToString("F0") + " m"; }
            }

            // The end of the path.
            if (endReached)
            {
                float allowed = Reach(EndSpeed, toEnd - ArriveWithin - reaction, a);
                coast = Mathf.Min(coast, Reach(EndSpeed, toEnd - ArriveWithin - reaction, CoastDecel));
                if (allowed < best) { best = allowed; why = "destination in " + toEnd.ToString("F0") + " m"; }
            }

            // A lot spot to park in: the road AI hands over at walking pace, and the turn into the lot is taken at it.
            if (Autopilot.DrivenCar != null && Autopilot.DrivenCar.Pointer == car.Pointer && Autopilot.LotSpotAhead != null)
            {
                Vector3 toSpot = Autopilot.LotSpotAhead.Centre - car.transform.position; toSpot.y = 0f;
                float allowed = Reach(Parking.HandOverKmh / 3.6f, toSpot.magnitude - Parking.HandOver - reaction, a);
                coast = Mathf.Min(coast, Reach(Parking.HandOverKmh / 3.6f, toSpot.magnitude - Parking.HandOver - reaction, CoastDecel));
                if (allowed < best) { best = allowed; why = "parking in " + toSpot.magnitude.ToString("F0") + " m"; }
            }

            // A lower speed zone ahead.
            if (!p.NoLimit)
            {
                for (int k = 4; k < Samples.Count; k += 4)
                {
                    float vz = ZoneKmh(Samples[k]) * p.LimitFactor / 3.6f;
                    float allowed = Reach(vz, k * Step - reaction, a);
                    coast = Mathf.Min(coast, Reach(vz, k * Step - reaction, CoastDecel));
                    if (allowed < best) { best = allowed; why = "zone " + (vz * 3.6f).ToString("F0") + " km/h in " + (k * Step).ToString("F0") + " m"; }
                }
            }

            // Something in the lane.
            float scan = v * v / (2f * a) + reaction + p.StopGap + 12f;
            FindWalks(scan + 10f);
            float halfLength = car.BoundingBoxDimensions.z * 0.5f;
            Plan result = default;
            if (Obstacle(car, p, scan, shift, out float at, out float gap, out float otherSpeed, out string what, out Collider blocker, out bool stopLine))
            {
                result.Blocker = blocker;
                result.BlockerAt = at;
                result.BlockerSpeed = otherSpeed;
                result.BlockerIsStopLine = stopLine;
                float stop = at - gap;
                if (otherSpeed < StandingSpeed && OffWalk(ref stop, halfLength, v, a, reaction)) what += ", waiting before the crossing";
                float allowed = Reach(otherSpeed, stop - reaction, a);
                coast = Mathf.Min(coast, Reach(otherSpeed, stop - reaction, CoastDecel));
                if (allowed < best) { best = allowed; why = what + " in " + at.ToString("F0") + " m"; }
            }

            // A car that will cross the path.
            if (Crossing(car, v, scan, out float cAt, out string cWhat))
            {
                float stop = cAt - p.StopGap;
                if (OffWalk(ref stop, halfLength, v, a, reaction)) cWhat += ", waiting before the crossing";
                float allowed = Reach(0f, stop - reaction, a);
                coast = Mathf.Min(coast, Reach(0f, stop - reaction, CoastDecel));
                if (allowed < best) { best = allowed; why = cWhat + " in " + cAt.ToString("F0") + " m"; }
            }

            result.Kmh = Mathf.Max(MinKmh, best * 3.6f);
            result.CoastKmh = Mathf.Max(MinKmh, coast * 3.6f);
            result.Why = why;
            return result;
        }

        /// <summary>Fastest speed from which <paramref name="target"/> is still reached within <paramref name="distance"/>.</summary>
        private static float Reach(float target, float distance, float decel) =>
            Mathf.Sqrt(target * target + 2f * decel * Mathf.Max(0f, distance));

        /// <summary>The path from the car's point on it, every <see cref="Step"/> metres up to <paramref name="horizon"/>.
        /// Returns whether the path ends inside the horizon, with the distance to that end.</summary>
        private static bool Resample(Il2CppSystem.Collections.Generic.List<Vector3> pts, int seg, float lerp, float horizon, out float toEnd)
        {
            Samples.Clear();
            int count = pts.Count;
            Vector3 prev = Vector3.Lerp(pts[seg], pts[seg + 1], lerp);
            Samples.Add(prev);
            float total = 0f, carry = 0f;
            int i = seg + 1;
            while (i < count && total < horizon)
            {
                Vector3 next = pts[i];
                float len = Vector3.Distance(prev, next);
                if (carry + len >= Step && len > 1e-4f)
                {
                    prev = Vector3.Lerp(prev, next, (Step - carry) / len);
                    Samples.Add(prev);
                    total += Step;
                    carry = 0f;
                }
                else
                {
                    carry += len;
                    prev = next;
                    i++;
                }
            }
            toEnd = total + carry;
            return i >= count;
        }

        /// <summary>
        /// The nearest thing standing in the lane within <paramref name="range"/> metres along the path: a car, a
        /// person, a bin, or a red light's stop line. Swept with spheres at bumper height from sample to sample, so it
        /// follows bends, which the 3-12 m sensors and the 15 m wheel sweep of the road AI do not reach at speed.
        /// </summary>
        private static bool Obstacle(LandVehicle car, DriveProfile p, float range, float shift, out float at, out float gap, out float otherSpeed, out string what,
                                     out Collider blocker, out bool stopLine)
        {
            at = float.MaxValue; gap = 0f; otherSpeed = 0f; what = string.Empty; blocker = null; stopLine = false;
            VehicleAgent agent = car.Agent;
            int mask = agent != null && agent.sensor_FM != null ? (int)agent.sensor_FM.checkMask : Physics.DefaultRaycastLayers;
            Transform self = car.transform;
            Transform seated = car.HumanoidColliderContainer != null ? car.HumanoidColliderContainer.transform : null;
            Vector3 fwd = self.forward;
            Vector3 off = self.position - Samples[0];
            off.y = 0f;

            for (int k = 0; k + 2 < Samples.Count && k * Step < range; k += 2)
            {
                Vector3 from = Samples[k] + Join(off, k, Left(k) * shift) + Vector3.up * ProbeHeight;
                Vector3 to = Samples[k + 2] + Join(off, k + 2, Left(k + 2) * shift) + Vector3.up * ProbeHeight;
                Vector3 dir = to - from;
                float len = dir.magnitude;
                if (len < 0.01f) continue;
                var hits = Physics.SphereCastAll(from, ProbeRadius, dir / len, len, mask, QueryTriggerInteraction.Collide);
                for (int h = 0; h < hits.Length; h++)
                {
                    RaycastHit hit = hits[h];
                    Collider c = hit.collider;
                    if (c == null) continue;
                    Transform t = c.transform;
                    if (t.IsChildOf(self) || (seated != null && t.IsChildOf(seated))) continue;
                    bool overlapping = hit.distance <= 0f && hit.point == Vector3.zero;

                    float hitGap = p.StopGap;
                    VehicleObstacle obstacle = c.GetComponentInParent<VehicleObstacle>();
                    if (obstacle != null)
                    {
                        if (obstacle.type == VehicleObstacle.EObstacleType.TrafficLight && p.IgnoreTrafficLights) continue;
                        if (!obstacle.twoSided && Vector3.Angle(-obstacle.transform.forward, fwd) > 90f) continue;
                        hitGap = StopLineGap;
                    }
                    else
                    {
                        if (c.isTrigger) continue;
                        Rigidbody body = c.attachedRigidbody;
                        // The sphere already inside static ground or a kerb at the first sample, or grazing the
                        // road surface, is not something in the lane.
                        if (body == null && (overlapping || hit.normal.y > 0.7f)) continue;
                    }

                    float d = k * Step + (overlapping ? 0f : hit.distance);
                    if (d >= at) continue;
                    at = d;
                    gap = hitGap;
                    blocker = c;
                    stopLine = obstacle != null;
                    Rigidbody rb = c.attachedRigidbody;
                    otherSpeed = rb != null && !rb.isKinematic ? Mathf.Max(0f, Vector3.Dot(rb.velocity, fwd)) : 0f;
                    what = (obstacle != null ? (obstacle.type == VehicleObstacle.EObstacleType.TrafficLight ? "red light" : "obstacle " + obstacle.name) : c.name);
                }
                if (at < float.MaxValue) return true;
            }
            return false;
        }

        /// <summary>Where the path ahead runs over a pedestrian crossing, within <paramref name="range"/> metres.</summary>
        private static void FindWalks(float range)
        {
            Walks.Clear();
            List<Crosswalks.Walk> all = Crosswalks.All;
            if (all.Count == 0 || Samples.Count < 2) return;
            Vector3 here = Samples[0];
            for (int w = 0; w < all.Count; w++)
            {
                Crosswalks.Walk walk = all[w];
                Vector3 d = walk.Centre - here; d.y = 0f;
                if (d.magnitude > range + walk.HalfAcross) continue;
                float enter = -1f, leave = -1f;
                for (int k = 0; k + 1 < Samples.Count && k * Step < range; k++)
                {
                    if (!Crosswalks.Clip(walk, Samples[k], Samples[k + 1], out float e, out float l)) continue;
                    if (enter < 0f) enter = (k + e) * Step;
                    leave = (k + l) * Step;
                }
                if (enter >= 0f) Walks.Add(new Vector2(enter, leave));
            }
        }

        /// <summary>
        /// Pulls a stop back to before a pedestrian crossing the car would otherwise stand on with any part of it.
        /// Only when the car can still stop there without braking harder than planned; a car already on the
        /// crossing keeps its stop. <paramref name="stop"/> is the distance the car's centre still travels.
        /// </summary>
        private static bool OffWalk(ref float stop, float halfLength, float v, float a, float reaction)
        {
            for (int i = 0; i < Walks.Count; i++)
            {
                Vector2 w = Walks[i];
                if (stop + halfLength <= w.x - CrosswalkGap || stop - halfLength >= w.y) continue;
                float before = w.x - CrosswalkGap - halfLength;
                if (before < v * v / (2f * a) + reaction) return false;
                stop = before;
                return true;
            }
            return false;
        }

        /// <summary>The car's offset from the path, turning into <paramref name="settle"/> over <see cref="JoinMetres"/>.</summary>
        private static Vector3 Join(Vector3 off, int k, Vector3 settle)
        {
            float t = Mathf.Min(1f, k * Step / JoinMetres);
            return off * (1f - t) + settle * t;
        }

        /// <summary>The unit vector to the left of the path at sample <paramref name="k"/>.</summary>
        internal static Vector3 Left(int k)
        {
            int a = Mathf.Clamp(k, 0, Samples.Count - 2);
            Vector3 d = Samples[a + 1] - Samples[a]; d.y = 0f;
            if (d.sqrMagnitude < 1e-6f) return Vector3.zero;
            d.Normalize();
            return new Vector3(-d.z, 0f, d.x);
        }

        /// <summary>
        /// The nearest point of the path that another moving car is predicted to reach about when this one does,
        /// assuming both keep their speed. Catches cross traffic at a junction and oncoming cars across a left turn,
        /// which the sweep only sees once they are already in the lane. Cars going the same way are left to the sweep.
        /// </summary>
        private static bool Crossing(LandVehicle car, float v, float range, out float at, out string what)
        {
            at = float.MaxValue; what = string.Empty;
            var all = NetworkSingleton<VehicleManager>.InstanceExists ? NetworkSingleton<VehicleManager>.Instance.AllVehicles : null;
            if (all == null || Samples.Count < 3) return false;
            Vector3 self = car.transform.position;
            Vector3 fwd = car.transform.forward; fwd.y = 0f; fwd.Normalize();
            float pace = Mathf.Max(v, 3f);
            float gap2 = CrossGap * CrossGap;
            for (int i = 0; i < all.Count; i++)
            {
                LandVehicle other = all[i];
                if (other == null || other.Pointer == car.Pointer || other.Rb == null) continue;
                Vector3 pos = other.transform.position;
                if ((pos - self).sqrMagnitude > CrossRange * CrossRange) continue;
                Vector3 vel = other.Rb.velocity; vel.y = 0f;
                float speed = vel.magnitude;
                if (speed < 2f || Vector3.Dot(vel / speed, fwd) > 0.7f) continue;
                for (int k = 2; k < Samples.Count && k * Step < range; k += 2)
                {
                    float t = k * Step / pace;
                    if (t > CrossSeconds) break;
                    Vector3 d = pos + vel * t - Samples[k];
                    d.y = 0f;
                    if (d.sqrMagnitude > gap2) continue;
                    if (k * Step < at) { at = k * Step; what = "crossing " + other.VehicleName; }
                    break;
                }
            }
            return at < float.MaxValue;
        }

        /// <summary>The speed limit at <paramref name="at"/> in km/h: the speed zone's, or the unmarked-road speed.</summary>
        internal static float ZoneKmh(Vector3 at)
        {
            var zones = SpeedZone.speedZones;
            for (int i = 0; zones != null && i < zones.Count; i++)
            {
                SpeedZone z = zones[i];
                if (z != null && z.col != null && z.col.bounds.Contains(at)) return z.speed;
            }
            return VehicleAgent.UnmarkedSpeed;
        }
    }
}
