using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;
using Yesla.Driving;

namespace Yesla.Visual
{
    internal enum NextStep { None, Left, Right, Arrive }

    /// <summary>What the visualisation's text shows, refreshed on every render.</summary>
    internal struct VizInfo
    {
        internal float SpeedKmh, LimitKmh;
        internal int Cars, People;
        internal bool Driving;
        internal string State, Destination;
        internal float RouteMetres;
        internal NextStep Next;
        internal float NextMetres;
    }

    /// <summary>
    /// The blue route band and the next manoeuvre, from the path the road AI is following. The band is as wide as the
    /// car, starts at its front bumper, runs a fixed distance ahead and fades out over its last stretch; the next turn
    /// is the first bend of more than 40 degrees over 15 m of road.
    /// </summary>
    internal static class Route
    {
        private const float Lift = 0.06f;
        private const float Sample = 5f;
        private const float TurnAngle = 40f;
        private const float LookAhead = 600f;
        private const float FrontOffset = 2.3f;   // from the car's centre to its front bumper, along the path
        private const float StripStep = 2f;       // the band's resolution along the path

        private static readonly List<Vector3> Strip = new List<Vector3>();

        private static readonly List<Vector3> Pts = new List<Vector3>();

        internal static VizInfo Update(LandVehicle car, Mesh band, float ahead, float width, Transform pin)
        {
            var info = new VizInfo { State = StateText(car, out bool driving), Driving = driving };
            bool autopilotCar = Autopilot.DrivenCar != null && Autopilot.DrivenCar.Pointer == car.Pointer;
            info.Destination = (autopilotCar || Autopilot.State == DriveState.Ready) && Autopilot.Destination.HasValue ? Autopilot.DestinationLabel : string.Empty;

            Pts.Clear();
            var agent = car.Agent;
            var path = agent != null && agent.AutoDriving ? agent.path : null;
            var vp = path != null ? path.vectorPath : null;
            if (vp != null && vp.Count >= 2)
            {
                Vector3 pos = car.transform.position;
                int seg = Closest(vp, pos, out float t);
                Pts.Add(Vector3.Lerp(vp[seg], vp[seg + 1], t));
                for (int i = seg + 1; i < vp.Count; i++) Pts.Add(vp[i]);
            }

            float total = 0f;
            for (int i = 0; i + 1 < Pts.Count; i++) total += VizScene.Flat(Pts[i + 1] - Pts[i]).magnitude;
            BuildBand(band, ahead, width);
            info.RouteMetres = total;
            NextTurn(ref info, total);

            Vector3? dest = autopilotCar || Autopilot.State == DriveState.Ready ? Autopilot.Destination : null;
            if (dest.HasValue && Pts.Count > 0) dest = Pts[Pts.Count - 1];
            pin.gameObject.SetActive(dest.HasValue);
            if (dest.HasValue) pin.position = dest.Value + Vector3.up * 1.6f + VizScene.Origin;
            return info;
        }

        /// <summary>
        /// A continuous strip along the path from the bumper, one row every 2 m. V runs 0 to 1 along it so the
        /// material's gradient fades the far end; a strip instead of overlapping quads keeps the see-through end even.
        /// </summary>
        private static void BuildBand(Mesh band, float ahead, float width)
        {
            Strip.Clear();
            float walked = 0f, next = FrontOffset, end = FrontOffset + ahead;
            for (int i = 0; i + 1 < Pts.Count && next <= end; i++)
            {
                Vector3 a = Pts[i], b = Pts[i + 1];
                float len = VizScene.Flat(b - a).magnitude;
                while (next <= walked + len && next <= end)
                {
                    Strip.Add(Vector3.Lerp(a, b, len < 0.001f ? 0f : (next - walked) / len));
                    next += StripStep;
                }
                walked += len;
            }
            if (Pts.Count > 1 && walked > FrontOffset && Strip.Count > 0 && walked < end) Strip.Add(Pts[Pts.Count - 1]);

            band.Clear();
            if (Strip.Count < 2) return;
            var v = new Vector3[Strip.Count * 2];
            var uv = new Vector2[Strip.Count * 2];
            var tri = new int[(Strip.Count - 1) * 6];
            float run = 0f, full = 0f;
            for (int i = 0; i + 1 < Strip.Count; i++) full += VizScene.Flat(Strip[i + 1] - Strip[i]).magnitude;
            for (int i = 0; i < Strip.Count; i++)
            {
                Vector3 back = i > 0 ? VizScene.Flat(Strip[i] - Strip[i - 1]) : Vector3.zero;
                Vector3 fwd = i + 1 < Strip.Count ? VizScene.Flat(Strip[i + 1] - Strip[i]) : Vector3.zero;
                Vector3 d = (back.normalized + fwd.normalized).normalized;
                if (d.sqrMagnitude < 0.01f) d = fwd.sqrMagnitude > 0f ? fwd.normalized : back.normalized;
                Vector3 h = Vector3.Cross(Vector3.up, d) * (width * 0.5f);
                if (i > 0) run += back.magnitude;
                float t = full > 0.01f ? run / full : 0f;
                Vector3 c = Strip[i] + Vector3.up * Lift;
                v[i * 2] = c - h; v[i * 2 + 1] = c + h;
                uv[i * 2] = new Vector2(0f, t); uv[i * 2 + 1] = new Vector2(1f, t);
            }
            for (int i = 0; i + 1 < Strip.Count; i++)
            {
                int k = i * 6, a = i * 2;
                tri[k] = a; tri[k + 1] = a + 2; tri[k + 2] = a + 1;
                tri[k + 3] = a + 1; tri[k + 4] = a + 2; tri[k + 5] = a + 3;
            }
            band.vertices = v;
            band.uv = uv;
            band.triangles = tri;
            band.RecalculateBounds();
        }

