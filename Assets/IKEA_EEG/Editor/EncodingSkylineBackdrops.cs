using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Distant skyline backdrops for the Encoding (Area A) Japanese city: four painted skyline
    /// images (North / East / South / West), each 4128 x 1024 and covering 90 degrees of
    /// azimuth, shown on a ring far behind the 3D surround.
    ///
    /// HOW IT IS SHAPED
    ///   * Angle-true from the Spawn_A eye: every vertex is placed by azimuth and elevation, so
    ///     each image column covers 90/4128 degrees and each row the same, whatever the
    ///     distance. The ring can therefore be a circle in front and behind and flat at the
    ///     sides (see k_MaxAbsX) without distorting the picture.
    ///   * Opaque, no transparency: the top edge of each strip follows the skyline silhouette
    ///     traced from the image itself, so the painted sky is never drawn and the scene's own
    ///     skybox shows above the buildings. One URP Unlit material per image.
    ///   * The image horizon (k_HorizonRow) sits at eye height, so the painted skyline stands
    ///     on the 3D ground's horizon and hides its hard edge in the gaps between buildings.
    ///
    /// WHAT IT ADDS: Encoding_JapaneseEnvironment/Backdrops with one child per ACTIVE direction
    /// (k_ActiveDirections; MeshFilter + MeshRenderer only — no collider, script, light or
    /// interactable), plus project-owned meshes and materials under Assets/IKEA_EEG/Art/JapaneseCity.
    /// The source JPGs are read, never modified. "Remove" deletes the Backdrops object and
    /// changes nothing else.
    ///
    /// ONLY SOUTH IS ACTIVE. North / East / West sat behind the storefront and the side
    /// buildings and added nothing visible from Spawn_A, so they are not built. Their JPGs,
    /// materials and meshes are kept for rollback: add the names back to k_ActiveDirections
    /// and run "Build Skyline Backdrops".
    /// </summary>
    public static class EncodingSkylineBackdrops
    {
        const string k_ScenePath = "Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaA.unity";
        const string k_ArtFolder = "Assets/IKEA_EEG/Art/JapaneseCity";
        const string k_TextureFolder = k_ArtFolder + "/Backdrops";
        const string k_MaterialFolder = k_ArtFolder + "/Materials";
        const string k_MeshFolder = k_ArtFolder + "/Meshes";
        const string k_UnlitShaderName = "Universal Render Pipeline/Unlit";

        const string k_GroupName = "Backdrops";

        static readonly (string name, float azimuth)[] k_Directions =
        {
            ("North", 0f), ("East", 90f), ("South", 180f), ("West", 270f),
        };

        // The directions that are built and expected by Validate. The others stay as assets only.
        static readonly string[] k_ActiveDirections = { "South" };

        static IEnumerable<(string name, float azimuth)> ActiveDirections =>
            k_Directions.Where(d => k_ActiveDirections.Contains(d.name));
        static IEnumerable<string> InactiveDirections =>
            k_Directions.Select(d => d.name).Where(n => !k_ActiveDirections.Contains(n));

        static string TexturePath(string dir) => $"{k_TextureFolder}/JC_Skyline_{dir}.jpg";
        static string MaterialPath(string dir) => $"{k_MaterialFolder}/JC_Skyline_{dir}.mat";
        static string MeshPath(string dir) => $"{k_MeshFolder}/JC_Skyline_{dir}.asset";

        // ---- placement -------------------------------------------------------------------------
        // Spawn_A eye. Spawn_A is the only participant position in Area A.
        static readonly Vector3 k_Eye = new Vector3(0f, 1.60f, -0.70f);

        // Distance of the ring in front of and behind the participant. The 3D surround ends at
        // about 88 m (z = -88), so 150 m keeps the backdrop well behind every building.
        const float k_RadiusM = 150f;

        // HARD LIMIT, as in JapaneseCityEnvironmentBuilder: Area 0 (x = -100) is in this scene and
        // Area B (x = +100) is loaded beside it during the room switch, so nothing may reach
        // past |x| = 88. The ring is flat at x = +/-87 where the circle would cross that.
        const float k_MaxAbsX = 87f;

        // Each image covers 90 degrees of azimuth across its width; rows use the same angle
        // per pixel. Row 700 of 1024 is the haze line where the painted city meets the ground.
        const float k_ImageAzimuthDeg = 90f;
        const int k_HorizonRow = 700;

        // ---- silhouette tracing ------------------------------------------------------------------
        const int k_ColumnPx = 4;            // one mesh column per 4 image pixels (~0.09 degrees)
        const int k_MaxRunColumns = 16;      // merged equal-height columns stay under ~1.4 degrees
        const int k_EdgeThreshold = 6;       // 0-255, any channel, vs the sky 3 rows above
        const int k_EdgePersistRows = 4;     // a building top must hold for 4 rows (JPEG noise)
        const int k_SpikeRows = 8;           // isolated columns this far above their neighbours are noise

        // ---- import --------------------------------------------------------------------------
        const int k_MaxTextureSize = 2048;   // 2048 px per 90 degrees ~ 23 texels/degree (Quest 2 ~ 20 px/degree)

        // =====================================================================================
        // Entry points
        // =====================================================================================

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Backdrops: Build Skyline Backdrops", false, 240)]
        public static void BuildMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"[IKEA_EEG] Open {k_ScenePath} first.");
                return;
            }
            var sb = new StringBuilder();
            if (Build(scene, sb)) EditorSceneManager.MarkSceneDirty(scene);
            sb.Append(Validate(scene));
            Debug.Log(sb + "\nSave the scene to keep it.");
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Backdrops: Remove Skyline Backdrops", false, 241)]
        public static void RemoveMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"[IKEA_EEG] Open {k_ScenePath} first.");
                return;
            }
            if (Remove(scene)) EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[IKEA_EEG] Skyline backdrops removed from the scene (assets kept). Save the scene to keep it.");
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Backdrops: Validate", false, 242)]
        public static void ValidateMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            Debug.Log(scene.IsValid() && scene.isLoaded ? Validate(scene) : $"[IKEA_EEG] {k_ScenePath} is not open.");
        }

        /// <summary>Batch: build, save the Area A scene ONLY, reopen and validate.</summary>
        public static void BuildFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            var sb = new StringBuilder();
            if (!Build(scene, sb))
            {
                Debug.LogError(sb.ToString());
                EditorApplication.Exit(2);
                return;
            }

            // The scene ONLY. Never AssetDatabase.SaveAssets(): it rewrites vendor materials.
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            sb.Append(Validate(scene));
            sb.Append(JapaneseCityEnvironmentBuilder.ValidateScene(scene));
            Debug.Log(sb.ToString());
        }

        /// <summary>Batch: delete the Backdrops object and save the Area A scene.</summary>
        public static void RemoveFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            if (Remove(scene))
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            Debug.Log("[IKEA_EEG] Skyline backdrops removed.");
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Backdrops: Remove Inactive Directions", false, 243)]
        public static void PruneMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError($"[IKEA_EEG] Open {k_ScenePath} first.");
                return;
            }
            if (Prune(scene) > 0) EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log(Validate(scene) + "\nSave the scene to keep it.");
        }

        /// <summary>
        /// Batch: delete only the Backdrop_* objects whose direction is not in k_ActiveDirections,
        /// save the Area A scene ONLY, reopen and validate. Active backdrops are left untouched.
        /// </summary>
        public static void PruneFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            var removed = Prune(scene);
            if (removed > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            }
            Debug.Log($"[IKEA_EEG] Removed {removed} inactive backdrop object(s).\n" + Validate(scene) +
                      JapaneseCityEnvironmentBuilder.ValidateScene(scene));
        }

        /// <summary>Batch: validate only. Saves nothing.</summary>
        public static void ValidateFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);
            Debug.Log(Validate(scene));
        }

        // =====================================================================================
        // Build
        // =====================================================================================

        static GameObject FindRoot(Scene scene) =>
            scene.GetRootGameObjects().FirstOrDefault(g => g.name == JapaneseCityEnvironmentBuilder.RootName);

        static bool Remove(Scene scene)
        {
            var group = FindRoot(scene)?.transform.Find(k_GroupName);
            if (group == null) return false;
            Object.DestroyImmediate(group.gameObject);
            return true;
        }

        static int Prune(Scene scene)
        {
            var group = FindRoot(scene)?.transform.Find(k_GroupName);
            if (group == null) return 0;
            var removed = 0;
            foreach (var dir in InactiveDirections)
            {
                Transform t;
                while ((t = group.Find("Backdrop_" + dir)) != null)
                {
                    Object.DestroyImmediate(t.gameObject);
                    removed++;
                }
            }
            return removed;
        }

        static bool Build(Scene scene, StringBuilder sb)
        {
            sb.AppendLine("[IKEA_EEG] ===== SKYLINE BACKDROPS: BUILD =====");

            var root = FindRoot(scene);
            if (root == null)
            {
                sb.AppendLine($"  FAIL  {JapaneseCityEnvironmentBuilder.RootName} not found. Nothing was changed.");
                return false;
            }

            var missing = ActiveDirections.Where(d => !File.Exists(TexturePath(d.name))).ToList();
            if (missing.Count > 0)
            {
                sb.AppendLine($"  FAIL  missing source image(s): {string.Join(", ", missing.Select(d => TexturePath(d.name)))}. Nothing was changed.");
                return false;
            }

            var shader = Shader.Find(k_UnlitShaderName);
            if (shader == null)
            {
                sb.AppendLine($"  FAIL  shader '{k_UnlitShaderName}' not found. Nothing was changed.");
                return false;
            }

            Remove(scene);
            var group = new GameObject(k_GroupName).transform;
            group.SetParent(root.transform, false);

            foreach (var (dir, azimuth) in ActiveDirections)
            {
                var texture = ImportTexture(TexturePath(dir), sb);
                var material = SaveMaterial(dir, shader, texture);

                var tops = TraceSilhouette(TexturePath(dir), out var width, out var height);
                var mesh = BuildMesh(dir, azimuth, tops, width, height, group);

                var go = new GameObject("Backdrop_" + dir);
                go.transform.SetParent(group, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;

                var topElev = (k_HorizonRow - tops.Min()) * k_ImageAzimuthDeg / width;
                sb.AppendLine($"  ok    Backdrop_{dir}: {mesh.vertexCount} vertices, {mesh.triangles.Length / 3} triangles, " +
                              $"skyline up to {topElev:0.0} deg above the horizon");
            }
            return true;
        }

        static Texture2D ImportTexture(string path, StringBuilder sb)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            importer.npotScale = TextureImporterNPOTScale.ToNearest;   // 4128 x 1024 -> 4096 x 1024, then Max Size
            importer.isReadable = false;
            importer.mipmapEnabled = true;                              // seen at a distance, minified
            importer.streamingMipmaps = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 1;                                    // viewed face-on
            importer.maxTextureSize = k_MaxTextureSize;
            importer.textureCompression = TextureImporterCompression.Compressed;
            importer.crunchedCompression = false;
            importer.SaveAndReimport();

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            sb.AppendLine($"  ok    {Path.GetFileName(path)}: imported {texture.width} x {texture.height} {texture.format}, " +
                          $"{texture.mipmapCount} mip(s)");
            return texture;
        }

        static Material SaveMaterial(string dir, Shader shader, Texture2D texture)
        {
            var path = MaterialPath(dir);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetTexture("_BaseMap", texture);
            material.mainTexture = texture;
            material.SetColor("_BaseColor", Color.white);
            material.enableInstancing = false;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        /// <summary>
        /// For each k_ColumnPx-wide column of the image, the first row (from the top) that belongs
        /// to a building. Reads the JPG file itself, at full resolution.
        ///   1. 3 x 3 box mean, so single JPEG block artefacts in the painted sky do not count.
        ///   2. Per pixel: the first row that differs from the sky three rows above it by more
        ///      than k_EdgeThreshold, for k_EdgePersistRows rows in a row.
        ///   3. Per column: the second-highest of its pixels (two must agree).
        ///   4. A column more than k_SpikeRows above the median of its +/-2 neighbours drops to
        ///      that median (isolated noise; real buildings are wider than 3 columns).
        /// Columns with no building above the horizon get the horizon row.
        /// </summary>
        static int[] TraceSilhouette(string path, out int width, out int height)
        {
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            image.LoadImage(File.ReadAllBytes(path));
            width = image.width;
            height = image.height;
            var px = image.GetPixels32();
            Object.DestroyImmediate(image);

            int w = width, h = height;
            var rows = Mathf.Min(h, k_HorizonRow + k_EdgePersistRows + 1);

            // Box-filtered colour of rows 0..rows-1 (row 0 = top of the image).
            var mean = new Vector3[w * rows];
            for (var row = 0; row < rows; row++)
                for (var x = 0; x < w; x++)
                {
                    var sum = Vector3.zero;
                    var n = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = row + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            var c = px[(h - 1 - yy) * w + xx];
                            sum += new Vector3(c.r, c.g, c.b);
                            n++;
                        }
                    mean[row * w + x] = sum / n;
                }

            bool Differs(int x, int rowA, int rowB)
            {
                var d = mean[rowA * w + x] - mean[rowB * w + x];
                return Mathf.Abs(d.x) > k_EdgeThreshold || Mathf.Abs(d.y) > k_EdgeThreshold || Mathf.Abs(d.z) > k_EdgeThreshold;
            }

            var raw = new int[(w + k_ColumnPx - 1) / k_ColumnPx];
            var pixelTops = new List<int>(k_ColumnPx);
            for (var c = 0; c < raw.Length; c++)
            {
                pixelTops.Clear();
                for (var x = c * k_ColumnPx; x < Mathf.Min(w, (c + 1) * k_ColumnPx); x++)
                {
                    var top = k_HorizonRow;
                    for (var row = 3; row < k_HorizonRow; row++)
                    {
                        var hit = true;
                        for (var k = 0; k < k_EdgePersistRows && hit; k++)
                            hit = Differs(x, row + k, row - 3);
                        if (!hit) continue;
                        top = row;
                        break;
                    }
                    pixelTops.Add(top);
                }
                pixelTops.Sort();
                raw[c] = pixelTops[Mathf.Min(1, pixelTops.Count - 1)];
            }

            var tops = (int[])raw.Clone();
            var window = new List<int>(5);
            for (var c = 0; c < raw.Length; c++)
            {
                window.Clear();
                for (var k = -2; k <= 2; k++)
                    if (c + k >= 0 && c + k < raw.Length) window.Add(raw[c + k]);
                window.Sort();
                var median = window[window.Count / 2];
                if (raw[c] < median - k_SpikeRows) tops[c] = median;
            }
            return tops;
        }

        /// <summary>
        /// The strip as runs of equal-height columns. Each run is a polygon from the image bottom
        /// to its own top, including the neighbours' top corners where they fall on its sides (no
        /// T-junctions, so no cracks), fanned around a centre vertex.
        /// </summary>
        static Mesh BuildMesh(string dir, float azimuth, int[] tops, int width, int height, Transform space)
        {
            var degPerPx = k_ImageAzimuthDeg / width;
            var leftAzimuth = azimuth - k_ImageAzimuthDeg * 0.5f;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            int Vertex(float pxX, float row)
            {
                var a = (leftAzimuth + pxX * degPerPx) * Mathf.Deg2Rad;
                var e = (k_HorizonRow - row) * degPerPx * Mathf.Deg2Rad;
                var horizontal = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var r = RingRadius(horizontal);
                var world = k_Eye + horizontal * r + Vector3.up * (r * Mathf.Tan(e));
                vertices.Add(space.InverseTransformPoint(world));
                uvs.Add(new Vector2(pxX / width, 1f - row / height));
                return vertices.Count - 1;
            }

            var c0 = 0;
            while (c0 < tops.Length)
            {
                var c1 = c0 + 1;
                while (c1 < tops.Length && tops[c1] == tops[c0] && c1 - c0 < k_MaxRunColumns) c1++;

                float xL = c0 * k_ColumnPx, xR = Mathf.Min(width, c1 * k_ColumnPx);
                var top = tops[c0];
                var outline = new List<(float x, float row)> { (xL, height), (xR, height) };
                if (c1 < tops.Length && tops[c1] > top) outline.Add((xR, tops[c1]));     // lower right neighbour
                outline.Add((xR, top));
                outline.Add((xL, top));
                if (c0 > 0 && tops[c0 - 1] > top) outline.Add((xL, tops[c0 - 1]));       // lower left neighbour

                var centre = Vertex((xL + xR) * 0.5f, (top + height) * 0.5f);
                var ring = outline.Select(p => Vertex(p.x, p.row)).ToList();
                for (var i = 0; i < ring.Count; i++)
                    AddFacingEye(triangles, vertices, space, centre, ring[i], ring[(i + 1) % ring.Count]);

                c0 = c1;
            }

            var path = MeshPath(dir);
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = new Mesh();
                AssetDatabase.CreateAsset(mesh, path);
            }
            mesh.Clear();
            mesh.name = Path.GetFileNameWithoutExtension(path);
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            AssetDatabase.SaveAssetIfDirty(mesh);
            return mesh;
        }

        /// <summary>Unity draws clockwise-from-the-viewer triangles; orient each toward the eye.</summary>
        static void AddFacingEye(List<int> triangles, List<Vector3> v, Transform space, int a, int b, int c)
        {
            var n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (n.sqrMagnitude < 1e-10f) return;
            var toEye = space.InverseTransformPoint(k_Eye) - v[a];
            if (Vector3.Dot(n, toEye) >= 0f) triangles.AddRange(new[] { a, b, c });
            else triangles.AddRange(new[] { a, c, b });
        }

        /// <summary>k_RadiusM, except where that would cross |x| = k_MaxAbsX.</summary>
        static float RingRadius(Vector3 horizontal)
        {
            var sx = Mathf.Abs(horizontal.x);
            var r = k_RadiusM;
            if (sx > 1e-4f) r = Mathf.Min(r, (k_MaxAbsX - Mathf.Abs(k_Eye.x)) / sx);
            return r;
        }

        // =====================================================================================
        // Validate (static only)
        // =====================================================================================

        public static string Validate(Scene scene)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== SKYLINE BACKDROPS VALIDATION =====");
            var problems = 0;

            var group = FindRoot(scene)?.transform.Find(k_GroupName);
            if (group == null)
            {
                sb.AppendLine($"  FAIL  {JapaneseCityEnvironmentBuilder.RootName}/{k_GroupName} not found.");
                return sb.ToString();
            }

            var renderers = group.GetComponentsInChildren<MeshRenderer>(true);
            problems += Expect(sb, $"backdrop renderers ({string.Join(", ", k_ActiveDirections)})",
                renderers.Length, k_ActiveDirections.Length);
            foreach (var dir in InactiveDirections)
                problems += Expect(sb, $"Backdrop_{dir} objects (inactive direction, assets kept for rollback)",
                    group.Cast<Transform>().Count(c => c.name == "Backdrop_" + dir), 0);

            var allowed = new[] { typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer) };
            var foreign = group.GetComponentsInChildren<Component>(true)
                .Where(c => c == null || !allowed.Contains(c.GetType())).ToList();
            problems += Expect(sb, "components other than Transform / MeshFilter / MeshRenderer", foreign.Count, 0);

            var eyeXZ = new Vector2(k_Eye.x, k_Eye.z);
            foreach (var (dir, _) in ActiveDirections)
            {
                var t = group.Find("Backdrop_" + dir);
                var r = t != null ? t.GetComponent<MeshRenderer>() : null;
                var f = t != null ? t.GetComponent<MeshFilter>() : null;
                if (r == null || f == null || f.sharedMesh == null)
                {
                    sb.AppendLine($"  FAIL  Backdrop_{dir}: missing renderer or mesh");
                    problems++;
                    continue;
                }

                var m = r.sharedMaterial;
                var tex = m != null ? m.GetTexture("_BaseMap") as Texture2D : null;
                var importer = tex != null ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter : null;
                var okMaterial = m != null && m.shader != null && m.shader.name == k_UnlitShaderName && m.shader.isSupported &&
                                 AssetDatabase.GetAssetPath(m) == MaterialPath(dir) &&
                                 tex != null && AssetDatabase.GetAssetPath(tex) == TexturePath(dir) &&
                                 m.renderQueue <= (int)RenderQueue.GeometryLast;
                var okImport = importer != null && importer.maxTextureSize == k_MaxTextureSize && importer.mipmapEnabled &&
                               importer.textureCompression != TextureImporterCompression.Uncompressed &&
                               importer.alphaSource == TextureImporterAlphaSource.None;
                var okRenderer = r.shadowCastingMode == ShadowCastingMode.Off && !r.receiveShadows &&
                                 r.lightProbeUsage == LightProbeUsage.Off && r.reflectionProbeUsage == ReflectionProbeUsage.Off;

                var world = f.sharedMesh.vertices.Select(v => t.TransformPoint(v)).ToArray();
                var dist = world.Select(p => Vector2.Distance(new Vector2(p.x, p.z), eyeXZ)).ToArray();
                var maxAbsX = world.Max(p => Mathf.Abs(p.x));

                problems += Report(sb, okMaterial && okImport && okRenderer,
                    $"Backdrop_{dir}: {m?.name} ({m?.shader?.name}, opaque), " +
                    $"{(tex != null ? $"{tex.width} x {tex.height} {tex.format}, {tex.mipmapCount} mips" : "no texture")}, " +
                    $"no shadows / probes; {f.sharedMesh.vertexCount} vertices; " +
                    $"{dist.Min():0}-{dist.Max():0} m from the eye, y {world.Min(p => p.y):0.0}..{world.Max(p => p.y):0.0}");
                problems += Report(sb, maxAbsX <= k_MaxAbsX + 0.01f,
                    $"Backdrop_{dir}: |x| <= {k_MaxAbsX:0} m (max {maxAbsX:0.0})");
            }

            // Keep-out volumes (Area 0, Area B, Area C, the storefront / plaza / road).
            foreach (var (name, box) in JapaneseCityEnvironmentBuilder.k_KeepOut)
            {
                var hits = renderers.Count(r => r.bounds.Intersects(box));
                problems += Expect(sb, $"backdrops inside the {name} keep-out", hits, 0);
            }

            // Every active surround building must stand in front of the backdrop.
            // (The ground plates are excluded: they may run on under the backdrop, which hides them.)
            var ground = FindRoot(scene).transform.Find("Ground");
            var buildings = FindRoot(scene).GetComponentsInChildren<MeshRenderer>(false)
                .Where(r => r.enabled && !r.transform.IsChildOf(group) && (ground == null || !r.transform.IsChildOf(ground)))
                .ToList();
            var behind = 0;
            var nearestGap = float.MaxValue;
            foreach (var b in buildings)
            {
                var bb = b.bounds;
                foreach (var corner in new[]
                         {
                             new Vector2(bb.min.x, bb.min.z), new Vector2(bb.max.x, bb.min.z),
                             new Vector2(bb.min.x, bb.max.z), new Vector2(bb.max.x, bb.max.z),
                         })
                {
                    var d = corner - eyeXZ;
                    if (d.sqrMagnitude < 1e-6f) continue;
                    var ring = RingRadius(new Vector3(d.x, 0f, d.y).normalized);
                    nearestGap = Mathf.Min(nearestGap, ring - d.magnitude);
                    if (d.magnitude > ring) { behind++; break; }
                }
            }
            problems += Expect(sb, "active surround renderers with a footprint corner behind the backdrop ring", behind, 0);
            sb.AppendLine($"  info  smallest gap between a surround footprint corner and the ring: {nearestGap:0.0} m");
            var sceneRenderers = scene.GetRootGameObjects()
                .SelectMany(g => g.GetComponentsInChildren<Renderer>(false)).Count(r => r.enabled);
            sb.AppendLine($"  info  active, enabled renderers in the whole Area A scene: {sceneRenderers}");

            sb.AppendLine(problems == 0
                ? "  RESULT: PASS (static check only — not a Play Mode or headset test)"
                : $"  RESULT: {problems} problem(s)");
            return sb.ToString();
        }

        static int Expect(StringBuilder sb, string what, int actual, int expected)
        {
            var ok = actual == expected;
            sb.AppendLine($"  {(ok ? "ok  " : "FAIL")}  {what}: {actual} (expected {expected})");
            return ok ? 0 : 1;
        }

        static int Report(StringBuilder sb, bool ok, string message)
        {
            sb.AppendLine($"  {(ok ? "ok  " : "FAIL")}  {message}");
            return ok ? 0 : 1;
        }

        // =====================================================================================
        // Capture (saves nothing)
        // =====================================================================================

        /// <summary>
        /// Batch: renders Bootstrap + Area A (Bootstrap active, as at runtime) from the Spawn_A
        /// eye in eight directions, two off-spawn positions, and the Area 0 spawn in four
        /// directions (the backdrop must not show inside the practice room). -captureOutput dir.
        /// </summary>
        public static void CaptureFromCommandLine()
        {
            var args = System.Environment.GetCommandLineArgs();
            var folder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Temp", "SkylineBackdrops");
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-captureOutput") folder = args[i + 1];

            var bootstrap = EditorSceneManager.OpenScene(ExperimentSceneBuilder.BootstrapScenePath, OpenSceneMode.Single);
            var areaA = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(bootstrap);

            var views = new List<(string name, Vector3 pos, Vector3 forward, float fov)>();
            for (var i = 0; i < 8; i++)
            {
                var a = i * 45f * Mathf.Deg2Rad;
                views.Add(($"A{i:0}_Eye_{i * 45:000}", k_Eye, new Vector3(Mathf.Sin(a), Mathf.Tan(4f * Mathf.Deg2Rad), Mathf.Cos(a)), 70f));
            }
            views.Add(("B1_Plaza_ThreeQuarter", new Vector3(-11f, 2.10f, -9f), new Vector3(11f, 0.5f, 10.9f), 70f));
            views.Add(("B2_Road_Back", new Vector3(0f, 1.75f, -19f), new Vector3(0f, 0.05f, -1f), 70f));

            var spawn0 = areaA.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true))
                .FirstOrDefault(t => t.name == "Spawn_0");
            if (spawn0 != null)
            {
                var eye0 = spawn0.position + Vector3.up * 1.6f;
                for (var i = 0; i < 4; i++)
                {
                    var a = i * 90f * Mathf.Deg2Rad;
                    views.Add(($"C{i}_Area0_{i * 90:000}", eye0, new Vector3(Mathf.Sin(a), 0.15f, Mathf.Cos(a)), 90f));
                }
            }

            Directory.CreateDirectory(folder);
            var host = new GameObject("~SkylineBackdropCapture") { hideFlags = HideFlags.HideAndDontSave };
            const int width = 1600, height = 900;
            try
            {
                var camera = host.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 1000f;   // the XR camera's far clip
                camera.enabled = false;

                foreach (var (name, pos, forward, fov) in views)
                {
                    host.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(forward.normalized, Vector3.up));
                    camera.fieldOfView = fov;
                    var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                    var previous = RenderTexture.active;
                    camera.targetTexture = rt;
                    camera.Render();
                    RenderTexture.active = rt;
                    var image = new Texture2D(width, height, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    image.Apply();
                    RenderTexture.active = previous;
                    camera.targetTexture = null;
                    File.WriteAllBytes(Path.Combine(folder, name + ".png"), image.EncodeToPNG());
                    Object.DestroyImmediate(image);
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
            Debug.Log($"[IKEA_EEG] Captured {views.Count} view(s) to {folder}" +
                      (spawn0 == null ? " (Spawn_0 not found: no Area 0 views)" : ""));
        }
    }
}
