using Il2CppScheduleOne.Vehicles;

namespace Yesla.Visual
{
    /// <summary>
    /// The detailed cabin. Every Yesla draws Cabin_LOD0 with its body; the car the local player sits in swaps it for
    /// Cabin_HI, so the steering wheel and dashboard in front of the camera keep their shape while every other car
    /// stays cheap.
    /// </summary>
    internal static class Cabin
    {
        internal const string LowName = "Cabin_LOD0";
        internal const string HighName = "Cabin_HI";

        private static Transform _shownIn;
        private static System.IntPtr _car;

        /// <summary>Shows the detailed cabin in <paramref name="car"/> and puts the low one back everywhere else.</summary>
        internal static void Show(LandVehicle car)
        {
            System.IntPtr key = car != null ? car.Pointer : System.IntPtr.Zero;
            if (key == _car) return;
            _car = key;
            Transform want = car != null ? VizScene.FindDeep(car.transform, "YeslaModel") : null;
            if (_shownIn != null && (want == null || _shownIn.Pointer != want.Pointer)) Swap(_shownIn, false);
            if (want != null && (_shownIn == null || _shownIn.Pointer != want.Pointer)) Swap(want, true);
            _shownIn = want;
        }

        private static void Swap(Transform model, bool detailed)
        {
            Transform low = model.Find(LowName), high = model.Find(HighName);
            if (low == null || high == null)
            {
                Core.Log.Warning("[Cabin] " + model.root.name + " has no " + (low == null ? LowName : HighName) + " - cabin left as it is.");
                return;
            }
            foreach (MeshRenderer r in low.GetComponentsInChildren<MeshRenderer>(true)) r.enabled = !detailed;
            high.gameObject.SetActive(detailed);
            Core.Log.Msg("[Cabin] " + model.root.name + ": " + (detailed ? "detailed cabin on" : "low cabin back") + ".");
        }

        internal static void Reset()
        {
            _shownIn = null;
            _car = System.IntPtr.Zero;
        }
    }
}
