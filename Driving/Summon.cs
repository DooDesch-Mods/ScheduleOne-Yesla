using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Il2CppPathfinding;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using Yesla.Net;
using Yesla.Vehicle;

namespace Yesla.Driving
{
    internal enum SummonState { Off, Coming, Arrived, Failed }

    /// <summary>
    /// Actual Super Summon: the empty car drives itself over the roads to its owner and stops at the kerb nearest
    /// to them.
    ///
    /// Only the host can drive an empty car. <c>VehicleAgent.Navigate</c> returns unless the caller is the host, and
    /// a car nobody sits in is simulated by the host alone (<c>LandVehicle.ShouldBePhysicallySimulated</c>), its
    /// position reaching the clients through the car's NetworkTransform. So every summon runs here on the host, and
    /// a client asks for one with a request and hears back the result.
    ///
    /// Far from every player the host keeps the empty car kinematic and the road AI slides it along the path; close
    /// to a player it is a physical car again and <see cref="PathFollower"/> steers it.
    /// </summary>
    internal static class Summon
    {
        /// <summary>Within this distance of its owner the car does not need to come.</summary>
        private const float AlreadyHere = 12f;
        /// <summary>A summon that has not arrived after this long is given up.</summary>
        private const float MaxSeconds = 300f;
        /// <summary>A car off the road may be put onto it only while no player is this close.</summary>
        private const float UnseenRadius = 40f;
        /// <summary>Road points near the player tried after the route to the player itself failed.</summary>
        private const int MaxRetries = 3;

        private sealed class Job
        {
            internal LandVehicle Car;
            internal int CarId;
            internal string Owner;
            internal Vector3 Target;
            internal float Started;
            /// <summary>How many road points near the player were tried after the route to the player failed.</summary>
            internal int Retries;
            /// <summary>The road points already tried; two road links often share their closest point.</summary>
            internal readonly List<Vector3> Tried = new List<Vector3>();
        }

        // Host: the running summons, by car network id.
        private static readonly Dictionary<int, Job> Jobs = new Dictionary<int, Job>();

        // Everyone: what the host last said about each car's summon.
        private static readonly Dictionary<int, (SummonState state, string reason)> Status = new Dictionary<int, (SummonState, string)>();

        internal static int Revision { get; private set; }

        /// <summary>Fires on every peer when a summon of one of the local player's cars changes state.</summary>
        internal static event Action<LandVehicle, SummonState, string> MineChanged;

        internal static (SummonState state, string reason) StatusOf(LandVehicle car)
        {
            int id = car != null ? Fleet.NetId(car) : -1;
            return id >= 0 && Status.TryGetValue(id, out var s) ? s : (SummonState.Off, string.Empty);
        }

        // ---- the asking player -------------------------------------------------------------------------------------

        /// <summary>Ask for the car to come to the local player. Returns why not, or null.</summary>
        internal static string Request(LandVehicle car)
        {
            if (car == null) return "No Yesla.";
            if (!Fleet.IsMine(car)) return "This Yesla is not yours.";
            Player local = Player.Local;
            if (local == null) return "No player.";
            Vector3 at = local.transform.position;
            DriveProfile profile = DriveProfile.Current;
            YeslaNet.ToHost("req.summon", Fleet.NetId(car).ToString(CultureInfo.InvariantCulture), Fleet.MyCode(), Vec(at), profile.Id);
            Core.Log.Msg("[Summon] asked " + car.VehicleName + " #" + Fleet.NetId(car) + " to come to " + at.ToString("F0") + " (" + profile.Label + ").");
            return null;
        }

        internal static string Cancel(LandVehicle car)
        {
            if (car == null) return "No Yesla.";
            YeslaNet.ToHost("req.unsummon", Fleet.NetId(car).ToString(CultureInfo.InvariantCulture), Fleet.MyCode());
            Core.Log.Msg("[Summon] asked " + car.VehicleName + " #" + Fleet.NetId(car) + " to stop coming.");
            return null;
        }

        // ---- host --------------------------------------------------------------------------------------------------

