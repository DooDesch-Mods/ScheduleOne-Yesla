using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppScheduleOne.Map.Infrastructure;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Vehicles;
using UnityEngine.Rendering.Universal;

namespace Yesla.Visual
{
    /// <summary>
    /// The driving visualisation: a small stand-in world, far below the map, that mirrors what is around the car and
    /// is photographed into a texture about 15 times a second. The texture feeds the HUD panel, the full-screen view
    /// and the car's centre screen.
    ///
    /// Stand-ins sit on layer 20 ("RuntimePreviewGeneration"): all 32 layers carry names, and the player camera's mask
    /// leaves this one out. They also sit 5 km below the map, past the player camera's 3 km far plane, so neither the
    /// game nor the stand-in camera can see the other's world. The camera is never enabled: it renders by hand, with
    /// the sun swapped for its own light for that one call, the same trade ModelIcon in Breed to Seed makes.
    /// </summary>
    internal static class VizScene
    {
        internal const int Layer = 20;
        internal static readonly Vector3 Origin = new Vector3(0f, -5000f, 0f);
        internal const int Width = 1024, Height = 800;

        private const float RenderInterval = 1f / 15f;
        private const float Range = 70f;
        private const float LightRange = 45f;
        private const float LightCone = 0.75f;   // cosine: about 40 degrees either side of straight ahead
        private const float RoadRadius = 120f;
        private const float RoadRebuildAfter = 30f;
        private const float RouteAhead = 220f;
        private const float RouteWidth = 1.3f;
        private const string LodName = "Body_LOD1";

        private static readonly Color Background = new Color(0.075f, 0.08f, 0.09f);
        private static readonly Color RoadColour = new Color(0.17f, 0.18f, 0.2f);
        private static readonly Color LineColour = new Color(0.6f, 0.62f, 0.66f);
        private static readonly Color RouteColour = new Color(0.2f, 0.47f, 1f);
        private static readonly Color OwnColour = new Color(0.93f, 0.94f, 0.96f);
        private static readonly Color CarColour = new Color(0.42f, 0.44f, 0.48f);
        private static readonly Color PersonColour = new Color(0.8f, 0.82f, 0.86f);

        internal static RenderTexture Texture { get; private set; }
        internal static bool Built => _root != null;

        private static GameObject _root;
        private static Camera _cam;
        private static Light _light;
        private static Material _unlit, _lit;
        private static Material _roadMat, _lineMat, _routeMat, _ownMat, _carMat, _personMat, _redMat, _orangeMat, _greenMat;
        private static Mesh _carMesh, _capsule, _sphere, _roadMesh, _lineMesh, _routeMesh;
        private static Bounds _carBounds;
        private static Transform _own, _pin;
        private static readonly List<Transform> Cars = new List<Transform>();
        private static readonly List<Transform> People = new List<Transform>();
        private static readonly List<MeshRenderer> Lights = new List<MeshRenderer>();
        private static List<TrafficLight> _trafficLights;
        private static Vector3 _roadCentre;
        private static bool _roadBuilt;
        private static Vector3 _heading;
        private static float _nextRender;

        // Render cost, for 'yesla viz' and the periodic log line.
        private static double _msSum;
        private static int _renders;
        private static float _lastCostLog;
        internal static double LastMs { get; private set; }

        /// <summary>What the overlay shows; filled on every render.</summary>
        internal static VizInfo Info;

