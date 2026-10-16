using System;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using Il2CppScheduleOne.Math;

namespace Yesla.Driving
{
    /// <summary>
    /// Lets a co-op client drive its own car with the vanilla road AI.
    ///
    /// <c>VehicleAgent.Navigate</c> returns before doing anything unless the caller is the host
    /// (decompiled mono VehicleAgent.cs:726). A client in the driver seat owns the car and simulates its physics
    /// itself (LandVehicle.cs:1084), and the override fields the agent writes are plain local fields, so a host-side
    /// agent could never steer it. The rest of the agent - path following, speed zones, sensors, traffic lights -
    /// has no host gate. This prefix runs the body of <c>Navigate</c> after that gate, only for a car the local
    /// player is driving on a client. Everything else, including every NPC car, goes through vanilla untouched.
    /// </summary>
    [HarmonyPatch(typeof(VehicleAgent), nameof(VehicleAgent.Navigate))]
    internal static class ClientNavigate
    {
        private static bool Prefix(VehicleAgent __instance, Vector3 location, NavigationSettings settings, VehicleAgent.NavigationCallback callback)
        {
            try
            {
                if (InstanceFinder.IsHost) return true;
                LandVehicle vehicle = __instance.vehicle;
                if (vehicle == null || !vehicle.LocalPlayerIsDriver) return true;

                Run(__instance, vehicle, location, settings, callback);
                return false;
            }
            catch (Exception e)
            {
                Core.Log.Error("[ClientNavigate] client-side navigation failed, falling back to vanilla (which does nothing on a client): " + e);
                return true;
            }
        }

        /// <summary>The body of vanilla <c>Navigate</c> from the line after its host check, member for member.</summary>
        private static void Run(VehicleAgent agent, LandVehicle vehicle, Vector3 location, NavigationSettings settings, VehicleAgent.NavigationCallback callback)
        {
            if (agent.navigationCalculationRoutine != null)
            {
                agent.StopCoroutine(agent.navigationCalculationRoutine);
                agent.navigationCalculationRoutine = null;
            }
            if (agent.reverseCoroutine != null) agent.StopReversing();

            agent.path = null;
            agent.timeOnLastNavigationCall = Time.timeSinceLevelLoad;
            if (settings == null) settings = new NavigationSettings();

            if (agent.GetDistanceFromVehicleGraph() > 6f)
            {
                if (settings.ensureProximityToGraph)
                {
                    agent.Teleporter.MoveToGraph();
                }
                else
                {
                    Core.Log.Warning("[ClientNavigate] car is more than 6 m from the road graph and may not teleport - navigation failed.");
                    callback?.Invoke(VehicleAgent.ENavigationResult.Failed);
                    return;
                }
            }

            agent.TargetLocation = location;
            agent.AutoDriving = true;
            agent.storedNavigationCallback = callback;
            var sensors = agent.sensors;
            for (int i = 0; i < sensors.Length; i++) sensors[i].Enabled = true;
            vehicle.OverrideMaxSteerAngle(35f);
            vehicle.overrideControls = true;
            agent.currentNavigationSettings = settings;

            NavigationUtility.NavigationCalculationCallback done =
                (Action<NavigationUtility.ENavigationCalculationResult, PathSmoothingUtility.SmoothedPath>)
                ((result, path) => agent.NavigationCalculationCallback(result, path));
            agent.navigationCalculationRoutine = NavigationUtility.CalculatePath(
                agent.frontOfVehiclePosition, location, settings, agent.Flags, agent.generalSeeker, agent.roadSeeker, done);

            Core.Log.Msg("[ClientNavigate] client path request to " + location.ToString("F1") + " started.");
        }
    }
}
