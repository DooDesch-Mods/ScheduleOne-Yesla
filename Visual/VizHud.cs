using UnityEngine.UI;

namespace Yesla.Visual
{
    /// <summary>
    /// Puts the visualisation on screen: a panel at the bottom right, or full screen over a dimmed game. It is one
    /// overlay canvas with the render texture in a RawImage; the texture is drawn elsewhere, this only places it.
    /// </summary>
    internal static class VizHud
    {
        private const float PanelHeight = 0.3f;     // of the screen height
        private const float FullHeight = 0.86f;
        private const float Margin = 24f;

        private static GameObject _canvas;
        private static RawImage _image, _backdrop;
        private static VizMode _shown = VizMode.Off;

        internal static void Show(VizMode mode)
        {
            if (mode != VizMode.Off && !Build()) return;
            if (_canvas == null) return;
            bool on = mode != VizMode.Off && VizScene.Texture != null;
            if (_canvas.activeSelf != on) _canvas.SetActive(on);
            if (!on) { _shown = VizMode.Off; return; }

            _image.texture = VizScene.Texture;
            float aspect = (float)VizScene.Width / VizScene.Height;
            RectTransform rt = _image.rectTransform;
            if (mode == VizMode.Full)
            {
                float hgt = Screen.height * FullHeight;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(hgt * aspect, hgt);
            }
            else
            {
                float hgt = Screen.height * PanelHeight;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
                rt.anchoredPosition = new Vector2(-Margin, Margin);
                rt.sizeDelta = new Vector2(hgt * aspect, hgt);
            }
            _backdrop.gameObject.SetActive(mode == VizMode.Full);
            if (_shown != mode) Core.Log.Msg("[Viz] on screen: " + mode + ", " + rt.sizeDelta.x.ToString("F0") + "x" + rt.sizeDelta.y.ToString("F0") + " px.");
            _shown = mode;
        }

        private static bool Build()
        {
            if (_canvas != null) return true;
            _canvas = new GameObject("YeslaViz_Hud");
            UnityEngine.Object.DontDestroyOnLoad(_canvas);
            _canvas.hideFlags = HideFlags.DontSave;
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5;

            _backdrop = Image("Backdrop", new Color(0f, 0f, 0f, 0.55f));
            RectTransform b = _backdrop.rectTransform;
            b.anchorMin = Vector2.zero; b.anchorMax = Vector2.one; b.sizeDelta = Vector2.zero;
            _image = Image("Picture", Color.white);
            _canvas.SetActive(false);
            Core.Log.Msg("[Viz] HUD canvas built.");
            return true;
        }

        private static RawImage Image(string name, Color colour)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var img = go.AddComponent<RawImage>();
            img.color = colour;
            img.raycastTarget = false;
            return img;
        }

        internal static void Destroy()
        {
            if (_canvas != null) UnityEngine.Object.Destroy(_canvas);
            _canvas = null; _image = null; _backdrop = null;
            _shown = VizMode.Off;
        }
    }
}