        /// <summary>Builds the stand-in world once per loaded world. False when something it needs is missing.</summary>
        internal static bool Ensure(LandVehicle car)
        {
            if (_root != null) return true;
            try
            {
                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                if (unlit == null || lit == null) { Core.Log.Error("[Viz] URP shaders missing (unlit " + (unlit != null) + ", lit " + (lit != null) + ") - no visualisation."); return false; }

                _carMesh = CarMesh(car, out _carBounds);
                if (_carMesh == null) return false;

                _root = new GameObject("YeslaViz") { layer = Layer };
                UnityEngine.Object.DontDestroyOnLoad(_root);
                _root.hideFlags = HideFlags.DontSave;
                _root.transform.position = Origin;

                _unlit = new Material(unlit);
                _lit = new Material(lit);
                // Road, lines and route lie flat on top of each other; heights from the lane links and from the
                // agent's path differ by a few centimetres, so depth would let the road cut through the route.
                // They draw in a fixed order without writing depth, before every car and person.
                _roadMat = Ground(Flat(RoadColour), 0);
                _lineMat = Ground(Flat(LineColour), 1);
                _routeMat = Ground(Flat(RouteColour), 2);
                _redMat = Flat(new Color(1f, 0.16f, 0.12f));
                _orangeMat = Flat(new Color(1f, 0.62f, 0.12f));
                _greenMat = Flat(new Color(0.22f, 0.9f, 0.38f));
                _ownMat = Shaded(OwnColour, 0.55f);
                _carMat = Shaded(CarColour, 0.35f);
                _personMat = Shaded(PersonColour, 0.1f);

                _capsule = PrimitiveMesh(PrimitiveType.Capsule);
                _sphere = PrimitiveMesh(PrimitiveType.Sphere);
                _roadMesh = new Mesh { name = "YeslaViz_Road" };
                _lineMesh = new Mesh { name = "YeslaViz_Lines" };
                _routeMesh = new Mesh { name = "YeslaViz_Route" };
                Static("Road", _roadMesh, _roadMat);
                Static("Lines", _lineMesh, _lineMat);
                Static("Route", _routeMesh, _routeMat);

                _own = CarProxy("Own", _ownMat);
                _pin = Proxy("Destination", _sphere, Flat(RouteColour), new Vector3(1.4f, 1.4f, 1.4f));

                var camGo = new GameObject("YeslaViz_Camera") { layer = Layer };
                camGo.transform.SetParent(_root.transform, false);
                _cam = camGo.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.cullingMask = 1 << Layer;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = Background;
                _cam.fieldOfView = 50f;
                _cam.nearClipPlane = 0.3f;
                _cam.farClipPlane = 260f;
                _cam.allowHDR = false;

                var lightGo = new GameObject("YeslaViz_Light") { layer = Layer };
                lightGo.transform.SetParent(_root.transform, false);
                lightGo.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
                _light = lightGo.AddComponent<Light>();
                _light.type = LightType.Directional;
                _light.intensity = 1.1f;
                _light.color = Color.white;
                _light.shadows = LightShadows.None;
                _light.cullingMask = 1 << Layer;
                _light.enabled = false;

                Texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { name = "YeslaViz", antiAliasing = 4 };
                Texture.hideFlags = HideFlags.DontSave;
                Texture.Create();
                _cam.targetTexture = Texture;

                VizOverlay.Build(_cam);
                _roadBuilt = false;
                Core.Log.Msg("[Viz] stand-in world built at " + Origin + " on layer " + Layer + " (" + LayerMask.LayerToName(Layer) + "), texture " + Width + "x" + Height + ".");
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Error("[Viz] building the visualisation failed - it stays off: " + e);
                Destroy();
                return false;
            }
        }

        /// <summary>Moves the stand-ins and renders, at most 15 times a second.</summary>
        internal static void Tick(LandVehicle car)
        {
            if (_root == null || car == null) return;
            float now = Time.unscaledTime;
            if (now < _nextRender) return;
            _nextRender = now + RenderInterval;

            var sw = Stopwatch.StartNew();
            Vector3 pos = car.transform.position;
            if (!_roadBuilt || Flat(pos - _roadCentre).magnitude > RoadRebuildAfter)
            {
                int lanes = VizRoads.Build(pos, RoadRadius, _roadMesh, _lineMesh);
                if (!_roadBuilt) Core.Log.Msg("[Viz] road around " + pos.ToString("F0") + ": " + lanes + " lane(s).");
                _roadCentre = pos;
                _roadBuilt = true;
            }

            Place(_own, car, _carBounds);
            PlaceCars(car, pos);
            PlacePeople(car, pos);
            PlaceLights(car, pos);
            Info = Route.Update(car, _routeMesh, RouteAhead, RouteWidth, _pin);
            Info.SpeedKmh = Mathf.Abs(car.Speed_Kmh);
            Info.LimitKmh = SpeedLimit(pos);
            AimCamera(car);
            VizOverlay.Update(Info);
            Render();

            sw.Stop();
            LastMs = sw.Elapsed.TotalMilliseconds;
            _msSum += LastMs;
            _renders++;
            if (now - _lastCostLog > 60f)
            {
                Core.Log.Msg("[Viz] " + _renders + " render(s), average " + (_msSum / Math.Max(1, _renders)).ToString("F2") + " ms each, last " + LastMs.ToString("F2") + " ms.");
                _lastCostLog = now;
                _msSum = 0; _renders = 0;
            }
        }

