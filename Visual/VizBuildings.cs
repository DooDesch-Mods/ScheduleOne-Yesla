using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Yesla.Visual
{
    /// <summary>
    /// Buildings as see-through grey blocks, the way Tesla's display shows them since update 2025.38. The shapes come
    /// from the buildings' collision geometry: a box collider becomes a box, a mesh collider shows its own simplified
    /// mesh. Footprint and roof line stay true, and the detailed render meshes, which the scene has statically
    /// batched into large combined meshes, are not needed.
    ///
    /// The town has about 8000 colliders within 120 m of a street, and reading them all at once took 80 ms. The
    /// world is cut into square cells instead; one cell is read per visualisation frame and kept, since buildings do
    /// not move. Driving on only reads the cells that come into range.
    ///
    /// A block shows only once the car has seen it: a ray from the car's camera height reaches it before anything
    /// else. Foundations under the road, cellars, interior walls and the back row behind a facade never get a clear
    /// line and stay out of the picture. A block seen once stays, the way the car remembers what it drove past.
    /// </summary>
    internal static class VizBuildings
    {
        /// <summary>Lower than this is a kerb, a bench or a low wall, not a building.</summary>
        private const float MinHeight = 2.5f;
        /// <summary>Narrower than this on its longer side is a pole or a sign.</summary>
        private const float MinFootprint = 2.5f;
        /// <summary>Wider than this is ground or a terrain piece, not a building.</summary>
        private const float MaxFootprint = 150f;
        /// <summary>A bottom this far above the car hangs in the air: a shelter roof or an awning the car drives under.</summary>
        private const float Overhead = 2f;
        private const float CellSize = 25f;
        /// <summary>A cell that takes longer than this to read is reported.</summary>
        private const double SlowCellMs = 6.0;
        /// <summary>Height of the car's cameras above its pivot.</summary>
        private const float EyeHeight = 1.4f;
        /// <summary>Unseen blocks checked for a clear line per visualisation frame, three rays each.</summary>
        private const int LooksPerTick = 30;
        /// <summary>A building the car passes this close is in plain view even where its collider hides behind
        /// a fence or a hedge.</summary>
        private const float SeenUpClose = 4f;

        private sealed class Block
        {
            internal Collider Collider;
            internal IntPtr Pointer;
            internal Mesh Mesh;
            internal Vector3 Position, Scale;
            internal Quaternion Rotation;
            internal Bounds Bounds;
            internal bool Seen;
            internal GameObject Shown;
        }

        private static readonly Dictionary<(int, int), List<Block>> Cells = new Dictionary<(int, int), List<Block>>();
        private static readonly HashSet<IntPtr> Taken = new HashSet<IntPtr>();
        private static readonly List<Block> Unseen = new List<Block>();
        private static readonly List<Block> Active = new List<Block>();
        private static readonly int Invisible = LayerMask.NameToLayer("Invisible");
        private static (int, int) _shownAround = (int.MinValue, int.MinValue);
        private static int _shownCells = -1;
        private static int _look;
        private static bool _reported;
        private static int _small, _overhead, _huge, _moving, _shape, _hidden, _colliders, _seen, _seenLogged;
        private static float _nextSeenLog;
        private static double _slowest;

        /// <summary>
        /// Reads at most one missing cell within <paramref name="radius"/>, nearest first, looks for a clear line to
        /// some of the blocks not seen yet, and shows the seen blocks of every read cell in range as children of
        /// <paramref name="parent"/>, which sits at the stand-in world's origin offset.
        /// </summary>
        internal static void Tick(Vector3 centre, float radius, Transform parent, Material mat, Mesh cube)
        {
            (int, int) here = CellOf(centre);
            int reach = Mathf.CeilToInt(radius / CellSize);
            (int, int) next = (0, 0);
            bool missing = false;
            float best = float.MaxValue;
            int inRange = 0;
            for (int dx = -reach; dx <= reach; dx++)
                for (int dz = -reach; dz <= reach; dz++)
                {
                    var cell = (here.Item1 + dx, here.Item2 + dz);
                    if (Flat(CellCentre(cell, centre.y) - centre).magnitude > radius + CellSize * 0.71f) continue;
                    if (Cells.ContainsKey(cell)) { inRange++; continue; }
                    float d = dx * dx + dz * dz;
                    if (d < best) { best = d; next = cell; missing = true; }
                }

            if (missing)
            {
                Read(next, centre.y, cube);
                inRange++;
            }
            if (missing || here != _shownAround || inRange != _shownCells) Show(here, reach, centre, radius, parent, mat, inRange);
            Look(centre + Vector3.up * EyeHeight, parent, mat);
            if (!missing && !_reported)
            {
                _reported = true;
                Core.Log.Msg("[Viz] buildings: " + Cells.Values.Sum(l => l.Count) + " block(s) in " + Cells.Count + " cell(s) of " + CellSize + " m from "
                             + _colliders + " collider(s); skipped " + _small + " small, " + _overhead + " overhead, " + _huge + " ground-sized, "
                             + _moving + " moving, " + _hidden + " invisible, " + _shape + " other shapes; slowest cell " + _slowest.ToString("F1") + " ms.");
            }
            if (_seen != _seenLogged && Time.unscaledTime >= _nextSeenLog)
            {
                _seenLogged = _seen;
                _nextSeenLog = Time.unscaledTime + 10f;
                Core.Log.Msg("[Viz] buildings: " + _seen + " of " + Cells.Values.Sum(l => l.Count) + " block(s) seen so far, " + Unseen.Count + " in range still hidden from the car.");
            }
        }

        /// <summary>
        /// Casts rays from <paramref name="eye"/> at a few unseen blocks in range, in turn: to the middle of the
        /// block, to its top middle and to its closest point. The first solid thing a ray meets decides; cars,
        /// people and invisible walls are looked through.
        /// </summary>
        private static void Look(Vector3 eye, Transform parent, Material mat)
        {
            if (Unseen.Count == 0) return;
            int looks = Mathf.Min(LooksPerTick, Unseen.Count);
            for (int n = 0; n < looks && Unseen.Count > 0; n++)
            {
                if (_look >= Unseen.Count) _look = 0;
                Block b = Unseen[_look];
                if (b.Seen) { Unseen.RemoveAt(_look); continue; }
                bool clear;
                try { clear = Visible(eye, b); }
                catch (Exception e)
                {
                    Core.Log.Error("[Viz] buildings: line-of-sight check failed, the block stays hidden: " + e.Message);
                    Unseen.RemoveAt(_look);
                    continue;
                }
                if (!clear) { _look++; continue; }
                b.Seen = true;
                _seen++;
                Unseen.RemoveAt(_look);
                Active.Add(b);
                Place(b, parent, mat);
            }
        }

        private static bool Visible(Vector3 eye, Block b)
        {
            if (b.Collider == null) return false;
            Vector3 near = b.Bounds.ClosestPoint(eye);
            if (Flat(near - eye).magnitude < SeenUpClose && near.y > eye.y - EyeHeight - 0.5f) return true;
            Vector3 top = b.Bounds.center;
            top.y = b.Bounds.max.y - 0.3f;
            return Clear(eye, b.Bounds.center, b.Pointer) || Clear(eye, top, b.Pointer) || Clear(eye, near, b.Pointer);
        }

#if DEBUG
        /// <summary>The <paramref name="count"/> nearest blocks in range the car has not seen, each with what the
        /// ray to its middle meets first instead.</summary>
        internal static string DescribeHidden(Vector3 pos, int count)
        {
            Vector3 eye = pos + Vector3.up * EyeHeight;
            var sb = new System.Text.StringBuilder();
            sb.Append(Unseen.Count).Append(" unseen in range, ").Append(Active.Count).Append(" shown");
            foreach (Block b in Unseen.Where(x => x.Collider != null).OrderBy(x => (x.Bounds.ClosestPoint(eye) - eye).sqrMagnitude).Take(count))
            {
                Vector3 d = b.Bounds.center - eye;
                string first = "nothing";
                RaycastHit[] hits = Physics.RaycastAll(eye, d.normalized, d.magnitude + 1f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider c = hits[i].collider;
                    if (c == null || c.attachedRigidbody != null || c.gameObject.layer == Invisible) continue;
                    if (hits[i].distance < best) { best = hits[i].distance; first = Name(c) + " at " + best.ToString("F1") + " m"; }
                }
                sb.Append("\n  ").Append(Name(b.Collider)).Append(" ").Append(b.Bounds.size.ToString("F1")).Append(" top ")
                  .Append((b.Bounds.max.y - pos.y).ToString("F1")).Append(" m, ").Append(d.magnitude.ToString("F0")).Append(" m away: ray meets ").Append(first);
            }
            return sb.ToString();
        }

        private static string Name(Collider c)
        {
            Transform up = c.transform.parent;
            return (up != null ? up.name + "/" : "") + c.name + " [" + LayerMask.LayerToName(c.gameObject.layer) + "]";
        }
#endif

        /// <summary>Whether the first solid thing on the line from <paramref name="eye"/> towards
        /// <paramref name="at"/> is the collider <paramref name="target"/>.</summary>
        private static bool Clear(Vector3 eye, Vector3 at, IntPtr target)
        {
            Vector3 d = at - eye;
            float len = d.magnitude;
            if (len < 0.01f) return true;
            RaycastHit[] hits = Physics.RaycastAll(eye, d / len, len + 1f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            IntPtr first = IntPtr.Zero;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.attachedRigidbody != null || c.gameObject.layer == Invisible) continue;
                if (hits[i].distance < best) { best = hits[i].distance; first = c.Pointer; }
            }
            return first == target;
        }

        private static void Read((int, int) cell, float y, Mesh cube)
        {
            var sw = Stopwatch.StartNew();
            var blocks = new List<Block>();
            Cells[cell] = blocks;
            Collider[] hits;
            try
            {
                Vector3 c = CellCentre(cell, y);
                hits = Physics.OverlapBox(c, new Vector3(CellSize * 0.5f, 300f, CellSize * 0.5f), Quaternion.identity,
                                          Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            }
            catch (Exception e)
            {
                Core.Log.Error("[Viz] reading the buildings of cell " + cell + " failed, the cell stays empty: " + e);
                return;
            }

            foreach (Collider c in hits)
            {
                if (c == null || !Taken.Add(c.Pointer)) continue;
                _colliders++;
                // Cars, people and anything else that moves carries a rigidbody; the world's buildings do not.
                if (c.attachedRigidbody != null) { _moving++; continue; }
                if (c.gameObject.layer == Invisible) { _hidden++; continue; }
                Bounds b = c.bounds;
                float wide = Mathf.Max(b.size.x, b.size.z);
                if (b.size.y < MinHeight || wide < MinFootprint) { _small++; continue; }
                if (wide > MaxFootprint) { _huge++; continue; }
                if (b.min.y > y + Overhead) { _overhead++; continue; }

                Transform t = c.transform;
                BoxCollider box = c.TryCast<BoxCollider>();
                MeshCollider mc = box == null ? c.TryCast<MeshCollider>() : null;
                if (box != null)
                    blocks.Add(new Block { Collider = c, Pointer = c.Pointer, Bounds = b, Mesh = cube, Position = t.TransformPoint(box.center), Rotation = t.rotation, Scale = Vector3.Scale(t.lossyScale, box.size) });
                else if (mc != null && mc.sharedMesh != null)
                    blocks.Add(new Block { Collider = c, Pointer = c.Pointer, Bounds = b, Mesh = mc.sharedMesh, Position = t.position, Rotation = t.rotation, Scale = t.lossyScale });
                else _shape++;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > _slowest) _slowest = ms;
            if (ms > SlowCellMs) Core.Log.Warning("[Viz] buildings: cell " + cell + " took " + ms.ToString("F1") + " ms for " + hits.Length + " collider(s).");
        }

        /// <summary>Shows the seen blocks of the cells in range, hides those that left it, and lines up the unseen
        /// ones in range for <see cref="Look"/>.</summary>
        private static void Show((int, int) here, int reach, Vector3 centre, float radius, Transform parent, Material mat, int inRange)
        {
            foreach (Block b in Active)
                if (b.Shown != null && b.Shown.activeSelf) b.Shown.SetActive(false);
            Active.Clear();
            Unseen.Clear();
            for (int dx = -reach; dx <= reach; dx++)
                for (int dz = -reach; dz <= reach; dz++)
                {
                    var cell = (here.Item1 + dx, here.Item2 + dz);
                    if (!Cells.TryGetValue(cell, out List<Block> blocks)) continue;
                    if (Flat(CellCentre(cell, centre.y) - centre).magnitude > radius + CellSize * 0.71f) continue;
                    foreach (Block b in blocks)
                    {
                        if (!b.Seen) { Unseen.Add(b); continue; }
                        Active.Add(b);
                        Place(b, parent, mat);
                    }
                }
            _look = 0;
            _shownAround = here;
            _shownCells = inRange;
        }

        private static void Place(Block b, Transform parent, Material mat)
        {
            if (b.Shown == null)
            {
                var go = new GameObject("Building") { layer = VizScene.Layer };
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = b.Mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                go.transform.localPosition = b.Position;
                go.transform.localRotation = b.Rotation;
                go.transform.localScale = b.Scale;
                b.Shown = go;
            }
            if (!b.Shown.activeSelf) b.Shown.SetActive(true);
        }

        private static (int, int) CellOf(Vector3 p) => (Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
        private static Vector3 CellCentre((int, int) c, float y) => new Vector3((c.Item1 + 0.5f) * CellSize, y, (c.Item2 + 0.5f) * CellSize);
        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>A new world or a torn-down visualisation: forget the cells and the blocks, whose objects went with the root.</summary>
        internal static void Reset()
        {
            Cells.Clear();
            Taken.Clear();
            Unseen.Clear();
            Active.Clear();
            _shownAround = (int.MinValue, int.MinValue);
            _shownCells = -1;
            _look = 0;
            _reported = false;
            _nextSeenLog = 0f;
            _small = _overhead = _huge = _moving = _shape = _hidden = _colliders = _seen = _seenLogged = 0;
            _slowest = 0;
        }
    }
}
