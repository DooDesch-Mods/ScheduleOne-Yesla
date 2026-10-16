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
    ///
    /// The car is shown from where the player's camera stands, turned about the vertical axis only. A set of
    /// <see cref="Views"/> photos around the car is rendered one per frame while the app is on screen; the page stacks
    /// them and shows the one nearest the camera, which is a repaint rather than a page rebuild. The view the camera
    /// needs right now is rendered first, so the app has a photo after one frame and the full set a few frames later.
    /// </summary>
    internal static class CarPhoto
    {
        private const int Layer = Visual.VizScene.Layer;
        // Twice the 276 x 190 css box, so the phone's own scaling has detail to work with.
        private const int Width = 552, Height = 380;
        private const float FieldOfView = 22f;
        /// <summary>Photos around the car, one every 15 degrees.</summary>
        internal const int Views = 24;
        private const float ViewStep = 360f / Views;
        // A little above the car, like a brochure shot: rise per metre of horizontal distance.
        private const float Elevation = 0.244f;
        // Front left, for a camera that sits inside the car.
        private const int DefaultView = 21;
        // A camera closer than this to the car's middle, in metres, is inside it.
        private const float InsideRadius = 2.5f;
        private const float Fill = 0.94f;                 // share of the picture width the car takes
        private const float ShadowAlpha = 0.55f;

        private static Camera _cam;
        private static Light _key, _rim;
        private static RenderTexture _msaa, _flat;
        private static string _shownKey = "";
        private static string _current;
        private static int _serial;
        private static bool _failed;

        private static string[] _views;
        private static string[] _pending;
        private static LandVehicle _pendingCar;
        private static int[] _order;
        private static int _orderAt;
        private static double _pendingMs, _slowestMs;
        private static int _lastView = -1;
        // Milliseconds per stage of Take over the set in progress: prepare, render, restore, cut-out, encode.
        private static readonly double[] Stages = new double[5];
        private static readonly System.Diagnostics.Stopwatch StageWatch = new System.Diagnostics.Stopwatch();

        /// <summary>The image name of the newest photo, or null when there is none.</summary>
        internal static string Current => _current;

        /// <summary>All <see cref="Views"/> photo names of the newest complete set, index = view; null until one is.</summary>
        internal static string[] ViewNames => _views;

        /// <summary>Changes whenever a new set of views is complete.</summary>
        internal static int Revision { get; private set; }

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
            _shownKey = key;

            // A set still being rendered for the old look is dropped; its finished photos go with it.
            if (_pending != null) Drop(app, _pending, "the car, its paint or its plate changed");
            _serial++;
            _pending = new string[Views];
            _pendingCar = car;
            _pendingMs = _slowestMs = 0;
            System.Array.Clear(Stages, 0, Stages.Length);
            int first = ViewOf(car);
            _order = new int[Views];
            _order[0] = first;
            for (int i = 1, n = 1; n < Views; i++)
            {
                _order[n++] = (first + i) % Views;
                if (n < Views) _order[n++] = (first - i + Views) % Views;
            }
            _orderAt = 0;
            Core.Log.Msg("[CarPhoto] photographing " + car.VehicleName + " (" + car.OwnedColor + ", plate '" + plate + "') from " + Views + " sides, view " + first + " first.");

            // The view the camera needs now, at once: the app shows it until the whole set is done.
            if (!Step(app)) return false;
            return true;
        }

        /// <summary>
        /// Renders the next view of the set in progress. Call once a frame while the app is on screen; true while
        /// the set is going well or finished, false when it was given up.
        /// </summary>
        internal static bool Step(AppHandle app)
        {
            if (_pending == null || app == null) return true;
            LandVehicle car = _pendingCar;
            if (car == null || !car.gameObject.activeInHierarchy)
            {
                Drop(app, _pending, "the car is gone");
                _pending = null;
                _shownKey = "";
                return false;
            }

            int view = _order[_orderAt];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            byte[] png = Take(car, view * ViewStep);
            watch.Stop();
            if (png == null)
            {
                Drop(app, _pending, "view " + view + " could not be photographed");
                _pending = null;
                return false;
            }
            _pendingMs += watch.Elapsed.TotalMilliseconds;
            _slowestMs = System.Math.Max(_slowestMs, watch.Elapsed.TotalMilliseconds);

            string name = "yesla-car-photo-" + _serial + "-" + view;
            app.Image(name, png);
            _pending[view] = name;
            if (_orderAt == 0) _current = name;
            _orderAt++;
            if (_orderAt < Views) return true;

            if (_views != null) Drop(app, _views, null);
            _views = _pending;
            _pending = null;
            Revision++;
            Core.Log.Msg("[CarPhoto] " + Views + " views of " + car.VehicleName + " ready: " + _pendingMs.ToString("0") + " ms in all, slowest "
                         + _slowestMs.ToString("0") + " ms, last " + (png.Length / 1024) + " KB. Stages: prepare " + Stages[0].ToString("0")
                         + ", render " + Stages[1].ToString("0") + ", restore " + Stages[2].ToString("0") + ", cut-out " + Stages[3].ToString("0")
                         + ", encode " + Stages[4].ToString("0") + " ms.");
            return true;
        }

        /// <summary>
        /// The view that shows <paramref name="car"/> as the player's camera sees it, turned about the vertical axis
        /// only. A camera inside the car gets the front-left view; near a boundary the last view holds for a few
        /// degrees, so a camera that sits on one does not flick between two.
        /// </summary>
        internal static int ViewOf(LandVehicle car)
        {
            Camera cam = PlayerSingleton<PlayerCamera>.InstanceExists ? PlayerSingleton<PlayerCamera>.Instance.Camera : null;
            if (cam == null) cam = Camera.main;
            if (car == null || cam == null) return DefaultView;

            Vector3 v = cam.transform.position - car.transform.position;
            v.y = 0f;
            if (v.sqrMagnitude < InsideRadius * InsideRadius) return _lastView = DefaultView;
            Vector3 local = car.transform.InverseTransformDirection(v);
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            if (_lastView >= 0 && Mathf.Abs(Mathf.DeltaAngle(angle, _lastView * ViewStep)) < ViewStep * 0.65f) return _lastView;
            int k = Mathf.RoundToInt(angle / ViewStep);
            return _lastView = ((k % Views) + Views) % Views;
        }

        internal static void Reset()
        {
            _shownKey = "";
            _current = null;
            _views = null;
            _pending = null;
            _pendingCar = null;
            _lastView = -1;
        }

        private static void Drop(AppHandle app, string[] names, string why)
        {
            if (why != null) Core.Log.Warning("[CarPhoto] set of views dropped: " + why + ".");
            foreach (string n in names)
                if (n != null && n != _current) app.Image(n, null);
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

        private static byte[] Take(LandVehicle car, float yaw)
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
            StageWatch.Restart();
            try
            {
                if (car.boundingBox == null) { Core.Log.Warning("[CarPhoto] " + car.VehicleName + " has no bounding box to aim at - the app keeps the drawn car picture."); return null; }
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
                if (moved.Count == 0) { Core.Log.Warning("[CarPhoto] " + car.VehicleName + " has no visible renderer - the app keeps the drawn car picture."); return null; }
                if (skipped > 0) Core.Log.Msg("[CarPhoto] left out " + skipped + " renderer(s) larger than the car, such as headlight beams.");

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

                Lap(0);
                black = Shoot(Color.black);
                white = Shoot(Color.white);
                Lap(1);
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
                Lap(2);
            }

            try
            {
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

        /// <summary>
        /// Points the camera at the car from <paramref name="yaw"/> degrees around it (0 = straight ahead, negative =
        /// the car's left) and a little above, and pulls back until the car's box fills the frame from that side.
        /// A view from the front is narrower than one from the side, so each view gets its own distance.
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
        /// <c>a * c + (1 - a)</c>. A soft ground shadow under the car's footprint goes in behind it.
        /// </summary>
        private static byte[] Compose(Texture2D black, Texture2D white, Transform car)
        {
            Tables();
            byte[] b = Bytes(black), w = Bytes(white);
            float[] shadow = Shadow(car);
            var outTex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            var px = new Color32[Width * Height];
            for (int i = 0; i < px.Length; i++)
            {
                int o = i * 4;
                float br = _toLinear[b[o]], bg = _toLinear[b[o + 1]], bb = _toLinear[b[o + 2]];
                float a = 1f - ((_toLinear[w[o]] - br) + (_toLinear[w[o + 1]] - bg) + (_toLinear[w[o + 2]] - bb)) / 3f;
                a = a < 0f ? 0f : a > 1f ? 1f : a;
                float s = shadow[i] * (1f - a);
                float alpha = a + s;
                if (alpha <= 0.002f) { px[i] = new Color32(0, 0, 0, 0); continue; }
                // Premultiplied car over a black shadow, then back to straight alpha for the PNG.
                float inv = 1f / alpha;
                px[i] = new Color32(Srgb(br * inv), Srgb(bg * inv), Srgb(bb * inv), (byte)(alpha * 255f + 0.5f));
            }
            Lap(3);
            outTex.SetPixels32(px);
            outTex.Apply(false, false);
            byte[] png = ImageConversion.EncodeToPNG(outTex);
            UnityEngine.Object.Destroy(outTex);
            Lap(4);
            return png;
        }

        /// <summary>
        /// The texture's RGBA bytes in one copy. Reading an IL2CPP array element by element crosses into the runtime
        /// for every pixel, which made one photo take seconds.
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

            Core.Log.Error("[CarPhoto] the block copy of the pixels does not match the texture - reading them one by one, which is slow.");
            for (int i = 0; i < native.Length; i++)
            {
                Color32 c = native[i];
                bytes[i * 4] = c.r; bytes[i * 4 + 1] = c.g; bytes[i * 4 + 2] = c.b; bytes[i * 4 + 3] = c.a;
            }
            return bytes;
        }

        private static void Lap(int stage)
        {
            Stages[stage] += StageWatch.Elapsed.TotalMilliseconds;
            StageWatch.Restart();
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

            float[] qx = new float[4], qy = new float[4];
            float[] sx = { -0.5f, 0.5f, 0.5f, -0.5f }, sz = { -0.5f, -0.5f, 0.5f, 0.5f };
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = box.TransformPoint(c + new Vector3(size.x * sx[k] * 0.92f, 0f, size.z * sz[k] * 0.96f));
                p.y = groundY;
                Vector3 sp = _cam.WorldToScreenPoint(p);
                // Screen y runs up, the picture's rows run down after ReadPixels' bottom-up order - both bottom-up here.
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

        /// <summary>
        /// Distance to a convex quad's edge, negative inside, either winding. Plain floats on purpose: Vector2 and
        /// Mathf members are calls into the IL2CPP runtime, and this runs for every pixel.
        /// </summary>
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
        // Linear light to an sRGB byte, sampled finely enough that neighbouring entries differ by at most one step.
        private const int GammaSteps = 4096;
        private static byte[] _toSrgb;

        private static void Tables()
        {
            if (_toLinear != null) return;
            _toLinear = new float[256];
            for (int i = 0; i < 256; i++) _toLinear[i] = Mathf.GammaToLinearSpace(i / 255f);
            _toSrgb = new byte[GammaSteps + 1];
            for (int i = 0; i <= GammaSteps; i++) _toSrgb[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(Mathf.LinearToGammaSpace(i / (float)GammaSteps)) * 255f);
        }

        private static byte Srgb(float linear)
        {
            if (!(linear > 0f)) return 0;
            if (linear >= 1f) return 255;
            return _toSrgb[(int)(linear * GammaSteps + 0.5f)];
        }
    }
}
