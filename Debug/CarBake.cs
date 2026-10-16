#if DEBUG
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Il2CppScheduleOne.Vehicles;
using MelonLoader;

namespace Yesla.Debugging
{
    /// <summary>
    /// Bakes the phone app's car photos: <see cref="Phone.CarPhoto.Views"/> studio shots around the player's Yesla,
    /// once with pure white paint and once with pure black, lamps off and the plate reading "YESLA". Light on paint is
    /// linear in the paint colour, so <see cref="Phone.CarPhoto"/> mixes any paint from the two sets at runtime.
    ///
    /// The car's renderers move to the visualisation's layer for each shot and back right after, so a camera that sees
    /// only that layer sees only the car, wherever it stands. Sun, ambient, fog and the game's renderer features are
    /// swapped for studio lights. Each shot is rendered over black and over white; the difference is the car's
    /// coverage, which gives a clean cut-out including the tinted glass. Shots are taken at <see cref="Supersample"/>
    /// times the size and averaged down in linear light.
    /// </summary>
    internal static class CarBake
    {
        private const int Layer = Visual.VizScene.Layer;
        private const int Supersample = 2;
        private const int Width = Phone.CarPhoto.Width * Supersample, Height = Phone.CarPhoto.Height * Supersample;
        private const float FieldOfView = 22f;
        // A little above the car, like a brochure shot: rise per metre of horizontal distance.
        private const float Elevation = 0.244f;
        private const float Fill = 0.94f;                 // share of the picture width the car takes
        private const float ShadowAlpha = 0.55f;
        private const string PlateText = "YESLA";

        private static Camera _cam;
        private static Light _key, _rim;
        private static RenderTexture _msaa, _flat;
        private static bool _running;

        /// <summary>Starts the bake into <paramref name="dir"/>; false with a reason when it cannot start.</summary>
        internal static bool Start(LandVehicle car, string dir, out string why)
        {
            why = null;
            if (_running) { why = "a bake is already running"; return false; }
            if (car == null || car.Color == null || car.Color.BodyMeshes == null || car.Color.BodyMeshes.Length == 0) { why = "no Yesla with paint"; return false; }
            Directory.CreateDirectory(dir);
            _running = true;
            MelonCoroutines.Start(Run(car, dir));
            return true;
        }

        private static IEnumerator Run(LandVehicle car, string dir)
        {
            var paints = new List<Material>();
            var was = new List<Color>();
            foreach (var bm in car.Color.BodyMeshes)
            {
                if (bm == null || bm.Renderer == null) continue;
                Material m = bm.Renderer.materials[bm.MaterialIndex];
                paints.Add(m);
                was.Add(m.color);
            }
            VehicleLights lights = car.GetComponent<VehicleLights>();
            bool lightsWere = lights != null && lights.HeadlightsOn;
            if (lights != null) lights.HeadlightsOn = false;
            Vehicle.Plate.Override = PlateText;
            Vehicle.Plate.Refresh();
            Core.Log.Msg("[Bake] baking " + Phone.CarPhoto.Views + " views x 2 paints of " + car.VehicleName + " into " + dir
                         + " (" + Width + "x" + Height + " -> " + Phone.CarPhoto.Width + "x" + Phone.CarPhoto.Height + ").");
            // Plate lettering and lamp materials settle over a few frames.
            for (int i = 0; i < 5; i++) yield return null;

            long bytes = 0;
            int written = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (char paint in new[] { 'w', 'b' })
            {
                foreach (Material m in paints) m.color = paint == 'w' ? Color.white : Color.black;
                yield return null;
                for (int view = 0; view < Phone.CarPhoto.Views; view++)
                {
                    if (car == null) { Core.Log.Error("[Bake] the car is gone - bake stopped after " + written + " file(s)."); goto done; }
                    if (lights != null && lights.HeadlightsOn) Core.Log.Warning("[Bake] the headlights came back on before view " + view + ".");
                    byte[] rgba = Take(car, view * Phone.CarPhoto.ViewStep);
                    if (rgba == null) { Core.Log.Error("[Bake] view " + view + " (" + paint + ") failed - bake stopped."); goto done; }
                    byte[] png = Phone.Png.Encode(rgba, Phone.CarPhoto.Width, Phone.CarPhoto.Height, true, System.IO.Compression.CompressionLevel.SmallestSize);
                    string path = Path.Combine(dir, paint + view.ToString("00") + ".png");
                    File.WriteAllBytes(path, png);
                    bytes += png.Length;
                    written++;
                    yield return null;
                }
            }
            Core.Log.Msg("[Bake] done: " + written + " file(s), " + (bytes / 1024) + " KB in all, " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s, in " + dir);

        done:
            for (int i = 0; i < paints.Count; i++) if (paints[i] != null) paints[i].color = was[i];
            if (lights != null && lightsWere) lights.HeadlightsOn = true;
            Vehicle.Plate.Override = null;
            Vehicle.Plate.Refresh();
            _running = false;
            Core.Log.Msg("[Bake] paint, lamps and plate restored.");
        }

        private static bool Build()
        {
            if (_cam != null) return true;
            var root = new GameObject("YeslaCarBake");
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

            _msaa = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "YeslaCarBake", antiAliasing = 8, hideFlags = HideFlags.DontSave };
            _msaa.Create();
            _flat = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32) { name = "YeslaCarBakeResolved", hideFlags = HideFlags.DontSave };
            _flat.Create();
            _cam.targetTexture = _msaa;
            return true;
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

