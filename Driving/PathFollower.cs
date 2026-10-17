using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Math;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;

namespace Yesla.Driving
{
    /// <summary>
    /// Steering, curve speed and the get-unstuck manoeuvres for a Yesla the road AI drives: Assisted Driving with
    /// the player at the wheel, or an empty car on its way to its owner (Summon).
    ///
    /// The vanilla agent keeps what it is good at: the route, speed limits, red lights, obstacle braking and the
    /// throttle. Its steering is replaced. On a player car it settled at zero steer while the path turned away, it
    /// does not slow for the sharp junction turns the road graph produces, and its reverse manoeuvre hung in place,
    /// so the car ran off the road or sat behind a parked car for good.
    ///
    /// Following is pure pursuit: aim at a point a speed-dependent distance ahead on the path and steer the arc that
    /// needs. The speed comes from <see cref="SpeedPlanner"/> with the driver's <see cref="DriveProfile"/>: bends,
    /// the destination, slower zones and anything in the lane ahead, braked for in time. On top of that two manoeuvres:
    /// - Bypass: something that is not a traffic light and not a car in traffic blocks the lane for a few seconds
    ///   (a parked car, a bin) - shift the aim point to the left of the path and crawl past it.
    /// - Reverse: the car stands still although it wants to move, or the obstacle is too close to swing out - back
    ///   up straight for a couple of metres, then carry on.
    /// - Turn: the path runs behind the car (it stands the wrong way round) - turn in moves at full lock, forward and
    ///   back, each move ending before the body touches something, until the nose points along the path.
    /// - Avoid: something standing in the lane ahead, such as a parked car - with the other lane clear, move over early
    ///   and pass it at speed instead of stopping behind it first.
    ///
    /// Runs as a postfix of <c>VehicleAgent.InfrequentUpdate</c>, after vanilla wrote its targets and before
    /// <c>LateUpdate</c> turns them into throttle and steer. Only cars handed in through <see cref="Attach"/> are
    /// touched; every other car is left alone.
    /// </summary>
    [HarmonyPatch(typeof(VehicleAgent), nameof(VehicleAgent.InfrequentUpdate))]
    internal static class PathFollower
    {
        private enum Manoeuvre { Follow, Reverse, Bypass, Turn }

        /// <summary>Lookahead in metres: the minimum, how much it grows per m/s, and the cap.</summary>
        private const float MinLookahead = 4f;
        private const float LookaheadPerMs = 0.7f;
        private const float MaxLookahead = 20f;
        /// <summary>Pure pursuit cuts a bend of radius R by about L^2 / 8R with lookahead L. In a bend the lookahead
        /// shrinks so the cut stays under this many metres; past it a right turn reaches the kerb and a left turn the
        /// oncoming lane.</summary>
        private const float MaxCut = 0.35f;
        /// <summary>The shortest lookahead in a tight bend, in metres; below it the steering starts to hunt.</summary>
        private const float BendLookahead = 3f;

        /// <summary>The speed plan is redone this often; vanilla's agent ticks at 30 Hz.</summary>
        private const float PlanSeconds = 0.1f;

        /// <summary>Closest-point search window around the last progress index, so a hairpin's return leg is not
        /// mistaken for progress.</summary>
        private const int SearchBack = 2;
        private const int SearchAhead = 25;

        /// <summary>
        /// Steering lock in degrees: full at a crawl, so tight corners and turns take one sweep, back to the 35 degrees
        /// vanilla's road AI uses at speed, where the pursuit asks for small angles anyway.
        /// </summary>
        private const float CrawlLock = 45f, SpeedLock = 35f, CrawlLockBelowKmh = 10f, SpeedLockFromKmh = 30f;

        /// <summary>
        /// How fast the wheels follow the pursuit, per second. Vanilla's agent eases the steering towards its target at
        /// 2 per second (<c>VehicleAgent.LateUpdate</c>), half a second behind: at 40 km/h the wheels turn 5 to 10 m
        /// after the bend began, and a right bend runs wide into the oncoming lane.
        /// </summary>
        private const float SteerFollowRate = 8f;

        /// <summary>
        /// Before a bend tighter than <see cref="SwingRadius"/> the car moves <see cref="SwingOut"/> metres to the
        /// outside of its lane, from <see cref="SwingFrom"/> metres before it, and back onto the line in the last
        /// <see cref="SwingUntil"/> metres: outside, inside, outside, the way a driver takes a corner. The turn gets a
        /// wider radius and the inside corner, a hydrant or a post at a driveway, stays further away.
        /// </summary>
        private const float SwingOut = 0.6f, SwingFrom = 18f, SwingUntil = 3f, SwingRadius = 15f, SwingRate = 0.8f;

        /// <summary>Heading error in degrees above which a reverse steers to turn the car, below it backs straight.</summary>
        private const float ReverseTurnAbove = 20f;

        /// <summary>Standing still this long while wanting to move means the car is wedged.</summary>
        private const float WedgedSeconds = 1.5f;

        /// <summary>Closer than this to the obstacle, the car backs up before it swings out.</summary>
        private const float SwingOutRoom = 2.5f;

        private const float ReverseDistance = 2.5f;
        private const float ReverseSeconds = 3.5f;
        private const float ReverseSpeedKmh = 5f;
        /// <summary>Reverse throttle while slower than <see cref="ReverseSpeedKmh"/> backwards.</summary>
        private const float ReverseThrottle = 1f;

        /// <summary>
        /// Turning in moves: starts below <see cref="TurnBelowKmh"/> when the path runs more than <see cref="TurnAbove"/>
        /// degrees away from the nose, ends within <see cref="TurnDone"/>. A move forward or back ends after its length,
        /// its time, or <see cref="TurnClearance"/> metres before the body would touch something.
        /// </summary>
        private const float TurnAbove = 110f, TurnDone = 30f, TurnBelowKmh = 10f, TurnKmh = 5f, TurnBackKmh = 4f;
        private const float TurnForwardMax = 7f, TurnBackMax = 4f, TurnStepSeconds = 7f, TurnClearance = 0.5f;
        /// <summary>Throttle that moves the car at a crawl forwards in a turn; vanilla's regulator gives too little to climb a kerb.</summary>
        private const float TurnThrottle = 0.6f;
        private const int MaxTurnSteps = 7;
        /// <summary>Standing this long held by something at the car's corner means it will not come free by waiting.</summary>
        private const float HeldSeconds = 2f;

        /// <summary>
        /// Passing something standing in the lane: the car keeps <see cref="AvoidMargin"/> metres from it, moves over at
        /// most <see cref="AvoidMax"/> metres (one lane), starts no closer than <see cref="AvoidMinAhead"/> metres and
        /// passes at up to <see cref="AvoidKmh"/>. Oncoming cars within <see cref="AvoidOncoming"/> metres past it keep
        /// the car in its lane.
        /// </summary>
        private const float AvoidMargin = 0.6f, AvoidMax = 3.4f, AvoidMinAhead = 5f, AvoidKmh = 25f, AvoidPassed = 1.5f, AvoidOncoming = 45f;

        private const float BypassOffset = 2.6f;
        private const float BypassLength = 14f;
        private const float BypassSpeedKmh = 8f;
        /// <summary>How often the car's corners are swept along its current steering.</summary>
        private const float CornerSeconds = 0.1f;
        /// <summary>Braking assumed for a post at a corner, and the furthest the sweep looks.</summary>
        private const float CornerBrake = 2.5f;
        private const float CornerReach = 8f;
        /// <summary>Gap the car stops short of a post at a corner.</summary>
        private const float CornerGap = 0.5f;
        /// <summary>
        /// Rolling instead of braking: up to this many km/h over the target the car rolls rather than brake (vanilla's
        /// regulator brakes from 0.6 km/h over), and it never rolls below <see cref="RollFloorKmh"/>, where a stop needs
        /// the brake.
        /// </summary>
        private const float RollBandKmh = 6f, RollFloorKmh = 12f;
        /// <summary>A post at a side moves the aim point this far away from it, at these rates out and back.</summary>
        private const float NudgeMetres = 1f;
        private const float NudgeRate = 2f;
        private const float NudgeRelax = 0.5f;
        private const float OffsetRate = 1.5f;

