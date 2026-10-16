using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.Modification;
using Sideload.Api;

namespace Yesla.Phone
{
    /// <summary>
    /// Studio photos of the player's own Yesla in its real paint, for the phone app, published as
    /// <c>s1://yesla-car-photo-&lt;set&gt;-&lt;view&gt;</c>. <see cref="Views"/> photos around the car ship with the mod
    /// twice, once rendered with white paint and once with black (Debug/CarBake.cs). Light on paint is linear in the
    /// paint colour, so any paint is <c>black + colour * (white - black)</c>, worked out per pixel in linear light;
    /// everything that is not paint comes out exactly as rendered.
    ///
    /// The mixing and the PNG encoding run on worker threads in plain C#, the view the player's camera needs first.
    /// The page stacks the views and shows the one nearest the camera, which is a repaint rather than a page rebuild.
    /// CarPicture's single drawn picture stays the fallback when the photos cannot be read.
    /// </summary>
    internal static class CarPhoto
    {
        // Twice the 276 x 190 css box, so the phone's own scaling has detail to work with.
        internal const int Width = 552, Height = 380;
        /// <summary>Photos around the car, one every 15 degrees.</summary>
        internal const int Views = 24;
        internal const float ViewStep = 360f / Views;
        // Front left, for a camera that sits inside the car.
        private const int DefaultView = 21;
        // A camera closer than this to the car's middle, in metres, is inside it.
        private const float InsideRadius = 2.5f;
        private const string ResourcePrefix = "Yesla.Assets.views.";

        /// <summary>sRGB byte to linear light.</summary>
        internal static readonly float[] ToLinear = MakeToLinear();
        /// <summary>Linear light to an sRGB byte, sampled finely enough that neighbouring entries differ by at most one step.</summary>
        internal const int GammaSteps = 4096;
        internal static readonly byte[] ToSrgb = MakeToSrgb();

        private sealed class Done
        {
            internal int Set, View;
            internal byte[] Png;
            internal string Error;
            internal double DecodeMs, MixMs, EncodeMs, ReadyAt; // ReadyAt: SetWatch time when the worker finished
        }

        private static readonly ConcurrentQueue<Done> Finished = new ConcurrentQueue<Done>();
        private static readonly object SourceLock = new object();
        private static byte[][] _white, _black;
        private static int _set;
        private static bool _failed;

        private static string _shownKey = "";
        private static string _current;
        private static string[] _views;
        private static string[] _pending;
        private static int _pendingLeft, _pendingFirst;
        private static readonly System.Diagnostics.Stopwatch SetWatch = new System.Diagnostics.Stopwatch();
        private static string _firstStages = string.Empty;
        private static double _firstMs;
        private static int _lastView = -1;

        /// <summary>The image name of the newest photo, or null when there is none.</summary>
        internal static string Current => _current;

        /// <summary>All <see cref="Views"/> photo names of the newest complete set, index = view; null until one is.</summary>
        internal static string[] ViewNames => _views;

        /// <summary>Changes whenever a new set of views is complete.</summary>
        internal static int Revision { get; private set; }

        /// <summary>
        /// Starts a new set of photos when the car or its paint changed, or when <paramref name="force"/> asks for one
        /// and there is no set yet. The photos arrive through <see cref="Step"/>. True when a set was started.
        /// </summary>
        internal static bool Update(AppHandle app, LandVehicle car, bool force)
        {
            if (app == null || _failed) return false;
            if (car == null || !car.gameObject.activeInHierarchy) return false;
            Color paint = PaintOf(car);
            string key = car.Pointer.ToInt64() + "|" + paint.r.ToString("F3") + "," + paint.g.ToString("F3") + "," + paint.b.ToString("F3");
            if (key == _shownKey && !(force && _views == null && _pending == null)) return false;
            _shownKey = key;

            // A set still being mixed for the old paint is dropped; its finished photos go with it.
            if (_pending != null) Drop(app, _pending, "the car or its paint changed");
            int set = Interlocked.Increment(ref _set);
            _pending = new string[Views];
            _pendingLeft = Views;
            int first = ViewOf(car);
            _pendingFirst = first;
            var order = new int[Views];
            order[0] = first;
            for (int i = 1, n = 1; n < Views; i++)
            {
                order[n++] = (first + i) % Views;
                if (n < Views) order[n++] = (first - i + Views) % Views;
            }
            Color linear = paint.linear;
            float r = linear.r, g = linear.g, b = linear.b;
            SetWatch.Restart();
            _firstMs = -1;
            Core.Log.Msg("[CarPhoto] mixing " + car.VehicleName + " in " + car.OwnedColor + " (linear " + r.ToString("F3") + ", " + g.ToString("F3") + ", " + b.ToString("F3")
                         + "), view " + first + " first.");
            Task.Run(() => Work(set, r, g, b, order));
            return true;
        }