        /// <param name="profileId">The requester's drive profile; an older client sends none and gets Standard.</param>
        internal static void HostStart(int carId, string requester, Vector3 playerAt, string profileId)
        {
            LandVehicle car = Fleet.FindById(carId);
            if (car == null) { Report(carId, SummonState.Failed, "The car is gone."); Core.Log.Warning("[Summon] no car #" + carId + "."); return; }

            Fleet.Rec rec = Fleet.HostRec(car);
            if (rec == null || rec.Owner != requester)
            {
                Core.Log.Warning("[Summon] " + requester + " may not summon " + car.VehicleName + " #" + carId + " (owner " + (rec?.Owner ?? "unknown") + ").");
                Report(carId, SummonState.Failed, "This Yesla is not yours.");
                return;
            }
            if (car.IsOccupied) { Report(carId, SummonState.Failed, "Someone is in the car."); Core.Log.Msg("[Summon] refused: #" + carId + " is occupied."); return; }
            if (car.Agent == null) { Report(carId, SummonState.Failed, "This car cannot drive itself."); Core.Log.Error("[Summon] #" + carId + " has no VehicleAgent."); return; }

            float distance = Vector3.Distance(car.transform.position, playerAt);
            if (distance < AlreadyHere) { Report(carId, SummonState.Arrived, "Your car is already here."); Core.Log.Msg("[Summon] #" + carId + " is " + distance.ToString("F0") + " m away - already here."); return; }

            // Parked in a lot or a driveway the car is off the road graph, where the road AI does not start. Vanilla
            // then puts the car onto the nearest road; that jump is only acceptable while no player can see it.
            float fromRoad = car.Agent.GetDistanceFromVehicleGraph();
            bool snapToRoad = false;
            if (fromRoad > Autopilot.MaxDistanceFromRoad)
            {
                string watcher = PlayerNear(car.transform.position, UnseenRadius);
                if (watcher != null)
                {
                    Core.Log.Msg("[Summon] refused: #" + carId + " stands " + fromRoad.ToString("F0") + " m from the road and " + watcher + " is next to it.");
                    Report(carId, SummonState.Failed, "Your car is too far from the road.");
                    return;
                }
                snapToRoad = true;
                Core.Log.Msg("[Summon] #" + carId + " stands " + fromRoad.ToString("F0") + " m from the road with nobody near - putting it onto the road.");
            }

            if (Jobs.TryGetValue(carId, out Job old)) Stop(old, null, "summoned again");

            var job = new Job { Car = car, CarId = carId, Owner = requester, Target = playerAt, Started = Time.time };
            Jobs[carId] = job;
            DriveProfile profile = DriveProfile.Find(profileId);
            if (profile == null)
            {
                Core.Log.Msg("[Summon] request without a known drive profile ('" + profileId + "') - using Standard.");
                profile = DriveProfile.Standard;
            }
            PathFollower.Attach(car, "Summon", profile, reason => Finish(job, SummonState.Failed,
                reason == "stuck" ? "Your car got stuck." : reason == "checkpoint" ? "A police checkpoint blocks the way." : "Summon stopped.", reason),
                driverless: true);
            Report(carId, SummonState.Coming, string.Empty);
            Core.Log.Msg("[Summon] " + car.VehicleName + " #" + carId + " drives to " + requester + " at " + playerAt.ToString("F0") + ", " + distance.ToString("F0") + " m away.");

            if (!snapToRoad) { Go(job, playerAt); return; }
            MelonLoader.MelonCoroutines.Start(SnapThenGo(job));
        }

        /// <summary>
        /// Vanilla's own snap (ensureProximityToGraph) moves the car and searches the route in the same frame, and
        /// that search fails. Moving the car here and searching a moment later finds the route.
        /// </summary>
        private static IEnumerator SnapThenGo(Job job)
        {
            try
            {
                Vector3 from = job.Car.transform.position;
                job.Car.Agent.Teleporter.MoveToGraph(false);
                Core.Log.Msg("[Summon] #" + job.CarId + " put onto the road: " + from.ToString("F0") + " -> " + job.Car.transform.position.ToString("F0") + ".");
            }
            catch (Exception e) { Core.Log.Error("[Summon] could not put #" + job.CarId + " onto the road: " + e.Message); Finish(job, SummonState.Failed, "Your car is too far from the road.", "snap failed"); yield break; }
            yield return new WaitForSeconds(0.5f);
            if (!Jobs.TryGetValue(job.CarId, out Job current) || current != job || job.Car == null) { Core.Log.Msg("[Summon] #" + job.CarId + " summon ended while the car was put onto the road."); yield break; }
            try { Go(job, job.Target); }
            catch (Exception e) { Core.Log.Error("[Summon] #" + job.CarId + " could not start after the snap: " + e.Message); Finish(job, SummonState.Failed, "No route to you.", "start failed"); }
        }

        private static void Go(Job job, Vector3 target)
        {
            // Kerb, not the player's feet: endAtRoad ends the route where it leaves the road network.
            var settings = new NavigationSettings { ensureProximityToGraph = false, endAtRoad = true };
            VehicleAgent.NavigationCallback callback = (Action<VehicleAgent.ENavigationResult>)(r => OnResult(job, r));
            job.Car.Agent.Navigate(target, settings, callback);
        }

