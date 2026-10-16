using System;
using Il2CppScheduleOne;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using Yesla.Config;

namespace Yesla.Driving
{
    internal enum DriveState { Off, Ready, Driving, Arrived, Aborted }

    /// <summary>
    /// Yesla Assisted Driving for the local player: a destination, an engage key, and the vanilla road AI doing
    /// the driving. Runs on whichever machine the driver sits at - the host through vanilla
    /// <c>VehicleAgent.Navigate</c>, a client through <see cref="ClientNavigate"/>.
    ///
    /// Taking over is instant and needs no key: steering, throttle, brake or handbrake input, leaving the seat, or
    /// the AI giving up all hand the car back.
    /// </summary>
    internal static class Autopilot
    {
        internal static DriveState State { get; private set; } = DriveState.Off;
        internal static Vector3? Destination { get; private set; }
        internal static string DestinationLabel { get; private set; } = string.Empty;
        internal static string LastReason { get; private set; } = string.Empty;

        private static LandVehicle _car;

        /// <summary>The car Assisted Driving is steering right now, or null.</summary>
        internal static LandVehicle DrivenCar => State == DriveState.Driving ? _car : null;

        /// <summary>Input above this on any axis counts as the driver taking over.</summary>
        private const float TakeoverThreshold = 0.5f;

        /// <summary>VehicleAgent.Navigate's own limit before it teleports a car onto the road graph.</summary>
        internal const float MaxDistanceFromRoad = 6f;

        internal static void SetDestination(Vector3 position, string label)
        {
            Destination = position;
            DestinationLabel = string.IsNullOrEmpty(label) ? position.ToString("F0") : label;
            if (State == DriveState.Driving && _car != null)
            {
                Core.Log.Msg("[Autopilot] new destination while driving, rerouting to " + DestinationLabel);
                Navigate(_car);
                return;
            }
            SetState(DriveState.Ready, "destination " + DestinationLabel);
        }

        internal static void ClearDestination()
        {
            if (State == DriveState.Driving) Disengage("destination cleared");
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
            PathFollower.Attach(car, "Assisted Driving", Disengage);
            SetState(DriveState.Driving, "engaged towards " + DestinationLabel);
            Navigate(car);
            why = State == DriveState.Driving ? string.Empty : LastReason;
            return State == DriveState.Driving;
        }

        private static void Navigate(LandVehicle car)
        {
            // The car is wherever the player parked it; vanilla's default would teleport it onto the road graph.
            var settings = new NavigationSettings { ensureProximityToGraph = false, endAtRoad = false };
            VehicleAgent.NavigationCallback callback = (Action<VehicleAgent.ENavigationResult>)OnNavigationResult;
            car.Agent.Navigate(Destination.Value, settings, callback);
        }

        private static void OnNavigationResult(VehicleAgent.ENavigationResult result)
        {
            Core.Log.Msg("[Autopilot] road AI reported " + result);
            if (State != DriveState.Driving) return;
            switch (result)
            {
                case VehicleAgent.ENavigationResult.Complete:
                    Release();
                    SetState(DriveState.Arrived, "arrived at " + DestinationLabel, DriveState.Driving);
                    break;
                case VehicleAgent.ENavigationResult.Failed:
                    Release();
                    SetState(DriveState.Aborted, "no route to " + DestinationLabel, DriveState.Driving);
                    break;
            }
        }

        internal static void Disengage(string reason)
        {
            if (State != DriveState.Driving) return;
            Release();
            SetState(DriveState.Aborted, reason, DriveState.Driving);
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
            if (State == DriveState.Driving)
            {
                if (_car == null) { Disengage("the car is gone"); return; }
                LandVehicle current = CurrentCar();
                if (current == null) { Disengage("left the car"); return; }
                if (current.Pointer != _car.Pointer) { Disengage("sitting in another car (" + current.VehicleName + ")"); return; }
                if (!current.LocalPlayerIsDriver) { Disengage("left the driver seat"); return; }
            }

            if (GameInput.IsTyping) return;
            if (!Input.GetKeyDown(Preferences.EngageKey)) return;
            if (State == DriveState.Driving) { Disengage("engage key pressed"); return; }
            if (!Engage(out string why)) Core.Log.Msg("[Autopilot] cannot engage: " + why);
        }

        /// <summary>
        /// Called from OnLateUpdate. The player's input is still read while the agent drives - the car just ignores
        /// it - so any deliberate input counts as taking over.
        /// </summary>
        internal static void LateTick()
        {
            if (State != DriveState.Driving || _car == null) return;
            Vector2 motion = GameInput.MotionAxis;
            if (Mathf.Abs(motion.x) > TakeoverThreshold) { Disengage("steering input"); return; }
            if (Mathf.Abs(GameInput.VehicleDriveAxis) > TakeoverThreshold) { Disengage("throttle or brake input"); return; }
            if (GameInput.GetButton(GameInput.ButtonCode.VehicleHandbrake)) Disengage("handbrake");
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
            if (State == DriveState.Driving) Release();
            State = DriveState.Off;
            Destination = null;
            DestinationLabel = string.Empty;
            LastReason = string.Empty;
            _car = null;
        }
    }
}
