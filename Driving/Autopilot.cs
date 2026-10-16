using System;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using Yesla.Config;

namespace Yesla.Driving
{
    internal enum DriveState { Off, Ready, Driving, Parking, Arrived, Aborted }

    /// <summary>
    /// Yesla Assisted Driving for the local player: a destination, an engage key, and the vanilla road AI doing
    /// the driving. Runs on whichever machine the driver sits at - the host through vanilla
    /// <c>VehicleAgent.Navigate</c>, a client through <see cref="ClientNavigate"/>.
    ///
    /// Taking over is instant and needs no key: steering, throttle, brake or handbrake input, leaving the seat, or
    /// the AI giving up all hand the car back.
    ///
    /// At the destination the car parks itself (<see cref="Parking"/>): in a free spot of a parking lot nearby, which
    /// the road AI heads for from the start, or at the kerb when there is none.
    /// </summary>
    internal static class Autopilot
    {
        internal static DriveState State { get; private set; } = DriveState.Off;
        internal static Vector3? Destination { get; private set; }
        internal static string DestinationLabel { get; private set; } = string.Empty;
        internal static string LastReason { get; private set; } = string.Empty;

        private static LandVehicle _car;
        private static ParkTarget _park;

        /// <summary>Driving towards the destination, or parking there.</summary>
        internal static bool Active => State == DriveState.Driving || State == DriveState.Parking;

        /// <summary>The spot the car parks in or drives to, for the visualisation; null without one.</summary>
        internal static ParkTarget PlannedSpot => Parking.Current ?? (State == DriveState.Driving ? _park : null);

        /// <summary>The car Assisted Driving is steering right now, or null.</summary>
        internal static LandVehicle DrivenCar => Active ? _car : null;

        /// <summary>Input above this on any axis counts as the driver taking over.</summary>
        private const float TakeoverThreshold = 0.5f;

        /// <summary>VehicleAgent.Navigate's own limit before it teleports a car onto the road graph.</summary>
        internal const float MaxDistanceFromRoad = 6f;

        internal static void SetDestination(Vector3 position, string label)
        {
            Destination = position;
            DestinationLabel = string.IsNullOrEmpty(label) ? position.ToString("F0") : label;
            if (Active && _car != null)
            {
                Core.Log.Msg("[Autopilot] new destination while driving, rerouting to " + DestinationLabel);
                if (State == DriveState.Parking)
                {
                    Parking.Cancel("new destination");
                    PathFollower.Attach(_car, "Assisted Driving", DriveProfile.Current, Disengage);
                    SetState(DriveState.Driving, "rerouting to " + DestinationLabel);
                }
                Navigate(_car);
                return;
            }
            SetState(DriveState.Ready, "destination " + DestinationLabel);
        }

        internal static void ClearDestination()
        {
            if (Active) Disengage("destination cleared");
            Destination = null;
            DestinationLabel = string.Empty;
            SetState(DriveState.Off, "destination cleared");
        }

        /// <summary>The car the local player is sitting in, or null.</summary>
        internal static LandVehicle CurrentCar()
        {
            Player local = Player.Local;
            if (local == null || local.CurrentVehicle == null) return null;
            return local.CurrentVehicle.GetComponent<LandVehicle>();
        }

        internal static bool Engage(out string why)
        {
            LandVehicle car = CurrentCar();
            if (car == null) { why = "not in a car"; Core.Log.Msg("[Autopilot] engage refused: " + why); return false; }
            if (!Phone.YeslaApp.IsYesla(car)) { why = "not a Yesla"; Core.Log.Msg("[Autopilot] engage refused: " + why + " (" + car.VehicleCode + ")"); return false; }
            if (!Vehicle.Fleet.IsMine(car)) { why = "not your Yesla"; Core.Log.Msg("[Autopilot] engage refused: " + why + " (" + Vehicle.Fleet.Describe(car) + ")"); return false; }
            if (!car.LocalPlayerIsDriver) { why = "not in the driver seat"; Core.Log.Msg("[Autopilot] engage refused: " + why); return false; }
            if (Destination == null) { why = "no destination"; Core.Log.Msg("[Autopilot] engage refused: " + why); return false; }
            if (car.Agent == null) { why = "this car has no road AI"; Core.Log.Warning("[Autopilot] engage refused: " + why); return false; }

            // The road AI only starts within 6 m of its graph; vanilla would teleport the car there, we refuse.
            float fromRoad = car.Agent.GetDistanceFromVehicleGraph();
            if (fromRoad > MaxDistanceFromRoad)
            {
                why = "too far from the road (" + fromRoad.ToString("F0") + " m)";
                Core.Log.Msg("[Autopilot] engage refused: " + why);
                return false;
            }

            // State first: a route that cannot be found reports Failed synchronously from inside Navigate.
            _car = car;
            Parking.Forget();
            PathFollower.Attach(car, "Assisted Driving", DriveProfile.Current, Disengage);
            SetState(DriveState.Driving, "engaged towards " + DestinationLabel);
            Navigate(car);
            why = State == DriveState.Driving ? string.Empty : LastReason;
            return State == DriveState.Driving;
        }

