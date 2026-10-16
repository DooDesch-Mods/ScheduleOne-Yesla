#if DEBUG
using System.Text;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Debugging
{
    /// <summary>
    /// Writes a car prefab's layout to the log: every transform with its local pose and components, plus the parts the
    /// Yesla swap rewires (wheels, colliders, LODs, body colour meshes, light arrays).
    /// </summary>
    internal static class PrefabDump
    {
        internal static void Dump(string code)
        {
            var manager = NetworkSingleton<VehicleManager>.Instance;
            LandVehicle lv = manager != null ? manager.GetVehiclePrefab(code) : null;
            if (lv == null) { Core.Log.Warning("[Dump] no vehicle prefab '" + code + "'."); return; }

            Core.Log.Msg("[Dump] ==== " + code + " (" + lv.gameObject.name + ")");
            Walk(lv.transform, lv.transform, 0);

            foreach (var w in lv.GetComponentsInChildren<Wheel>(true))
            {
                var wc = w.wheelCollider;
                Core.Log.Msg("[Dump] wheel " + Path(lv.transform, w.transform) + " local " + V(lv.transform.InverseTransformPoint(w.transform.position))
                             + " collider r=" + (wc != null ? wc.radius.ToString("F3") + " susp=" + wc.suspensionDistance.ToString("F3") + " center=" + V(wc.center) + " target=" + wc.suspensionSpring.targetPosition.ToString("F2") : "none")
                             + " model=" + Path(lv.transform, w.wheelModel) + " container=" + Path(lv.transform, w.modelContainer));
            }
            foreach (var bc in lv.GetComponentsInChildren<BoxCollider>(true))
                Core.Log.Msg("[Dump] box " + Path(lv.transform, bc.transform) + " center=" + V(bc.center) + " size=" + V(bc.size) + " scale=" + V(bc.transform.localScale)
                             + " trigger=" + bc.isTrigger + " layer=" + LayerMask.LayerToName(bc.gameObject.layer));
            foreach (var mc in lv.GetComponentsInChildren<MeshCollider>(true))
                Core.Log.Msg("[Dump] meshcol " + Path(lv.transform, mc.transform) + " convex=" + mc.convex + " mesh=" + (mc.sharedMesh != null ? mc.sharedMesh.name + " b=" + V(mc.sharedMesh.bounds.size) : "none")
                             + " layer=" + LayerMask.LayerToName(mc.gameObject.layer));
            foreach (var lod in lv.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lod.GetLODs();
                var sb = new StringBuilder("[Dump] lodgroup " + Path(lv.transform, lod.transform) + " size=" + lod.size.ToString("F2"));
                for (int i = 0; i < lods.Length; i++) sb.Append(" | LOD" + i + " h=" + lods[i].screenRelativeTransitionHeight.ToString("F3") + " r=" + lods[i].renderers.Length);
                Core.Log.Msg(sb.ToString());
            }
            if (lv.Color != null)
                foreach (var bm in lv.Color.BodyMeshes)
                    Core.Log.Msg("[Dump] bodymesh " + Path(lv.transform, bm.Renderer?.transform) + " matIndex=" + bm.MaterialIndex);
            var lights = lv.GetComponentInChildren<VehicleLights>(true);
            if (lights != null)
            {
                Core.Log.Msg("[Dump] lights head=" + lights.headLightMeshes.Length + " headSrc=" + lights.headLightSources.Length + " brake=" + lights.brakeLightMeshes.Length
                             + " brakeSrc=" + lights.brakeLightSources.Length + " reverse=" + lights.hasReverseLights + "/" + lights.reverseLightMeshes.Length);
                foreach (var m in lights.headLightMeshes) Core.Log.Msg("[Dump]   head mesh " + Path(lv.transform, m?.transform));
                foreach (var s in lights.headLightSources) Core.Log.Msg("[Dump]   head src " + Path(lv.transform, s?.transform) + " local " + V(lv.transform.InverseTransformPoint(s.transform.position)));
                foreach (var m in lights.brakeLightMeshes) Core.Log.Msg("[Dump]   brake mesh " + Path(lv.transform, m?.transform));
                foreach (var s in lights.brakeLightSources) Core.Log.Msg("[Dump]   brake src " + Path(lv.transform, s?.transform) + " local " + V(lv.transform.InverseTransformPoint(s.transform.position)));
                Core.Log.Msg("[Dump]   mats headOn=" + lights.headlightMat_On?.name + " headOff=" + lights.headLightMat_Off?.name + " brakeOn=" + lights.brakeLightMat_On?.name + " brakeOff=" + lights.brakeLightMat_Off?.name);
            }
            Core.Log.Msg("[Dump] vehicleModel=" + Path(lv.transform, lv.vehicleModel?.transform) + " boundingBox=" + Path(lv.transform, lv.boundingBox?.transform));
            Core.Log.Msg("[Dump] ==== end");
        }

        private static void Walk(Transform root, Transform t, int depth)
        {
            var sb = new StringBuilder("[Dump] ");
            sb.Append(new string(' ', depth * 2)).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)")
              .Append(" p=").Append(V(t.localPosition)).Append(" r=").Append(V(t.localEulerAngles)).Append(" s=").Append(V(t.localScale)).Append(" [");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n == "Transform") continue;
                sb.Append(n);
                var mf = c.TryCast<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) sb.Append("(").Append(mf.sharedMesh.name).Append(" ").Append(V(mf.sharedMesh.bounds.center)).Append("±").Append(V(mf.sharedMesh.bounds.extents)).Append(")");
                var mr = c.TryCast<MeshRenderer>();
                if (mr != null)
                {
                    sb.Append("(");
                    foreach (var m in mr.sharedMaterials) sb.Append(m != null ? m.name : "null").Append(",");
                    sb.Append(")");
                }
                sb.Append(" ");
            }
            sb.Append("]");
            Core.Log.Msg(sb.ToString());
            for (int i = 0; i < t.childCount; i++) Walk(root, t.GetChild(i), depth + 1);
        }

        private static string Path(Transform root, Transform t)
        {
            if (t == null) return "null";
            string p = t.name;
            while (t.parent != null && t.parent.Pointer != root.Pointer) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        private static string V(Vector3 v) => "(" + v.x.ToString("F3") + "," + v.y.ToString("F3") + "," + v.z.ToString("F3") + ")";
    }
}
#endif
