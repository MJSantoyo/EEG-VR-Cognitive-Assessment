using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using IkeaEeg.Interaction;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Static decorative dressing for Area 0, the familiarization (practice) room.
    ///
    /// SCOPE. Visual only. It adds one child, <see cref="VisualRootName"/>, under
    /// Environment/Area_0_Familiarization, and re-finishes the graybox shell (four walls, floor,
    /// ceiling) and the four practice posts by swapping ONE field each: sharedMaterial. It does
    /// not move, resize, rename, enable or disable anything; not a sphere, not a label, not a
    /// canvas, not the controller diagram, not Spawn_0, not Floor_0_Stripe.
    ///
    /// PROJECT-OWNED ONLY. Unity primitives plus M_Area0_* materials created here. No
    /// third-party pack is referenced, so a fresh clone renders this room complete. (The
    /// Yughues wood textures were checked on 2026-10-15 and rejected: "LaminatedWood" is a
    /// near-white speckle, "Plywood" is OSB chipboard, "WoodFlooring" is a parquet weave.)
    ///
    /// NOTHING IT CREATES HAS A COLLIDER, A LIGHT OR A SCRIPT. Decoration must never be
    /// something the far interactor can select instead of a practice sphere or a button.
    ///
    /// THE PRACTICE VOLUME STAYS EMPTY. The spheres reach |x| = 1.2 and the instruction panel
    /// |x| = 0.88, both in front of the front wall, so anything vertical lives at
    /// |x| >= <see cref="k_KeepClearX"/> or flat against the front wall. Validate() enforces it.
    ///
    /// SHARED MATERIALS ARE NEVER EDITED. M_Wall, M_Floor, M_Ceiling, M_DoorFrame and
    /// M_Accent are used by other rooms; this room gets its own copies instead.
    /// </summary>
    public static class Area0EnvironmentBuilder
    {
        public const string VisualRootName = "Area0_Visuals";
        public const string Area0RootName = "Area_0_Familiarization";
        public const string PrefabPath = "Assets/IKEA_EEG/Prefabs/Area0_Visuals.prefab";

        const string k_MaterialsFolder = "Assets/IKEA_EEG/Materials";

        // Room, measured from ExperimentSceneBuilder.BuildArea0: 6.0 x 5.5 m, walls 0.25 thick
        // centred on the edges, floor top y = 0, ceiling centre y = 3.0 and 0.1 thick.
        const float k_InnerX = 2.875f;
        const float k_InnerZ = 2.625f;
        const float k_CeilingY = 2.95f;

        // The practice volume: spheres reach |x| = 1.2, the instruction panel |x| = 0.88. Nothing
        // vertical may stand inside |x| < 2.2 in front of the front-wall dressing plane.
        const float k_KeepClearX = 2.20f;
        const float k_FrontDressingZ = 2.45f;

        // Above this, an element is overhead structure (beams, light strips) and exempt.
        const float k_OverheadMinY = 2.75f;

        static readonly string[] k_SideWalls = { "Wall_0_Left", "Wall_0_Right", "Wall_0_Back" };

        sealed class Mats
        {
            public Material wall, wallFront, floor, ceiling, wood, trim, screen, accent, light;
        }

        // =================================================================================
        // Entry points
        // =================================================================================

        [MenuItem("IKEA_EEG/Visuals/Rebuild Area 0 Environment", false, 220)]
        public static void RebuildMenu()
        {
            var area0 = FindArea0Root();

            if (area0 == null)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    $"Could not find Environment/{Area0RootName} in the open scenes.", "OK");
                return;
            }

            Rebuild(area0);
            EditorSceneManager.MarkSceneDirty(area0.gameObject.scene);
            Debug.Log("[IKEA_EEG] Area 0 environment rebuilt. Save the scene to keep it." +
                      System.Environment.NewLine + Validate());
        }

        [MenuItem("IKEA_EEG/Visuals/Validate Area 0 Environment", false, 221)]
        public static void ValidateMenu() => Debug.Log(Validate());

        /// <summary>
        /// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   IkeaEeg.EditorTools.Area0EnvironmentBuilder.RebuildFromCommandLine
        /// Opens the Area A room scene (which holds Area 0), rebuilds the dressing, SAVES the
        /// scene and only this tool's own assets, prints validation.
        /// </summary>
        public static void RebuildFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(ExperimentSceneBuilder.AreaAScenePath,
                OpenSceneMode.Single);

            var area0 = FindArea0Root();

            if (area0 == null)
            {
                Debug.LogError($"[IKEA_EEG] {Area0RootName} not found — nothing changed.");
                EditorApplication.Exit(2);
                return;
            }

            Rebuild(area0);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log(Validate());
        }

        public static void ValidateFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.AreaAScenePath,
                OpenSceneMode.Single);
            Debug.Log(Validate());
        }

        // =================================================================================
        // Build
        // =================================================================================

        public static void Rebuild(Transform area0Root)
        {
            for (var i = area0Root.childCount - 1; i >= 0; i--)
            {
                if (area0Root.GetChild(i).name == VisualRootName)
                    Object.DestroyImmediate(area0Root.GetChild(i).gameObject);
            }

            var mats = BuildMaterials();

            var root = new GameObject(VisualRootName);
            root.transform.SetParent(area0Root, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            BuildWoodDetails(root.transform, mats);
            BuildCeilingDetails(root.transform, mats);
            BuildAccent(root.transform, mats);
            BuildTechnicalStation(root.transform, mats);
            BuildShelfDecor(root.transform, mats);

            // A SCENE edit on objects this builder does not own, so it lives outside the
            // prefab and is re-applied by PreserveAfterSceneRebuild.
            ApplyShellMaterials(area0Root, mats);

            if (!AssetDatabase.IsValidFolder("Assets/IKEA_EEG/Prefabs"))
                AssetDatabase.CreateFolder("Assets/IKEA_EEG", "Prefabs");

            PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath,
                InteractionMode.AutomatedAction);
        }

        // ---- wood ------------------------------------------------------------------------

        static void BuildWoodDetails(Transform parent, Mats m)
        {
            var g = Group("WoodDetails", parent);

            // Corner pilasters frame the front wall. (Two slim uprights at x = +/-1.7 were
            // removed: the controller help now stands in front of the left one.)
            Box("A0_Pilaster_FrontLeft", g, V(-k_InnerX, 0f, k_InnerZ - 0.22f),
                V(-k_InnerX + 0.22f, k_CeilingY, k_InnerZ), m.wood);
            Box("A0_Pilaster_FrontRight", g, V(k_InnerX - 0.22f, 0f, k_InnerZ - 0.22f),
                V(k_InnerX, k_CeilingY, k_InnerZ), m.wood);

            // No left-wall slats and no ceiling beams: both were removed after review (the
            // slats cluttered the left wall, the beams cut across the light strips).
        }

        // ---- ceiling ---------------------------------------------------------------------

        static void BuildCeilingDetails(Transform parent, Mats m)
        {
            var g = Group("CeilingDetails", parent);

            // Emissive strips read as fittings. There is no Light component: nothing here can
            // change how the task is lit or cost a real-time light on the Quest.
            foreach (var (name, x) in new[] { ("A0_LightStrip_Left", -1.30f),
                                              ("A0_LightStrip_Right", 1.30f) })
            {
                Box(name, g, V(x - 0.06f, k_CeilingY - 0.025f, -2.30f),
                    V(x + 0.06f, k_CeilingY - 0.005f, 2.30f), m.light);
            }
        }

        // ---- accent ----------------------------------------------------------------------

        static void BuildAccent(Transform parent, Mats m)
        {
            var g = Group("Accent", parent);

            // The one cyan line: low along the front wall, under everything the participant
            // reads, so it frames the room without competing with the text.
            Box("A0_AccentLine_Front", g, V(-k_InnerX + 0.22f, 0.12f, k_InnerZ - 0.02f),
                V(k_InnerX - 0.22f, 0.145f, k_InnerZ), m.accent);
        }

        // ---- technical station (right wall only) ----------------------------------------

        static void BuildTechnicalStation(Transform parent, Mats m)
        {
            var g = Group("TechnicalStation", parent);
            const float wall = k_InnerX;

            Box("A0_Station_WoodPanel", g, V(wall - 0.03f, 0f, 0.05f),
                V(wall, 2.45f, 1.85f), m.wood);

            // Desk front edge at x = 2.255: 1.05 m clear of the green sphere at x = 1.2.
            Box("A0_Station_Desk", g, V(wall - 0.62f, 0.72f, 0.30f),
                V(wall - 0.03f, 0.76f, 1.60f), m.wood);
            Box("A0_Station_DeskSide_A", g, V(wall - 0.60f, 0f, 0.32f),
                V(wall - 0.03f, 0.72f, 0.36f), m.trim);
            Box("A0_Station_DeskSide_B", g, V(wall - 0.60f, 0f, 1.54f),
                V(wall - 0.03f, 0.72f, 1.58f), m.trim);
            Box("A0_Station_Keyboard", g, V(wall - 0.48f, 0.76f, 0.70f),
                V(wall - 0.32f, 0.78f, 1.20f), m.trim);

            // Wall-mounted monitor. The "display" is three abstract bars: nothing readable,
            // nothing that looks like data a participant might try to interpret.
            Box("A0_Station_MonitorBezel", g, V(wall - 0.07f, 1.10f, 0.50f),
                V(wall - 0.03f, 1.66f, 1.40f), m.trim);
            Box("A0_Station_Screen", g, V(wall - 0.075f, 1.13f, 0.53f),
                V(wall - 0.07f, 1.63f, 1.37f), m.screen);

            var bars = new[] { (1.50f, 0.60f, 1.20f), (1.42f, 0.60f, 0.95f), (1.22f, 0.60f, 1.30f) };

            for (var i = 0; i < bars.Length; i++)
            {
                var (y, z0, z1) = bars[i];
                Box($"A0_Station_ScreenBar_{i + 1}", g, V(wall - 0.079f, y, z0),
                    V(wall - 0.075f, y + 0.015f, z1), m.accent);
            }
        }

        // ---- shelf (left wall, rear) -----------------------------------------------------

        static void BuildShelfDecor(Transform parent, Mats m)
        {
            var g = Group("ShelfDecor", parent);
            const float wall = -k_InnerX;

            Box("A0_Shelf_Board_Upper", g, V(wall, 1.55f, -2.20f), V(wall + 0.26f, 1.58f, -1.10f),
                m.wood);
            Box("A0_Shelf_Board_Lower", g, V(wall, 1.10f, -2.20f), V(wall + 0.26f, 1.13f, -1.10f),
                m.wood);

            Box("A0_Shelf_Book_1", g, V(wall + 0.02f, 1.58f, -2.08f), V(wall + 0.22f, 1.84f, -2.03f),
                m.trim);
            Box("A0_Shelf_Book_2", g, V(wall + 0.02f, 1.58f, -2.02f), V(wall + 0.21f, 1.80f, -1.96f),
                m.wallFront);
            Box("A0_Shelf_Book_3", g, V(wall + 0.02f, 1.58f, -1.95f), V(wall + 0.22f, 1.82f, -1.90f),
                m.wood);
            Box("A0_Shelf_Device", g, V(wall + 0.04f, 1.58f, -1.55f), V(wall + 0.20f, 1.68f, -1.25f),
                m.trim);
            Box("A0_Shelf_Box", g, V(wall + 0.02f, 1.13f, -1.85f), V(wall + 0.22f, 1.31f, -1.45f),
                m.wall);
        }

        // =================================================================================
        // Shell re-finish (scene edit)
        // =================================================================================

        /// <summary>
        /// Re-finishes the graybox shell and the four practice posts by swapping sharedMaterial
        /// only. Transform, collider, active state, name and static flags are not touched.
        /// Floor_0_Stripe keeps its shared accent material on purpose: it is the spawn cue.
        /// </summary>
        static void ApplyShellMaterials(Transform area0Root, Mats m)
        {
            foreach (var wallName in k_SideWalls)
                SetMaterial(area0Root.Find(wallName), m.wall);

            SetMaterial(area0Root.Find("Wall_0_Front"), m.wallFront);
            SetMaterial(area0Root.Find("Floor_0"), m.floor);
            SetMaterial(area0Root.Find("Ceiling_0"), m.ceiling);

            foreach (var post in Posts(area0Root))
                SetMaterial(post, m.wood);
        }

        static IEnumerable<Transform> Posts(Transform area0Root)
        {
            var practice = area0Root.Find("PracticeObjects");

            return practice == null
                ? Enumerable.Empty<Transform>()
                : practice.Cast<Transform>().Where(t => t.name.EndsWith("_Post"));
        }

        static void SetMaterial(Transform t, Material material)
        {
            if (t == null || material == null)
                return;

            var renderer = t.GetComponent<MeshRenderer>();

            if (renderer == null)
                return;

            renderer.sharedMaterial = material;
            EditorUtility.SetDirty(renderer);
        }

        // =================================================================================
        // Materials
        // =================================================================================

        // Bootstrap's directional light barely reaches inside this closed box and the ambient
        // is a flat cool grey, so plain albedo alone renders as the graybox look this pass
        // removes. A small warm emission term lifts the large surfaces, as in Area B.
        static Mats BuildMaterials() => new Mats
        {
            wall = Emissive("M_Area0_Wall", new Color(0.86f, 0.82f, 0.76f), 0.25f,
                new Color(0.31f, 0.28f, 0.235f)),
            wallFront = Emissive("M_Area0_Wall_Front", new Color(0.72f, 0.67f, 0.60f), 0.25f,
                new Color(0.15f, 0.135f, 0.115f)),
            floor = Emissive("M_Area0_Floor", new Color(0.60f, 0.57f, 0.53f), 0.45f,
                new Color(0.10f, 0.09f, 0.08f)),
            ceiling = Emissive("M_Area0_Ceiling", new Color(0.54f, 0.50f, 0.45f), 0.20f,
                new Color(0.11f, 0.10f, 0.085f)),
            wood = Emissive("M_Area0_Wood", new Color(0.66f, 0.45f, 0.27f), 0.30f,
                new Color(0.16f, 0.10f, 0.055f)),
            trim = Emissive("M_Area0_Trim", new Color(0.25f, 0.23f, 0.21f), 0.40f, Color.black),
            screen = Emissive("M_Area0_Screen", new Color(0.06f, 0.09f, 0.12f), 0.70f,
                new Color(0.03f, 0.07f, 0.10f)),
            accent = Emissive("M_Area0_Accent_Cyan", new Color(0.20f, 0.55f, 0.62f), 0.40f,
                new Color(0.12f, 0.45f, 0.55f)),
            light = Emissive("M_Area0_LightStrip", new Color(0.97f, 0.95f, 0.90f), 0.35f,
                new Color(0.80f, 0.73f, 0.60f)),
        };

        static Material Emissive(string assetName, Color baseColor, float smoothness,
            Color emission)
        {
            var path = $"{k_MaterialsFolder}/{assetName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit");

            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetColor("_BaseColor", baseColor);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", smoothness);

            if (emission.maxColorComponent > 0f)
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.SetColor("_EmissionColor", emission);
            }

            EditorUtility.SetDirty(material);

            // Only this tool's own asset. Never AssetDatabase.SaveAssets(): it also rewrites
            // vendor materials that Unity dirtied on load.
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }

        // =================================================================================
        // Primitives
        // =================================================================================

        static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        static Transform Group(string name, Transform parent)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent, false);
            return g.transform;
        }

        /// <summary>An axis-aligned box from min to max, WITHOUT a collider or shadows.</summary>
        static void Box(string name, Transform parent, Vector3 min, Vector3 max, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;

            var collider = go.GetComponent<Collider>();

            if (collider != null)
                Object.DestroyImmediate(collider);

            go.transform.SetParent(parent, false);
            go.transform.localPosition = (min + max) * 0.5f;
            go.transform.localScale = new Vector3(
                Mathf.Abs(max.x - min.x), Mathf.Abs(max.y - min.y), Mathf.Abs(max.z - min.z));

            var renderer = go.GetComponent<MeshRenderer>();

            if (material != null)
                renderer.sharedMaterial = material;

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        static Transform FindArea0Root()
        {
            var environment = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.parent == null && t.name == "Environment" &&
                                     t.Find(Area0RootName) != null);

            return environment == null ? null : environment.Find(Area0RootName);
        }

        // =================================================================================
        // Preservation after a destructive scene rebuild
        // =================================================================================

        /// <summary>
        /// Puts the dressing back after ExperimentSceneBuilder recreates Area 0, mirroring
        /// AreaBEnvironmentBuilder.PreserveAfterSceneRebuild. Instantiates the EXISTING prefab
        /// and re-applies the shell materials from the EXISTING M_Area0_* assets; regenerates
        /// and rewrites nothing.
        /// </summary>
        public static void PreserveAfterSceneRebuild(Transform area0Root)
        {
            if (area0Root == null)
                return;

            var existing = area0Root.Find(VisualRootName);

            if (existing == null)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

                if (asset == null)
                {
                    Debug.LogWarning($"[IKEA_EEG] {VisualRootName} is absent and no prefab exists " +
                                     $"at {PrefabPath}; Area 0 keeps its graybox finish.");
                    return;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, area0Root);
                instance.name = VisualRootName;
                instance.transform.SetParent(area0Root, false);
                existing = instance.transform;

                Debug.Log($"[IKEA_EEG] {VisualRootName} re-instantiated from {PrefabPath}.");
            }

            existing.localPosition = Vector3.zero;
            existing.localRotation = Quaternion.identity;
            existing.localScale = Vector3.one;
            EditorUtility.SetDirty(existing);

            ApplyShellMaterials(area0Root, LoadExistingMaterials());
        }

        static Mats LoadExistingMaterials()
        {
            Material Load(string n) =>
                AssetDatabase.LoadAssetAtPath<Material>($"{k_MaterialsFolder}/{n}.mat");

            return new Mats
            {
                wall = Load("M_Area0_Wall"),
                wallFront = Load("M_Area0_Wall_Front"),
                floor = Load("M_Area0_Floor"),
                ceiling = Load("M_Area0_Ceiling"),
                wood = Load("M_Area0_Wood"),
            };
        }

        // =================================================================================
        // Validation
        // =================================================================================

        public static string Validate()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== AREA 0 VISUAL ENVIRONMENT VALIDATION =====");

            var area0 = FindArea0Root();

            if (area0 == null)
            {
                sb.AppendLine($"  FAIL  {Area0RootName} not found.");
                return sb + "[IKEA_EEG] AREA 0 VISUALS: 1 PROBLEM(S)";
            }

            var root = area0.Find(VisualRootName);

            if (root == null)
            {
                sb.AppendLine($"  FAIL  '{VisualRootName}' not found under {Area0RootName}.");
                return sb + "[IKEA_EEG] AREA 0 VISUALS: 1 PROBLEM(S)";
            }

            var problems = 0;

            problems += Report(sb,
                root.localPosition == Vector3.zero &&
                root.localRotation == Quaternion.identity &&
                root.localScale == Vector3.one,
                $"{VisualRootName} is on the identity transform");

            // ---- decoration only ----------------------------------------------------------
            problems += Report(sb, root.GetComponentsInChildren<Collider>(true).Length == 0,
                "no collider under the dressing");
            problems += Report(sb, root.GetComponentsInChildren<Light>(true).Length == 0,
                "no Light under the dressing");
            problems += Report(sb, root.GetComponentsInChildren<MonoBehaviour>(true).Length == 0,
                "no script, Canvas or interactable under the dressing");

            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);

            problems += Report(sb, renderers.All(r => !r.name.EndsWith("_Post")),
                "no dressing object is named *_Post (the self-test counts posts by name)");
            problems += Report(sb, renderers.All(r =>
                    r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off),
                "no dressing renderer casts shadows");

            var external = renderers.SelectMany(r => r.sharedMaterials)
                .Where(mat => mat != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(p => !p.StartsWith(k_MaterialsFolder + "/M_Area0_"))
                .Distinct().ToList();

            problems += Report(sb, external.Count == 0,
                external.Count == 0
                    ? "every dressing material is a project-owned M_Area0_* asset"
                    : $"dressing uses materials outside M_Area0_*: {string.Join(", ", external)}");

            sb.AppendLine($"  info  {renderers.Length} dressing renderer(s)");

            var packTextures = renderers.SelectMany(r => r.sharedMaterials)
                .Where(mat => mat != null).Distinct()
                .SelectMany(mat => mat.GetTexturePropertyNameIDs().Select(mat.GetTexture))
                .Where(t => t != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(p => !p.StartsWith("Assets/IKEA_EEG/"))
                .Distinct().ToList();

            sb.AppendLine($"  info  {packTextures.Count} texture(s) from outside Assets/IKEA_EEG " +
                          "(optional; plain tint without them): " +
                          (packTextures.Count == 0 ? "none" : string.Join(", ", packTextures)));

            // ---- the practice volume stays clear -------------------------------------------
            var intrusions = new List<string>();

            foreach (var r in renderers)
            {
                var b = r.bounds;
                var minY = b.min.y - area0.position.y;
                var minX = b.min.x - area0.position.x;
                var maxX = b.max.x - area0.position.x;
                var minZ = b.min.z - area0.position.z;

                if (minY >= k_OverheadMinY || minZ >= k_FrontDressingZ)
                    continue;   // overhead, or flat against the front wall behind the UI

                if (minX < k_KeepClearX && maxX > -k_KeepClearX)
                    intrusions.Add(r.name);
            }

            problems += Report(sb, intrusions.Count == 0,
                intrusions.Count == 0
                    ? $"nothing stands inside |x| < {k_KeepClearX:F2} between spawn and front wall"
                    : $"dressing intrudes into the practice volume: {string.Join(", ", intrusions)}");

            // ---- the shell is re-finished, and ONLY re-finished ----------------------------
            foreach (var name in k_SideWalls.Concat(new[] { "Wall_0_Front", "Floor_0", "Ceiling_0" }))
            {
                var t = area0.Find(name);
                var mr = t == null ? null : t.GetComponent<MeshRenderer>();
                var col = t == null ? null : t.GetComponent<Collider>();

                problems += Report(sb,
                    mr != null && mr.enabled && mr.sharedMaterial != null &&
                    mr.sharedMaterial.name.StartsWith("M_Area0_") &&
                    col != null && col.enabled,
                    $"{name}: Area 0 finish ({(mr == null || mr.sharedMaterial == null ? "none" : mr.sharedMaterial.name)}), renderer and collider enabled");
            }

            var posts = Posts(area0).ToList();

            problems += Report(sb, posts.Count == 4 && posts.All(p =>
                    p.GetComponent<MeshRenderer>().sharedMaterial != null &&
                    p.GetComponent<MeshRenderer>().sharedMaterial.name == "M_Area0_Wood" &&
                    p.GetComponent<Collider>() != null),
                $"all four practice posts carry M_Area0_Wood and keep their collider ({posts.Count})");

            // ---- the task's own objects are present ----------------------------------------
            var practice = area0.GetComponentsInChildren<PracticeObject>(true);

            problems += Report(sb, practice.Length == 4,
                $"four practice spheres present ({practice.Length})");
            problems += Report(sb, area0.Find("Spawn_0") != null, "Spawn_0 is present");
            problems += Report(sb, area0.Find("UI_0_Canvas") != null, "UI_0_Canvas is present");
            problems += Report(sb, area0.Find("ControllerDiagram") != null,
                "ControllerDiagram is present");

            sb.AppendLine();
            sb.Append(problems == 0
                ? "[IKEA_EEG] AREA 0 VISUALS: PASS"
                : $"[IKEA_EEG] AREA 0 VISUALS: {problems} PROBLEM(S)");

            return sb.ToString();
        }

        static int Report(StringBuilder sb, bool ok, string message)
        {
            sb.AppendLine((ok ? "  ok    " : "  FAIL  ") + message);
            return ok ? 0 : 1;
        }

        // =================================================================================
        // Static captures (read-only)
        // =================================================================================

        /// <summary>
        /// Batch: renders Area 0 from fixed viewpoints with Bootstrap loaded first and active,
        /// as at run time, so its light and ambient apply. Never saves. Output folder via
        /// -captureOutput &lt;dir&gt;. Edit-mode render: runtime UI text is not filled in.
        /// </summary>
        public static void CaptureFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.BootstrapScenePath,
                OpenSceneMode.Single);
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.AreaAScenePath,
                OpenSceneMode.Additive);

            var args = System.Environment.GetCommandLineArgs();
            var folder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Presentation_Evidence", "Area0_Static");

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-captureOutput")
                    folder = args[i + 1];
            }

            Directory.CreateDirectory(folder);

            var origin = new Vector3(-100f, 0f, 0f);
            var eye = origin + new Vector3(0f, 1.60f, -1.60f);
            var views = new (string name, Vector3 pos, Vector3 lookAt, float fov)[]
            {
                ("A0_spawn_forward", eye, origin + new Vector3(0f, 1.6f, 2.6f), 84f),
                ("A0_spawn_slight_left", eye, origin + new Vector3(-1.4f, 1.6f, 2.6f), 84f),
                ("A0_spawn_slight_right", eye, origin + new Vector3(1.4f, 1.6f, 2.6f), 84f),
                ("A0_spawn_right", eye, origin + new Vector3(2.8f, 1.2f, 0.9f), 84f),
                ("A0_overview_back_corner", origin + new Vector3(2.3f, 2.5f, -2.3f),
                    origin + new Vector3(-0.6f, 0.9f, 1.2f), 80f),
                ("A0_left_rear", eye, origin + new Vector3(-2.8f, 1.4f, -1.7f), 80f),
            };

            var host = new GameObject("~Area0ViewCapture") { hideFlags = HideFlags.HideAndDontSave };
            var written = 0;

            try
            {
                var camera = host.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 60f;
                camera.enabled = false;

                foreach (var (name, pos, lookAt, fov) in views)
                {
                    host.transform.SetPositionAndRotation(pos,
                        Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up));
                    camera.fieldOfView = fov;

                    var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32)
                        { antiAliasing = 4 };
                    var previous = RenderTexture.active;
                    camera.targetTexture = rt;
                    camera.Render();

                    RenderTexture.active = rt;
                    var image = new Texture2D(1600, 900, TextureFormat.RGB24, false);
                    image.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
                    image.Apply();
                    RenderTexture.active = previous;
                    camera.targetTexture = null;

                    File.WriteAllBytes(Path.Combine(folder, name + ".png"), image.EncodeToPNG());
                    written++;

                    Object.DestroyImmediate(image);
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }

            Debug.Log($"[IKEA_EEG] Captured {written} Area 0 view(s) to {folder}");
        }
    }
}