        /// <summary>Reverse manoeuvres without 10 m of progress in between before the car is given up.</summary>
        private const int MaxReverses = 3;
        /// <summary>An empty car waits this long at a closed police checkpoint before it gives up.</summary>
        private const float CheckpointGiveUpSeconds = 10f;
        private const float ProgressResetsReverses = 10f;
        /// <summary>A wheel sweep hit whose surface tilts less than this from level, in degrees, is a ramp to drive
        /// up (a bridge, a driveway), not something to stop for.</summary>
        private const float RampDeg = 40f;
        private const float RampKmh = 12f;

        /// <summary>Everything one followed car carries between ticks.</summary>
        private sealed class Follow
        {
            internal string Who;                   // "Assisted Driving" or "Summon", for the log
            internal LandVehicle Car;
            internal Action<string> GiveUp;        // called once when the car is stuck for good
            internal DriveProfile Profile;
            internal bool Driverless;              // nobody in the seat to talk the car through a police checkpoint
            internal IntPtr CheckpointSeen;        // the checkpoint the car last stopped at, noticed once
            internal float CheckpointWait;

            internal float PlanKmh = SpeedPlanner.MinKmh;
            internal float CoastKmh = float.MaxValue;
            internal bool Rolling;                 // last LateTick took the throttle away, for the log
            internal float RollLogAt = -10f;
            internal string PlanWhy = string.Empty;
            internal float NextPlan;

            internal PathSmoothingUtility.SmoothedPath Path;
            internal int Progress;
            internal float LastLog;
            internal float LastTick;

            internal Manoeuvre Manoeuvre = Manoeuvre.Follow;
            internal float ManoeuvreStart;
            internal Vector3 ManoeuvreFrom;
            internal bool BypassAfterReverse;
            internal float Offset;
            internal float Nudge;                  // metres to the left (negative: right) away from a post at a corner
            internal float Swing;                  // metres to the left (negative: right) towards a bend's outside
            internal float NextCorner;
            internal float CornerAt = float.MaxValue;
            internal int CornerSide;
            internal string CornerWhat = string.Empty;
            internal IntPtr CornerSeen;
            internal float WaitFor;
            internal float SweepWait;
            internal float WedgedFor;
            internal int Reverses;
            internal Vector3 LastReverseAt;
            internal int VanillaReversesStopped;
            internal float LastReverseStopLog = -100f;
            internal string LastLine = string.Empty;

            internal float Cte;                    // signed distance from the path, + left of it
            internal float WorstLeft, WorstRight;  // the furthest off the line this drive, for the log
            internal float NextCteLog;
            internal IntPtr RampSeen;              // the ramp last driven onto, logged once
            internal bool OnLine;                  // the car has reached the line once; the start can be off it
            internal float VanillaFollowRate = -1f; // the agent's own steering rate, put back on detach

            internal int TurnDir = 1;              // +1 forward, -1 back, in a turn
            internal int TurnSide = 1;             // +1 the nose swings right, -1 left
            internal int TurnSteps;
            internal int TurnDrive;                // what LateTick pushes: +1 forward, -1 back, 0 nothing
            internal Vector3 StepFrom;
            internal float StepStart, StallFor, HeldFor;

            internal float Avoid;                  // metres to the left the car moves to pass something standing
            internal Transform AvoidBody;
            internal Vector3 AvoidCentre;
            internal float AvoidHalf;
            internal string AvoidWhat = string.Empty;
            internal IntPtr AvoidRefused;
        }

        private static readonly Dictionary<IntPtr, Follow> Cars = new Dictionary<IntPtr, Follow>();

        /// <summary>Start steering <paramref name="car"/> with <paramref name="profile"/>. <paramref name="giveUp"/> runs
        /// when it is stuck for good. A <paramref name="driverless"/> car gives up at a closed police checkpoint; with the
        /// player in the seat it waits there for the search.</summary>
        internal static void Attach(LandVehicle car, string who, DriveProfile profile, Action<string> giveUp, bool driverless = false)
        {
            if (car == null) { Core.Log.Warning("[PathFollower] attach for " + who + " with no car - nothing to steer."); return; }
            profile ??= DriveProfile.Standard;
            float vanillaRate = Cars.TryGetValue(car.Pointer, out Follow old) ? old.VanillaFollowRate : car.Agent != null ? car.Agent.steerTargetFollowRate : -1f;
            Cars[car.Pointer] = new Follow { Who = who, GiveUp = giveUp, Car = car, Profile = profile, Driverless = driverless, VanillaFollowRate = vanillaRate };
            if (car.Agent != null)
            {
                ApplyFlags(car.Agent, profile, profile.MaxKmh);
                car.Agent.steerTargetFollowRate = SteerFollowRate;
            }
            else Core.Log.Warning("[PathFollower] " + car.VehicleName + " has no road AI - the steering rate stays as it is.");
            Core.Log.Msg("[PathFollower] " + who + " drives " + car.VehicleName + " with the " + profile.Label + " profile.");
        }

        /// <summary>Drive <paramref name="car"/> with <paramref name="profile"/> from its next tick on.</summary>
        internal static void ChangeProfile(LandVehicle car, DriveProfile profile)
        {
            if (car == null || profile == null || !Cars.TryGetValue(car.Pointer, out Follow f)) { Core.Log.Warning("[PathFollower] profile change for a car that is not followed - nothing to change."); return; }
            if (f.Profile == profile) return;
            Core.Log.Msg("[PathFollower] " + f.Who + " switches from " + f.Profile.Label + " to " + profile.Label + ".");
            f.Profile = profile;
            f.NextPlan = 0f;
            if (car.Agent != null) ApplyFlags(car.Agent, profile, f.PlanKmh);
        }

        /// <summary>The profile <paramref name="car"/> is followed with, or null when it is not followed.</summary>
        internal static DriveProfile ProfileFor(LandVehicle car) =>
            car != null && Cars.TryGetValue(car.Pointer, out Follow f) ? f.Profile : null;

        /// <summary>The profile of the followed car whose road seeker is <paramref name="seeker"/>, or null.</summary>
        internal static DriveProfile ProfileForRoadSeeker(IntPtr seeker)
        {
            foreach (Follow f in Cars.Values)
            {
                VehicleAgent agent = f.Car != null ? f.Car.Agent : null;
                if (agent != null && agent.roadSeeker != null && agent.roadSeeker.Pointer == seeker) return f.Profile;
            }
            return null;
        }

        /// <summary>
        /// The agent's flags for a profile. The planner does the bend speeds, so vanilla's own turn slowdown is off;
        /// a profile with no limit overrides the zone speed with the planned speed, so vanilla's obstacle slowdown
        /// (a factor on the target) still has a sane speed to scale. Takes effect at the agent's next tick.
        /// </summary>
        private static void ApplyFlags(VehicleAgent agent, DriveProfile p, float plannedKmh)
        {
            DriveFlags flags = agent.Flags;
            flags.TurnBasedSpeedReduction = false;
            flags.IgnoreTrafficLights = p.IgnoreTrafficLights;
            flags.SpeedLimitMultiplier = p.LimitFactor;
            flags.OverrideSpeed = p.NoLimit;
            if (p.NoLimit) flags.OverriddenSpeed = Mathf.Max(SpeedPlanner.MinKmh, plannedKmh);
        }

        internal static void Detach(LandVehicle car)
        {
            if (car == null) return;
            if (Cars.TryGetValue(car.Pointer, out Follow f) && f.VanillaFollowRate > 0f && car.Agent != null)
                car.Agent.steerTargetFollowRate = f.VanillaFollowRate;
            Cars.Remove(car.Pointer);
        }

        /// <summary>
        /// Who a route from <paramref name="start"/> to <paramref name="end"/> is being searched for: a followed car
        /// whose front is at the start and whose road AI is headed for the end. Null for every other car.
        /// </summary>
        internal static string WhoseRoute(Vector3 start, Vector3 end)
        {
            foreach (Follow f in Cars.Values)
            {
                VehicleAgent agent = f.Car != null ? f.Car.Agent : null;
                if (agent == null) continue;
                Vector3 front = agent.frontOfVehiclePosition;
                if ((front - start).sqrMagnitude > 10f * 10f) continue;
                if ((agent.TargetLocation - end).sqrMagnitude > 25f * 25f) continue;
                return f.Who;
            }
            return null;
        }

