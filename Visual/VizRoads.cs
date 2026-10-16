using System.Collections.Generic;
using Il2CppPathfinding;

namespace Yesla.Visual
{
    /// <summary>
    /// The road around the car for the visualisation, built from the game's road links. Every link is one lane in one
    /// direction (all of them are one-way, 3 m apart on a two-lane road), so each becomes a dark strip with a soft grey
    /// edge line on its outer side. Its inner side gets a solid yellow centre line when a lane runs the other way next
    /// to it, as on a US two-way road, and a dashed white line when it does not.
    ///
    /// Junctions are drawn as one filled patch. The road graph has no junction object: a junction is where the
    /// short turn links meet and where a node has three or more links. Those points are grouped by distance, and each
    /// group is covered by the convex hull around its points, widened by half a lane. A lane that runs into a
    /// junction ends in a white stop line. Lines stop at a junction's patch: the through road of a T-junction is one
    /// link past the side road's mouth, and its edge and centre lines would otherwise run straight across it.
    /// </summary>
    internal static class VizRoads
    {
        private const float LaneWidth = 3.4f;
        private const float EdgeOffset = 1.5f;
        private const float LineWidth = 0.14f;
        private const float EdgeWidth = 0.3f;
        private const float CentreWidth = 0.16f;
        private const float OppositeMin = 1.5f, OppositeMax = 5f;   // sideways gap to a lane running the other way
        private const float JointOverlap = 1.2f;   // strips run past their ends so bends have no gaps
        private const float MinLinedLength = 8f;   // short links are the turns inside junctions, drawn without lines
        private const float Dash = 3f, Gap = 6f;
        private const float JunctionLink = 3.5f;   // junction points closer than this belong to one junction
        private const float JunctionSpan = 16f;    // a link shorter than this between two junction points lies inside one
        private const float JunctionPad = 2f;      // the patch reaches this far past its outermost points
        private const float StopWidth = 0.45f;
        private const float StopBack = 0.8f;       // the stop line sits this far before the lane's end
        private const int PadSides = 8;

        private struct Lane { internal Vector3 A, B, Dir; internal float Length; }
        private static readonly List<Lane> Near = new List<Lane>();
        private static readonly Dictionary<long, int> Degree = new Dictionary<long, int>();
        private static readonly List<Vector3> JunctionPoints = new List<Vector3>();
        private static readonly HashSet<long> JunctionKeys = new HashSet<long>();
        private static readonly HashSet<long> StopKeys = new HashSet<long>();
        private static readonly List<List<Vector2>> Patches = new List<List<Vector2>>();
        private static readonly List<Vector2> Cuts = new List<Vector2>();

