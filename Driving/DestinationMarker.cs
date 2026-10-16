using Il2CppScheduleOne.UI.Compass;
using UnityEngine.UI;

namespace Yesla.Driving
{
    /// <summary>
    /// Shows the Assisted Driving destination on the vanilla compass strip, with the distance label the compass
    /// draws for every element, for as long as a destination is set and not yet reached.
    ///
    /// The compass tracks a Transform, so the destination gets an empty GameObject of its own. The element content
    /// is a small icon built once per scene from an embedded PNG; quest markers use their quest's prefab there,
    /// and the Yesla has none.
    /// </summary>
    internal static class DestinationMarker
    {
        private const string IconResource = "Yesla.Assets.compass.png";

        private static GameObject _target;
        private static CompassManager.Element _element;
        private static RectTransform _template;
        private static bool _templateFailed;
        private static Vector3 _shownAt;

        internal static void Tick()
        {
#if SNITCH
            using var __perf = Snitch.Api.Profiler.Sample("Yesla.DestinationMarker.Tick");
#endif
            bool want = Autopilot.Destination.HasValue && Autopilot.State != DriveState.Arrived && Autopilot.State != DriveState.Off;
            if (!want) { Hide(); return; }

            Vector3 dest = Autopilot.Destination.Value;
            if (_element != null && _target != null)
            {
                if (_shownAt != dest) { _target.transform.position = dest; _shownAt = dest; }
                return;
            }
            Show(dest);
        }

        private static void Show(Vector3 dest)
        {
            if (!Singleton<CompassManager>.InstanceExists) return;
            RectTransform content = Template();
            if (content == null) return;

            try
            {
                if (_target == null) _target = new GameObject("YeslaDestination");
                _target.transform.position = dest;
                _shownAt = dest;
                _element = Singleton<CompassManager>.Instance.AddElement(_target.transform, content, true);
                Core.Log.Msg("[Marker] destination on the compass at " + dest.ToString("F0") + ".");
            }
            catch (Exception e)
            {
                _element = null;
                Core.Log.Error("[Marker] adding the destination to the compass failed: " + e.Message);
            }
        }

        private static void Hide()
        {
            if (_element == null && _target == null) return;
            try
            {
                if (_element != null && Singleton<CompassManager>.InstanceExists)
                    Singleton<CompassManager>.Instance.RemoveElement(_element, true);
                Core.Log.Msg("[Marker] destination removed from the compass.");
            }
            catch (Exception e) { Core.Log.Warning("[Marker] removing the compass element failed: " + e.Message); }
            _element = null;
            if (_target != null) UnityEngine.Object.Destroy(_target);
            _target = null;
        }

        /// <summary>Scene teardown destroys the compass and our objects with it; only the references are dropped.</summary>
        internal static void Reset()
        {
            _element = null;
            _target = null;
            _template = null;
            _templateFailed = false;
        }

        private static RectTransform Template()
        {
            if (_template != null || _templateFailed) return _template;
            try
            {
                Sprite sprite = LoadSprite();
                // The template lives under an inactive holder so it is never drawn itself; clones made from it are
                // parented to the compass and come out active.
                var holder = new GameObject("YeslaCompassTemplates");
                holder.SetActive(false);
                var go = new GameObject("YeslaCompassIcon");
                go.transform.SetParent(holder.transform, false);
                var rect = go.AddComponent<RectTransform>();
                var image = go.AddComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
                if (sprite == null) image.color = new Color(0.24f, 0.42f, 0.88f, 1f);
                _template = rect;
                return _template;
            }
            catch (Exception e)
            {
                _templateFailed = true;
                Core.Log.Error("[Marker] building the compass icon failed - no compass marker this session: " + e.Message);
                return null;
            }
        }

        private static Sprite LoadSprite()
        {
            using var stream = typeof(DestinationMarker).Assembly.GetManifestResourceStream(IconResource);
            if (stream == null) { Core.Log.Warning("[Marker] " + IconResource + " is not embedded - the compass shows a plain square."); return null; }
            var bytes = new byte[stream.Length];
            int read = 0;
            while (read < bytes.Length) { int n = stream.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes)) { Core.Log.Warning("[Marker] the compass icon PNG did not decode."); return null; }
            tex.wrapMode = TextureWrapMode.Clamp;
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }
    }
}
