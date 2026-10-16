using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Math;

namespace Yesla.Driving
{
    /// <summary>
    /// The line a Yesla drives along: straight between the route points, with a circular arc only at each corner.
    ///
    /// Vanilla lays a cubic B-spline over the route points (PathSmoothingUtility.CalculateSmoothedPath). A B-spline
    /// does not pass through its points; at a junction it cuts the corner by several metres, and for a car on the
    /// right-hand lane the inside of a right turn is the pavement and the inside of a left turn the oncoming lane.
    /// The route points themselves sit on the lane, so for a car the follower drives the line keeps to them: straight
    /// segments stay on the lane, and a corner gets the arc of <see cref="TurnRadius"/> that is tangent to both
    /// segments, smaller where the segments are too short for it. Every other car keeps vanilla's spline.
    /// </summary>
    [HarmonyPatch(typeof(PathSmoothingUtility), nameof(PathSmoothingUtility.CalculateSmoothedPath))]
    internal static class LaneLine
    {
        /// <summary>Arc radius at a corner, in metres: a right turn into a two-lane street.</summary>
        private const float TurnRadius = 6f;
        /// <summary>An arc takes at most this share of either neighbouring segment.</summary>
        private const float MaxShare = 0.45f;
        /// <summary>Corners flatter than this, in degrees, stay corners: the follower's lookahead rounds them.</summary>
        private const float MinCornerDeg = 6f;
        /// <summary>Spacing of the points on a straight and on an arc, in metres.</summary>
        private const float StraightStep = 2f;
        private const float ArcStep = 0.75f;

        private static bool Prefix(Il2CppSystem.Collections.Generic.List<Vector3> controlPoints, ref PathSmoothingUtility.SmoothedPath __result)
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.LaneLine.Build");
#endif
            try
            {
                if (controlPoints == null || controlPoints.Count < 2) return true;
                Vector3 first = controlPoints[0], last = controlPoints[controlPoints.Count - 1];
                string who = PathFollower.WhoseRoute(first, last);
                if (who == null) return true;

                var raw = new List<Vector3>(controlPoints.Count);
                for (int i = 0; i < controlPoints.Count; i++)
                {
                    Vector3 p = controlPoints[i];
                    if (raw.Count == 0 || Flat(p - raw[raw.Count - 1]) >= 0.5f) raw.Add(p);
                }
                if (raw.Count < 2) return true;

                List<Vector3> line = Build(raw, out int arcs, out float tightest);
                KeepClear(line, out int moved, out float most);
                var path = new PathSmoothingUtility.SmoothedPath();
                foreach (Vector3 p in line) path.vectorPath.Add(p);
                __result = path;
                Core.Log.Msg("[LaneLine] " + who + " route: " + raw.Count + " route points -> " + line.Count + " line points, " + arcs
                           + " corners rounded" + (arcs > 0 ? ", tightest radius " + tightest.ToString("F1") + " m" : "")
                           + ", " + moved + " points moved off a kerb" + (moved > 0 ? " (up to " + most.ToString("F1") + " m)" : "") + ".");
#if DEBUG
                var sb = new System.Text.StringBuilder("[LaneLine] route points:");
                for (int i = 0; i < raw.Count; i++) sb.Append(' ').Append(i).Append('=').Append(raw[i].x.ToString("F1")).Append(',').Append(raw[i].z.ToString("F1"));
                Core.Log.Msg(sb.ToString());
#endif
                return false;
            }
            catch (Exception e)
            {
                Core.Log.Error("[LaneLine] building the lane line failed, vanilla's spline is used: " + e);
                return true;
            }
        }

