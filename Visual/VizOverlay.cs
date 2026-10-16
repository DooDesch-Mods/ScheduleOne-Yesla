using Il2CppScheduleOne.DevUtilities;
using Il2CppTMPro;

namespace Yesla.Visual
{
    /// <summary>
    /// The text inside the visualisation picture: speed, speed limit, Assisted Driving state and the next turn. It is
    /// world text hung one metre in front of the stand-in camera, so it is part of the rendered texture and shows on
    /// the HUD, the full-screen view and the car's centre screen alike.
    /// </summary>
    internal static class VizOverlay
    {
        private const float Depth = 1f;

        private static TextMeshPro _speed, _unit, _limit, _limitTitle, _state, _dest, _next;
        private static GameObject _limitSign, _nextBar;
        private static TMP_FontAsset _font;

        internal static void Build(Camera cam)
        {
            _font = FindFont();
            float h = Depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float w = h * VizScene.Width / VizScene.Height;
            Transform root = new GameObject("YeslaViz_Overlay") { layer = VizScene.Layer }.transform;
            root.SetParent(cam.transform, false);
            root.localPosition = new Vector3(0f, 0f, Depth);

            _speed = Text(root, "Speed", new Vector2(-w + 0.04f, h - 0.03f), new Vector2(0.3f, 0.14f), 1.25f, TextAlignmentOptions.TopLeft, Color.white);
            _unit = Text(root, "Unit", new Vector2(-w + 0.045f, h - 0.17f), new Vector2(0.3f, 0.05f), 0.32f, TextAlignmentOptions.TopLeft, new Color(0.7f, 0.72f, 0.76f));

            _limitSign = Quad(root, "LimitSign", new Vector2(-w + 0.31f, h - 0.11f), new Vector2(0.105f, 0.13f), Color.white);
            _limitTitle = Text(root, "LimitTitle", new Vector2(-w + 0.31f - 0.0525f, h - 0.052f), new Vector2(0.105f, 0.04f), 0.2f, TextAlignmentOptions.Top, Color.black);
            _limit = Text(root, "Limit", new Vector2(-w + 0.31f - 0.0525f, h - 0.085f), new Vector2(0.105f, 0.08f), 0.62f, TextAlignmentOptions.Top, Color.black);

            _state = Text(root, "State", new Vector2(w - 0.64f, h - 0.035f), new Vector2(0.6f, 0.07f), 0.5f, TextAlignmentOptions.TopRight, new Color(0.35f, 0.6f, 1f));
            _dest = Text(root, "Destination", new Vector2(w - 0.64f, h - 0.1f), new Vector2(0.6f, 0.06f), 0.34f, TextAlignmentOptions.TopRight, new Color(0.78f, 0.8f, 0.84f));

            _nextBar = Quad(root, "NextBar", new Vector2(0f, -h + 0.065f), new Vector2(w * 1.2f, 0.1f), new Color(0.11f, 0.12f, 0.14f));
            _next = Text(root, "Next", new Vector2(-w * 0.6f, -h + 0.105f), new Vector2(w * 1.2f, 0.08f), 0.48f, TextAlignmentOptions.Top, Color.white);
            Core.Log.Msg("[Viz] overlay text " + (_font != null ? "in " + _font.name : "in TextMeshPro's default font") + ".");
        }

        internal static void Update(VizInfo info)
        {
            if (_speed == null) return;
            bool imperial = Imperial();
            _speed.text = Mathf.RoundToInt(imperial ? info.SpeedKmh * 0.621371f : info.SpeedKmh).ToString();
            _unit.text = imperial ? "MPH" : "KM/H";
            _limit.text = Mathf.RoundToInt(imperial ? info.LimitKmh * 0.621371f : info.LimitKmh).ToString();
            _limitTitle.text = "LIMIT";
            _state.text = info.State ?? string.Empty;
            _state.color = info.Driving ? new Color(0.35f, 0.6f, 1f) : new Color(0.78f, 0.8f, 0.84f);
            _dest.text = info.Destination ?? string.Empty;

            string next = null;
            switch (info.Next)
            {
                case NextStep.Left: next = "Turn left in " + Distance(info.NextMetres, imperial); break;
                case NextStep.Right: next = "Turn right in " + Distance(info.NextMetres, imperial); break;
                case NextStep.Arrive: next = "Arriving in " + Distance(info.NextMetres, imperial); break;
            }
            _next.text = next ?? string.Empty;
            _nextBar.SetActive(next != null);
        }

        internal static string Distance(float metres, bool imperial)
        {
            if (imperial)
            {
                float feet = metres * 3.28084f;
                return feet < 1000f ? (Mathf.Round(feet / 10f) * 10f).ToString("F0") + " ft" : (metres / 1609.34f).ToString("F1") + " mi";
            }
            return metres < 1000f ? (Mathf.Round(metres / 10f) * 10f).ToString("F0") + " m" : (metres / 1000f).ToString("F1") + " km";
        }

        private static bool Imperial() =>
            Singleton<Settings>.InstanceExists && Singleton<Settings>.Instance.UnitType == Settings.EUnitType.Imperial;

        private static TextMeshPro Text(Transform root, string name, Vector2 topLeft, Vector2 size, float fontSize, TextAlignmentOptions align, Color colour)
        {
            var go = new GameObject(name) { layer = VizScene.Layer };
            go.transform.SetParent(root, false);
            var t = go.AddComponent<TextMeshPro>();
            if (_font != null) t.font = _font;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = colour;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.rectTransform.pivot = new Vector2(0f, 1f);
            t.rectTransform.sizeDelta = size;
            t.rectTransform.localPosition = new Vector3(topLeft.x, topLeft.y, 0f);
            t.text = string.Empty;
            return t;
        }

        private static GameObject Quad(Transform root, string name, Vector2 centre, Vector2 size, Color colour)
        {
            var go = new GameObject(name) { layer = VizScene.Layer };
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(centre.x, centre.y, 0.01f);
            var m = new Mesh { name = "YeslaViz_" + name };
            float x = size.x * 0.5f, y = size.y * 0.5f;
            m.vertices = new[] { new Vector3(-x, -y, 0f), new Vector3(x, -y, 0f), new Vector3(-x, y, 0f), new Vector3(x, y, 0f) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = VizScene.Flat(colour);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>The game's own Open Sans, bold where it has it; null falls back to TextMeshPro's default.</summary>
        private static TMP_FontAsset FindFont()
        {
            TMP_FontAsset best = null;
            int bestScore = -1;
            foreach (TMP_FontAsset f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f == null) continue;
                string n = f.name.ToLowerInvariant();
                int score = n.Contains("opensans") || n.Contains("open sans") ? 2 : 0;
                if (n.Contains("semibold") || n.Contains("bold")) score += 1;
                if (n.Contains("italic")) score -= 2;
                if (score > bestScore) { best = f; bestScore = score; }
            }
            return best;
        }

        internal static void Reset()
        {
            _speed = _unit = _limit = _limitTitle = _state = _dest = _next = null;
            _limitSign = _nextBar = null;
        }
    }
}
