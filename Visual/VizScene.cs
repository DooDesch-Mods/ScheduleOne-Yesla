using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppScheduleOne.Map.Infrastructure;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Vehicles;
using Il2CppTMPro;
using Yesla.Driving;
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
        private const float BuildingRadius = 100f;
        private const float RouteAhead = 70f;
        private const float RouteWidth = 1.95f;   // the car's own width, as Tesla draws its path
        private const float FieldOfView = 38f;
        private const float SpotRange = 30f;        // free spots this close to the planned spot, or to a slow car, are drawn
        private const float SpotsNearPlanned = 90f; // the planned spot's lot shows once the car is this close to it
        private const float SpotsBelowKmh = 12f;    // without a planned spot, spots show only when crawling, as when looking for one
        private const float SpotLine = 0.16f;
        private static readonly Color SpotColour = new Color(0.42f, 0.435f, 0.46f);       // #6B6F75
        private static readonly Color TargetColour = new Color(0.173f, 0.49f, 0.98f);     // #2C7DFA, as the route
        private static readonly Color TargetFill = new Color(0.106f, 0.2f, 0.345f);       // #1B3358

        // Chase camera: metres behind, above, and the ground point ahead it looks at. Close is the normal view; wide
        // shows more of a junction before a turn and of the road at speed.
        private static readonly Vector3 CloseCam = new Vector3(14f, 8f, 6f);
        private static readonly Vector3 WideCam = new Vector3(21f, 12.5f, 10f);
        private const float WideBeforeTurn = 45f;
        private const float WideAboveKmh = 70f;
        private const string ModelName = "YeslaModel";
        private const string VizName = "Body_Viz";

        private static readonly Color Background = new Color(0.141f, 0.149f, 0.173f);   // off-road, #24262C
        private static readonly Color Horizon = new Color(0.055f, 0.059f, 0.067f);      // #0E0F11
        private static readonly Color RoadColour = new Color(0.11f, 0.114f, 0.129f);    // #1C1D21
        private static readonly Color LineColour = new Color(0.9f, 0.91f, 0.92f);       // #E6E8EB
        private static readonly Color EdgeColour = new Color(0.54f, 0.553f, 0.576f);    // #8A8D93
        private static readonly Color CentreColour = new Color(0.95f, 0.82f, 0.106f);   // #F2D21B
        private static readonly Color RouteColour = new Color(0.173f, 0.49f, 0.98f, 0.85f);   // #2C7DFA
        private static readonly Color GlowColour = new Color(0.173f, 0.482f, 1f, 0.45f);      // #2C7BFF
        private static readonly Color OwnFallback = new Color(0.93f, 0.94f, 0.96f);
        private static readonly Color OwnDarkColour = new Color(0.12f, 0.13f, 0.15f);
        private static readonly Color CarColour = new Color(0.659f, 0.671f, 0.69f);     // #A8ABB0
        private static readonly Color CarDarkColour = new Color(0.25f, 0.26f, 0.28f);
        private static readonly Color LeadColour = new Color(0.165f, 0.173f, 0.188f);   // #2A2C30, the car ahead on the path
        private static readonly Color LeadDarkColour = new Color(0.08f, 0.085f, 0.09f);
        private static readonly Color PersonColour = new Color(0.659f, 0.671f, 0.69f);
        private static readonly Color HousingColour = new Color(0.165f, 0.169f, 0.18f);  // #2A2B2E
        private static readonly Color LampOffColour = new Color(0.23f, 0.235f, 0.25f);
        private static readonly Color BuildingColour = new Color(0.62f, 0.64f, 0.68f, 0.4f);  // #9EA3AD, see-through

        internal static RenderTexture Texture { get; private set; }
        internal static bool Built => _root != null;

        private static GameObject _root;
        private static Camera _cam;
        private static Light _light;
        private static Material _unlit, _lit;
        private static Material _roadMat, _lineMat, _edgeMat, _centreMat, _routeMat, _ownMat, _ownDarkMat, _carMat, _carDarkMat, _leadMat, _leadDarkMat,
            _personMat, _redMat, _orangeMat, _greenMat, _lampOffMat, _housingMat, _shadowMat, _glowMat;
        private static Mesh _carMesh, _capsule, _sphere, _cube, _figure, _flat, _roadMesh, _lineMesh, _edgeMesh, _centreMesh, _routeMesh;
        private static Mesh _spotMesh, _targetMesh, _targetFillMesh, _walkMesh;
        private static Material _buildingMat;
        private static TextMeshPro _spotLabel;
        private static readonly List<ParkTarget> Spots = new List<ParkTarget>();
        private static float _nextSpots;
        private static int _spotsShown = -1;
        private static Il2CppReferenceArray<Material> _carMats, _leadMats;
        private static readonly List<Texture2D> Textures = new List<Texture2D>();
        private static MeshRenderer _paint;
        private static GameObject _glow;
        private static float _nextPaint, _zoom;
        private static Bounds _carBounds;
        private static Transform _own, _pin;
        private static readonly List<Transform> Cars = new List<Transform>();
        private static readonly List<Transform> People = new List<Transform>();
        private static readonly List<SignalProxy> Lights = new List<SignalProxy>();

        /// <summary>A traffic light stand-in: a dark housing with three lamps, only the active one lit.</summary>
        private sealed class SignalProxy
        {
            internal Transform Root;
            internal MeshRenderer Red, Amber, Green;
        }
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
                _edgeMat = Ground(Flat(EdgeColour), 1);
                _lineMat = Ground(Flat(LineColour), 2);
                _centreMat = Ground(Flat(CentreColour), 2);
                _routeMat = Overlay(Fading(RouteColour, Gradient("YeslaViz_RouteFade", 4, 64, false, 0.65f, 1f)), 3);
                _glowMat = Overlay(Fading(GlowColour, Gradient("YeslaViz_Blob", 64, 64, true, 0f, 1f)), 4);
                _shadowMat = Overlay(Fading(new Color(0f, 0f, 0f, 0.55f), Gradient("YeslaViz_Blob", 64, 64, true, 0f, 1f)), 5);
                _redMat = Flat(new Color(1f, 0.231f, 0.188f));       // #FF3B30
                _orangeMat = Flat(new Color(1f, 0.769f, 0f));        // #FFC400
                _greenMat = Flat(new Color(0.125f, 0.816f, 0.29f));  // #20D04A
                _lampOffMat = Shaded(LampOffColour, 0.3f);
                _housingMat = Shaded(HousingColour, 0.3f);
                _ownMat = Shaded(OwnFallback, 0.65f);
                _ownDarkMat = Shaded(OwnDarkColour, 0.85f);
                _carMat = Shaded(CarColour, 0.25f);
                _carDarkMat = Shaded(CarDarkColour, 0.5f);
                _leadMat = Shaded(LeadColour, 0.3f);
                _leadDarkMat = Shaded(LeadDarkColour, 0.5f);
                _personMat = Shaded(PersonColour, 0.15f);
                _buildingMat = SeeThrough(BuildingColour);
                _carMats = Pair(_carMat, _carDarkMat);
                _leadMats = Pair(_leadMat, _leadDarkMat);

                _capsule = PrimitiveMesh(PrimitiveType.Capsule);
                _sphere = PrimitiveMesh(PrimitiveType.Sphere);
                _cube = PrimitiveMesh(PrimitiveType.Cube);
                _figure = FigureMesh();
                _flat = FlatQuad();
                _roadMesh = new Mesh { name = "YeslaViz_Road" };
                _lineMesh = new Mesh { name = "YeslaViz_Lines" };
                _edgeMesh = new Mesh { name = "YeslaViz_Edges" };
                _centreMesh = new Mesh { name = "YeslaViz_Centre" };
                _routeMesh = new Mesh { name = "YeslaViz_Route" };
                Static("Road", _roadMesh, _roadMat);
                Static("Edges", _edgeMesh, _edgeMat);
                Static("Lines", _lineMesh, _lineMat);
                Static("Centre", _centreMesh, _centreMat);
                _walkMesh = new Mesh { name = "YeslaViz_Crosswalks" };
                Static("Crosswalks", _walkMesh, Ground(Flat(LineColour), 3));
                Static("Route", _routeMesh, _routeMat);
                _spotMesh = new Mesh { name = "YeslaViz_Spots" };
                _targetMesh = new Mesh { name = "YeslaViz_TargetSpot" };
                _targetFillMesh = new Mesh { name = "YeslaViz_TargetFill" };
                Static("TargetFill", _targetFillMesh, Ground(Flat(TargetFill), 1));
                Static("Spots", _spotMesh, Ground(Flat(SpotColour), 2));
                Static("TargetSpot", _targetMesh, Ground(Flat(TargetColour), 3));
                _spotLabel = SpotLabel();

                _paint = PaintRenderer(car);
                _own = CarProxy("Own", _ownMat, _ownDarkMat);
                _glow = Blob(_own.GetChild(0), "Glow", _glowMat, 1.7f, 1.35f, 0.02f);
                _glow.SetActive(false);
                _pin = Proxy("Destination", _sphere, Flat(RouteColour), new Vector3(1.4f, 1.4f, 1.4f));

                var camGo = new GameObject("YeslaViz_Camera") { layer = Layer };
                camGo.transform.SetParent(_root.transform, false);
                _cam = camGo.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.cullingMask = 1 << Layer;
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = Background;
                _cam.fieldOfView = FieldOfView;
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

                Fade(camGo.transform);
                VizOverlay.Build(_cam);
                _roadBuilt = false;
                _nextSpots = 0f;
                _spotsShown = -1;
                _zoom = 0f;
                _nextPaint = 0f;
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
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.VizScene.Tick");
#endif
            if (_root == null || car == null) return;
            float now = Time.unscaledTime;
            if (now < _nextRender) return;
            _nextRender = now + RenderInterval;

            var sw = Stopwatch.StartNew();
            Vector3 pos = car.transform.position;
            if (!_roadBuilt || Flat(pos - _roadCentre).magnitude > RoadRebuildAfter)
            {
                int lanes = VizRoads.Build(pos, RoadRadius, _roadMesh, _lineMesh, _edgeMesh, _centreMesh);
                int walks = VizRoads.Crosswalks(pos, RoadRadius, _walkMesh);
                if (!_roadBuilt) Core.Log.Msg("[Viz] road around " + pos.ToString("F0") + ": " + lanes + " lane(s), " + VizRoads.TwoWay + " beside oncoming traffic, " + VizRoads.Junctions + " junction(s), " + walks + " crossing(s).");
                _roadCentre = pos;
                _roadBuilt = true;
            }

            VizBuildings.Tick(pos, BuildingRadius, _root.transform, _buildingMat, _cube);
            Place(_own, car, _carBounds);
            if (now >= _nextPaint) { UpdatePaint(); _nextPaint = now + 1f; }
            Info = Route.Update(car, _routeMesh, RouteAhead, RouteWidth, _pin);
            _glow.SetActive(Info.Driving);
            PlaceCars(car, pos);
            PlacePeople(car, pos);
            PlaceLights(car, pos);
            if (now >= _nextSpots) { PlaceSpots(car, pos); _nextSpots = now + 1f; }
            if (_spotLabel.gameObject.activeSelf)
            {
                _spotLabel.transform.rotation = Quaternion.LookRotation(Vector3.down, _heading.sqrMagnitude > 0.01f ? _heading : Vector3.forward);
                // The blue spot marks the destination; the pin would sit on top of it.
                _pin.gameObject.SetActive(false);
            }
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

        /// <summary>
        /// Parking spots, as Tesla draws them: the free bays the car could take as grey outlines open towards the
        /// aisle, the one it parks in or drives to filled blue with a "P". Once a second; each free check is a box
        /// overlap in the real world.
        /// </summary>
        private static void PlaceSpots(LandVehicle car, Vector3 pos)
        {
            ParkTarget planned = Autopilot.PlannedSpot;
            if (planned != null && planned.Kerb) planned = null;
            bool near = planned != null && Flat(planned.Centre - pos).magnitude < SpotsNearPlanned;
            bool crawl = Mathf.Abs(car.Speed_Kmh) < SpotsBelowKmh;
            Spots.Clear();
            if (near || crawl) Parking.Nearby(car, near ? planned.Centre : pos, SpotRange, Spots);

            var v = new List<Vector3>();
            var t = new List<int>();
            Vector3 dims = car.BoundingBoxDimensions;
            float halfW = dims.x * 0.5f + 0.35f, halfL = dims.z * 0.5f + 0.3f;
            foreach (ParkTarget s in Spots)
            {
                if (planned != null && s.Spot != null && planned.Spot != null && s.Spot.Pointer == planned.Spot.Pointer) continue;
                Outline(v, t, OnFloor(car, s), s.Into, halfW, halfL, false);
            }
            VizRoads.Fill(_spotMesh, v, t);

            v.Clear(); t.Clear();
            var fv = new List<Vector3>();
            var ft = new List<int>();
            if (near)
            {
                Vector3 c = OnFloor(car, planned);
                Outline(v, t, c, planned.Into, halfW, halfL, true);
                Vector3 right = Vector3.Cross(Vector3.up, planned.Heading);
                VizRoads.Quad(fv, ft, c - planned.Heading * halfL, c + planned.Heading * halfL, right, 0f, halfW * 2f, 0.01f);
                _spotLabel.transform.position = Origin + c + Vector3.up * 0.05f;
            }
            VizRoads.Fill(_targetMesh, v, t);
            VizRoads.Fill(_targetFillMesh, fv, ft);
            _spotLabel.gameObject.SetActive(near);

            int shown = Spots.Count + (near ? 1000 : 0);
            if (shown != _spotsShown)
            {
                Core.Log.Msg("[Viz] parking: " + Spots.Count + " free spot(s) shown" + (near ? ", target " + planned.Label : "") + ".");
                _spotsShown = shown;
            }
        }

        /// <summary>
        /// A spot's centre on the asphalt. The spot's own centre is where the car's pivot goes, well above the ground,
        /// and paint drawn there covers the lower half of the car standing in it.
        /// </summary>
        private static Vector3 OnFloor(LandVehicle car, ParkTarget s)
        {
            Vector3 c = s.Centre;
            c.y = Parking.Ground(car, c);
            return c;
        }

        /// <summary>A bay's lines around <paramref name="c"/> on the floor; the side towards the aisle stays open unless <paramref name="closed"/>.</summary>
        private static void Outline(List<Vector3> v, List<int> t, Vector3 c, Vector3 h, float halfW, float halfL, bool closed)
        {
            Vector3 r = Vector3.Cross(Vector3.up, h);
            Vector3 back = c - h * halfL, front = c + h * halfL;
            VizRoads.Quad(v, t, back - r * halfW, front - r * halfW, r, 0f, SpotLine, 0.03f);
            VizRoads.Quad(v, t, back + r * halfW, front + r * halfW, r, 0f, SpotLine, 0.03f);
            VizRoads.Quad(v, t, front - r * (halfW + SpotLine * 0.5f), front + r * (halfW + SpotLine * 0.5f), h, 0f, SpotLine, 0.03f);
            if (closed) VizRoads.Quad(v, t, back - r * (halfW + SpotLine * 0.5f), back + r * (halfW + SpotLine * 0.5f), h, 0f, SpotLine, 0.03f);
        }

        private static TextMeshPro SpotLabel()
        {
            var go = new GameObject("TargetSpotLabel") { layer = Layer };
            go.transform.SetParent(_root.transform, false);
            var tmp = go.AddComponent<TextMeshPro>();
            TMP_FontAsset font = VizOverlay.FindFont();
            if (font != null) tmp.font = font;
            else Core.Log.Warning("[Viz] no font for the parking 'P' - TextMeshPro's default draws it.");
            tmp.enableAutoSizing = false;
            tmp.fontSize = 16f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Midline;
            tmp.enableWordWrapping = false;
            tmp.color = Color.white;
            tmp.rectTransform.sizeDelta = new Vector2(3f, 3f);
            tmp.text = "P";
            go.SetActive(false);
            return tmp;
        }

        private static void AimCamera(LandVehicle car)
        {
            Vector3 fwd = Flat(car.transform.forward);
            if (fwd.sqrMagnitude < 0.01f) fwd = _heading.sqrMagnitude > 0.01f ? _heading : Vector3.forward;
            fwd.Normalize();
            // About 0.4 s to follow a turn at 15 renders a second, so the car visibly yaws before the view catches up.
            _heading = _heading.sqrMagnitude < 0.01f ? fwd : Vector3.Slerp(_heading, fwd, 0.18f).normalized;

            bool turnSoon = (Info.Next == NextStep.Left || Info.Next == NextStep.Right) && Info.NextMetres < WideBeforeTurn;
            float target = turnSoon || Info.SpeedKmh > WideAboveKmh ? 1f : 0f;
            _zoom += (target - _zoom) * 0.08f;
            Vector3 c = Vector3.Lerp(CloseCam, WideCam, Mathf.SmoothStep(0f, 1f, _zoom));

            Vector3 at = car.transform.position + Origin;
            _cam.transform.position = at - _heading * c.x + Vector3.up * c.y;
            _cam.transform.rotation = Quaternion.LookRotation(at + _heading * c.z - _cam.transform.position, Vector3.up);
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
            int n = 0, lead = -1;
            float leadAlong = float.MaxValue;
            if (NetworkSingleton<VehicleManager>.InstanceExists)
            {
                var all = NetworkSingleton<VehicleManager>.Instance.AllVehicles;
                for (int i = 0; i < all.Count; i++)
                {
                    LandVehicle v = all[i];
                    if (v == null || v.Pointer == own.Pointer || !v.gameObject.activeInHierarchy || v.boundingBox == null) continue;
                    if (Flat(v.transform.position - at).sqrMagnitude > Range * Range) continue;
                    if (n == Cars.Count) Cars.Add(CarProxy("Car" + n, _carMat, _carDarkMat));
                    Transform p = Cars[n];
                    p.gameObject.SetActive(true);
                    BoxCollider bb = v.boundingBox;
                    Vector3 size = Vector3.Scale(bb.size, bb.transform.lossyScale);
                    Vector3 centre = bb.transform.TransformPoint(bb.center);
                    p.position = centre + Origin;
                    p.rotation = bb.transform.rotation;
                    p.localScale = new Vector3(size.x / _carBounds.size.x, size.y / _carBounds.size.y, size.z / _carBounds.size.z);
                    // The car the Yesla follows: the nearest one on the path ahead, as Tesla marks it.
                    if (Info.Driving && Route.Along(centre, out float along, out float side) && side < 1.8f && along < leadAlong) { lead = n; leadAlong = along; }
                    n++;
                }
            }
            for (int i = 0; i < n; i++) Cars[i].GetChild(0).GetComponent<MeshRenderer>().sharedMaterials = i == lead ? _leadMats : _carMats;
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
                n = PlacePerson(n, npc.transform, at);
            }
            var players = Player.PlayerList;
            for (int i = 0; players != null && i < players.Count; i++)
            {
                Player p = players[i];
                if (p == null || p.CurrentVehicle != null || !p.gameObject.activeInHierarchy) continue;
                n = PlacePerson(n, p.transform, at);
            }
            for (int i = n; i < People.Count; i++) People[i].gameObject.SetActive(false);
            Info.People = n;
        }

        private static int PlacePerson(int n, Transform who, Vector3 at)
        {
            Vector3 p = who.position;
            if (Flat(p - at).sqrMagnitude > Range * Range) return n;
            if (n == People.Count) People.Add(Proxy("Person" + n, _figure, _personMat, Vector3.one));
            Transform t = People[n];
            t.gameObject.SetActive(true);
            t.position = p + Origin;
            Vector3 f = Flat(who.forward);
            t.rotation = f.sqrMagnitude > 0.01f ? Quaternion.LookRotation(f, Vector3.up) : Quaternion.identity;
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
                // A junction has lights along both roads. The game switches the two directions of one road together
                // (an intersection has two light groups), so the ones along the car's road show its phase, whichever
                // way each one points; the cross road's lights are left out.
                Vector3 facing = Facing(l);
                if (facing != Vector3.zero && Mathf.Abs(Vector3.Dot(facing, fwd)) < 0.5f) continue;
                if (n == Lights.Count) Lights.Add(Signal("Light" + n));
                SignalProxy s = Lights[n++];
                s.Root.gameObject.SetActive(true);
                // Lifted to above the car roof: the real lamp hangs over the junction and would sit near the top edge.
                s.Root.position = new Vector3(p.x, at.y + 3.2f, p.z) + Origin;
                s.Root.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                TrafficLight.State st = l.CurrentState;
                s.Red.sharedMaterial = st == TrafficLight.State.Red ? _redMat : _lampOffMat;
                s.Amber.sharedMaterial = st == TrafficLight.State.Orange ? _orangeMat : _lampOffMat;
                s.Green.sharedMaterial = st == TrafficLight.State.Green ? _greenMat : _lampOffMat;
            }
            for (int i = n; i < Lights.Count; i++) Lights[i].Root.gameObject.SetActive(false);
        }

        /// <summary>
        /// The axis a traffic light points along, flat: its forward axis, which lies along the road it controls. The
        /// lamps sit straight above the pivot and give no direction. Zero for a light whose forward axis points up,
        /// which then counts as being on the car's road.
        /// </summary>
        private static Vector3 Facing(TrafficLight l)
        {
            Vector3 f = Flat(l.transform.forward);
            return f.sqrMagnitude < 0.01f ? Vector3.zero : f.normalized;
        }

        /// <summary>Every traffic light near the car with its facing data, for the 'viz lights' console check.</summary>
        internal static string DescribeLights(LandVehicle car)
        {
            var sb = new System.Text.StringBuilder();
            Vector3 at = car.transform.position, fwd = Flat(car.transform.forward).normalized;
            var near = new List<TrafficLight>();
            foreach (TrafficLight l in UnityEngine.Object.FindObjectsOfType<TrafficLight>())
                if (l != null && Flat(l.transform.position - at).magnitude < 60f) near.Add(l);
            sb.Append(near.Count + " light(s) within 60 m, car forward " + fwd.ToString("F2"));
            foreach (TrafficLight l in near)
            {
                var lamp = l._redLight;
                Vector3 off = lamp != null ? lamp.transform.position - l.transform.position : Vector3.zero;
                sb.Append("\n  " + l.name + " at " + Flat(l.transform.position - at).ToString("F1") + " state " + l.CurrentState
                          + " fwd " + l.transform.forward.ToString("F2") + " lampOff " + off.ToString("F2")
                          + " lampFwd " + (lamp != null ? lamp.transform.forward.ToString("F2") : "-")
                          + " facing.dot(fwd) " + Vector3.Dot(Facing(l), fwd).ToString("F2"));
            }
            return sb.ToString();
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

        /// <summary>The real car's paint renderer, so the stand-in wears the same colour.</summary>
        private static MeshRenderer PaintRenderer(LandVehicle car)
        {
            Transform model = FindDeep(car.transform, ModelName);
            Transform lod = model != null ? FindDeep(model, "Body_LOD0") : null;
            if (lod != null)
                foreach (MeshRenderer r in lod.GetComponentsInChildren<MeshRenderer>(true))
                    if (Vehicle.GlbModel.InGroup(r.gameObject.name, "Paint")) return r;
            Core.Log.Warning("[Viz] no paint renderer on " + car.name + " - the own car stays white.");
            return null;
        }

        private static void UpdatePaint()
        {
            if (_paint == null || _paint.sharedMaterial == null) return;
            Color c = _paint.sharedMaterial.HasProperty("_BaseColor") ? _paint.sharedMaterial.GetColor("_BaseColor") : OwnFallback;
            // A little lift so black and dark blue paint still read against the dark road.
            c = Color.Lerp(c, new Color(0.55f, 0.56f, 0.6f), 0.15f);
            c.a = 1f;
            if (_ownMat.GetColor("_BaseColor") != c) _ownMat.SetColor("_BaseColor", c);
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
        /// The Yesla's low-poly stand-in (Body_Viz, built by YeslaBody, never drawn in the world) merged into one mesh
        /// in car space: submesh 0 the body, submesh 1 glass, tyres and black trim. Every car is drawn with it: the own
        /// car at scale one, other cars stretched to their bounding box.
        /// </summary>
        private static Mesh CarMesh(LandVehicle car, out Bounds bounds)
        {
            bounds = default;
            Transform model = FindDeep(car.transform, ModelName);
            if (model == null) { Core.Log.Error("[Viz] the car has no " + ModelName + " - no visualisation."); return null; }
            Transform viz = FindDeep(model, VizName);
            if (viz == null) { Core.Log.Error("[Viz] the Yesla has no " + VizName + " - no visualisation."); return null; }

            // A CombineInstance takes one submesh, so each part's submeshes go in one by one.
            var body = new List<CombineInstance>();
            var dark = new List<CombineInstance>();
            Matrix4x4 toCar = car.transform.worldToLocalMatrix;
            var filters = viz.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh m = filters[i].sharedMesh;
                if (m == null) { Core.Log.Warning("[Viz] " + VizName + " part " + filters[i].name + " has no mesh - skipped."); continue; }
                string mat = filters[i].gameObject.name;
                bool isDark = Vehicle.GlbModel.InGroup(mat, "Glass") || Vehicle.GlbModel.InGroup(mat, "Tyre_Rubber") || Vehicle.GlbModel.InGroup(mat, "Trim_Black");
                for (int s = 0; s < m.subMeshCount; s++)
                    (isDark ? dark : body).Add(new CombineInstance { mesh = m, subMeshIndex = s, transform = toCar * filters[i].transform.localToWorldMatrix });
            }
            if (body.Count == 0 || dark.Count == 0) { Core.Log.Error("[Viz] " + VizName + " is incomplete (" + body.Count + " body, " + dark.Count + " dark part(s)) - no visualisation."); return null; }

            Mesh bodyMesh = Merge("YeslaViz_CarBody", body);
            Mesh darkMesh = Merge("YeslaViz_CarDark", dark);
            var both = new Il2CppStructArray<CombineInstance>(2);
            both[0] = new CombineInstance { mesh = bodyMesh, subMeshIndex = 0, transform = Matrix4x4.identity };
            both[1] = new CombineInstance { mesh = darkMesh, subMeshIndex = 0, transform = Matrix4x4.identity };
            var mesh = new Mesh { name = "YeslaViz_Car", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(both, false, true);
            UnityEngine.Object.Destroy(bodyMesh);
            UnityEngine.Object.Destroy(darkMesh);
            mesh.RecalculateBounds();
            mesh.hideFlags = HideFlags.DontSave;
            bounds = mesh.bounds;
            Core.Log.Msg("[Viz] car mesh from " + VizName + ": " + filters.Length + " part(s), " + body.Count + " body / " + dark.Count + " dark submesh(es), "
                       + mesh.vertexCount + " vertices, " + (mesh.triangles.Length / 3) + " triangles, size " + bounds.size.ToString("F2") + ".");
            return mesh;
        }

        private static Mesh Merge(string name, List<CombineInstance> parts)
        {
            var combine = new Il2CppStructArray<CombineInstance>(parts.Count);
            for (int i = 0; i < parts.Count; i++) combine[i] = parts[i];
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(combine, true, true);
            return mesh;
        }

        /// <summary>A stand-in car: own car child at the origin, other cars' child centred so the box scale fits.</summary>
        private static Transform CarProxy(string name, Material body, Material dark)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(_root.transform, false);
            var child = new GameObject("Body") { layer = Layer };
            child.transform.SetParent(go.transform, false);
            child.transform.localPosition = -_carBounds.center;
            child.AddComponent<MeshFilter>().sharedMesh = _carMesh;
            var r = child.AddComponent<MeshRenderer>();
            var mats = new Il2CppReferenceArray<Material>(2);
            mats[0] = body;
            mats[1] = dark;
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            Blob(child.transform, "Shadow", _shadowMat, 1.2f, 1.1f, 0.03f);
            return go.transform;
        }

        /// <summary>A soft round patch on the ground under a car, in the car mesh's space: its shadow or its glow.</summary>
        private static GameObject Blob(Transform body, string name, Material mat, float widen, float lengthen, float lift)
        {
            var go = new GameObject(name) { layer = Layer };
            go.transform.SetParent(body, false);
            go.transform.localPosition = new Vector3(_carBounds.center.x, _carBounds.min.y + lift, _carBounds.center.z);
            go.transform.localScale = new Vector3(_carBounds.size.x * widen, 1f, _carBounds.size.z * lengthen);
            go.AddComponent<MeshFilter>().sharedMesh = _flat;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        private static Il2CppReferenceArray<Material> Pair(Material a, Material b)
        {
            var m = new Il2CppReferenceArray<Material>(2);
            m[0] = a;
            m[1] = b;
            return m;
        }

        private static SignalProxy Signal(string name)
        {
            Transform root = Proxy(name, _cube, _housingMat, Vector3.one);
            root.localScale = new Vector3(0.42f, 1.1f, 0.3f);
            var s = new SignalProxy { Root = root };
            s.Red = Lamp(root, 0.32f);
            s.Amber = Lamp(root, 0f);
            s.Green = Lamp(root, -0.32f);
            return s;
        }

        /// <summary>One lamp on the housing's front face (towards -Z, which faces the camera).</summary>
        private static MeshRenderer Lamp(Transform housing, float y)
        {
            var go = new GameObject("Lamp") { layer = Layer };
            go.transform.SetParent(housing, false);
            go.transform.localPosition = new Vector3(0f, y, -0.5f);
            go.transform.localScale = new Vector3(0.7f, 0.26f, 0.5f);
            go.AddComponent<MeshFilter>().sharedMesh = _sphere;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _lampOffMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        /// <summary>A faceless grey figure, feet at the origin: legs, body and head, about 1.75 m tall.</summary>
        private static Mesh FigureMesh()
        {
            var parts = new Il2CppStructArray<CombineInstance>(4);
            parts[0] = new CombineInstance { mesh = _capsule, transform = Matrix4x4.TRS(new Vector3(0f, 1.12f, 0f), Quaternion.identity, new Vector3(0.44f, 0.34f, 0.28f)) };
            parts[1] = new CombineInstance { mesh = _sphere, transform = Matrix4x4.TRS(new Vector3(0f, 1.6f, 0f), Quaternion.identity, new Vector3(0.24f, 0.26f, 0.24f)) };
            parts[2] = new CombineInstance { mesh = _capsule, transform = Matrix4x4.TRS(new Vector3(0.1f, 0.43f, 0f), Quaternion.identity, new Vector3(0.16f, 0.43f, 0.16f)) };
            parts[3] = new CombineInstance { mesh = _capsule, transform = Matrix4x4.TRS(new Vector3(-0.1f, 0.43f, 0f), Quaternion.identity, new Vector3(0.16f, 0.43f, 0.16f)) };
            var m = new Mesh { name = "YeslaViz_Figure" };
            m.CombineMeshes(parts, true, true);
            m.RecalculateBounds();
            m.hideFlags = HideFlags.DontSave;
            return m;
        }

        /// <summary>A 1 x 1 quad lying flat, facing up, with UVs.</summary>
        private static Mesh FlatQuad()
        {
            var m = new Mesh { name = "YeslaViz_Flat" };
            m.vertices = new[] { new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateBounds();
            m.hideFlags = HideFlags.DontSave;
            return m;
        }

        /// <summary>
        /// The horizon fade: a sheet just in front of the camera, clear over the near road and turning into the
        /// horizon colour over the top of the picture, where the road is 50 to 90 m away. Far road ends and late
        /// arrivals melt into it instead of ending hard.
        /// </summary>
        private static void Fade(Transform cam)
        {
            const float depth = 1.5f;
            float h = depth * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
            float w = h * Width / Height;
            var go = new GameObject("YeslaViz_Fade") { layer = Layer };
            go.transform.SetParent(cam, false);
            go.transform.localPosition = new Vector3(0f, 0f, depth);
            var m = new Mesh { name = "YeslaViz_FadeSheet" };
            m.vertices = new[] { new Vector3(-w * 1.05f, -h * 1.05f, 0f), new Vector3(w * 1.05f, -h * 1.05f, 0f), new Vector3(-w * 1.05f, h * 1.05f, 0f), new Vector3(w * 1.05f, h * 1.05f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateBounds();
            m.hideFlags = HideFlags.DontSave;
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            Material mat = Fading(Horizon, Gradient("YeslaViz_Horizon", 4, 128, false, 0.74f, 0.93f, true));
            mat.renderQueue = 2990;
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>URP/Unlit, see-through, its alpha taken from <paramref name="tex"/> times the colour's alpha.</summary>
        private static Material Fading(Color c, Texture2D tex)
        {
            var m = new Material(_unlit);
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Cull", 0f);
            Vehicle.GlbModel.MakeTransparent(m);
            // Keep the texture's alpha at 1: the on-screen picture is drawn with it, and a see-through layer that
            // wrote its own alpha would let the game show through wherever it lies.
            m.SetFloat("_SrcBlendAlpha", 0f);   // Zero
            m.SetFloat("_DstBlendAlpha", 1f);   // One
            m.hideFlags = HideFlags.DontSave;
            return m;
        }

        /// <summary>
        /// A white texture whose alpha falls off: radially from the centre (<paramref name="radial"/>), or along V,
        /// opaque until <paramref name="from"/> and clear at <paramref name="to"/>; <paramref name="rising"/> turns it
        /// round, clear until <paramref name="from"/> and opaque from <paramref name="to"/>.
        /// </summary>
        private static Texture2D Gradient(string name, int w, int h, bool radial, float from, float to, bool rising = false)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.hideFlags = HideFlags.DontSave;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float a;
                    if (radial)
                    {
                        float dx = (x + 0.5f) / w * 2f - 1f, dy = (y + 0.5f) / h * 2f - 1f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        a = Mathf.Pow(Mathf.Clamp01(1f - r), 1.4f);
                    }
                    else
                    {
                        float v = (y + 0.5f) / h;
                        float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, to, v));
                        a = rising ? k : 1f - k;
                    }
                    px[y * w + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            Textures.Add(t);
            return t;
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

        /// <summary>
        /// Buildings: lit, so their walls and roofs read as shapes, and see-through, so a car, a person or the route
        /// behind one still shows, as on Tesla's display. Drawn after the route and the ground overlays.
        /// </summary>
        private static Material SeeThrough(Color c)
        {
            Material m = Shaded(c, 0.05f);
            Vehicle.GlbModel.MakeTransparent(m);
            // The texture's alpha stays 1 for the same reason as in Fading.
            m.SetFloat("_SrcBlendAlpha", 0f);
            m.SetFloat("_DstBlendAlpha", 1f);
            m.renderQueue = 2950;
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

        /// <summary>
        /// A see-through layer on the ground. It goes into the transparent queue because only there does the
        /// renderer blend it; in the opaque queue it came out as a solid square. Drawn after the cars, it is hidden
        /// under them by the depth test.
        /// </summary>
        private static Material Overlay(Material m, int order)
        {
            m.SetFloat("_ZWrite", 0f);
            m.renderQueue = 2900 + order;
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
            VizBuildings.Reset();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (Texture != null) { Texture.Release(); UnityEngine.Object.Destroy(Texture); }
            if (_carMesh != null) UnityEngine.Object.Destroy(_carMesh);
            if (_figure != null) UnityEngine.Object.Destroy(_figure);
            foreach (Texture2D t in Textures) if (t != null) UnityEngine.Object.Destroy(t);
            Textures.Clear();
            _carMesh = null; _figure = null;
            _paint = null; _glow = null;
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
