using Il2CppScheduleOne.Vehicles;
using UnityEngine;

namespace Yesla.Driving
{
    /// <summary>
    /// What the car's own body would touch driving on with its current steering: a lamp post, a hydrant or a
    /// bollard beside a tight bend. The swept path line and vanilla's sensors look ahead of the car's centre, but in a
    /// bend the front corner swings out and the inner side cuts in, and that is where these stand.
    ///
    /// The car turns about its rear axle on a circle of radius wheelbase / tan(steer). Spheres sit at the leading
    /// bumper (both corners and the middle) and at the trailing corners, a hand's breadth outside the body, from
    /// 0.3 m above the ground (a kerb is driven over) to 1 m (a hydrant's top), and are swept along that circle.
    /// </summary>
    internal static class Footprint
    {
        private const float Wheelbase = 2.9f;
        private const float Radius = 0.35f;
        private const float Height = 0.65f;
        /// <summary>How far outside the body the spheres reach.</summary>
        private const float Margin = 0.1f;
        private const float Step = 1f;
        /// <summary>
        /// How far the road may rise over one step. A ramp onto a bridge rises well under this; a hydrant's top or a
        /// kerbside box rises more and stays an obstacle instead of becoming the road.
        /// </summary>
        private const float MaxRise = 0.35f;