        private static void AimCamera(LandVehicle car)
        {
            Vector3 fwd = Flat(car.transform.forward);
            if (fwd.sqrMagnitude < 0.01f) fwd = _heading.sqrMagnitude > 0.01f ? _heading : Vector3.forward;
            fwd.Normalize();
            _heading = _heading.sqrMagnitude < 0.01f ? fwd : Vector3.Slerp(_heading, fwd, 0.35f).normalized;
            Vector3 at = car.transform.position + Origin;
            _cam.transform.position = at - _heading * 10.5f + Vector3.up * 6.8f;
            _cam.transform.rotation = Quaternion.LookRotation(at + _heading * 9f - _cam.transform.position, Vector3.up);
        }

        private static void Render()
        {
            Light sun = RenderSettings.sun;
            bool sunOn = sun != null && sun.enabled;
            bool fog = RenderSettings.fog;
            var ambientMode = RenderSettings.ambientMode;
            Color ambient = RenderSettings.ambientLight;
            RenderTexture active = RenderTexture.active;
            try
            {
                FeaturesOff();
                if (sunOn) sun.enabled = false;
                RenderSettings.sun = _light;
                _light.enabled = true;
                RenderSettings.fog = false;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.55f, 0.56f, 0.6f);

                // A camera added at runtime has no URP camera data, so URP does not clear for it.
                RenderTexture.active = Texture;
                GL.Clear(true, true, Background);
                RenderTexture.active = active;
                _cam.Render();
            }
            finally
            {
                _light.enabled = false;
                RenderSettings.sun = sun;
                if (sunOn) sun.enabled = true;
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambient;
                RenderTexture.active = active;
                FeaturesBack();
            }
        }

        private static readonly List<ScriptableRendererFeature> Paused = new List<ScriptableRendererFeature>();
        private static bool _featuresLogged;

        /// <summary>
        /// The game's renderer features (its fog, rain, outlines, full-screen effects) run for every camera, this one
        /// too. They go off for the one render and back on right after, as the game's own IconGenerator does.
        /// </summary>
        private static void FeaturesOff()
        {
            Paused.Clear();
            if (!NetworkSingleton<Il2CppScheduleOne.Weather.EnvironmentManager>.InstanceExists)
            {
                if (!_featuresLogged) { Core.Log.Warning("[Viz] no EnvironmentManager - the game's renderer features stay on for the visualisation."); _featuresLogged = true; }
                return;
            }
            var data = NetworkSingleton<Il2CppScheduleOne.Weather.EnvironmentManager>.Instance._rendererData;
            var features = data != null ? data.rendererFeatures : null;
            if (features == null)
            {
                if (!_featuresLogged) { Core.Log.Warning("[Viz] the EnvironmentManager has no renderer data - the game's renderer features stay on for the visualisation."); _featuresLogged = true; }
                return;
            }
            for (int i = 0; i < features.Count; i++)
            {
                ScriptableRendererFeature f = features[i];
                if (f == null || !f.isActive) continue;
                f.SetActive(false);
                Paused.Add(f);
            }
            if (!_featuresLogged)
            {
                var names = new List<string>();
                foreach (var f in Paused) names.Add(f.name);
                Core.Log.Msg("[Viz] renderer features off for the visualisation: " + (names.Count == 0 ? "none active" : string.Join(", ", names)) + ".");
                _featuresLogged = true;
            }
        }

        private static void FeaturesBack()
        {
            foreach (var f in Paused)
                if (f != null) f.SetActive(true);
            Paused.Clear();
        }

        private static void PlaceCars(LandVehicle own, Vector3 at)
        {
            int n = 0;
            if (NetworkSingleton<VehicleManager>.InstanceExists)
            {
                var all = NetworkSingleton<VehicleManager>.Instance.AllVehicles;
                for (int i = 0; i < all.Count; i++)
                {
                    LandVehicle v = all[i];
                    if (v == null || v.Pointer == own.Pointer || !v.gameObject.activeInHierarchy || v.boundingBox == null) continue;
                    if (Flat(v.transform.position - at).sqrMagnitude > Range * Range) continue;
                    if (n == Cars.Count) Cars.Add(CarProxy("Car" + n, _carMat));
                    Transform p = Cars[n++];
                    p.gameObject.SetActive(true);
                    BoxCollider bb = v.boundingBox;
                    Vector3 size = Vector3.Scale(bb.size, bb.transform.lossyScale);
                    p.position = bb.transform.TransformPoint(bb.center) + Origin;
                    p.rotation = bb.transform.rotation;
                    p.localScale = new Vector3(size.x / _carBounds.size.x, size.y / _carBounds.size.y, size.z / _carBounds.size.z);
                }
            }
            for (int i = n; i < Cars.Count; i++) Cars[i].gameObject.SetActive(false);
            Info.Cars = n;
        }

