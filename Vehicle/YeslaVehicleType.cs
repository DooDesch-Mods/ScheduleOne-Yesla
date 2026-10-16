using System;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.Modification;
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

        /// <summary>
        /// Motor curve in km/h -> curve value, the electric drive: full pull from standstill and nearly flat to
        /// 80 km/h, then falling to nothing at 135. The Hounddog's curve starts at 360 and drops to 230 by 50 km/h.
        /// LandVehicle drives each drive wheel with value * diffGearing / 2; the car's drag sets the top speed where
        /// the falling curve meets it, a little under 100 km/h.
        /// </summary>
        private static readonly (float kmh, float value)[] MotorCurve =
        {
            (0f, 560f), (50f, 540f), (80f, 470f), (100f, 380f), (120f, 250f), (135f, 0f),
        };

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
            ElectricDrive(lv);

            try
            {
                if (!YeslaBody.Apply(clone)) Core.Log.Error("[VehicleType] the Yesla body could not be built - the car keeps the donor look.");
            }
            catch (Exception e) { Core.Log.Error("[VehicleType] the Yesla body failed, the car keeps the donor look: " + e); }

            // A new car takes its colour from here (LandVehicle.Awake: OwnedColor = Color.DefaultColor); a saved car
            // keeps the colour its save names.
            if (lv.Color != null) lv.Color.DefaultColor = EVehicleColor.White;
            else Core.Log.Error("[VehicleType] the clone has no VehicleColor - a new Yesla keeps the donor's colour.");

            try { YeslaSound.Apply(clone); }
            catch (Exception e) { Core.Log.Error("[VehicleType] the electric drive sound failed, the car keeps the donor engine: " + e); }

            _prefab = clone;
            Core.Log.Msg("[VehicleType] built the '" + Code + "' prefab from '" + DonorCode + "'.");
            return true;
        }

        private static void ElectricDrive(LandVehicle lv)
        {
            var curve = new AnimationCurve();
            foreach (var (kmh, value) in MotorCurve) curve.AddKey(kmh, value);
            lv.motorTorque = curve;
            Core.Log.Msg("[VehicleType] electric drive: motor curve " + string.Join(", ", Array.ConvertAll(MotorCurve, k => k.kmh + ":" + k.value))
                       + ", gearing " + lv.diffGearing + ".");
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
