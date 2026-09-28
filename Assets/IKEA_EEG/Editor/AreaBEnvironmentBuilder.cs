using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Static decorative dressing for Area B, the showroom where the chair task runs.
    ///
    /// SCOPE. Visual only, and deliberately conservative. It adds one child,
    /// <see cref="VisualRootName"/>, under Environment/Area_B_Showroom and touches nothing
    /// else: not Floor_B, not Ceiling_B, not the four walls, not a chair, not a chair slot,
    /// not a UI canvas, not a spawn point. The graybox floor and ceiling keep their renderers
    /// and their colliders and are simply covered, the same way the Area A plaza covers
    /// Floor_A.
    ///
    /// NOTHING IT CREATES HAS A COLLIDER. Every primitive is stripped immediately. Decoration
    /// must never be something the participant can bump into, teleport onto, or have the far
    /// interactor select instead of a chair.
    ///
    /// THE CENTRE STAYS EMPTY. The chair arc reaches |x| = 3.83 and the instruction panel sits
    /// at z = +5.6 on the mid-line, so everything vertical lives at |x| >= <see
    /// cref="k_KeepClearX"/> = 4.60. The only thing inside that corridor is the floor overlay,
    /// 16 mm tall, which cannot occlude anything. Validate() enforces this rather than
    /// trusting it.
    /// </summary>
    public static class AreaBEnvironmentBuilder
    {
        public const string VisualRootName = "AreaB_Visuals";
        public const string AreaBRootPath = "Environment/Area_B_Showroom";
        public const string PrefabPath = "Assets/IKEA_EEG/Prefabs/AreaB_Visuals.prefab";

        const string k_MaterialsFolder = "Assets/IKEA_EEG/Materials";
        const string k_Yughues = "Assets/YughuesFreeArchitecturalMaterials/Materials";

        // Room, measured from the scene. Walls are 0.25 thick at +/-7.00 and +/-6.50, so the
        // inner faces are here. Floor_B's top is y = 0; Ceiling_B's underside is y = 3.55.
        const float k_InnerX = 6.85f;
        const float k_InnerZ = 6.35f;
        const float k_CeilingY = 3.55f;

        // The protected corridor. Chairs reach |x| = 3.83; this leaves 0.77 m of margin before
        // anything vertical is allowed, and 2.47 m between the nearest chair and the shelving.
        const float k_KeepClearX = 4.60f;

        // Anything below this is flat floor dressing and exempt from the corridor rule.
        const float k_FlatMaxY = 0.05f;

        sealed class Mats
        {
            public Material floor, ceiling, light;
        }

        // =================================================================================
        // Entry points
        // =================================================================================

        [MenuItem("IKEA_EEG/Visuals/Rebuild Area B Environment", false, 210)]
        public static void RebuildMenu()
        {
            var areaB = FindAreaBRoot();

            if (areaB == null)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    "Could not find " + AreaBRootPath + " in the open scene.", "OK");
                return;
            }

            Rebuild(areaB);
            EditorSceneManager.MarkSceneDirty(areaB.gameObject.scene);
            Debug.Log("[IKEA_EEG] Area B environment rebuilt. Save the scene to keep it." +
                      System.Environment.NewLine + Validate());
        }

        [MenuItem("IKEA_EEG/Visuals/Validate Area B Environment", false, 211)]
        public static void ValidateMenu() => Debug.Log(Validate());

        /// <summary>
        /// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   IkeaEeg.EditorTools.AreaBEnvironmentBuilder.RebuildFromCommandLine
        /// Opens the experiment scene, rebuilds Area B dressing, SAVES, prints validation.
        /// </summary>
        public static void RebuildFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath,
                OpenSceneMode.Single);

            var areaB = FindAreaBRoot();

            if (areaB == null)
            {
                Debug.LogError("[IKEA_EEG] " + AreaBRootPath + " not found — nothing changed.");
                EditorApplication.Exit(2);
                return;
            }

            Rebuild(areaB);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(Validate());
            Debug.Log(ExperimentSceneBuilder.ValidateOpenScene());
        }

        public static void ValidateFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath, OpenSceneMode.Single);
            Debug.Log(Validate());
        }

        // =================================================================================
        // Build
        // =================================================================================

        public static void Rebuild(Transform areaBRoot)
        {
            for (var i = areaBRoot.childCount - 1; i >= 0; i--)
            {
                if (areaBRoot.GetChild(i).name == VisualRootName)
                    Object.DestroyImmediate(areaBRoot.GetChild(i).gameObject);
            }

            var mats = BuildMaterials();

            var root = new GameObject(VisualRootName);
            root.transform.SetParent(areaBRoot, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            BuildFloor(root.transform, mats);
            BuildCeiling(root.transform, mats);

            // A SCENE edit, like disabling the Area A graybox renderers: it changes a
            // component on an object this builder does not own, so it lives outside the
            // prefab and has to be re-applied after a rebuild.
            ApplyWallMaterials(areaBRoot);

            // NO LATERAL WALL TREATMENT. See the note on BuildWainscot: anything placed
            // against the -X wall of Area B does not draw, and the cause is not known.
            // Only surfaces that span the whole room are emitted, because those cannot
            // be asymmetric.

            EnsureFolder("Assets/IKEA_EEG", "Prefabs");
            PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath,
                InteractionMode.AutomatedAction);
        }

        // ---- floor -----------------------------------------------------------------------

        static void BuildFloor(Transform parent, Mats m)
        {
            var g = Group("Ground", parent);

            // Sits ON TOP of Floor_B (top face y = 0), never replacing it. Floor_B keeps its
            // renderer and its collider; it is occluded, not modified.
            Box("Floor_Wood", g, V(-k_InnerX, 0.002f, -k_InnerZ), V(k_InnerX, 0.016f, k_InnerZ),
                m.floor);

            // NO RUG. A first pass put a 9.4 x 4.1 m mat under the chair arc; at that size it
            // stopped reading as a rug and became a flat slab covering most of the visible
            // floor, hiding the wood it was meant to sit on. The wood floor alone reads better.
        }

        // ---- ceiling ---------------------------------------------------------------------

        static void BuildCeiling(Transform parent, Mats m)
        {
            var g = Group("CeilingDressing", parent);

            Box("Ceiling_Panel", g,
                V(-k_InnerX, k_CeilingY - 0.022f, -k_InnerZ),
                V(k_InnerX, k_CeilingY - 0.006f, k_InnerZ), m.ceiling);

            // Three recessed strips running the length of the room. Purely visual: there is no
            // extra Light component, so this costs nothing at run time and cannot change how
            // the task is lit.
            foreach (var x in new[] { -4.60f, 0f, 4.60f })
            {
                Box($"LightStrip_{x:0.0}".Replace("-", "neg"), g,
                    V(x - 0.17f, k_CeilingY - 0.050f, -5.60f),
                    V(x + 0.17f, k_CeilingY - 0.022f, 5.60f), m.light);
            }
        }

        // ---- lateral wall treatment: NOT SHIPPED --------------------------------------
        //
        // Two attempts, both removed:
        //   1. 4 mm wall panels plus three shelving bays per side.
        //   2. A solid 0.12 m wainscot band and capping rail per side, standing clear of
        //      the wall, mirrored by construction from a single loop.
        //
        // Both produced provably symmetric data -- Validate() compares the rendered
        // BOUNDS of every off-centre renderer and reported every one matched across
        // x = 0 -- and both drew on the +X wall only. Ruled out: missing objects, a
        // non-identity root, disabled renderers, null materials, prefab overrides,
        // occlusion culling (not baked in this scene at all), and a degenerate capture
        // camera. The cause is still unknown.
        //
        // Rather than ship a showroom with one dressed wall and one bare one, Area B gets
        // only surfaces that span the whole room: the floor, the ceiling and the light
        // strips. Those are symmetric by definition and verified.
        // ---- wall finish -------------------------------------------------------------------

        /// <summary>The four walls this pass re-finishes, and nothing else in Area B.</summary>
        public static readonly string[] WallNames =
            { "Wall_B_Left", "Wall_B_Right", "Wall_B_Back", "Wall_B_Front" };

        /// <summary>
        /// Re-finishes the existing graybox walls by swapping ONE field: sharedMaterial.
        ///
        /// WHY THIS AND NOT NEW GEOMETRY. Decorative boxes added to Area B do not render when
        /// they lie wholly at negative X -- reproduced with mirrored magenta probes, cause
        /// still unknown. These four walls are original scene objects and are confirmed to
        /// render correctly on both sides in real Play Mode, so re-finishing them sidesteps
        /// the anomaly completely instead of fighting it.
        ///
        /// NOTHING ELSE IS TOUCHED. Not the transform, not the collider, not the active state,
        /// not the name, not the static flags. Only the material the renderer points at.
        /// Validate() re-checks the colliders afterwards so a regression here is caught.
        /// </summary>
        public static void ApplyWallMaterials(Transform areaBRoot)
        {
            if (areaBRoot == null)
                return;

            var side = WallMaterialSide();
            var front = WallMaterialFront();

            foreach (var wallName in WallNames)
            {
                var wall = areaBRoot.Find(wallName);

                if (wall == null)
                {
                    Debug.LogWarning($"[IKEA_EEG] Area B: {wallName} not found; its finish was " +
                                     "not applied.");
                    continue;
                }

                var renderer = wall.GetComponent<MeshRenderer>();

                if (renderer == null)
                    continue;

                // The front wall carries the instruction panel. It gets a fractionally cooler,
                // lighter tone -- a restrained nod to the Area A facade blue -- so the dark UI
                // reads as mounted on a considered surface rather than stuck to a grey box.
                renderer.sharedMaterial = wallName == "Wall_B_Front" ? front : side;
                EditorUtility.SetDirty(renderer);
            }
        }

        /// <summary>
        /// Warm off-white, with a low emission term.
        ///
        /// The emission is not decoration, it is compensation. The lateral walls face away
        /// from the single directional light, and with no reflection probe and flat ambient
        /// a plain 0.88 albedo surface renders as cold blue-grey -- which is exactly the
        /// graybox look this pass exists to remove. A small warm emissive floor lifts them
        /// to a lit off-white without blowing out or casting anything.
        /// </summary>
        public static Material WallMaterialSide() =>
            Emissive("M_AreaB_Wall_OffWhite", new Color(0.930f, 0.918f, 0.898f),
                new Color(0.500f, 0.482f, 0.452f));

        /// <summary>
        /// The front wall, behind the instruction panel. A fraction cooler and lighter than
        /// the sides -- the restrained nod to the Area A facade blue -- and less emissive,
        /// because this wall already faces the light. Kept plain so the dark UI panel keeps
        /// maximum contrast.
        /// </summary>
        public static Material WallMaterialFront() =>
            Emissive("M_AreaB_Wall_Feature", new Color(0.912f, 0.926f, 0.944f),
                new Color(0.118f, 0.126f, 0.138f));

        // =================================================================================
        // Materials
        // =================================================================================

        static Mats BuildMaterials()
        {
            return new Mats
            {
                // Architectural textures already in the project. URP/Lit, so they render
                // correctly under this pipeline -- checked rather than assumed.
                // Tiling chosen so one texture tile is roughly a real-world unit: ~2 m planks
                // on the 13.7 x 12.7 m floor, ~1.2 m ceiling tiles, and a wood rail that reads
                // as boards rather than one smeared stripe.
                floor = Tiled("M_AreaB_Floor_Wood",
                    $"{k_Yughues}/M_YFAM_WoodFlooring.mat", new Vector2(7f, 6.5f)),
                ceiling = Tiled("M_AreaB_Ceiling",
                    $"{k_Yughues}/M_YFAM_Ceiling.mat", new Vector2(11f, 10f)),


                // Emissive so the strips read as fittings rather than pale boxes, without
                // adding a single real-time Light to the scene.
                light = Emissive("M_AreaB_LightStrip", new Color(0.97f, 0.97f, 0.94f),
                    new Color(1.30f, 1.28f, 1.18f)),

            };
        }

        /// <summary>
        /// A project-local COPY of a shared material, with texture tiling set for the surface
        /// it covers.
        ///
        /// A cube face maps 0..1 in UV however large it is, so a 13.7 m floor stretched one
        /// plank texture across the whole room and the planks came out two metres wide. Tiling
        /// has to be set per surface. It is set on a COPY so the shared Yughues material, which
        /// other things may use, is never modified.
        /// </summary>
        static Material Tiled(string assetName, string sourcePath, Vector2 tiling)
        {
            var source = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);

            if (source == null)
            {
                Debug.LogWarning($"[IKEA_EEG] Area B: source material not found at {sourcePath}.");
                return null;
            }

            var path = $"{k_MaterialsFolder}/{assetName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                material = new Material(source);
                EnsureFolder("Assets/IKEA_EEG", "Materials");
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = source.shader;
                material.CopyPropertiesFromMaterial(source);
            }

            material.mainTextureScale = tiling;

            foreach (var slot in new[] { "_BaseMap", "_BumpMap", "_MetallicGlossMap",
                                         "_OcclusionMap" })
            {
                if (material.HasProperty(slot))
                    material.SetTextureScale(slot, tiling);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        static Material Existing(string path)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (m == null)
                Debug.LogWarning($"[IKEA_EEG] Area B: material not found at {path}.");

            return m;
        }

        static Material Mat(string assetName, Color color, float metallic, float smoothness)
        {
            var path = $"{k_MaterialsFolder}/{assetName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit");

            if (material == null)
            {
                material = new Material(shader);
                EnsureFolder("Assets/IKEA_EEG", "Materials");
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);

            return material;
        }

        static Material Emissive(string assetName, Color baseColor, Color emission)
        {
            var m = Mat(assetName, baseColor, 0f, 0.35f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", emission);
            EditorUtility.SetDirty(m);
            return m;
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

        /// <summary>
        /// An axis-aligned box from min to max, WITHOUT a collider.
        ///
        /// The collider is destroyed the moment the primitive is created. Decoration the
        /// participant could collide with, teleport onto, or select with the far interactor
        /// instead of a chair would be a task regression dressed as art.
        /// </summary>
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
            // Deliberately NOT marked BatchingStatic. Static batching was tested as a cause
            // of the -X rendering anomaly documented below and RULED OUT -- the anomaly is
            // identical either way. It stays off as a precaution while the cause is unknown;
            // in a room of this few boxes the draw calls do not matter.
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }

        static Transform FindAreaBRoot()
        {
            var environment = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.parent == null && t.name == "Environment");

            return environment == null ? null : environment.Find("Area_B_Showroom");
        }

        // =================================================================================
        // Validation
        // =================================================================================

        public static string Validate()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== AREA B VISUAL ENVIRONMENT VALIDATION =====");

            var areaB = FindAreaBRoot();

            if (areaB == null)
            {
                sb.AppendLine("  FAIL  " + AreaBRootPath + " not found.");
                return sb + "[IKEA_EEG] AREA B VISUALS: 1 PROBLEM(S)";
            }

            var root = areaB.Find(VisualRootName);

            if (root == null)
            {
                sb.AppendLine($"  FAIL  '{VisualRootName}' not found under Area_B_Showroom.");
                return sb + "[IKEA_EEG] AREA B VISUALS: 1 PROBLEM(S)";
            }

            var problems = 0;

            problems += Report(sb,
                root.localPosition == Vector3.zero &&
                root.localRotation == Quaternion.identity &&
                root.localScale == Vector3.one,
                $"{VisualRootName} is on the identity transform");

            // ---- nothing decorative may be collidable ------------------------------------
            var colliders = root.GetComponentsInChildren<Collider>(true);

            problems += Report(sb, colliders.Length == 0,
                $"no decorative object has a collider ({colliders.Length} found) — nothing to " +
                "bump into, teleport onto, or select instead of a chair");

            // ---- the centre corridor stays clear -----------------------------------------
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            var intrusions = new List<string>();

            foreach (var r in renderers)
            {
                var b = r.bounds;
                var localTop = b.max.y - areaB.position.y;

                if (localTop <= k_FlatMaxY)
                    continue;   // flat floor dressing cannot occlude

                var minAbsX = Mathf.Min(Mathf.Abs(b.min.x - areaB.position.x),
                                        Mathf.Abs(b.max.x - areaB.position.x));

                var straddles = (b.min.x - areaB.position.x) < 0f &&
                                (b.max.x - areaB.position.x) > 0f;

                if (straddles || minAbsX < k_KeepClearX)
                    intrusions.Add($"{r.name} (|x| {minAbsX:F2})");
            }

            // The ceiling panel and the centre light strip legitimately span the room overhead.
            var overhead = intrusions.Where(s => s.StartsWith("Ceiling_Panel") ||
                                                 s.StartsWith("LightStrip")).ToList();
            var real = intrusions.Except(overhead).ToList();

            problems += Report(sb, real.Count == 0,
                real.Count == 0
                    ? $"every vertical prop stays outside |x| = {k_KeepClearX:F2}, so the chair " +
                      "arc and the instruction panel are never occluded"
                    : $"props intrude into the protected corridor: {string.Join(", ", real)}");

            if (overhead.Count > 0)
            {
                sb.AppendLine($"  info  {overhead.Count} overhead element(s) span the room by " +
                              "design (ceiling panel, centre light strip), all above 3.50 m");
            }

            // ---- the two sides must be mirror images --------------------------------------
            // The check the previous attempt lacked. Symmetric SOURCE data is not the same
            // as a symmetric ROOM: the shelving generated 43 matching renderers per side
            // and still only drew on one. This measures rendered bounds, so anything that
            // fails to mirror is caught here rather than in a screenshot.
            var offCentre = renderers
                .Select(r => new
                {
                    r.name,
                    x = (r.bounds.center.x - areaB.position.x),
                    w = r.bounds.size.x,
                    h = r.bounds.size.y,
                })
                .Where(o => Mathf.Abs(o.x) > 0.5f)
                .ToList();

            var unmatched = new List<string>();

            foreach (var o in offCentre)
            {
                var hasMirror = offCentre.Any(p =>
                    Mathf.Sign(p.x) != Mathf.Sign(o.x) &&
                    Mathf.Abs(Mathf.Abs(p.x) - Mathf.Abs(o.x)) < 0.02f &&
                    Mathf.Abs(p.w - o.w) < 0.02f &&
                    Mathf.Abs(p.h - o.h) < 0.02f);

                if (!hasMirror)
                    unmatched.Add($"{o.name} (x {o.x:F2})");
            }

            problems += Report(sb, unmatched.Count == 0,
                unmatched.Count == 0
                    ? $"the two lateral sides are mirror images ({offCentre.Count} " +
                      "off-centre renderers, every one matched across x = 0)"
                    : $"the sides are NOT symmetric; unmatched: {string.Join(", ", unmatched)}");

            // ---- the graybox room is covered, not modified --------------------------------
            foreach (var name in new[] { "Floor_B", "Ceiling_B", "Wall_B_Left", "Wall_B_Right",
                                         "Wall_B_Back", "Wall_B_Front" })
            {
                var t = areaB.Find(name);

                if (t == null)
                {
                    sb.AppendLine($"  FAIL  {name} is missing.");
                    problems++;
                    continue;
                }

                var mr = t.GetComponent<MeshRenderer>();
                var col = t.GetComponent<Collider>();

                problems += Report(sb, mr != null && mr.enabled,
                    $"{name}: renderer untouched (still enabled) — covered, not modified");
                problems += Report(sb, col != null && col.enabled,
                    $"{name}: collider still enabled");
            }

            // ---- the walls are re-finished, and ONLY re-finished --------------------------
            var sideMat = WallMaterialSide();
            var frontMat = WallMaterialFront();

            foreach (var wallName in WallNames)
            {
                var wall = areaB.Find(wallName);

                if (wall == null)
                    continue;   // already reported as missing above

                var mr = wall.GetComponent<MeshRenderer>();
                var expected = wallName == "Wall_B_Front" ? frontMat : sideMat;

                problems += Report(sb, mr != null && mr.sharedMaterial == expected,
                    $"{wallName}: carries its Area B finish " +
                    $"({(mr == null || mr.sharedMaterial == null ? "none" : mr.sharedMaterial.name)})");

                var col = wall.GetComponent<BoxCollider>();

                problems += Report(sb, col != null && col.enabled && !col.isTrigger,
                    $"{wallName}: collider still enabled and solid AFTER the re-finish");
            }

            // ---- the task's own objects are untouched -------------------------------------
            var chairSlots = areaB.GetComponentsInChildren<Transform>(true)
                .Count(t => t.name.StartsWith("ChairSlot_"));

            problems += Report(sb, chairSlots == 6,
                $"all 6 chair slots are present and unmoved ({chairSlots})");

            problems += Report(sb, areaB.Find("Spawn_B") != null, "Spawn_B is present");

            sb.AppendLine();
            sb.Append(problems == 0
                ? "[IKEA_EEG] AREA B VISUALS: PASS"
                : $"[IKEA_EEG] AREA B VISUALS: {problems} PROBLEM(S)");

            return sb.ToString();
        }

        static int Report(StringBuilder sb, bool ok, string message)
        {
            sb.AppendLine((ok ? "  ok    " : "  FAIL  ") + message);
            return ok ? 0 : 1;
        }

        /// <summary>
        /// Puts the dressing back after a destructive scene rebuild, mirroring
        /// AreaAEnvironmentBuilder.PreserveAfterSceneRebuild. Instantiates the EXISTING prefab
        /// and normalizes its transform; regenerates nothing.
        /// </summary>
        public static void PreserveAfterSceneRebuild(Transform areaBRoot)
        {
            if (areaBRoot == null)
                return;

            var existing = areaBRoot.Find(VisualRootName);

            if (existing == null)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);

                if (asset == null)
                {
                    Debug.LogWarning($"[IKEA_EEG] {VisualRootName} is absent and no prefab exists " +
                                     $"at {PrefabPath}; Area B keeps its graybox dressing.");
                    return;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, areaBRoot);
                instance.name = VisualRootName;
                instance.transform.SetParent(areaBRoot, false);
                existing = instance.transform;

                Debug.Log($"[IKEA_EEG] {VisualRootName} re-instantiated from {PrefabPath}.");
            }

            existing.localPosition = Vector3.zero;
            existing.localRotation = Quaternion.identity;
            existing.localScale = Vector3.one;
            EditorUtility.SetDirty(existing);

            // The wall finish is a scene edit on objects the rebuild recreates, so it
            // has to come back with the dressing or Area B returns to graybox.
            ApplyWallMaterials(areaBRoot);
        }
    }
}
