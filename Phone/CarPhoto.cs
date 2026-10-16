using System.Collections.Generic;
using Il2CppScheduleOne.Vehicles;
using Sideload.Api;

namespace Yesla.Phone
{
    /// <summary>
    /// A studio photo of the player's own Yesla, taken off the car in the world, for the phone app: its real paint,
    /// plate, lamps and wheels. Published as <c>s1://yesla-car-photo-&lt;n&gt;</c>; CarPicture's baked renders stay
    /// the fallback for a car that cannot be photographed.
    ///
    /// The car's renderers move to the visualisation's layer for the one render and back right after, so a camera
    /// that sees only that layer sees only the car, wherever it stands. The lighting is the visualisation's trade:
    /// sun, ambient, fog and the game's renderer features are swapped for the call and restored. The camera is
    /// rendered twice, over black and over white; the difference between the two is the car's coverage, which gives
    /// a clean cut-out including the tinted glass.
    /// </summary>
    internal static class CarPhoto
    {
        private const int Layer = Visual.VizScene.Layer;
        // Twice the 236 x 122 css box, so the phone's own scaling has detail to work with.
        private const int Width = 472, Height = 244;
        private const float FieldOfView = 22f;
        // Seen from the front left and a little above, like a brochure shot.
        private static readonly Vector3 ViewDirection = new Vector3(-0.78f, 0.30f, 0.95f);
        private const float Fill = 0.86f;                 // share of the picture width the car takes
        private const float ShadowAlpha = 0.55f;

        private static Camera _cam;
        private static Light _key, _rim;
        private static RenderTexture _msaa, _flat;
        private static string _shownKey = "";
        private static string _current;
        private static int _serial;
        private static bool _failed;

        /// <summary>The image name of the newest photo, or null when there is none.</summary>
        internal static string Current => _current;

        /// <summary>
        /// Takes a new photo when the car, its paint or its plate changed, or when <paramref name="force"/> asks for one
        /// (the app has just been opened). Returns true when a new picture was published.
        /// </summary>
        internal static bool Update(AppHandle app, LandVehicle car, string plate, bool force)
        {
            if (app == null || _failed) return false;
            if (car == null || !car.gameObject.activeInHierarchy) return false;
            string key = car.Pointer.ToInt64() + "|" + car.OwnedColor + "|" + plate;
            if (!force && key == _shownKey) return false;

            byte[] png = Take(car);
            if (png == null) return false;

            string old = _current;
            _current = "yesla-car-photo-" + (++_serial);
            app.Image(_current, png);
            if (old != null) app.Image(old, null);
            _shownKey = key;
            Core.Log.Msg("[CarPhoto] " + car.VehicleName + " photographed (" + car.OwnedColor + ", plate '" + plate + "', " + (png.Length / 1024) + " KB) as " + _current + ".");
            return true;
        }

        internal static void Reset()
        {
            _shownKey = "";
            _current = null;
        }

