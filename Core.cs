using System;
using MelonLoader;
using Yesla.Config;
using Yesla.Driving;

[assembly: MelonInfo(typeof(Yesla.Core), "Yesla", DooDesch.ModVersion.Current, "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Yesla")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Yesla
{
    /// <summary>
    /// MelonLoader entry point for Yesla - the Yesla Model 3, an electric sedan sold at the dealership, with Yesla
    /// Assisted Driving: send a destination from the phone, press the key, and the car drives itself there on the
    /// game's own road AI.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private bool _inWorld;

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();
            Net.YeslaNet.Apply(HarmonyInstance);
            Net.Remote.Init();
            Phone.YeslaApp.Register();

            // No PatchAll here: MelonLoader applies the [HarmonyPatch] classes of a MelonMod itself, and a second
            // PatchAll runs every prefix and postfix twice per call.
            int patched = 0;
            foreach (var _ in HarmonyInstance.GetPatchedMethods()) patched++;
            Log.Msg("[Core] Harmony patches applied by MelonLoader on " + patched + " method(s).");

#if DEBUG
            Log.Msg("Yesla (DEBUG) loaded. Type 'yesla help' in the console.");
#else
            Log.Msg("Yesla loaded.");
#endif
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _inWorld = sceneName == "Main";
            if (!_inWorld) ResetWorld();
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName != "Main") return;
            // Normally VehicleManager.Awake has registered the car already; this covers a mod loaded after it.
            if (!NetworkSingleton<Il2CppScheduleOne.Vehicles.VehicleManager>.InstanceExists)
            {
                Log.Warning("[Core] Main is up but there is no VehicleManager - the Yesla Model 3 is not registered.");
                return;
            }
            Vehicle.YeslaVehicleType.Register(NetworkSingleton<Il2CppScheduleOne.Vehicles.VehicleManager>.Instance);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            _inWorld = false;
            ResetWorld();
        }

        private static void ResetWorld()
        {
            Autopilot.Reset();
            Parking.Reset();
            Summon.Reset();
            PathFollower.Reset();
            RoadRouter.Reset();
            Vehicle.Sentry.Reset();
            Vehicle.Fleet.Reset();
            Vehicle.Plate.Reset();
            DestinationMarker.Reset();
            Vehicle.Indicators.Reset();
            Vehicle.Pws.Reset();
            Vehicle.AutoLights.Reset();
            KeyHint.Reset();
            FirstPerson.Reset();
            Visual.Viz.Reset();
            Phone.YeslaApp.Reset();
        }

        public override void OnUpdate()
        {
            if (!_inWorld) return;
#if DEBUG
            Debugging.JitterProbe.Own.Start();
            Debugging.ConsoleRegistration.EnsureRegistered();
            Debugging.DebugConsolePatch.FlushToConsole();
#endif
            Vehicle.Fleet.Tick();
            Autopilot.Tick();
            Summon.Tick();
            if (Net.YeslaNet.IsHost) Parking.HostTick();
            Vehicle.Sentry.Tick();
            Vehicle.Plate.Tick();
            Vehicle.Indicators.Tick();
            Vehicle.Pws.Tick();
            if (Net.YeslaNet.IsHost) Vehicle.AutoLights.Tick();
            DestinationMarker.Tick();
            FirstPerson.Tick();
            Visual.Viz.Tick();
            KeyHint.Tick();
            Phone.YeslaApp.Tick();
#if DEBUG
            Debugging.JitterProbe.Own.Stop();
#endif
        }

        public override void OnLateUpdate()
        {
            if (!_inWorld) return;
#if DEBUG
            Debugging.JitterProbe.Own.Start();
#endif
            // After VehicleAgent.LateUpdate has written its overrides; reads the driver's own input for a takeover.
            Autopilot.LateTick();
            Parking.LateTick();
            PathFollower.LateTick();
            Visual.Viz.LateTick();
#if DEBUG
            Debugging.AccelTest.LateTick();
            Debugging.FrameBurst.LateTick();
            Debugging.JitterProbe.Own.Stop();
            Debugging.JitterProbe.LateTick();
#endif
        }
    }
}
