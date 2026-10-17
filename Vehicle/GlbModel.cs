using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Reads the embedded <c>yesla.glb</c> into Unity meshes and materials. Based on Yoink's GlbReader, cut down to
    /// what this model uses: one BIN chunk, float positions, normals and first UV set, PBR materials (base colour,
    /// metallic, roughness, alpha blend) and base colour textures embedded as PNG. Only the lamps are textured; every
    /// other material is a flat colour.
    ///
    /// glTF is right-handed and Unity left-handed. Negating X (not Z) converts between them and keeps the car facing
    /// +Z, which is the vehicle forward in Schedule I; the model's left side (+X in glTF) lands on -X, Unity's left.
    /// </summary>
    internal sealed class GlbModel
    {
        internal sealed class Part
        {
            internal Mesh Mesh;
            internal Material Material;
            internal string MaterialName;
        }

        /// <summary>Parts per glTF node name (Body_LOD0, Body_LOD1, Body_LOD2, Wheel, Wheel_LOD1).</summary>
        internal readonly Dictionary<string, List<Part>> Nodes = new Dictionary<string, List<Part>>();

        /// <summary>
        /// A moving part's hinge: the node's translation (its meshes are stored around it) and the axis it turns about,
        /// both in Unity model space. Only the moving parts (Steering, Wiper_L/R, Bonnet, Boot) carry one.
        /// </summary>
        internal readonly Dictionary<string, Hinge> Hinges = new Dictionary<string, Hinge>();

        internal struct Hinge
        {
            internal Vector3 Pivot;
            internal Vector3 Axis;
        }

        /// <summary>One material per glTF material name, shared by every part and every car.</summary>
        internal readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

        internal const string ResourceName = "Yesla.Assets.yesla.glb";

        /// <summary>
        /// True when a glTF material belongs to a named group: the group name itself or "Group.detail". Lamp
        /// materials are named after their class ("Light_Brake", "Light_Brake.left_rear_light"), so groups are found by
        /// prefix and a name like "Light_Cover" never lands in "Light_Head" by accident.
        /// </summary>
        internal static bool InGroup(string materialName, string group)
        {
            if (materialName == null) return false;
            return materialName == group || (materialName.Length > group.Length && materialName[group.Length] == '.' && materialName.StartsWith(group, StringComparison.Ordinal));
        }

        internal static GlbModel LoadEmbedded()
        {
            byte[] bytes;
            using (Stream s = typeof(GlbModel).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (s == null) { Core.Log.Error("[Model] embedded resource '" + ResourceName + "' is missing."); return null; }
                using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
            }
            try { return Parse(bytes); }
            catch (Exception e) { Core.Log.Error("[Model] reading yesla.glb failed: " + e); return null; }
        }

        private static GlbModel Parse(byte[] glb)
        {
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67) { Core.Log.Error("[Model] yesla.glb is not a GLB file."); return null; }

            JObject json = null;
            byte[] bin = null;
            int offset = 12;
            while (offset + 8 <= glb.Length)
            {
                int length = (int)BitConverter.ToUInt32(glb, offset);
                uint type = BitConverter.ToUInt32(glb, offset + 4);
                int start = offset + 8;
                if (start + length > glb.Length) break;
                if (type == 0x4E4F534A) json = JObject.Parse(Encoding.UTF8.GetString(glb, start, length));
                else if (type == 0x004E4942) { bin = new byte[length]; Buffer.BlockCopy(glb, start, bin, 0, length); }
                offset = start + length;
                if ((offset & 3) != 0) offset += 4 - (offset & 3);
            }
            if (json == null || bin == null) { Core.Log.Error("[Model] yesla.glb has no JSON or BIN chunk."); return null; }

            var model = new GlbModel();
            var accessors = (JArray)json["accessors"];
            var views = (JArray)json["bufferViews"];
            var meshes = (JArray)json["meshes"];
            var nodes = (JArray)json["nodes"];
            var materialDefs = (JArray)json["materials"];

            List<Texture2D> textures = ReadTextures(json, views, bin);

            var materials = new List<Material>();
            var materialNames = new List<string>();
            var materialTextured = new List<bool>();
            for (int i = 0; i < materialDefs.Count; i++)
            {
                var m = (JObject)materialDefs[i];
                string name = (string)m["name"] ?? ("material" + i);
                Material mat = BuildMaterial(m, name, textures, out bool textured);
                materials.Add(mat);
                materialNames.Add(name);
                materialTextured.Add(textured);
                model.Materials[name] = mat;
            }

            int triangles = 0;
            foreach (JObject node in nodes)
            {
                if (node["mesh"] == null) continue;
                string nodeName = (string)node["name"];
                var parts = new List<Part>();
                var prims = (JArray)meshes[(int)node["mesh"]]["primitives"];
                for (int p = 0; p < prims.Count; p++)
                {
                    var prim = (JObject)prims[p];
                    var attr = (JObject)prim["attributes"];
                    Vector3[] pos = ReadVec3(accessors, views, bin, (int)attr["POSITION"]);
                    Vector3[] nrm = attr["NORMAL"] != null ? ReadVec3(accessors, views, bin, (int)attr["NORMAL"]) : null;
                    int[] idx = ReadIndices(accessors, views, bin, (int)prim["indices"]);
                    int mi = prim["material"] != null ? (int)prim["material"] : -1;
                    bool needsUv = mi >= 0 && materialTextured[mi];
                    Vector2[] uv = needsUv && attr["TEXCOORD_0"] != null ? ReadVec2(accessors, views, bin, (int)attr["TEXCOORD_0"]) : null;
                    if (needsUv && uv == null) Core.Log.Warning("[Model] material '" + materialNames[mi] + "' is textured but the mesh in node '" + nodeName + "' has no usable UVs.");
                    // Mirroring one axis reverses the winding; swap two corners so faces point outwards again.
                    for (int i = 0; i + 2 < idx.Length; i += 3) { int t = idx[i + 1]; idx[i + 1] = idx[i + 2]; idx[i + 2] = t; }

                    var mesh = new Mesh { name = nodeName + "_" + (mi >= 0 ? materialNames[mi] : "none") };
                    if (pos.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    mesh.SetVertices(ToList(pos));
                    if (nrm != null && nrm.Length == pos.Length) mesh.SetNormals(ToList(nrm)); else mesh.RecalculateNormals();
                    if (uv != null && uv.Length == pos.Length) mesh.SetUVs(0, ToList(uv));
                    mesh.SetTriangles(ToList(idx), 0);
                    mesh.RecalculateBounds();
                    mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    triangles += idx.Length / 3;

                    parts.Add(new Part { Mesh = mesh, Material = mi >= 0 ? materials[mi] : null, MaterialName = mi >= 0 ? materialNames[mi] : "none" });
                }
                model.Nodes[nodeName] = parts;

                var tr = node["translation"] as JArray;
                var axis = node["extras"]?["axis"] as JArray;
                if (tr != null || axis != null)
                {
                    if (tr == null || axis == null || tr.Count != 3 || axis.Count != 3)
                        Core.Log.Warning("[Model] node '" + nodeName + "' has a pivot without an axis or the other way round - it cannot move.");
                    else
                        model.Hinges[nodeName] = new Hinge
                        {
                            Pivot = new Vector3(-(float)tr[0], (float)tr[1], (float)tr[2]),
                            Axis = new Vector3(-(float)axis[0], (float)axis[1], (float)axis[2]).normalized,
                        };
                }
            }

            Core.Log.Msg("[Model] yesla.glb read: " + model.Nodes.Count + " node(s), " + model.Hinges.Count + " moving, " + materials.Count + " material(s), " + triangles + " triangles.");
            return model;
        }

        private static Material BuildMaterial(JObject m, string name, List<Texture2D> textures, out bool textured)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) { Core.Log.Warning("[Model] URP/Lit shader not found, material '" + name + "' uses the default shader."); shader = Shader.Find("Standard"); }
            var mat = new Material(shader) { name = "Yesla_" + name };
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            var pbr = m["pbrMetallicRoughness"] as JObject;
            var f = pbr?["baseColorFactor"] as JArray;
            Color c = f != null && f.Count >= 3 ? new Color((float)f[0], (float)f[1], (float)f[2], f.Count > 3 ? (float)f[3] : 1f) : Color.white;
            float metallic = pbr?["metallicFactor"] != null ? (float)pbr["metallicFactor"] : 1f;
            float roughness = pbr?["roughnessFactor"] != null ? (float)pbr["roughnessFactor"] : 1f;

            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", 1f - roughness);

            textured = false;
            var baseTexture = pbr?["baseColorTexture"] as JObject;
            if (baseTexture != null)
            {
                int ti = baseTexture["index"] != null ? (int)baseTexture["index"] : -1;
                Texture2D tex = ti >= 0 && ti < textures.Count ? textures[ti] : null;
                if (tex == null) Core.Log.Error("[Model] material '" + name + "' points at texture " + ti + ", which did not load - it stays untextured.");
                else if (!mat.HasProperty("_BaseMap")) Core.Log.Error("[Model] the shader of material '" + name + "' has no _BaseMap - it stays untextured.");
                else
                {
                    mat.SetTexture("_BaseMap", tex);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                    textured = true;
                }
            }

            if ((string)m["alphaMode"] == "BLEND") MakeTransparent(mat);
            // The windows are single sheets whose faces point into the cabin; culled from outside they vanish.
            if (m["doubleSided"] != null && (bool)m["doubleSided"]) mat.SetFloat("_Cull", 0f);
            return mat;
        }

        /// <summary>URP/Lit set to Transparent / Alpha, the same state the material inspector writes.</summary>
        internal static void MakeTransparent(Material mat)
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", 5f);   // SrcAlpha
            mat.SetFloat("_DstBlend", 10f);  // OneMinusSrcAlpha
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = 3000;
            // A see-through sheet drawn from both sides would also shadow itself from both sides: the shadow map
            // lands on its own surface and shows as dark jagged patches across the windows.
            mat.SetShaderPassEnabled("ShadowCaster", false);
        }

        /// <summary>
        /// The lit version of a lamp material. A textured lamp keeps its base map and lights it up: the map is also the
        /// emission map, so the lamp's own pattern glows in the glow colour. A flat lamp takes the glow colour as its
        /// base colour.
        /// </summary>
        internal static Material Emissive(Material baseMat, Color glow, string name, bool useMap = true)
        {
            var mat = new Material(baseMat) { name = name };
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            Texture map = useMap && baseMat.HasProperty("_BaseMap") ? baseMat.GetTexture("_BaseMap") : null;
            if (map != null) mat.SetTexture("_EmissionMap", map);
            else mat.SetColor("_BaseColor", glow);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", glow * 4f);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return mat;
        }

        /// <summary>Decodes every glTF texture's embedded PNG; an entry is null when its image could not be decoded.</summary>
        private static List<Texture2D> ReadTextures(JObject json, JArray views, byte[] bin)
        {
            var result = new List<Texture2D>();
            var texDefs = json["textures"] as JArray;
            if (texDefs == null) return result;
            var images = json["images"] as JArray;
            var samplers = json["samplers"] as JArray;
            for (int i = 0; i < texDefs.Count; i++)
            {
                try
                {
                    var def = (JObject)texDefs[i];
                    int source = def["source"] != null ? (int)def["source"] : -1;
                    var image = images != null && source >= 0 && source < images.Count ? (JObject)images[source] : null;
                    if (image == null || image["bufferView"] == null) { Core.Log.Error("[Model] texture " + i + " has no embedded image."); result.Add(null); continue; }
                    var view = (JObject)views[(int)image["bufferView"]];
                    int offset = view["byteOffset"] != null ? (int)view["byteOffset"] : 0;
                    var bytes = new byte[(int)view["byteLength"]];
                    Buffer.BlockCopy(bin, offset, bytes, 0, bytes.Length);

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (!ImageConversion.LoadImage(tex, bytes, false))
                    {
                        Core.Log.Error("[Model] texture " + i + " (" + ((string)image["name"] ?? "unnamed") + ") is not a PNG Unity can decode.");
                        result.Add(null);
                        continue;
                    }
                    tex.name = "Yesla_" + ((string)image["name"] ?? ("texture" + i));
                    tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;

                    // Without a sampler in the file: bilinear filtering, repeat wrapping.
                    var sampler = samplers != null && def["sampler"] != null ? (JObject)samplers[(int)def["sampler"]] : null;
                    tex.filterMode = FilterFor(sampler);
                    tex.wrapMode = WrapFor(sampler?["wrapS"]);
                    result.Add(tex);
                }
                catch (Exception e)
                {
                    Core.Log.Error("[Model] reading texture " + i + " failed: " + e);
                    result.Add(null);
                }
            }
            return result;
        }

        private static FilterMode FilterFor(JObject sampler)
        {
            int mag = sampler?["magFilter"] != null ? (int)sampler["magFilter"] : 9729;
            int min = sampler?["minFilter"] != null ? (int)sampler["minFilter"] : 9987;
            if (mag == 9728 && (min == 9728 || min == 9984 || min == 9986)) return FilterMode.Point;
            return min == 9987 ? FilterMode.Trilinear : FilterMode.Bilinear;
        }

        private static TextureWrapMode WrapFor(JToken code)
        {
            int c = code != null ? (int)code : 10497;
            return c == 33071 ? TextureWrapMode.Clamp : c == 33648 ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;
        }

        private static Vector2[] ReadVec2(JArray accessors, JArray views, byte[] bin, int index)
        {
            var a = (JObject)accessors[index];
            if ((int)a["componentType"] != 5126 || (string)a["type"] != "VEC2") { Core.Log.Warning("[Model] a UV accessor is not float VEC2 - the mesh stays without UVs."); return null; }
            int count = (int)a["count"];
            Resolve(a, views, out int start, out int stride, 8);
            var r = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                // glTF puts the UV origin top-left, Unity bottom-left.
                r[i] = new Vector2(BitConverter.ToSingle(bin, o), 1f - BitConverter.ToSingle(bin, o + 4));
            }
            return r;
        }

        private static Vector3[] ReadVec3(JArray accessors, JArray views, byte[] bin, int index)
        {
            var a = (JObject)accessors[index];
            int count = (int)a["count"];
            Resolve(a, views, out int start, out int stride, 12);
            var r = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                r[i] = new Vector3(-BitConverter.ToSingle(bin, o), BitConverter.ToSingle(bin, o + 4), BitConverter.ToSingle(bin, o + 8));
            }
            return r;
        }

        private static int[] ReadIndices(JArray accessors, JArray views, byte[] bin, int index)
        {
            var a = (JObject)accessors[index];
            int type = (int)a["componentType"];
            int size = type == 5125 ? 4 : type == 5123 ? 2 : type == 5121 ? 1 : 0;
            if (size == 0) throw new InvalidDataException("index component type " + type);
            int count = (int)a["count"];
            Resolve(a, views, out int start, out int stride, size);
            var r = new int[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * stride;
                r[i] = size == 4 ? (int)BitConverter.ToUInt32(bin, o) : size == 2 ? BitConverter.ToUInt16(bin, o) : bin[o];
            }
            return r;
        }

        private static void Resolve(JObject accessor, JArray views, out int start, out int stride, int elementSize)
        {
            var view = (JObject)views[(int)accessor["bufferView"]];
            start = (view["byteOffset"] != null ? (int)view["byteOffset"] : 0) + (accessor["byteOffset"] != null ? (int)accessor["byteOffset"] : 0);
            stride = view["byteStride"] != null ? (int)view["byteStride"] : elementSize;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector3> ToList(Vector3[] v)
        {
            var l = new Il2CppSystem.Collections.Generic.List<Vector3>(v.Length);
            for (int i = 0; i < v.Length; i++) l.Add(v[i]);
            return l;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector2> ToList(Vector2[] v)
        {
            var l = new Il2CppSystem.Collections.Generic.List<Vector2>(v.Length);
            for (int i = 0; i < v.Length; i++) l.Add(v[i]);
            return l;
        }

        private static Il2CppSystem.Collections.Generic.List<int> ToList(int[] v)
        {
            var l = new Il2CppSystem.Collections.Generic.List<int>(v.Length);
            for (int i = 0; i < v.Length; i++) l.Add(v[i]);
            return l;
        }
    }
}