        private static bool Build()
        {
            if (_cam != null) return true;
            try
            {
                var root = new GameObject("YeslaCarPhoto");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.hideFlags = HideFlags.HideAndDontSave;

                var camGo = new GameObject("Camera") { layer = Layer };
                camGo.transform.SetParent(root.transform, false);
                _cam = camGo.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.cullingMask = 1 << Layer;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.fieldOfView = FieldOfView;
                _cam.nearClipPlane = 0.3f;
                _cam.farClipPlane = 60f;
                _cam.allowHDR = false;

                _key = StudioLight(root.transform, "Key", 1.25f, new Color(1f, 0.97f, 0.93f), LightShadows.Soft);
                _rim = StudioLight(root.transform, "Rim", 0.55f, new Color(0.85f, 0.9f, 1f), LightShadows.None);

                _msaa = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "YeslaCarPhoto", antiAliasing = 8, hideFlags = HideFlags.DontSave };
                _msaa.Create();
                _flat = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32) { name = "YeslaCarPhotoResolved", hideFlags = HideFlags.DontSave };
                _flat.Create();
                _cam.targetTexture = _msaa;
                return true;
            }
            catch (System.Exception e)
            {
                Core.Log.Error("[CarPhoto] building the studio failed - the app keeps the drawn car picture: " + e.Message);
                _failed = true;
                return false;
            }
        }

        private static Light StudioLight(Transform parent, string name, float intensity, Color colour, LightShadows shadows)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(parent, false);
            Light l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.color = colour;
            l.shadows = shadows;
            l.cullingMask = 1 << Layer;
            l.enabled = false;
            return l;
        }

        private static byte[] Take(LandVehicle car)
        {
            if (!Build()) return null;

            var moved = new List<KeyValuePair<GameObject, int>>();
            Light sun = RenderSettings.sun;
            bool sunOn = sun != null && sun.enabled;
            bool fog = RenderSettings.fog;
            var ambientMode = RenderSettings.ambientMode;
            Color sky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor, ground = RenderSettings.ambientGroundColor;
            RenderTexture active = RenderTexture.active;
            Texture2D black = null, white = null;
            try
            {
                if (car.boundingBox == null) { Core.Log.Warning("[CarPhoto] " + car.VehicleName + " has no bounding box to aim at - the app keeps the drawn car picture."); return null; }
                // The collider's own bounds, enabled or not: the box the road AI measures the car with.
                Transform boxT = car.boundingBox.transform;
                Bounds bounds = new Bounds(boxT.TransformPoint(car.boundingBox.center), Vector3.zero);
                Vector3 h = car.boundingBox.size * 0.5f;
                for (int corner = 0; corner < 8; corner++)
                    bounds.Encapsulate(boxT.TransformPoint(car.boundingBox.center + new Vector3((corner & 1) == 0 ? -h.x : h.x, (corner & 2) == 0 ? -h.y : h.y, (corner & 4) == 0 ? -h.z : h.z)));
                float carSize = bounds.extents.magnitude;

                int skipped = 0;
                foreach (Renderer r in car.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                    // Headlight beams and other light volumes reach far past the car and are not part of its look.
                    if (r.bounds.extents.magnitude > carSize * 1.3f || (r.bounds.center - bounds.center).magnitude > carSize) { skipped++; continue; }
                    moved.Add(new KeyValuePair<GameObject, int>(r.gameObject, r.gameObject.layer));
                    r.gameObject.layer = Layer;
                }
                if (moved.Count == 0) { Core.Log.Warning("[CarPhoto] " + car.VehicleName + " has no visible renderer - the app keeps the drawn car picture."); return null; }
                if (skipped > 0) Core.Log.Msg("[CarPhoto] left out " + skipped + " renderer(s) larger than the car, such as headlight beams.");

                Aim(car.transform, bounds);

                Visual.VizScene.FeaturesOff();
                if (sunOn) sun.enabled = false;
                RenderSettings.sun = _key;
                _key.enabled = true;
                _rim.enabled = true;
                RenderSettings.fog = false;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.62f, 0.65f, 0.72f);
                RenderSettings.ambientEquatorColor = new Color(0.42f, 0.43f, 0.46f);
                RenderSettings.ambientGroundColor = new Color(0.16f, 0.16f, 0.17f);

                black = Shoot(Color.black);
                white = Shoot(Color.white);
            }
            catch (System.Exception e)
            {
                Core.Log.Error("[CarPhoto] photographing " + car.VehicleName + " failed - the app keeps the drawn car picture: " + e.Message);
                return null;
            }
            finally
            {
                foreach (var m in moved) if (m.Key != null) m.Key.layer = m.Value;
                if (_key != null) _key.enabled = false;
                if (_rim != null) _rim.enabled = false;
                RenderSettings.sun = sun;
                if (sunOn) sun.enabled = true;
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientSkyColor = sky;
                RenderSettings.ambientEquatorColor = equator;
                RenderSettings.ambientGroundColor = ground;
                RenderTexture.active = active;
                Visual.VizScene.FeaturesBack();
            }

            try
            {
#if DEBUG
                string dir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "YeslaPhoto");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "black.png"), ImageConversion.EncodeToPNG(black));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "white.png"), ImageConversion.EncodeToPNG(white));