        private static void PlacePeople(LandVehicle own, Vector3 at)
        {
            int n = 0;
            var npcs = NPCManager.NPCRegistry;
            for (int i = 0; npcs != null && i < npcs.Count; i++)
            {
                NPC npc = npcs[i];
                if (npc == null || !npc.gameObject.activeInHierarchy || !npc.isVisible || npc.IsInVehicle) continue;
                n = PlacePerson(n, npc.transform.position, at);
            }
            var players = Player.PlayerList;
            for (int i = 0; players != null && i < players.Count; i++)
            {
                Player p = players[i];
                if (p == null || p.CurrentVehicle != null || !p.gameObject.activeInHierarchy) continue;
                n = PlacePerson(n, p.transform.position, at);
            }
            for (int i = n; i < People.Count; i++) People[i].gameObject.SetActive(false);
            Info.People = n;
        }

        private static int PlacePerson(int n, Vector3 p, Vector3 at)
        {
            if (Flat(p - at).sqrMagnitude > Range * Range) return n;
            if (n == People.Count) People.Add(Proxy("Person" + n, _capsule, _personMat, new Vector3(0.55f, 0.88f, 0.55f)));
            Transform t = People[n];
            t.gameObject.SetActive(true);
            t.position = p + Vector3.up * 0.88f + Origin;
            return n + 1;
        }

        private static void PlaceLights(LandVehicle car, Vector3 at)
        {
            if (_trafficLights == null)
            {
                _trafficLights = new List<TrafficLight>();
                foreach (TrafficLight l in UnityEngine.Object.FindObjectsOfType<TrafficLight>())
                    if (l != null) _trafficLights.Add(l);
                Core.Log.Msg("[Viz] " + _trafficLights.Count + " traffic light(s) in the world.");
            }
            int n = 0;
            Vector3 fwd = Flat(car.transform.forward).normalized;
            foreach (TrafficLight l in _trafficLights)
            {
                if (l == null) continue;
                Vector3 p = l.transform.position;
                Vector3 to = Flat(p - at);
                if (to.sqrMagnitude > LightRange * LightRange || Vector3.Dot(to.normalized, fwd) < LightCone) continue;
                if (n == Lights.Count) Lights.Add(Proxy("Light" + n, _sphere, _redMat, new Vector3(0.7f, 0.7f, 0.7f)).GetComponent<MeshRenderer>());
                MeshRenderer r = Lights[n++];
                r.gameObject.SetActive(true);
                // The light's own height is the top of its pole; at car height it reads as part of the junction, not the sky.
                r.transform.position = new Vector3(p.x, at.y + 1.6f, p.z) + Origin;
                TrafficLight.State s = l.CurrentState;
                r.sharedMaterial = s == TrafficLight.State.Red ? _redMat : s == TrafficLight.State.Orange ? _orangeMat : _greenMat;
            }
            for (int i = n; i < Lights.Count; i++) Lights[i].gameObject.SetActive(false);
        }

        /// <summary>The speed limit where the car is, in km/h: the speed zone's, or the game's unmarked-road speed.</summary>
        private static float SpeedLimit(Vector3 at)
        {
            var zones = SpeedZone.speedZones;
            for (int i = 0; zones != null && i < zones.Count; i++)
            {
                SpeedZone z = zones[i];
                if (z != null && z.col != null && z.col.bounds.Contains(at)) return z.speed;
            }
            return Il2CppScheduleOne.Vehicles.AI.VehicleAgent.UnmarkedSpeed;
        }

        private static void Place(Transform proxy, LandVehicle car, Bounds b)
        {
            proxy.position = car.transform.position + Origin;
            proxy.rotation = car.transform.rotation;
            proxy.localScale = Vector3.one;
            // The own car's mesh is in car space, so its child sits at the origin; see CarProxy.
            proxy.GetChild(0).localPosition = Vector3.zero;
        }