        /// <summary>One cut-out view, bottom-up RGBA at the app's size with straight alpha; null when it failed.</summary>
        private static byte[] Take(LandVehicle car, float yaw)
        {
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
                Build();
                if (car.boundingBox == null) { Core.Log.Warning("[Bake] " + car.VehicleName + " has no bounding box to aim at."); return null; }
                // The collider's own bounds, enabled or not: the box the road AI measures the car with.
                Transform boxT = car.boundingBox.transform;
                Bounds bounds = new Bounds(boxT.TransformPoint(car.boundingBox.center), Vector3.zero);
                Vector3 h = car.boundingBox.size * 0.5f;
                var corners = new Vector3[8];
                for (int corner = 0; corner < 8; corner++)
                {
                    corners[corner] = boxT.TransformPoint(car.boundingBox.center + new Vector3((corner & 1) == 0 ? -h.x : h.x, (corner & 2) == 0 ? -h.y : h.y, (corner & 4) == 0 ? -h.z : h.z));
                    bounds.Encapsulate(corners[corner]);
                }
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
                if (moved.Count == 0) { Core.Log.Warning("[Bake] " + car.VehicleName + " has no visible renderer."); return null; }
                if (skipped > 0 && yaw == 0f) Core.Log.Msg("[Bake] left out " + skipped + " renderer(s) larger than the car, such as headlight beams.");

                Aim(car.transform, bounds.center, corners, yaw);

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
                Core.Log.Error("[Bake] photographing " + car.VehicleName + " failed: " + e.Message);
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
                return Compose(black, white, car);
            }
            catch (System.Exception e)
            {
                Core.Log.Error("[Bake] cutting out the photo failed: " + e.Message);
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(black);
                UnityEngine.Object.Destroy(white);
            }
        }

