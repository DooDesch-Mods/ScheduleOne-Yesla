using System;
using System.Collections.Generic;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Driving
{
    /// <summary>Where a car is to park: a spot in one of the game's parking lots, or the kerb ahead.</summary>
    internal sealed class ParkTarget
    {
        internal ParkingLot Lot;
        internal int Index = -1;
        internal ParkingSpot Spot;
        /// <summary>The car's root position once parked, and the direction it faces then.</summary>
        internal Vector3 Centre, Heading;
        /// <summary>Where the road AI brings the car before the parking manoeuvre takes over.</summary>
        internal Vector3 Entry;
        internal string Label = string.Empty;
        internal bool Kerb;
        /// <summary>Backed into the spot, so the car faces out of it; <see cref="Heading"/> points to the aisle then.</summary>
        internal bool Reverse;

        /// <summary>The direction the car moves in to get into the spot.</summary>
        internal Vector3 Into => Reverse ? -Heading : Heading;

        internal string LotGuid => Lot != null ? Lot.GUID.ToString() : string.Empty;
    }

    /// <summary>
    /// Autopark: finds a free spot in a vanilla parking lot near the destination and drives into it, backwards
    /// unless the car already faces the spot head-on or the player chose nose first
    /// (<see cref="Config.Preferences.ParkDirection"/>). With no lot nearby the car pulls over to the kerb instead.
    ///
    /// Vanilla parks its NPC cars by teleporting them into the spot and freezing them (<c>LandVehicle.Park</c>), which
    /// would take a player's car out of physics. So the car drives in itself: the road AI brings it to the lot's
    /// entry, then a slow pure-pursuit manoeuvre steers it onto the spot's axis and in. Backing in works like Tesla's
    /// Autopark: the car stops about a car length past the spot, then backs in on one quarter circle at full lock
    /// around the rear axle and the last metres straight. The stop point follows from that circle, so the arc ends
    /// exactly on the spot's axis; pure pursuit with the steering mirrored follows it and absorbs the error of
    /// where the car really stopped. Every rule below holds for both directions. When it ends up crooked it
    /// backs out straight and tries again, at most <see cref="MaxTries"/> times. A wall, post or kerb stone ahead
    /// turns the approach into a multi-point turn: the car backs up with the wheels turned the other way and goes
    /// again, at most <see cref="MaxTurns"/> times. Only a car or a person ahead makes it wait. The spot is then marked as taken
    /// on the host, so vanilla does not park an NPC car into it, and freed again once the car drives off.
    /// </summary>
    internal static class Parking
    {
        private const float SearchRadius = 60f;
        private const int MaxCandidates = 40;
        /// <summary>The manoeuvre lines up on the spot's axis from this far out.</summary>
        private const float FarOut = 8f;
        private const float Wheelbase = 2.9f;
        private const float Lookahead = 3.2f;
        /// <summary>
        /// Backing down the spot's axis from close in, the car aims down to this close, so it takes up a sideways
        /// offset within the few metres a narrow aisle leaves in front of the spot. From further out it keeps
        /// <see cref="Lookahead"/>: aiming this close from 8 m swung it over the axis.
        /// </summary>
        private const float FinalLookahead = 2.2f;
        private const float ApproachKmh = 6f;
        private const float CreepKmh = 3f;
        private const float ReverseKmh = 4f;
        /// <summary>Parked well enough within this sideways offset and heading error.</summary>
        private const float GoodLateral = 0.5f;
        private const float GoodHeading = 12f;
        private const int MaxTries = 3;
        /// <summary>Backing out for a new try ends this far before the spot.</summary>
        private const float BackTo = 5f;
        private const float BlockedGiveUp = 6f;
        private const int MaxTurns = 6;
        private const int SameHitsGiveUp = 2;
        private const int MaxSpots = 3;            // spots given up on before the manoeuvre stops trying
        private const float RetargetInPlace = 14f; // a spot in the same lot this close is reached without the road AI
        private const float MissedBy = 1.5f;       // past or beside the spot by more than this is not parked
        private const float SwingHeading = 25f;    // turned more than this from the spot: back up steering, not straight
        private const float ParkLock = 45f;        // steering lock while parking, degrees
        /// <summary>The backing arc is this much wider than full lock allows: the wheels take a moment to turn.</summary>
        private const float ArcSlack = 1.15f;
        /// <summary>Metres backed straight down the spot's axis after the arc.</summary>
        private const float StraightIn = 2.5f;
        /// <summary>
        /// The line the car drives past the spot on: its rear axle this far out from the spot's centre, so its side
        /// passes about a metre clear of the cars parked in the row, as driving schools teach.
        /// </summary>
        private const float PassLine = 4.4f;
        /// <summary>Radii tried for the swing away from the spot and the arc back into it, widest first; full lock
        /// comes last and needs no swing at all.</summary>
        private static readonly float[] SwingRadii = { 6f, 5f, 4.2f };
        /// <summary>The swing's end runs on this far, so the car settles on its angle instead of cutting onto the point.</summary>
        private const float StageRunOut = 2f;
        /// <summary>Beside the aisle line by more than this is not at the stop point: the arc from there would need
        /// more lock than the car has.</summary>
        private const float StageOffLine = 0.8f;
        /// <summary>At the swing's angle within about 16 degrees counts as lined up at the stop point.</summary>
        private const float StageSquare = 0.96f;
        /// <summary>Turned further than this from facing out of the spot, the car looks into it and cannot back in.</summary>
        private const float NoseInFrom = 120f;
        /// <summary>The car drives this far along the aisle straight before the stop point, where there is room.</summary>
        private const float StageLeadIn = 5f;
        /// <summary>Backing in from where the car stands: at most this far straight back before the arc, and no wider arc.</summary>
        private const float MaxStraightBack = 4f, MaxArc = 12f;
        /// <summary>A retry after backing in crooked lines up on the spot's axis this far out.</summary>
        private const float ReverseLineUp = 5.5f;
        /// <summary>One reverse leg of a multi-point turn.</summary>
        private const float TurnBack = 3f;
        /// <summary>
        /// Pulling out for another try steers onto the spot's axis on the way, and goes on up to this far past
        /// <see cref="BackTo"/> until the car is on it within <see cref="OnAxisLateral"/> and <see cref="OnAxisHeading"/>,
        /// so the next try goes straight in instead of starting as crooked as the last one ended.
        /// </summary>
        private const float AlignExtra = 3f, OnAxisLateral = 0.35f, OnAxisHeading = 6f;
        private const float KerbShift = 0.7f;
        private const float KerbAhead = 8f;
        /// <summary>The host frees a spot once its car is this far from it.</summary>
        private const float LeaveDistance = 5f;

        private enum Phase { Stage, Approach, Back, Settle }

        private static readonly List<ParkingLot> Lots = new List<ParkingLot>();
        private static bool _lotsFound;

        // ---- finding a spot ----------------------------------------------------------------------------------------

        private static List<ParkingLot> AllLots()
        {
            if (_lotsFound) return Lots;
            Lots.Clear();
            try
            {
                foreach (ParkingLot lot in UnityEngine.Object.FindObjectsOfType<ParkingLot>())
                    if (lot != null) Lots.Add(lot);
                _lotsFound = true;
                Core.Log.Msg("[Park] " + Lots.Count + " parking lot(s) in the world.");
            }
            catch (Exception e) { Core.Log.Error("[Park] could not list the parking lots: " + e.Message); }
            return Lots;
        }

        /// <summary>The nearest free lot spot within <see cref="SearchRadius"/> of <paramref name="near"/>, or null.</summary>
        internal static ParkTarget Find(LandVehicle car, Vector3 near, out string why)
        {
            var candidates = new List<(float d, ParkingLot lot, int index, ParkingSpot spot)>();
            foreach (ParkingLot lot in AllLots())
            {
                if (lot == null || lot.ParkingSpots == null) continue;
                for (int i = 0; i < lot.ParkingSpots.Count; i++)
                {
                    ParkingSpot spot = lot.ParkingSpots[i];
                    if (spot == null || spot.AlignmentPoint == null) continue;
                    float d = Flat(spot.AlignmentPoint.position - near).magnitude;
                    if (d <= SearchRadius) candidates.Add((d, lot, i, spot));
                }
            }
            if (candidates.Count == 0) { why = "no parking lot within " + SearchRadius.ToString("F0") + " m"; return null; }
            candidates.Sort((a, b) => a.d.CompareTo(b.d));

            int taken = 0, blocked = 0;
            for (int c = 0; c < candidates.Count && c < MaxCandidates; c++)
            {
                var (_, lot, index, spot) = candidates[c];
                if (!spot.IsUsable || (spot.OccupantVehicle != null && spot.OccupantVehicle.Pointer != car.Pointer)) { taken++; continue; }
                if (Skip.Contains(spot.Pointer)) { blocked++; continue; }
                ParkTarget t = ForSpot(car, lot, index, spot);
                if (!Free(car, t, out string what)) { blocked++; if (blocked <= 3) Core.Log.Msg("[Park] " + t.Label + " is blocked by " + what + "."); continue; }
                why = string.Empty;
                Core.Log.Msg("[Park] chose " + t.Label + ", " + candidates[c].d.ToString("F0") + " m from the destination (" + taken + " taken, " + blocked + " blocked before it).");
                return t;
            }
            why = "all " + Mathf.Min(candidates.Count, MaxCandidates) + " spot(s) nearby are taken";
            return null;
        }

        /// <summary>Free lot spots within <paramref name="radius"/> of <paramref name="near"/>, laid out for this car.</summary>
        internal static void Nearby(LandVehicle car, Vector3 near, float radius, List<ParkTarget> into)
        {
            into.Clear();
            foreach (ParkingLot lot in AllLots())
            {
                if (lot == null || lot.ParkingSpots == null) continue;
                for (int i = 0; i < lot.ParkingSpots.Count; i++)
                {
                    ParkingSpot spot = lot.ParkingSpots[i];
                    if (spot == null || spot.AlignmentPoint == null || !spot.IsUsable) continue;
                    if (Flat(spot.AlignmentPoint.position - near).magnitude > radius) continue;
                    if (spot.OccupantVehicle != null && spot.OccupantVehicle.Pointer != car.Pointer) continue;
                    if (Skip.Contains(spot.Pointer)) continue;
                    ParkTarget t = ForSpot(car, lot, i, spot);
                    if (Free(car, t, out _)) into.Add(t);
                }
            }
        }

        private static ParkTarget ForSpot(LandVehicle car, ParkingLot lot, int index, ParkingSpot spot)
        {
            bool reverse = Config.Preferences.ParkDirection != "forward";
            // Vanilla puts the car's centre at the same place for both; only the rotation turns round.
            var aligned = car.GetAlignmentTransform(spot.AlignmentPoint, reverse ? EParkingAlignment.RearToKerb : EParkingAlignment.FrontToKerb);
            var t = new ParkTarget
            {
                Lot = lot, Index = index, Spot = spot, Centre = aligned.Item1,
                Heading = Flat(aligned.Item2 * Vector3.forward).normalized, Reverse = reverse,
                Label = lot.gameObject.name + " #" + index,
            };
            t.Entry = lot.EntryPoint != null ? lot.EntryPoint.position : t.Centre - t.Into * FarOut;
            return t;
        }

        /// <summary>The kerb a few metres ahead, from the car's pose where the road AI stopped it.</summary>
        internal static ParkTarget Kerb(LandVehicle car)
        {
            Vector3 fwd = Flat(car.transform.forward).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 pos = car.transform.position;
            return new ParkTarget
            {
                Centre = pos + fwd * KerbAhead + right * KerbShift, Heading = fwd, Entry = pos,
                Label = "the kerb", Kerb = true,
            };
        }

        /// <summary>Whether anything stands in the space the car will take, at bumper to roof height.</summary>
        internal static bool Free(LandVehicle car, ParkTarget t, out string what)
        {
            what = string.Empty;
            Vector3 dims = car.BoundingBoxDimensions;
            // The floor, not the roof of a car standing in the spot: from that roof the box floated above the car
            // and every taken spot counted as free.
            float ground = Ground(car, t.Centre + Vector3.up);
            Vector3 middle = new Vector3(t.Centre.x, ground + 0.95f, t.Centre.z);
            Vector3 half = new Vector3(dims.x * 0.5f, 0.5f, dims.z * 0.5f - 0.3f);
            var hits = Physics.OverlapBox(middle, half, Quaternion.LookRotation(t.Heading), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i];
                if (c == null || c.transform.IsChildOf(car.transform)) continue;
                Transform up = c.transform.parent;
                what = (up != null ? up.name + "/" : "") + c.name;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Whether a car-wide strip from <paramref name="from"/> to <paramref name="to"/> is clear at bumper to roof
        /// height. One overlap box along the strip: the box sweeps of the physics API are stripped from the IL2CPP
        /// build and throw.
        /// </summary>
        private static bool Corridor(LandVehicle car, Vector3 from, Vector3 to, out string what)
        {
            what = string.Empty;
            Vector3 run = Flat(to - from);
            float len = run.magnitude;
            if (len < 0.1f) return true;
            Vector3 mid = (from + to) * 0.5f;
            mid.y = Mathf.Max(Ground(car, from + Vector3.up), Ground(car, to + Vector3.up)) + 0.95f;
            var hits = Physics.OverlapBox(mid, new Vector3(car.BoundingBoxDimensions.x * 0.5f, 0.45f, len * 0.5f), Quaternion.LookRotation(run / len),
                                          Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i];
                if (c == null || c.transform.IsChildOf(car.transform)) continue;
                Transform up = c.transform.parent;
                what = (up != null ? up.name + "/" : "") + c.name;
                return false;
            }
            return true;
        }

        /// <summary>The road AI hands over this close to the spot, as soon as the run into the aisle is clear.</summary>
        private const float HandOver = 25f;
        /// <summary>The aisle in front of the spot, as far out as the car stops to back in.</summary>
        private const float AisleOut = 4.5f;
        private static string _reachBlock = string.Empty;

        /// <summary>
        /// Whether the car is close enough to the spot to take over from the road AI, with nothing between it and
        /// the aisle in front of the spot at bumper to roof height. Kerbs are lower and do not count. The parking
        /// manoeuvre then starts with room to line up instead of where the road AI stops, a few metres from the spot.
        /// </summary>
        internal static bool InReach(LandVehicle car, ParkTarget t, out string why)
        {
            why = string.Empty;
            Vector3 pos = car.transform.position;
            if (Flat(t.Centre - pos).magnitude > HandOver) return false;
            Vector3 front = t.Centre - t.Into * AisleOut;
            float d = Flat(front - pos).magnitude;
            if (d < 1f) { why = "at the aisle in front of it"; return true; }
            Vector3 nose = pos + Flat(front - pos) / d * (car.BoundingBoxDimensions.z * 0.5f);
            if (!Corridor(car, nose, front, out string what))
            {
                if (what != _reachBlock) Core.Log.Msg("[Park] " + t.Label + " is " + Flat(t.Centre - pos).magnitude.ToString("F0") + " m away, " + what + " stands between the car and its aisle - the road AI drives on.");
                _reachBlock = what;
                return false;
            }
            _reachBlock = string.Empty;
            why = Flat(t.Centre - pos).magnitude.ToString("F0") + " m from it, a clear run of " + d.ToString("F0") + " m into its aisle";
            return true;
        }

        // ---- the manoeuvre -----------------------------------------------------------------------------------------

        private static LandVehicle _car;
        private static ParkTarget _target;
        private static Action<bool, string> _done;
        private static Phase _phase;
        private static readonly List<Vector3> Path = new List<Vector3>();
        /// <summary>Backing in: the arc and the straight into the spot, car centres from the stop point on.</summary>
        private static readonly List<Vector3> ReversePath = new List<Vector3>();
        /// <summary>Backing in: past the spot and the swing away from it, car centres up to the stop point.</summary>
        private static readonly List<Vector3> StagePath = new List<Vector3>();
        private static Vector3 _stage, _aisle, _stageFacing;
        private static float _swing, _arc;
        private static int _seg;
        /// <summary>+1 when the car drives into the spot forwards, -1 when it backs in.</summary>
        private static float _dir = 1f;
        private static int _tries, _turns, _sameHits;
        private static string _lastHit = string.Empty;
        // Spots given up on during this manoeuvre; Find passes over them when it looks for the next one.
        private static readonly HashSet<IntPtr> Skip = new HashSet<IntPtr>();
        private static bool _turning;
        private static Vector3 _backFrom;
        private static float _phaseTime, _blockedFor, _stillFor, _stuckFor, _steer;
        private static string _blockedBy = string.Empty;

        internal static bool Active => _car != null;

        /// <summary>The spot the running manoeuvre heads for, or null.</summary>
        internal static ParkTarget Current => _target;
        internal static string Describe() => _car == null ? "parking: idle"
            : "parking: " + _target.Label + " phase " + _phase + " try " + _tries + " " + Pose();

        internal static void Start(LandVehicle car, ParkTarget target, Action<bool, string> done)
        {
            if (_car != null) Cancel("a new parking manoeuvre started");
            Begin(car, target, done);
        }

        /// <summary>Forgets the spots given up on; called when a new trip starts.</summary>
        internal static void Forget() => Skip.Clear();

        /// <summary>
        /// Set when the manoeuvre gave up its spot for one the road AI has to drive to (another lot, or too far to
        /// reach at walking pace). The caller reads it once in its done callback.
        /// </summary>
        internal static ParkTarget Next { get; private set; }

        internal static ParkTarget TakeNext()
        {
            ParkTarget next = Next;
            Next = null;
            return next;
        }

        private static void Begin(LandVehicle car, ParkTarget target, Action<bool, string> done)
        {
            // The player may have switched the direction since the spot was chosen; the target turns round with it.
            // On auto the car backs in, unless it already faces the spot head-on and drives straight in.
            string mode = Config.Preferences.ParkDirection;
            bool reverse = !target.Kerb && mode != "forward";
            if (reverse && mode == "auto" && HeadOn(car, target, out string headOn))
            {
                reverse = false;
                Core.Log.Msg("[Park] auto: " + headOn + " - parking nose first.");
            }
            if (reverse != target.Reverse)
            {
                target.Heading = -target.Heading;
                target.Reverse = reverse;
            }
            _dir = reverse ? -1f : 1f;
            _car = car;
            _target = target;
            _done = done;
            // The player steers with 25 degrees and vanilla's road AI with 35; parking turns tighter than either.
            car.OverrideMaxSteerAngle(ParkLock);
            _tries = 0;
            _turns = 0;
            _sameHits = 0;
            _lastHit = string.Empty;
            _steer = 0f;
            _seg = 0;
            Path.Clear();
            Vector3 pos = Flat3(car.transform.position, target.Centre.y);
            Path.Add(pos);
            string towards = target.Label + " (" + Vector3.Distance(car.transform.position, target.Centre).ToString("F0") + " m)";
            if (target.Reverse)
            {
                if (PlanReverse(car, target, pos))
                {
                    Path.AddRange(StagePath);
                    SetPhase(Phase.Stage, "past " + towards + (_swing > 1f ? ", swinging out " + _swing.ToString("F0") + " deg" : ", square to the aisle")
                             + " to stop " + Flat(_stage - target.Centre).magnitude.ToString("F1") + " m from it, then back on a " + _arc.ToString("F1") + " m arc");
                    return;
                }
                Core.Log.Warning("[Park] no room in the aisle to back into " + target.Label + " - parking nose first.");
                target.Heading = -target.Heading;
                target.Reverse = false;
                _dir = 1f;
            }
            if (target.Kerb) Path.Add(pos + target.Heading * 3f + Vector3.Cross(Vector3.up, target.Heading) * (KerbShift * 0.5f));
            else Path.Add(target.Centre - target.Heading * FarOut);
            Path.Add(target.Centre);
            SetPhase(Phase.Approach, "towards " + towards);
        }

        /// <summary>Facing the spot within this many degrees counts as head-on.</summary>
        private const float HeadOnAngle = 25f;
        /// <summary>Head-on, the car stands at most this far to the side of the spot's axis.</summary>
        private const float HeadOnLateral = 1.2f;

        /// <summary>
        /// Whether the car stands in front of the spot, near its axis and facing into it, so that driving straight in
        /// beats driving past, swinging out and backing in. Anything less clear backs in.
        /// </summary>
        private static bool HeadOn(LandVehicle car, ParkTarget t, out string why)
        {
            Vector3 into = t.Into;
            Vector3 pos = car.transform.position;
            float ahead = Vector3.Dot(Flat(t.Centre - pos), into);
            float aside = Vector3.Dot(Flat(pos - t.Centre), Vector3.Cross(Vector3.up, into));
            float angle = Vector3.Angle(into, Flat(car.transform.forward));
            why = "facing " + t.Label + " " + angle.ToString("F0") + " deg off its axis, " + ahead.ToString("F1") + " m short and "
                  + aside.ToString("F1") + " m aside";
            return angle < HeadOnAngle && Mathf.Abs(aside) < HeadOnLateral && ahead > 2f && ahead < HandOver;
        }

        /// <summary>
        /// Backing in the way a driver does it: past the spot on <see cref="PassLine"/>, a swing away from the spot,
        /// then back on an arc until the car faces out of the spot, and the last <see cref="StraightIn"/> metres
        /// straight. With h the way out of the spot and a the way along the aisle, the rear axle ends the arc at E on
        /// the axis and the circle's centre is C = E + a·r; at angle φ on it the rear axle is at
        /// C + r·(h·cos φ - a·sin φ) and the car faces h·sin φ + a·cos φ. The swing is a forward arc of the same
        /// radius from the pass line through ψ, and it ends where the backing arc starts when
        /// PassLine + r·(1 - cos ψ) = E + r·cos ψ. A wide radius swings further and backs in smoothly; full lock
        /// needs no swing and stops square to the aisle. The widest radius whose stop point and line there are clear
        /// wins.
        /// Of the two ways along the aisle it takes the one the car is heading, unless no stop point is free there.
        /// </summary>
        private static bool PlanReverse(LandVehicle car, ParkTarget target, Vector3 pos)
        {
            Vector3 h = target.Heading;
            Vector3 side = Vector3.Cross(Vector3.up, h);
            float y = target.Centre.y;
            float rMin = Wheelbase / Mathf.Tan(ParkLock * Mathf.Deg2Rad) * ArcSlack;
            float eh = StraightIn - Wheelbase * 0.5f;
            Vector3 end = target.Centre + h * eh;
            bool sideFirst = Vector3.Dot(Flat(target.Centre - pos), side) >= 0f;
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 a = sideFirst == (pass == 0) ? side : -side;
                string blocked = null;
                for (int k = 0; k <= SwingRadii.Length; k++)
                {
                    float r = k < SwingRadii.Length ? SwingRadii[k] : rMin;
                    float cos = Mathf.Min(1f, (PassLine + r - eh) / (2f * r));
                    float psi = Mathf.Acos(cos), sin = Mathf.Sin(psi);
                    Vector3 c = end + a * r;
                    Vector3 stopRear = c + (h * cos - a * sin) * r;
                    Vector3 facing = h * sin + a * cos;
                    Vector3 startRear = stopRear - a * (r * sin) - h * (r * (1f - cos));
                    Vector3 stop = Flat3(stopRear + facing * (Wheelbase * 0.5f), y);
                    Vector3 start = Flat3(startRear + a * (Wheelbase * 0.5f), y);
                    if (!Free(car, new ParkTarget { Centre = stop, Heading = facing }, out string what)) { blocked ??= what; continue; }

                    StagePath.Clear();
                    Vector3 leadIn = start - a * StageLeadIn;
                    if (Vector3.Dot(Flat(leadIn - pos), a) > 1f) StagePath.Add(leadIn);
                    StagePath.Add(start);
                    int steps = Mathf.CeilToInt(psi * Mathf.Rad2Deg / 10f);
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = psi * i / steps;
                        Vector3 rear = startRear + a * (r * Mathf.Sin(t)) + h * (r * (1f - Mathf.Cos(t)));
                        StagePath.Add(Flat3(rear + (a * Mathf.Cos(t) + h * Mathf.Sin(t)) * (Wheelbase * 0.5f), y));
                    }
                    StagePath.Add(stop + facing * StageRunOut);
                    // The car drives this whole line, front bumper first; a fence across the aisle shows here.
                    bool clear = true;
                    for (int i = 1; i < StagePath.Count && clear; i++)
                        clear = Corridor(car, StagePath[i - 1], StagePath[i], out what);
                    if (!clear) { blocked ??= what; continue; }

                    ReversePath.Clear();
                    for (float deg = psi * Mathf.Rad2Deg; ; deg = Mathf.Min(90f, deg + 10f))
                    {
                        float phi = deg * Mathf.Deg2Rad;
                        Vector3 rear = c + (h * Mathf.Cos(phi) - a * Mathf.Sin(phi)) * r;
                        ReversePath.Add(Flat3(rear + (h * Mathf.Sin(phi) + a * Mathf.Cos(phi)) * (Wheelbase * 0.5f), y));
                        if (deg >= 90f) break;
                    }
                    ReversePath.Add(target.Centre);
                    _stage = stop;
                    _aisle = a;
                    _stageFacing = facing;
                    _swing = psi * Mathf.Rad2Deg;
                    _arc = r;
                    if (blocked != null) Core.Log.Msg("[Park] backing into " + target.Label + ": a wider swing is blocked by " + blocked + ", taking a " + r.ToString("F1") + " m arc.");
                    return true;
                }
                Core.Log.Msg("[Park] backing into " + target.Label + (pass == 0 ? " from this side" : " from the other side") + ": every stop point is blocked, first by " + blocked + ".");
            }
            return false;
        }

        /// <summary>
        /// Backing in: one arc from where the car really stands when it fits, otherwise the arc planned from the stop
        /// point, joined wherever the car is.
        /// </summary>
        private static void StartIn(Vector3 pos, string why)
        {
            // Nose towards the spot, the way it came in from the road when the turn into the aisle failed: no
            // reverse line exists from here, so it parks nose first like a spot without room in the aisle, after
            // pulling back onto the spot's axis.
            float turned = Vector3.SignedAngle(_target.Heading, Flat(_car.transform.forward), Vector3.up);
            if (Mathf.Abs(turned) > NoseInFrom)
            {
                Core.Log.Warning("[Park] " + why + ", facing into " + _target.Label + " (" + Pose() + ") - parking nose first.");
                _target.Heading = -_target.Heading;
                _target.Reverse = false;
                _dir = 1f;
                StartBack(pos, false, "pulling back onto the axis to drive in nose first");
                return;
            }
            Path.Clear();
            if (!ArcFrom(pos, _car.transform.forward, out string how))
            {
                Path.AddRange(ReversePath);
                how = how + ", following the arc planned from the stop point";
            }
            _seg = 0;
            SetPhase(Phase.Approach, why + ", backing in, " + Pose() + "; " + how);
        }

        /// <summary>
        /// Backing in from the car's real pose: d metres straight back, then one circle onto the spot's axis, then
        /// straight in. With u the way the car faces, h the way out of the spot and theta the turn from u to h, the
        /// rear axle P ends at E = P + s*r*(right(u) - right(h)); E is on the axis when s*r*(1 - cos theta) equals P's
        /// offset from it, which fixes the radius. Backing with the centre on side s turns the car by -s, so the sign
        /// of theta must agree. The first d that gives a radius between full lock and <see cref="MaxArc"/> wins.
        /// </summary>
        private static bool ArcFrom(Vector3 pos, Vector3 forward, out string how)
        {
            Vector3 h = _target.Heading;
            Vector3 hr = Vector3.Cross(Vector3.up, h);
            Vector3 u = Flat(forward);
            if (u.sqrMagnitude < 1e-4f) { how = "no heading to plan from"; return false; }
            u.Normalize();
            float theta = Vector3.SignedAngle(u, h, Vector3.up);
            if (Mathf.Abs(theta) < 10f) { how = "already facing out of the spot"; return false; }
            float rMin = Wheelbase / Mathf.Tan(ParkLock * Mathf.Deg2Rad) * ArcSlack;
            float bend = 1f - Mathf.Cos(theta * Mathf.Deg2Rad);
            float y = _target.Centre.y;
            string first = null;
            for (float d = 0f; d <= MaxStraightBack; d += 0.25f)
            {
                Vector3 p = Flat3(pos - u * (Wheelbase * 0.5f + d), y);
                float lat = Vector3.Dot(p - _target.Centre, hr);
                float side = lat >= 0f ? 1f : -1f;
                if (Mathf.Sign(theta) != -side) { first ??= "the car is turned the wrong way for one arc"; continue; }
                float r = Mathf.Abs(lat) / bend;
                if (r < rMin) { first ??= "arc tighter than full lock (" + r.ToString("F1") + " m)"; continue; }
                if (r > MaxArc) { first ??= "arc wider than " + MaxArc + " m"; continue; }
                Vector3 c = p + Vector3.Cross(Vector3.up, u) * (side * r);
                Vector3 e = c - hr * (side * r);
                if (Vector3.Dot(e - _target.Centre, h) < -Wheelbase * 0.5f + StraightIn * 0.5f) { first ??= "the arc would end inside the spot"; continue; }

                Path.Add(Flat3(pos, y));
                if (d > 0.1f) Path.Add(Flat3(pos - u * d, y));
                int steps = Mathf.Max(3, Mathf.CeilToInt(Mathf.Abs(theta) / 10f));
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 facing = Quaternion.AngleAxis(theta * i / steps, Vector3.up) * u;
                    Vector3 rear = c - Vector3.Cross(Vector3.up, facing) * (side * r);
                    Path.Add(Flat3(rear + facing * (Wheelbase * 0.5f), y));
                }
                Path.Add(_target.Centre);
                how = "one arc from here: " + d.ToString("F2") + " m straight back, radius " + r.ToString("F1") + " m, " + Mathf.Abs(theta).ToString("F0") + " deg";
                return true;
            }
            how = first ?? "no straight-back distance gives a fitting arc";
            return false;
        }

        /// <summary>Stops the manoeuvre without reporting back; the caller hands the controls back.</summary>
        internal static void Cancel(string why)
        {
            if (_car == null) return;
            Core.Log.Msg("[Park] cancelled: " + why + ".");
            _car.ResetMaxSteerAngle();
            _car = null;
            _target = null;
            _done = null;
        }

        private static void Finish(bool ok, string why)
        {
            LandVehicle car = _car;
            ParkTarget t = _target;
            Action<bool, string> done = _done;
            _car = null;
            _target = null;
            _done = null;
            if (ok) Core.Log.Msg("[Park] parked in " + t.Label + ": " + why + ".");
            else Core.Log.Warning("[Park] could not park in " + t.Label + ": " + why + ".");
            if (car != null)
            {
                car.throttleOverride = 0f;
                car.steerOverride = 0f;
                car.ResetMaxSteerAngle();
                if (ok && !t.Kerb) Net.Remote.TakeSpot(car, t);
            }
            done?.Invoke(ok, why);
        }

        private static void SetPhase(Phase next, string why)
        {
            Core.Log.Msg("[Park] " + next + ": " + why + ".");
            _phase = next;
            _phaseTime = 0f;
            _blockedFor = 0f;
            _stillFor = 0f;
            _stuckFor = 0f;
        }

        /// <summary>Called from OnLateUpdate, after vanilla's agent, which no longer drives this car.</summary>
        internal static void LateTick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.Parking.LateTick");
