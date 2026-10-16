using System.Collections.Generic;
using Il2CppPathfinding;

namespace Yesla.Visual
{
    /// <summary>
    /// The road around the car for the visualisation, built from the game's road links. Every link is one lane in one
    /// direction (all of them are one-way, 3 m apart on a two-lane road), so each becomes a dark strip with a soft grey
    /// edge line on its outer side. Its inner side gets a solid yellow centre line when a lane runs the other way next
    /// to it, as on a US two-way road, and a dashed white line when it does not.
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

        private struct Lane { internal Vector3 A, B, Dir; internal float Length; }
        private static readonly List<Lane> Near = new List<Lane>();

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

            int twoWay = 0;
            for (int i = 0; i < Near.Count; i++)
            {
                Lane l = Near[i];
                Vector3 a = l.A, b = l.B, d = l.Dir;
                Vector3 right = Vector3.Cross(Vector3.up, d);
                Quad(rv, rt, a - d * JointOverlap, b + d * JointOverlap, right, 0f, LaneWidth, 0f);
                if (l.Length < MinLinedLength) continue;
                Quad(ev, et, a, b, right, EdgeOffset, EdgeWidth, 0.02f);
                if (HasOpposite(i))
                {
                    Quad(cv, ct, a, b, right, -EdgeOffset, CentreWidth, 0.02f);
                    twoWay++;
                    continue;
                }
                for (float s = 0.5f; s < l.Length; s += Dash + Gap)
                {
                    float e = Mathf.Min(s + Dash, l.Length);
                    Quad(lv, lt, a + d * s, a + d * e, right, -EdgeOffset, LineWidth, 0.02f);
                }
            }

            Fill(road, rv, rt);
            Fill(lines, lv, lt);
            Fill(edges, ev, et);
            Fill(centreLines, cv, ct);
            TwoWay = twoWay;
            return Near.Count;
        }

        /// <summary>Lanes in the last build that have a lane running the other way beside them.</summary>
        internal static int TwoWay { get; private set; }

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