        /// <summary>
        /// The first thing the car touches within <paramref name="reach"/> metres, driving forwards
        /// (<paramref name="dir"/> +1) or backwards (-1) on its current steering. <paramref name="at"/> is the
        /// distance the car still travels, <paramref name="side"/> -1 left, 0 the middle of the bumper, +1 right.
        /// </summary>
        internal static bool Sweep(LandVehicle car, float dir, float reach, out float at, out int side, out Collider hit, out string what)
        {
            at = float.MaxValue; side = 0; hit = null; what = string.Empty;
            Transform self = car.transform;
            Transform seated = car.HumanoidColliderContainer != null ? car.HumanoidColliderContainer.transform : null;
            Vector3 fwd = Flat(self.forward).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 dims = car.BoundingBoxDimensions;
            Vector3 pos = self.position;
            Vector3 rear = pos - fwd * (Wheelbase * 0.5f);
            float carGround = Parking.Ground(car, pos);
            rear.y = carGround;

            float lead = dir > 0f ? Wheelbase * 0.5f + dims.z * 0.5f - Radius : Wheelbase * 0.5f - dims.z * 0.5f + Radius;
            float trail = dir > 0f ? Wheelbase * 0.5f - dims.z * 0.5f + Radius : Wheelbase * 0.5f + dims.z * 0.5f - Radius;
            float lat = dims.x * 0.5f + Margin - Radius;
            // Along the car from the rear axle, across it, and whether it leads: a body already touching something
            // beside a trailing corner is not news, a leading one is.
            Vector3[] spots =
            {
                new Vector3(lead, -lat, 1f), new Vector3(lead, 0f, 1f), new Vector3(lead, lat, 1f),
                new Vector3(trail, -lat, 0f), new Vector3(trail, lat, 0f),
            };

            float steer = car.CurrentSteerAngle * Mathf.Deg2Rad;
            float turn = Mathf.Abs(steer) < 0.01f ? 0f : Mathf.Tan(steer) / Wheelbase;
            int steps = Mathf.Max(1, Mathf.CeilToInt(reach / Step));
            // Each sphere rides the road under it, so a ramp up to a bridge is driven onto rather than into.
            var ground = new float[spots.Length];
            // Where the car stands now its corners sit on the road it is pitched along: on a ramp the front corners
            // are already higher than the ground under the car's middle.
            Vector3 f3 = self.forward;
            float slope = f3.y / Mathf.Max(0.1f, new Vector2(f3.x, f3.z).magnitude);
            for (int i = 0; i < spots.Length; i++)
                ground[i] = RoadUnder(car, Place(rear, fwd, right, turn, 0f, spots[i]),
                                      carGround + slope * (spots[i].x - Wheelbase * 0.5f));
            for (int k = 0; k < steps; k++)
            {
                float s0 = dir * reach * k / steps, s1 = dir * reach * (k + 1) / steps;
                for (int i = 0; i < spots.Length; i++)
                {
                    Vector3 from = Place(rear, fwd, right, turn, s0, spots[i]);
                    Vector3 to = Place(rear, fwd, right, turn, s1, spots[i]);
                    float next = RoadUnder(car, to, ground[i]);
                    from.y = ground[i] + Height;
                    to.y = next + Height;
                    ground[i] = next;
                    Vector3 d = to - from;
                    float len = d.magnitude;
                    if (len < 1e-3f) continue;
                    var hits = Physics.SphereCastAll(from, Radius, d / len, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    for (int h = 0; h < hits.Length; h++)
                    {
                        RaycastHit rh = hits[h];
                        Collider c = rh.collider;
                        if (c == null) continue;
                        Transform t = c.transform;
                        if (t.IsChildOf(self) || (seated != null && t.IsChildOf(seated)) || Aboard(c, car)) continue;
                        bool inside = rh.distance <= 0f && rh.point == Vector3.zero;
                        if (inside && (k > 0 || spots[i].z < 0.5f)) continue;
                        // The road rising ahead, or the top of a kerb, faces up.
                        if (!inside && c.attachedRigidbody == null && rh.normal.y > 0.7f) continue;
                        float d0 = Mathf.Abs(s0) + (inside ? 0f : rh.distance * Mathf.Abs(s1 - s0) / len);
                        if (d0 >= at) continue;
                        at = d0;
                        side = spots[i].y < -0.01f ? -1 : spots[i].y > 0.01f ? 1 : 0;
                        hit = c;
                        Transform up = t.parent;
                        what = (up != null ? up.name + "/" : "") + c.name;
                    }
                }
                if (hit != null) return true;
            }
            return false;
        }

        /// <summary>
        /// A point fixed to the car (<paramref name="spot"/>: x along it from the rear axle, y across) after the rear
        /// axle travelled <paramref name="s"/> metres on the circle of curvature <paramref name="turn"/>.
        /// </summary>
        private static Vector3 Place(Vector3 rear, Vector3 fwd, Vector3 right, float turn, float s, Vector3 spot)
        {
            Vector3 r, f;
            if (turn == 0f) { r = rear + fwd * s; f = fwd; }
            else
            {
                float th = s * turn;
                r = rear + (fwd * Mathf.Sin(th) + right * (1f - Mathf.Cos(th))) / turn;
                f = fwd * Mathf.Cos(th) + right * Mathf.Sin(th);
            }
            Vector3 across = Vector3.Cross(Vector3.up, f);
            return r + f * spot.x + across * spot.y;
        }

        /// <summary>
        /// The road surface under <paramref name="at"/>, given the road was at <paramref name="was"/> one step back:
        /// the highest upward-facing static surface no more than <see cref="MaxRise"/> above it. Nothing found keeps
        /// the height, as on a flat road.
        /// </summary>
        private static float RoadUnder(LandVehicle car, Vector3 at, float was)
        {
            float top = was + MaxRise;
            var hits = Physics.RaycastAll(new Vector3(at.x, top + 0.01f, at.z), Vector3.down, MaxRise + 3f,
                                          Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MinValue;
            for (int h = 0; h < hits.Length; h++)
            {
                RaycastHit rh = hits[h];
                Collider c = rh.collider;
                if (c == null || c.attachedRigidbody != null || rh.normal.y < 0.7f || c.transform.IsChildOf(car.transform)) continue;
                if (rh.point.y > best) best = rh.point.y;
            }
            return best > float.MinValue ? best : was;
        }

        /// <summary>
        /// A player sitting in this car: their capsule stays a world object beside the seat, not a child of the car.
        /// </summary>
        private static bool Aboard(Collider c, LandVehicle car)
        {
            var p = c.GetComponentInParent<Il2CppScheduleOne.PlayerScripts.Player>();
            if (p == null) return false;
            var v = p.CurrentVehicle;
            return v != null && v.gameObject == car.gameObject;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