        /// <summary>The last state line for <paramref name="car"/>, or empty when it is not followed.</summary>
        internal static string LastLineFor(LandVehicle car) =>
            car != null && Cars.TryGetValue(car.Pointer, out Follow f) ? f.LastLine : string.Empty;

        private static void Postfix(VehicleAgent __instance)
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.PathFollower.Steer");
#endif
            try
            {
                if (Cars.Count == 0) return;
                LandVehicle car = __instance.vehicle;
                if (car == null || !Cars.TryGetValue(car.Pointer, out Follow f)) return;
                if (!__instance.AutoDriving || __instance.KinematicMode) return;
                // Vanilla's reverse trigger in native UpdateSweep fires after 0.25 s of a wheel sweep hit; held far
                // below that, it never starts. Stopping it afterwards would also zero targetSpeed and stall the car.
                __instance.sweepTestFailedTime = -1000f;
                if (__instance.reverseCoroutine != null)
                {
                    // Vanilla's own reverse hung in place on a player car; ours replaces it. Its trigger sits in
                    // native UpdateSweep, out of Harmony's reach, so the coroutine is stopped as soon as it starts.
                    __instance.StopReversing();
                    f.VanillaReversesStopped++;
                    if (Time.time - f.LastReverseStopLog > 10f)
                    {
                        f.LastReverseStopLog = Time.time;
                        Core.Log.Msg("[PathFollower] stopped vanilla's reverse manoeuvre (" + f.VanillaReversesStopped + " so far), Yesla handles reversing.");
                    }
                }
                var path = __instance.path;
                if (path == null || path.vectorPath == null || path.vectorPath.Count < 2) return;

                Drive(f, __instance, car, path);
            }
            catch (Exception e)
            {
                Core.Log.Error("[PathFollower] steering failed, vanilla steering stays in charge this tick: " + e);
            }
        }

        private static void Drive(Follow f, VehicleAgent agent, LandVehicle car, PathSmoothingUtility.SmoothedPath path)
        {
            float now = Time.time;
            float dt = Mathf.Clamp(now - f.LastTick, 0f, 0.25f);
            f.LastTick = now;

            var pts = path.vectorPath;
            if (f.Path == null || f.Path.Pointer != path.Pointer)
            {
                f.Path = path;
                f.Progress = Closest(pts, car.transform.position, 0, pts.Count - 2, out _);
                Core.Log.Msg("[PathFollower] " + f.Who + " following a new path: " + pts.Count + " points, starting at #" + f.Progress
                             + ", steering lock " + CrawlLock + " deg below " + CrawlLockBelowKmh + " km/h, " + SpeedLock + " deg from " + SpeedLockFromKmh + " km/h"
                             + ", wheels follow at " + agent.steerTargetFollowRate.ToString("F0") + "/s (vanilla " + f.VanillaFollowRate.ToString("F0") + "/s)");
            }
            // Vanilla's Navigate set its own 35 degrees once; this replaces it on every update and its reset at the end
            // of the drive restores the car's own lock.
            car.OverrideMaxSteerAngle(Mathf.Lerp(CrawlLock, SpeedLock, Mathf.InverseLerp(CrawlLockBelowKmh, SpeedLockFromKmh, Mathf.Abs(car.Speed_Kmh))));

            Vector3 pos = car.transform.position;
            int seg = Closest(pts, pos, f.Progress - SearchBack, f.Progress + SearchAhead, out float lerp);
            if (seg > f.Progress) f.Progress = seg;
            TrackLine(f, pts, seg, lerp, pos, car, now);
            if (f.Reverses > 0 && Vector3.Distance(pos, f.LastReverseAt) > ProgressResetsReverses) f.Reverses = 0;

            // Heading error to the path a little ahead: reversing with the wheels turned against it swings the nose back
            // towards the path, the first half of a three-point turn.
            float headingError = HeadingError(path, car, pos, seg, lerp);
            f.TurnDrive = 0;
            if (f.Manoeuvre == Manoeuvre.Reverse)
            {
                Reverse(f, agent, car, now, headingError);
                f.LastLine = "REVERSE " + Vector3.Distance(pos, f.ManoeuvreFrom).ToString("F1") + " m";
                LogLine(f, now);
                return;
            }
            if (f.Manoeuvre == Manoeuvre.Turn)
            {
                Turn(f, agent, car, now, dt, headingError, pos);
                f.LastLine = "TURN move " + (f.TurnSteps + 1) + " " + (f.TurnDir > 0 ? "forward" : "back") + " " + Vector3.Distance(pos, f.StepFrom).ToString("F1")
                           + " m, heading " + headingError.ToString("F0") + " deg off, speed " + car.Speed_Kmh.ToString("F0");
                LogLine(f, now);
                return;
            }
            if (f.Manoeuvre == Manoeuvre.Follow && Mathf.Abs(car.Speed_Kmh) < TurnBelowKmh && Mathf.Abs(headingError) > TurnAbove)
            {
                StartTurn(f, car, headingError, "the path runs " + Mathf.Abs(headingError).ToString("F0") + " deg away from the nose");
                agent.targetSpeed = 0f;
                return;
            }

            if (f.Manoeuvre == Manoeuvre.Bypass && Vector3.Distance(pos, f.ManoeuvreFrom) > BypassLength && !FrontBlocked(agent, 3f))
                SetManoeuvre(f, Manoeuvre.Follow, pos, "past the obstacle");
            f.Offset = Mathf.MoveTowards(f.Offset, f.Manoeuvre == Manoeuvre.Bypass ? BypassOffset : f.Avoid, OffsetRate * dt);

            float speedMs = Mathf.Abs(car.Speed_Kmh) / 3.6f;
            float lookahead = Mathf.Clamp(MinLookahead + speedMs * LookaheadPerMs, MinLookahead, MaxLookahead);
            // Off the line (the start, after a bypass) a short aim point lies beside the car and turns it into the
            // kerb; the cap only holds once the car is on the line, and never below twice the distance to it.
            if (f.OnLine && f.Manoeuvre == Manoeuvre.Follow)
                lookahead = Mathf.Max(Mathf.Max(BendLookahead, 2f * Mathf.Abs(f.Cte)), Mathf.Min(lookahead, BendCap(pts, seg, lookahead + 4f)));
            // The car's own corners on its current steering: a lamp post or a hydrant beside a tight bend, which
            // the line ahead and vanilla's sensors miss. One at a side moves the aim point away from it.
            if (now >= f.NextCorner)
            {
                f.NextCorner = now + CornerSeconds;
                float reach = Mathf.Clamp(1.5f + speedMs * speedMs / (2f * CornerBrake), 1.5f, CornerReach);
                if (Footprint.Sweep(car, 1f, reach, out float cAt, out int cSide, out Collider cHit, out string cWhat))
                {
                    f.CornerAt = cAt;
                    f.CornerSide = cSide;
                    f.CornerWhat = cWhat;
                    if (cHit.Pointer != f.CornerSeen)
                    {
                        f.CornerSeen = cHit.Pointer;
                        Core.Log.Msg("[PathFollower] " + f.Who + ": " + cWhat + " in " + cAt.ToString("F1") + " m at the car's "
                                     + (cSide < 0 ? "left" : cSide > 0 ? "right" : "front") + " on the current steering - slowing"
                                     + (cSide != 0 ? " and keeping " + NudgeMetres.ToString("F0") + " m to the " + (cSide > 0 ? "left" : "right") : "") + ".");
                    }
                }
                else { f.CornerAt = float.MaxValue; f.CornerSide = 0; }
            }
            bool cornerHit = f.CornerAt < float.MaxValue;
            f.Nudge = Mathf.MoveTowards(f.Nudge, cornerHit ? f.CornerSide * NudgeMetres : 0f, (cornerHit ? NudgeRate : NudgeRelax) * dt);

            float swing = f.Manoeuvre == Manoeuvre.Follow && f.OnLine && !cornerHit && f.Avoid <= 0f ? BendSwing(pts, seg, pos) : 0f;
            f.Swing = Mathf.MoveTowards(f.Swing, swing, SwingRate * dt);
            Vector3 target = PathUtility.GetAheadPoint(path, pos, lookahead, seg, lerp);
            if (target == Vector3.zero) target = pts[pts.Count - 1];
            float shift = f.Offset + f.Nudge + f.Swing;
            if (Mathf.Abs(shift) > 0.01f)
            {
                Vector3 beyond = PathUtility.GetAheadPoint(path, pos, lookahead + 1f, seg, lerp);
                Vector3 dir = beyond - target; dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = car.transform.forward;
                dir.Normalize();
                target += new Vector3(-dir.z, 0f, dir.x) * shift;
            }

            float alpha = PursuitSteer(agent, car, target, out float steer);
            agent.targetSteerAngle_Normalized = steer;

            // Planned speed, and a crawl while the car points well away from the path.
            if (now >= f.NextPlan)
            {
                f.NextPlan = now + PlanSeconds;
                SpeedPlanner.Plan plan = SpeedPlanner.Run(car, agent, pts, seg, lerp, f.Profile, f.Manoeuvre == Manoeuvre.Follow ? f.Avoid : 0f);
                f.PlanKmh = plan.Kmh;
                f.CoastKmh = plan.CoastKmh;
                f.PlanWhy = plan.Why;
                ApplyFlags(agent, f.Profile, f.PlanKmh);
                if (f.Manoeuvre == Manoeuvre.Follow) UpdateAvoid(f, car, plan, pos);
            }
            float planned = f.PlanKmh;
            float heading = Mathf.Abs(alpha) * Mathf.Rad2Deg;
            if (heading > 35f) planned = Mathf.Min(planned, SpeedPlanner.MinKmh);
            if (planned < agent.targetSpeed) agent.targetSpeed = planned;
            float cornerKmh = cornerHit ? Mathf.Sqrt(2f * CornerBrake * Mathf.Max(0f, f.CornerAt - CornerGap)) * 3.6f : float.MaxValue;
            if (cornerKmh < agent.targetSpeed) agent.targetSpeed = cornerKmh;
            if (agent.targetSpeed < 1f && planned > 3f && f.Manoeuvre == Manoeuvre.Follow && OnRamp(f, agent, car))
                agent.targetSpeed = Mathf.Min(planned, RampKmh);
            // Passing something standing: vanilla's sensors still see it beside the car and slow for it, but the lane
            // the car moved into was swept clear by the planner, which also brakes for anything that turns up there.
            if (f.Avoid > 0f && f.Manoeuvre == Manoeuvre.Follow)
                agent.targetSpeed = Mathf.Min(planned, AvoidKmh, cornerKmh);

            if (f.Manoeuvre == Manoeuvre.Bypass)
            {
                // Vanilla still brakes for the obstacle being passed; only something right at the bumper stops us.
                bool atBumper = FrontBlocked(agent, 1f);
                agent.targetSpeed = atBumper ? 0f : Mathf.Min(BypassSpeedKmh, Mathf.Max(agent.targetSpeed, BypassSpeedKmh));
                if (atBumper && Mathf.Abs(car.Speed_Kmh) < 1f)
                {
                    f.WaitFor += dt;
                    if (f.WaitFor >= f.Profile.WaitBeforeBypass)
                    {
                        Sensor s = NearestFront(agent);
                        f.BypassAfterReverse = true;
                        StartReverse(f, car, "blocked while passing, " + (s != null ? s.obstruction.name + " at " + s.obstructionDistance.ToString("F1") + " m" : "front sensor"));
                    }
                }
                else f.WaitFor = 0f;
            }
            else
            {
                CheckWaiting(f, agent, car, dt);
            }
            if (cornerKmh < agent.targetSpeed) agent.targetSpeed = cornerKmh;
            CheckWedged(f, agent, car, dt);
            if (f.Manoeuvre == Manoeuvre.Reverse || f.Manoeuvre == Manoeuvre.Turn) return;
            CheckHeld(f, car, dt, cornerHit && cornerKmh < 1f, headingError);

            f.LastLine = f.Manoeuvre + " seg=" + seg + "/" + pts.Count + " look=" + lookahead.ToString("F1") + " alpha=" + heading.ToString("F0")
                       + " steer=" + steer.ToString("F2") + " plan=" + f.PlanKmh.ToString("F0") + " (" + f.PlanWhy + ") target=" + agent.targetSpeed.ToString("F0")
                       + " speed=" + car.Speed_Kmh.ToString("F0") + " " + f.Profile.Id
                       + " offset=" + f.Offset.ToString("F1") + " cte=" + f.Cte.ToString("F1") + " worst L/R=" + f.WorstLeft.ToString("F1") + "/" + f.WorstRight.ToString("F1")
                       + " wait=" + f.WaitFor.ToString("F1") + (cornerHit ? " corner=" + f.CornerWhat + "@" + f.CornerAt.ToString("F1") : "")
                       + (Mathf.Abs(f.Nudge) > 0.05f ? " nudge=" + f.Nudge.ToString("F1") : "")
                       + (Mathf.Abs(f.Swing) > 0.05f ? " swing=" + f.Swing.ToString("F1") : "")
                       + (f.Avoid > 0f ? " avoid=" + f.Avoid.ToString("F1") + " (" + f.AvoidWhat + ")" : "");
            LogLine(f, now);
        }