        /// <summary>Fills the road and line meshes with every lane within <paramref name="radius"/> of <paramref name="centre"/>.</summary>
        internal static int Build(Vector3 centre, float radius, Mesh road, Mesh lines, Mesh edges, Mesh centreLines)
        {
            var rv = new List<Vector3>();
            var rt = new List<int>();
            var lv = new List<Vector3>();
            var lt = new List<int>();
            var ev = new List<Vector3>();
            var et = new List<int>();
            var cv = new List<Vector3>();
            var ct = new List<int>();

            var links = NodeLink.validNodeLinks;
            if (links == null) { Core.Log.Warning("[Viz] the game has no road links yet - the road stays empty."); return 0; }
            float r2 = radius * radius;
            Near.Clear();
            for (int i = 0; i < links.Count; i++)
            {
                NodeLink link = links[i];
                if (link == null || link.Start == null || link.End == null) continue;
                Vector3 a = link.Start.position, b = link.End.position;
                if (DistSqToSegment(centre, a, b) > r2) continue;
                Vector3 d = b - a; d.y = 0f;
                float len = d.magnitude;
                if (len < 0.1f) continue;
                Near.Add(new Lane { A = a, B = b, Dir = d / len, Length = len });
            }

            FindJunctions();
            Junctions = PatchJunctions(rv, rt);

            int twoWay = 0;
            for (int i = 0; i < Near.Count; i++)
            {
                Lane l = Near[i];
                Vector3 a = l.A, b = l.B, d = l.Dir;
                Vector3 right = Vector3.Cross(Vector3.up, d);
                Quad(rv, rt, a - d * JointOverlap, b + d * JointOverlap, right, 0f, LaneWidth, 0f);
                if (l.Length < MinLinedLength) continue;
                if (StopKeys.Contains(Key(b)))
                    Quad(lv, lt, b - d * (StopBack + StopWidth * 0.5f), b - d * (StopBack - StopWidth * 0.5f), right, 0f, LaneWidth * 0.9f, 0.02f);
                Clipped(ev, et, a, b, right, EdgeOffset, EdgeWidth, 0.02f);
                if (HasOpposite(i))
                {
                    Clipped(cv, ct, a, b, right, -EdgeOffset, CentreWidth, 0.02f);
                    twoWay++;
                    continue;
                }
                for (float s = 0.5f; s < l.Length; s += Dash + Gap)
                {
                    float e = Mathf.Min(s + Dash, l.Length);
                    Clipped(lv, lt, a + d * s, a + d * e, right, -EdgeOffset, LineWidth, 0.02f);
                }
            }

            Fill(road, rv, rt);
            Fill(lines, lv, lt);
            Fill(edges, ev, et);
            Fill(centreLines, cv, ct);
            TwoWay = twoWay;
            return Near.Count;
        }

        private const float ZebraBar = 0.5f, ZebraGap = 0.5f;

        /// <summary>The pedestrian crossings within <paramref name="radius"/>, as white bars along the road.</summary>
        internal static int Crosswalks(Vector3 centre, float radius, Mesh into)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            int count = 0;
            foreach (Driving.Crosswalks.Walk w in Driving.Crosswalks.All)
            {
                Vector3 d = w.Centre - centre; d.y = 0f;
                if (d.magnitude > radius + w.HalfAcross) continue;
                count++;
                int bars = Mathf.Max(1, Mathf.FloorToInt((w.HalfAcross * 2f + ZebraGap) / (ZebraBar + ZebraGap)));
                float first = -(bars * (ZebraBar + ZebraGap) - ZebraGap) * 0.5f + ZebraBar * 0.5f;
                for (int i = 0; i < bars; i++)
                {
                    Vector3 c = w.Centre + w.Across * (first + i * (ZebraBar + ZebraGap));
                    Quad(v, t, c - w.Along * w.HalfAlong, c + w.Along * w.HalfAlong, Vector3.Cross(Vector3.up, w.Along), 0f, ZebraBar, 0.03f);
                }
            }
            Fill(into, v, t);
            return count;
        }

        /// <summary>Lanes in the last build that have a lane running the other way beside them.</summary>
        internal static int TwoWay { get; private set; }

        /// <summary>Junction patches in the last build.</summary>
        internal static int Junctions { get; private set; }

        private static long Key(Vector3 p) =>
            ((long)Mathf.RoundToInt(p.x * 4f) << 32) ^ (uint)Mathf.RoundToInt(p.z * 4f);

        /// <summary>The ends of the short turn links, and every node with three or more links.</summary>
        private static void FindJunctions()
        {
            Degree.Clear();
            JunctionPoints.Clear();
            JunctionKeys.Clear();
            for (int i = 0; i < Near.Count; i++)
            {
                long ka = Key(Near[i].A), kb = Key(Near[i].B);
                Degree[ka] = Degree.TryGetValue(ka, out int da) ? da + 1 : 1;
                Degree[kb] = Degree.TryGetValue(kb, out int db) ? db + 1 : 1;
            }
            for (int i = 0; i < Near.Count; i++)
            {
                Lane l = Near[i];
                bool turn = l.Length < MinLinedLength;
                AddJunctionPoint(l.A, turn || Degree[Key(l.A)] >= 3);
                AddJunctionPoint(l.B, turn || Degree[Key(l.B)] >= 3);
            }
        }