        /// <summary>
        /// Publishes the photos the workers have finished. Call once a frame while the app is on screen. True when the
        /// picture on screen changed, so the page should hear of it now rather than at the next pulse.
        /// </summary>
        internal static bool Step(AppHandle app)
        {
            if (app == null) return false;
            bool shown = false;
            while (Finished.TryDequeue(out Done d))
            {
                if (d.Set != _set || _pending == null) continue;
                if (d.Error != null)
                {
                    Core.Log.Error("[CarPhoto] " + d.Error + " - the app keeps the drawn car picture.");
                    _failed = true;
                    Drop(app, _pending, null);
                    _pending = null;
                    return shown;
                }
                string name = "yesla-car-photo-" + d.Set + "-" + d.View;
                app.Image(name, d.Png);
                _pending[d.View] = name;
                if (d.View == _pendingFirst)
                {
                    _firstMs = SetWatch.Elapsed.TotalMilliseconds;
                    _firstStages = "decode " + d.DecodeMs.ToString("0") + ", mix " + d.MixMs.ToString("0") + ", encode " + d.EncodeMs.ToString("0")
                                 + ", worker done at " + d.ReadyAt.ToString("0") + ", " + (d.Png.Length / 1024) + " KB";
                    SetCurrent(app, name);
                    shown = true;
                }
                if (--_pendingLeft > 0) continue;

                string[] old = _views;
                _views = _pending;
                _pending = null;
                if (old != null) Drop(app, old, null);
                Revision++;
                Core.Log.Msg("[CarPhoto] " + Views + " views ready: first after " + _firstMs.ToString("0") + " ms, all after " + SetWatch.Elapsed.TotalMilliseconds.ToString("0") + " ms (first: "
                             + _firstStages + ").");
            }
            return shown;
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
            Interlocked.Increment(ref _set);
            while (Finished.TryDequeue(out _)) { }
            _shownKey = "";
            _current = null;
            _views = null;
            _pending = null;
            _lastView = -1;
        }

        /// <summary>The paint as the car shows it: its paint material's colour, or the paint library's for its paint name.</summary>
        private static Color PaintOf(LandVehicle car)
        {
            VehicleColor vc = car.Color;
            if (vc != null && vc.BodyMeshes != null && vc.BodyMeshes.Length > 0 && vc.BodyMeshes[0] != null && vc.BodyMeshes[0].Renderer != null)
            {
                var mats = vc.BodyMeshes[0].Renderer.sharedMaterials;
                int i = vc.BodyMeshes[0].MaterialIndex;
                if (mats != null && i >= 0 && i < mats.Length && mats[i] != null) return mats[i].color;
            }
            if (Singleton<VehicleColors>.InstanceExists)
            {
                var lib = Singleton<VehicleColors>.Instance.colorLibrary;
                for (int i = 0; i < lib.Count; i++)
                    if (lib[i] != null && lib[i].color == car.OwnedColor) return lib[i].MaterialColor;
            }
            return Color.white;
        }

        private static void SetCurrent(AppHandle app, string name)
        {
            string old = _current;
            _current = name;
            if (old == null || old == name) return;
            if (_views != null && System.Array.IndexOf(_views, old) >= 0) return;
            if (_pending != null && System.Array.IndexOf(_pending, old) >= 0) return;
            app.Image(old, null);
        }

        private static void Drop(AppHandle app, string[] names, string why)
        {
            if (why != null) Core.Log.Msg("[CarPhoto] set of views dropped: " + why + ".");
            foreach (string n in names)
                if (n != null && n != _current) app.Image(n, null);
        }

        // ---- worker threads: plain C# only, nothing that calls into the IL2CPP runtime ----

        private static void Work(int set, float r, float g, float b, int[] order)
        {
            try
            {
                LoadSources();
            }
            catch (System.Exception e)
            {
                Finished.Enqueue(new Done { Set = set, View = -1, Error = "reading the baked car photos failed: " + e.Message });
                return;
            }
            One(set, order[0], r, g, b, first: true);
            var options = new ParallelOptions { MaxDegreeOfParallelism = System.Math.Max(1, System.Environment.ProcessorCount / 2) };
            Parallel.For(1, order.Length, options, i => One(set, order[i], r, g, b));
        }

