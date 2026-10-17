using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Storage;
using Il2CppScheduleOne.Vehicles;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Turns the cloned Hounddog into the Yesla Model 3 on the outside. The donor keeps its physics, seats, storage,
    /// sensors and network parts; only what you see changes:
    ///
    /// * the donor body and wheel renderers are switched off, the Yesla meshes go into the same LODGroup;
    /// * the wheels move to the model's wheel positions and get its radius, so the tyres sit in the arches;
    /// * <c>VehicleColor</c> paints the Yesla's paint parts, <c>VehicleLights</c> switches the Yesla's lamps;
    /// * the bounding box (the road AI reads it as the car's size) is fitted to the model.
    ///
    /// Everything happens once on the prefab; spawned cars are copies of it, and Unity remaps the references.
    /// </summary>
    internal static class YeslaBody
    {
        // Model space: ground at y = 0, car centred on the wheelbase. Car space (the donor's): the wheel transforms
        // sit at y = -0.302 and the default suspension pose drops the wheel centre to -0.350.
        private const float WheelTransformY = -0.302f;
        private const float WheelRestCentreY = -0.350f;
        private const float ModelOffsetZ = 0.0985f;        // the donor's wheelbase centre, so the car keeps its balance

        private static readonly Color HeadOn = new Color(1f, 0.97f, 0.9f);
        private static readonly Color BrakeOn = new Color(1f, 0.03f, 0.02f);
        /// <summary>The tail lamps with the headlights on: the brake lamps at a quarter of their glow.</summary>
        private static readonly Color TailOn = new Color(0.25f, 0.008f, 0.005f);
        private static readonly Color ReverseOn = Color.white;

        /// <summary>Tail lamp material, shared by every Yesla; <see cref="MovingParts"/> puts it on the brake lamps.</summary>
        internal static Material TailMat;

        /// <summary>
        /// Car paint: a metallic flake under a clear coat. The model's paint is a flat dielectric at 0.72 smoothness,
        /// which reads as plastic; URP/Lit has no clear coat, so a little metal and a high smoothness stand in for it.
        /// </summary>
        internal static float PaintMetallic = 0.45f, PaintSmoothness = 0.9f;
        /// <summary>The headlight beams start this far in front of the lamp's foremost point, in metres.</summary>
        private const float BeamAhead = 0.02f;

        internal static bool Apply(GameObject car)
        {
            GlbModel model = GlbModel.LoadEmbedded();
            if (model == null) return false;

            YeslaLayout layout = YeslaLayout.Default;
            LandVehicle lv = car.GetComponent<LandVehicle>();
            Transform body = lv.vehicleModel != null ? lv.vehicleModel.transform : null;
            if (body == null) { Core.Log.Error("[Body] the donor has no vehicle model - keeping the donor look."); return false; }
            LODGroup lodGroup = body.GetComponentInChildren<LODGroup>(true);
            if (lodGroup == null) { Core.Log.Error("[Body] the donor body has no LODGroup - keeping the donor look."); return false; }

            int hidden = HideRenderers(lodGroup.transform);
            int layer = lodGroup.gameObject.layer;
            float modelOffsetY = WheelRestCentreY - layout.WheelRadius;

            var root = new GameObject("YeslaModel");
            root.layer = layer;
            root.transform.SetParent(lodGroup.transform, false);
            root.transform.localPosition = new Vector3(0f, modelOffsetY, ModelOffsetZ);

            var lods = new List<Renderer>[3];
            var paint = new List<MeshRenderer>();
            var head = new List<MeshRenderer>();
            var brake = new List<MeshRenderer>();
            var reverse = new List<MeshRenderer>();
            for (int i = 0; i < 3; i++)
            {
                lods[i] = new List<Renderer>();
                if (!model.Nodes.TryGetValue("Body_LOD" + i, out List<GlbModel.Part> parts)) { Core.Log.Warning("[Body] the model has no Body_LOD" + i + "."); continue; }
                var lodRoot = new GameObject("Body_LOD" + i);
                lodRoot.layer = layer;
                lodRoot.transform.SetParent(root.transform, false);
                foreach (GlbModel.Part part in parts)
                {
                    MeshRenderer r = AddPart(lodRoot.transform, part, layer);
                    lods[i].Add(r);
                    Sort(r, part, lodRoot.transform, i == 0);
                }
            }

            // Lamp materials are named after their class ("Light_Brake" or "Light_Brake.left_rear_light"), so the
            // groups are found by prefix. Light_Cover, the smoked lens over the tail lamps, is in no group.
            void Sort(MeshRenderer r, GlbModel.Part part, Transform parent, bool lod0)
            {
                if (GlbModel.InGroup(part.MaterialName, "Paint")) paint.Add(r);
                else if (GlbModel.InGroup(part.MaterialName, "Light_Head")) head.Add(r);
                else if (GlbModel.InGroup(part.MaterialName, "Light_Brake")) brake.Add(r);
                else if (GlbModel.InGroup(part.MaterialName, "Light_Reverse")) reverse.Add(r);
                else if (lod0 && GlbModel.InGroup(part.MaterialName, "Licence_Plate")) Plate.Fit(parent, r, layer);
            }

            // The parts that move hang from a pivot at their hinge or axle (MovingParts turns the pivots). They are
            // drawn with LOD0; LOD1 and LOD2 carry them closed and still.
            int moving = 0;
            foreach (var hinge in model.Hinges)
            {
                if (!model.Nodes.TryGetValue(hinge.Key, out List<GlbModel.Part> parts)) { Core.Log.Warning("[Body] hinge " + hinge.Key + " has no meshes."); continue; }
                var pivot = new GameObject(MovingParts.PivotPrefix + hinge.Key);
                pivot.layer = layer;
                pivot.transform.SetParent(root.transform, false);
                pivot.transform.localPosition = hinge.Value.Pivot;
                MovingParts.Axes[hinge.Key] = hinge.Value.Axis;
                foreach (GlbModel.Part part in parts)
                {
                    MeshRenderer r = AddPart(pivot.transform, part, layer);
                    lods[0].Add(r);
                    Sort(r, part, pivot.transform, true);
                }
                moving++;
            }

            // The cabin: Cabin_LOD0 is drawn with LOD0; Cabin_HI, the detailed one, stays outside the LODGroup and only
            // replaces Cabin_LOD0 in the car the local player sits in (Visual.Cabin.Show).
            if (model.Nodes.TryGetValue("Cabin_LOD0", out List<GlbModel.Part> cabinParts))
            {
                var cabin = new GameObject(Visual.Cabin.LowName);
                cabin.layer = layer;
                cabin.transform.SetParent(root.transform, false);
                foreach (GlbModel.Part part in cabinParts) lods[0].Add(AddPart(cabin.transform, part, layer));
            }
            else Core.Log.Warning("[Body] the model has no Cabin_LOD0 - the car has no interior.");
            if (model.Nodes.TryGetValue("Cabin_HI", out List<GlbModel.Part> cabinHi))
            {
                var hi = new GameObject(Visual.Cabin.HighName);
                hi.layer = layer;
                hi.SetActive(false);
                hi.transform.SetParent(root.transform, false);
                foreach (GlbModel.Part part in cabinHi) AddPart(hi.transform, part, layer);
            }
            else Core.Log.Warning("[Body] the model has no Cabin_HI - the driver sees the low cabin.");

            // The visualisation's stand-in car. It stays inactive and outside the LODGroup, so the world never draws it;
            // VizScene only reads its meshes and transforms.
            if (model.Nodes.TryGetValue("Body_Viz", out List<GlbModel.Part> vizParts))
            {
                var viz = new GameObject("Body_Viz");
                viz.layer = layer;
                viz.SetActive(false);
                viz.transform.SetParent(root.transform, false);
                foreach (GlbModel.Part part in vizParts) AddPart(viz.transform, part, layer);
            }
            else Core.Log.Warning("[Body] the model has no Body_Viz - the visualisation has no car.");

            var old = lodGroup.GetLODs();
            var newLods = new Il2CppReferenceArray<LOD>(3);
            for (int i = 0; i < 3; i++)
            {
                float h = i < old.Length ? old[i].screenRelativeTransitionHeight : 0.01f;
                newLods[i] = new LOD { screenRelativeTransitionHeight = h, renderers = ToArray(lods[i]) };
            }
            lodGroup.SetLODs(newLods);
            lodGroup.RecalculateBounds();

            if (paint.Count > 0 && paint[0].sharedMaterial != null) Finish(paint[0].sharedMaterial);
            BindColour(lv, paint);
            BindLights(car, head, brake, reverse);
            PlaceBeams(car, head);
            int wheels = FitWheels(lv, model, layout, layer);
            FitBounds(lv, layout, modelOffsetY);
            HideExhaust(lodGroup.transform);
            AddFrunk(car, layout);

            Core.Log.Msg("[Body] Yesla body on: " + hidden + " donor renderer(s) off, LODs " + lods[0].Count + "/" + lods[1].Count + "/" + lods[2].Count
                       + " part(s), " + moving + " moving part(s), " + paint.Count + " paint, lights " + head.Count + " head / " + brake.Count + " brake / " + reverse.Count + " reverse, " + wheels + " wheel(s).");
            return true;
        }

        /// <summary>Sets the paint finish; VehicleColor copies this material for every repaint, so the copies keep it.</summary>
        internal static void Finish(Material paint)
        {
            paint.SetFloat("_Metallic", PaintMetallic);
            paint.SetFloat("_Smoothness", PaintSmoothness);
        }

        /// <summary>
        /// The donor's headlight sources carry the light and its volumetric beam, and sit where the Hounddog's lamps
        /// are; on the Yesla the beam then starts in the air beside the lamp. Each source moves to the middle of the
        /// Yesla head lamp on its side, at the lamp's front.
        /// </summary>
        private static void PlaceBeams(GameObject car, List<MeshRenderer> head)
        {
            VehicleLights lights = car.GetComponent<VehicleLights>();
            if (lights == null || lights.headLightSources == null || lights.headLightSources.Length == 0) { Core.Log.Warning("[Body] the donor has no headlight sources - no beams to move."); return; }
            Transform t = car.transform;
            bool[] found = new bool[2];
            Bounds[] side = new Bounds[2];
            // Vertices, not renderer bounds: the prefab is inactive, and an inactive renderer has no world bounds. One
            // head lamp part may hold both lamps, so every vertex counts for the side it is on.
            foreach (MeshRenderer r in head)
            {
                MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null) continue;
                Matrix4x4 toCar = t.worldToLocalMatrix * r.transform.localToWorldMatrix;
                foreach (Vector3 v in mf.sharedMesh.vertices)
                {
                    Vector3 local = toCar.MultiplyPoint3x4(v);
                    int s = local.x < 0f ? 0 : 1;
                    if (!found[s]) { side[s] = new Bounds(local, Vector3.zero); found[s] = true; }
                    else side[s].Encapsulate(local);
                }
            }
            if (!found[0] || !found[1]) { Core.Log.Warning("[Body] the head lamps are not on both sides of the car - the beams stay where the donor had them."); return; }
            int moved = 0;
            foreach (var source in lights.headLightSources)
            {
                if (source == null) continue;
                Transform st = source.transform;
                Vector3 was = t.InverseTransformPoint(st.position);
                Bounds lamp = side[was.x < 0f ? 0 : 1];
                Vector3 at = new Vector3(lamp.center.x, lamp.center.y, lamp.max.z + BeamAhead);
                st.position = t.TransformPoint(at);
                Core.Log.Msg("[Body] headlight beam " + st.name + " moved from " + was.ToString("F2") + " to " + at.ToString("F2") + " (car space).");
                moved++;
            }
            if (moved == 0) Core.Log.Warning("[Body] every headlight source is empty - no beams moved.");
        }

        private static MeshRenderer AddPart(Transform parent, GlbModel.Part part, int layer)
        {
            var go = new GameObject(part.MaterialName);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = part.Material;
            return r;
        }

        private static int HideRenderers(Transform t)
        {
            int n = 0;
            foreach (MeshRenderer r in t.GetComponentsInChildren<MeshRenderer>(true)) { if (r.enabled) n++; r.enabled = false; }
            return n;
        }

        private static void BindColour(LandVehicle lv, List<MeshRenderer> paint)
        {
            if (lv.Color == null) { Core.Log.Warning("[Body] no VehicleColor on the donor - the Yesla cannot be repainted."); return; }
            var meshes = new Il2CppReferenceArray<VehicleColor.BodyMesh>(paint.Count);
            for (int i = 0; i < paint.Count; i++) meshes[i] = new VehicleColor.BodyMesh { Renderer = paint[i], MaterialIndex = 0 };
            lv.Color.BodyMeshes = meshes;
        }

        private static void BindLights(GameObject car, List<MeshRenderer> head, List<MeshRenderer> brake, List<MeshRenderer> reverse)
        {
            VehicleLights lights = car.GetComponent<VehicleLights>();
            if (lights == null) { Core.Log.Warning("[Body] no VehicleLights on the donor - the Yesla lamps stay dark."); return; }

            // Every lamp renderer already carries its textured material; the group's off material is that one, the on
            // material is the same material with its texture glowing in the lamp colour.
            lights.headLightMeshes = ToMeshArray(head);
            Material headOff = GroupMaterial("Light_Head", head);
            if (headOff != null)
            {
                lights.headLightMat_Off = headOff;
                lights.headlightMat_On = GlbModel.Emissive(headOff, HeadOn, "Yesla_Light_Head_On");
            }

            lights.brakeLightMeshes = ToMeshArray(brake);
            Material brakeOff = GroupMaterial("Light_Brake", brake);
            if (brakeOff != null)
            {
                lights.brakeLightMat_Off = brakeOff;
                lights.brakeLightMat_On = GlbModel.Emissive(brakeOff, BrakeOn, "Yesla_Light_Brake_On");
                TailMat = GlbModel.Emissive(brakeOff, TailOn, "Yesla_Light_Tail_On");
            }

            lights.hasReverseLights = reverse.Count > 0;
            lights.reverseLightMeshes = ToMeshArray(reverse);
            Material reverseOff = GroupMaterial("Light_Reverse", reverse);
            if (reverseOff != null)
            {
                lights.reverseLightMat_Off = reverseOff;
                // The reverse lamp's texture is mostly dark, and the smoked cover sits over it: lit through the
                // texture it barely shows. It glows flat white instead.
                lights.reverseLightMat_On = GlbModel.Emissive(reverseOff, ReverseOn, "Yesla_Light_Reverse_On", useMap: false);
            }
        }

        /// <summary>
        /// The material a lamp group switches back to when it is off. VehicleLights swaps a whole group to one material,
        /// so a group whose renderers use different materials would lose that difference on the first switch; it is
        /// logged and the first renderer's material is taken.
        /// </summary>
        private static Material GroupMaterial(string group, List<MeshRenderer> renderers)
        {
            if (renderers.Count == 0) { Core.Log.Warning("[Body] the model has no " + group + " renderers - that lamp group stays as the donor had it."); return null; }
            Material first = renderers[0].sharedMaterial;
            if (first == null) { Core.Log.Error("[Body] the first " + group + " renderer has no material - that lamp group keeps the donor's materials."); return null; }
            var names = new HashSet<string>();
            foreach (MeshRenderer r in renderers) names.Add(r.sharedMaterial != null ? r.sharedMaterial.name : "(none)");
            if (names.Count > 1)
                Core.Log.Warning("[Body] the " + group + " renderers use " + names.Count + " different materials (" + string.Join(", ", names) + "); VehicleLights swaps the group to one material, so every lamp takes " + first.name + " once it switches.");
            return first;
        }

        private static int FitWheels(LandVehicle lv, GlbModel model, YeslaLayout layout, int layer)
        {
            model.Nodes.TryGetValue("Wheel", out List<GlbModel.Part> lod0);
            model.Nodes.TryGetValue("Wheel_LOD1", out List<GlbModel.Part> lod1);
            if (lod0 == null) { Core.Log.Error("[Body] the model has no Wheel - keeping the donor wheels."); return 0; }

            int n = 0;
            foreach (Wheel w in lv.GetComponentsInChildren<Wheel>(true))
            {
                Transform t = w.transform;
                bool right = t.localPosition.x > 0f;
                bool front = t.localPosition.z > 0f;
                t.localPosition = new Vector3((right ? 1f : -1f) * layout.WheelX, WheelTransformY, (front ? layout.WheelZ : -layout.WheelZ) + ModelOffsetZ);

                float scale = t.localScale.y;
                if (w.wheelCollider != null) w.wheelCollider.radius = layout.WheelRadius / scale;
                else Core.Log.Warning("[Body] wheel " + t.name + " has no WheelCollider.");

                if (w.wheelModel == null) { Core.Log.Warning("[Body] wheel " + t.name + " has no model transform - no Yesla wheel there."); continue; }
                HideRenderers(w.wheelModel);

                var root = new GameObject("YeslaWheel");
                root.layer = layer;
                root.transform.SetParent(w.wheelModel, false);
                // The wheel mesh's outer face points left; the right side gets a mirrored copy.
                root.transform.localScale = new Vector3((right ? -1f : 1f) / scale, 1f / scale, 1f / scale);

                var r0 = new List<Renderer>();
                foreach (GlbModel.Part p in lod0) r0.Add(AddPart(root.transform, p, layer));
                var r1 = new List<Renderer>();
                if (lod1 != null)
                {
                    var l1 = new GameObject("LOD1");
                    l1.layer = layer;
                    l1.transform.SetParent(root.transform, false);
                    foreach (GlbModel.Part p in lod1) r1.Add(AddPart(l1.transform, p, layer));
                }
                LODGroup g = root.AddComponent<LODGroup>();
                var lods = new Il2CppReferenceArray<LOD>(r1.Count > 0 ? 2 : 1);
                lods[0] = new LOD { screenRelativeTransitionHeight = 0.03f, renderers = ToArray(r0) };
                if (r1.Count > 0) lods[1] = new LOD { screenRelativeTransitionHeight = 0.004f, renderers = ToArray(r1) };
                g.SetLODs(lods);
                g.RecalculateBounds();

                // The brake caliper: on the wheel, not on its model, so it steers (MovingParts) but does not spin.
                if (model.Nodes.TryGetValue(front ? "Caliper_F" : "Caliper_R", out List<GlbModel.Part> caliper))
                {
                    var cal = new GameObject(MovingParts.CaliperName);
                    cal.layer = layer;
                    cal.transform.SetParent(t, false);
                    cal.transform.position = w.wheelModel.position;
                    cal.transform.localScale = new Vector3((right ? -1f : 1f) / scale, 1f / scale, 1f / scale);
                    var rc = new List<Renderer>();
                    foreach (GlbModel.Part p in caliper) rc.Add(AddPart(cal.transform, p, layer));
                    LODGroup cg = cal.AddComponent<LODGroup>();
                    var clods = new Il2CppReferenceArray<LOD>(1);
                    clods[0] = new LOD { screenRelativeTransitionHeight = 0.03f, renderers = ToArray(rc) };
                    cg.SetLODs(clods);
                    cg.RecalculateBounds();
                }
                else Core.Log.Warning("[Body] the model has no " + (front ? "Caliper_F" : "Caliper_R") + " - wheel " + t.name + " has no caliper.");
                n++;
            }

            Transform carRoot = lv.transform;
            SetZ(carRoot.Find("FrontAxlePosition"), layout.WheelZ + ModelOffsetZ);
            SetZ(carRoot.Find("RearAxlePosition"), -layout.WheelZ + ModelOffsetZ);
            return n;
        }

        private static void SetZ(Transform t, float z)
        {
            if (t == null) { Core.Log.Warning("[Body] axle marker missing on the donor."); return; }
            Vector3 p = t.localPosition;
            t.localPosition = new Vector3(p.x, p.y, z);
        }

        private static void FitBounds(LandVehicle lv, YeslaLayout layout, float modelOffsetY)
        {
            if (lv.boundingBox == null) { Core.Log.Warning("[Body] the donor has no bounding box."); return; }
            Transform b = lv.boundingBox.transform;
            float bottom = modelOffsetY;
            float top = modelOffsetY + layout.Height;
            float centreZ = (layout.MinZ + layout.MaxZ) * 0.5f + ModelOffsetZ;
            b.localPosition = new Vector3(0f, (bottom + top) * 0.5f, centreZ);
            b.localScale = new Vector3(layout.BodyWidth, layout.Height, layout.MaxZ - layout.MinZ);
        }

        /// <summary>
        /// A second way into the car's storage at the nose, "Access frunk": a copy of the donor's trunk access point,
        /// moved as far in from the front bumper as the original sits in from the rear one. It finds the same
        /// StorageEntity (GetComponentInParent), so both ends open the same slots; MovingParts opens the lid at the end
        /// it was opened from.
        /// </summary>
        private static void AddFrunk(GameObject car, YeslaLayout layout)
        {
            var points = car.GetComponentsInChildren<StorageEntityInteractable>(true);
            if (points.Length == 0) { Core.Log.Warning("[Body] the donor has no storage access point - the Yesla has no frunk."); return; }
            Transform trunk = points[0].transform;
            Transform ct = car.transform;
            Vector3 at = ct.InverseTransformPoint(trunk.position);
            float inset = at.z - (layout.MinZ + ModelOffsetZ);
            GameObject frunk = UnityEngine.Object.Instantiate(trunk.gameObject, trunk.parent);
            frunk.name = MovingParts.FrunkName;
            frunk.transform.position = ct.TransformPoint(new Vector3(at.x, at.y, layout.MaxZ + ModelOffsetZ - inset));
            frunk.transform.rotation = trunk.rotation;
            InteractableObject io = frunk.GetComponent<InteractableObject>();
            if (io != null) io.SetMessage("Access frunk");
            else Core.Log.Warning("[Body] the frunk access point has no InteractableObject - it keeps the trunk's prompt.");
            Core.Log.Msg("[Body] frunk access point at " + ct.InverseTransformPoint(frunk.transform.position).ToString("F2") + " (trunk " + at.ToString("F2") + ", " + trunk.name + ").");
        }

        /// <summary>An electric car has no exhaust; the donor's tailpipe smoke goes.</summary>
        private static void HideExhaust(Transform t)
        {
            foreach (Transform c in t.GetComponentsInChildren<Transform>(true))
                if (c.name.StartsWith("SmokeParticles", StringComparison.Ordinal)) c.gameObject.SetActive(false);
        }

        private static Il2CppReferenceArray<Renderer> ToArray(List<Renderer> list)
        {
            var a = new Il2CppReferenceArray<Renderer>(list.Count);
            for (int i = 0; i < list.Count; i++) a[i] = list[i];
            return a;
        }

        private static Il2CppReferenceArray<MeshRenderer> ToMeshArray(List<MeshRenderer> list)
        {
            var a = new Il2CppReferenceArray<MeshRenderer>(list.Count);
            for (int i = 0; i < list.Count; i++) a[i] = list[i];
            return a;
        }
    }

    /// <summary>Measurements of yesla.glb in model space (metres, ground at 0), from yesla.layout.json.</summary>
    internal struct YeslaLayout
    {
        internal float WheelX, WheelZ, WheelRadius, Height, BodyWidth, MinZ, MaxZ;

        internal static readonly YeslaLayout Default = new YeslaLayout
        {
            WheelX = 0.8293f,
            WheelZ = 1.4270f,
            WheelRadius = 0.3324f,
            Height = 1.4002f,
            BodyWidth = 1.86f,      // without the mirrors
            MinZ = -2.4777f,
            MaxZ = 2.2189f,
        };
    }
}
