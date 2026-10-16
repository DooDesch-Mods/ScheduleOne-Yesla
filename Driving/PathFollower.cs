using System;
using HarmonyLib;
using Il2CppScheduleOne.Math;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;

namespace Yesla.Driving
{
    /// <summary>
    /// Steering, curve speed and the get-unstuck manoeuvres for a car under Yesla Assisted Driving.
    ///
    /// The vanilla agent keeps what it is good at: the route, speed limits, red lights, obstacle braking and the
    /// throttle. Its steering is replaced. On a player car it settled at zero steer while the path turned away, it
    /// does not slow for the sharp junction turns the road graph produces, and its reverse manoeuvre hung in place,
    /// so the car ran off the road or sat behind a parked car for good.
    ///
    /// Following is pure pursuit: aim at a point a speed-dependent distance ahead on the path, steer the arc that
    /// needs, and cap the speed by how much the path bends ahead. On top of that two manoeuvres:
    /// - Bypass: something that is not a traffic light and not a car in traffic blocks the lane for a few seconds
    ///   (a parked car, a bin) - shift the aim point to the left of the path and crawl past it.
    /// - Reverse: the car stands still although it wants to move, or the obstacle is too close to swing out - back
    ///   up straight for a couple of metres, then carry on.
    ///
    /// Runs as a postfix of <c>VehicleAgent.InfrequentUpdate</c>, after vanilla wrote its targets and before
    /// <c>LateUpdate</c> turns them into throttle and steer. Every other car is left alone.
    /// </summary>
    [HarmonyPatch(typeof(VehicleAgent), nameof(VehicleAgent.InfrequentUpdate))]
    internal static class PathFollower
    {
        private enum Manoeuvre { Follow, Reverse, Bypass }

        /// <summary>Lookahead in metres: the minimum, how much it grows per m/s, and the cap.</summary>
        private const float MinLookahead = 4f;
        private const float LookaheadPerMs = 0.7f;
        private const float MaxLookahead = 14f;

        /// <summary>How far ahead the bend of the path is measured for the curve speed.</summary>
        private const float CurveLookahead = 25f;

        /// <summary>Speed for a 90 degree bend within the curve lookahead, in km/h.</summary>
        private const float SpeedAt90 = 14f;

        /// <summary>Slowest the curve speed goes, so a hairpin is still crawled through, in km/h.</summary>
        private const float MinCurveSpeed = 8f;

        /// <summary>Closest-point search window around the last progress index, so a hairpin's return leg is not
        /// mistaken for progress.</summary>
        private const int SearchBack = 2;
        private const int SearchAhead = 25;

        /// <summary>Waiting this long behind something that is neither a light nor traffic starts a bypass.</summary>
        private const float WaitBeforeBypass = 4f;
        /// <summary>Heading error in degrees above which a reverse steers to turn the car, below it backs straight.</summary>
        private const float ReverseTurnAbove = 20f;

        /// <summary>A car in traffic (its own AI driving) is waited for this long before it counts as stuck.</summary>
        private const float WaitBehindTraffic = 25f;

        /// <summary>Standing still this long while wanting to move means the car is wedged.</summary>
        private const float WedgedSeconds = 1.5f;

        /// <summary>Closer than this to the obstacle, the car backs up before it swings out.</summary>
        private const float SwingOutRoom = 2.5f;

        private const float ReverseDistance = 2.5f;
        private const float ReverseSeconds = 3.5f;
        private const float ReverseSpeedKmh = 5f;

        private const float BypassOffset = 2.6f;
        private const float BypassLength = 14f;
        private const float BypassSpeedKmh = 8f;
        private const float OffsetRate = 1.5f;

        /// <summary>Reverse manoeuvres without 10 m of progress in between before Assisted Driving gives up.</summary>
        private const int MaxReverses = 3;
        private const float ProgressResetsReverses = 10f;

        private static PathSmoothingUtility.SmoothedPath _path;
        private static int _progress;
        private static float _lastLog;
        private static float _lastTick;

        private static Manoeuvre _manoeuvre = Manoeuvre.Follow;
        private static float _manoeuvreStart;
        private static Vector3 _manoeuvreFrom;
        private static bool _bypassAfterReverse;
        private static float _offset;
        private static float _waitFor;
        private static float _sweepWait;
        private static float _wedgedFor;
        private static int _reverses;
        private static Vector3 _lastReverseAt;
        private static int _vanillaReversesStopped;
        private static float _lastReverseStopLog = -100f;