        private static void Navigate(LandVehicle car, ParkTarget park = null)
        {
            string none = string.Empty;
            _park = park ?? Parking.Find(car, Destination.Value, out none);
            if (_park == null) Core.Log.Msg("[Autopilot] no parking spot at " + DestinationLabel + " (" + none + ") - will pull over at the kerb.");
            Vector3 target = _park != null ? _park.Entry : Destination.Value;
            // The car is wherever the player parked it; vanilla's default would teleport it onto the road graph.
            var settings = new NavigationSettings { ensureProximityToGraph = false, endAtRoad = false };
            VehicleAgent.NavigationCallback callback = (Action<VehicleAgent.ENavigationResult>)OnNavigationResult;
            car.Agent.Navigate(target, settings, callback);
        }

        private static void OnNavigationResult(VehicleAgent.ENavigationResult result)
        {
            Core.Log.Msg("[Autopilot] road AI reported " + result);
            if (State != DriveState.Driving) return;
            switch (result)
            {
                case VehicleAgent.ENavigationResult.Complete:
                    StartParking();
                    break;
                case VehicleAgent.ENavigationResult.Failed:
                    Release();
                    SetState(DriveState.Aborted, "no route to " + DestinationLabel, DriveState.Driving);
                    break;
            }
        }

        /// <summary>The road AI is done, or the spot is in reach; the parking manoeuvre takes the car the last metres.</summary>
        private static void StartParking(string why = null)
        {
            LandVehicle car = _car;
            StopAgent(car);
            ParkTarget target = _park;
            if (target != null && !target.Kerb && (!Parking.Free(car, target, out string what) || (target.Spot.OccupantVehicle != null && target.Spot.OccupantVehicle.Pointer != car.Pointer)))
            {
                Core.Log.Msg("[Autopilot] " + target.Label + " was taken on the way" + (what.Length > 0 ? " (" + what + ")" : "") + " - looking again.");
                target = Parking.Find(car, Destination.Value, out _);
            }
            target ??= Parking.Kerb(car);
            if (target.Kerb && !Parking.Free(car, target, out string kerbWhat))
            {
                Release();
                SetState(DriveState.Arrived, "arrived at " + DestinationLabel + ", the kerb is taken (" + kerbWhat + ")", DriveState.Driving);
                return;
            }
            SetState(DriveState.Parking, (why ?? "arrived at " + DestinationLabel) + ", parking in " + target.Label, DriveState.Driving);
            _park = target;
            Parking.Start(car, target, OnParked);
        }

        private static void OnParked(bool ok, string why)
        {
            if (State != DriveState.Parking) return;
            ParkTarget next = Parking.TakeNext();
            if (next != null && _car != null)
            {
                // The spot was blocked; the road AI takes the car to the next one, which parks as usual.
                PathFollower.Attach(_car, "Assisted Driving", DriveProfile.Current, Disengage);
                SetState(DriveState.Driving, why, DriveState.Parking);
                Navigate(_car, next);
                return;
            }
            string where = _park != null && !_park.Kerb ? "a parking spot" : "the kerb";
            Release();
            SetState(DriveState.Arrived, ok ? "parked at " + where + " at " + DestinationLabel : "arrived at " + DestinationLabel + ", could not park: " + why, DriveState.Parking);
        }

        internal static void Disengage(string reason)
        {
            if (!Active) return;
            DriveState was = State;
            Parking.Cancel(reason);
            Release();
            SetState(DriveState.Aborted, reason, was);
        }

        /// <summary>Stops the road AI and the path follower but keeps the car under Assisted Driving.</summary>
        private static void StopAgent(LandVehicle car)
        {
            PathFollower.Detach(car);
            if (car == null || car.Agent == null) return;
            try
            {
                // Parking first, so the Stopped callback that StopNavigating fires is ignored.
                State = DriveState.Parking;
                car.Agent.StopNavigating();
                car.Agent.Flags.ResetFlags();
            }
            catch (Exception e) { Core.Log.Error("[Autopilot] could not stop the road AI for parking: " + e.Message); }
        }

