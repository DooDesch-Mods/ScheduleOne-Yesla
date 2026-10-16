using System;
using Il2CppScheduleOne;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.Vehicles;
using Yesla.Config;
using Yesla.Driving;

namespace Yesla.Visual
{
    internal enum VizMode { Hud, Full, Off }

    /// <summary>
    /// Runs the driving visualisation while the local player sits in a Yesla. The visualisation key cycles the screen
    /// view: panel, full screen, off. The centre screen in the car shows the picture in every view; in first person the
    /// panel steps aside because the car's own screen shows the same thing.
    /// </summary>
    internal static class Viz
    {
        internal static VizMode Mode { get; private set; } = VizMode.Hud;

        private static LandVehicle _car;
        private static bool _failed;

        internal static void Tick()
        {
            LandVehicle car = Autopilot.CurrentCar();
            bool inYesla = car != null && Phone.YeslaApp.IsYesla(car);
            if (!inYesla)
            {
                if (_car != null) Leave(car == null ? "left the car" : "not a Yesla");
                return;
            }
            if (_car == null || _car.Pointer != car.Pointer)
            {
                _car = car;
                Core.Log.Msg("[Viz] in a Yesla (" + car.name + "), view " + Mode + ".");
            }
            if (!GameInput.IsTyping && Input.GetKeyDown(Preferences.VizKey)) Cycle("visualisation key");
        }

        /// <summary>After the car has moved this frame: render and place the picture.</summary>
        internal static void LateTick()
        {
            if (_car == null || _failed) return;
            try
            {
                if (!VizScene.Ensure(_car)) { _failed = true; Core.Log.Error("[Viz] the visualisation could not be built and stays off until the world reloads."); return; }
                bool hidden = UiBlocks();
                VizMode screen = hidden ? VizMode.Off : Mode == VizMode.Hud && FirstPerson.Active ? VizMode.Off : Mode;
                if (FirstPerson.Active || screen != VizMode.Off) VizScene.Tick(_car);
                VizHud.Show(screen);
                CenterScreen.Show(_car);
                Cabin.Show(_car);
                CabinLight.Show(_car, FirstPerson.Active);
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Log.Error("[Viz] the visualisation failed and is switched off until the world reloads: " + e);
                VizHud.Show(VizMode.Off);
            }
        }

        internal static void Set(VizMode mode, string reason)
        {
            Mode = mode;
            Core.Log.Msg("[Viz] view " + mode + " (" + reason + ").");
        }

        private static void Cycle(string reason) =>
            Set(Mode == VizMode.Hud ? VizMode.Full : Mode == VizMode.Full ? VizMode.Off : VizMode.Hud, reason);

        /// <summary>The phone and the pause menu sit above the picture; it steps aside for them.</summary>
        private static bool UiBlocks()
        {
            if (PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.InstanceExists && PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance.IsOpen) return true;
            if (Singleton<PauseMenu>.InstanceExists && Singleton<PauseMenu>.Instance.IsPaused) return true;
            return false;
        }

        private static void Leave(string reason)
        {
            Core.Log.Msg("[Viz] off (" + reason + ").");
            _car = null;
            VizHud.Show(VizMode.Off);
            CenterScreen.Show(null);
            Cabin.Show(null);
            CabinLight.Show(null, false);
        }

        internal static void Reset()
        {
            _car = null;
            _failed = false;
            CenterScreen.Reset();
            Cabin.Reset();
            CabinLight.Reset();
            VizHud.Destroy();
            VizScene.Destroy();
        }
    }
}