        /// <summary>
        /// Where to stand before the next tight bend: <see cref="SwingOut"/> to its outside (+ left, - right) while
        /// the bend starts between <see cref="SwingUntil"/> and <see cref="SwingFrom"/> metres ahead, 0 otherwise.
        /// The first point that bends tighter than <see cref="SwingRadius"/> is the bend's start; inside the bend that
        /// point is right ahead, so the car stays on the line through it.
        /// </summary>
        private static float BendSwing(Il2CppSystem.Collections.Generic.List<Vector3> pts, int seg, Vector3 pos)
        {
            if (seg + 1 >= pts.Count) return 0f;
            Vector3 first = pts[seg + 1] - pos; first.y = 0f;
            float run = first.magnitude;
            for (int i = seg + 1; i < pts.Count - 1 && run < SwingFrom; i++)
            {
                Vector3 a = pts[i] - pts[i - 1], b = pts[i + 1] - pts[i];
                a.y = 0f; b.y = 0f;
                float la = a.magnitude, lb = b.magnitude;
                if (la > 0.01f && lb > 0.01f)
                {
                    float angle = Vector3.SignedAngle(a, b, Vector3.up);
                    float k = Mathf.Abs(angle) * Mathf.Deg2Rad / Mathf.Max(0.5f, 0.5f * (la + lb));
                    if (k > 1f / SwingRadius && Mathf.Abs(angle) > 3f)
                        return run < SwingUntil ? 0f : angle > 0f ? SwingOut : -SwingOut;
                }
                run += lb;
            }
            return 0f;
        }

        /// <summary>
        /// The longest lookahead that keeps the cut through the tightest bend within <paramref name="window"/> metres
        /// ahead under <see cref="MaxCut"/>. Curvature is the turn at a point over the length around it.
        /// </summary>
        private static float BendCap(Il2CppSystem.Collections.Generic.List<Vector3> pts, int seg, float window)
        {
            float kappa = 0f, run = 0f;
            for (int i = Mathf.Max(1, seg + 1); i < pts.Count - 1 && run < window; i++)
            {
                Vector3 a = pts[i] - pts[i - 1], b = pts[i + 1] - pts[i];
                a.y = 0f; b.y = 0f;
                float la = a.magnitude, lb = b.magnitude;
                run += la;
                if (la < 0.01f || lb < 0.01f) continue;
                float k = Vector3.Angle(a, b) * Mathf.Deg2Rad / Mathf.Max(0.5f, 0.5f * (la + lb));
                if (k > kappa) kappa = k;
            }
            if (kappa < 0.002f) return MaxLookahead;
            return Mathf.Sqrt(8f * MaxCut / kappa);
        }

