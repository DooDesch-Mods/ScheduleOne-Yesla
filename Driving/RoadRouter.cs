using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppPathfinding;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Vehicles.AI;
using Path = Il2CppPathfinding.Path;

namespace Yesla.Driving
{
    /// <summary>
    /// The fastest route over the roads for a Yesla, instead of the shortest.
    ///
    /// Vanilla's route search (NavigationUtility.CalculatePath) asks the agent's road seeker for the road leg between
    /// two road links with <c>Seeker.StartPath(start, end, callback)</c>, and that A* search weighs metres only; the
    /// speed limits never enter it. For a car this follower drives, the request is answered here instead: Dijkstra
    /// over the same road graph with travel time as the cost - each edge's length over the speed its profile drives
    /// there, plus time for the turn into it. The answer is a finished path (<c>ABPath.FakePath</c>) handed to the
    /// callback at once; vanilla's routine waits for exactly that and goes on with entry and exit legs, smoothing and
    /// the drive. Every other seeker, every NPC car, goes through vanilla untouched.
    ///
    /// A road through an active police checkpoint costs ten extra minutes: its gate opens on its own only for NPC
    /// drivers, so a detour wins whenever there is one, and without one the car still gets there after the search.
    /// </summary>
    [HarmonyPatch(typeof(Seeker), nameof(Seeker.StartPath), new[] { typeof(Vector3), typeof(Vector3), typeof(OnPathDelegate) })]
    internal static class RoadRouter
    {
        /// <summary>Speed assumed on open road for a profile with no limit, in km/h; the bends set the rest.</summary>
        private const float OpenRoadKmh = 90f;
        /// <summary>Seconds a 90 degree turn costs over driving straight on; scales with the angle.</summary>
        private const float TurnSeconds = 3f;
        /// <summary>A request further than this from every road node is not a road request; vanilla answers it.</summary>
        private const float MaxSnap = 8f;
        /// <summary>Extra seconds for a road connection through an active police checkpoint.</summary>
        private const float CheckpointSeconds = 600f;
        /// <summary>How far around a checkpoint's barrier a road connection counts as going through it, in metres.</summary>
        private const float CheckpointReach = 4f;
        /// <summary>Checkpoints switch on and off with the game's curfew and wanted state; re-read after this long.</summary>
        private const float CheckpointRefreshSeconds = 3f;

        private sealed class RoadGraph
        {
            internal IntPtr Source;
            internal int SourceCount;
            internal Vector3[] Pos;
            internal int[][] To;
            internal float[][] Len;
            internal float[][] ZoneKmh;
            internal int Edges;
        }

        private static RoadGraph _graph;
        private static bool _disabled;

        private static HashSet<long> _closed = new HashSet<long>();
        private static RoadGraph _closedFor;
        private static float _closedAt = -100f;
        private static string _closedLog = string.Empty;

        private static bool Prefix(Seeker __instance, Vector3 start, Vector3 end, OnPathDelegate callback, ref Path __result)
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.RoadRouter.Search");
#endif
            if (_disabled) return true;
            try
            {
                DriveProfile profile = PathFollower.ProfileForRoadSeeker(__instance.Pointer);
                if (profile == null) return true;

                RoadGraph g = GraphFor(__instance);
                if (g == null) return true;

                int from = Nearest(g, start, out float snapFrom);
                int to = Nearest(g, end, out float snapTo);
                if (from < 0 || to < 0 || snapFrom > MaxSnap || snapTo > MaxSnap)
                {
                    Core.Log.Warning("[Router] road request " + start.ToString("F0") + " -> " + end.ToString("F0") + " is " + snapFrom.ToString("F1") + " / "
                                   + snapTo.ToString("F1") + " m from the road graph - vanilla's search answers it.");
                    return true;
                }

                HashSet<long> closed = ClosedEdges(g);
                List<int> fastest = Search(g, from, to, profile, closed, byTime: true, out float fastSeconds, out float fastMetres);
                if (fastest == null)
                {
                    Core.Log.Warning("[Router] no road route " + start.ToString("F0") + " -> " + end.ToString("F0") + " - vanilla's search answers it.");
                    return true;
                }
                List<int> shortest = Search(g, from, to, profile, closed, byTime: false, out float shortSeconds, out float shortMetres);
                int through = 0;
                for (int i = 1; i < fastest.Count; i++)
                    if (closed.Contains(Key(fastest[i - 1], Array.IndexOf(g.To[fastest[i - 1]], fastest[i])))) through++;

                var points = new Il2CppSystem.Collections.Generic.List<Vector3>();
                foreach (int n in fastest) points.Add(g.Pos[n]);
                if (points.Count == 1) points.Add(g.Pos[fastest[0]]);
                ABPath path = ABPath.FakePath(points, null);

                Core.Log.Msg("[Router] " + profile.Label + " road leg: fastest " + fastMetres.ToString("F0") + " m / " + fastSeconds.ToString("F0") + " s"
                           + (shortest != null ? ", shortest " + shortMetres.ToString("F0") + " m / " + shortSeconds.ToString("F0") + " s" : ", shortest none")
                           + ", " + fastest.Count + " nodes" + (through > 0 ? ", through a police checkpoint (no way around)" : "") + ".");

                __result = path;
                if (callback != null) callback.Invoke(path);
                else Core.Log.Warning("[Router] road request without a callback - the route is returned only.");
                return false;
            }
            catch (Exception e)
            {
                Core.Log.Error("[Router] fastest-route search failed, vanilla's shortest route is used: " + e);
                return true;
            }
        }