        /// <summary>Stops the agent and hands the controls back. Safe when the car is already gone.</summary>
        private static void Release()
        {
            LandVehicle car = _car;
            _car = null;
            PathFollower.Detach(car);
            if (car == null) { Core.Log.Warning("[Autopilot] release with no car - nothing to stop"); return; }
            try
            {
                // Clear the state first so the Stopped callback that StopNavigating fires is ignored.
                State = DriveState.Off;
                if (car.Agent != null)
                {
                    car.Agent.StopNavigating();
                    car.Agent.Flags.ResetFlags();
                }
                car.overrideControls = false;
            }
            catch (Exception e) { Core.Log.Error("[Autopilot] could not release the car: " + e.Message); }
        }

        /// <summary>Called from OnUpdate: engage key and leaving the seat.</summary>
        internal static void Tick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.Autopilot.Tick");
#endif
            if (Active)
            {
                if (_car == null) { Disengage("the car is gone"); return; }
                LandVehicle current = CurrentCar();
                if (current == null) { Disengage("left the car"); return; }
                if (current.Pointer != _car.Pointer) { Disengage("sitting in another car (" + current.VehicleName + ")"); return; }
                if (!current.LocalPlayerIsDriver) { Disengage("left the driver seat"); return; }
            }

            if (GameInput.IsTyping || PhoneOpen()) return;
            if (!Input.GetKeyDown(Preferences.EngageKey)) return;
            if (Active) { Disengage("engage key pressed"); return; }
            if (!Engage(out string why)) Core.Log.Msg("[Autopilot] cannot engage: " + why);
        }

        /// <summary>
        /// Called from OnLateUpdate. The player's input is still read while the agent drives - the car just ignores
        /// it - so any deliberate input counts as taking over. With the phone open the car ignores the driving keys
        /// in vanilla too, so they do not take over then.
        /// </summary>
        internal static void LateTick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.Autopilot.LateTick");
#endif
            if (!Active || _car == null) return;
            if (PhoneOpen())
            {
                if (!_phoneWasOpen) Core.Log.Msg("[Autopilot] phone open - driving keys do not take over until it closes.");
                _phoneWasOpen = true;
                return;
            }
            if (_phoneWasOpen) { _phoneWasOpen = false; Core.Log.Msg("[Autopilot] phone closed - driving keys take over again."); }
            Vector2 motion = GameInput.MotionAxis;
            if (Mathf.Abs(motion.x) > TakeoverThreshold) { Disengage("steering input"); return; }
            if (Mathf.Abs(GameInput.VehicleDriveAxis) > TakeoverThreshold) { Disengage("throttle or brake input"); return; }
            if (GameInput.GetButton(GameInput.ButtonCode.VehicleHandbrake)) { Disengage("handbrake"); return; }

            if (State == DriveState.Driving && _park != null && !_park.Kerb && Time.time >= _nextReach)
            {
                _nextReach = Time.time + 0.25f;
                if (Parking.InReach(_car, _park, out string reach)) StartParking("in reach of " + _park.Label + ", " + reach);
            }
        }

        private static bool _phoneWasOpen;
        private static float _nextReach;

        private static bool PhoneOpen()
        {
            return PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.InstanceExists
                && PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance.IsOpen;
        }

        internal static string Describe()
        {
            string car = _car != null ? _car.VehicleName : (CurrentCar()?.VehicleName ?? "none");
            string speed = _car != null ? _car.Speed_Kmh.ToString("F0") + " km/h" : "-";
            string agent = _car?.Agent != null ? (" autoDriving=" + _car.Agent.AutoDriving + " pathKnown=" + (_car.Agent.path != null)) : "";
            return "state=" + State + " car=" + car + " speed=" + speed + " dest=" + (DestinationLabel.Length > 0 ? DestinationLabel : "none")
                 + " last=" + (LastReason.Length > 0 ? LastReason : "-") + agent;
        }

        /// <param name="from">The state to log as the previous one, when Release has already cleared it.</param>
        private static void SetState(DriveState next, string reason, DriveState? from = null)
        {
            LastReason = reason;
            DriveState was = from ?? State;
            if (was == next) { Core.Log.Msg("[Autopilot] " + next + " (" + reason + ")"); State = next; return; }
            Core.Log.Msg("[Autopilot] " + was + " -> " + next + " (" + reason + ")");
            State = next;
        }

        internal static void Reset()
        {
            if (Active) Release();
            State = DriveState.Off;
            _park = null;
            Destination = null;
            DestinationLabel = string.Empty;
            LastReason = string.Empty;
            _car = null;
        }
    }
}
