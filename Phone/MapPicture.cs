using Sideload.Api;
using UnityEngine.UI;

namespace Yesla.Phone
{
    /// <summary>
    /// Hands the vanilla map picture to the page as <c>s1://yesla-map</c>, once per session and again whenever the map
    /// app's picture changes, as when a map expansion draws a larger map.
    ///
    /// Pictures the map app lays over its background (a map expansion's sheets at their own resolution) are handed
    /// over as well, each as <c>s1://yesla-map/sheet-N</c> with its place on the background, so the page shows the
    /// map as sharp as the map app does.
    ///
    /// The picture is read off the live MapApp because which sprite is current depends on the tutorial. Its texture
    /// is not CPU-readable, so it goes through a RenderTexture first; GetPixels on it directly would throw.
    /// </summary>
    internal static class MapPicture
    {
        internal const string ImageName = "yesla-map";

        /// <summary>A picture over the background, placed in 0..1 of the background from its top left.</summary>
        internal sealed class Sheet
        {
            public string Image;
            public double X, Y, W, H;
            /// <summary>Clockwise degrees, as css rotates.</summary>
            public double Rotation;
        }

        /// <summary>Smallest picture taken for a sheet, in pixels; smaller ones are icons, not map.</summary>
        private const int MinSheetPixels = 256;

        private static bool _done;
        private static bool _failed;
        private static IntPtr _published;
        private static readonly List<Sheet> _sheets = new();

        internal static bool Ready => _done;

        /// <summary>Counts the publications, so the page reads the state again after each.</summary>
        internal static int Revision { get; private set; }

        internal static IReadOnlyList<Sheet> Sheets => _sheets;

        internal static void Ensure(AppHandle app)
        {
            if (_failed || app == null) return;

            var mapApp = PlayerSingleton<Il2CppScheduleOne.UI.Phone.Map.MapApp>.Instance;
            // No MapApp yet means the phone has not built itself; ask again on the next tick.
            if (mapApp == null || mapApp.MainMapSprite == null || mapApp.MainMapSprite.texture == null) return;
            IntPtr texture = mapApp.MainMapSprite.texture.Pointer;
            if (_done && texture == _published) return;
            if (_done) Core.Log.Msg("[Map] the map app's picture changed; it is published again.");

            byte[] png = EncodePng(mapApp.MainMapSprite);
            if (png == null || png.Length == 0)
            {
                _failed = true;
                Core.Log.Warning("[Map] the map picture could not be read - the Yesla app draws a plain backdrop.");
                return;
            }

            app.Image(ImageName, png);
            _done = true;
            _published = texture;
            Core.Log.Msg("[Map] map picture published (" + (png.Length / 1024) + " KB).");
            PublishSheets(app, mapApp.BackgroundImage);
            Revision++;
        }

        internal static void Reset()
        {
            _done = false;
            _failed = false;
            _published = IntPtr.Zero;
            _sheets.Clear();
        }

        private static void PublishSheets(AppHandle app, Image background)
        {
            foreach (var old in _sheets) app.Image(old.Image, null);
            _sheets.Clear();
            if (background == null)
            {
                Core.Log.Msg("[Map] the map app has no background image; no sheets to publish.");
                return;
            }

            var bg = background.rectTransform;
            Rect area = bg.rect;
            if (area.width <= 0f || area.height <= 0f)
            {
                Core.Log.Warning("[Map] the map app's background has no size; its sheets are not published.");
                return;
            }
            for (int i = 0; i < bg.childCount; i++)
            {
                var child = bg.GetChild(i);
                var image = child.GetComponent<Image>();
                // The map app is usually closed here, so its images are not active in the hierarchy; their own switches count.
                if (image == null || !image.enabled || !child.gameObject.activeSelf || image.sprite == null || image.sprite.texture == null) continue;
                var sprite = image.sprite;
                if (sprite.textureRect.width < MinSheetPixels || sprite.textureRect.height < MinSheetPixels) continue;

                var rt = image.rectTransform;
                Vector3 middle = bg.InverseTransformPoint(rt.TransformPoint(rt.rect.center));
                Vector3 scale = rt.localScale;
                float turn = (Quaternion.Inverse(bg.rotation) * rt.rotation).eulerAngles.z;
                var sheet = new Sheet
                {
                    Image = ImageName + "/sheet-" + _sheets.Count,
                    X = (middle.x - area.xMin) / area.width,
                    Y = (area.yMax - middle.y) / area.height,
                    W = rt.rect.width * scale.x / area.width,
                    H = rt.rect.height * scale.y / area.height,
                    Rotation = -turn,
                };

                byte[] png = EncodePng(sprite);
                if (png == null || png.Length == 0)
                {
                    Core.Log.Warning("[Map] sheet '" + child.name + "' could not be read; the page shows the background there.");
                    continue;
                }
                app.Image(sheet.Image, png);
                _sheets.Add(sheet);
                Core.Log.Msg("[Map] sheet '" + child.name + "' published as " + sheet.Image + " (" + (png.Length / 1024) + " KB, "
                             + (int)sprite.textureRect.width + " px) at " + sheet.X.ToString("F3") + ", " + sheet.Y.ToString("F3")
                             + ", " + sheet.W.ToString("F3") + " wide, turned " + sheet.Rotation.ToString("F0") + " deg.");
            }
            Core.Log.Msg("[Map] " + _sheets.Count + " of " + bg.childCount + " pictures over the map's background published as sheets.");
        }

        private static byte[] EncodePng(Sprite sprite)
        {
            Texture source = sprite.texture;
            Rect r = sprite.textureRect;
            int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;

                readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(Mathf.Round(r.x), Mathf.Round(r.y), w, h), 0, 0);
                readable.Apply(false, false);
                return ImageConversion.EncodeToPNG(readable);
            }
            catch (Exception e)
            {
                Core.Log.Error("[Map] encoding the map picture failed: " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }
    }
}
