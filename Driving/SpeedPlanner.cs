using System.Collections.Generic;
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
        private const float ProbeRadius = 0.8f;
        /// <summary>Gap kept to a stop line.</summary>
        private const float StopLineGap = 1.5f;

        internal const float MinKmh = 8f;

        private static readonly List<Vector3> Samples = new List<Vector3>(128);

        internal struct Plan
        {
            internal float Kmh;
            internal string Why;
        }

        internal static Plan Run(LandVehicle car, VehicleAgent agent, Il2CppSystem.Collections.Generic.List<Vector3> pts,
                                 int seg, float lerp, DriveProfile p)
        {
            float v = Mathf.Abs(car.Speed_Kmh) / 3.6f;
            float a = p.BrakeDecel;
            float reaction = v * ReactionSeconds;
            float horizon = Mathf.Clamp(v * v / (2f * a) + reaction + 40f, MinHorizon, MaxHorizon);

            bool endReached = Resample(pts, seg, lerp, horizon, out float toEnd);

            float best = p.NoLimit ? p.MaxKmh / 3.6f : float.MaxValue;
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
                if (allowed < best) { best = allowed; why = "bend r=" + radius.ToString("F0") + " m in " + (k * Step).ToString("F0") + " m"; }
            }

            // The end of the path.
            if (endReached)
            {
                float allowed = Reach(EndSpeed, toEnd - ArriveWithin - reaction, a);
                if (allowed < best) { best = allowed; why = "destination in " + toEnd.ToString("F0") + " m"; }
            }

            // A lower speed zone ahead.
            if (!p.NoLimit)
            {
                for (int k = 4; k < Samples.Count; k += 4)
                {
                    float vz = ZoneKmh(Samples[k]) * p.LimitFactor / 3.6f;
                    float allowed = Reach(vz, k * Step - reaction, a);
                    if (allowed < best) { best = allowed; why = "zone " + (vz * 3.6f).ToString("F0") + " km/h in " + (k * Step).ToString("F0") + " m"; }
                }
            }

            // Something in the lane.
            float scan = v * v / (2f * a) + reaction + p.StopGap + 12f;
            if (Obstacle(car, p, scan, out float at, out float gap, out float otherSpeed, out string what))
            {
                float allowed = Reach(otherSpeed, at - gap - reaction, a);
                if (allowed < best) { best = allowed; why = what + " in " + at.ToString("F0") + " m"; }
            }

            return new Plan { Kmh = Mathf.Max(MinKmh, best * 3.6f), Why = why };
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
        private static bool Obstacle(LandVehicle car, DriveProfile p, float range, out float at, out float gap, out float otherSpeed, out string what)
        {
            at = float.MaxValue; gap = 0f; otherSpeed = 0f; what = string.Empty;
            VehicleAgent agent = car.Agent;
            int mask = agent != null && agent.sensor_FM != null ? (int)agent.sensor_FM.checkMask : Physics.DefaultRaycastLayers;
            Transform self = car.transform;
            Transform seated = car.HumanoidColliderContainer != null ? car.HumanoidColliderContainer.transform : null;
            Vector3 fwd = self.forward;

            for (int k = 0; k + 2 < Samples.Count && k * Step < range; k += 2)
            {
                Vector3 from = Samples[k] + Vector3.up * ProbeHeight;
                Vector3 to = Samples[k + 2] + Vector3.up * ProbeHeight;
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
                    Rigidbody rb = c.attachedRigidbody;
                    otherSpeed = rb != null && !rb.isKinematic ? Mathf.Max(0f, Vector3.Dot(rb.velocity, fwd)) : 0f;
                    what = (obstacle != null ? (obstacle.type == VehicleObstacle.EObstacleType.TrafficLight ? "red light" : "obstacle " + obstacle.name) : c.name);
                }
                if (at < float.MaxValue) return true;
            }
            return false;
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
