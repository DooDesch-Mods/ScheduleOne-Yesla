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
    ///
    /// Runs as a postfix of <c>VehicleAgent.InfrequentUpdate</c>, after vanilla wrote its targets and before
    /// <c>LateUpdate</c> turns them into throttle and steer. Only cars handed in through <see cref="Attach"/> are
    /// touched; every other car is left alone.
    /// </summary>
    [HarmonyPatch(typeof(VehicleAgent), nameof(VehicleAgent.InfrequentUpdate))]
    internal static class PathFollower
    {
        private enum Manoeuvre { Follow, Reverse, Bypass }

        /// <summary>Lookahead in metres: the minimum, how much it grows per m/s, and the cap.</summary>
        private const float MinLookahead = 4f;
        private const float LookaheadPerMs = 0.7f;
        private const float MaxLookahead = 20f;

        /// <summary>The speed plan is redone this often; vanilla's agent ticks at 30 Hz.</summary>
        private const float PlanSeconds = 0.1f;

        /// <summary>Closest-point search window around the last progress index, so a hairpin's return leg is not
        /// mistaken for progress.</summary>
        private const int SearchBack = 2;
        private const int SearchAhead = 25;

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

        private const float BypassOffset = 2.6f;
        private const float BypassLength = 14f;
        private const float BypassSpeedKmh = 8f;
        private const float OffsetRate = 1.5f;

        /// <summary>Reverse manoeuvres without 10 m of progress in between before the car is given up.</summary>
        private const int MaxReverses = 3;
        /// <summary>An empty car waits this long at a closed police checkpoint before it gives up.</summary>
        private const float CheckpointGiveUpSeconds = 10f;
        private const float ProgressResetsReverses = 10f;

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
            internal float WaitFor;
            internal float SweepWait;
            internal float WedgedFor;
            internal int Reverses;
            internal Vector3 LastReverseAt;
            internal int VanillaReversesStopped;
            internal float LastReverseStopLog = -100f;
            internal string LastLine = string.Empty;
        }

        private static readonly Dictionary<IntPtr, Follow> Cars = new Dictionary<IntPtr, Follow>();

        /// <summary>Start steering <paramref name="car"/> with <paramref name="profile"/>. <paramref name="giveUp"/> runs
        /// when it is stuck for good. A <paramref name="driverless"/> car gives up at a closed police checkpoint; with the
        /// player in the seat it waits there for the search.</summary>
        internal static void Attach(LandVehicle car, string who, DriveProfile profile, Action<string> giveUp, bool driverless = false)
        {
            if (car == null) { Core.Log.Warning("[PathFollower] attach for " + who + " with no car - nothing to steer."); return; }
            profile ??= DriveProfile.Standard;
            Cars[car.Pointer] = new Follow { Who = who, GiveUp = giveUp, Car = car, Profile = profile, Driverless = driverless };
            if (car.Agent != null) ApplyFlags(car.Agent, profile, profile.MaxKmh);
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
            Cars.Remove(car.Pointer);
        }

        /// <summary>The last state line for <paramref name="car"/>, or empty when it is not followed.</summary>
        internal static string LastLineFor(LandVehicle car) =>
            car != null && Cars.TryGetValue(car.Pointer, out Follow f) ? f.LastLine : string.Empty;

        private static void Postfix(VehicleAgent __instance)
        {
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
                Core.Log.Msg("[PathFollower] " + f.Who + " following a new path: " + pts.Count + " points, starting at #" + f.Progress);
            }

            Vector3 pos = car.transform.position;
            int seg = Closest(pts, pos, f.Progress - SearchBack, f.Progress + SearchAhead, out float lerp);
            if (seg > f.Progress) f.Progress = seg;
            if (f.Reverses > 0 && Vector3.Distance(pos, f.LastReverseAt) > ProgressResetsReverses) f.Reverses = 0;

            if (f.Manoeuvre == Manoeuvre.Reverse)
            {
                // Heading error to the path a little ahead: reversing with the wheels turned against it swings the
                // nose back towards the path, the first half of a three-point turn.
                Vector3 ahead = PathUtility.GetAheadPoint(path, pos, MinLookahead * 2f, seg, lerp);
                Vector3 toAhead = ahead - pos; toAhead.y = 0f;
                Vector3 fwdFlat = car.transform.forward; fwdFlat.y = 0f;
                float headingError = ahead == Vector3.zero || toAhead.sqrMagnitude < 0.01f ? 0f : Vector3.SignedAngle(fwdFlat, toAhead, Vector3.up);
                Reverse(f, agent, car, now, headingError);
                f.LastLine = "REVERSE " + Vector3.Distance(pos, f.ManoeuvreFrom).ToString("F1") + " m";
                LogLine(f, now);
                return;
            }

            if (f.Manoeuvre == Manoeuvre.Bypass && Vector3.Distance(pos, f.ManoeuvreFrom) > BypassLength && !FrontBlocked(agent, 3f))
                SetManoeuvre(f, Manoeuvre.Follow, pos, "past the obstacle");
            f.Offset = Mathf.MoveTowards(f.Offset, f.Manoeuvre == Manoeuvre.Bypass ? BypassOffset : 0f, OffsetRate * dt);

            float speedMs = Mathf.Abs(car.Speed_Kmh) / 3.6f;
            float lookahead = Mathf.Clamp(MinLookahead + speedMs * LookaheadPerMs, MinLookahead, MaxLookahead);
            Vector3 target = PathUtility.GetAheadPoint(path, pos, lookahead, seg, lerp);
            if (target == Vector3.zero) target = pts[pts.Count - 1];
            if (f.Offset > 0.01f)
            {
                Vector3 beyond = PathUtility.GetAheadPoint(path, pos, lookahead + 1f, seg, lerp);
                Vector3 dir = beyond - target; dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = car.transform.forward;
                dir.Normalize();
                target += new Vector3(-dir.z, 0f, dir.x) * f.Offset;
            }

            float alpha = PursuitSteer(agent, car, target, out float steer);
            agent.targetSteerAngle_Normalized = steer;

            // Planned speed, and a crawl while the car points well away from the path.
            if (now >= f.NextPlan)
            {
                f.NextPlan = now + PlanSeconds;
                SpeedPlanner.Plan plan = SpeedPlanner.Run(car, agent, pts, seg, lerp, f.Profile);
                f.PlanKmh = plan.Kmh;
                f.PlanWhy = plan.Why;
                ApplyFlags(agent, f.Profile, f.PlanKmh);
            }
            float planned = f.PlanKmh;
            float heading = Mathf.Abs(alpha) * Mathf.Rad2Deg;
            if (heading > 35f) planned = Mathf.Min(planned, SpeedPlanner.MinKmh);
            if (planned < agent.targetSpeed) agent.targetSpeed = planned;

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
            CheckWedged(f, agent, car, dt);

            f.LastLine = f.Manoeuvre + " seg=" + seg + "/" + pts.Count + " look=" + lookahead.ToString("F1") + " alpha=" + heading.ToString("F0")
                       + " steer=" + steer.ToString("F2") + " plan=" + f.PlanKmh.ToString("F0") + " (" + f.PlanWhy + ") target=" + agent.targetSpeed.ToString("F0")
                       + " speed=" + car.Speed_Kmh.ToString("F0") + " " + f.Profile.Id
                       + " offset=" + f.Offset.ToString("F1") + " wait=" + f.WaitFor.ToString("F1");
            LogLine(f, now);
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
                : "nothing the sweep can see";
            Core.Log.Warning("[PathFollower] held at 0 km/h for " + f.Profile.WaitBeforeBypass + " s with no front sensor hit; wheel sweep: " + what
                           + ", reduction min=" + agent.speedReductionTracker.GetLowestValue().ToString("F2"));
            f.BypassAfterReverse = hit.collider != null;
            StartReverse(f, car, "blocked by " + what);
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
            // Swinging out only makes sense while roughly lined up with the road; turned away, turn back first.
            if (f.BypassAfterReverse && Mathf.Abs(headingError) < 60f) SetManoeuvre(f, Manoeuvre.Bypass, car.transform.position, why + ", swinging out");
            else SetManoeuvre(f, Manoeuvre.Follow, car.transform.position, why);
        }

        /// <summary>
        /// Called from OnLateUpdate, after <c>VehicleAgent.LateUpdate</c> has turned the target speed into throttle.
        /// Vanilla's speed regulator (p = 0.08) gives -0.4 throttle for -5 km/h, which does not free a car whose
        /// nose is pressed against a wall; while backing up below the reverse speed, full reverse throttle does.
        /// </summary>
        internal static void LateTick()
        {
            if (Cars.Count == 0) return;
            foreach (var kv in Cars)
            {
                Follow f = kv.Value;
                if (f.Manoeuvre != Manoeuvre.Reverse || f.Car == null) continue;
                LandVehicle car = f.Car;
                if (car.Agent == null || !car.Agent.AutoDriving) continue;
                if (car.Speed_Kmh > -ReverseSpeedKmh) car.throttleOverride = -ReverseThrottle;
            }
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
            f.Manoeuvre = next;
            f.ManoeuvreStart = Time.time;
            f.ManoeuvreFrom = at;
            f.WaitFor = 0f; f.SweepWait = 0f;
            f.WedgedFor = 0f;
            if (next != Manoeuvre.Reverse) f.BypassAfterReverse = false;
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