        /// <summary>
        /// Points the camera at the car from <paramref name="yaw"/> degrees around it (0 = straight ahead, negative =
        /// the car's left) and a little above, and pulls back until the car's box fills the frame from that side.
        /// </summary>
        private static void Aim(Transform car, Vector3 centre, Vector3[] corners, float yaw)
        {
            float rad = yaw * Mathf.Deg2Rad;
            Vector3 dir = car.TransformDirection(new Vector3(Mathf.Sin(rad), Elevation, Mathf.Cos(rad)).normalized);
            Quaternion look = Quaternion.LookRotation(-dir, Vector3.up);
            Vector3 right = look * Vector3.right, up = look * Vector3.up, forward = look * Vector3.forward;
            float tanH = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) * Fill;
            float tanW = tanH * Width / Height;
            // A corner at (x, y, z) from the centre in camera axes sits at depth distance + z; it fits when
            // |x| <= tanW * (distance + z), and the same for y.
            float distance = 0f;
            foreach (Vector3 corner in corners)
            {
                Vector3 v = corner - centre;
                float z = Vector3.Dot(v, forward);
                distance = Mathf.Max(distance, Mathf.Abs(Vector3.Dot(v, right)) / tanW - z, Mathf.Abs(Vector3.Dot(v, up)) / tanH - z);
            }
            _cam.transform.position = centre + dir * distance;
            _cam.transform.rotation = look;

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
        /// <c>a * c + (1 - a)</c>. A soft ground shadow under the car's footprint goes in behind it. The premultiplied
        /// result is averaged over each <see cref="Supersample"/> square, then turned back into straight alpha.
        /// </summary>
        private static byte[] Compose(Texture2D black, Texture2D white, LandVehicle car)
        {
            Tables();
            byte[] b = Bytes(black), w = Bytes(white);
            float[] shadow = Shadow(car);
            int ow = Phone.CarPhoto.Width, oh = Phone.CarPhoto.Height;
            var outBytes = new byte[ow * oh * 4];
            float share = 1f / (Supersample * Supersample);
            for (int oy = 0; oy < oh; oy++)
                for (int ox = 0; ox < ow; ox++)
                {
                    float r = 0f, g = 0f, bl = 0f, al = 0f;
                    for (int sy = 0; sy < Supersample; sy++)
                        for (int sx = 0; sx < Supersample; sx++)
                        {
                            int i = (oy * Supersample + sy) * Width + ox * Supersample + sx;
                            int o = i * 4;
                            float br = _toLinear[b[o]], bg = _toLinear[b[o + 1]], bb = _toLinear[b[o + 2]];
                            float a = 1f - ((_toLinear[w[o]] - br) + (_toLinear[w[o + 1]] - bg) + (_toLinear[w[o + 2]] - bb)) / 3f;
                            a = a < 0f ? 0f : a > 1f ? 1f : a;
                            // Car over a black shadow, premultiplied: the colour over black already is a * c.
                            r += br; g += bg; bl += bb;
                            al += a + shadow[i] * (1f - a);
                        }
                    r *= share; g *= share; bl *= share; al *= share;
                    int q = (oy * ow + ox) * 4;
                    if (al <= 0.002f) continue;
                    float inv = 1f / al;
                    outBytes[q] = Srgb(r * inv);
                    outBytes[q + 1] = Srgb(g * inv);
                    outBytes[q + 2] = Srgb(bl * inv);
                    outBytes[q + 3] = (byte)(System.Math.Min(al, 1f) * 255f + 0.5f);
                }
            return outBytes;
        }

        /// <summary>
        /// The texture's RGBA bytes in one copy. Reading an IL2CPP array element by element crosses into the runtime
        /// for every pixel.
        /// </summary>
        private static byte[] Bytes(Texture2D tex)
        {
            var native = tex.GetPixels32();
            var bytes = new byte[native.Length * 4];
            // An IL2CPP array's elements follow its header: class, monitor, bounds and length, one pointer each.
            System.Runtime.InteropServices.Marshal.Copy(System.IntPtr.Add(native.Pointer, 4 * System.IntPtr.Size), bytes, 0, bytes.Length);

            int last = native.Length - 1;
            Color32 first = native[0], end = native[last];
            if (bytes[0] == first.r && bytes[1] == first.g && bytes[2] == first.b && bytes[3] == first.a
                && bytes[last * 4] == end.r && bytes[last * 4 + 3] == end.a)
                return bytes;

            Core.Log.Error("[Bake] the block copy of the pixels does not match the texture - reading them one by one, which is slow.");
            for (int i = 0; i < native.Length; i++)
            {
                Color32 c = native[i];
                bytes[i * 4] = c.r; bytes[i * 4 + 1] = c.g; bytes[i * 4 + 2] = c.b; bytes[i * 4 + 3] = c.a;
            }
            return bytes;
        }