        private static List<Vector3> Build(List<Vector3> raw, out int arcs, out float tightest)
        {
            arcs = 0;
            tightest = float.MaxValue;
            var line = new List<Vector3> { raw[0] };
            Vector3 from = raw[0];
            for (int i = 1; i < raw.Count - 1; i++)
            {
                Vector3 a = raw[i - 1], c = raw[i], b = raw[i + 1];
                Vector3 inDir = c - a; inDir.y = 0f;
                Vector3 outDir = b - c; outDir.y = 0f;
                float lenIn = inDir.magnitude, lenOut = outDir.magnitude;
                float turn = lenIn > 0.01f && lenOut > 0.01f ? Vector3.Angle(inDir, outDir) : 0f;
                if (turn < MinCornerDeg || turn > 175f)
                {
                    Straight(line, from, c);
                    from = c;
                    continue;
                }
                inDir /= lenIn; outDir /= lenOut;
                float half = Mathf.Tan(turn * 0.5f * Mathf.Deg2Rad);
                // Tangent length for the wanted radius, limited by the room on both sides of the corner.
                float tangent = Mathf.Min(TurnRadius * half, MaxShare * lenIn, MaxShare * lenOut);
                float radius = tangent / half;
                Vector3 t1 = c - inDir * tangent; t1.y = Mathf.Lerp(c.y, a.y, tangent / lenIn);
                Vector3 t2 = c + outDir * tangent; t2.y = Mathf.Lerp(c.y, b.y, tangent / lenOut);
                Straight(line, from, t1);
                Arc(line, t1, t2, c, turn, radius);
                from = t2;
                arcs++;
                tightest = Mathf.Min(tightest, radius);
            }
            Straight(line, from, raw[raw.Count - 1]);
            return line;
        }

        /// <summary>Wanted room from the line to a kerb or a post on either side, in metres: half the car plus a margin.</summary>
        private const float Clearance = 1.4f;
        /// <summary>The line moves sideways at most this far, in metres.</summary>
        private const float MaxShift = 1.2f;
        /// <summary>How far to each side the road is felt for a kerb, in metres, and in which steps.</summary>
        private const float Feel = 2.5f;
        private const float FeelStep = 0.25f;
        /// <summary>A step up of this much, in metres, is a kerb.</summary>
        private const float KerbHeight = 0.07f;

        /// <summary>
        /// Moves the line away from a kerb or a post that is closer than <see cref="Clearance"/>. The road's lane
        /// points sit close to the kerb on some streets, and the inside of a rounded corner can reach the pavement;
        /// the road surface itself tells where the kerb is. Only static colliders count, so traffic does not bend
        /// the line. The shift spreads over the neighbouring points so the car eases over instead of jinking.
        /// </summary>
        private static void KeepClear(List<Vector3> line, out int moved, out float most)
        {
            moved = 0;
            most = 0f;
            int n = line.Count;
            if (n < 3) return;
            var shift = new float[n];
            var right = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 d = line[Mathf.Min(i + 1, n - 1)] - line[Mathf.Max(i - 1, 0)];
                d.y = 0f;
                if (d.sqrMagnitude < 0.0001f) continue;
                d.Normalize();
                right[i] = new Vector3(d.z, 0f, -d.x);
                if (!Ground(line[i], out float h0)) continue;
                float roomR = Room(line[i], right[i], h0), roomL = Room(line[i], -right[i], h0);
                float o = 0f;
                if (roomR < Clearance && roomL < Clearance) o = (roomR - roomL) * 0.5f;
                else if (roomR < Clearance) o = -Mathf.Min(Clearance - roomR, roomL - Clearance);
                else if (roomL < Clearance) o = Mathf.Min(Clearance - roomL, roomR - Clearance);
                shift[i] = Mathf.Clamp(o, -MaxShift, MaxShift);
            }
            // Hold the strongest shift over a few metres either side, then ease it in and out.
            var held = new float[n];
            for (int i = 0; i < n; i++)
            {
                float best = 0f;
                for (int k = Mathf.Max(0, i - 4); k <= Mathf.Min(n - 1, i + 4); k++)
                    if (Mathf.Abs(shift[k]) > Mathf.Abs(best)) best = shift[k];
                held[i] = best;
            }
            for (int i = 1; i < n - 1; i++)
            {
                float sum = 0f;
                int count = 0;
                for (int k = Mathf.Max(0, i - 3); k <= Mathf.Min(n - 1, i + 3); k++) { sum += held[k]; count++; }
                float o = sum / count;
                if (Mathf.Abs(o) < 0.05f) continue;
                line[i] += right[i] * o;
                moved++;
                most = Mathf.Max(most, Mathf.Abs(o));
            }
        }