        private static void AddJunctionPoint(Vector3 p, bool junction)
        {
            if (!junction || !JunctionKeys.Add(Key(p))) return;
            JunctionPoints.Add(p);
        }

        /// <summary>Groups the junction points and covers each group with its widened convex hull. Returns the patch count.</summary>
        private static int PatchJunctions(List<Vector3> v, List<int> t)
        {
            StopKeys.Clear();
            Patches.Clear();
            int n = JunctionPoints.Count;
            if (n == 0) return 0;
            var group = new int[n];
            for (int i = 0; i < n; i++) group[i] = i;
            float link2 = JunctionLink * JunctionLink;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Vector3 d = JunctionPoints[i] - JunctionPoints[j];
                    if (d.x * d.x + d.z * d.z > link2) continue;
                    Join(group, i, j);
                }
            var index = new Dictionary<long, int>();
            for (int i = 0; i < n; i++) index[Key(JunctionPoints[i])] = i;
            foreach (Lane l in Near)
                if (l.Length < JunctionSpan && index.TryGetValue(Key(l.A), out int ia) && index.TryGetValue(Key(l.B), out int ib))
                    Join(group, ia, ib);

            var members = new Dictionary<int, List<Vector3>>();
            for (int i = 0; i < n; i++)
            {
                int g = Root(group, i);
                if (!members.TryGetValue(g, out List<Vector3> list)) members[g] = list = new List<Vector3>();
                list.Add(JunctionPoints[i]);
            }