        /// <summary>Shadow strength per pixel: the car's footprint on the ground, projected into the picture, with a soft edge.</summary>
        private static float[] Shadow(LandVehicle lv)
        {
            var result = new float[Width * Height];
            Transform box = lv.boundingBox.transform;
            Vector3 size = lv.boundingBox.size;
            Vector3 c = lv.boundingBox.center;
            float groundY = float.MaxValue;
            if (lv.wheels != null)
                foreach (Wheel wh in lv.wheels)
                    if (wh != null && wh.wheelCollider != null) groundY = Mathf.Min(groundY, wh.wheelCollider.transform.position.y - wh.wheelCollider.radius);
            if (groundY == float.MaxValue) groundY = box.TransformPoint(c - new Vector3(0f, size.y * 0.5f, 0f)).y;

            float[] qx = new float[4], qy = new float[4];
            float[] sx = { -0.5f, 0.5f, 0.5f, -0.5f }, sz = { -0.5f, -0.5f, 0.5f, 0.5f };
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = box.TransformPoint(c + new Vector3(size.x * sx[k] * 0.92f, 0f, size.z * sz[k] * 0.96f));
                p.y = groundY;
                Vector3 sp = _cam.WorldToScreenPoint(p);
                // Screen y runs up, like the rows of ReadPixels: both bottom-up here.
                qx[k] = sp.x * Width / _cam.pixelWidth;
                qy[k] = sp.y * Height / _cam.pixelHeight;
            }

            float soft = Width * 0.045f;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    float d = SignedDistance(x + 0.5f, y + 0.5f, qx, qy);
                    float t = 0.5f - d / (2f * soft);
                    t = t < 0f ? 0f : t > 1f ? 1f : t;
                    result[y * Width + x] = ShadowAlpha * t * t * (3f - 2f * t);
                }
            return result;
        }

        /// <summary>Distance to a convex quad's edge, negative inside, either winding. Plain floats: this runs for every pixel.</summary>
        private static float SignedDistance(float px, float py, float[] qx, float[] qy)
        {
            float best = float.MaxValue;
            bool inside = true;
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) & 3;
                float abx = qx[j] - qx[i], aby = qy[j] - qy[i], apx = px - qx[i], apy = py - qy[i];
                float len2 = abx * abx + aby * aby;
                float t = (apx * abx + apy * aby) / (len2 > 1e-6f ? len2 : 1e-6f);
                t = t < 0f ? 0f : t > 1f ? 1f : t;
                float dx = apx - abx * t, dy = apy - aby * t;
                float dist = (float)System.Math.Sqrt(dx * dx + dy * dy);
                if (dist < best) best = dist;
                float cross = abx * apy - aby * apx;
                int side = cross > 0f ? 1 : cross < 0f ? -1 : 0;
                if (sign == 0) sign = side;
                else if (side != 0 && side != sign) inside = false;
            }
            return inside ? -best : best;
        }

        private static float[] _toLinear;
        private static byte[] _toSrgb;

        private static void Tables()
        {
            if (_toLinear != null) return;
            _toLinear = Phone.CarPhoto.ToLinear;
            _toSrgb = Phone.CarPhoto.ToSrgb;
        }

        private static byte Srgb(float linear)
        {
            if (!(linear > 0f)) return 0;
            if (linear >= 1f) return 255;
            return _toSrgb[(int)(linear * Phone.CarPhoto.GammaSteps + 0.5f)];
        }
    }
}
#endif
