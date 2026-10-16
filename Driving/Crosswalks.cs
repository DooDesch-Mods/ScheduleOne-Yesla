using System;
using System.Collections.Generic;

namespace Yesla.Driving
{
    /// <summary>
    /// The pedestrian crossings of the map. Vanilla has no crossing logic: each one is a flat painted mesh named
    /// "PedestrianCrossing" (a LODGroup with one MeshRenderer) placed in the scene. The scene is statically batched,
    /// so the mesh filter holds the combined mesh of many objects; each rectangle comes from the transform's axes
    /// instead, sized to fit the renderer's world bounds.
    /// </summary>
    internal static class Crosswalks
    {
        private const string Prefix = "PedestrianCrossing";
        private const float RescanEmptyAfter = 30f;

        internal struct Walk
        {
            internal Vector3 Centre;
            /// <summary>Unit vector across the road, along the long side.</summary>
            internal Vector3 Across;
            /// <summary>Unit vector along the road, the direction cars cross it in.</summary>
            internal Vector3 Along;
            internal float HalfAcross, HalfAlong;
        }

        private static readonly List<Walk> Found = new List<Walk>();
        private static Transform _sentinel;
        private static float _nextScan;

        /// <summary>Every crossing of the loaded world; scanned once, again after the world changed.</summary>
        internal static List<Walk> All
        {
            get
            {
                bool gone = Found.Count > 0 && _sentinel == null;
                if (gone || (Found.Count == 0 && Time.unscaledTime >= _nextScan)) Scan();
                return Found;
            }
        }

        private static void Scan()
        {
            Found.Clear();
            _sentinel = null;
            _nextScan = Time.unscaledTime + RescanEmptyAfter;
            int skipped = 0;
            try
            {
                var groups = UnityEngine.Object.FindObjectsOfType<LODGroup>();
                foreach (LODGroup g in groups)
                {
                    if (g == null || !g.name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                    Renderer r = g.GetComponentInChildren<Renderer>();
                    if (r == null) { skipped++; continue; }
                    Bounds b = r.bounds;
                    Transform t = r.transform;
                    Vector3 x = Flat(t.TransformVector(Vector3.right));
                    Vector3 y = Flat(t.TransformVector(Vector3.up));
                    Vector3 z = Flat(t.TransformVector(Vector3.forward));
                    // The axis that points up is the flat one; the two others span the painted rectangle.
                    Vector3 p, q;
                    if (x.sqrMagnitude <= y.sqrMagnitude && x.sqrMagnitude <= z.sqrMagnitude) { p = y; q = z; }
                    else if (y.sqrMagnitude <= z.sqrMagnitude) { p = x; q = z; }
                    else { p = x; q = y; }
                    if (p.sqrMagnitude < q.sqrMagnitude) { Vector3 s = p; p = q; q = s; }
                    // The rectangle k*p by k*q, turned as it is, has exactly the renderer's world bounds.
                    float span = Mathf.Abs(p.x) + Mathf.Abs(q.x) + Mathf.Abs(p.z) + Mathf.Abs(q.z);
                    if (q.sqrMagnitude < 1e-4f || span < 1e-4f) { skipped++; continue; }
                    float k = (b.size.x + b.size.z) / span * 0.5f;
                    Found.Add(new Walk
                    {
                        Centre = b.center,
                        Across = p.normalized,
                        Along = q.normalized,
                        HalfAcross = p.magnitude * k,
                        HalfAlong = q.magnitude * k,
                    });
                    if (_sentinel == null) _sentinel = g.transform;
                }
            }
            catch (Exception e)
            {
                Core.Log.Error("[Crosswalks] scanning the pedestrian crossings failed: " + e);
                Found.Clear();
                return;
            }
            if (Found.Count == 0) Core.Log.Warning("[Crosswalks] no pedestrian crossings found (" + skipped + " without a renderer); looking again in " + RescanEmptyAfter + " s.");
            else Core.Log.Msg("[Crosswalks] " + Found.Count + " pedestrian crossing(s), first at " + Found[0].Centre.ToString("F1") + ", "
                              + (Found[0].HalfAcross * 2f).ToString("F1") + " x " + (Found[0].HalfAlong * 2f).ToString("F1") + " m"
                              + ", across " + Found[0].Across.ToString("F2") + (skipped > 0 ? ", " + skipped + " without a renderer" : "") + ".");
        }

        /// <summary>
        /// Where the segment from <paramref name="a"/> to <paramref name="b"/> runs inside the crossing, as fractions of
        /// the segment (Liang-Barsky against the rectangle, ignoring height).
        /// </summary>
        internal static bool Clip(Walk w, Vector3 a, Vector3 b, out float enter, out float leave)
        {
            Vector3 d0 = a - w.Centre, d1 = b - w.Centre;
            float u0 = d0.x * w.Across.x + d0.z * w.Across.z, u1 = d1.x * w.Across.x + d1.z * w.Across.z;
            float v0 = d0.x * w.Along.x + d0.z * w.Along.z, v1 = d1.x * w.Along.x + d1.z * w.Along.z;
            enter = 0f;
            leave = 1f;
            return Slab(u0, u1 - u0, w.HalfAcross, ref enter, ref leave) && Slab(v0, v1 - v0, w.HalfAlong, ref enter, ref leave);
        }

        private static bool Slab(float start, float delta, float half, ref float enter, ref float leave)
        {
            if (Mathf.Abs(delta) < 1e-6f) return Mathf.Abs(start) <= half;
            float t0 = (-half - start) / delta, t1 = (half - start) / delta;
            if (t0 > t1) { float s = t0; t0 = t1; t1 = s; }
            enter = Mathf.Max(enter, t0);
            leave = Mathf.Min(leave, t1);
            return enter <= leave;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    }
}