#if DEBUG
        /// <summary>For the console: what the kerb feeler finds at a point, facing a direction.</summary>
        internal static string Describe(Vector3 p, Vector3 forward)
        {
            forward.y = 0f;
            forward.Normalize();
            var r = new Vector3(forward.z, 0f, -forward.x);
            if (!Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out RaycastHit g, 3f, ~0, QueryTriggerInteraction.Ignore))
                return "no ground under " + p.ToString("F1");
            float h0 = g.point.y;
            var sb = new System.Text.StringBuilder("ground " + h0.ToString("F2") + " on " + g.collider.name + " (layer " + LayerMask.LayerToName(g.collider.gameObject.layer)
                                                   + (g.collider.attachedRigidbody != null ? ", has rigidbody" : "") + ")");
            foreach (var side in new[] { ("right", r), ("left", -r) })
            {
                sb.Append("\n  ").Append(side.Item1).Append(": room ").Append(Room(p, side.Item2, h0).ToString("F2")).Append(" |");
                Vector3 low = new Vector3(p.x, h0 + 0.45f, p.z);
                if (Physics.Raycast(low, side.Item2, out RaycastHit w, Feel, ~0, QueryTriggerInteraction.Ignore))
                    sb.Append(" wall ").Append(w.collider.name).Append(" at ").Append(w.distance.ToString("F2")).Append(w.collider.attachedRigidbody != null ? " (rb)" : "").Append(" |");
                for (float s = FeelStep; s <= Feel; s += FeelStep * 2f)
                {
                    if (Physics.Raycast(low + side.Item2 * s, Vector3.down, out RaycastHit h, 1.5f, ~0, QueryTriggerInteraction.Ignore))
                        sb.Append(' ').Append(s.ToString("F1")).Append(':').Append((h.point.y - h0).ToString("+0.00;-0.00")).Append(h.collider.attachedRigidbody != null ? "rb" : "");
                    else sb.Append(' ').Append(s.ToString("F1")).Append(":-");
                }
            }
            return sb.ToString();
        }
#endif

        /// <summary>Height of the road under <paramref name="p"/>.</summary>
        private static bool Ground(Vector3 p, out float h)
        {
            h = 0f;
            if (!Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out RaycastHit hit, 3f, ~0, QueryTriggerInteraction.Ignore)) return false;
            if (hit.collider.attachedRigidbody != null) return false;
            h = hit.point.y;
            return true;
        }

        /// <summary>Distance from <paramref name="p"/> along <paramref name="dir"/> to the first kerb, post or wall,
        /// or <see cref="Feel"/> plus a step when there is none.</summary>
        private static float Room(Vector3 p, Vector3 dir, float h0)
        {
            Vector3 low = new Vector3(p.x, h0 + 0.45f, p.z);
            float room = Feel + FeelStep;
            if (Physics.Raycast(low, dir, out RaycastHit wall, Feel, ~0, QueryTriggerInteraction.Ignore) && wall.collider.attachedRigidbody == null)
                room = wall.distance;
            for (float s = FeelStep; s < room; s += FeelStep)
            {
                Vector3 q = low + dir * s;
                if (!Physics.Raycast(q, Vector3.down, out RaycastHit hit, 1.5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (hit.collider.attachedRigidbody != null) continue;
                if (hit.point.y - h0 > KerbHeight) return s;
            }
            return room;
        }

        /// <summary>Points from <paramref name="a"/> (already on the line) to <paramref name="b"/>, <see cref="StraightStep"/> apart.</summary>
        private static void Straight(List<Vector3> line, Vector3 a, Vector3 b)
        {
            float len = Flat(b - a);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / StraightStep));
            for (int k = 1; k <= n; k++) Add(line, Vector3.Lerp(a, b, k / (float)n));
        }

        /// <summary>The arc from tangent point <paramref name="t1"/> to <paramref name="t2"/> around the corner
        /// <paramref name="c"/>, as a quadratic Bezier with the corner as control point - close to the circle for the
        /// angles a road turns, and it keeps the heights of the route.</summary>
        private static void Arc(List<Vector3> line, Vector3 t1, Vector3 t2, Vector3 c, float turnDeg, float radius)
        {
            float length = radius * turnDeg * Mathf.Deg2Rad;
            int n = Mathf.Max(2, Mathf.CeilToInt(length / ArcStep));
            for (int k = 1; k <= n; k++)
            {
                float t = k / (float)n, u = 1f - t;
                Add(line, u * u * t1 + 2f * u * t * c + t * t * t2);
            }
        }

        private static void Add(List<Vector3> line, Vector3 p)
        {
            if (Flat(p - line[line.Count - 1]) >= 0.05f) line.Add(p);
        }

        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }
    }
}