        internal static string LastLine { get; private set; } = string.Empty;

        private static void Postfix(VehicleAgent __instance)
        {
            try
            {
                if (Autopilot.State != DriveState.Driving) return;
                LandVehicle car = Autopilot.DrivenCar;
                if (car == null || __instance.vehicle == null || __instance.vehicle.Pointer != car.Pointer) return;
                if (!__instance.AutoDriving || __instance.KinematicMode) return;
                // Vanilla's reverse trigger in native UpdateSweep fires after 0.25 s of a wheel sweep hit; held far
                // below that, it never starts. Stopping it afterwards would also zero targetSpeed and stall the car.
                __instance.sweepTestFailedTime = -1000f;
                if (__instance.reverseCoroutine != null)
                {
                    // Vanilla's own reverse hung in place on a player car; ours replaces it. Its trigger sits in
                    // native UpdateSweep, out of Harmony's reach, so the coroutine is stopped as soon as it starts.
                    __instance.StopReversing();
                    _vanillaReversesStopped++;
                    if (Time.time - _lastReverseStopLog > 10f)
                    {
                        _lastReverseStopLog = Time.time;
                        Core.Log.Msg("[PathFollower] stopped vanilla's reverse manoeuvre (" + _vanillaReversesStopped + " so far), Yesla handles reversing.");
                    }
                }
                var path = __instance.path;
                if (path == null || path.vectorPath == null || path.vectorPath.Count < 2) return;

                Drive(__instance, car, path);
            }
            catch (Exception e)
            {
                Core.Log.Error("[PathFollower] steering failed, vanilla steering stays in charge this tick: " + e);
            }
        }

