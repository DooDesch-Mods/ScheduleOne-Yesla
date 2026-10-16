using System;
using HarmonyLib;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Vehicles;
using Yesla.Config;

namespace Yesla.Driving
{
    /// <summary>
    /// First-person view inside a Yesla: the view key puts the camera at the eyes of the local player's seat and the
    /// mouse looks around the cabin. Pressing the key again, or leaving the car, returns to the game's chase camera.
    ///
    /// The game's VehicleCamera owns the camera while the player sits in a car (PlayerCamera keeps its hands off an
    /// overridden transform), so the view is written in a postfix to VehicleCamera.LateUpdate: same frame, after the
    /// chase camera, nothing later moves it again.
    /// </summary>
    [HarmonyPatch(typeof(VehicleCamera), nameof(VehicleCamera.LateUpdate))]
    internal static class FirstPerson
    {
        private const float MaxYaw = 135f;
        private const float MinPitch = -60f;
        private const float MaxPitch = 50f;
        private const float NearClip = 0.03f;

        /// <summary>Eye position in the seat's local space; the seat transform sits about at hip height, the roof 0.5 m above it.</summary>
        internal static Vector3 EyeOffset = new Vector3(0f, 0.22f, -0.1f);

        internal static bool Active { get; private set; }

        private static float _yaw, _pitch;
        private static float _savedNearClip = -1f;
        private static bool _announced;

        /// <summary>Called from OnUpdate: the view key, and dropping the view when the player is no longer in a Yesla.</summary>
        internal static void Tick()
        {
            LandVehicle car = Autopilot.CurrentCar();
            bool inYesla = car != null && Phone.YeslaApp.IsYesla(car);
            if (Active && !inYesla) { Set(false, car == null ? "left the car" : "not a Yesla"); return; }
            if (!inYesla || GameInput.IsTyping) return;
            if (Input.GetKeyDown(Preferences.ViewKey)) Set(!Active, "view key pressed");
        }

        internal static void Reset()
        {
            if (Active) Set(false, "world unloaded");
            _announced = false;
        }

        internal static void Set(bool on, string reason)
        {
            if (on == Active) return;
            Active = on;
            _yaw = 0f;
            _pitch = 0f;
            Camera cam = MainCamera();
            if (cam != null)
            {
                if (on) { _savedNearClip = cam.nearClipPlane; cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, NearClip); }
                else if (_savedNearClip > 0f) cam.nearClipPlane = _savedNearClip;
            }
            else Core.Log.Warning("[FirstPerson] no player camera - the near clip plane stays as it is.");
            Core.Log.Msg("[FirstPerson] " + (on ? "on" : "off") + " (" + reason + ").");
        }

        private static void Postfix(VehicleCamera __instance)
        {
            if (!Active) return;
            try
            {
                LandVehicle car = __instance.vehicle;
                Player local = Player.Local;
                if (car == null || local == null || !car.LocalPlayerIsInVehicle) return;
                VehicleSeat seat = local.CurrentVehicleSeat;
                if (seat == null)
                {
                    Set(false, "the player has no seat in this car");
                    return;
                }

                if (car.State != null && car.State.IsAcceptingInput)
                {
                    float sens = Singleton<Settings>.InstanceExists ? Singleton<Settings>.Instance.LookSensitivity : 1f;
                    Vector2 mouse = GameInput.MouseDelta;
                    _yaw = Mathf.Clamp(_yaw + mouse.x * 0.6f * sens, -MaxYaw, MaxYaw);
                    _pitch = Mathf.Clamp(_pitch - mouse.y * 0.6f * sens, MinPitch, MaxPitch);
                }

                Transform cam = PlayerSingleton<PlayerCamera>.Instance.transform;
                Transform s = seat.transform;
                cam.position = s.TransformPoint(EyeOffset);
                cam.rotation = car.transform.rotation * Quaternion.Euler(_pitch, _yaw, 0f);

                if (!_announced)
                {
                    _announced = true;
                    Core.Log.Msg("[FirstPerson] camera at the " + (seat.isDriverSeat ? "driver" : "passenger") + " seat eyes "
                        + cam.position.ToString("F2") + ", seat " + s.position.ToString("F2") + ", car " + car.transform.position.ToString("F2") + ".");
                }
            }
            catch (Exception e)
            {
                Core.Log.Error("[FirstPerson] could not place the camera, back to the chase camera: " + e);
                Set(false, "camera error");
            }
        }

        private static Camera MainCamera()
        {
            return PlayerSingleton<PlayerCamera>.InstanceExists ? PlayerSingleton<PlayerCamera>.Instance.Camera : null;
        }

        internal static string Describe()
        {
            return "firstPerson=" + (Active ? "on" : "off") + " eye=" + EyeOffset.ToString("F2") + " yaw=" + _yaw.ToString("F0") + " pitch=" + _pitch.ToString("F0");
        }
    }
}
