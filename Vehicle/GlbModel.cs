using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Yesla.Vehicle
{
    /// <summary>
    /// Reads the embedded <c>yesla.glb</c> into Unity meshes and materials. Based on Yoink's GlbReader, cut down to
    /// what this model uses: one BIN chunk, float positions and normals, flat PBR materials (base colour, metallic,
    /// roughness, alpha blend), no textures.
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

        /// <summary>One material per glTF material name, shared by every part and every car.</summary>
        internal readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

        internal const string ResourceName = "Yesla.Assets.yesla.glb";

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

            var materials = new List<Material>();
            var materialNames = new List<string>();
            for (int i = 0; i < materialDefs.Count; i++)
            {
                var m = (JObject)materialDefs[i];
                string name = (string)m["name"] ?? ("material" + i);
                Material mat = BuildMaterial(m, name);
                materials.Add(mat);
                materialNames.Add(name);
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
                    // Mirroring one axis reverses the winding; swap two corners so faces point outwards again.
                    for (int i = 0; i + 2 < idx.Length; i += 3) { int t = idx[i + 1]; idx[i + 1] = idx[i + 2]; idx[i + 2] = t; }

                    int mi = prim["material"] != null ? (int)prim["material"] : -1;
                    var mesh = new Mesh { name = nodeName + "_" + (mi >= 0 ? materialNames[mi] : "none") };
                    if (pos.Length > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                    mesh.SetVertices(ToList(pos));
                    if (nrm != null && nrm.Length == pos.Length) mesh.SetNormals(ToList(nrm)); else mesh.RecalculateNormals();
                    mesh.SetTriangles(ToList(idx), 0);
                    mesh.RecalculateBounds();
                    mesh.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    triangles += idx.Length / 3;

                    parts.Add(new Part { Mesh = mesh, Material = mi >= 0 ? materials[mi] : null, MaterialName = mi >= 0 ? materialNames[mi] : "none" });
                }
                model.Nodes[nodeName] = parts;
            }

            Core.Log.Msg("[Model] yesla.glb read: " + model.Nodes.Count + " node(s), " + materials.Count + " material(s), " + triangles + " triangles.");
            return model;
        }

        private static Material BuildMaterial(JObject m, string name)
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

            if ((string)m["alphaMode"] == "BLEND") MakeTransparent(mat);
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
        }

        internal static Material Emissive(Material baseMat, Color glow, string name)
        {
            var mat = new Material(baseMat) { name = name };
            mat.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            mat.SetColor("_BaseColor", glow);
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", glow * 4f);
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return mat;
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

        private static Il2CppSystem.Collections.Generic.List<int> ToList(int[] v)
        {
            var l = new Il2CppSystem.Collections.Generic.List<int>(v.Length);
            for (int i = 0; i < v.Length; i++) l.Add(v[i]);
            return l;
        }
    }
}
