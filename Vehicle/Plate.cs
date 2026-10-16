using System.Text;
using Il2CppScheduleOne.Vehicles;
using Il2CppTMPro;

namespace Yesla.Vehicle
{
    /// <summary>
    /// The licence plate on the Yesla's tail: text the owner picks in the phone app, "YESLA" and three digits until
    /// they do. The text lives in the owner record (<see cref="Fleet"/>), so it is saved with the car and every peer
    /// shows the same one.
    ///
    /// The model's plate is a flat panel with no lettering. <see cref="Fit"/> puts a TextMeshPro on it once, on the
    /// template every Yesla is cloned from; <see cref="Tick"/> writes each car's text into its copy.
    ///
    /// The template is inactive, so the TextMeshPro's Awake only runs on each clone, and Awake loads TMP's default
    /// settings (size 36, no auto size) over anything set before. The style is applied to the live copy instead,
    /// the first time <see cref="TextOf"/> finds it; size 36 would be letters metres tall around the car.
    /// </summary>
    internal static class Plate
    {
        internal const string ObjectName = "YeslaPlate";
        internal const int MaxChars = 8;

        private const float TickSeconds = 1f;
        private static readonly Color Ink = new Color(0.07f, 0.08f, 0.1f);
        private static readonly Color Panel = new Color(0.93f, 0.93f, 0.91f);

        private static float _nextTick;
        /// <summary>While set, every Yesla plate reads this instead of its own text (the photo bake).</summary>
        internal static string Override;
        private static int _seenRevision = -1;
        private static TMP_FontAsset _font;
        private static Vector2 _rect = new Vector2(0.4f, 0.08f);
        private static readonly Dictionary<IntPtr, TextMeshPro> Texts = new Dictionary<IntPtr, TextMeshPro>();

        /// <summary>
        /// Upper case letters and digits, single spaces between groups, at most <see cref="MaxChars"/> letters and
        /// digits. Anything else is dropped. An empty result means "the factory plate".
        /// </summary>
        internal static string Clean(string raw)
        {
            var sb = new StringBuilder();
            int chars = 0;
            bool gap = false;
            foreach (char ch in (raw ?? string.Empty).ToUpperInvariant())
            {
                if (ch == ' ') { gap = sb.Length > 0; continue; }
                if (!((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))) continue;
                if (chars == MaxChars) break;
                if (gap) { sb.Append(' '); gap = false; }
                sb.Append(ch);
                chars++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The factory plate: "YESLA" and three digits taken from the car's save GUID, so a car keeps its number
        /// without storing it. FNV-1a, because string.GetHashCode changes from one game start to the next.
        /// </summary>
        internal static string Default(string guid)
        {
            uint h = 2166136261;
            foreach (char ch in guid ?? string.Empty) { h ^= ch; h *= 16777619; }
            return "YESLA " + (h % 1000).ToString("000");
        }

        /// <summary>Template: letter the plate panel. <paramref name="plate"/> is the plate part of LOD0.</summary>
        internal static void Fit(Transform parent, MeshRenderer plate, int layer)
        {
            Mesh mesh = plate.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) { Core.Log.Warning("[Plate] the plate part has no mesh - the Yesla drives without lettering."); return; }

            Bounds b = mesh.bounds;
            Vector3 normal = Vector3.zero;
            var normals = mesh.normals;
            if (normals != null) for (int i = 0; i < normals.Length; i++) normal += normals[i];
            if (normal.sqrMagnitude < 1e-6f) normal = b.center.z < 0f ? Vector3.back : Vector3.forward;
            normal.Normalize();
            // A double-sided panel may carry inward normals; the lettering faces away from the car.
            if (Vector3.Dot(normal, new Vector3(0f, 0f, b.center.z)) < 0f) normal = -normal;

            if (plate.sharedMaterial != null) plate.sharedMaterial.SetColor("_BaseColor", Panel);

            var go = new GameObject(ObjectName) { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = b.center + normal * 0.004f;
            // Text reads from the side its transform's forward points away from.
            go.transform.localRotation = Quaternion.LookRotation(-normal, Vector3.up);
            // The panel is tilted and its bounds centre lies above the middle of the face the player sees, by about a
            // tenth of the panel's height; the lettering moves down by that much along the panel.
            go.transform.localPosition += go.transform.localRotation * Vector3.down * (b.size.y * 0.1f);

            var t = go.AddComponent<TextMeshPro>();
            _rect = new Vector2(b.size.x * 0.86f, Mathf.Max(b.size.y, 0.05f) * 0.72f);
            Style(t);
            Core.Log.Msg("[Plate] lettering fitted: plate " + b.size.x.ToString("F2") + " x " + b.size.y.ToString("F2") + " m at " + b.center.ToString("F2")
                       + ", facing " + normal.ToString("F2") + ".");
        }

        /// <summary>Every peer: write each Yesla's plate text, once a second or when a record changed.</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _nextTick && Fleet.Revision == _seenRevision) return;
            _nextTick = now + TickSeconds;
            _seenRevision = Fleet.Revision;
            if (!NetworkSingleton<VehicleManager>.InstanceExists) return;

            var all = NetworkSingleton<VehicleManager>.Instance.AllVehicles;
            for (int i = 0; i < all.Count; i++)
            {
                LandVehicle v = all[i];
                if (v == null || !Phone.YeslaApp.IsYesla(v)) continue;
                TextMeshPro t = TextOf(v);
                if (t == null) continue;
                Fleet.Rec r = Fleet.For(v);
                string text = Override ?? (r != null ? r.PlateText : string.Empty);
                if (t.text == text) continue;
                if (_font == null) _font = Visual.VizOverlay.FindFont();
                if (_font != null && t.font != _font) t.font = _font;
                t.text = text;
                t.ForceMeshUpdate();
                Core.Log.Msg("[Plate] " + v.VehicleName + " #" + Fleet.NetId(v) + " plate: '" + text + "', letters "
                             + t.fontSize.ToString("F2") + " in " + t.rectTransform.sizeDelta.ToString("F2") + " m.");
            }
        }

        private static TextMeshPro TextOf(LandVehicle v)
        {
            if (Texts.TryGetValue(v.Pointer, out TextMeshPro t) && t != null) return t;
            Transform found = null;
            foreach (Transform tr in v.GetComponentsInChildren<Transform>(true))
                if (tr.name == ObjectName) { found = tr; break; }
            if (found == null)
            {
                if (!Texts.ContainsKey(v.Pointer)) Core.Log.Warning("[Plate] " + v.VehicleName + " #" + Fleet.NetId(v) + " has no plate lettering.");
                Texts[v.Pointer] = null;
                return null;
            }
            t = found.GetComponent<TextMeshPro>();
            if (t != null) Style(t);
            Texts[v.Pointer] = t;
            return t;
        }

        private static void Style(TextMeshPro t)
        {
            // Midline centres the glyphs themselves; Center centres the line box, and capitals sit high in it.
            t.alignment = TextAlignmentOptions.Midline;
            t.color = Ink;
            t.fontStyle = FontStyles.Bold;
            t.enableWordWrapping = false;
            t.fontSize = 0.5f;
            t.enableAutoSizing = true;
            t.fontSizeMin = 0.05f;
            t.fontSizeMax = 1.2f;
            t.characterSpacing = 6f;
            t.rectTransform.sizeDelta = _rect;
            t.text = string.Empty;
        }

        /// <summary>Letters the plates on the next tick instead of within the second.</summary>
        internal static void Refresh() => _nextTick = 0f;

        internal static void Reset()
        {
            Texts.Clear();
            _seenRevision = -1;
            _nextTick = 0f;
        }
    }
}
