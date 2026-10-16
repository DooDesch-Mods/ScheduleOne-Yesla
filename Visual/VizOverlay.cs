using Il2CppScheduleOne.DevUtilities;
using Il2CppTMPro;

namespace Yesla.Visual
{
    /// <summary>
    /// The text inside the visualisation picture, laid out like Tesla's driving screen: speed and the speed limit sign
    /// top left with the Assisted Driving state under them, the next-turn card top right of them, the destination card
    /// bottom right. It is world text hung one metre in front of the stand-in camera, so it is part of the rendered
    /// texture and shows on the HUD, the full-screen view and the car's centre screen alike.
    /// </summary>
    internal static class VizOverlay
    {
        private const float Depth = 1f;

        // The layout is written for a frame 0.597 x 0.466 half-size at one metre; the root scales it to the camera.
        private const float W = 0.597f, H = 0.466f;

        /// <summary>Below this many metres the next step reads "now" instead of a distance.</summary>
        private const float Now = 10f;

        private static readonly Color Blue = new Color(0.173f, 0.529f, 0.98f);   // #2C87FA, Tesla's dark-mode accent
        private static readonly Color Grey = new Color(0.62f, 0.64f, 0.68f);
        private static readonly Color Card = new Color(0.098f, 0.106f, 0.118f);

        private static TextMeshPro _speed, _unit, _limit, _limitTitle, _state, _nextDistance, _nextWhat, _destName, _destDetail;
        private static GameObject _nextCard, _destCard;
        private static MeshFilter _arrow;
        private static Mesh _left, _right, _pin;
        private static TMP_FontAsset _font;

        internal static void Build(Camera cam)
        {
            _font = FindFont();
            float h = Depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            Transform root = new GameObject("YeslaViz_Overlay") { layer = VizScene.Layer }.transform;
            root.SetParent(cam.transform, false);
            root.localPosition = new Vector3(0f, 0f, Depth);
            root.localScale = Vector3.one * (h / H);

            _speed = Text(root, "Speed", new Vector2(-W + 0.04f, H - 0.03f), new Vector2(0.3f, 0.14f), 1.25f, TextAlignmentOptions.TopLeft, Color.white);
            _unit = Text(root, "Unit", new Vector2(-W + 0.045f, H - 0.17f), new Vector2(0.3f, 0.05f), 0.3f, TextAlignmentOptions.TopLeft, Grey);
            _state = Text(root, "State", new Vector2(-W + 0.045f, H - 0.225f), new Vector2(0.4f, 0.05f), 0.3f, TextAlignmentOptions.TopLeft, Blue);

            // US speed limit sign: white, black border, "SPEED LIMIT" over the number.
            Quad(root, "LimitBorder", new Vector2(-W + 0.31f, H - 0.115f), new Vector2(0.118f, 0.158f), Color.black, 0.012f);
            Quad(root, "LimitSign", new Vector2(-W + 0.31f, H - 0.115f), new Vector2(0.106f, 0.146f), Color.white, 0.01f);
            _limitTitle = Text(root, "LimitTitle", new Vector2(-W + 0.31f - 0.053f, H - 0.048f), new Vector2(0.106f, 0.05f), 0.15f, TextAlignmentOptions.Top, Color.black);
            _limitTitle.lineSpacing = -30f;
            _limit = Text(root, "Limit", new Vector2(-W + 0.31f - 0.053f, H - 0.1f), new Vector2(0.106f, 0.08f), 0.58f, TextAlignmentOptions.Top, Color.black);

            // Next-turn card: arrow, distance in bold, what happens there in grey.
            const float cardLeft = -W + 0.42f, cardTop = H - 0.035f;
            var cardSize = new Vector2(0.5f, 0.16f);
            _nextCard = Quad(root, "NextCard", new Vector2(cardLeft + cardSize.x * 0.5f, cardTop - cardSize.y * 0.5f), cardSize, Card, 0.01f);
            Transform card = _nextCard.transform;
            _left = Arrow(false);
            _right = Arrow(true);
            _pin = Pin();
            var arrowGo = new GameObject("Arrow") { layer = VizScene.Layer };
            arrowGo.transform.SetParent(card, false);
            arrowGo.transform.localPosition = new Vector3(-cardSize.x * 0.5f + 0.075f, 0f, -0.002f);
            _arrow = arrowGo.AddComponent<MeshFilter>();
            var ar = arrowGo.AddComponent<MeshRenderer>();
            ar.sharedMaterial = VizScene.Flat(Color.white);
            ar.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _nextDistance = Text(card, "NextDistance", new Vector2(-cardSize.x * 0.5f + 0.15f, 0.07f), new Vector2(0.34f, 0.08f), 0.56f, TextAlignmentOptions.TopLeft, Color.white, -0.01f);
            _nextWhat = Text(card, "NextWhat", new Vector2(-cardSize.x * 0.5f + 0.152f, -0.005f), new Vector2(0.34f, 0.06f), 0.28f, TextAlignmentOptions.TopLeft, Grey, -0.01f);

            // Destination card, bottom right.
            var destSize = new Vector2(0.44f, 0.13f);
            _destCard = Quad(root, "DestCard", new Vector2(W - 0.03f - destSize.x * 0.5f, -H + 0.03f + destSize.y * 0.5f), destSize, Card, 0.01f);
            Transform dest = _destCard.transform;
            _destName = Text(dest, "DestName", new Vector2(-destSize.x * 0.5f + 0.03f, 0.052f), new Vector2(destSize.x - 0.06f, 0.06f), 0.36f, TextAlignmentOptions.TopLeft, Color.white, -0.01f);
            _destDetail = Text(dest, "DestDetail", new Vector2(-destSize.x * 0.5f + 0.03f, -0.005f), new Vector2(destSize.x - 0.06f, 0.05f), 0.27f, TextAlignmentOptions.TopLeft, Grey, -0.01f);

            Core.Log.Msg("[Viz] overlay text " + (_font != null ? "in " + _font.name : "in TextMeshPro's default font") + ".");
        }

