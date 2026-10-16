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
    /// The blue route band and the next manoeuvre, from the path the road AI is following. The band starts at the
    /// car's spot on the path and runs a fixed distance ahead; the next turn is the first bend of more than 40 degrees
    /// over 15 m of road.
    /// </summary>
    internal static class Route
    {
        private const float Lift = 0.06f;
        private const float Sample = 5f;
        private const float TurnAngle = 40f;
        private const float LookAhead = 600f;

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

            var v = new List<Vector3>();
            var tri = new List<int>();
            float total = 0f, drawn = 0f;
            for (int i = 0; i + 1 < Pts.Count; i++)
            {
                float len = VizScene.Flat(Pts[i + 1] - Pts[i]).magnitude;
                if (drawn < ahead && len > 0.01f)
                {
                    Vector3 d = VizScene.Flat(Pts[i + 1] - Pts[i]) / len;
                    VizRoads.Quad(v, tri, Pts[i], Pts[i + 1] + d * 0.3f, Vector3.Cross(Vector3.up, d), 0f, width, Lift);
                    drawn += len;
                }
                total += len;
            }
            VizRoads.Fill(band, v, tri);
            info.RouteMetres = total;
            NextTurn(ref info, total);

            Vector3? dest = autopilotCar || Autopilot.State == DriveState.Ready ? Autopilot.Destination : null;
            if (dest.HasValue && Pts.Count > 0) dest = Pts[Pts.Count - 1];
            pin.gameObject.SetActive(dest.HasValue);
            if (dest.HasValue) pin.position = dest.Value + Vector3.up * 1.6f + VizScene.Origin;
            return info;
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