        /// <summary>Pure pursuit: the arc through <paramref name="target"/>, measured from the rear axle.
        /// Returns the heading error in radians.</summary>
        private static float PursuitSteer(VehicleAgent agent, LandVehicle car, Vector3 target, out float normalized)
        {
            float wheelbase = Mathf.Max(2f, agent.vehicleLength * 0.6f);
            Vector3 rear = car.transform.position - car.transform.forward * (wheelbase * 0.5f);
            Vector3 toTarget = target - rear; toTarget.y = 0f;
            Vector3 fwd = car.transform.forward; fwd.y = 0f;
            float alpha = Vector3.SignedAngle(fwd, toTarget, Vector3.up) * Mathf.Deg2Rad;
            float dist = Mathf.Max(toTarget.magnitude, 0.5f);
            float steerDeg = Mathf.Atan2(2f * wheelbase * Mathf.Sin(alpha), dist) * Mathf.Rad2Deg;
            float maxSteer = Mathf.Max(1f, car.ActualMaxSteeringAngle);
            normalized = Mathf.Abs(alpha) > Mathf.PI * 0.5f ? Mathf.Sign(alpha) : Mathf.Clamp(steerDeg / maxSteer, -1f, 1f);
            return alpha;
        }

        /// <summary>
        /// Vanilla brought the car to a halt for something ahead. A red light (its stop line is a
        /// <c>VehicleObstacle</c>) is waited out, a car in traffic is waited for, anything else is passed.
        /// </summary>
        private static void CheckWaiting(Follow f, VehicleAgent agent, LandVehicle car, float dt)
        {
            if (Mathf.Abs(car.Speed_Kmh) > 1f || agent.targetSpeed > 1f) { f.WaitFor = 0f; f.SweepWait = 0f; return; }

            Sensor blocker = NearestFront(agent);
            if (blocker == null) { CheckSweepBlock(f, agent, car, dt); return; }
            f.SweepWait = 0f;
            Collider c = blocker.obstruction;
            RoadCheckpoint checkpoint = c.GetComponentInParent<RoadCheckpoint>();
            if (checkpoint != null) { WaitAtCheckpoint(f, checkpoint, dt); return; }
            if (LayerMask.LayerToName(c.gameObject.layer) == "VehicleObstacle") { f.WaitFor = 0f; return; }

            LandVehicle other = c.GetComponentInParent<LandVehicle>();
            bool traffic = other != null && other.Agent != null && other.Agent.AutoDriving;

            f.WaitFor += dt;
            if (f.WaitFor < (traffic ? f.Profile.WaitBehindTraffic : f.Profile.WaitBeforeBypass)) return;
            f.WaitFor = 0f;

            string what = c.name + (other != null ? " (" + other.VehicleName + (traffic ? ", in traffic" : ", parked") + ")" : "");
            if (blocker.obstructionDistance < SwingOutRoom)
            {
                f.BypassAfterReverse = true;
                StartReverse(f, car, "too close to swing out around " + what + " at " + blocker.obstructionDistance.ToString("F1") + " m");
            }
            else
            {
                SetManoeuvre(f, Manoeuvre.Bypass, car.transform.position, "passing " + what);
            }
        }

        /// <summary>
        /// A closed police checkpoint. Vanilla opens its gate on its own only for an NPC driver; a player gets out, asks
        /// the officer for a search, and the gate opens when it is done (DialogueHandler_Police, CheckpointBehaviour).
        /// With the player at the wheel the car waits for that; an empty car on its way to its owner gives up.
        /// </summary>
        private static void WaitAtCheckpoint(Follow f, RoadCheckpoint checkpoint, float dt)
        {
            f.WaitFor = 0f;
            if (f.CheckpointSeen != checkpoint.Pointer)
            {
                f.CheckpointSeen = checkpoint.Pointer;
                f.CheckpointWait = 0f;
                Core.Log.Msg("[PathFollower] " + f.Who + " stopped at the closed police checkpoint " + checkpoint.name + " at "
                           + checkpoint.transform.position.ToString("F0") + (f.Driverless ? " with nobody in the car." : " - waiting for the search."));
                if (!f.Driverless) Phone.YeslaApp.Notify("Police checkpoint", "Talk to the officer to pass.", 6f);
                return;
            }
            if (!f.Driverless) return;
            f.CheckpointWait += dt;
            if (f.CheckpointWait < CheckpointGiveUpSeconds) return;
            Core.Log.Warning("[PathFollower] " + f.Who + " waited " + CheckpointGiveUpSeconds + " s at the police checkpoint " + checkpoint.name + " - giving up.");
            Action<string> giveUp = f.GiveUp;
            f.GiveUp = null;
            if (giveUp != null) giveUp("checkpoint");
            else Core.Log.Warning("[PathFollower] " + f.Who + " already gave up - nothing more to stop.");
        }

        /// <summary>
        /// Held at zero with no front sensor hit: vanilla's wheel sweep (UpdateSpeedReduction) sees something within
        /// 1.5 m of a front wheel - a kerb post, a bin, a pedestrian - which the sensors miss. Too close to steer
        /// around, so back up first.
        /// </summary>
        private static void CheckSweepBlock(Follow f, VehicleAgent agent, LandVehicle car, float dt)
        {
            f.WaitFor = 0f;
            var path = agent.path;
            if (path != null && path.vectorPath.Count > 0
                && Vector3.Distance(car.transform.position, path.vectorPath[path.vectorPath.Count - 1]) < 10f)
            {
                f.SweepWait = 0f;
                return;
            }

            f.SweepWait += dt;
            if (f.SweepWait < f.Profile.WaitBeforeBypass) return;
            f.SweepWait = 0f;

            float steer = car.CurrentSteerAngle;
            agent.GetWheelSweepCollision(VehicleAgent.ESweepType.FL, steer, false, out float dl, out RaycastHit hl);
            agent.GetWheelSweepCollision(VehicleAgent.ESweepType.FR, steer, false, out float dr, out RaycastHit hr);
            RaycastHit hit = dl <= dr ? hl : hr;
            float dist = Mathf.Min(dl, dr);
            if (hit.collider != null && LayerMask.LayerToName(hit.collider.gameObject.layer) == "VehicleObstacle")
            {
                Core.Log.Msg("[PathFollower] held at a stop line (" + hit.collider.name + "), waiting.");
                return;
            }

            string what = hit.collider != null
                ? hit.collider.name + " (layer " + LayerMask.LayerToName(hit.collider.gameObject.layer) + ") at " + dist.ToString("F1") + " m by the " + (dl <= dr ? "left" : "right") + " front wheel"
                : f.CornerAt < float.MaxValue ? f.CornerWhat + " at the car's corner" : "nothing the sweep can see";
            Core.Log.Warning("[PathFollower] held at 0 km/h for " + f.Profile.WaitBeforeBypass + " s with no front sensor hit; wheel sweep: " + what
                           + ", reduction min=" + agent.speedReductionTracker.GetLowestValue().ToString("F2"));
            f.BypassAfterReverse = hit.collider != null;
            StartReverse(f, car, "blocked by " + what);
        }

        /// <summary>
        /// Vanilla's wheel sweep stops the car for anything within reach of a front wheel, the start of a bridge ramp
        /// included. A surface the car can drive up faces upwards; a kerb, a post or a person faces sideways.
        /// </summary>
        private static bool OnRamp(Follow f, VehicleAgent agent, LandVehicle car)
        {
            float steer = car.CurrentSteerAngle;
            agent.GetWheelSweepCollision(VehicleAgent.ESweepType.FL, steer, false, out float dl, out RaycastHit hl);
            agent.GetWheelSweepCollision(VehicleAgent.ESweepType.FR, steer, false, out float dr, out RaycastHit hr);
            RaycastHit hit = dl <= dr ? hl : hr;
            if (hit.collider == null) return false;
            if (hit.collider.attachedRigidbody != null || Vector3.Angle(hit.normal, Vector3.up) > RampDeg) return false;
            if (f.RampSeen != hit.collider.Pointer)
            {
                f.RampSeen = hit.collider.Pointer;
                Core.Log.Msg("[PathFollower] " + f.Who + " drives up " + hit.collider.name + " (surface " + Vector3.Angle(hit.normal, Vector3.up).ToString("F0")
                           + " deg from level) instead of stopping for it.");
            }
            return true;
        }

        /// <summary>Wants to move, gives throttle, stays put: wedged on a kerb, a wall or at a turn too sharp.</summary>
        private static void CheckWedged(Follow f, VehicleAgent agent, LandVehicle car, float dt)
        {
            if (Mathf.Abs(car.Speed_Kmh) > 1f || agent.targetSpeed < 3f || car.throttleOverride < 0.3f) { f.WedgedFor = 0f; return; }
            f.WedgedFor += dt;
            if (f.WedgedFor < WedgedSeconds) return;
            f.WedgedFor = 0f;
            f.BypassAfterReverse = NearestFront(agent) != null;
            StartReverse(f, car, "wedged at " + car.transform.position.ToString("F1"));
        }