        /// <summary>
        /// The Yesla's middle LOD, merged into one mesh in car space. Every car is drawn with it: the own car at
        /// scale one, other cars stretched to their bounding box.
        /// </summary>
        private static Mesh CarMesh(LandVehicle car, out Bounds bounds)
        {
            bounds = default;
            // LOD2 is a few hundred triangles and reads as a box from the chase camera; LOD1 keeps the silhouette.
            Transform lod = FindDeep(car.transform, LodName);
            if (lod == null) { Core.Log.Error("[Viz] the Yesla has no " + LodName + " - no visualisation."); return null; }
            var filters = lod.GetComponentsInChildren<MeshFilter>(true);
            // A CombineInstance takes one submesh; the LOD's parts are submeshes of one mesh, so each gets its own.
            var parts = new List<CombineInstance>();
            Matrix4x4 toCar = car.transform.worldToLocalMatrix;
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh m = filters[i].sharedMesh;
                if (m == null) continue;
                for (int s = 0; s < m.subMeshCount; s++)
                    parts.Add(new CombineInstance { mesh = m, subMeshIndex = s, transform = toCar * filters[i].transform.localToWorldMatrix });
            }
            if (parts.Count == 0) { Core.Log.Error("[Viz] " + LodName + " has no mesh - no visualisation."); return null; }
            var combine = new Il2CppStructArray<CombineInstance>(parts.Count);
            for (int i = 0; i < parts.Count; i++) combine[i] = parts[i];
            var mesh = new Mesh { name = "YeslaViz_Car", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(combine, true, true);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontSave;
            bounds = mesh.bounds;
            Core.Log.Msg("[Viz] car mesh from " + LodName + ": " + filters.Length + " mesh(es), " + parts.Count + " submesh(es), " + mesh.vertexCount + " vertices, " + (mesh.triangles.Length / 3) + " triangles, size " + bounds.size.ToString("F2") + ".");
            return mesh;
        }

        /// <summary>A stand-in car: own car child at the origin, other cars' child centred so the box scale fits.</summary>
        private static Transform CarProxy(string name, Material mat)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(_root.transform, false);
            var child = new GameObject("Body") { layer = Layer };
            child.transform.SetParent(go.transform, false);
            child.transform.localPosition = -_carBounds.center;
            child.AddComponent<MeshFilter>().sharedMesh = _carMesh;
            var r = child.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        private static Transform Proxy(string name, Mesh mesh, Material mat, Vector3 scale)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(_root.transform, false);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        private static void Static(string name, Mesh mesh, Material mat)
        {
            // Road, lines and route are in world coordinates; their parent sits at the origin offset.
            Proxy(name, mesh, mat, Vector3.one);
        }

        private static Mesh PrimitiveMesh(PrimitiveType type)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            Mesh m = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(go);
            return m;
        }

        internal static Material Flat(Color c)
        {
            var m = new Material(_unlit);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Cull", 0f);
            m.hideFlags = HideFlags.DontSave;
            return m;
        }

        private static Material Ground(Material m, int order)
        {
            m.SetFloat("_ZWrite", 0f);
            m.renderQueue = 1990 + order;
            return m;
        }

        private static Material Shaded(Color c, float smoothness)
        {
            var m = new Material(_lit);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", smoothness);
            m.hideFlags = HideFlags.DontSave;
            return m;
        }

        internal static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform f = FindDeep(t.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }

        internal static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>Writes the current frame to a PNG, for checks from the console.</summary>
        internal static string Save(string path)
        {
            if (Texture == null) return "no visualisation texture yet";
            RenderTexture active = RenderTexture.active;
            try
            {
                RenderTexture.active = Texture;
                var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                byte[] png = ImageConversion.EncodeToPNG(tex);
                System.IO.File.WriteAllBytes(path, png);
                UnityEngine.Object.Destroy(tex);
                return "saved " + path;
            }
            finally { RenderTexture.active = active; }
        }

        internal static string Describe()
        {
            if (_root == null) return "viz: not built";
            return "viz: last render " + LastMs.ToString("F2") + " ms, cars " + Info.Cars + ", people " + Info.People + ", route " + Info.RouteMetres.ToString("F0")
                   + " m, next " + Info.Next + ", speed " + Info.SpeedKmh.ToString("F0") + "/" + Info.LimitKmh.ToString("F0") + " km/h";
        }

        internal static void Destroy()
        {
            VizOverlay.Reset();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (Texture != null) { Texture.Release(); UnityEngine.Object.Destroy(Texture); }
            _root = null; _cam = null; _light = null; Texture = null;
            _own = null; _pin = null;
            Cars.Clear(); People.Clear(); Lights.Clear();
            _trafficLights = null;
            _roadBuilt = false;
            _heading = Vector3.zero;
            Info = default;
        }
    }
}