        /// <param name="first">The view on screen: both sources decode side by side and the encoder skips its filter
        /// search, so the new paint shows as soon as it can.</param>
        private static void One(int set, int view, float r, float g, float b, bool first = false)
        {
            if (Volatile.Read(ref _set) != set) return;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] w = null, k = null;
                int ww = 0, wh = 0, bw = 0, bh = 0;
                if (first)
                    Parallel.Invoke(() => w = Png.Decode(_white[view], out ww, out wh), () => k = Png.Decode(_black[view], out bw, out bh));
                else
                {
                    w = Png.Decode(_white[view], out ww, out wh);
                    k = Png.Decode(_black[view], out bw, out bh);
                }
                if (ww != Width || wh != Height || bw != Width || bh != Height)
                    throw new InvalidDataException("view " + view + " is " + ww + "x" + wh + " / " + bw + "x" + bh + ", not " + Width + "x" + Height);
                double decode = sw.Elapsed.TotalMilliseconds;
                byte[] mixed = Mix(w, k, r, g, b);
                double mix = sw.Elapsed.TotalMilliseconds;
                byte[] png = Png.Encode(mixed, Width, Height, false, System.IO.Compression.CompressionLevel.Fastest, quick: first);
                double encode = sw.Elapsed.TotalMilliseconds;
                Finished.Enqueue(new Done { Set = set, View = view, Png = png, DecodeMs = decode, MixMs = mix - decode, EncodeMs = encode - mix,
                                            ReadyAt = SetWatch.Elapsed.TotalMilliseconds });
            }
            catch (System.Exception e)
            {
                Finished.Enqueue(new Done { Set = set, View = view, Error = "mixing view " + view + " failed: " + e.Message });
            }
        }

        private static void LoadSources()
        {
            lock (SourceLock)
            {
                if (_white != null) return;
                var white = new byte[Views][];
                var black = new byte[Views][];
                Assembly asm = Assembly.GetExecutingAssembly();
                for (int v = 0; v < Views; v++)
                {
                    white[v] = Resource(asm, "w" + v.ToString("00") + ".png");
                    black[v] = Resource(asm, "b" + v.ToString("00") + ".png");
                }
                _black = black;
                _white = white;
            }
        }

        private static byte[] Resource(Assembly asm, string file)
        {
            using Stream s = asm.GetManifestResourceStream(ResourcePrefix + file);
            if (s == null) throw new FileNotFoundException(ResourcePrefix + file + " is not in the mod");
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>Per pixel and channel <c>black + paint * (white - black)</c> in linear light; the coverage is the white shot's.</summary>
        private static byte[] Mix(byte[] white, byte[] black, float r, float g, float b)
        {
            var o = new byte[white.Length];
            for (int i = 0; i < white.Length; i += 4)
            {
                byte a = white[i + 3];
                if (a == 0) continue;
                o[i] = Channel(black[i], white[i], r);
                o[i + 1] = Channel(black[i + 1], white[i + 1], g);
                o[i + 2] = Channel(black[i + 2], white[i + 2], b);
                o[i + 3] = a;
            }
            return o;
        }

        private static byte Channel(byte black, byte white, float paint)
        {
            if (black == white) return black;
            float lb = ToLinear[black];
            float v = lb + paint * (ToLinear[white] - lb);
            int i = (int)(v * GammaSteps + 0.5f);
            return ToSrgb[i < 0 ? 0 : i > GammaSteps ? GammaSteps : i];
        }

        private static float[] MakeToLinear()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double v = i / 255.0;
                t[i] = (float)(v <= 0.04045 ? v / 12.92 : System.Math.Pow((v + 0.055) / 1.055, 2.4));
            }
            return t;
        }

        private static byte[] MakeToSrgb()
        {
            var t = new byte[GammaSteps + 1];
            for (int i = 0; i <= GammaSteps; i++)
            {
                double v = i / (double)GammaSteps;
                double s = v <= 0.0031308 ? v * 12.92 : 1.055 * System.Math.Pow(v, 1 / 2.4) - 0.055;
                t[i] = (byte)System.Math.Clamp((int)System.Math.Round(s * 255.0), 0, 255);
            }
            return t;
        }
    }
}