#endif
            if (_car == null) return;
            LandVehicle car = _car;
            if (car.gameObject == null || !car.gameObject.activeInHierarchy) { Finish(false, "the car is gone"); return; }

            float dt = Time.deltaTime;
            _phaseTime += dt;
            car.overrideControls = true;
            float v = car.Speed_Kmh;
            Vector3 pos = car.transform.position;
            Vector3 fwd = Flat(car.transform.forward).normalized;
            Vector3 into = _target.Into;
            // The way the car moves into the spot, and its speed that way.
            Vector3 travel = fwd * _dir;
            float vIn = v * _dir;
            float along = Vector3.Dot(Flat(_target.Centre - pos), into);
            float lat = Vector3.Dot(Flat(pos - _target.Centre), Vector3.Cross(Vector3.up, into));
            float heading = Vector3.SignedAngle(_target.Heading, fwd, Vector3.up);

            switch (_phase)
            {
                case Phase.Stage:
                {
                    float left = Remaining(pos);
                    // At the stop point and at the swing's angle; still turning in from the road is not there yet,
                    // and the arc from the real pose takes up any distance driven past it.
                    float beyond = Vector3.Dot(Flat(pos - _stage), _stageFacing);
                    float offLine = Vector3.Dot(Flat(pos - _stage), Vector3.Cross(Vector3.up, _stageFacing));
                    bool past = beyond > -0.4f && Vector3.Dot(fwd, _stageFacing) > StageSquare && Mathf.Abs(offLine) < StageOffLine;
                    bool overshot = beyond > StageRunOut - 0.5f;
                    if (past || overshot || left < 1f || _phaseTime > 15f)
                    {
                        StartIn(pos, past ? "stopped past the spot" : overshot || left < 1f ? "at the end of the aisle line, " + offLine.ToString("F1") + " m beside it" : "still not past the spot after 15 s");
                        break;
                    }
                    if (Blocked(car, fwd, Mathf.Max(0f, v), out string ahead, out bool aheadMoving))
                    {
                        if (!aheadMoving) { StartIn(pos, ahead + " ahead"); break; }
                        Hold(car, v, dt, ahead);
                        if (_blockedFor > BlockedGiveUp) Finish(false, "blocked by " + ahead);
                        break;
                    }
                    _blockedFor = 0f;
                    _stuckFor = Mathf.Abs(v) < 0.5f && car.throttleOverride > 0.25f ? _stuckFor + dt : 0f;
                    if (_stuckFor > 2.5f) { StartIn(pos, "not moving with the throttle on"); break; }
                    Drive(car, v, left < 3f ? CreepKmh : ApproachKmh, dt);
                    Steer(car, Pursuit(pos, fwd, 1f), dt);
                    break;
                }
                case Phase.Approach:
                {
                    bool onAxis = _seg >= Path.Count - 2;
                    if (onAxis && along < 0.25f)
                    {
                        // Past the spot or beside it is not parked, however many tries it took.
                        bool there = along > -MissedBy && Mathf.Abs(lat) < MissedBy;
                        if (there && (_target.Kerb || (Mathf.Abs(lat) <= GoodLateral && Mathf.Abs(heading) <= GoodHeading) || _tries >= MaxTries))
                        {
                            SetPhase(Phase.Settle, "in place, " + Pose());
                            break;
                        }
                        if (_tries >= MaxTries)
                        {
                            Finish(false, "missed the spot (" + Pose() + ")");
                            break;
                        }
                        _tries++;
                        // Badly turned: backing up with the wheels the other way round swings the nose onto the axis.
                        StartBack(pos, Mathf.Abs(heading) > SwingHeading, (there ? "crooked" : "missed the spot") + " (" + Pose() + "), backing out for try " + (_tries + 1));
                        break;
                    }
                    if (!_target.Kerb && onAxis && along < 2.5f && Mathf.Abs(lat) > 0.9f && _tries < MaxTries)
                    {
                        _tries++;
                        StartBack(pos, Mathf.Abs(heading) > SwingHeading, "too far off the spot's axis (" + Pose() + "), backing out for try " + (_tries + 1));
                        break;
                    }
                    if (Blocked(car, travel, Mathf.Max(0f, vIn), out string what, out bool moving))
                    {
                        // A parked car or a wall right at the end of the spot: as far in as it goes.
                        if (!moving && onAxis && along < 2f)
                        {
                            SetPhase(Phase.Settle, "stopped short behind " + what + ", " + Pose());
                            break;
                        }
                        if (!moving && !_target.Kerb && Hit(what) >= SameHitsGiveUp)
                        {
                            Retarget(car, what);
                            break;
                        }
                        if (!moving && _turns < MaxTurns)
                        {
                            _turns++;
                            StartBack(pos, true, what + " ahead, backing up for turn " + _turns + " (" + Pose() + ")");
                            break;
                        }
                        Hold(car, v, dt, what);
                        if (_blockedFor > BlockedGiveUp) Finish(false, "blocked by " + what);
                        break;
                    }
                    _blockedFor = 0f;
                    // Throttle on and not moving: a wheel against a kerb or a low wall the probe passes over.
                    _stuckFor = Mathf.Abs(v) < 0.5f && car.throttleOverride * _dir > 0.25f ? _stuckFor + dt : 0f;
                    if (_stuckFor > 2.5f && _turns < MaxTurns)
                    {
                        _turns++;
                        StartBack(pos, true, "not moving with the throttle on, backing up for turn " + _turns + " (" + Pose() + ")");
                        break;
                    }
                    float remaining = Remaining(pos);
                    Drive(car, v, _dir * (remaining < 3f || (onAxis && along < 3f) ? CreepKmh : ApproachKmh), dt);
                    Steer(car, Pursuit(pos, fwd, _dir), dt);
                    break;
                }
                case Phase.Back:
                {
                    bool aligned = Mathf.Abs(lat) < OnAxisLateral && Mathf.Abs(heading) < OnAxisHeading;
                    bool far = _turning ? Flat(pos - _backFrom).magnitude >= TurnBack : along >= BackTo && (aligned || along >= BackTo + AlignExtra);
                    if (far || _phaseTime > 9f || Blocked(car, -travel, Mathf.Max(0f, -vIn), out _, out _))
                    {
                        Path.Clear();
                        string how = "";
                        if (!_target.Reverse || !ArcFrom(pos, fwd, out how))
                        {
                            if (_target.Reverse) how = "; " + how + ", lining up on the axis";
                            Path.Add(_target.Centre - into * (_target.Reverse ? ReverseLineUp : FarOut));
                            Path.Add(_target.Centre);
                        }
                        else how = "; " + how;
                        _seg = 0;
                        SetPhase(Phase.Approach, (far ? "backed out" : "stopped backing") + ", " + Pose() + how);
                        break;
                    }
                    Drive(car, v, -_dir * ReverseKmh, dt);
                    // Turning: the wheels the other way round, so going the other way swings the leading end
                    // towards the path.
                    Steer(car, _turning ? -Pursuit(pos, fwd, _dir) : Pursuit(pos, fwd, -_dir), dt);
                    break;
                }
                case Phase.Settle:
                {
                    Steer(car, 0f, dt);
                    car.throttleOverride = Mathf.Abs(v) > 0.5f ? -Mathf.Sign(v) * 0.6f : 0f;
                    _stillFor = Mathf.Abs(v) < 0.3f ? _stillFor + dt : 0f;
                    if (_stillFor > 0.3f || _phaseTime > 4f) Finish(true, Pose() + (_tries > 0 ? ", " + (_tries + 1) + " tries" : ""));
                    break;
                }
            }
        }

        /// <summary>Counts how often in a row the same thing stopped the car; the distance is not part of it.</summary>
        private static int Hit(string what)
        {
            string key = Key(what);
            _sameHits = key == _lastHit ? _sameHits + 1 : 1;
            _lastHit = key;
            return _sameHits;
        }

        private static string Key(string what)
        {
            int cut = what.LastIndexOf(' ');
            cut = cut > 0 ? what.LastIndexOf(' ', cut - 1) : -1;
            return cut > 0 ? what.Substring(0, cut) : what;
        }

        /// <summary>
        /// The same parked car or wall keeps the car out of this spot: a narrow spot or a neighbour over the line.
        /// Takes the next free spot near it, or the kerb where the car stands, instead of turning on the spot forever.
        /// </summary>
        private static void Retarget(LandVehicle car, string what)
        {
            ParkTarget old = _target;
            if (old.Spot != null) Skip.Add(old.Spot.Pointer);
            if (Skip.Count > MaxSpots)
            {
                Finish(false, Key(what) + " keeps the car out, and " + MaxSpots + " spots were tried");
                return;
            }
            ParkTarget next = Find(car, old.Centre, out string none);
            if (next == null)
            {
                Finish(false, Key(what) + " keeps the car out, no other spot (" + none + ")");
                return;
            }
            car.throttleOverride = 0f;
            bool close = next.Lot == old.Lot && Flat(next.Centre - car.transform.position).magnitude <= RetargetInPlace;
            if (close)
            {
                Core.Log.Msg("[Park] giving up " + old.Label + ": " + Key(what) + " keeps the car out; trying " + next.Label + " from here.");
                Begin(car, next, _done);
                return;
            }
            Core.Log.Msg("[Park] giving up " + old.Label + ": " + Key(what) + " keeps the car out; driving to " + next.Label + ".");
            Next = next;
            Finish(false, "driving on to " + next.Label);
        }

        private static void StartBack(Vector3 pos, bool turning, string why)
        {
            _turning = turning;
            _backFrom = pos;
            if (!turning)
            {
                // The way out along the spot's axis, followed by pursuit like any other path.
                Path.Clear();
                Path.Add(_target.Centre);
                Path.Add(_target.Centre - _target.Into * (BackTo + AlignExtra + 10f));
                _seg = 0;
            }
            SetPhase(Phase.Back, why);
        }

        private static string Pose()
        {
            if (_car == null || _target == null) return "-";
            Vector3 pos = _car.transform.position;
            float along = Vector3.Dot(Flat(_target.Centre - pos), _target.Into);
            float lat = Vector3.Dot(Flat(pos - _target.Centre), Vector3.Cross(Vector3.up, _target.Into));
            float heading = Vector3.SignedAngle(_target.Heading, Flat(_car.transform.forward), Vector3.up);
            return "short " + along.ToString("F2") + " m, aside " + lat.ToString("F2") + " m, turned " + heading.ToString("F0") + " deg";
        }

        private static void Hold(LandVehicle car, float v, float dt, string what)
        {
            // The distance at the end of what changes every frame; the thing itself is what gets logged once.
            string key = Key(what);
            if (_blockedFor == 0f || key != _blockedBy) Core.Log.Msg("[Park] waiting: " + what + " in the way.");
            _blockedBy = key;
            _blockedFor += dt;
            car.throttleOverride = Mathf.Abs(v) > 0.5f ? -Mathf.Sign(v) * 0.8f : 0f;
        }

        /// <summary>Throttle towards a signed speed. Below 4 km/h vanilla drives a negative throttle backwards, above
        /// it brakes with it, so the same proportional rule serves both.</summary>
        private static void Drive(LandVehicle car, float v, float targetKmh, float dt)
        {
            float t = Mathf.Clamp((targetKmh - v) * 0.15f, -1f, 1f);
            if (targetKmh > 0f && v < targetKmh * 0.5f) t = Mathf.Max(t, 0.3f);
            if (targetKmh < 0f && v > targetKmh * 0.5f) t = Mathf.Min(t, -0.3f);
            car.throttleOverride = t;
        }

        private static void Steer(LandVehicle car, float angleDeg, float dt)
        {
            float max = Mathf.Max(1f, car.ActualMaxSteeringAngle);
            _steer = Mathf.MoveTowards(_steer, Mathf.Clamp(angleDeg / max, -1f, 1f), 3f * dt);
            car.steerOverride = _steer;
        }

        /// <summary>
        /// Pure pursuit from the rear axle: the steering angle in degrees that arcs onto the aim point, driving
        /// forwards (<paramref name="dir"/> +1) or backwards (-1). The car turns about its rear axle either way, so
        /// backing up uses the same arc measured against the backward direction, with the steering mirrored.
        /// </summary>
        private static float Pursuit(Vector3 pos, Vector3 fwd, float dir)
        {
            Vector3 rear = pos - fwd * (Wheelbase * 0.5f);
            Vector3 travel = fwd * dir;
            Vector3 aim = AimPoint(pos);
            Vector3 d = Flat(aim - rear);
            Vector3 right = Vector3.Cross(Vector3.up, travel);
            float x = Vector3.Dot(d, right), z = Vector3.Dot(d, travel);
            // Aim point behind the way the car moves: full lock towards its side, pursuit alone would barely turn.
            if (z < 0f) return dir * (x < 0f ? -1f : 1f) * 45f;
            float l2 = Mathf.Max(0.5f, x * x + z * z);
            float curvature = 2f * x / l2;
            return dir * Mathf.Atan(curvature * Wheelbase) * Mathf.Rad2Deg;
        }

        /// <summary>The point <see cref="Lookahead"/> metres on along the path from the car's closest point on it,
        /// continued straight past the end so the car keeps aiming down the spot's axis.</summary>
        private static Vector3 AimPoint(Vector3 pos)
        {
            Vector3 p = Flat3(pos, 0f);
            float best = float.MaxValue;
            float bestT = 0f;
            int bestSeg = _seg;
            for (int i = _seg; i + 1 < Path.Count; i++)
            {
                Vector3 a = Flat3(Path[i], 0f), b = Flat3(Path[i + 1], 0f);
                Vector3 ab = b - a;
                float t = ab.sqrMagnitude > 1e-4f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                float d = (a + ab * t - p).sqrMagnitude;
                if (d < best) { best = d; bestSeg = i; bestT = t; }
            }
            _seg = bestSeg;
            float left = Lookahead;
            if (_phase == Phase.Approach && _target.Reverse && _seg >= Path.Count - 2)
                left = Mathf.Clamp(Vector3.Dot(Flat(_target.Centre - pos), _target.Into) * 0.45f, FinalLookahead, Lookahead);
            Vector3 at = Vector3.Lerp(Path[_seg], Path[_seg + 1], bestT);
            for (int i = _seg; i + 1 < Path.Count; i++)
            {
                Vector3 to = Path[i + 1];
                float len = Flat(to - at).magnitude;
                if (len >= left) return at + Flat(to - at).normalized * left;
                left -= len;
                at = to;
            }
            Vector3 lastDir = Flat(Path[Path.Count - 1] - Path[Path.Count - 2]).normalized;
            return at + lastDir * left;
        }

        private static float Remaining(Vector3 pos)
        {
            float total = Flat(Path[Mathf.Min(_seg + 1, Path.Count - 1)] - pos).magnitude;
            for (int i = _seg + 1; i + 1 < Path.Count; i++) total += Flat(Path[i + 1] - Path[i]).magnitude;
            return total;
        }

        /// <summary>
        /// Something within stopping distance in <paramref name="dir"/>, from 0.25 m above the ground up: a kerb stone
        /// is driven over, a post or a bumper is not. <paramref name="moving"/> is true for a car that drives or has
        /// someone in it, an NPC or a player, which is worth waiting for; a parked car or a plant box is not.
        /// </summary>
        private static bool Blocked(LandVehicle car, Vector3 dir, float kmh, out string what, out bool moving)
        {
            what = string.Empty;
            moving = false;
            // The whole body on its current steering, corners included: in a full-lock turn the front corner swings
            // out and the inner side cuts in, where a straight cast from the bumper saw no hydrant or post.
            float sign = Vector3.Dot(dir, car.transform.forward) >= 0f ? 1f : -1f;
            float reach = 0.6f + kmh / 3.6f * 0.6f;
            if (!Footprint.Sweep(car, sign, reach, out float at, out int side, out Collider c, out string name)) return false;
            what = name + " " + at.ToString("F1") + " m" + (side < 0 ? " at the left" : side > 0 ? " at the right" : "");
            LandVehicle other = c.GetComponentInParent<LandVehicle>();
            if (other != null) moving = other.IsOccupied || (other.Rb != null && other.Rb.velocity.sqrMagnitude > 0.25f);
            else moving = c.attachedRigidbody != null
                && (c.GetComponentInParent<Il2CppScheduleOne.NPCs.NPC>() != null
                    || c.GetComponentInParent<Il2CppScheduleOne.PlayerScripts.Player>() != null);
            return true;
        }

        /// <summary>The ground height under the car, skipping the car's own colliders.</summary>
        internal static float Ground(LandVehicle car, Vector3 pos)
        {
            float best = pos.y - 0.5f, bestD = float.MaxValue;
            var hits = Physics.RaycastAll(pos + Vector3.up, Vector3.down, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.transform.IsChildOf(car.transform) || c.attachedRigidbody != null) continue;
                if (hits[i].distance < bestD) { bestD = hits[i].distance; best = hits[i].point.y; }
            }
            return best;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
        private static Vector3 Flat3(Vector3 v, float y) => new Vector3(v.x, y, v.z);

        // ---- host: which spot each Yesla holds ---------------------------------------------------------------------

        private static readonly Dictionary<IntPtr, (LandVehicle car, ParkingSpot spot, string label)> Held =
            new Dictionary<IntPtr, (LandVehicle, ParkingSpot, string)>();
        private static float _heldCheck;

        /// <summary>Host: marks the spot as taken by <paramref name="car"/>, so vanilla parks no NPC car into it.</summary>
        internal static void HostTake(LandVehicle car, string lotGuid, string indexText)
        {
            if (!int.TryParse(indexText, out int index)) { Core.Log.Warning("[Park] unreadable spot index '" + indexText + "'."); return; }
            ParkingLot lot = null;
            foreach (ParkingLot l in AllLots()) if (l != null && l.GUID.ToString() == lotGuid) { lot = l; break; }
            if (lot == null) { Core.Log.Warning("[Park] no parking lot " + lotGuid + " on the host."); return; }
            if (index < 0 || index >= lot.ParkingSpots.Count) { Core.Log.Warning("[Park] " + lot.gameObject.name + " has no spot #" + index + "."); return; }
            ParkingSpot spot = lot.ParkingSpots[index];
            string label = lot.gameObject.name + " #" + index;
            if (spot.OccupantVehicle != null && spot.OccupantVehicle.Pointer != car.Pointer)
            {
                Core.Log.Warning("[Park] " + label + " is already held by " + spot.OccupantVehicle.VehicleName + " - not marking it.");
                return;
            }
            spot.SetOccupant(car);
            Held[car.Pointer] = (car, spot, label);
            Core.Log.Msg("[Park] host: " + label + " held by " + car.VehicleName + ".");
        }

        /// <summary>Host, every second: frees the spots whose car has driven off or is gone.</summary>
        internal static void HostTick()
        {
            if (Held.Count == 0) return;
            _heldCheck -= Time.deltaTime;
            if (_heldCheck > 0f) return;
            _heldCheck = 1f;
            List<IntPtr> gone = null;
            foreach (var kv in Held)
            {
                var (car, spot, label) = kv.Value;
                bool carGone = car == null || car.gameObject == null;
                if (!carGone && spot != null && spot.AlignmentPoint != null
                    && Flat(car.transform.position - spot.AlignmentPoint.position).magnitude <= LeaveDistance + car.BoundingBoxDimensions.z * 0.5f)
                    continue;
                if (spot != null && (spot.OccupantVehicle == null || carGone || spot.OccupantVehicle.Pointer == car.Pointer)) spot.SetOccupant(null);
                Core.Log.Msg("[Park] host: " + label + " free again (" + (carGone ? "the car is gone" : "the car drove off") + ").");
                (gone ??= new List<IntPtr>()).Add(kv.Key);
            }
            if (gone != null) foreach (IntPtr k in gone) Held.Remove(k);
        }

        internal static string DescribeLots(Vector3 near, float radius)
        {
            var parts = new List<string>();
            foreach (ParkingLot lot in AllLots())
            {
                if (lot == null || lot.ParkingSpots == null || lot.ParkingSpots.Count == 0) continue;
                Vector3 at = lot.ParkingSpots[0] != null && lot.ParkingSpots[0].AlignmentPoint != null ? lot.ParkingSpots[0].AlignmentPoint.position : lot.transform.position;
                float d = Flat(at - near).magnitude;
                if (d > radius) continue;
                int free = 0;
                for (int i = 0; i < lot.ParkingSpots.Count; i++)
                    if (lot.ParkingSpots[i] != null && lot.ParkingSpots[i].IsUsable && lot.ParkingSpots[i].OccupantVehicle == null) free++;
                parts.Add(lot.gameObject.name + " " + d.ToString("F0") + " m: " + free + "/" + lot.ParkingSpots.Count + " free, entry " + (lot.EntryPoint != null ? lot.EntryPoint.position.ToString("F0") : "none"));
            }
            return parts.Count == 0 ? "no parking lot within " + radius.ToString("F0") + " m" : string.Join("; ", parts);
        }

        internal static void Reset()
        {
            _car = null;
            _target = null;
            _done = null;
            Lots.Clear();
            _lotsFound = false;
            Held.Clear();
        }
    }
}
