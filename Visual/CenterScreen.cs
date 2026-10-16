using Il2CppScheduleOne.Vehicles;

namespace Yesla.Visual
{
    /// <summary>
    /// The visualisation on the Yesla's centre screen. A thin quad sits 3 mm in front of the model's LCD, at the
    /// position, size and angle measured from yesla.glb (yesla.layout.json: lcdCenter, lcdNormal, lcdSize), so the
    /// picture does not depend on how the LCD mesh is unwrapped. Only the car the local player sits in shows it;
    /// every other Yesla keeps its dark screen.
    /// </summary>
    internal static class CenterScreen
    {
        private const string Name = "YeslaScreen";
        // Unity space of the model: the glTF X axis is negated on import (GlbModel), which only matters for the sign here.
        private static readonly Vector3 LcdCentre = new Vector3(0.0002f, 0.8113f, 0.5063f);
        private static readonly Vector3 LcdNormal = new Vector3(0f, 0.2264f, -0.974f);
        private const float LcdWidth = 0.338f, LcdHeight = 0.265f;

        private static Material _mat;
        private static Mesh _mesh;
        private static GameObject _shown;

        /// <summary>Shows the picture in <paramref name="car"/> and takes it out of any other car.</summary>
        internal static void Show(LandVehicle car)
        {
            GameObject want = car != null && VizScene.Texture != null ? ScreenOf(car) : null;
            if (_shown != null && (want == null || _shown.Pointer != want.Pointer)) _shown.SetActive(false);
            if (want != null)
            {
                _mat.SetTexture("_BaseMap", VizScene.Texture);
                if (!want.activeSelf) want.SetActive(true);
            }
            _shown = want;
        }

        private static GameObject ScreenOf(LandVehicle car)
        {
            Transform model = VizScene.FindDeep(car.transform, "YeslaModel");
            if (model == null) { Core.Log.Warning("[Viz] the Yesla has no YeslaModel - no centre screen."); return null; }
            Transform existing = model.Find(Name);
            if (existing != null) return existing.gameObject;

            if (_mat == null)
            {
                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                if (unlit == null) { Core.Log.Error("[Viz] URP/Unlit missing - no centre screen."); return null; }
                _mat = new Material(unlit) { name = "Yesla_CenterScreen" };
                _mat.hideFlags = HideFlags.DontSave;
                _mat.SetColor("_BaseColor", new Color(0.9f, 0.9f, 0.9f));
            }
            if (_mesh == null)
            {
                float x = LcdWidth * 0.5f, y = LcdHeight * 0.5f;
                _mesh = new Mesh { name = "Yesla_CenterScreen" };
                _mesh.vertices = new[] { new Vector3(-x, -y, 0f), new Vector3(x, -y, 0f), new Vector3(-x, y, 0f), new Vector3(x, y, 0f) };
                // Seen from the driver the quad's +x is the car's right, so the picture is not mirrored.
                _mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
                _mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
                _mesh.RecalculateBounds();
                _mesh.hideFlags = HideFlags.DontSave;
            }

            var go = new GameObject(Name) { layer = model.gameObject.layer };
            go.transform.SetParent(model, false);
            Vector3 n = LcdNormal.normalized;
            go.transform.localPosition = LcdCentre + n * 0.003f;
            // The quad's +z points away from the driver, into the dashboard.
            go.transform.localRotation = Quaternion.LookRotation(-n, Vector3.up);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Core.Log.Msg("[Viz] centre screen fitted to " + car.name + " at " + go.transform.position.ToString("F2") + ".");
            return go;
        }

        internal static void Reset()
        {
            _shown = null;
        }
    }
}
