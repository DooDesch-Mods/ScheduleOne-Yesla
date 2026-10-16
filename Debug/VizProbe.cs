#if DEBUG
using System.Text;
using Il2CppPathfinding;
using Il2CppScheduleOne.PlayerScripts;

namespace Yesla.Debugging
{
    /// <summary>
    /// DEBUG: the facts the visualisation is built on, measured in the running game: the road links around the
    /// player, the layers nobody named, which shaders the build kept, and every camera with its culling mask.
    /// </summary>
    internal static class VizProbe
    {
        internal static void Run()
        {
            var sb = new StringBuilder();
            Vector3 at = Player.Local != null ? Player.Local.transform.position : Vector3.zero;

            var links = NodeLink.validNodeLinks;
            int total = links != null ? links.Count : -1;
            int oneWay = 0;
            float sum = 0f;
            if (links != null)
            {
                for (int i = 0; i < links.Count; i++)
                {
                    NodeLink l = links[i];
                    if (l == null || l.Start == null || l.End == null) continue;
                    if (l.oneWay) oneWay++;
                    sum += Vector3.Distance(l.Start.position, l.End.position);
                }
            }
            sb.Append("[VizProbe] links=" + total + " oneWay=" + oneWay + " avgLen=" + (total > 0 ? (sum / total).ToString("F1") : "-") + "\n");
            var near = NodeLink.GetClosestLinks(at, 8);
            for (int i = 0; near != null && i < near.Count; i++)
            {
                NodeLink l = near[i];
                sb.Append("[VizProbe]   link " + l.name + " " + l.Start.position.ToString("F1") + " -> " + l.End.position.ToString("F1")
                          + " oneWay=" + l.oneWay + " parent=" + (l.transform.parent != null ? l.transform.parent.name : "-") + "\n");
            }

            sb.Append("[VizProbe] unnamed layers:");
            for (int i = 8; i < 32; i++) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) sb.Append(" " + i);
            sb.Append("\n[VizProbe] named layers:");
            for (int i = 0; i < 32; i++) { string n = LayerMask.LayerToName(i); if (!string.IsNullOrEmpty(n)) sb.Append(" " + i + "=" + n); }
            sb.Append("\n");

            foreach (string s in new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit", "Universal Render Pipeline/Simple Lit",
                                         "Unlit/Color", "Sprites/Default", "UI/Default", "TextMeshPro/Distance Field", "Hidden/Internal-Colored" })
                sb.Append("[VizProbe] shader '" + s + "' " + (Shader.Find(s) != null ? "found" : "missing") + "\n");

            foreach (Camera c in Camera.allCameras)
                sb.Append("[VizProbe] camera " + c.name + " enabled=" + c.enabled + " mask=0x" + c.cullingMask.ToString("X8") + " depth=" + c.depth
                          + " far=" + c.farClipPlane + " target=" + (c.targetTexture != null ? c.targetTexture.name : "screen") + "\n");
            sb.Append("[VizProbe] sun=" + (RenderSettings.sun != null ? RenderSettings.sun.name + " " + RenderSettings.sun.intensity : "none")
                      + " fog=" + RenderSettings.fog + " ambientMode=" + RenderSettings.ambientMode);
            Core.Log.Msg(sb.ToString());
        }
    }
}
#endif
