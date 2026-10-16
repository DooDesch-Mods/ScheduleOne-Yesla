using Il2CppScheduleOne.Vehicles;

namespace Yesla.Visual
{
    /// <summary>
    /// A soft light under the roof, aimed at the dashboard, while the local player looks out of a Yesla in first
    /// person. The cabin has no light of its own and the game's sun barely reaches it, so without this the interior is
    /// close to black. It exists only on this machine and only in the car the player sits in.
    /// </summary>
    internal static class CabinLight
    {
        private static float Range = 2.4f;
        private static float Intensity = 7f;
        // Model space: under the roof between the front seats, aimed down at the dashboard and the wheel. A spot keeps
        // the light grey headliner out of the beam; a point light there turned it white before the dashboard lit up.
        private static Vector3 Position = new Vector3(0f, 1.12f, -0.05f);
        private static readonly Vector3 Aim = new Vector3(0f, 0.72f, 0.5f);
        private const float Angle = 120f;

        private static Light _light;
        private static LandVehicle _car;

        internal static void Show(LandVehicle car, bool on)
        {
            if (!on) car = null;
            if (_car != null && (car == null || _car.Pointer != car.Pointer))
            {
                if (_light != null) _light.enabled = false;
                _car = null;
            }
            if (car == null) return;
            if (_car != null && _light != null) { _light.enabled = true; return; }

            Transform model = VizScene.FindDeep(car.transform, "YeslaModel");
            if (model == null) { Core.Log.Warning("[Viz] the Yesla has no YeslaModel - no cabin light."); return; }
            Transform t = model.Find("YeslaCabinLight");
            if (t == null)
            {
                var go = new GameObject("YeslaCabinLight");
                go.transform.SetParent(model, false);
                go.transform.localPosition = Position;
                _light = go.AddComponent<Light>();
                _light.type = LightType.Spot;
                _light.spotAngle = Angle;
                go.transform.localRotation = Quaternion.LookRotation(Aim - Position, Vector3.up);
                _light.range = Range;
                _light.intensity = Intensity;
                _light.color = new Color(1f, 0.95f, 0.88f);
                _light.shadows = LightShadows.None;
                Core.Log.Msg("[Viz] cabin light fitted to " + car.name + ".");
            }
            else _light = t.GetComponent<Light>();
            _light.enabled = true;
            _car = car;
        }

        /// <summary>Changes the light live, for tuning from the dev console.</summary>
        internal static string Tune(float intensity, float range, Vector3? position)
        {
            Intensity = intensity;
            Range = range;
            if (position.HasValue) Position = position.Value;
            if (_light == null) return "cabin light set to " + Intensity + " / " + Range + " m; it applies once first person is on";
            _light.intensity = Intensity;
            _light.range = Range;
            _light.transform.localPosition = Position;
            _light.transform.localRotation = Quaternion.LookRotation(Aim - Position, Vector3.up);
            return "cabin light " + Intensity + " / " + Range + " m at " + Position.ToString("F2");
        }

        internal static void Reset()
        {
            _light = null;
            _car = null;
        }
    }
}