#endif
                return Compose(black, white, car.transform);
            }
            catch (System.Exception e)
            {
                Core.Log.Error("[CarPhoto] cutting out the photo failed - the app keeps the drawn car picture: " + e.Message);
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(black);
                UnityEngine.Object.Destroy(white);
            }
        }

        /// <summary>Points the camera at the car from the front left and pulls back until the car fills the frame.</summary>
        private static void Aim(Transform car, Bounds bounds)
        {
            Vector3 centre = bounds.center;
            Vector3 dir = car.TransformDirection(ViewDirection.normalized);
            float radius = bounds.extents.magnitude;
            // Distance at which a sphere around the car spans Fill of the picture width.
            float halfH = FieldOfView * 0.5f * Mathf.Deg2Rad;
            float halfW = Mathf.Atan(Mathf.Tan(halfH) * Width / Height);
            float distance = radius / Mathf.Sin(halfW * Fill);
            _cam.transform.position = centre + dir * distance;
            _cam.transform.rotation = Quaternion.LookRotation(centre - _cam.transform.position, Vector3.up);

            // Key light high from the camera's left, rim light low from behind the car on the right.
            _key.transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(-35f, Vector3.up) * Flat(-dir) + Vector3.down * 1.3f, Vector3.up);
            _rim.transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(150f, Vector3.up) * Flat(-dir) + Vector3.down * 0.35f, Vector3.up);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward;
        }

        private static Texture2D Shoot(Color background)
        {
            // A camera added at runtime has no URP camera data, so URP does not clear for it.
            RenderTexture.active = _msaa;
            GL.Clear(true, true, background);
            _cam.backgroundColor = background;
            _cam.Render();
            Graphics.Blit(_msaa, _flat);
            RenderTexture.active = _flat;
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// Coverage from the two shots, in linear light: over black a pixel is <c>a * c</c>, over white
        /// <c>a * c + (1 - a)</c>. A soft ground shadow under the car's footprint goes in behind it.
        /// </summary>
        private static byte[] Compose(Texture2D black, Texture2D white, Transform car)
        {
            Tables();
            var b = black.GetPixels32();
            var w = white.GetPixels32();
            float[] shadow = Shadow(car);
            var outTex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            var px = new Color32[Width * Height];
            for (int i = 0; i < px.Length; i++)
            {
                Color32 cb = b[i], cw = w[i];
                float br = _toLinear[cb.r], bg = _toLinear[cb.g], bb = _toLinear[cb.b];
                float a = 1f - ((_toLinear[cw.r] - br) + (_toLinear[cw.g] - bg) + (_toLinear[cw.b] - bb)) / 3f;
                a = Mathf.Clamp01(a);
                float s = shadow[i] * (1f - a);
                float alpha = a + s;
                if (alpha <= 0.002f) { px[i] = new Color32(0, 0, 0, 0); continue; }
                // Premultiplied car over a black shadow, then back to straight alpha for the PNG.
                float inv = 1f / alpha;
                px[i] = new Color32(Srgb(br * inv), Srgb(bg * inv), Srgb(bb * inv), (byte)Mathf.RoundToInt(alpha * 255f));
            }
            outTex.SetPixels32(px);
            outTex.Apply(false, false);
            byte[] png = ImageConversion.EncodeToPNG(outTex);
            UnityEngine.Object.Destroy(outTex);
            return png;
        }

        /// <summary>
        /// Shadow strength per pixel: the car's footprint on the ground, projected into the picture, with a soft edge.
        /// </summary>
        private static float[] Shadow(Transform car)
        {
            var result = new float[Width * Height];
            LandVehicle lv = car.GetComponent<LandVehicle>();
            if (lv == null || lv.boundingBox == null) { Core.Log.Warning("[CarPhoto] no bounding box on the car - the photo has no ground shadow."); return result; }

            Transform box = lv.boundingBox.transform;
            Vector3 size = lv.boundingBox.size;
            Vector3 c = lv.boundingBox.center;
            float groundY = float.MaxValue;
            if (lv.wheels != null)
                foreach (Wheel wh in lv.wheels)
                    if (wh != null && wh.wheelCollider != null) groundY = Mathf.Min(groundY, wh.wheelCollider.transform.position.y - wh.wheelCollider.radius);
            if (groundY == float.MaxValue) groundY = box.TransformPoint(c - new Vector3(0f, size.y * 0.5f, 0f)).y;

            var corners = new Vector2[4];
            float[] sx = { -0.5f, 0.5f, 0.5f, -0.5f }, sz = { -0.5f, -0.5f, 0.5f, 0.5f };
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = box.TransformPoint(c + new Vector3(size.x * sx[k] * 0.92f, 0f, size.z * sz[k] * 0.96f));
                p.y = groundY;
                Vector3 sp = _cam.WorldToScreenPoint(p);
                // Screen y runs up, the picture's rows run down after ReadPixels' bottom-up order - both bottom-up here.
                corners[k] = new Vector2(sp.x * Width / _cam.pixelWidth, sp.y * Height / _cam.pixelHeight);
            }

            float soft = Width * 0.045f;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    float d = SignedDistance(new Vector2(x + 0.5f, y + 0.5f), corners);
                    float t = Mathf.Clamp01(0.5f - d / (2f * soft));
                    result[y * Width + x] = ShadowAlpha * t * t * (3f - 2f * t);
                }
            return result;
        }

        /// <summary>Distance to a convex quad's edge, negative inside, either winding.</summary>
        private static float SignedDistance(Vector2 p, Vector2[] q)
        {
            float best = float.MaxValue;
            bool inside = true;
            float sign = 0f;
            for (int i = 0; i < q.Length; i++)
            {
                Vector2 a = q[i], b = q[(i + 1) % q.Length];
                Vector2 ab = b - a, ap = p - a;
                float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, (ap - ab * t).magnitude);
                float cross = ab.x * ap.y - ab.y * ap.x;
                if (sign == 0f) sign = Mathf.Sign(cross);
                else if (Mathf.Sign(cross) != sign) inside = false;
            }
            return inside ? -best : best;
        }

        private static float[] _toLinear;

        private static void Tables()
        {
            if (_toLinear != null) return;
            _toLinear = new float[256];
            for (int i = 0; i < 256; i++) _toLinear[i] = Mathf.GammaToLinearSpace(i / 255f);
        }

        private static byte Srgb(float linear) => (byte)Mathf.RoundToInt(Mathf.Clamp01(Mathf.LinearToGammaSpace(Mathf.Clamp01(linear))) * 255f);
    }
}
