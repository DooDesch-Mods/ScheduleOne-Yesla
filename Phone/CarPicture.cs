using System.IO;
using System.Reflection;
using Il2CppScheduleOne.Vehicles.Modification;
using Sideload.Api;

namespace Yesla.Phone
{
    /// <summary>
    /// The car picture in the phone app, in the car's own paint, published as <c>s1://yesla-car-&lt;Paint&gt;</c>.
    ///
    /// Two renders of the model ship with the mod: one with white paint, one with black. Light on a painted surface
    /// is linear in the paint colour, so any paint is <c>black + colour * (white - black)</c>, worked out in linear
    /// light. That keeps the reflections and the parts that are not paint exactly as rendered. Each paint is
    /// worked out the first time the app shows it and kept for the session.
    /// </summary>
    internal static class CarPicture
    {
        private const string WhiteResource = "Yesla.Assets.car_w.png";
        private const string BlackResource = "Yesla.Assets.car_b.png";

        private static Color32[] _white, _black;
        private static int _w, _h;
        private static bool _failed;
        private static readonly HashSet<string> Published = new HashSet<string>();
        private static float[] _toLinear;
        private static byte[] _toSrgb;

        internal static string NameFor(EVehicleColor paint) => "yesla-car-" + paint;

        /// <summary>The image name for this paint, publishing it first if needed; null when there is no picture.</summary>
        internal static string Ensure(AppHandle app, EVehicleColor paint)
        {
            if (app == null || _failed) return null;
            string name = NameFor(paint);
            if (Published.Contains(name)) return name;
            if (_white == null && !Load()) return null;

            Color colour = Color.white;
            if (paint != EVehicleColor.Custom && Singleton<VehicleColors>.InstanceExists)
            {
                var lib = Singleton<VehicleColors>.Instance.colorLibrary;
                for (int i = 0; i < lib.Count; i++)
                    if (lib[i] != null && lib[i].color == paint) { colour = lib[i].MaterialColor; break; }
            }

            byte[] png = Compose(colour.linear);
            if (png == null) return null;
            app.Image(name, png);
            Published.Add(name);
            Core.Log.Msg("[CarPicture] " + paint + " published (" + (png.Length / 1024) + " KB).");
            return name;
        }

        private static bool Load()
        {
            try
            {
                Texture2D w = Decode(WhiteResource), b = Decode(BlackResource);
                if (w == null || b == null) { _failed = true; return false; }
                if (w.width != b.width || w.height != b.height)
                {
                    Core.Log.Error("[CarPicture] the two renders differ in size (" + w.width + "x" + w.height + " / " + b.width + "x" + b.height + ") - the app shows no car.");
                    _failed = true;
                    return false;
                }
                _w = w.width;
                _h = w.height;
                _white = w.GetPixels32();
                _black = b.GetPixels32();
                UnityEngine.Object.Destroy(w);
                UnityEngine.Object.Destroy(b);

                _toLinear = new float[256];
                for (int i = 0; i < 256; i++)
                {
                    float v = i / 255f;
                    _toLinear[i] = v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
                }
                _toSrgb = new byte[4096];
                for (int i = 0; i < 4096; i++)
                {
                    float v = i / 4095f;
                    float s = v <= 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f;
                    _toSrgb[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(s * 255f), 0, 255);
                }
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Error("[CarPicture] reading the renders failed - the app shows no car: " + e.Message);
                _failed = true;
                return false;
            }
        }

        private static Texture2D Decode(string resource)
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (s == null) { Core.Log.Error("[CarPicture] " + resource + " is not in the mod - the app shows no car."); return null; }
            var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes, false))
            {
                Core.Log.Error("[CarPicture] " + resource + " did not decode - the app shows no car.");
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            return tex;
        }

        private static byte[] Compose(Color linear)
        {
            Texture2D tex = null;
            try
            {
                var px = new Color32[_white.Length];
                for (int i = 0; i < px.Length; i++)
                {
                    Color32 w = _white[i], b = _black[i];
                    if (w.r == b.r && w.g == b.g && w.b == b.b) { px[i] = w; continue; }
                    px[i] = new Color32(Mix(b.r, w.r, linear.r), Mix(b.g, w.g, linear.g), Mix(b.b, w.b, linear.b), w.a);
                }
                tex = new Texture2D(_w, _h, TextureFormat.RGBA32, false);
                tex.SetPixels32(px);
                tex.Apply(false, false);
                return ImageConversion.EncodeToPNG(tex);
            }
            catch (Exception e)
            {
                Core.Log.Error("[CarPicture] painting the picture failed: " + e.Message);
                return null;
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        private static byte Mix(byte black, byte white, float paint)
        {
            float lb = _toLinear[black];
            float v = lb + paint * (_toLinear[white] - lb);
            int i = (int)(v * 4095f + 0.5f);
            return _toSrgb[i < 0 ? 0 : (i > 4095 ? 4095 : i)];
        }

        internal static void Reset() => Published.Clear();
    }
}
