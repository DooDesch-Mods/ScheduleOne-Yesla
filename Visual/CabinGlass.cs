using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Visual
{
    /// <summary>
    /// Clearer windows while the local player looks out of a Yesla in first person. From outside the glass is a dark
    /// tint at 60 % cover, which reads as glass; from the driver's seat that tint darkens the whole road. Only the car
    /// the player sits in changes, through a property block, so the shared material and every other Yesla keep the
    /// outside look.
    /// </summary>
    internal static class CabinGlass
    {
        private const float InsideAlpha = 0.18f;

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly List<Renderer> Clear = new List<Renderer>();
        private static System.IntPtr _car;

        internal static void Show(LandVehicle car, bool firstPerson)
        {
            if (!firstPerson) car = null;
            System.IntPtr key = car != null ? car.Pointer : System.IntPtr.Zero;
            if (key == _car) return;
            Restore();
            _car = key;
            if (car == null) return;

            Transform model = VizScene.FindDeep(car.transform, "YeslaModel");
            if (model == null) { Core.Log.Warning("[Viz] the Yesla has no YeslaModel - the windows keep their tint."); return; }
            var block = new MaterialPropertyBlock();
            foreach (MeshRenderer r in model.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null || r.sharedMaterial == null || !Vehicle.GlbModel.InGroup(r.gameObject.name, "Glass")) continue;
                Color c = r.sharedMaterial.GetColor(BaseColor);
                c.a = InsideAlpha;
                block.Clear();
                block.SetColor(BaseColor, c);
                r.SetPropertyBlock(block);
                Clear.Add(r);
            }
            if (Clear.Count == 0) Core.Log.Warning("[Viz] no window renderers in " + car.name + " - the windows keep their tint.");
            else Core.Log.Msg("[Viz] " + Clear.Count + " window(s) in " + car.name + " clearer for the first-person view.");
        }

        private static void Restore()
        {
            foreach (Renderer r in Clear) if (r != null) r.SetPropertyBlock(null);
            if (Clear.Count > 0) Core.Log.Msg("[Viz] windows back to their outside tint.");
            Clear.Clear();
        }

        internal static void Reset()
        {
            Clear.Clear();
            _car = System.IntPtr.Zero;
        }
    }
}
