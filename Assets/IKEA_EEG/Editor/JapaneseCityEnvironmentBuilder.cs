using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using IkeaEeg.XR;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Builds the decorative Japanese-city surround for the Encoding environment (Area A) out of
    /// TWO groups of the vendor "Japanese Otaku City" model: PQ_Remake_AKIHABARA/Block_H and
    /// PQ_Remake_AKIHABARA/Block_I. Nothing else from that package is referenced — not the
    /// train, roads, ground, cars, characters or demo systems.
    ///
    /// WHY NOT DRAG THE FBX CHILDREN INTO THE SCENE: a child of a model asset can only be
    /// instantiated as part of the WHOLE model prefab (2,700+ meshes), and disabling the rest
    /// still leaves the full city serialized in the scene. Instead this builder writes
    /// project-owned prefabs whose children are plain MeshFilter + MeshRenderer objects that
    /// reference the FBX's mesh sub-assets by GUID. The FBX, its .meta and every vendor .mat
    /// are read, never written.
    ///
    /// WHY NEW MATERIALS: the vendor materials use "Legacy Shaders/Diffuse", which URP cannot
    /// draw (magenta). Each one actually used by Block_H / Block_I gets a URP "Simple Lit" copy
    /// that points at the SAME vendor texture with the same colour and tiling. The copies live
    /// under Assets/IKEA_EEG/Art/JapaneseCity — deliberately not under Assets/IKEA_EEG/Materials,
    /// which ExperimentSelfTest snapshots and restores material by material.
    ///
    /// WHAT THIS IS NOT: no MonoBehaviour, Collider, Light, Canvas or interactable is created.
    /// The scene part adds ONE top-level root (Encoding_JapaneseEnvironment) to
    /// IKEA_EEG_AreaA.unity and touches no existing object, so deleting that root removes the
    /// whole surround — buildings and ground — and changes nothing else.
    ///
    /// IDEMPOTENT: re-running updates the material copies, prefabs and ground meshes in place
    /// (same GUIDs) and replaces the scene root.
    /// </summary>
    public static class JapaneseCityEnvironmentBuilder
    {
        public const string RootName = "Encoding_JapaneseEnvironment";

        const string k_FbxPath = "Assets/ZRNAssets/005339_08932_25_14/Models/PQ_Remake_AKIHABARA.fbx";
        const string k_ScenePath = "Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaA.unity";
        const string k_ArtFolder = "Assets/IKEA_EEG/Art/JapaneseCity";
        const string k_MaterialFolder = k_ArtFolder + "/Materials";
        const string k_PrefabFolder = k_ArtFolder + "/Prefabs";
        const string k_MeshFolder = k_ArtFolder + "/Meshes";
        const string k_UrpShaderName = "Universal Render Pipeline/Simple Lit";

        const string k_BlockH = "Block_H";
        const string k_BlockI = "Block_I";

        /// <summary>Must never be referenced. It is a top-level node, not a child of either block;
        /// the name check below is a second line of defence, not the mechanism.</summary>
        const string k_ExcludedName = "005339_08932_train";

        // ---- prefabs ---------------------------------------------------------------------
        const string k_PrefabH = "JC_Block_H";
        const string k_PrefabI = "JC_Block_I";
        const string k_PrefabInfill = "JC_Infill_H_EastEnd";

        /// <summary>
        /// The infill: the five buildings at the south end of Block_H's east cluster (raw FBX
        /// X 390-453, Z -562 to -528 — one 29.5 m tower and four 12-17 m buildings). Chosen
        /// because, in this layout, that part of both complete H copies stands 150-200 m out
        /// and is hidden behind the I blocks, so the duplicates cannot be seen next to their
        /// originals.
        /// </summary>
        static readonly string[] k_InfillChildren =
        {
            "005339_08932_-10304001",
            "005339_08932_-10589601",
            "005339_08932_-9029101",
            "005339_08932_-9894101",
            "005339_08932_-9895901",
        };

        // ---- ground ----------------------------------------------------------------------
        const string k_GroundGroup = "Ground";
        const string k_GroundExtensionName = "Ground_Extension";
        const string k_SidewalksName = "Sidewalks";
        const string k_GroundExtensionMeshPath = k_MeshFolder + "/JC_Ground_Extension.asset";
        const string k_SidewalkMeshPath = k_MeshFolder + "/JC_Sidewalks.asset";
        const string k_SidewalkMaterialPath = k_MaterialFolder + "/JC_Ground_Sidewalk.mat";

        /// <summary>
        /// The Area A far ground (x +/-160, z -200..20). The extension continues it with the
        /// SAME material, so the join cannot be seen. Read, never written.
        /// </summary>
        const string k_AsphaltMaterialPath = "Assets/IKEA_EEG/Materials/M_AreaA_Asphalt.mat";
        const string k_GroundBaseName = "Ground_Base";

        const float k_GroundMargin = 40f;      // ground beyond the outermost building
        const float k_GroundOverlap = 1f;      // tiles tuck this far under Ground_Base (same material)
        const float k_PadJoin = 3f;            // buildings closer than this share one sidewalk pad
        const float k_PadMargin = 1.5f;        // pavement around each building cluster
        const float k_PadTopY = 0.12f;         // a kerb height, like Area A's own kerb (0.135)
        const float k_PadBottomY = -0.10f;

        // =================================================================================
        // Scale. The FBX declares centimetres (UnitScaleFactor 1) and its importer uses Scale
        // Factor 0.1 with Convert Units on, so the imported size cannot be trusted from the
        // settings alone. It is MEASURED instead: these are the raw FBX-file extents of each
        // block (union of child bounding boxes, parsed from the file), and the imported prefab
        // is compared against them.
        //
        // One raw FBX unit reads as one metre: the commonest building heights are 7.3 (two
        // storeys) and 14.1 (four storeys).
        // =================================================================================

        const float k_RawWidthH = 331.90f;   // Block_H X extent in the FBX file
        const float k_RawWidthI = 103.55f;   // Block_I X extent in the FBX file

        // HARD LIMIT: Area 0 (x = -100) is in THIS scene and Area B (x = +100) is loaded beside
        // it during the room switch. A backdrop reaching into those rooms is exactly the
        // 2026-09-28 "negative-X does not render" fault, so the lateral blocks must stay inside
        // |x| <= 88 near z = 0 — the same reasoning that stopped the old Skyline volumes at 90.
        // Block_I is 103.6 m wide at 1:1, which does not fit between x = 22 and x = 88, so the
        // whole surround is scaled by ONE uniform factor computed below (about 0.64).
        const float k_LateralInnerX = 22.0f;  // 8 m clear of the storefront's x = +/-14
        const float k_LateralMaxX = 88.0f;

        /// <summary>Volumes nothing may enter, in world space.</summary>
        internal static readonly (string name, Bounds box)[] k_KeepOut =
        {
            ("Area A storefront, plaza and road", Box(-16f, -1f, -28f, 16f, 8f, 4f)),
            ("Area 0 practice room (x = -100)",   Box(-106f, -1f, -10f, -94f, 8f, 10f)),
            ("Area B showroom (x = +100)",        Box(90f, -1f, -15f, 110f, 10f, 15f)),
            ("Area C exit lounge (x = +200)",     Box(190f, -1f, -15f, 210f, 10f, 15f)),
        };

        // Spawn_A eye, and the other areas the surround must hide (the job the removed grey
        // Skyline used to do). Same points as AreaAEnvironmentBuilder's sightline tests.
        static readonly Vector3 k_Eye = new Vector3(0f, 1.60f, -0.70f);
        static readonly (string name, Vector3 target)[] k_Sightlines =
        {
            ("Area 0", new Vector3(-100f, 1.5f, 0f)),
            ("Area B", new Vector3(100f, 1.8f, 0f)),
            ("Area C", new Vector3(200f, 2.1f, 0f)),
        };

        // =================================================================================
        // Layout — world metres. Area_A_Entrance sits at the world origin; the participant
        // stands at z = -0.70 facing +Z, the storefront spans x = +/-14 at z = 1.8 and the road
        // runs behind the participant to z = -26.
        //
        //                 H_North (west strip directly behind the storefront)
        //        I_West ┐ [storefront]          ┌ I_East (shifted +15 m in z)
        //   (-15 m in z)│     @ Spawn_A         │
        //               │ [plaza / road]        │
        //   Infill_BackLeft   H_South (strip across the road)   Infill_BackRight
        //
        // WHY THIS AND NOT A PLAIN CROSS: complete Block_H is three groups — a dense 28 m wide
        // west strip, a ~100 m empty stretch with two towers at its far end, and a 44 m east
        // cluster. Centred on the participant, the empty stretch fell exactly in front of the
        // storefront and behind the road, so those directions showed no building at all. Each
        // H copy is now shifted sideways so its west strip — almost exactly the storefront's
        // width — stands directly behind the storefront (front) and across the road (back), and
        // turned so its empty stretch opens toward a diagonal the I blocks already screen.
        // The I blocks are staggered 15 m to cover the two remaining diagonals, and the two
        // small infill groups close the back corners the complete blocks cannot reach without
        // entering the other rooms.
        // =================================================================================

        enum Edge { Min, Centre, Max }

        struct Placement
        {
            public string parent;
            public string prefab;       // prefab file stem under k_PrefabFolder
            public string source;       // FBX group its meshes come from
            public float yaw;           // used when openSide == 0
            public int openSide;        // Block_H only: turn so its empty stretch opens toward world -X (-1) / +X (+1)
            public bool anchorStrip;    // X alignment on Block_H's west strip rather than the whole group
            public Edge edgeX;
            public float x;
            public Edge edgeZ;          // always on the whole group's bounds
            public float z;
            public string role;
        }

        static readonly Placement[] k_Placements =
        {
            new Placement
            {
                parent = "H_North", prefab = k_PrefabH, source = k_BlockH, openSide = -1, anchorStrip = true,
                edgeX = Edge.Centre, x = 0f, edgeZ = Edge.Min, z = 8f,
                role = "front: west strip directly behind the storefront, near edge 6 m behind the facade",
            },
            new Placement
            {
                parent = "H_South", prefab = k_PrefabH, source = k_BlockH, openSide = +1, anchorStrip = true,
                edgeX = Edge.Centre, x = 0f, edgeZ = Edge.Max, z = -32f,
                role = "back: west strip across the road (road ends at z = -26)",
            },
            new Placement
            {
                parent = "I_West", prefab = k_PrefabI, source = k_BlockI, yaw = 180f,
                edgeX = Edge.Max, x = -k_LateralInnerX, edgeZ = Edge.Centre, z = -15f,
                role = "left side and back-left",
            },
            new Placement
            {
                parent = "I_East", prefab = k_PrefabI, source = k_BlockI, yaw = 0f,
                edgeX = Edge.Min, x = k_LateralInnerX, edgeZ = Edge.Centre, z = 15f,
                role = "right side and front-right",
            },
            new Placement
            {
                parent = "Infill_BackRight", prefab = k_PrefabInfill, source = k_BlockH, yaw = 180f,
                edgeX = Edge.Min, x = k_LateralInnerX, edgeZ = Edge.Max, z = -36f,
                role = "back-right corner, 6 m behind I_East",
            },
            new Placement
            {
                parent = "Infill_BackLeft", prefab = k_PrefabInfill, source = k_BlockH, yaw = 0f,
                edgeX = Edge.Max, x = -k_LateralInnerX, edgeZ = Edge.Max, z = -66f,
                role = "back-left corner, 6 m behind I_West",
            },
        };

        static string PrefabPath(string prefab) => $"{k_PrefabFolder}/{prefab}.prefab";

        // =================================================================================
        // Menu entry points
        // =================================================================================

        [MenuItem("IKEA_EEG/Visuals/Japanese City/1. Build Block H + I Assets (materials, prefabs)", false, 220)]
        public static void BuildAssetsMenu()
        {
            var report = new StringBuilder();
            var ok = BuildAssets(report);
            Debug.Log(report.ToString());

            if (!ok)
                EditorUtility.DisplayDialog("IKEA_EEG", "Japanese City asset build failed. See the Console.", "OK");
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/2. Place Surround in Open Area A Scene", false, 221)]
        public static void PlaceMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);

            if (!scene.IsValid() || !scene.isLoaded)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    $"Open {k_ScenePath} first. Nothing was changed.", "OK");
                return;
            }

            var report = new StringBuilder();

            if (Place(scene, report))
            {
                EditorSceneManager.MarkSceneDirty(scene);
                report.AppendLine("[IKEA_EEG] The Area A scene is dirty — save it to keep the surround.");
            }

            report.Append(ValidateScene(scene));
            Debug.Log(report.ToString());
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/3. Validate Surround", false, 222)]
        public static void ValidateMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            Debug.Log(scene.IsValid() && scene.isLoaded
                ? ValidateScene(scene)
                : $"[IKEA_EEG] {k_ScenePath} is not open.");
        }

        [MenuItem("IKEA_EEG/Visuals/Japanese City/Remove Surround from Open Area A Scene", false, 240)]
        public static void RemoveMenu()
        {
            var scene = SceneManager.GetSceneByPath(k_ScenePath);
            var root = scene.IsValid() && scene.isLoaded ? FindRoot(scene) : null;

            if (root == null)
            {
                Debug.Log($"[IKEA_EEG] No {RootName} in the open Area A scene. Nothing was changed.");
                return;
            }

            Undo.DestroyObjectImmediate(root);
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[IKEA_EEG] {RootName} removed. Save the scene to keep the removal. The " +
                      "materials, prefabs and meshes under " + k_ArtFolder + " were left in place.");
        }

        /// <summary>
        /// Batch form of steps 1 + 2:
        /// Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   IkeaEeg.EditorTools.JapaneseCityEnvironmentBuilder.BuildAndPlaceFromCommandLine
        ///
        /// Opens IKEA_EEG_AreaA.unity ONLY, builds the assets, places the surround and SAVES
        /// that one scene. Regenerates nothing else.
        /// </summary>
        public static void BuildAndPlaceFromCommandLine()
        {
            var report = new StringBuilder();

            if (!BuildAssets(report))
            {
                Debug.LogError(report.ToString());
                EditorApplication.Exit(2);
                return;
            }

            var scene = EditorSceneManager.OpenScene(k_ScenePath, OpenSceneMode.Single);

            if (!Place(scene, report))
            {
                Debug.LogError(report.ToString());
                EditorApplication.Exit(3);
                return;
            }

            // The scene ONLY. Never AssetDatabase.SaveAssets() here — see SaveOwnAssets.
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            report.Append(ValidateScene(scene));
            report.AppendLine(AreaAEnvironmentBuilder.Validate());
            report.AppendLine(ExperimentSceneBuilder.ValidateRoomScene(ExperimentArea.AreaA));
            Debug.Log(report.ToString());
        }

        // =================================================================================
        // Step 1 — URP material copies and the block / infill prefabs
        // =================================================================================

        public static bool BuildAssets(StringBuilder report)
        {
            report.AppendLine("[IKEA_EEG] ===== JAPANESE CITY: BUILD ASSETS =====");

            var urp = Shader.Find(k_UrpShaderName);
            if (urp == null)
            {
                report.AppendLine($"  FAIL  shader '{k_UrpShaderName}' not found. Nothing was written.");
                return false;
            }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(k_FbxPath);
            if (model == null)
            {
                report.AppendLine($"  FAIL  could not load {k_FbxPath}. Nothing was written.");
                return false;
            }

            var blocks = new[] { k_BlockH, k_BlockI }
                .Select(b => (name: b, node: model.transform.Find(b)))
                .ToArray();

            foreach (var (name, node) in blocks)
            {
                if (node != null) continue;
                report.AppendLine($"  FAIL  '{name}' not found under the model root. Nothing was written.");
                return false;
            }

            var nodeH = blocks[0].node;
            var missingInfill = k_InfillChildren.Where(n => nodeH.Find(n) == null).ToList();
            if (missingInfill.Count > 0)
            {
                report.AppendLine($"  FAIL  infill building(s) not found under {k_BlockH}: " +
                                  $"{string.Join(", ", missingInfill)}. Nothing was written.");
                return false;
            }

            EnsureFolder("Assets/IKEA_EEG", "Art");
            EnsureFolder("Assets/IKEA_EEG/Art", "JapaneseCity");
            EnsureFolder(k_ArtFolder, "Materials");
            EnsureFolder(k_ArtFolder, "Prefabs");
            EnsureFolder(k_ArtFolder, "Meshes");

            var converted = new Dictionary<Material, Material>();
            var usedPaths = new Dictionary<string, Material>();
            var nonDiffuse = new List<string>();
            var nullSlots = 0;

            // ---- materials: only those actually on a Block_H / Block_I renderer ------------
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (_, node) in blocks)
                {
                    foreach (Transform child in node)
                    {
                        var mr = child.GetComponent<MeshRenderer>();
                        if (mr == null) continue;

                        foreach (var src in mr.sharedMaterials)
                        {
                            if (src == null) { nullSlots++; continue; }
                            if (converted.ContainsKey(src)) continue;

                            if (src.shader == null || src.shader.name != "Legacy Shaders/Diffuse")
                                nonDiffuse.Add($"{src.name} ({(src.shader == null ? "no shader" : src.shader.name)})");

                            converted[src] = ConvertMaterial(src, urp, usedPaths);
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            var sidewalk = SidewalkMaterial(urp);
            SaveOwnAssets(converted.Values.Cast<Object>().Append(sidewalk));

            report.AppendLine($"  ok    {converted.Count} URP Simple Lit material(s) written to {k_MaterialFolder}");
            report.AppendLine(nonDiffuse.Count == 0
                ? "  ok    every source material is Legacy Shaders/Diffuse (mapped 1:1 to opaque Simple Lit)"
                : $"  note  {nonDiffuse.Count} source material(s) are not Legacy Diffuse: {string.Join(", ", nonDiffuse)}");
            if (nullSlots > 0)
                report.AppendLine($"  WARN  {nullSlots} renderer slot(s) have no source material; they will stay empty (magenta).");
            report.AppendLine($"  ok    {k_SidewalkMaterialPath} (plain Simple Lit pavement grey, no texture)");

            // ---- prefabs -----------------------------------------------------------------
            // Built in a preview scene so nothing is ever parented into the open scene.
            var infillSet = new HashSet<string>(k_InfillChildren);
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                if (!BuildPrefab(k_PrefabH, blocks[0].node, null, converted, preview, report)) return false;
                if (!BuildPrefab(k_PrefabI, blocks[1].node, null, converted, preview, report)) return false;
                if (!BuildPrefab(k_PrefabInfill, nodeH, infillSet.Contains, converted, preview, report)) return false;
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }

            // SaveAsPrefabAsset has already written the prefabs.
            return true;
        }

        /// <summary>
        /// Saves the builder's own assets and NOTHING else.
        ///
        /// WHY NOT AssetDatabase.SaveAssets(): that writes every dirty asset in the project.
        /// Loading the FBX loads all of its vendor materials, and Unity marks some of them dirty
        /// on load (format upgrade 6 -> 8 and the Standard-shader emissive-flag fix-up). On
        /// 2026-10-14 a SaveAssets() here re-serialized three vendor TransP materials the
        /// surround does not even use. They had to be restored from the .unitypackage.
        /// </summary>
        static void SaveOwnAssets(IEnumerable<Object> assets)
        {
            foreach (var a in assets)
            {
                if (a != null && AssetDatabase.GetAssetPath(a).StartsWith(k_ArtFolder + "/"))
                    AssetDatabase.SaveAssetIfDirty(a);
            }
        }

        static Material ConvertMaterial(Material src, Shader urp, Dictionary<string, Material> usedPaths)
        {
            var path = $"{k_MaterialFolder}/{Sanitize(src.name)}_URP.mat";

            // Two different vendor materials with the same name must not share one copy.
            if (usedPaths.TryGetValue(path, out var owner) && owner != src)
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(src, out var guid, out long localId);
                path = $"{k_MaterialFolder}/{Sanitize(src.name)}_{guid.Substring(0, 8)}_{localId}_URP.mat";
            }
            usedPaths[path] = src;

            var dst = AssetDatabase.LoadAssetAtPath<Material>(path);
            var isNew = dst == null;
            if (isNew)
                dst = new Material(urp);
            else
                dst.shader = urp;

            dst.name = System.IO.Path.GetFileNameWithoutExtension(path);

            var tex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            var scale = src.HasProperty("_MainTex") ? src.GetTextureScale("_MainTex") : Vector2.one;
            var offset = src.HasProperty("_MainTex") ? src.GetTextureOffset("_MainTex") : Vector2.zero;
            var color = src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;

            foreach (var prop in new[] { "_BaseMap", "_MainTex" })
            {
                if (!dst.HasProperty(prop)) continue;
                dst.SetTexture(prop, tex);
                dst.SetTextureScale(prop, scale);
                dst.SetTextureOffset(prop, offset);
            }

            foreach (var prop in new[] { "_BaseColor", "_Color" })
                if (dst.HasProperty(prop)) dst.SetColor(prop, color);

            // Legacy Diffuse is pure Lambert. A black specular colour keeps Simple Lit
            // Lambert-equivalent even if the inspector later re-enables its specular keyword.
            if (dst.HasProperty("_SpecColor")) dst.SetColor("_SpecColor", Color.black);
            if (dst.HasProperty("_Smoothness")) dst.SetFloat("_Smoothness", 0f);

            ApplySurface(dst, src.shader == null ? "" : src.shader.name,
                src.HasProperty("_Cutoff") ? src.GetFloat("_Cutoff") : 0.5f);

            if (isNew)
                AssetDatabase.CreateAsset(dst, path);
            else
                EditorUtility.SetDirty(dst);

            return dst;
        }

        /// <summary>
        /// Plain pavement grey for the sidewalk pads under the building clusters — a shade
        /// darker than Area A's light plaza concrete so the storefront plaza stays the brightest
        /// ground surface, and clearly lighter than the asphalt streets between the pads.
        /// </summary>
        static Material SidewalkMaterial(Shader urp)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(k_SidewalkMaterialPath);
            var isNew = m == null;
            if (isNew) m = new Material(urp) { name = "JC_Ground_Sidewalk" };
            else m.shader = urp;

            var color = new Color(0.52f, 0.515f, 0.50f);
            foreach (var prop in new[] { "_BaseColor", "_Color" })
                if (m.HasProperty(prop)) m.SetColor(prop, color);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", Color.black);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
            ApplySurface(m, "", 0.5f);

            if (isNew) AssetDatabase.CreateAsset(m, k_SidewalkMaterialPath);
            else EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Opaque for Diffuse; the two legacy alpha variants only if a source uses them.</summary>
        static void ApplySurface(Material m, string legacyShader, float cutoff)
        {
            var cutout = legacyShader.Contains("Cutout");
            var transparent = !cutout && legacyShader.Contains("Transparent");

            SetIf(m, "_Surface", transparent ? 1f : 0f);
            SetIf(m, "_Blend", 0f);
            SetIf(m, "_AlphaClip", cutout ? 1f : 0f);
            SetIf(m, "_Cutoff", cutoff);
            SetIf(m, "_SrcBlend", transparent ? (float)BlendMode.SrcAlpha : (float)BlendMode.One);
            SetIf(m, "_DstBlend", transparent ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
            SetIf(m, "_SrcBlendAlpha", transparent ? (float)BlendMode.One : (float)BlendMode.One);
            SetIf(m, "_DstBlendAlpha", transparent ? (float)BlendMode.OneMinusSrcAlpha : (float)BlendMode.Zero);
            SetIf(m, "_ZWrite", transparent ? 0f : 1f);
            SetIf(m, "_Cull", (float)CullMode.Back);

            if (cutout) m.EnableKeyword("_ALPHATEST_ON"); else m.DisableKeyword("_ALPHATEST_ON");
            if (transparent) m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); else m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");

            m.SetOverrideTag("RenderType", transparent ? "Transparent" : cutout ? "TransparentCutout" : "Opaque");
            m.renderQueue = transparent ? (int)RenderQueue.Transparent
                : cutout ? (int)RenderQueue.AlphaTest
                : -1;
        }

        /// <param name="include">null = every child of the block (except the train name).</param>
        static bool BuildPrefab(string prefabName, Transform node, System.Func<string, bool> include,
            Dictionary<Material, Material> converted, Scene preview, StringBuilder report)
        {
            var root = new GameObject(prefabName);
            SceneManager.MoveGameObjectToScene(root, preview);

            var renderers = 0;
            var skipped = new List<string>();

            foreach (Transform child in node)
            {
                if (child.name == k_ExcludedName) { skipped.Add(child.name + " (excluded)"); continue; }
                if (include != null && !include(child.name)) continue;

                var mf = child.GetComponent<MeshFilter>();
                var mr = child.GetComponent<MeshRenderer>();

                if (mf == null || mf.sharedMesh == null || mr == null)
                {
                    skipped.Add(child.name + " (no mesh)");
                    continue;
                }

                if (child.childCount > 0)
                    skipped.Add(child.name + $" ({child.childCount} nested child(ren) not copied)");

                var go = new GameObject(child.name);
                go.transform.SetParent(root.transform, false);

                // The child's full transform in model space, including any root transform the
                // importer applied, so an instance of this prefab at the origin overlays an
                // instance of the full model exactly.
                go.transform.localPosition = child.position;
                go.transform.localRotation = child.rotation;
                go.transform.localScale = child.lossyScale;

                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;

                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mr.sharedMaterials
                    .Select(m => m != null && converted.TryGetValue(m, out var c) ? c : null)
                    .ToArray();

                ConfigureBackdropRenderer(go, r);
                renderers++;
            }

            var path = PrefabPath(prefabName);
            PrefabUtility.SaveAsPrefabAsset(root, path, out var saved);
            Object.DestroyImmediate(root);

            if (!saved)
            {
                report.AppendLine($"  FAIL  could not save {path}");
                return false;
            }

            report.AppendLine($"  ok    {path}: {renderers} renderer(s) from {node.name}" +
                              (include == null ? $" ({node.childCount} child(ren) in the FBX)" : " (named subset)"));
            if (skipped.Count > 0)
                report.AppendLine($"  note  skipped / partial: {string.Join(", ", skipped)}");

            return true;
        }

        /// <summary>
        /// Decorative backdrop on a Quest 2 budget: no shadow casting (towers up to ~50 m would
        /// otherwise throw shadows across the storefront the task is viewed in), and no probes —
        /// the scene has none.
        /// </summary>
        static void ConfigureBackdropRenderer(GameObject go, MeshRenderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;

            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic |
                                                       StaticEditorFlags.OccluderStatic |
                                                       StaticEditorFlags.OccludeeStatic);
        }

        // =================================================================================
        // Step 2 — placement in IKEA_EEG_AreaA.unity
        // =================================================================================

        public static bool Place(Scene scene, StringBuilder report)
        {
            report.AppendLine("[IKEA_EEG] ===== JAPANESE CITY: PLACE IN AREA A =====");

            var prefabs = new[] { k_PrefabH, k_PrefabI, k_PrefabInfill }
                .ToDictionary(p => p, p => AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(p)));

            if (prefabs.Values.Any(p => p == null))
            {
                report.AppendLine("  FAIL  prefabs are missing — run step 1 first. Nothing was changed.");
                return false;
            }

            var boundsH = MeasurePrefab(prefabs[k_PrefabH], out _);
            var boundsI = MeasurePrefab(prefabs[k_PrefabI], out _);

            // Imported units per raw FBX unit, measured on both blocks independently.
            var kH = boundsH.size.x / k_RawWidthH;
            var kI = boundsI.size.x / k_RawWidthI;
            report.AppendLine($"  info  measured import scale: Block_H {kH:0.#####}, Block_I {kI:0.#####} " +
                              "imported units per FBX unit");

            if (kH <= 0f || Mathf.Abs(kH - kI) > 0.01f * kH)
            {
                report.AppendLine("  FAIL  the two blocks disagree on import scale; the raw extents no " +
                                  "longer match this FBX. Nothing was changed.");
                return false;
            }

            // Metres per raw unit after fitting Block_I between the storefront and the rooms.
            var fit = Mathf.Min(1f, (k_LateralMaxX - k_LateralInnerX) / k_RawWidthI);
            var parentScale = fit / kH;
            report.AppendLine($"  info  layout scale {fit:0.###} m per FBX unit -> parent scale {parentScale:0.#####}");

            // Block_H's west strip and the side its empty stretch lies on, in prefab space.
            var strip = FindStrip(prefabs[k_PrefabH], out var stripCentreX, out var gapLo, out var gapHi);
            if (strip.Count == 0)
            {
                report.AppendLine("  FAIL  could not find Block_H's internal gap / west strip. Nothing was changed.");
                return false;
            }
            var gapDir = (gapLo + gapHi) * 0.5f > stripCentreX ? 1 : -1;
            report.AppendLine($"  info  Block_H west strip: {strip.Count} buildings; widest empty stretch " +
                              $"{(gapHi - gapLo) * parentScale:0.0} m, on the prefab's {(gapDir > 0 ? "+X" : "-X")} side");

            // ---- replace any previous root --------------------------------------------------
            var previous = FindRoot(scene);
            if (previous != null)
            {
                Undo.DestroyObjectImmediate(previous);
                report.AppendLine($"  info  replaced the previous {RootName}");
            }

            var root = new GameObject(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            var buildingBounds = new List<Bounds>();

            foreach (var p in k_Placements)
            {
                var prefab = prefabs[p.prefab];
                var bounds = MeasurePrefab(prefab, out var groundY);

                // yaw 0 keeps the prefab's +X on world +X; 180 sends it to world -X.
                var yaw = p.openSide == 0 ? p.yaw : (gapDir == p.openSide ? 0f : 180f);

                // The parent carries the placement; the vendor-derived prefab keeps the city
                // transforms untouched and is only offset so its footprint centre — at the
                // HIGHEST building base, so nothing floats above the ground — sits on the
                // parent's origin.
                var parent = new GameObject(p.parent).transform;
                parent.SetParent(root.transform, false);
                parent.localPosition = Vector3.zero;
                parent.localRotation = Quaternion.Euler(0f, yaw, 0f);
                parent.localScale = Vector3.one * parentScale;

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                instance.transform.SetParent(parent, false);
                instance.transform.localPosition = new Vector3(-bounds.center.x, -groundY, -bounds.center.z);
                instance.transform.localRotation = Quaternion.identity;
                instance.transform.localScale = Vector3.one;

                // Measure where it landed with the parent at the origin, then slide the parent.
                var whole = WorldBounds(parent, null);
                var anchor = p.anchorStrip ? WorldBounds(parent, strip) : whole;
                var dx = p.x - EdgeOf(anchor.min.x, anchor.center.x, anchor.max.x, p.edgeX);
                var dz = p.z - EdgeOf(whole.min.z, whole.center.z, whole.max.z, p.edgeZ);
                parent.localPosition = new Vector3(dx, 0f, dz);

                var placed = WorldBounds(parent, null);
                report.AppendLine($"  ok    {p.parent,-16} pos ({dx:0.00}, 0, {dz:0.00}) yaw {yaw:0}  " +
                                  $"x {placed.min.x:0.0}..{placed.max.x:0.0}  z {placed.min.z:0.0}..{placed.max.z:0.0}  — {p.role}");

                if (p.anchorStrip)
                {
                    var s = WorldBounds(parent, strip);
                    report.AppendLine($"          west strip x {s.min.x:0.0}..{s.max.x:0.0}  z {s.min.z:0.0}..{s.max.z:0.0}");
                }

                buildingBounds.AddRange(parent.GetComponentsInChildren<MeshFilter>(true)
                    .Where(mf => mf.sharedMesh != null)
                    .Select(mf => TransformBounds(mf.sharedMesh.bounds, mf.transform.localToWorldMatrix)));
            }

            if (!BuildGround(scene, root.transform, buildingBounds, report))
            {
                Undo.DestroyObjectImmediate(root);
                return false;
            }

            Undo.RegisterCreatedObjectUndo(root, $"Place {RootName}");
            report.AppendLine($"  ok    {RootName} created at the scene root");
            return true;
        }

        /// <summary>
        /// Block_H's west strip: the buildings on the narrower side of the block's widest empty
        /// X stretch. Returned by child name; the gap bounds are in prefab space.
        /// </summary>
        static HashSet<string> FindStrip(GameObject prefab, out float stripCentreX, out float gapLo, out float gapHi)
        {
            var items = prefab.GetComponentsInChildren<MeshFilter>(true)
                .Where(mf => mf.sharedMesh != null)
                .Select(mf => (name: mf.name, b: TransformBounds(mf.sharedMesh.bounds, mf.transform.localToWorldMatrix)))
                .OrderBy(i => i.b.min.x)
                .ToList();

            stripCentreX = gapLo = gapHi = 0f;
            if (items.Count < 2) return new HashSet<string>();

            var reach = items[0].b.max.x;
            var best = 0f;
            for (var i = 1; i < items.Count; i++)
            {
                var start = items[i].b.min.x;
                if (start - reach > best) { best = start - reach; gapLo = reach; gapHi = start; }
                reach = Mathf.Max(reach, items[i].b.max.x);
            }

            var lo = gapLo; var hi = gapHi;
            var below = items.Where(i => i.b.max.x <= lo).ToList();
            var above = items.Where(i => i.b.min.x >= hi).ToList();
            if (below.Count == 0 || above.Count == 0) return new HashSet<string>();

            var belowSpan = lo - below.Min(i => i.b.min.x);
            var aboveSpan = above.Max(i => i.b.max.x) - hi;
            var side = belowSpan <= aboveSpan ? below : above;

            stripCentreX = (side.Min(i => i.b.min.x) + side.Max(i => i.b.max.x)) * 0.5f;
            return new HashSet<string>(side.Select(i => i.name));
        }

        static float EdgeOf(float min, float centre, float max, Edge e) =>
            e == Edge.Min ? min : e == Edge.Max ? max : centre;

        /// <summary>World bounds of the meshes under <paramref name="t"/>, optionally only the named ones.</summary>
        static Bounds WorldBounds(Transform t, HashSet<string> only)
        {
            var first = true;
            var total = new Bounds();
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || (only != null && !only.Contains(mf.name))) continue;
                var b = TransformBounds(mf.sharedMesh.bounds, mf.transform.localToWorldMatrix);
                if (first) { total = b; first = false; }
                else total.Encapsulate(b);
            }
            return total;
        }

        /// <summary>Union of the prefab's mesh bounds in prefab space, and the highest per-mesh base.</summary>
        static Bounds MeasurePrefab(GameObject prefab, out float highestBase)
        {
            var first = true;
            var total = new Bounds();
            highestBase = float.MinValue;

            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;

                var b = TransformBounds(mf.sharedMesh.bounds, mf.transform.localToWorldMatrix);
                highestBase = Mathf.Max(highestBase, b.min.y);

                if (first) { total = b; first = false; }
                else total.Encapsulate(b);
            }

            if (first) highestBase = 0f;
            return total;
        }

        static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var result = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
            for (var i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                result.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return result;
        }

        // =================================================================================
        // Ground — the fix for the visible void under the surround
        //
        // Area A's Ground_Base stops at z = 20 and x = +/-160, so H_North and the far parts of
        // both H copies stood over nothing. Two project-owned meshes, one renderer each:
        //
        //   Ground_Extension  flat tiles that continue Ground_Base outward, in Ground_Base's
        //                     own asphalt material and at its exact top height. They tuck 1 m
        //                     under its edge (same surface, same material, so the overlap
        //                     cannot show) instead of butting against it, which can leave a
        //                     hairline crack to the sky. They leave holes over every keep-out
        //                     volume, so nothing new lies under another room.
        //   Sidewalks         a 12 cm kerb-height pavement pad under each building cluster, so
        //                     the alleys inside a block read as paved city ground and the
        //                     asphalt between pads reads as streets.
        //
        // No collider: nothing here is meant to be stood on or teleported to.
        // =================================================================================

        static bool BuildGround(Scene scene, Transform root, List<Bounds> buildings, StringBuilder report)
        {
            var asphalt = AssetDatabase.LoadAssetAtPath<Material>(k_AsphaltMaterialPath);
            var sidewalk = AssetDatabase.LoadAssetAtPath<Material>(k_SidewalkMaterialPath);
            if (asphalt == null || sidewalk == null)
            {
                report.AppendLine($"  FAIL  ground material missing ({k_AsphaltMaterialPath} / {k_SidewalkMaterialPath}). " +
                                  "Nothing was changed.");
                return false;
            }

            var groundBase = FindGroundBase(scene);
            var footprint = buildings.Select(FootprintOf).Aggregate(Union);
            var outer = Expand(footprint, k_GroundMargin);

            // Ground_Base first, so the keep-outs only cut the few tiles they actually touch.
            var holes = new List<Rect>();
            float topY;
            if (groundBase != null)
            {
                holes.Add(Expand(FootprintOf(groundBase.bounds), -k_GroundOverlap));
                topY = groundBase.bounds.max.y;
            }
            else
            {
                topY = -0.02f;
                report.AppendLine($"  WARN  '{k_GroundBaseName}' not found; the extension covers the whole footprint.");
            }
            holes.AddRange(k_KeepOut.Select(k => Expand(FootprintOf(k.box), 1f)));

            var tiles = new List<Rect> { outer };
            foreach (var h in holes) tiles = tiles.SelectMany(t => Subtract(t, h)).ToList();

            // Neighbouring tiles overlap by 25 cm instead of meeting edge to edge: a T-junction
            // between coplanar tiles can leave pixel cracks to the sky, and an overlap of the
            // same material at the same height cannot be seen. Still 0.75 m clear of every
            // keep-out.
            tiles = tiles.Select(t => Expand(t, 0.25f)).ToList();

            var pads = ClusterPads(buildings.Select(FootprintOf).ToList());
            foreach (var k in k_KeepOut) pads = pads.SelectMany(p => Subtract(p, Expand(FootprintOf(k.box), 0.5f))).ToList();

            var tileMesh = WriteMesh(k_GroundExtensionMeshPath, "JC_Ground_Extension", tiles, topY, topY, false);
            var padMesh = WriteMesh(k_SidewalkMeshPath, "JC_Sidewalks", pads, k_PadBottomY, k_PadTopY, true);
            SaveOwnAssets(new Object[] { tileMesh, padMesh });

            var group = new GameObject(k_GroundGroup).transform;
            group.SetParent(root, false);

            MakeGroundObject(group, k_GroundExtensionName, tileMesh, asphalt);
            MakeGroundObject(group, k_SidewalksName, padMesh, sidewalk);

            report.AppendLine($"  ok    Ground/{k_GroundExtensionName}: {tiles.Count} tile(s), " +
                              $"{tiles.Sum(t => t.width * t.height) / 10000f:0.0} ha at y = {topY:0.000}, " +
                              $"outer x {outer.xMin:0}..{outer.xMax:0} z {outer.yMin:0}..{outer.yMax:0}, " +
                              $"material {asphalt.name} (shared with {k_GroundBaseName})");
            report.AppendLine($"  ok    Ground/{k_SidewalksName}: {pads.Count} pad(s) under the building clusters, " +
                              $"top y = {k_PadTopY:0.00}, material {sidewalk.name}");
            return true;
        }

        static MeshRenderer FindGroundBase(Scene scene) =>
            scene.GetRootGameObjects()
                .Where(g => g.name != RootName)
                .SelectMany(g => g.GetComponentsInChildren<MeshRenderer>(true))
                .FirstOrDefault(r => r.name == k_GroundBaseName);

        static void MakeGroundObject(Transform parent, string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            ConfigureBackdropRenderer(go, r);
        }

        /// <summary>Buildings closer than k_PadJoin share a pad; overlapping pads merge.</summary>
        static List<Rect> ClusterPads(List<Rect> foot)
        {
            var n = foot.Count;
            var link = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (link[i] != i) i = link[i] = link[link[i]]; return i; }

            var grown = foot.Select(r => Expand(r, k_PadJoin * 0.5f)).ToList();
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                    if (grown[i].Overlaps(grown[j])) link[Find(i)] = Find(j);

            var pads = Enumerable.Range(0, n).GroupBy(Find)
                .Select(g => Expand(g.Select(i => foot[i]).Aggregate(Union), k_PadMargin))
                .ToList();

            for (var merged = true; merged;)
            {
                merged = false;
                for (var i = 0; i < pads.Count && !merged; i++)
                    for (var j = i + 1; j < pads.Count && !merged; j++)
                    {
                        if (!pads[i].Overlaps(pads[j])) continue;
                        pads[i] = Union(pads[i], pads[j]);
                        pads.RemoveAt(j);
                        merged = true;
                    }
            }
            return pads;
        }

        /// <summary>
        /// One mesh of axis-aligned slabs. Each rect writes a FIXED vertex count (4 for a top
        /// face only, 20 with sides) so the validator can read the rects back.
        /// </summary>
        static Mesh WriteMesh(string path, string name, List<Rect> rects, float y0, float y1, bool sides)
        {
            var v = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var tri = new List<int>();

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                // Unity front faces wind clockwise; Cross(b - a, c - a) then points outward.
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f) { var t = b; b = d; d = t; }
                var i = v.Count;
                v.AddRange(new[] { a, b, c, d });
                nrm.AddRange(new[] { normal, normal, normal, normal });
                uv.AddRange(new[] { a, b, c, d }.Select(p => new Vector2(p.x + p.y, p.z + p.y) * 0.25f));
                tri.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }

            foreach (var r in rects)
            {
                Vector3 P(float x, float y, float z) => new Vector3(x, y, z);
                Quad(P(r.xMin, y1, r.yMin), P(r.xMin, y1, r.yMax), P(r.xMax, y1, r.yMax), P(r.xMax, y1, r.yMin), Vector3.up);
                if (!sides) continue;
                Quad(P(r.xMin, y0, r.yMin), P(r.xMin, y1, r.yMin), P(r.xMax, y1, r.yMin), P(r.xMax, y0, r.yMin), Vector3.back);
                Quad(P(r.xMax, y0, r.yMax), P(r.xMax, y1, r.yMax), P(r.xMin, y1, r.yMax), P(r.xMin, y0, r.yMax), Vector3.forward);
                Quad(P(r.xMin, y0, r.yMax), P(r.xMin, y1, r.yMax), P(r.xMin, y1, r.yMin), P(r.xMin, y0, r.yMin), Vector3.left);
                Quad(P(r.xMax, y0, r.yMin), P(r.xMax, y1, r.yMin), P(r.xMax, y1, r.yMax), P(r.xMax, y0, r.yMax), Vector3.right);
            }

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            var isNew = mesh == null;
            if (isNew) mesh = new Mesh();
            else mesh.Clear();

            mesh.name = name;
            mesh.indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(v);
            mesh.SetNormals(nrm);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();

            if (isNew) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            return mesh;
        }

        /// <summary>The rects WriteMesh wrote, recovered from the vertex layout.</summary>
        static List<Rect> ReadRects(Mesh mesh, int vertsPerRect)
        {
            var v = mesh.vertices;
            var list = new List<Rect>();
            for (var i = 0; i + vertsPerRect <= v.Length; i += vertsPerRect)
            {
                var xs = Enumerable.Range(i, vertsPerRect).Select(k => v[k].x).ToList();
                var zs = Enumerable.Range(i, vertsPerRect).Select(k => v[k].z).ToList();
                list.Add(Rect.MinMaxRect(xs.Min(), zs.Min(), xs.Max(), zs.Max()));
            }
            return list;
        }

        // ---- 2D rect helpers (x = world X, y = world Z) -------------------------------------

        static Rect FootprintOf(Bounds b) => Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);

        static Rect Expand(Rect r, float m) => Rect.MinMaxRect(r.xMin - m, r.yMin - m, r.xMax + m, r.yMax + m);

        static Rect Union(Rect a, Rect b) =>
            Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
                Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));

        /// <summary>a minus h, as up to four non-overlapping rects.</summary>
        static IEnumerable<Rect> Subtract(Rect a, Rect h)
        {
            if (!a.Overlaps(h)) { yield return a; yield break; }

            if (h.xMin > a.xMin) yield return Rect.MinMaxRect(a.xMin, a.yMin, h.xMin, a.yMax);
            if (h.xMax < a.xMax) yield return Rect.MinMaxRect(h.xMax, a.yMin, a.xMax, a.yMax);

            var x0 = Mathf.Max(a.xMin, h.xMin);
            var x1 = Mathf.Min(a.xMax, h.xMax);
            if (h.yMin > a.yMin) yield return Rect.MinMaxRect(x0, a.yMin, x1, h.yMin);
            if (h.yMax < a.yMax) yield return Rect.MinMaxRect(x0, h.yMax, x1, a.yMax);
        }

        static bool Covered(Vector2 p, List<Rect> ground) => ground.Any(g => g.Contains(p));

        // =================================================================================
        // Validation
        // =================================================================================

        public static string ValidateScene(Scene scene)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== JAPANESE CITY SURROUND VALIDATION =====");

            var root = FindRoot(scene);
            if (root == null)
            {
                sb.AppendLine($"  FAIL  {RootName} not found in {scene.path}.");
                return sb.ToString();
            }

            var problems = 0;

            // ---- structure -----------------------------------------------------------------
            var groups = new Dictionary<string, MeshRenderer[]>();
            foreach (var p in k_Placements)
            {
                var parent = root.transform.Find(p.parent);
                var src = parent != null && parent.childCount == 1
                    ? PrefabUtility.GetCorrespondingObjectFromSource(parent.GetChild(0).gameObject)
                    : null;
                var expectedPath = PrefabPath(p.prefab);
                var okSource = src != null && AssetDatabase.GetAssetPath(src) == expectedPath;

                if (parent != null && okSource)
                {
                    // Active renderers only: buildings switched off by EncodingSurroundCleanup
                    // must not count toward the sightline and clearance checks below.
                    var all = parent.GetComponentsInChildren<MeshRenderer>(true);
                    groups[p.parent] = all.Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
                    var off = all.Length - groups[p.parent].Length;
                    sb.AppendLine($"  ok    {p.parent,-16} one instance of {expectedPath}, {groups[p.parent].Length} active renderer(s)" +
                                  $"{(off > 0 ? $" ({off} disabled)" : "")}, " +
                                  $"pos ({parent.localPosition.x:0.00}, {parent.localPosition.y:0.00}, {parent.localPosition.z:0.00}) " +
                                  $"yaw {parent.localEulerAngles.y:0}");
                }
                else
                {
                    sb.AppendLine($"  FAIL  {p.parent}: missing, or not a single instance of {expectedPath}");
                    problems++;
                }
            }

            var buildings = groups.Values.SelectMany(r => r).ToArray();
            if (buildings.Length == 0)
            {
                sb.AppendLine("  FAIL  no building renderers under the placement groups.");
                return sb.ToString();
            }
            sb.AppendLine($"  info  {buildings.Length} building renderer(s) " +
                          $"(complete blocks {groups.Where(g => !g.Key.StartsWith("Infill")).Sum(g => g.Value.Length)}, " +
                          $"infill {groups.Where(g => g.Key.StartsWith("Infill")).Sum(g => g.Value.Length)}), " +
                          $"{buildings.Sum(r => r.sharedMaterials.Length)} material slot(s), " +
                          $"{buildings.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().Count()} distinct building material(s)");

            // ---- nothing but renderers -------------------------------------------------------
            var allowed = new[] { typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer) };
            var foreign = root.GetComponentsInChildren<Component>(true)
                .Where(c => c == null || !allowed.Contains(c.GetType()))
                .Select(c => c == null ? "missing script" : c.GetType().Name)
                .Distinct().ToList();
            if (foreign.Count == 0)
                sb.AppendLine("  ok    only Transform / MeshFilter / MeshRenderer under the root (no collider, script, light, canvas, interactable)");
            else
            {
                sb.AppendLine($"  FAIL  unexpected component type(s): {string.Join(", ", foreign)}");
                problems++;
            }

            // ---- the train -------------------------------------------------------------------
            var trainRefs = root.GetComponentsInChildren<Transform>(true).Count(t => t.name.Contains("train")) +
                            root.GetComponentsInChildren<MeshFilter>(true)
                                .Count(f => f.sharedMesh != null && f.sharedMesh.name.Contains("train"));
            problems += Expect(sb, "objects or meshes named like the train (" + k_ExcludedName + ")", trainRefs, 0);

            // ---- only Block_H / Block_I meshes on the buildings ---------------------------------
            var allowedMeshes = new HashSet<Mesh>(new[] { k_BlockH, k_BlockI }.SelectMany(BlockMeshes));
            var outsider = buildings
                .Select(r => r.GetComponent<MeshFilter>())
                .Count(f => f == null || f.sharedMesh == null || !allowedMeshes.Contains(f.sharedMesh));
            problems += Expect(sb, "building meshes that are not a Block_H / Block_I child mesh", outsider, 0);

            // ---- materials -----------------------------------------------------------------
            var badSlots = new List<string>();
            foreach (var r in buildings)
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) badSlots.Add($"{r.name}: empty slot");
                    else if (m.shader == null || m.shader.name != k_UrpShaderName || !m.shader.isSupported)
                        badSlots.Add($"{r.name}: {m.name} ({(m.shader == null ? "no shader" : m.shader.name)})");
                    else if (!AssetDatabase.GetAssetPath(m).StartsWith(k_MaterialFolder + "/"))
                        badSlots.Add($"{r.name}: {m.name} is not a project-owned copy");
                }
            }
            problems += Expect(sb, "building material slots that would render magenta or are not project-owned", badSlots.Count, 0);
            foreach (var s in badSlots.Take(20))
                sb.AppendLine($"          {s}");

            // ---- ground ----------------------------------------------------------------------
            var tileRenderer = root.transform.Find($"{k_GroundGroup}/{k_GroundExtensionName}")?.GetComponent<MeshRenderer>();
            var padRenderer = root.transform.Find($"{k_GroundGroup}/{k_SidewalksName}")?.GetComponent<MeshRenderer>();
            var tileMesh = tileRenderer == null ? null : tileRenderer.GetComponent<MeshFilter>().sharedMesh;
            var padMesh = padRenderer == null ? null : padRenderer.GetComponent<MeshFilter>().sharedMesh;

            var tiles = new List<Rect>();
            var pads = new List<Rect>();

            if (tileMesh == null || padMesh == null ||
                AssetDatabase.GetAssetPath(tileMesh) != k_GroundExtensionMeshPath ||
                AssetDatabase.GetAssetPath(padMesh) != k_SidewalkMeshPath)
            {
                sb.AppendLine($"  FAIL  {k_GroundGroup}/{k_GroundExtensionName} or {k_GroundGroup}/{k_SidewalksName} " +
                              "missing, or not using the generated mesh assets");
                problems++;
            }
            else
            {
                tiles = ReadRects(tileMesh, 4);
                pads = ReadRects(padMesh, 20);

                var groundMats = new[] { tileRenderer, padRenderer }.SelectMany(r => r.sharedMaterials).ToList();
                var badGround = groundMats.Count(m => m == null || m.shader == null || !m.shader.isSupported ||
                                                      !m.shader.name.StartsWith("Universal Render Pipeline/"));
                problems += Expect(sb, "ground material slots that would render magenta", badGround, 0);
                sb.AppendLine($"  ok    ground: {tiles.Count} extension tile(s) ({tileRenderer.sharedMaterial.name}), " +
                              $"{pads.Count} sidewalk pad(s) ({padRenderer.sharedMaterial.name}), 2 renderers");

                // Every building corner must stand on Ground_Base or an extension tile.
                var gb = FindGroundBase(scene);
                var ground = new List<Rect>(tiles);
                if (gb != null) ground.Add(FootprintOf(gb.bounds));

                var unsupported = buildings.Where(r =>
                {
                    var f = FootprintOf(r.bounds);
                    return !new[] { f.min, f.max, new Vector2(f.xMin, f.yMax), new Vector2(f.xMax, f.yMin), f.center }
                        .All(c => Covered(c, ground));
                }).Select(r => r.name).ToList();
                problems += Expect(sb, "buildings with any footprint corner off the ground (Ground_Base + extension)",
                    unsupported.Count, 0);
                foreach (var u in unsupported.Take(10)) sb.AppendLine($"          {u}");
            }

            // ---- clearance -----------------------------------------------------------------
            var world = buildings.Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            sb.AppendLine($"  info  building extent x {world.min.x:0.0}..{world.max.x:0.0}, " +
                          $"y {world.min.y:0.0}..{world.max.y:0.0}, z {world.min.z:0.0}..{world.max.z:0.0}");

            foreach (var (name, box) in k_KeepOut)
            {
                var keep = FootprintOf(box);
                var hits = buildings.Where(r => r.bounds.Intersects(box)).Select(r => r.name)
                    .Concat(tiles.Where(t => t.Overlaps(keep)).Select(t => "ground tile"))
                    .Concat(pads.Where(t => t.Overlaps(keep)).Select(t => "sidewalk pad"))
                    .ToList();
                problems += Expect(sb, $"surround pieces inside the {name} keep-out", hits.Count, 0);
                foreach (var h in hits.Take(10))
                    sb.AppendLine($"          {h}");
            }

            // Groups must not interpenetrate each other.
            var groupOverlaps = 0;
            var keys = groups.Keys.ToList();
            for (var i = 0; i < keys.Count; i++)
                for (var j = i + 1; j < keys.Count; j++)
                {
                    var n = groups[keys[i]].Sum(a => groups[keys[j]].Count(b =>
                        FootprintOf(a.bounds).Overlaps(FootprintOf(b.bounds))));
                    if (n == 0) continue;
                    groupOverlaps += n;
                    sb.AppendLine($"          {keys[i]} x {keys[j]}: {n} overlapping footprint(s)");
                }
            problems += Expect(sb, "building footprints overlapping a building of another group", groupOverlaps, 0);

            // Nor anything already in the scene (ground-level surfaces excluded).
            var existing = scene.GetRootGameObjects()
                .Where(g => g != root)
                .SelectMany(g => g.GetComponentsInChildren<MeshRenderer>(false))
                .Where(r => r.enabled && r.bounds.max.y > 0.2f)
                .ToList();
            var clashes = (from a in buildings from b in existing where a.bounds.Intersects(b.bounds)
                           select $"{a.name} x {b.name}").ToList();
            problems += Expect(sb, "surround buildings intersecting existing scene geometry", clashes.Count, 0);
            foreach (var c in clashes.Take(10)) sb.AppendLine($"          {c}");

            // ---- the other areas stay hidden from Spawn_A ------------------------------------
            // The removed grey Skyline used to do this; the city must now.
            foreach (var (name, target) in k_Sightlines)
            {
                var ray = new Ray(k_Eye, (target - k_Eye).normalized);
                var distance = Vector3.Distance(k_Eye, target);
                var nearest = float.MaxValue;
                foreach (var r in buildings)
                    if (r.bounds.IntersectRay(ray, out var hit) && hit < distance)
                        nearest = Mathf.Min(nearest, hit);

                problems += Report(sb, nearest < float.MaxValue,
                    $"the sightline from Spawn_A to {name} is blocked by a surround building at " +
                    $"{(nearest < float.MaxValue ? nearest.ToString("0") : "no")} m of {distance:0} m");
            }

            // ---- enclosure (information, not pass/fail) ------------------------------------
            sb.Append(EnclosureReport(buildings));

            sb.AppendLine(problems == 0
                ? "  RESULT: PASS (static check only — not a Play Mode or headset test)"
                : $"  RESULT: {problems} problem(s)");
            return sb.ToString();
        }

        /// <summary>
        /// Horizontal sweep from the Spawn_A eye against building footprints: for each direction,
        /// the nearest building and the highest roofline angle. Footprint boxes, so it is an
        /// approximation of what the participant sees, not a render.
        /// </summary>
        static string EnclosureReport(MeshRenderer[] buildings)
        {
            var sb = new StringBuilder();
            var boxes = buildings.Select(r => r.bounds).ToList();
            var open = new List<int>();
            var sectors = new[]
            {
                ("front", -30, 30), ("front-right", 30, 60), ("right", 60, 120), ("back-right", 120, 150),
                ("back", 150, 210), ("back-left", 210, 240), ("left", 240, 300), ("front-left", 300, 330),
            };
            var nearest = new Dictionary<int, float>();
            var roof = new Dictionary<int, float>();

            for (var az = 0; az < 360; az += 3)
            {
                var a = az * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var flat = new Ray(new Vector3(k_Eye.x, 1.0f, k_Eye.z), dir);
                var near = float.MaxValue;
                var top = 0f;
                foreach (var b in boxes)
                {
                    var tall = new Bounds(b.center, new Vector3(b.size.x, 200f, b.size.z));
                    if (!tall.IntersectRay(flat, out var d)) continue;
                    near = Mathf.Min(near, d);
                    top = Mathf.Max(top, Mathf.Atan2(b.max.y - k_Eye.y, Mathf.Max(d, 0.5f)) * Mathf.Rad2Deg);
                }
                nearest[az] = near;
                roof[az] = top;
                if (near > 90f || top < 10f) open.Add(az);
            }

            sb.AppendLine("  info  enclosure from the Spawn_A eye (footprint sweep, 3 deg steps; median nearest building / mean roofline angle):");
            foreach (var (name, a0, a1) in sectors)
            {
                var azs = nearest.Keys.Where(k => (k >= a0 && k < a1) || (a0 < 0 && k >= 360 + a0)).ToList();
                var ns = azs.Select(k => nearest[k]).OrderBy(x => x).ToList();
                var median = ns[ns.Count / 2];
                sb.AppendLine($"          {name,-12} {(median > 1e6f ? "none" : median.ToString("0") + " m"),7}  " +
                              $"{azs.Average(k => roof[k]),3:0} deg");
            }
            sb.AppendLine($"  info  open directions (no building within 90 m, or roofline below 10 deg): " +
                          $"{open.Count} of 120{(open.Count > 0 ? " — at " + string.Join(", ", open) + " deg" : "")}");
            return sb.ToString();
        }

        static IEnumerable<Mesh> BlockMeshes(string block)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(k_FbxPath);
            var node = model == null ? null : model.transform.Find(block);
            if (node == null) yield break;

            foreach (Transform child in node)
            {
                if (child.name == k_ExcludedName) continue;
                var mf = child.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) yield return mf.sharedMesh;
            }
        }

        // =================================================================================
        // Helpers
        // =================================================================================

        static GameObject FindRoot(Scene scene) =>
            scene.GetRootGameObjects().FirstOrDefault(g => g.name == RootName);

        static int Expect(StringBuilder sb, string what, int actual, int expected)
        {
            var ok = actual == expected;
            sb.AppendLine($"  {(ok ? "ok  " : "FAIL")}  {what}: {actual}");
            return ok ? 0 : 1;
        }

        static int Report(StringBuilder sb, bool ok, string what)
        {
            sb.AppendLine($"  {(ok ? "ok  " : "FAIL")}  {what}");
            return ok ? 0 : 1;
        }

        static void SetIf(Material m, string prop, float value)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, value);
        }

        static string Sanitize(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }

        static Bounds Box(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            var b = new Bounds();
            b.SetMinMax(new Vector3(x0, y0, z0), new Vector3(x1, y1, z1));
            return b;
        }
    }
}