        internal static void Update(VizInfo info)
        {
            if (_speed == null) return;
            bool imperial = Imperial();
            _speed.text = Mathf.RoundToInt(imperial ? info.SpeedKmh * 0.621371f : info.SpeedKmh).ToString();
            _unit.text = imperial ? "MPH" : "KM/H";
            _limit.text = Mathf.RoundToInt(imperial ? info.LimitKmh * 0.621371f : info.LimitKmh).ToString();
            _limitTitle.text = "SPEED\nLIMIT";
            _state.text = info.State ?? string.Empty;
            _state.color = info.Driving ? Blue : Grey;

            Mesh arrow = null;
            string what = null;
            switch (info.Next)
            {
                case NextStep.Left: arrow = _left; what = "Turn left"; break;
                case NextStep.Right: arrow = _right; what = "Turn right"; break;
                case NextStep.Arrive: arrow = _pin; what = string.IsNullOrEmpty(info.Destination) ? "Arrive" : info.Destination; break;
            }
            _nextCard.SetActive(arrow != null);
            if (arrow != null)
            {
                _arrow.sharedMesh = arrow;
                // Distances round to 10 m, so the last few metres would read "0 m".
                _nextDistance.text = info.NextMetres < Now ? "Now" : Distance(info.NextMetres, imperial);
                _nextWhat.text = what;
            }

            bool hasDest = !string.IsNullOrEmpty(info.Destination);
            _destCard.SetActive(hasDest);
            if (hasDest)
            {
                _destName.text = info.Destination;
                _destDetail.text = info.RouteMetres > 1f ? Distance(info.RouteMetres, imperial) + "  ·  " + Minutes(info) + " min" : "Ready";
            }
        }