        private static void StartReverse(Follow f, LandVehicle car, string why)
        {
            if (f.Reverses >= MaxReverses)
            {
                Core.Log.Warning("[PathFollower] " + f.Who + " still stuck after " + MaxReverses + " reverse manoeuvres (" + why + ") - giving up.");
                Action<string> giveUp = f.GiveUp;
                f.GiveUp = null;
                if (giveUp != null) giveUp("stuck");
                else Core.Log.Warning("[PathFollower] " + f.Who + " already gave up - nothing more to stop.");
                return;
            }
            f.Reverses++;
            f.LastReverseAt = car.transform.position;
            SetManoeuvre(f, Manoeuvre.Reverse, car.transform.position, why + ", reversing " + f.Reverses + "/" + MaxReverses);
        }

        private static void Reverse(Follow f, VehicleAgent agent, LandVehicle car, float now, float headingError)
        {
            agent.targetSteerAngle_Normalized = Mathf.Abs(headingError) > ReverseTurnAbove ? -Mathf.Sign(headingError) : 0f;
            agent.targetSpeed = -ReverseSpeedKmh;

            float moved = Vector3.Distance(car.transform.position, f.ManoeuvreFrom);
            Sensor rl = agent.sensor_RL, rr = agent.sensor_RR;
            bool rearBlocked = (rl != null && rl.obstruction != null && rl.obstructionDistance < 1f)
                            || (rr != null && rr.obstruction != null && rr.obstructionDistance < 1f);
            string why = moved >= ReverseDistance ? "backed up " + moved.ToString("F1") + " m"
                       : rearBlocked ? "something behind"
                       : now - f.ManoeuvreStart > ReverseSeconds ? "reverse timed out after " + moved.ToString("F1") + " m"
                       : null;
            if (why == null) return;

            agent.targetSpeed = 0f;
            // Swinging out only makes sense while roughly lined up with the road; turned away, turn in moves first.
            if (Mathf.Abs(headingError) >= 60f) StartTurn(f, car, headingError, why + ", still " + Mathf.Abs(headingError).ToString("F0") + " deg off the path");
            else if (f.BypassAfterReverse) SetManoeuvre(f, Manoeuvre.Bypass, car.transform.position, why + ", swinging out");
            else SetManoeuvre(f, Manoeuvre.Follow, car.transform.position, why);
        }

        /// <summary>Heading error in degrees from the nose to the path a little ahead, + to the right.</summary>
        private static float HeadingError(PathSmoothingUtility.SmoothedPath path, LandVehicle car, Vector3 pos, int seg, float lerp)
        {
            Vector3 ahead = PathUtility.GetAheadPoint(path, pos, MinLookahead * 2f, seg, lerp);
            Vector3 toAhead = ahead - pos; toAhead.y = 0f;
            Vector3 fwdFlat = car.transform.forward; fwdFlat.y = 0f;
            return ahead == Vector3.zero || toAhead.sqrMagnitude < 0.01f ? 0f : Vector3.SignedAngle(fwdFlat, toAhead, Vector3.up);
        }

        /// <summary>
        /// Standing still because something at the car's corner holds it, which waiting does not change: turn in moves
        /// when the car points away from the path, otherwise back up and carry on.
        /// </summary>
        private static void CheckHeld(Follow f, LandVehicle car, float dt, bool heldByCorner, float headingError)
        {
            f.HeldFor = heldByCorner && Mathf.Abs(car.Speed_Kmh) < 1f ? f.HeldFor + dt : 0f;
            if (f.HeldFor < HeldSeconds) return;
            f.HeldFor = 0f;
            string why = "held for " + HeldSeconds.ToString("F0") + " s by " + f.CornerWhat + " " + f.CornerAt.ToString("F1") + " m from the car's "
                       + (f.CornerSide < 0 ? "left" : f.CornerSide > 0 ? "right" : "front");
            if (Mathf.Abs(headingError) > 45f) StartTurn(f, car, headingError, why);
            else
            {
                f.BypassAfterReverse = false;
                StartReverse(f, car, why);
            }
        }

        private static void StartTurn(Follow f, LandVehicle car, float headingError, string why)
        {
            Vector3 pos = car.transform.position;
            f.TurnSide = Mathf.Abs(headingError) > 150f ? RoomierSide(car) : headingError > 0f ? 1 : -1;
            f.TurnDir = 1;
            f.TurnSteps = 0;
            SetManoeuvre(f, Manoeuvre.Turn, pos, why + ", turning " + (f.TurnSide > 0 ? "right" : "left") + " in moves");
            f.StepFrom = pos;
            f.StepStart = Time.time;
            f.StallFor = 0f;
        }

        /// <summary>+1 when there is more room to the car's right than to its left, else -1.</summary>
        private static int RoomierSide(LandVehicle car)
        {
            Transform t = car.transform;
            Vector3 from = t.position + Vector3.up * 0.8f;
            Vector3 right = t.right; right.y = 0f; right.Normalize();
            float r = Free(car, from, right), l = Free(car, from, -right);
            Core.Log.Msg("[PathFollower] room beside the car: " + l.ToString("F1") + " m left, " + r.ToString("F1") + " m right.");
            return r >= l ? 1 : -1;
        }