        /// <summary>
        /// How far along the current path a point lies, from the car, and how far it is to the side of it. False when
        /// there is no path or the point is behind the car.
        /// </summary>
        internal static bool Along(Vector3 p, out float along, out float side)
        {
            along = 0f; side = float.MaxValue;
            if (Pts.Count < 2) return false;
            float walked = 0f, best = float.MaxValue;
            for (int i = 0; i + 1 < Pts.Count && walked < 120f; i++)
            {
                Vector3 a = VizScene.Flat(Pts[i]), ab = VizScene.Flat(Pts[i + 1] - Pts[i]);
                float l = ab.sqrMagnitude;
                float k = l < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(VizScene.Flat(p) - a, ab) / l);
                float d = (a + ab * k - VizScene.Flat(p)).magnitude;
                if (d < best) { best = d; along = walked + Mathf.Sqrt(l) * k; }
                walked += Mathf.Sqrt(l);
            }
            side = best;
            return along > 0.5f;
        }

        private static string StateText(LandVehicle car, out bool driving)
        {
            driving = Autopilot.DrivenCar != null && Autopilot.DrivenCar.Pointer == car.Pointer;
            if (driving) return "Assisted Driving";
            switch (Autopilot.State)
            {
                case DriveState.Ready: return "Ready";
                case DriveState.Arrived: return "Arrived";
                case DriveState.Aborted: return "Driver in control";
                default: return string.Empty;
            }
        }

        /// <summary>Walks the path in 5 m steps and finds the first bend sharp enough to call a turn.</summary>
        private static void NextTurn(ref VizInfo info, float total)
        {
            info.Next = NextStep.None;
            if (Pts.Count < 2) return;
            var s = new List<Vector3> { Pts[0] };
            float carry = 0f;
            for (int i = 0; i + 1 < Pts.Count && s.Count * Sample < LookAhead; i++)
            {
                Vector3 a = Pts[i], b = Pts[i + 1];
                float len = VizScene.Flat(b - a).magnitude;
                float at = Sample - carry;
                while (at <= len) { s.Add(Vector3.Lerp(a, b, at / len)); at += Sample; }
                carry = len - (at - Sample);
            }
            for (int k = 1; k + 2 < s.Count; k++)
            {
                float angle = Vector3.SignedAngle(VizScene.Flat(s[k] - s[k - 1]), VizScene.Flat(s[k + 2] - s[k + 1]), Vector3.up);
                if (Mathf.Abs(angle) < TurnAngle) continue;
                info.Next = angle > 0f ? NextStep.Right : NextStep.Left;
                info.NextMetres = k * Sample;
                return;
            }
            if (total < LookAhead) { info.Next = NextStep.Arrive; info.NextMetres = total; }
        }

        private static int Closest(Il2CppSystem.Collections.Generic.List<Vector3> vp, Vector3 p, out float t)
        {
            int best = 0;
            float bestD = float.MaxValue;
            t = 0f;
            for (int i = 0; i + 1 < vp.Count; i++)
            {
                Vector3 a = vp[i], ab = vp[i + 1] - a;
                float l = ab.sqrMagnitude;
                float k = l < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / l);
                float d = (a + ab * k - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; t = k; }
            }
            return best;
        }
    }
}
