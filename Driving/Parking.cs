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
        /// The line the car drives past the spot on: its rear axle this far out from the spot's centre. A spot runs
        /// about 2.4 m from its centre to its front edge and the car is 0.95 m wide to its side, so the side passes
        /// about half a metre from the spots' front edge: the closer it passes, the more of the aisle is left to swing
        /// out into.
        /// </summary>
        private const float PassLine = 3.9f;
        /// <summary>The pass lines tried, closest first: the wider one when a fence across a short aisle blocks the
        /// bigger swing the close line needs.</summary>
        private static readonly float[] PassLines = { PassLine, 4.4f };
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
        /// <summary>
        /// Further than this from the stop point, a post or a hydrant ahead is steered around instead of starting the
        /// reverse from where the car stands: no arc reaches the spot from there.
        /// </summary>
        private const float StageSteerAround = 4f;
        /// <summary>How far the aim point moves away from the side a post was hit on, and how fast.</summary>
        private const float StageNudge = 1.2f;
        private const float StageNudgeRate = 2f;
        /// <summary>
        /// A post right at the bumper is not passed by steering alone: the car backs this far with the wheels turned,
        /// which swings the nose away from it, at most <see cref="StageBackOffs"/> times before the spot is given up.
        /// </summary>
        private const float StageBackOff = 2f;
        private const int StageBackOffs = 3;
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
                if (c == null || c.transform.IsChildOf(car.transform) || Footprint.Aboard(c, car)) continue;
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
            var hits = Physics.OverlapBox(mid, new Vector3(car.BoundingBoxDimensions.x * 0.5f + CorridorMargin, 0.45f, len * 0.5f), Quaternion.LookRotation(run / len),
                                          Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i];
                if (c == null || c.transform.IsChildOf(car.transform) || Footprint.Aboard(c, car)) continue;
                Transform up = c.transform.parent;
                what = (up != null ? up.name + "/" : "") + c.name;
                return false;
            }
            return true;
        }

        /// <summary>Room kept round the car's outline on a planned turn: the car follows the line within a few
        /// decimetres, and the outer front corner is the part that meets a fence.</summary>
        private const float OutlineMargin = 0.25f;
        /// <summary>The backing arc is checked up to this angle; past it the car is between the spot's neighbours,
        /// which <see cref="Free"/> and the spot's own check cover.</summary>
        private const float OutlineUpTo = 60f;

        /// <summary>
        /// Whether the car's whole outline, at <paramref name="centre"/> facing <paramref name="facing"/>, is clear at
        /// bumper to roof height. On a turn the outer front corner swings up to a metre wider than a strip between
        /// the car's centres, so the strip passes a fence that the corner hits.
        /// </summary>
        private static bool Outline(LandVehicle car, Vector3 centre, Vector3 facing, out string what)
        {
            what = string.Empty;
            Vector3 dims = car.BoundingBoxDimensions;
            float ground = Ground(car, centre + Vector3.up);
            Vector3 middle = new Vector3(centre.x, ground + 0.95f, centre.z);
            Vector3 half = new Vector3(dims.x * 0.5f + OutlineMargin, 0.45f, dims.z * 0.5f + OutlineMargin);
            var hits = Physics.OverlapBox(middle, half, Quaternion.LookRotation(Flat(facing).normalized), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i];
                if (c == null || c.transform.IsChildOf(car.transform) || Footprint.Aboard(c, car)) continue;
                Transform up = c.transform.parent;
                what = (up != null ? up.name + "/" : "") + c.name + " at the car's outline";
                return false;
            }
            return true;
        }

        /// <summary>Room left beside the car on a planned run: pursuit cuts the corners of the line a little.</summary>
        private const float CorridorMargin = 0.15f;
        /// <summary>A step in the ground of more than this between two samples is a kerb; a lowered kerb or a ramp is less.</summary>
        private const float KerbStep = 0.12f;
        private const float KerbSample = 0.25f;

        /// <summary>
        /// Whether a raised kerb crosses the wheel tracks from <paramref name="from"/> to <paramref name="to"/>. The
        /// ground is felt under both tracks every <see cref="KerbSample"/> metres; the lowered kerb at a lot's
        /// driveway rises in small steps and passes, a kerb stone along the pavement does not. Only static ground
        /// counts, so cars and people are left to <see cref="Corridor"/>.
        /// </summary>
        private static bool KerbOnRun(LandVehicle car, Vector3 from, Vector3 to, out string what)
        {
            what = string.Empty;
            Vector3 run = Flat(to - from);
            float len = run.magnitude;
            if (len < 0.1f) return false;
            Vector3 dir = run / len;
            Vector3 side = Vector3.Cross(Vector3.up, dir) * Mathf.Max(0.3f, car.BoundingBoxDimensions.x * 0.5f - 0.25f);
            float top = Mathf.Max(Ground(car, from + Vector3.up), Ground(car, to + Vector3.up)) + 1.5f;
            int n = Mathf.CeilToInt(len / KerbSample);
            for (int track = -1; track <= 1; track += 2)
            {
                float was = float.NaN;
                for (int k = 0; k <= n; k++)
                {
                    Vector3 p = from + dir * (len * k / n) + side * track;
                    if (!GroundAt(car, new Vector3(p.x, top, p.z), out float h)) { was = float.NaN; continue; }
                    if (!float.IsNaN(was) && Mathf.Abs(h - was) > KerbStep)
                    {
                        what = "a kerb (" + Mathf.Abs(h - was).ToString("F2") + " m step) at the " + (track < 0 ? "left" : "right") + " " + (len * k / n).ToString("F1") + " m";
                        return true;
                    }
                    was = h;
                }
            }
            return false;
        }

        /// <summary>The highest static ground straight down from <paramref name="top"/>, skipping the car.</summary>
        private static bool GroundAt(LandVehicle car, Vector3 top, out float h)
        {
            h = float.MinValue;
            var hits = Physics.RaycastAll(top, Vector3.down, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.attachedRigidbody != null || c.transform.IsChildOf(car.transform)) continue;
                if (hits[i].point.y > h) h = hits[i].point.y;
            }
            return h > float.MinValue;
        }

        /// <summary>The road AI hands over this close to the spot, as soon as the run into the aisle is clear.</summary>
        internal const float HandOver = 25f;
        /// <summary>The road AI slows to this before it hands over, and hands over no faster: the turn into a lot
        /// passes hydrants and posts close by, and parking starts at walking pace.</summary>
        internal const float HandOverKmh = 12f;
        /// <summary>The car backs in only when it can stand this far out on the spot's axis; a shallower aisle has no
        /// room to swing out, and the car parks nose first along it.</summary>
        private const float MinReverseRoom = 6f;
        /// <summary>The aisle in front of the spot, as far out as the car stops to back in.</summary>
        private const float AisleOut = 4.5f;
        private static string _reachBlock = string.Empty;

        /// <summary>
        /// Whether the car is close enough to the spot to take over from the road AI, with nothing between it and
        /// the aisle in front of the spot at bumper to roof height and no raised kerb on the way: the road AI keeps the
        /// car until the straight run goes through the lot's lowered kerb rather than over the pavement. The parking
        /// manoeuvre then starts with room to line up instead of where the road AI stops, a few metres from the spot.
        /// </summary>
        internal static bool InReach(LandVehicle car, ParkTarget t, out string why)
        {
            why = string.Empty;
            Vector3 pos = car.transform.position;
            if (Flat(t.Centre - pos).magnitude > HandOver) return false;
            if (Mathf.Abs(car.Speed_Kmh) > HandOverKmh + 2f) return false;
            Vector3 front = t.Centre - t.Into * AisleOut;
            float d = Flat(front - pos).magnitude;
            if (d < 1f) { why = "at the aisle in front of it"; return true; }
            Vector3 nose = pos + Flat(front - pos) / d * (car.BoundingBoxDimensions.z * 0.5f);
            if (!Corridor(car, nose, front, out string what) || KerbOnRun(car, nose, front, out what))
            {
                if (what != _reachBlock) Core.Log.Msg("[Park] " + t.Label + " is " + Flat(t.Centre - pos).magnitude.ToString("F0") + " m away, " + what + " stands between the car and its aisle - the road AI drives on.");
                _reachBlock = what;
                return false;
            }
            // Backing in, the car drives the stage line from where it stands, not the straight run: that line has to
            // be clear too, or a hydrant beside the driveway and the kerb next to it end up under the swing.
            bool noseIn = t.Reverse && Config.Preferences.ParkDirection == "auto" && NoseInArc(car, t, Flat3(pos, t.Centre.y), car.transform.forward, null, out _);
            if (t.Reverse && !noseIn && !PlanReverse(car, t, Flat3(pos, t.Centre.y), true, out string stage)
                && !NoseInLane(car, t, Flat3(pos, t.Centre.y), null, out _))
            {
                if (stage != _reachBlock) Core.Log.Msg("[Park] " + t.Label + " is " + Flat(t.Centre - pos).magnitude.ToString("F0") + " m away, the line to back in from here is blocked by " + stage + " - the road AI drives on.");
                _reachBlock = stage;
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
        /// <summary>How long the run to the stop point may take: its length at approach speed, half again, plus slack.</summary>
        private static float _stageBudget = 15f;
        /// <summary>Metres to the left (negative: right) of the stage path the car aims, away from a post.</summary>
        private static float _nudge;
        private static bool _stageBacking;
        private static int _stageBackOffs;
        private static string _blockedBy = string.Empty;

        internal static bool Active => _car != null;

        /// <summary>The spot the running manoeuvre heads for, or null.</summary>
        internal static ParkTarget Current => _target;
        /// <summary>The car stands in the spot and is braking to a halt.</summary>
        internal static bool Settling => _car != null && _phase == Phase.Settle;
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
            Vector3 at = Flat3(car.transform.position, target.Centre.y);
            List<Vector3> arc = null;
            if (!target.Kerb && (mode == "auto" || mode == "forward"))
            {
                arc = new List<Vector3>();
                if (NoseInArc(car, target, at, car.transform.forward, arc, out string arcHow))
                {
                    if (reverse) Core.Log.Msg("[Park] auto: " + arcHow + " - parking nose first.");
                    reverse = false;
                }
                else arc = null;
            }
            if (arc == null && reverse && mode == "auto" && HeadOn(car, target, out string headOn))
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
            _stageBackOffs = 0;
            _stageBacking = false;
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
                if (PlanReverse(car, target, pos, false, out _))
                {
                    Path.AddRange(StagePath);
                    float run = 0f;
                    for (int i = 1; i < Path.Count; i++) run += Flat(Path[i] - Path[i - 1]).magnitude;
                    _stageBudget = 8f + run / (ApproachKmh / 3.6f) * 1.5f;
                    SetPhase(Phase.Stage, "past " + towards + (_swing > 1f ? ", swinging out " + _swing.ToString("F0") + " deg" : ", square to the aisle")
                             + " to stop " + Flat(_stage - target.Centre).magnitude.ToString("F1") + " m from it, then back on a " + _arc.ToString("F1") + " m arc");
                    return;
                }
                Core.Log.Warning("[Park] no room in the aisle to back into " + target.Label + " - parking nose first.");
                target.Heading = -target.Heading;
                target.Reverse = false;
                _dir = 1f;
            }
            string line = "one forward arc";
            if (arc == null && !target.Kerb)
            {
                var lane = new List<Vector3>();
                if (NoseInLane(car, target, pos, lane, out string laneHow)) { arc = lane; line = "the lane along the aisle"; Core.Log.Msg("[Park] " + target.Label + ": " + laneHow + "."); }
                else Core.Log.Msg("[Park] " + target.Label + ": " + laneHow + " - lining up on its axis.");
            }
            if (arc != null)
            {
                Path.Clear();
                Path.AddRange(arc);
                SetPhase(Phase.Approach, "towards " + towards + " on " + line);
                return;
            }
            if (target.Kerb) Path.Add(pos + target.Heading * 3f + Vector3.Cross(Vector3.up, target.Heading) * (KerbShift * 0.5f));
            else Path.Add(target.Centre - target.Heading * AxisRoom(car, target, FarOut));
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

        /// <summary>A forward arc may start after this much straight ahead, and leaves at least this much straight into the spot.</summary>
        private const float MaxStraightOn = 6f, NoseStraight = 1f;

        /// <summary>
        /// Nose first in one go from the car's real pose: d metres straight on, one circle onto the spot's axis, then
        /// straight in. With u the way the car faces, n the way into the spot and theta the turn from u to n, the rear
        /// axle P ends at E with lateral(E) = lateral(P) - s*r*(1 - cos theta), s the side of the turn, so r =
        /// |lateral(P)| / (1 - cos theta) when the car is on the side it turns away from. A radius between full lock
        /// and <see cref="MaxArc"/>, at least <see cref="NoseStraight"/> metres straight at the end, and a clear
        /// run in the aisle make it; then a driver would simply drive in, and so does the car. Nearly square to the
        /// spot already, the arc is the straight line itself. <paramref name="path"/> takes the car centres when given.
        /// </summary>
        private static bool NoseInArc(LandVehicle car, ParkTarget t, Vector3 pos, Vector3 forward, List<Vector3> path, out string how)
        {
            how = string.Empty;
            Vector3 n = t.Into;
            Vector3 hr = Vector3.Cross(Vector3.up, n);
            Vector3 u = Flat(forward);
            if (u.sqrMagnitude < 1e-4f) return false;
            u.Normalize();
            float y = t.Centre.y;
            float theta = Vector3.SignedAngle(u, n, Vector3.up);
            float rMin = Wheelbase / Mathf.Tan(ParkLock * Mathf.Deg2Rad) * ArcSlack;
            float half = Wheelbase * 0.5f;
            float s = Mathf.Sign(theta);
            float bend = 1f - Mathf.Cos(theta * Mathf.Deg2Rad);
            for (float d = 0f; d <= MaxStraightOn; d += 0.5f)
            {
                Vector3 p = Flat3(pos - u * half + u * d, y);
                float lat = Vector3.Dot(p - t.Centre, hr);
                Vector3 c = default, e;
                float r = 0f;
                if (Mathf.Abs(theta) < HeadOnAngle * 0.4f)
                {
                    // Already square: straight in when the car is on the axis.
                    if (Mathf.Abs(lat) > HeadOnLateral * 0.5f) return false;
                    e = p;
                }
                else
                {
                    if (Mathf.Abs(theta) > 100f || Mathf.Sign(lat) != s || Mathf.Abs(lat) < 0.05f) continue;
                    r = Mathf.Abs(lat) / bend;
                    if (r < rMin || r > MaxArc) continue;
                    c = p + Vector3.Cross(Vector3.up, u) * (s * r);
                    e = c - hr * (s * r);
                }
                float straight = Vector3.Dot(t.Centre - (e + n * half), n);
                if (straight < NoseStraight) continue;

                var line = path ?? new List<Vector3>();
                line.Clear();
                line.Add(Flat3(pos, y));
                if (d > 0.1f) line.Add(Flat3(pos + u * d, y));
                if (r > 0f)
                {
                    int steps = Mathf.Max(3, Mathf.CeilToInt(Mathf.Abs(theta) / 10f));
                    for (int i = 1; i <= steps; i++)
                    {
                        Vector3 facing = Quaternion.AngleAxis(theta * i / steps, Vector3.up) * u;
                        Vector3 rear = c - Vector3.Cross(Vector3.up, facing) * (s * r);
                        line.Add(Flat3(rear + facing * half, y));
                    }
                }
                line.Add(t.Centre);
                // The aisle part must be clear; the last stretch is the spot itself, which Free already checked.
                bool clear = true;
                for (int i = 1; i < line.Count - 1 && clear; i++)
                    clear = Corridor(car, line[i - 1], line[i], out string what) && !KerbOnRun(car, line[i - 1], line[i], out what);
                if (!clear) continue;
                how = r > 0f
                    ? "one forward arc fits: " + d.ToString("F1") + " m straight on, radius " + r.ToString("F1") + " m, " + Mathf.Abs(theta).ToString("F0") + " deg, " + straight.ToString("F1") + " m straight in"
                    : "square to " + t.Label + ", " + Mathf.Abs(lat).ToString("F1") + " m off its axis";
                return true;
            }
            return false;
        }

        /// <summary>The lane may take a turn this much wider than full lock; the car runs straight this far before it turns.</summary>
        private const float LaneArcSpare = 2f, LaneRunIn = 3f;

        /// <summary>
        /// Nose first from the lot's entry along the aisle, the way a driver takes a spot where the aisle is too
        /// narrow to swing out and back in: straight along a lane, a quarter circle of radius r onto the spot's axis,
        /// then <see cref="NoseStraight"/> metres straight in. With a the way along the aisle and n the way into the
        /// spot, the lane runs w = r + wheelbase/2 + NoseStraight out from the spot's centre, and the rear axle turns
        /// round C = centre - n·(w - r) - a·r. The tightest lane that is clear wins, so the car keeps away from the
        /// far side of the aisle; <paramref name="path"/> takes the car centres when given.
        /// </summary>
        private static bool NoseInLane(LandVehicle car, ParkTarget t, Vector3 pos, List<Vector3> path, out string how)
        {
            how = string.Empty;
            Vector3 n = t.Into;
            Vector3 side = Vector3.Cross(Vector3.up, n);
            float y = t.Centre.y;
            float half = Wheelbase * 0.5f;
            float rMin = Wheelbase / Mathf.Tan(ParkLock * Mathf.Deg2Rad) * ArcSlack;
            Vector3 from = t.Lot != null && t.Lot.EntryPoint != null ? t.Lot.EntryPoint.position : pos;
            float across = Vector3.Dot(Flat(t.Centre - from), side);
            Vector3 facingNow = Flat(car.transform.forward).normalized;
            string blocked = null;
            // The car comes along the aisle from the entry; right in front of the spot, either way may do.
            int ways = Mathf.Abs(across) < 1f ? 2 : 1;
            for (int way = 0; way < ways; way++)
            {
                Vector3 a = (across >= 0f) == (way == 0) ? side : -side;
                if (Vector3.Dot(facingNow, a) < -0.5f) { blocked ??= "the car faces the other way along the aisle"; continue; }
                for (float r = rMin + LaneArcSpare; r >= rMin - 0.01f; r -= 0.5f)
                {
                    float w = r + half + NoseStraight;
                    Vector3 cross = t.Centre - n * w;
                    Vector3 c = cross - a * r + n * r;
                    Vector3 arcStart = Flat3(cross - a * r + a * half, y);
                    if (Vector3.Dot(Flat(pos - arcStart), a) > -0.5f) { blocked ??= "the car is already past the turn"; continue; }
                    float s0 = Mathf.Min(Vector3.Dot(Flat(from - t.Centre), a), -r + half - LaneRunIn);
                    Vector3 runIn = Flat3(cross + a * s0, y);
                    var line = path ?? new List<Vector3>();
                    line.Clear();
                    line.Add(Flat3(pos, y));
                    if (Vector3.Dot(Flat(pos - runIn), a) < 0f) line.Add(runIn);
                    line.Add(arcStart);
                    for (int i = 1; i <= 9; i++)
                    {
                        float phi = 10f * i * Mathf.Deg2Rad;
                        Vector3 rear = c - n * (r * Mathf.Cos(phi)) + a * (r * Mathf.Sin(phi));
                        line.Add(Flat3(rear + (a * Mathf.Cos(phi) + n * Mathf.Sin(phi)) * half, y));
                    }
                    line.Add(t.Centre);
                    bool clear = true;
                    string what = string.Empty;
                    for (int i = 1; i < line.Count - 1 && clear; i++)
                        clear = Corridor(car, line[i - 1], line[i], out what) && !KerbOnRun(car, line[i - 1], line[i], out what);
                    for (int i = 0; i * 10f < OutlineUpTo && clear; i++)
                    {
                        // The turn's first part, out in the aisle; its last part is the spot's mouth.
                        float phi = 10f * i * Mathf.Deg2Rad;
                        Vector3 rear = c - n * (r * Mathf.Cos(phi)) + a * (r * Mathf.Sin(phi));
                        Vector3 f = a * Mathf.Cos(phi) + n * Mathf.Sin(phi);
                        clear = Outline(car, Flat3(rear + f * half, y), f, out what);
                    }
                    if (!clear) { blocked ??= what; continue; }
                    how = "no room to back in, nose first along the aisle: a " + r.ToString("F1") + " m turn from a lane " + w.ToString("F1") + " m out";
                    return true;
                }
            }
            how = "no lane along the aisle (" + (blocked ?? "nothing tried") + ")";
            return false;
        }

        /// <summary>
        /// How far out on the spot's axis the car can line up, up to <paramref name="want"/>: a wall across a
        /// narrow aisle ends the run before it, and a line-up point behind that wall drives the car into it.
        /// </summary>
        private static float AxisRoom(LandVehicle car, ParkTarget t, float want, bool quiet = false)
        {
            Vector3 n = t.Into;
            float tail = car.BoundingBoxDimensions.z * 0.5f;
            for (float d = want; d > 2f; d -= 0.5f)
                if (Corridor(car, t.Centre - n * 2f, t.Centre - n * (d + tail), out _)) return d;
            if (!quiet) Core.Log.Msg("[Park] " + t.Label + ": less than 2 m clear in front of it - lining up right at the spot.");
            return 2f;
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
        private static bool PlanReverse(LandVehicle car, ParkTarget target, Vector3 pos, bool quiet, out string firstBlock)
        {
            firstBlock = string.Empty;
            float room = AxisRoom(car, target, MinReverseRoom + 1f, true);
            if (room < MinReverseRoom)
            {
                firstBlock = "an aisle only " + room.ToString("F1") + " m deep in front of it (" + MinReverseRoom.ToString("F0") + " m needed to swing out)";
                if (!quiet) Core.Log.Msg("[Park] backing into " + target.Label + ": " + firstBlock + ".");
                return false;
            }
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
                for (int k = 0; k < (SwingRadii.Length + 1) * PassLines.Length; k++)
                {
                    int ri = k / PassLines.Length;
                    float r = ri < SwingRadii.Length ? SwingRadii[ri] : rMin;
                    float line = PassLines[k % PassLines.Length];
                    float cos = Mathf.Min(1f, (line + r - eh) / (2f * r));
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
                    // From where the car stands to the start of the line, then the line itself.
                    bool clear = Corridor(car, pos, StagePath[0], out what) && !KerbOnRun(car, pos, StagePath[0], out what);
                    for (int i = 1; i < StagePath.Count && clear; i++)
                        clear = Corridor(car, StagePath[i - 1], StagePath[i], out what) && !KerbOnRun(car, StagePath[i - 1], StagePath[i], out what);
                    // The strips miss the outer front corner on the turn: the outline at each pose of the swing and of
                    // the backing arc up to the spot's mouth.
                    for (int i = 0; i <= steps && clear; i++)
                    {
                        float t = psi * i / steps;
                        Vector3 rear = startRear + a * (r * Mathf.Sin(t)) + h * (r * (1f - Mathf.Cos(t)));
                        Vector3 f = a * Mathf.Cos(t) + h * Mathf.Sin(t);
                        clear = Outline(car, Flat3(rear + f * (Wheelbase * 0.5f), y), f, out what);
                    }
                    for (float deg = psi * Mathf.Rad2Deg; deg < OutlineUpTo && clear; deg += 10f)
                    {
                        float phi = deg * Mathf.Deg2Rad;
                        Vector3 rear = c + (h * Mathf.Cos(phi) - a * Mathf.Sin(phi)) * r;
                        Vector3 f = h * Mathf.Sin(phi) + a * Mathf.Cos(phi);
                        clear = Outline(car, Flat3(rear + f * (Wheelbase * 0.5f), y), f, out what);
                    }
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
                    if (blocked != null && !quiet) Core.Log.Msg("[Park] backing into " + target.Label + ": a wider swing or a closer pass is blocked by " + blocked + ", taking a " + r.ToString("F1") + " m arc passing " + line.ToString("F1") + " m out.");
                    return true;
                }
                if (firstBlock.Length == 0 && blocked != null) firstBlock = blocked;
                if (!quiet) Core.Log.Msg("[Park] backing into " + target.Label + (pass == 0 ? " from this side" : " from the other side") + ": every stop point is blocked, first by " + blocked + ".");
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
            _car.handbrakeOverride = false;
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
                car.handbrakeOverride = false;
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
            _nudge = 0f;
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
                    if (_stageBacking)
                    {
                        float backed = Flat(pos - _backFrom).magnitude;
                        bool behind = Blocked(car, -fwd, Mathf.Max(0f, -v), out string back, out _, out _);
                        if (backed < StageBackOff && _blockedFor < 4f && !behind)
                        {
                            // The clock stands while the car makes room; the stop point is no nearer.
                            _phaseTime -= dt;
                            _blockedFor += dt;
                            Drive(car, v, -ReverseKmh, dt);
                            Steer(car, -Pursuit(pos, fwd, 1f), dt);
                            break;
                        }
                        _stageBacking = false;
                        _blockedFor = 0f;
                        Core.Log.Msg("[Park] Stage: backed " + backed.ToString("F1") + " m" + (behind ? ", stopped by " + back : "") + ", aiming " + Mathf.Abs(_nudge).ToString("F1") + " m to the " + (_nudge > 0f ? "left" : "right") + " of the line.");
                    }
                    float left = Remaining(pos);
                    // At the stop point and at the swing's angle; still turning in from the road is not there yet,
                    // and the arc from the real pose takes up any distance driven past it.
                    float beyond = Vector3.Dot(Flat(pos - _stage), _stageFacing);
                    float offLine = Vector3.Dot(Flat(pos - _stage), Vector3.Cross(Vector3.up, _stageFacing));
                    bool past = beyond > -0.4f && Vector3.Dot(fwd, _stageFacing) > StageSquare && Mathf.Abs(offLine) < StageOffLine;
                    bool overshot = beyond > StageRunOut - 0.5f;
                    if (!past && !overshot && left >= 1f && _phaseTime > _stageBudget && Flat(_stage - pos).magnitude > StageSteerAround)
                    {
                        Retarget(car, "the way to the stop point " + Flat(_stage - pos).magnitude.ToString("F1") + " m");
                        break;
                    }
                    if (past || overshot || left < 1f || _phaseTime > _stageBudget)
                    {
                        StartIn(pos, past ? "stopped past the spot" : overshot || left < 1f ? "at the end of the aisle line, " + offLine.ToString("F1") + " m beside it" : "still not past the spot after " + _stageBudget.ToString("F0") + " s");
                        break;
                    }
                    if (Blocked(car, fwd, Mathf.Max(0f, v), out string ahead, out bool aheadMoving, out int aheadSide))
                    {
                        float toStop = Flat(_stage - pos).magnitude;
                        if (!aheadMoving && toStop <= StageSteerAround) { StartIn(pos, ahead + " ahead"); break; }
                        if (!aheadMoving)
                        {
                            // Still on the way to the stop point: the wheels turn away from the post while the car
                            // stands, and the next sweep runs on the new steering. A post that stays in the way gives
                            // the spot up rather than backing in from the wrong place.
                            if (aheadSide != 0) _nudge = aheadSide * StageNudge;
                            _phaseTime -= dt;
                            Hold(car, v, dt, ahead);
                            Steer(car, Pursuit(pos, fwd, 1f), dt);
                            if (_blockedFor < 1f) break;
                            if (_stageBackOffs >= StageBackOffs) { Retarget(car, ahead); break; }
                            _stageBackOffs++;
                            _stageBacking = true;
                            _backFrom = pos;
                            _blockedFor = 0f;
                            Core.Log.Msg("[Park] Stage: " + ahead + ", " + toStop.ToString("F1") + " m before the stop point - backing " + StageBackOff.ToString("F0") + " m to get round it (" + _stageBackOffs + " of " + StageBackOffs + ").");
                            break;
                        }
                        Hold(car, v, dt, ahead);
                        if (_blockedFor > BlockedGiveUp) Finish(false, "blocked by " + ahead);
                        break;
                    }
                    _blockedFor = 0f;
                    // Past the post the aim drifts back onto the stage line, so the stop point is reached on it.
                    _nudge = Mathf.MoveTowards(_nudge, 0f, StageNudgeRate * 0.25f * dt);
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
                        bool fair = Mathf.Abs(lat) <= GoodLateral * 2f && Mathf.Abs(heading) <= GoodHeading * 2f;
                        if (there && (_target.Kerb || (Mathf.Abs(lat) <= GoodLateral && Mathf.Abs(heading) <= GoodHeading) || (_tries >= MaxTries && fair)))
                        {
                            SetPhase(Phase.Settle, "in place, " + Pose());
                            break;
                        }
                        if (_tries >= MaxTries)
                        {
                            // Standing in the spot at an angle is not parked; another spot nearby may suit the car.
                            if (!_target.Kerb) { Retarget(car, "no straight way in after " + MaxTries + " tries (" + Pose() + ")"); break; }
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
                    if (Blocked(car, travel, Mathf.Max(0f, vIn), out string what, out bool moving, out _))
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
                    if (far || _phaseTime > 9f || Blocked(car, -travel, Mathf.Max(0f, -vIn), out _, out _, out _))
                    {
                        Path.Clear();
                        string how = "";
                        if (!_target.Reverse || !ArcFrom(pos, fwd, out how))
                        {
                            if (_target.Reverse) how = "; " + how + ", lining up on the axis";
                            Path.Add(_target.Centre - into * (_target.Reverse ? ReverseLineUp : AxisRoom(car, _target, FarOut)));
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
                    // No throttle against the motion: below 4 km/h vanilla drives that throttle the other way, and the
                    // car rocked back and forth. Vanilla also clears handbrakeOverride in its own LateUpdate, so the
                    // brake is applied to the body directly, as hard as a firm stop.
                    car.throttleOverride = 0f;
                    Brake(car, dt);
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
            // The distance at the end ("... 1.5 m") changes every frame; anything else is the thing itself.
            if (!what.EndsWith(" m")) return what;
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

        /// <summary>How hard the car stops in the spot, in m/s per second.</summary>
        private const float SettleBrake = 6f;

        private static void Brake(LandVehicle car, float dt)
        {
            Rigidbody rb = car.Rb;
            if (rb == null) { Core.Log.Warning("[Park] no rigidbody to brake - the car rolls out."); return; }
            Vector3 vel = rb.velocity;
            Vector3 flat = Flat(vel);
            flat = Vector3.MoveTowards(flat, Vector3.zero, SettleBrake * dt);
            rb.velocity = new Vector3(flat.x, vel.y, flat.z);
            rb.angularVelocity = Vector3.MoveTowards(rb.angularVelocity, Vector3.zero, SettleBrake * dt);
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
            Vector3 right = Vector3.Cross(Vector3.up, travel);
            if (_nudge != 0f) aim -= Vector3.Cross(Vector3.up, Flat(fwd)) * _nudge;
            Vector3 d = Flat(aim - rear);
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
        private static bool Blocked(LandVehicle car, Vector3 dir, float kmh, out string what, out bool moving, out int side)
        {
            what = string.Empty;
            moving = false;
            // The whole body on its current steering, corners included: in a full-lock turn the front corner swings
            // out and the inner side cuts in, where a straight cast from the bumper saw no hydrant or post.
            float sign = Vector3.Dot(dir, car.transform.forward) >= 0f ? 1f : -1f;
            float reach = 0.6f + kmh / 3.6f * 0.6f;
            if (!Footprint.Sweep(car, sign, reach, out float at, out side, out Collider c, out string name)) return false;
            what = name + (side < 0 ? " at the left" : side > 0 ? " at the right" : "") + " " + at.ToString("F1") + " m";
            LandVehicle other = c.GetComponentInParent<LandVehicle>();
            if (other != null) moving = other.IsOccupied || (other.Rb != null && other.Rb.velocity.sqrMagnitude > 0.25f);
            else moving = c.attachedRigidbody != null
                && (c.GetComponentInParent<Il2CppScheduleOne.NPCs.NPC>() != null
                    || c.GetComponentInParent<Il2CppScheduleOne.PlayerScripts.Player>() != null);
            return true;
        }

        /// <summary>
        /// The ground height under the car, skipping the car's own colliders, people, and anything above
        /// <paramref name="pos"/>: a car's pivot stands above its floor, and the player sitting in the car has a collider
        /// of its own that is no child of the car.
        /// </summary>
        internal static float Ground(LandVehicle car, Vector3 pos)
        {
            float best = pos.y - 0.5f, bestD = float.MaxValue;
            var hits = Physics.RaycastAll(pos + Vector3.up, Vector3.down, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.transform.IsChildOf(car.transform) || c.attachedRigidbody != null) continue;
                if (hits[i].point.y > pos.y + 0.1f) continue;
                if (c.GetComponentInParent<Il2CppScheduleOne.PlayerScripts.Player>() != null
                    || c.GetComponentInParent<Il2CppScheduleOne.NPCs.NPC>() != null) continue;
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

#if DEBUG
        /// <summary>
        /// The lot nearest <paramref name="near"/> as numbers: its entry, each spot as the car would take it, and the
        /// static colliders around it with their bounds, to read a lot's layout without a picture.
        /// </summary>
        internal static string MapLot(LandVehicle car, Vector3 near, float radius)
        {
            ParkingLot best = null;
            float bestD = float.MaxValue;
            foreach (ParkingLot lot in AllLots())
            {
                if (lot == null || lot.ParkingSpots == null) continue;
                for (int i = 0; i < lot.ParkingSpots.Count; i++)
                {
                    ParkingSpot s = lot.ParkingSpots[i];
                    if (s == null || s.AlignmentPoint == null) continue;
                    float d = Flat(s.AlignmentPoint.position - near).magnitude;
                    if (d < bestD) { bestD = d; best = lot; }
                }
            }
            if (best == null) return "lotmap: no lot";
            var sb = new System.Text.StringBuilder();
            sb.Append("lotmap ").Append(best.gameObject.name).Append(" at ").Append(best.transform.position.ToString("F1"));
            if (best.EntryPoint != null) sb.Append(" entry ").Append(best.EntryPoint.position.ToString("F1")).Append(" facing ").Append(Flat(best.EntryPoint.forward).ToString("F2"));
            Vector3 mid = Vector3.zero;
            int n = 0;
            for (int i = 0; i < best.ParkingSpots.Count; i++)
            {
                ParkingSpot s = best.ParkingSpots[i];
                if (s == null || s.AlignmentPoint == null) continue;
                ParkTarget t = ForSpot(car, best, i, s);
                bool free = Free(car, t, out string what);
                sb.Append("\n  #").Append(i).Append(" centre ").Append(t.Centre.ToString("F1")).Append(" heading ").Append(t.Heading.ToString("F2"))
                  .Append(free ? " free" : " blocked by " + what);
                mid += t.Centre;
                n++;
            }
            if (n > 0) mid /= n;
            float floor = Ground(car, mid + Vector3.up);
            var seen = new HashSet<IntPtr>();
            var hits = Physics.OverlapSphere(mid, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            int listed = 0;
            for (int i = 0; i < hits.Length && listed < 200; i++)
            {
                Collider c = hits[i];
                if (c == null || c.attachedRigidbody != null || !seen.Add(c.Pointer)) continue;
                Bounds b = c.bounds;
                // What a car can touch: from the kerb to the roof, not the floors above.
                if (b.size.x > 30f || b.size.z > 30f || b.max.y < floor + 0.1f || b.min.y > floor + 1.6f) continue;
                Transform up = c.transform.parent;
                sb.Append("\n  obj ").Append(up != null ? up.name + "/" : "").Append(c.name)
                  .Append(" c ").Append(b.center.ToString("F1")).Append(" s ").Append(b.size.ToString("F1"));
                listed++;
            }
            // Ground height around the lot in decimetres above its floor, one cell per metre, rows from north to south;
            // a jump of two or more between neighbours is a kerb.
            int half = Mathf.CeilToInt(radius);
            sb.Append("\n  ground dm above ").Append(floor.ToString("F2")).Append(", x from ").Append((Mathf.Round(mid.x) - half).ToString("F0"))
              .Append(" to ").Append((Mathf.Round(mid.x) + half).ToString("F0")).Append(", z from ").Append((Mathf.Round(mid.z) + half).ToString("F0")).Append(" down");
            for (int dz = half; dz >= -half; dz--)
            {
                sb.Append("\n  z ").Append((Mathf.Round(mid.z) + dz).ToString("F0")).Append(":");
                for (int dx = -half; dx <= half; dx++)
                {
                    var p = new Vector3(Mathf.Round(mid.x) + dx, floor + 3f, Mathf.Round(mid.z) + dz);
                    if (!GroundAt(car, p, out float h)) { sb.Append("  ."); continue; }
                    int dm = Mathf.RoundToInt((h - floor) * 10f);
                    sb.Append(dm > 99 ? " ##" : dm.ToString().PadLeft(3));
                }
            }
            return sb.ToString();
        }
#endif

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