            int patches = 0;
            var ring = new List<Vector2>();
            foreach (List<Vector3> pts in members.Values)
            {
                if (pts.Count < 3) continue;   // a lone bend or merge is no junction
                foreach (Vector3 p in pts) StopKeys.Add(Key(p));
                ring.Clear();
                float y = 0f;
                foreach (Vector3 p in pts)
                {
                    y += p.y;
                    for (int k = 0; k < PadSides; k++)
                    {
                        float a = k * Mathf.PI * 2f / PadSides;
                        ring.Add(new Vector2(p.x + Mathf.Cos(a) * JunctionPad, p.z + Mathf.Sin(a) * JunctionPad));
                    }
                }
                y /= pts.Count;
                List<Vector2> hull = Hull(ring);
                if (hull.Count < 3) continue;
                Patches.Add(hull);
                Vector2 c = Vector2.zero;
                foreach (Vector2 h in hull) c += h;
                c /= hull.Count;
                int start = v.Count;
                v.Add(new Vector3(c.x, y, c.y));
                foreach (Vector2 h in hull) v.Add(new Vector3(h.x, y, h.y));
                for (int k = 0; k < hull.Count; k++)
                {
                    // The hull runs counter-clockwise seen from above; this order faces up.
                    t.Add(start);
                    t.Add(start + 1 + (k + 1) % hull.Count);
                    t.Add(start + 1 + k);
                }
                patches++;
            }
            return patches;
        }

        /// <summary><see cref="Quad"/> without the parts that lie inside a junction patch.</summary>
        private static void Clipped(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 right, float offset, float width, float lift)
        {
            Vector3 shift = right * offset;
            Vector2 p0 = new Vector2(a.x + shift.x, a.z + shift.z), p1 = new Vector2(b.x + shift.x, b.z + shift.z);
            Cuts.Clear();
            foreach (List<Vector2> hull in Patches)
                if (Inside(hull, p0, p1, out float enter, out float leave)) Cuts.Add(new Vector2(enter, leave));
            if (Cuts.Count == 0) { Quad(v, t, a, b, right, offset, width, lift); return; }
            Cuts.Sort((x, y) => x.x.CompareTo(y.x));
            float from = 0f;
            foreach (Vector2 cut in Cuts)
            {
                if (cut.x > from + 0.01f) Quad(v, t, Vector3.Lerp(a, b, from), Vector3.Lerp(a, b, cut.x), right, offset, width, lift);
                from = Mathf.Max(from, cut.y);
            }
            if (from < 0.99f) Quad(v, t, Vector3.Lerp(a, b, from), b, right, offset, width, lift);
        }

        /// <summary>The part of the segment inside a counter-clockwise convex hull (Cyrus-Beck), as fractions of it.</summary>
        private static bool Inside(List<Vector2> hull, Vector2 p0, Vector2 p1, out float enter, out float leave)
        {
            enter = 0f;
            leave = 1f;
            for (int k = 0; k < hull.Count; k++)
            {
                Vector2 h0 = hull[k], h1 = hull[(k + 1) % hull.Count];
                float f0 = Cross(h0, h1, p0), f1 = Cross(h0, h1, p1);
                float df = f1 - f0;
                if (Mathf.Abs(df) < 1e-6f)
                {
                    if (f0 < 0f) return false;
                    continue;
                }
                float at = -f0 / df;
                if (df > 0f) enter = Mathf.Max(enter, at);
                else leave = Mathf.Min(leave, at);
                if (enter >= leave) return false;
            }
            return true;
        }

        private static void Join(int[] group, int i, int j)
        {
            int gi = Root(group, i), gj = Root(group, j);
            if (gi != gj) group[gj] = gi;
        }

        private static int Root(int[] group, int i)
        {
            while (group[i] != i) { group[i] = group[group[i]]; i = group[i]; }
            return i;
        }

        /// <summary>Convex hull, counter-clockwise (monotone chain).</summary>
        private static List<Vector2> Hull(List<Vector2> pts)
        {
            pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var h = new List<Vector2>(pts.Count * 2);
            for (int pass = 0; pass < 2; pass++)
            {
                int floor = h.Count;
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector2 p = pass == 0 ? pts[i] : pts[pts.Count - 1 - i];
                    while (h.Count >= floor + 2 && Cross(h[h.Count - 2], h[h.Count - 1], p) <= 0f) h.RemoveAt(h.Count - 1);
                    h.Add(p);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        /// <summary>True when a lane runs the other way alongside lane <paramref name="i"/>, on its inner (left) side.</summary>
        private static bool HasOpposite(int i)
        {
            Lane l = Near[i];
            Vector3 mid = (l.A + l.B) * 0.5f;
            Vector3 left = -Vector3.Cross(Vector3.up, l.Dir);
            for (int j = 0; j < Near.Count; j++)
            {
                if (j == i) continue;
                Lane o = Near[j];
                if (Vector3.Dot(o.Dir, l.Dir) > -0.8f) continue;
                Vector3 to = (o.A + o.B) * 0.5f - mid; to.y = 0f;
                float side = Vector3.Dot(to, left);
                if (side < OppositeMin || side > OppositeMax) continue;
                if (Mathf.Abs(Vector3.Dot(to, l.Dir)) > (l.Length + o.Length) * 0.5f) continue;
                return true;
            }
            return false;
        }

        /// <summary>A flat strip from <paramref name="a"/> to <paramref name="b"/>, its centre shifted sideways by <paramref name="offset"/>.</summary>
        internal static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 right, float offset, float width, float lift)
        {
            Vector3 c = right * offset + Vector3.up * lift;
            Vector3 h = right * (width * 0.5f);
            int i = v.Count;
            v.Add(a + c - h); v.Add(a + c + h); v.Add(b + c - h); v.Add(b + c + h);
            t.Add(i); t.Add(i + 2); t.Add(i + 1);
            t.Add(i + 1); t.Add(i + 2); t.Add(i + 3);
        }

        internal static void Fill(Mesh m, List<Vector3> v, List<int> t)
        {
            m.Clear();
            if (v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = v.ToArray();
            m.triangles = t.ToArray();
            m.RecalculateBounds();
        }

        private static float DistSqToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector2 p2 = new Vector2(p.x, p.z), a2 = new Vector2(a.x, a.z), b2 = new Vector2(b.x, b.z);
            Vector2 ab = b2 - a2;
            float l = ab.sqrMagnitude;
            float k = l < 0.0001f ? 0f : Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / l);
            return (a2 + ab * k - p2).sqrMagnitude;
        }
    }
}