        /// <summary>
        /// The route search fails when no car can reach the player's spot: a walled yard, a building, a field. The
        /// car then heads for a road point near them instead, one of the nearest road links in turn - a point on a
        /// road link always has a route. Called a frame later: vanilla ends the drive right after the Failed
        /// callback returns, which would cancel a Navigate made inside it.
        /// </summary>
        private static IEnumerator RetryNearestRoad(Job job)
        {
            yield return null;
            if (!Jobs.TryGetValue(job.CarId, out Job current) || current != job || job.Car == null) { Core.Log.Msg("[Summon] #" + job.CarId + " retry dropped: the summon ended."); yield break; }
            Vector3? road = null;
            try
            {
                var links = NodeLink.GetClosestLinks(job.Target, MaxRetries * 2);
                for (int i = 0; links != null && i < links.Count && road == null; i++)
                {
                    Vector3 point = links[i].GetClosestPoint(job.Target);
                    if (!job.Tried.Exists(t => Vector3.Distance(t, point) < 1f)) road = point;
                }
            }
            catch (Exception e) { Core.Log.Error("[Summon] could not find the road nearest to " + job.Target.ToString("F0") + ": " + e.Message); Finish(job, SummonState.Failed, "No route to you.", "no road near the player"); yield break; }
            if (road == null) { Finish(job, SummonState.Failed, "No route to you.", "no untried road point left near the player"); yield break; }
            job.Tried.Add(road.Value);
            Core.Log.Msg("[Summon] #" + job.CarId + ": no route to " + job.Target.ToString("F0") + " - trying road point " + job.Retries + " at " + road.Value.ToString("F0") + ", " + Vector3.Distance(road.Value, job.Target).ToString("F0") + " m from the player.");
            try { Go(job, road.Value); }
            catch (Exception e) { Core.Log.Error("[Summon] retry for #" + job.CarId + " failed: " + e.Message); Finish(job, SummonState.Failed, "No route to you.", "retry failed"); }
        }

        internal static void HostCancel(int carId, string requester)
        {
            if (!Jobs.TryGetValue(carId, out Job job)) { Core.Log.Msg("[Summon] cancel for #" + carId + ": nothing running."); Report(carId, SummonState.Off, string.Empty); return; }
            if (job.Owner != requester) { Core.Log.Warning("[Summon] " + requester + " may not cancel the summon of #" + carId + "."); return; }
            Finish(job, SummonState.Off, string.Empty, "cancelled by the owner");
        }

        private static void OnResult(Job job, VehicleAgent.ENavigationResult result)
        {
            Core.Log.Msg("[Summon] road AI reported " + result + " for #" + job.CarId + ".");
            if (!Jobs.TryGetValue(job.CarId, out Job current) || current != job) return;
            switch (result)
            {
                case VehicleAgent.ENavigationResult.Complete: Finish(job, SummonState.Arrived, "Your car is here.", "arrived"); break;
                case VehicleAgent.ENavigationResult.Failed:
#if DEBUG
                    if (job.Retries == 0) Debugging.NavCheck.Run(job.Car, job.Target);
#endif
                    if (job.Retries < MaxRetries) { job.Retries++; MelonLoader.MelonCoroutines.Start(RetryNearestRoad(job)); break; }
                    Finish(job, SummonState.Failed, "No route to you.", "no route, " + MaxRetries + " road points near the player tried");
                    break;
                case VehicleAgent.ENavigationResult.Stopped: Finish(job, SummonState.Failed, "Summon stopped.", "the road AI stopped"); break;
            }
        }

        /// <summary>Host, from OnUpdate: someone got in, the car vanished, or the summon took too long.</summary>
        internal static void Tick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.Summon.Tick");
#endif
            if (!YeslaNet.IsHost) return;
            ClearSettled();
            if (Jobs.Count == 0) return;
            var jobs = new List<Job>(Jobs.Values);
            foreach (Job job in jobs)
            {
                if (job.Car == null) { Finish(job, SummonState.Failed, "The car is gone.", "car destroyed"); continue; }
                if (job.Car.IsOccupied) { Finish(job, SummonState.Off, string.Empty, "someone got in"); continue; }
                if (Time.time - job.Started > MaxSeconds) Finish(job, SummonState.Failed, "Summon took too long.", "timed out after " + MaxSeconds + " s");
            }
        }

        private static float _nextSettledCheck;