        /// <summary>The road graph the seeker searches, read into managed arrays once per graph.</summary>
        private static RoadGraph GraphFor(Seeker seeker)
        {
            Il2Cpp.AstarPath astar = Il2Cpp.AstarPath.active;
            if (astar == null || astar.data == null || astar.data.graphs == null) { Core.Log.Warning("[Router] no active A* data - vanilla's search answers."); return null; }

            int mask = seeker.graphMask.value;
            var graphs = astar.data.graphs;
            PointGraph pg = null;
            for (int i = 0; i < graphs.Length && i < 32; i++)
            {
                if ((mask & (1 << i)) == 0 || graphs[i] == null) continue;
                pg = graphs[i].TryCast<PointGraph>();
                if (pg != null) break;
            }
            if (pg == null)
            {
                Core.Log.Warning("[Router] the road seeker (mask " + mask + ") searches no point graph - fastest routes are off for this session.");
                _disabled = true;
                return null;
            }

            int count = pg.nodeCount;
            if (_graph != null && _graph.Source == pg.Pointer && _graph.SourceCount == count) return _graph;
            _graph = Build(pg, count);
            return _graph;
        }

        private static RoadGraph Build(PointGraph pg, int count)
        {
            float t0 = Time.realtimeSinceStartup;
            var nodes = pg.nodes;
            var index = new Dictionary<IntPtr, int>(count);
            var g = new RoadGraph { Source = pg.Pointer, SourceCount = count, Pos = new Vector3[count], To = new int[count][], Len = new float[count][], ZoneKmh = new float[count][] };
            for (int i = 0; i < count; i++)
            {
                PointNode n = nodes[i];
                if (n == null) continue;
                index[n.Pointer] = i;
                g.Pos[i] = (Vector3)n.position;
            }
            for (int i = 0; i < count; i++)
            {
                PointNode n = nodes[i];
                var to = new List<int>();
                var conns = n != null && n.Walkable ? n.connections : null;
                for (int c = 0; conns != null && c < conns.Length; c++)
                {
                    GraphNode other = conns[c]?.node;
                    if (other == null || !other.Walkable || !index.TryGetValue(other.Pointer, out int j)) continue;
                    to.Add(j);
                }
                g.To[i] = to.ToArray();
                g.Len[i] = new float[to.Count];
                g.ZoneKmh[i] = new float[to.Count];
                for (int k = 0; k < to.Count; k++)
                {
                    Vector3 a = g.Pos[i], b = g.Pos[to[k]];
                    g.Len[i][k] = Vector3.Distance(a, b);
                    g.ZoneKmh[i][k] = SpeedPlanner.ZoneKmh((a + b) * 0.5f);
                }
                g.Edges += to.Count;
            }
            Core.Log.Msg("[Router] road graph read: " + count + " nodes, " + g.Edges + " connections, in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms.");
            return g;
        }