        private static void Drive(VehicleAgent agent, LandVehicle car, PathSmoothingUtility.SmoothedPath path)
        {
            float now = Time.time;
            float dt = Mathf.Clamp(now - _lastTick, 0f, 0.25f);
            _lastTick = now;

            var pts = path.vectorPath;
            if (_path == null || _path.Pointer != path.Pointer)
            {
                _path = path;
                _progress = Closest(pts, car.transform.position, 0, pts.Count - 2, out _);
                Core.Log.Msg("[PathFollower] following a new path: " + pts.Count + " points, starting at #" + _progress);
            }

            Vector3 pos = car.transform.position;
            int seg = Closest(pts, pos, _progress - SearchBack, _progress + SearchAhead, out float lerp);
            if (seg > _progress) _progress = seg;
            if (_reverses > 0 && Vector3.Distance(pos, _lastReverseAt) > ProgressResetsReverses) _reverses = 0;

            if (_manoeuvre == Manoeuvre.Reverse)
            {
                // Heading error to the path a little ahead: reversing with the wheels turned against it swings the
                // nose back towards the path, the first half of a three-point turn.
                Vector3 ahead = PathUtility.GetAheadPoint(path, pos, MinLookahead * 2f, seg, lerp);
                Vector3 toAhead = ahead - pos; toAhead.y = 0f;
                Vector3 fwdFlat = car.transform.forward; fwdFlat.y = 0f;
                float headingError = ahead == Vector3.zero || toAhead.sqrMagnitude < 0.01f ? 0f : Vector3.SignedAngle(fwdFlat, toAhead, Vector3.up);
                Reverse(agent, car, now, headingError);
                LastLine = "REVERSE " + Vector3.Distance(pos, _manoeuvreFrom).ToString("F1") + " m";
                LogLine(now);
                return;
            }

            if (_manoeuvre == Manoeuvre.Bypass && Vector3.Distance(pos, _manoeuvreFrom) > BypassLength && !FrontBlocked(agent, 3f))
                SetManoeuvre(Manoeuvre.Follow, pos, "past the obstacle");
            _offset = Mathf.MoveTowards(_offset, _manoeuvre == Manoeuvre.Bypass ? BypassOffset : 0f, OffsetRate * dt);

            float speedMs = Mathf.Abs(car.Speed_Kmh) / 3.6f;
            float lookahead = Mathf.Clamp(MinLookahead + speedMs * LookaheadPerMs, MinLookahead, MaxLookahead);
            Vector3 target = PathUtility.GetAheadPoint(path, pos, lookahead, seg, lerp);
            if (target == Vector3.zero) target = pts[pts.Count - 1];
            if (_offset > 0.01f)
            {
                Vector3 beyond = PathUtility.GetAheadPoint(path, pos, lookahead + 1f, seg, lerp);
                Vector3 dir = beyond - target; dir.y = 0f;
                if (dir.sqrMagnitude < 1e-4f) dir = car.transform.forward;
                dir.Normalize();
                target += new Vector3(-dir.z, 0f, dir.x) * _offset;
            }

            float alpha = PursuitSteer(agent, car, target, out float steer);
            agent.targetSteerAngle_Normalized = steer;

            // Curve speed from how much the path bends ahead, and a crawl while the car points well away from it.
            float bend = PathUtility.CalculateAngleChangeOverPath(path, seg, lerp, CurveLookahead);
            float curveSpeed = Mathf.Max(MinCurveSpeed, Mathf.Lerp(agent.targetSpeed, SpeedAt90, Mathf.Clamp01(bend / 90f)));
            float heading = Mathf.Abs(alpha) * Mathf.Rad2Deg;
            if (heading > 35f) curveSpeed = Mathf.Min(curveSpeed, MinCurveSpeed);
            if (curveSpeed < agent.targetSpeed) agent.targetSpeed = curveSpeed;

            if (_manoeuvre == Manoeuvre.Bypass)
            {
                // Vanilla still brakes for the obstacle being passed; only something right at the bumper stops us.
                bool atBumper = FrontBlocked(agent, 1f);
                agent.targetSpeed = atBumper ? 0f : Mathf.Min(BypassSpeedKmh, Mathf.Max(agent.targetSpeed, BypassSpeedKmh));
                if (atBumper && Mathf.Abs(car.Speed_Kmh) < 1f)
                {
                    _waitFor += dt;
                    if (_waitFor >= WaitBeforeBypass)
                    {
                        Sensor s = NearestFront(agent);
                        _bypassAfterReverse = true;
                        StartReverse(car, "blocked while passing, " + (s != null ? s.obstruction.name + " at " + s.obstructionDistance.ToString("F1") + " m" : "front sensor"));
                    }
                }
                else _waitFor = 0f;
            }
            else
            {
                CheckWaiting(agent, car, dt);
            }
            CheckWedged(agent, car, dt);

            LastLine = _manoeuvre + " seg=" + seg + "/" + pts.Count + " look=" + lookahead.ToString("F1") + " alpha=" + heading.ToString("F0")
                     + " steer=" + steer.ToString("F2") + " bend=" + bend.ToString("F0") + " target=" + agent.targetSpeed.ToString("F0")
                     + " offset=" + _offset.ToString("F1") + " wait=" + _waitFor.ToString("F1");
            LogLine(now);
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
        private static void CheckWaiting(VehicleAgent agent, LandVehicle car, float dt)
        {
            if (Mathf.Abs(car.Speed_Kmh) > 1f || agent.targetSpeed > 1f) { _waitFor = 0f; _sweepWait = 0f; return; }

            Sensor blocker = NearestFront(agent);
            if (blocker == null) { CheckSweepBlock(agent, car, dt); return; }
            _sweepWait = 0f;
            Collider c = blocker.obstruction;
            if (LayerMask.LayerToName(c.gameObject.layer) == "VehicleObstacle") { _waitFor = 0f; return; }

            LandVehicle other = c.GetComponentInParent<LandVehicle>();
            bool traffic = other != null && other.Agent != null && other.Agent.AutoDriving;

            _waitFor += dt;
            if (_waitFor < (traffic ? WaitBehindTraffic : WaitBeforeBypass)) return;
            _waitFor = 0f;

            string what = c.name + (other != null ? " (" + other.VehicleName + (traffic ? ", in traffic" : ", parked") + ")" : "");
            if (blocker.obstructionDistance < SwingOutRoom)
            {
                _bypassAfterReverse = true;
                StartReverse(car, "too close to swing out around " + what + " at " + blocker.obstructionDistance.ToString("F1") + " m");
            }
            else
            {
                SetManoeuvre(Manoeuvre.Bypass, car.transform.position, "passing " + what);
            }
        }

        /// <summary>
        /// Held at zero with no front sensor hit: vanilla's wheel sweep (UpdateSpeedReduction) sees something within
        /// 1.5 m of a front wheel - a kerb post, a bin, a pedestrian - which the sensors miss. Too close to steer
        /// around, so back up first.
        /// </summary>
        private static void CheckSweepBlock(VehicleAgent agent, LandVehicle car, float dt)
        {
            _waitFor = 0f;
            var path = agent.path;
            if (path != null && path.vectorPath.Count > 0
                && Vector3.Distance(car.transform.position, path.vectorPath[path.vectorPath.Count - 1]) < 10f)
            {
                _sweepWait = 0f;
                return;
            }

            _sweepWait += dt;
            if (_sweepWait < WaitBeforeBypass) return;
            _sweepWait = 0f;

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
            Core.Log.Warning("[PathFollower] held at 0 km/h for " + WaitBeforeBypass + " s with no front sensor hit; wheel sweep: " + what
                           + ", reduction min=" + agent.speedReductionTracker.GetLowestValue().ToString("F2"));
            _bypassAfterReverse = hit.collider != null;
            StartReverse(car, "blocked by " + what);
        }

        /// <summary>Wants to move, gives throttle, stays put: wedged on a kerb, a wall or at a turn too sharp.</summary>
        private static void CheckWedged(VehicleAgent agent, LandVehicle car, float dt)
        {
            if (Mathf.Abs(car.Speed_Kmh) > 1f || agent.targetSpeed < 3f || car.throttleOverride < 0.3f) { _wedgedFor = 0f; return; }
            _wedgedFor += dt;
            if (_wedgedFor < WedgedSeconds) return;
            _wedgedFor = 0f;
            _bypassAfterReverse = NearestFront(agent) != null;
            StartReverse(car, "wedged at " + car.transform.position.ToString("F1"));
        }

        private static void StartReverse(LandVehicle car, string why)
        {
            if (_reverses >= MaxReverses)
            {
                Core.Log.Warning("[PathFollower] still stuck after " + MaxReverses + " reverse manoeuvres (" + why + ") - giving the car back.");
                Autopilot.Disengage("stuck");
                return;
            }
            _reverses++;
            _lastReverseAt = car.transform.position;
            SetManoeuvre(Manoeuvre.Reverse, car.transform.position, why + ", reversing " + _reverses + "/" + MaxReverses);
        }

        private static void Reverse(VehicleAgent agent, LandVehicle car, float now, float headingError)
        {
            agent.targetSteerAngle_Normalized = Mathf.Abs(headingError) > ReverseTurnAbove ? -Mathf.Sign(headingError) : 0f;
            agent.targetSpeed = -ReverseSpeedKmh;

            float moved = Vector3.Distance(car.transform.position, _manoeuvreFrom);
            Sensor rl = agent.sensor_RL, rr = agent.sensor_RR;
            bool rearBlocked = (rl != null && rl.obstruction != null && rl.obstructionDistance < 1f)
                            || (rr != null && rr.obstruction != null && rr.obstructionDistance < 1f);
            string why = moved >= ReverseDistance ? "backed up " + moved.ToString("F1") + " m"
                       : rearBlocked ? "something behind"
                       : now - _manoeuvreStart > ReverseSeconds ? "reverse timed out after " + moved.ToString("F1") + " m"
                       : null;
            if (why == null) return;

            agent.targetSpeed = 0f;
            // Swinging out only makes sense while roughly lined up with the road; turned away, turn back first.
            if (_bypassAfterReverse && Mathf.Abs(headingError) < 60f) SetManoeuvre(Manoeuvre.Bypass, car.transform.position, why + ", swinging out");
            else SetManoeuvre(Manoeuvre.Follow, car.transform.position, why);
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

        private static void SetManoeuvre(Manoeuvre next, Vector3 at, string why)
        {
            Core.Log.Msg("[PathFollower] " + _manoeuvre + " -> " + next + " (" + why + ")");
            _manoeuvre = next;
            _manoeuvreStart = Time.time;
            _manoeuvreFrom = at;
            _waitFor = 0f; _sweepWait = 0f;
            _wedgedFor = 0f;
            if (next != Manoeuvre.Reverse) _bypassAfterReverse = false;
        }

        private static void LogLine(float now)
        {
            if (now - _lastLog < 2f) return;
            _lastLog = now;
            Core.Log.Msg("[PathFollower] " + LastLine);
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

        internal static void Reset()
        {
            _path = null;
            _progress = 0;
            _manoeuvre = Manoeuvre.Follow;
            _bypassAfterReverse = false;
            _offset = 0f;
            _waitFor = 0f; _sweepWait = 0f;
            _wedgedFor = 0f;
            _reverses = 0;
            LastLine = string.Empty;
        }
    }
}