        /// <summary>Host: "Your car is here." ends when someone gets in; the phone stops showing it.</summary>
        private static void ClearSettled()
        {
            if (Status.Count == 0 || Time.time < _nextSettledCheck) return;
            _nextSettledCheck = Time.time + 1f;
            List<int> settled = null;
            foreach (var kv in Status)
            {
                if (kv.Value.state == SummonState.Off || Jobs.ContainsKey(kv.Key)) continue;
                LandVehicle car = Fleet.FindById(kv.Key);
                if (car != null && !car.IsOccupied) continue;
                (settled ??= new List<int>()).Add(kv.Key);
            }
            if (settled == null) return;
            foreach (int id in settled)
            {
                Core.Log.Msg("[Summon] #" + id + " was " + Status[id].state + "; someone got in or the car is gone - status cleared.");
                Report(id, SummonState.Off, string.Empty);
            }
        }

        private static void Finish(Job job, SummonState state, string message, string why)
        {
            Stop(job, message, why);
            Report(job.CarId, state, message);
        }

        private static void Stop(Job job, string message, string why)
        {
            if (Jobs.TryGetValue(job.CarId, out Job current) && current == job) Jobs.Remove(job.CarId);
            LandVehicle car = job.Car;
            PathFollower.Detach(car);
            Core.Log.Msg("[Summon] #" + job.CarId + " stops: " + why + ".");
            if (car == null) return;
            try
            {
                if (car.Agent != null && car.Agent.AutoDriving)
                {
                    // Remove the job first so the Stopped callback StopNavigating fires is ignored.
                    car.Agent.StopNavigating();
                    car.Agent.Flags.ResetFlags();
                }
                car.overrideControls = false;
            }
            catch (Exception e) { Core.Log.Error("[Summon] could not release #" + job.CarId + ": " + e.Message); }
        }

        private static void Report(int carId, SummonState state, string message) =>
            YeslaNet.ToAll("summon", carId.ToString(CultureInfo.InvariantCulture), state.ToString(), message ?? string.Empty);

        // ---- everyone ----------------------------------------------------------------------------------------------

        internal static void ApplyState(string carIdText, string stateText, string message)
        {
            if (!int.TryParse(carIdText, out int carId) || !Enum.TryParse(stateText, out SummonState state))
            {
                Core.Log.Warning("[Summon] unreadable status '" + carIdText + "' '" + stateText + "'.");
                return;
            }
            Status[carId] = (state, message);
            Revision++;
            LandVehicle car = Fleet.FindById(carId);
            if (car != null && Fleet.IsMine(car))
            {
                Core.Log.Msg("[Summon] my " + car.VehicleName + " #" + carId + ": " + state + (message.Length > 0 ? " (" + message + ")" : "") + ".");
                try { MineChanged?.Invoke(car, state, message); }
                catch (Exception e) { Core.Log.Error("[Summon] a listener failed: " + e); }
            }
        }

        /// <summary>The name of a player within <paramref name="radius"/> of <paramref name="at"/>, or null.</summary>
        private static string PlayerNear(Vector3 at, float radius)
        {
            var players = Player.PlayerList;
            for (int i = 0; i < players.Count; i++)
            {
                Player p = players[i];
                if (p != null && Vector3.Distance(p.transform.position, at) < radius) return p.PlayerName;
            }
            return null;
        }

        internal static string Vec(Vector3 v) =>
            v.x.ToString("F2", CultureInfo.InvariantCulture) + "," + v.y.ToString("F2", CultureInfo.InvariantCulture) + "," + v.z.ToString("F2", CultureInfo.InvariantCulture);

        internal static bool TryVec(string s, out Vector3 v)
        {
            v = Vector3.zero;
            string[] p = (s ?? string.Empty).Split(',');
            if (p.Length != 3) return false;
            if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        internal static string Describe()
        {
            var sb = new System.Text.StringBuilder("summon jobs=" + Jobs.Count);
            foreach (var j in Jobs.Values)
                sb.Append(" [#").Append(j.CarId).Append(" to ").Append(j.Owner).Append(" dist=")
                  .Append(j.Car != null ? Vector3.Distance(j.Car.transform.position, j.Target).ToString("F0") : "-")
                  .Append(" speed=").Append(j.Car != null ? j.Car.Speed_Kmh.ToString("F0") : "-")
                  .Append(" kinematic=").Append(j.Car?.Agent != null && j.Car.Agent.KinematicMode)
                  .Append(" follower=").Append(PathFollower.LastLineFor(j.Car)).Append(']');
            foreach (var kv in Status) sb.Append(" status#").Append(kv.Key).Append('=').Append(kv.Value.state);
            return sb.ToString();
        }

        internal static void Reset()
        {
            Jobs.Clear();
            Status.Clear();
        }
    }
}