        /// <summary>Minutes left at three quarters of the speed limit, the road AI's usual pace through town.</summary>
        private static int Minutes(VizInfo info)
        {
            float kmh = Mathf.Max(info.LimitKmh * 0.75f, 15f);
            return Mathf.Max(1, Mathf.CeilToInt(info.RouteMetres / (kmh / 3.6f) / 60f));
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

        private static TextMeshPro Text(Transform root, string name, Vector2 topLeft, Vector2 size, float fontSize, TextAlignmentOptions align, Color colour, float z = 0f)
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
            t.rectTransform.localPosition = new Vector3(topLeft.x, topLeft.y, z);
            t.text = string.Empty;
            return t;
        }

        private static GameObject Quad(Transform root, string name, Vector2 centre, Vector2 size, Color colour, float z)
        {
            var go = new GameObject(name) { layer = VizScene.Layer };
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(centre.x, centre.y, z);
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

        /// <summary>A turn arrow in a 0.1 box: stem up, bar across, head pointing left (or right when mirrored).</summary>
        private static Mesh Arrow(bool right)
        {
            float s = right ? -1f : 1f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            Rect(v, t, 0.011f * s, 0.029f * s, -0.045f, 0.019f);   // stem
            Rect(v, t, -0.02f * s, 0.029f * s, 0.001f, 0.019f);    // bar
            int i = v.Count;                                        // head
            v.Add(new Vector3(-0.048f * s, 0.01f, 0f));
            v.Add(new Vector3(-0.018f * s, 0.036f, 0f));
            v.Add(new Vector3(-0.018f * s, -0.016f, 0f));
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            return Build(right ? "YeslaViz_ArrowRight" : "YeslaViz_ArrowLeft", v, t);
        }

        /// <summary>A map pin in a 0.1 box: a round head over a point.</summary>
        private static Mesh Pin()
        {
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();
            const int seg = 20;
            const float r = 0.024f, cy = 0.012f;
            v.Add(new Vector3(0f, cy, 0f));
            for (int k = 0; k <= seg; k++)
            {
                float a = k * Mathf.PI * 2f / seg;
                v.Add(new Vector3(Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r, 0f));
                if (k > 0) { t.Add(0); t.Add(k + 1); t.Add(k); }
            }
            int i = v.Count;
            v.Add(new Vector3(-0.02f, cy - 0.013f, 0f));
            v.Add(new Vector3(0.02f, cy - 0.013f, 0f));
            v.Add(new Vector3(0f, -0.045f, 0f));
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
            return Build("YeslaViz_Pin", v, t);
        }

        private static void Rect(System.Collections.Generic.List<Vector3> v, System.Collections.Generic.List<int> t, float x0, float x1, float y0, float y1)
        {
            int i = v.Count;
            v.Add(new Vector3(x0, y0, 0f)); v.Add(new Vector3(x1, y0, 0f)); v.Add(new Vector3(x0, y1, 0f)); v.Add(new Vector3(x1, y1, 0f));
            t.Add(i); t.Add(i + 2); t.Add(i + 1);
            t.Add(i + 1); t.Add(i + 2); t.Add(i + 3);
        }

        private static Mesh Build(string name, System.Collections.Generic.List<Vector3> v, System.Collections.Generic.List<int> t)
        {
            var m = new Mesh { name = name };
            m.vertices = v.ToArray();
            m.triangles = t.ToArray();
            m.RecalculateBounds();
            m.hideFlags = HideFlags.DontSave;
            return m;
        }

        /// <summary>The game's own Open Sans, bold where it has it; null falls back to TextMeshPro's default.</summary>
        internal static TMP_FontAsset FindFont()
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
            _speed = _unit = _limit = _limitTitle = _state = _nextDistance = _nextWhat = _destName = _destDetail = null;
            _nextCard = _destCard = null;
            _arrow = null;
            _left = _right = _pin = null;
        }
    }
}
