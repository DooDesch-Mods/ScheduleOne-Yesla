using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;

namespace Yesla.Phone
{
    /// <summary>
    /// The route on the phone map: the road AI's path from the car to its destination, thinned to a polyline the
    /// page can draw as a handful of line pieces.
    ///
    /// The polyline is taken once per path and only replaced when the road AI plans a new one. Between those, only
    /// <see cref="At"/> moves - the first point the car has not reached - so the page shortens the line under the
    /// car by moving pieces instead of building new ones, and Sideload repaints instead of rebuilding the page.
    /// </summary>
    internal static class MapRoute
    {
        /// <summary>How far, in metres, the drawn line may stray from the road AI's path.</summary>
        private const float Tolerance = 1.5f;
        private const int MaxPoints = 90;
        private static readonly List<Vector3> Raw = new List<Vector3>();
        private static readonly List<bool> Keep = new List<bool>();

        private static readonly List<Vector3> Points = new List<Vector3>();
        private static readonly List<double> Map = new List<double>();
        private static System.IntPtr _path;
        private static int _pathCount;
        private static Vector3 _pathEnd;

        /// <summary>Changes whenever the drawn polyline is replaced.</summary>
        internal static int Revision { get; private set; }
        /// <summary>Index of the first polyline point the car has not passed yet; 0 when there is no route.</summary>
        internal static int At { get; private set; }

        /// <summary>Follow <paramref name="car"/>'s current road AI path. Cheap when the path is unchanged.</summary>
        internal static void Update(LandVehicle car)
        {
            var agent = car != null ? car.Agent : null;
            var path = agent != null && agent.AutoDriving ? agent.path : null;
            var vp = path != null ? path.vectorPath : null;
            if (vp == null || vp.Count < 2)
            {
                if (Points.Count > 0) Clear("the road AI has no path");
                return;
            }

            Vector3 end = vp[vp.Count - 1];
            if (path.Pointer != _path || vp.Count != _pathCount || (end - _pathEnd).sqrMagnitude > 1f)
            {
                _path = path.Pointer;
                _pathCount = vp.Count;
                _pathEnd = end;
                Take(vp, car.transform.position);
            }
            At = Passed(car.transform.position);
        }

        internal static void Clear(string why)
        {
            if (Points.Count == 0) return;
            Points.Clear();
            Map.Clear();
            _path = System.IntPtr.Zero;
            _pathCount = 0;
            At = 0;
            Revision++;
            Core.Log.Msg("[MapRoute] route cleared: " + why + ".");
        }

        /// <summary>The route as <c>{"rev":n,"at":k,"pts":[x0,y0,x1,y1,...]}</c> in map coordinates, or null.</summary>
        internal static void Write(Json j)
        {
            if (Map.Count < 4) { j.Null("route"); return; }
            j.Key("route").Open().Prop("rev", Revision).Prop("at", At).Key("pts").OpenArray();
            j.Numbers(Map);
            j.CloseArray().Close();
        }

        private static void Take(Il2CppSystem.Collections.Generic.List<Vector3> vp, Vector3 from)
        {
            Points.Clear();
            Map.Clear();

            int seg = ClosestSegment(vp, from);
            Raw.Clear();
            Raw.Add(from);
            for (int i = seg + 1; i < vp.Count; i++) Raw.Add(vp[i]);

            // Ramer-Douglas-Peucker: keep the point furthest off each straight until every dropped one is within
            // the tolerance. A very long path is thinned with a wider tolerance rather than cut short.
            float tolerance = Tolerance;
            do
            {
                Keep.Clear();
                for (int i = 0; i < Raw.Count; i++) Keep.Add(i == 0 || i == Raw.Count - 1);
                Simplify(0, Raw.Count - 1, tolerance);
                Points.Clear();
                for (int i = 0; i < Raw.Count; i++) if (Keep[i]) Points.Add(Raw[i]);
                tolerance *= 1.5f;
            }
            while (Points.Count > MaxPoints);

            foreach (Vector3 p in Points)
            {
                if (!MapGeo.TryNormalise(p, out double x, out double y))
                {
                    Core.Log.Warning("[MapRoute] a route point at " + p.ToString("F0") + " is off the map - no route shown.");
                    Points.Clear();
                    Map.Clear();
                    break;
                }
                Map.Add(x);
                Map.Add(y);
            }
            At = Points.Count > 1 ? 1 : 0;
            Revision++;
            Core.Log.Msg("[MapRoute] route taken: " + vp.Count + " path points thinned to " + Points.Count + ".");
        }

        private static void Simplify(int first, int last, float tolerance)
        {
            // Iterative, so a path of a few thousand points cannot run the stack out.
            var spans = new Stack<(int, int)>();
            spans.Push((first, last));
            while (spans.Count > 0)
            {
                var (a, b) = spans.Pop();
                if (b - a < 2) continue;
                int worst = -1;
                float worstD = tolerance;
                for (int i = a + 1; i < b; i++)
                {
                    float d = DistanceToSegment(Flat(Raw[i]), Flat(Raw[a]), Flat(Raw[b]));
                    if (d > worstD) { worstD = d; worst = i; }
                }
                if (worst < 0) continue;
                Keep[worst] = true;
                spans.Push((a, worst));
                spans.Push((worst, b));
            }
        }

        /// <summary>The first point after the polyline piece the car is closest to.</summary>
        private static int Passed(Vector3 car)
        {
            if (Points.Count < 2) return 0;
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i + 1 < Points.Count; i++)
            {
                float d = DistanceToSegment(Flat(car), Flat(Points[i]), Flat(Points[i + 1]));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best + 1;
        }

        private static int ClosestSegment(Il2CppSystem.Collections.Generic.List<Vector3> vp, Vector3 from)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i + 1 < vp.Count; i++)
            {
                float d = DistanceToSegment(Flat(from), Flat(vp[i]), Flat(vp[i + 1]));
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return (a + ab * t - p).magnitude;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