        private static int Nearest(RoadGraph g, Vector3 p, out float distance)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < g.Pos.Length; i++)
            {
                if (g.To[i] == null) continue;
                float d = (g.Pos[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            distance = best >= 0 ? Mathf.Sqrt(bestD) : float.MaxValue;
            return best;
        }

        /// <summary>
        /// Dijkstra from <paramref name="from"/> to <paramref name="to"/>. By time: each edge costs its length over the
        /// profile's speed there, plus <see cref="TurnSeconds"/> scaled by the turn from the edge the node was reached
        /// by. By distance: metres only, which is what vanilla's A* finds. Returns the node list, or null.
        /// </summary>
        private static List<int> Search(RoadGraph g, int from, int to, DriveProfile p, HashSet<long> closed, bool byTime, out float seconds, out float metres)
        {
            seconds = 0f; metres = 0f;
            int n = g.Pos.Length;
            var cost = new float[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++) { cost[i] = float.MaxValue; prev[i] = -1; }
            cost[from] = 0f;
            var open = new PriorityQueue<int, float>();
            open.Enqueue(from, 0f);
            while (open.TryDequeue(out int u, out float cu))
            {
                if (cu > cost[u]) continue;
                if (u == to) break;
                int[] outs = g.To[u];
                if (outs == null) continue;
                for (int k = 0; k < outs.Length; k++)
                {
                    int v = outs[k];
                    float step = byTime ? EdgeSeconds(g, u, k, prev[u], p, closed) : g.Len[u][k];
                    float c = cu + step;
                    if (c >= cost[v]) continue;
                    cost[v] = c;
                    prev[v] = u;
                    open.Enqueue(v, c);
                }
            }
            if (cost[to] == float.MaxValue) return null;

            var route = new List<int>();
            for (int at = to; at >= 0; at = prev[at]) route.Add(at);
            route.Reverse();
            for (int i = 1; i < route.Count; i++)
            {
                int u = route[i - 1], v = route[i];
                int k = Array.IndexOf(g.To[u], v);
                metres += g.Len[u][k];
                seconds += EdgeSeconds(g, u, k, i >= 2 ? route[i - 2] : -1, p, closed);
            }
            return route;
        }

        private static float EdgeSeconds(RoadGraph g, int u, int k, int before, DriveProfile p, HashSet<long> closed)
        {
            float kmh = p.NoLimit ? OpenRoadKmh : g.ZoneKmh[u][k] * p.LimitFactor;
            float s = g.Len[u][k] / Mathf.Max(1f, kmh / 3.6f);
            if (closed.Contains(Key(u, k))) s += CheckpointSeconds;
            if (before >= 0)
            {
                Vector3 inDir = g.Pos[u] - g.Pos[before]; inDir.y = 0f;
                Vector3 outDir = g.Pos[g.To[u][k]] - g.Pos[u]; outDir.y = 0f;
                if (inDir.sqrMagnitude > 0.01f && outDir.sqrMagnitude > 0.01f)
                {
                    float angle = Vector3.Angle(inDir, outDir);
                    if (angle > 20f) s += TurnSeconds * angle / 90f;
                }
            }
            return s;
        }

        private static long Key(int u, int k) => ((long)u << 16) | (uint)k;

        /// <summary>
        /// Road connections that pass an active police checkpoint's barrier. Read from the scene at most every
        /// <see cref="CheckpointRefreshSeconds"/>; one route search asks up to nine times in a row.
        /// </summary>
        private static HashSet<long> ClosedEdges(RoadGraph g)
        {
            if (_closedFor == g && Time.time - _closedAt < CheckpointRefreshSeconds) return _closed;
            _closedFor = g;
            _closedAt = Time.time;
            var barriers = new List<Bounds>();
            var names = new List<string>();
            foreach (RoadCheckpoint cp in UnityEngine.Object.FindObjectsOfType<RoadCheckpoint>())
            {
                if (cp == null || cp.ActivationState != RoadCheckpoint.ECheckpointState.Enabled) continue;
                int before = barriers.Count;
                foreach (var ob in new[] { cp.VehicleObstacle1, cp.VehicleObstacle2 })
                {
                    if (ob == null || ob.col == null) continue;
                    Bounds b = ob.col.bounds;
                    b.Expand(new Vector3(CheckpointReach * 2f, 40f, CheckpointReach * 2f));
                    barriers.Add(b);
                }
                if (barriers.Count > before) names.Add(cp.name + " " + cp.transform.position.ToString("F0"));
            }
            var closed = new HashSet<long>();
            for (int u = 0; u < g.To.Length; u++)
            {
                int[] outs = g.To[u];
                for (int k = 0; outs != null && k < outs.Length; k++)
                    foreach (Bounds b in barriers)
                        if (Crosses(b, g.Pos[u], g.Pos[outs[k]])) { closed.Add(Key(u, k)); break; }
            }
            _closed = closed;
            string log = names.Count + " active police checkpoints, " + closed.Count + " road connections through them"
                       + (names.Count > 0 ? ": " + string.Join(", ", names) : "");
            if (log != _closedLog) { _closedLog = log; Core.Log.Msg("[Router] " + log + "."); }
            return _closed;
        }

        private static bool Crosses(Bounds b, Vector3 a, Vector3 c)
        {
            if (b.Contains(a) || b.Contains(c)) return true;
            Vector3 d = c - a;
            float len = d.magnitude;
            if (len < 0.001f) return false;
            return b.IntersectRay(new Ray(a, d / len), out float t) && t <= len;
        }

        /// <summary>Forgets the graph; the world is going away.</summary>
        internal static void Reset()
        {
            _graph = null;
            _disabled = false;
            _closed = new HashSet<long>();
            _closedFor = null;
            _closedAt = -100f;
            _closedLog = string.Empty;
        }
    }
}
