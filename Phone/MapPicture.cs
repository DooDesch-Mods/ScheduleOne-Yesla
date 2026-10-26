using Sideload.Api;

namespace Yesla.Phone
{
    /// <summary>
    /// Hands the vanilla map picture to the page as <c>s1://yesla-map</c>, once per session and again whenever the map
    /// app's picture changes, as when a map expansion draws a larger map.
    ///
    /// The picture is read off the live MapApp because which sprite is current depends on the tutorial. Its texture
    /// is not CPU-readable, so it goes through a RenderTexture first; GetPixels on it directly would throw.
    /// </summary>
    internal static class MapPicture
    {
        internal const string ImageName = "yesla-map";

        private static bool _done;
        private static bool _failed;
        private static IntPtr _published;

        internal static bool Ready => _done;

        internal static void Ensure(AppHandle app)
        {
            if (_failed || app == null) return;

            var mapApp = PlayerSingleton<Il2CppScheduleOne.UI.Phone.Map.MapApp>.Instance;
            // No MapApp yet means the phone has not built itself; ask again on the next tick.
            if (mapApp == null || mapApp.MainMapSprite == null || mapApp.MainMapSprite.texture == null) return;
            IntPtr texture = mapApp.MainMapSprite.texture.Pointer;
            if (_done && texture == _published) return;
            if (_done) Core.Log.Msg("[Map] the map app's picture changed; it is published again.");

            byte[] png = EncodePng(mapApp.MainMapSprite.texture);
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
        }

        internal static void Reset()
        {
            _done = false;
            _failed = false;
            _published = IntPtr.Zero;
        }

        private static byte[] EncodePng(Texture source)
        {
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;

                readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
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
