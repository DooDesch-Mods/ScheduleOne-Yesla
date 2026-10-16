using System;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppScheduleOne.Vehicles;
using FishNetObject = Il2CppFishNet.Object.NetworkObject;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The Yesla Model 3 as its own vehicle code: a clone of the Hounddog prefab with its own code, name, price and
    /// top speed, registered with FishNet and the VehicleManager on every peer.
    ///
    /// The save stores only the code (LandVehicle.GetVehicleData), so the clone must be in
    /// <c>VehicleManager.VehiclePrefabs</c> before the save's vehicles load. <c>GetVehiclePrefab</c> scans that list on
    /// every call, so adding it in VehicleManager.Awake is early enough. FishNet gives the clone its own PrefabId when
    /// it is added to SpawnablePrefabs; without that a client would build the donor (a Hounddog) for every spawn.
    /// </summary>
    internal static class YeslaVehicleType
    {
        internal const string Code = "yesla_model3";
        internal const string Name = "Yesla Model 3";
        internal const float Price = 60000f;
        internal const float TopSpeed = 65f;
        private const string DonorCode = "hounddog";

        private static GameObject _prefab;

        internal static LandVehicle Prefab => _prefab != null ? _prefab.GetComponent<LandVehicle>() : null;

        /// <summary>Builds the prefab once per process and adds it to this scene's VehicleManager.</summary>
        internal static void Register(VehicleManager manager)
        {
            if (manager == null) { Core.Log.Error("[VehicleType] no VehicleManager - the Yesla Model 3 is not registered."); return; }

            if (_prefab == null && !Build(manager)) return;
            LandVehicle lv = _prefab.GetComponent<LandVehicle>();

            if (!RegisterWithFishNet(_prefab.GetComponent<FishNetObject>())) return;

            var list = manager.VehiclePrefabs;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Pointer == lv.Pointer)
                {
                    Core.Log.Msg("[VehicleType] '" + Code + "' already in this VehicleManager.");
                    return;
                }
            }
            list.Add(lv);
            var nob = _prefab.GetComponent<FishNetObject>();
            Core.Log.Msg("[VehicleType] registered '" + Code + "' (" + Name + ", $" + Price.ToString("N0") + ", top speed " + TopSpeed
                       + ") as vehicle prefab #" + (list.Count - 1) + ", FishNet prefab id " + nob.PrefabId + " in collection " + nob.SpawnableCollectionId + ".");
        }

        private static bool Build(VehicleManager manager)
        {
            LandVehicle donor = manager.GetVehiclePrefab(DonorCode);
            if (donor == null) { Core.Log.Error("[VehicleType] donor '" + DonorCode + "' not found - the Yesla Model 3 is not registered."); return false; }

            GameObject staging = new GameObject("Yesla_InactiveStaging");
            staging.SetActive(false);
            GameObject clone;
            try
            {
                // Instantiated under an inactive parent so no Awake runs on the template.
                clone = UnityEngine.Object.Instantiate(donor.gameObject, staging.transform, false);
                clone.SetActive(false);
                clone.transform.SetParent(null, false);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(staging);
            }

            clone.name = "YeslaModel3";
            clone.transform.position = new Vector3(0f, -100f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(clone);

            LandVehicle lv = clone.GetComponent<LandVehicle>();
            lv.vehicleCode = Code;
            lv.vehicleName = Name;
            lv.vehiclePrice = Price;
            lv.TopSpeed = TopSpeed;

            try
            {
                if (!YeslaBody.Apply(clone)) Core.Log.Error("[VehicleType] the Yesla body could not be built - the car keeps the donor look.");
            }
            catch (Exception e) { Core.Log.Error("[VehicleType] the Yesla body failed, the car keeps the donor look: " + e); }

            _prefab = clone;
            Core.Log.Msg("[VehicleType] built the '" + Code + "' prefab from '" + DonorCode + "'.");
            return true;
        }

        private static bool RegisterWithFishNet(FishNetObject nob)
        {
            if (nob == null) { Core.Log.Error("[VehicleType] the clone has no NetworkObject - cannot spawn it in co-op."); return false; }
            var spawnables = InstanceFinder.NetworkManager?.SpawnablePrefabs;
            if (spawnables == null) { Core.Log.Error("[VehicleType] FishNet's SpawnablePrefabs is not available yet - the Yesla Model 3 is not registered."); return false; }

            int count = spawnables.GetObjectCount();
            for (int i = 0; i < count; i++)
            {
                var candidate = spawnables.GetObject(true, i);
                if (candidate != null && candidate.Pointer == nob.Pointer) return true;
            }
            spawnables.AddObject(nob);
            Core.Log.Msg("[VehicleType] added to FishNet's spawnable prefabs at index " + count + ".");
            return true;
        }

        [HarmonyPatch(typeof(VehicleManager), nameof(VehicleManager.Awake))]
        private static class RegisterOnAwake
        {
            private static void Postfix(VehicleManager __instance)
            {
                try { Register(__instance); }
                catch (Exception e) { Core.Log.Error("[VehicleType] registration failed: " + e); }
            }
        }
    }
}