        private static float Free(LandVehicle car, Vector3 from, Vector3 dir)
        {
            const float Reach = 15f;
            var hits = Physics.RaycastAll(from, dir, Reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = Reach;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.transform.IsChildOf(car.transform) || Footprint.Aboard(c, car)) continue;
                if (hits[i].distance < best) best = hits[i].distance;
            }
            return best;
        }

        /// <summary>
        /// One tick of turning in moves. Forward the wheels turn towards the path, back away from it, so each move
        /// swings the nose further round; the wheels are set before the car moves, as a driver does in a tight spot.
        /// </summary>
        private static void Turn(Follow f, VehicleAgent agent, LandVehicle car, float now, float dt, float headingError, Vector3 pos)
        {
            if (Mathf.Abs(headingError) < TurnDone)
            {
                agent.targetSpeed = 0f;
                SetManoeuvre(f, Manoeuvre.Follow, pos, "lined up after " + (f.TurnSteps + 1) + " move(s), " + Mathf.Abs(headingError).ToString("F0") + " deg off");
                return;
            }
            float steer = f.TurnDir > 0 ? f.TurnSide : -f.TurnSide;
            agent.targetSteerAngle_Normalized = steer;
            float lockNow = car.ActualMaxSteeringAngle > 0.1f ? car.CurrentSteerAngle / car.ActualMaxSteeringAngle : 0f;
            bool wheelsSet = Mathf.Abs(lockNow - steer) < 0.25f;
            agent.targetSpeed = !wheelsSet ? 0f : f.TurnDir > 0 ? TurnKmh : -TurnBackKmh;
            f.TurnDrive = wheelsSet ? f.TurnDir : 0;

            float moved = Vector3.Distance(pos, f.StepFrom);
            f.StallFor = wheelsSet && Mathf.Abs(car.Speed_Kmh) < 0.5f && now - f.StepStart > 1f ? f.StallFor + dt : 0f;
            string end = null;
            if (wheelsSet && Footprint.Sweep(car, f.TurnDir, TurnClearance + 1f, out float at, out _, out _, out string what) && at < TurnClearance)
                end = what + " " + at.ToString("F1") + " m " + (f.TurnDir > 0 ? "ahead" : "behind");
            else if (moved > (f.TurnDir > 0 ? TurnForwardMax : TurnBackMax)) end = "moved " + moved.ToString("F1") + " m";
            else if (now - f.StepStart > TurnStepSeconds) end = "move timed out after " + moved.ToString("F1") + " m";
            else if (f.StallFor > 1.5f) end = "not moving";
            // Backed round far enough that a forward move at full lock finishes the turn.
            else if (f.TurnDir < 0 && moved > 1f && Mathf.Abs(headingError) < 75f) end = "nose round to " + Mathf.Abs(headingError).ToString("F0") + " deg";
            if (end == null) return;

            f.TurnSteps++;
            if (f.TurnSteps >= MaxTurnSteps)
            {
                agent.targetSpeed = 0f;
                f.TurnDrive = 0;
                Core.Log.Warning("[PathFollower] " + f.Who + " could not turn round in " + MaxTurnSteps + " moves (" + end + ", heading "
                                 + headingError.ToString("F0") + " deg off) - giving up.");
                Action<string> giveUp = f.GiveUp;
                f.GiveUp = null;
                if (giveUp != null) giveUp("stuck");
                else Core.Log.Warning("[PathFollower] " + f.Who + " already gave up - nothing more to stop.");
                SetManoeuvre(f, Manoeuvre.Follow, pos, "turning given up");
                return;
            }
            f.TurnDir = -f.TurnDir;
            f.StepFrom = pos;
            f.StepStart = now;
            f.StallFor = 0f;
            Core.Log.Msg("[PathFollower] " + f.Who + " turn move " + f.TurnSteps + " ended (" + end + "), heading " + headingError.ToString("F0")
                         + " deg off - next move " + (f.TurnDir > 0 ? "forward" : "back") + ".");
        }

        /// <summary>
        /// Decides whether to pass the thing the planner found standing in the lane, and ends a pass once the car is
        /// by it. Moving traffic, people and a red light are waited for as before; a pass needs the lane beside the
        /// thing swept clear and no oncoming car close.
        /// </summary>
        private static void UpdateAvoid(Follow f, LandVehicle car, SpeedPlanner.Plan plan, Vector3 pos)
        {
            Vector3 fwd = car.transform.forward; fwd.y = 0f; fwd.Normalize();
            float halfLen = car.BoundingBoxDimensions.z * 0.5f;
            if (f.Avoid > 0f)
            {
                if (f.AvoidBody == null) { EndAvoid(f, "the thing being passed is gone"); return; }
                if (Vector3.Dot(f.AvoidCentre - pos, fwd) < -(f.AvoidHalf + halfLen + AvoidPassed)) EndAvoid(f, "past " + f.AvoidWhat);
                return;
            }

            Collider c = plan.Blocker;
            if (c == null || plan.BlockerIsStopLine || plan.BlockerAt < AvoidMinAhead || plan.BlockerSpeed > 0.3f) return;
            Rigidbody rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic && rb.velocity.sqrMagnitude > 0.25f) return;
            if (c.GetComponentInParent<Il2CppScheduleOne.PlayerScripts.Player>() != null) return;
            if (c.GetComponentInParent<Il2CppScheduleOne.NPCs.NPC>() != null) return;
            LandVehicle other = c.GetComponentInParent<LandVehicle>();
            if (other != null && other.Agent != null && other.Agent.AutoDriving) return;

            var ahead = SpeedPlanner.Ahead;
            if (ahead.Count < 3) return;
            int k = Mathf.Clamp(Mathf.RoundToInt(plan.BlockerAt / SpeedPlanner.SampleStep), 0, ahead.Count - 2);
            Vector3 left = SpeedPlanner.Left(k);
            if (left == Vector3.zero) return;
            Vector3 along = new Vector3(left.z, 0f, -left.x);
            Vector3 basePt = ahead[k];

            // The thing's outline across the lane: a car's own box, anything else its collider's bounds.
            var corners = new Vector3[8];
            if (other != null && other.boundingBox != null)
            {
                Transform bt = other.boundingBox.transform;
                Vector3 h = other.boundingBox.size * 0.5f, ctr = other.boundingBox.center;
                for (int i = 0; i < 8; i++)
                    corners[i] = bt.TransformPoint(ctr + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z));
            }
            else
            {
                Bounds b = c.bounds;
                for (int i = 0; i < 8; i++)
                    corners[i] = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
            }
            float maxLeft = float.MinValue, minAlong = float.MaxValue, maxAlong = float.MinValue;
            foreach (Vector3 corner in corners)
            {
                Vector3 v = corner - basePt; v.y = 0f;
                maxLeft = Mathf.Max(maxLeft, Vector3.Dot(v, left));
                float a = Vector3.Dot(v, along);
                minAlong = Mathf.Min(minAlong, a);
                maxAlong = Mathf.Max(maxAlong, a);
            }
            string what = other != null ? other.VehicleName + " (parked)" : (c.transform.parent != null ? c.transform.parent.name + "/" : "") + c.name;
            float need = maxLeft + car.BoundingBoxDimensions.x * 0.5f + AvoidMargin;
            if (need <= 0.2f) return;
            if (need > AvoidMax) { RefuseAvoid(f, c, what + " reaches too far across the road - " + need.ToString("F1") + " m to pass it"); return; }
            if (!LaneClear(car, c, other, k, need, maxAlong, out string busy)) { RefuseAvoid(f, c, "passing " + what + " waits: " + busy); return; }

            f.Avoid = need;
            f.AvoidBody = other != null ? other.transform : c.transform;
            f.AvoidCentre = basePt + along * ((minAlong + maxAlong) * 0.5f);
            f.AvoidHalf = (maxAlong - minAlong) * 0.5f;
            f.AvoidWhat = what;
            f.AvoidRefused = IntPtr.Zero;
            Core.Log.Msg("[PathFollower] " + f.Who + ": " + what + " standing in the lane in " + plan.BlockerAt.ToString("F0") + " m - moving "
                         + need.ToString("F1") + " m to the left to pass it.");
        }

        private static void RefuseAvoid(Follow f, Collider c, string why)
        {
            if (f.AvoidRefused == c.Pointer) return;
            f.AvoidRefused = c.Pointer;
            Core.Log.Msg("[PathFollower] " + f.Who + ": " + why + ".");
        }

        private static void EndAvoid(Follow f, string why)
        {
            Core.Log.Msg("[PathFollower] " + f.Who + " back into the lane: " + why + ".");
            f.Avoid = 0f;
            f.AvoidBody = null;
            f.AvoidWhat = string.Empty;
        }

        /// <summary>
        /// The lane <paramref name="shift"/> metres left of the path, from the car to 8 m past the thing being passed:
        /// nothing standing in it, and no car coming the other way within <see cref="AvoidOncoming"/> metres.
        /// </summary>
        private static bool LaneClear(LandVehicle car, Collider blocker, LandVehicle blockerCar, int k, float shift, float pastAlong, out string busy)
        {
            busy = null;
            var ahead = SpeedPlanner.Ahead;
            Transform self = car.transform;
            Transform seated = car.HumanoidColliderContainer != null ? car.HumanoidColliderContainer.transform : null;
            Transform skip = blockerCar != null ? blockerCar.transform : blocker.transform;
            int last = Mathf.Min(ahead.Count - 1, k + Mathf.CeilToInt((pastAlong + 8f) / SpeedPlanner.SampleStep));
            for (int i = 1; i + 1 <= last; i++)
            {
                Vector3 from = ahead[i] + SpeedPlanner.Left(i) * shift + Vector3.up * 1f;
                Vector3 to = ahead[i + 1] + SpeedPlanner.Left(i + 1) * shift + Vector3.up * 1f;
                Vector3 d = to - from;
                float len = d.magnitude;
                if (len < 0.01f) continue;
                var hits = Physics.SphereCastAll(from, 0.95f, d / len, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int h = 0; h < hits.Length; h++)
                {
                    Collider c = hits[h].collider;
                    if (c == null) continue;
                    Transform t = c.transform;
                    if (t.IsChildOf(self) || t.IsChildOf(skip) || (seated != null && t.IsChildOf(seated)) || Footprint.Aboard(c, car)) continue;
                    if (c.attachedRigidbody == null && hits[h].normal.y > 0.7f) continue;
                    if (c.GetComponentInParent<VehicleObstacle>() != null) continue;
                    busy = (t.parent != null ? t.parent.name + "/" : "") + c.name + " in the other lane " + (i * SpeedPlanner.SampleStep).ToString("F0") + " m ahead";
                    return false;
                }
            }

            var all = NetworkSingleton<VehicleManager>.InstanceExists ? NetworkSingleton<VehicleManager>.Instance.AllVehicles : null;
            if (all == null) return true;
            Vector3 basePt = ahead[0], left = SpeedPlanner.Left(0);
            Vector3 fwd = new Vector3(left.z, 0f, -left.x);
            for (int i = 0; i < all.Count; i++)
            {
                LandVehicle v = all[i];
                if (v == null || v.Pointer == car.Pointer || v.Rb == null || (blockerCar != null && v.Pointer == blockerCar.Pointer)) continue;
                Vector3 vel = v.Rb.velocity; vel.y = 0f;
                if (vel.magnitude < 2f || Vector3.Dot(vel, fwd) > -1f) continue;
                Vector3 rel = v.transform.position - basePt; rel.y = 0f;
                float a = Vector3.Dot(rel, fwd), lat = Vector3.Dot(rel, left);
                if (a < -5f || a > pastAlong + AvoidOncoming) continue;
                if (Mathf.Abs(lat - shift) > 2.6f) continue;
                busy = v.VehicleName + " coming the other way " + a.ToString("F0") + " m ahead";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Called from OnLateUpdate, after <c>VehicleAgent.LateUpdate</c> has turned the target speed into throttle.
        /// Vanilla's speed regulator (p = 0.08) gives -0.4 throttle for -5 km/h, which does not free a car whose
        /// nose is pressed against a wall; while backing up below the reverse speed, full reverse throttle does.
        /// </summary>
        internal static void LateTick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.PathFollower.LateTick");
#endif
            if (Cars.Count == 0) return;
            foreach (var kv in Cars)
            {
                Follow f = kv.Value;
                if (f.Car == null) continue;
                LandVehicle car = f.Car;
                if (car.Agent == null || !car.Agent.AutoDriving) continue;
                if (f.Manoeuvre == Manoeuvre.Reverse || (f.Manoeuvre == Manoeuvre.Turn && f.TurnDrive < 0))
                {
                    if (car.Speed_Kmh > -(f.Manoeuvre == Manoeuvre.Turn ? TurnBackKmh : ReverseSpeedKmh)) car.throttleOverride = -ReverseThrottle;
                }
                else if (f.Manoeuvre == Manoeuvre.Turn && f.TurnDrive > 0 && car.Speed_Kmh < TurnKmh)
                    car.throttleOverride = Mathf.Max(car.throttleOverride, TurnThrottle);
                else if (f.Manoeuvre == Manoeuvre.Follow) Roll(f, car);
            }
        }

        /// <summary>
        /// Lifts off where rolling is enough: early, once the speed is above what rolling still slows down in time for
        /// (<see cref="SpeedPlanner.Plan.CoastKmh"/>), and instead of a light brake a little over the target. Brake
        /// lights stay off while it rolls, since vanilla only lights them for a negative throttle.
        /// </summary>
        private static void Roll(Follow f, LandVehicle car)
        {
            float v = car.Speed_Kmh, target = car.Agent.targetSpeed, th = car.throttleOverride;
            bool roll = v > RollFloorKmh && ((th > 0f && v >= f.CoastKmh) || (th < 0f && target > RollFloorKmh && v - target < RollBandKmh));
            if (roll) car.throttleOverride = 0f;
            if (roll == f.Rolling || Time.time - f.RollLogAt < 2f) return;
            f.Rolling = roll;
            f.RollLogAt = Time.time;
            Core.Log.Msg("[PathFollower] " + f.Who + (roll ? " rolls" : " stops rolling") + " at " + v.ToString("F0") + " km/h (target "
                         + target.ToString("F0") + ", roll below " + (f.CoastKmh > 999f ? "-" : f.CoastKmh.ToString("F0")) + ", plan " + f.PlanWhy + ").");
        }

        /// <summary>The closest front sensor that sees something, or null.</summary>
        private static Sensor NearestFront(VehicleAgent agent)
        {
            Sensor best = null;
            foreach (Sensor s in new[] { agent.sensor_FL, agent.sensor_FM, agent.sensor_FR })
            {
                if (s == null || s.obstruction == null) continue;
                if (best == null || s.obstructionDistance < best.obstructionDistance) best = s;
            }
            return best;
        }

        private static bool FrontBlocked(VehicleAgent agent, float within)
        {
            Sensor s = NearestFront(agent);
            return s != null && s.obstructionDistance < within;
        }

        private static void SetManoeuvre(Follow f, Manoeuvre next, Vector3 at, string why)
        {
            Core.Log.Msg("[PathFollower] " + f.Who + " " + f.Manoeuvre + " -> " + next + " (" + why + ")");
            if (next != Manoeuvre.Follow && f.Avoid > 0f) EndAvoid(f, "the " + next + " manoeuvre takes over");
            f.HeldFor = 0f;
            f.Manoeuvre = next;
            f.ManoeuvreStart = Time.time;
            f.ManoeuvreFrom = at;
            f.WaitFor = 0f; f.SweepWait = 0f;
            f.WedgedFor = 0f;
            if (next != Manoeuvre.Reverse) f.BypassAfterReverse = false;
        }

        /// <summary>Off the line this far, in metres, is logged: past it a car on the right-hand lane reaches the
        /// centre line (left) or the kerb (right).</summary>
        private const float CteWarn = 1.5f;

        /// <summary>
        /// Signed distance of the car from the path, + to the left. Left of the line is the oncoming lane, right of it
        /// the pavement; the furthest each way is kept and a new worst beyond <see cref="CteWarn"/> is logged.
        /// </summary>
        private static void TrackLine(Follow f, Il2CppSystem.Collections.Generic.List<Vector3> pts, int seg, float lerp, Vector3 pos, LandVehicle car, float now)
        {
            // Moved over on purpose (passing something standing) is not off the line.
            if (f.Manoeuvre != Manoeuvre.Follow || Mathf.Abs(f.Offset) > 0.05f) { f.Cte = 0f; return; }
            Vector3 a = pts[seg], b = pts[seg + 1];
            Vector3 ab = b - a; ab.y = 0f;
            Vector3 on = Vector3.Lerp(a, b, lerp);
            Vector3 off = pos - on; off.y = 0f;
            float side = Mathf.Sign(Vector3.Cross(ab, off).y);
            f.Cte = -side * off.magnitude;
            float d = Mathf.Abs(f.Cte);
            if (!f.OnLine) { f.OnLine = d < 0.7f; return; }
            if (f.Cte > 0f) f.WorstLeft = Mathf.Max(f.WorstLeft, d); else f.WorstRight = Mathf.Max(f.WorstRight, d);
            if (d < CteWarn || now < f.NextCteLog) return;
            f.NextCteLog = now + 1f;
            float turn = 0f;
            if (seg > 0 && seg + 2 < pts.Count) turn = Vector3.SignedAngle(pts[seg] - pts[seg - 1], pts[seg + 2] - pts[seg + 1], Vector3.up);
            Core.Log.Msg("[PathFollower] " + f.Who + " off the line " + d.ToString("F1") + " m to the " + (f.Cte > 0f ? "left" : "right")
                       + " at seg " + seg + " " + pos.ToString("F0") + ", " + car.Speed_Kmh.ToString("F0") + " km/h, path turning " + turn.ToString("F0") + " deg"
                       + ", wheels " + car.CurrentSteerAngle.ToString("F0") + " of " + car.ActualMaxSteeringAngle.ToString("F0") + " deg.");
        }

        private static void LogLine(Follow f, float now)
        {
            if (now - f.LastLog < 2f) return;
            f.LastLog = now;
            Core.Log.Msg("[PathFollower] " + f.Who + " " + f.LastLine);
        }

        /// <summary>Index of the path segment closest to <paramref name="p"/> within [from, to], with the position
        /// along it in <paramref name="lerp"/>.</summary>
        private static int Closest(Il2CppSystem.Collections.Generic.List<Vector3> pts, Vector3 p, int from, int to, out float lerp)
        {
            from = Mathf.Clamp(from, 0, pts.Count - 2);
            to = Mathf.Clamp(to, from, pts.Count - 2);
            int best = from;
            float bestD = float.MaxValue;
            lerp = 0f;
            for (int i = from; i <= to; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1];
                Vector3 ab = b - a; ab.y = 0f;
                Vector3 ap = p - a; ap.y = 0f;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-4f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / len2) : 0f;
                float d = (ap - ab * t).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; lerp = t; }
            }
            return best;
        }

        /// <summary>Forgets every car; the world is going away.</summary>
        internal static void Reset() => Cars.Clear();
    }
}
