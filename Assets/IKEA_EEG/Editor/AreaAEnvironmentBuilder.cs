using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Builds the STATIC visual environment for Area A — the outdoor showroom entrance.
    ///
    /// WHY A SEPARATE BUILDER INSTEAD OF EXTENDING ExperimentSceneBuilder: that builder calls
    /// EditorSceneManager.NewScene(EmptyScene) and re-saves the whole experiment scene, so it
    /// is the one file in this project that must not acquire new responsibilities. This builder
    /// only ever ADDS one root under Environment/Area_A_Entrance and never touches anything
    /// else, which also means a full scene rebuild is recovered by re-running this one menu
    /// item rather than by redoing the work.
    ///
    /// WHAT THIS IS NOT: it contains no behaviour. Every object it creates is a MeshFilter +
    /// MeshRenderer and nothing more — no MonoBehaviour, no Collider, no Light, no Canvas, no
    /// interactable. It is invisible to the experiment: no serialized reference points at it,
    /// it raises no event, and it cannot intercept an XR ray.
    ///
    /// IDEMPOTENT: re-running deletes the previous AreaA_Visuals and rebuilds from scratch, so
    /// the result depends only on the constants in this file.
    /// </summary>
    public static class AreaAEnvironmentBuilder
    {
        public const string VisualRootName = "AreaA_Visuals";
        public const string AreaARootPath = "Environment/Area_A_Entrance";

        public const string PrefabPath = "Assets/IKEA_EEG/Prefabs/AreaA_Visuals.prefab";
        const string k_MeshFolder = "Assets/IKEA_EEG/Meshes";
        const string k_SignMeshPath = k_MeshFolder + "/AreaA_SignText.mesh";
        const string k_MaterialsFolder = ExperimentAssetBuilder.MaterialsFolder;

        // =================================================================================
        // Geometry constants — ALL in metres, local to Area_A_Entrance (which sits at the
        // world origin, so these are world coordinates too).
        //
        // The existing doorway is 2.6 x 2.6 m centred on x = 0 at z = 2.0. Every number below
        // is derived from that and from the two clearance rules the audit established:
        //
        //   RULE 1  Nothing above the ground may sit closer than z = 1.75. The UI panel plane
        //           is z = 1.40 and the recognition buttons reach z = 1.21; 1.75 keeps every
        //           new surface behind both, so no XR ray can be intercepted.
        //   RULE 2  Anything that must sit in front of z = 1.75 (pavement, mat) has to stay at
        //           or below y = 0.05. A ray from a ~1.6 m eye to a button at y >= 0.82 never
        //           descends that far before reaching its target.
        // =================================================================================

        const float k_MinFrontZ = 1.75f;      // RULE 1
        const float k_GroundMaxY = 0.05f;     // RULE 2

        /// <summary>
        /// Spawn_A's z. RULE 1 only bites BETWEEN the participant and the UI — geometry behind
        /// the spawn mark cannot occlude a panel in front of it, however tall it is. Block 1
        /// never needed this because everything it added was either in front or on the ground;
        /// the kerb and the road are the first objects to sit behind the participant.
        /// </summary>
        const float k_SpawnZ = -0.70f;

        /// <summary>
        /// Objects excused from RULE 1 by name, with the reason they are safe.
        ///
        /// "Skyline" is a single combined mesh whose volumes stand at |x| >= 14 and 30-96 m
        /// away, but the merge gives it one enormous axis-aligned bounding box that straddles
        /// x = 0 — so a bounds test says it is in the corridor when no part of it is. Its real
        /// placement is verified separately, and far more meaningfully, by the three Spawn_A
        /// sightline ray tests further down.
        /// </summary>
        static readonly string[] k_ClearanceExemptByName = { "Skyline" };

        const float k_DoorHalfWidth = 1.30f;  // the existing opening: x in [-1.30, 1.30]
        const float k_DoorHeight = 2.60f;

        // Depth bands. Chosen so that no two surfaces are ever coplanar: boxes either clear
        // each other or interpenetrate outright. Coplanar faces are what produce z-fighting,
        // and interpenetration never does.
        const float k_SignZMin = 1.750f, k_SignZMax = 1.830f;
        const float k_TextZMin = 1.755f, k_TextZMax = 1.825f;
        const float k_PortalZMin = 1.750f, k_PortalZMax = 1.860f;
        const float k_SeamZMin = 1.780f, k_SeamZMax = 1.820f;
        const float k_CladZMin = 1.800f, k_CladZMax = 1.900f;
        const float k_LeafGlassZMin = 1.820f, k_LeafGlassZMax = 1.855f;
        const float k_SensorZMin = 1.820f, k_SensorZMax = 1.865f;
        const float k_LeafFrameZMin = 1.840f, k_LeafFrameZMax = 1.890f;
        const float k_HeaderZMin = 1.870f, k_HeaderZMax = 1.930f;
        const float k_FixedGlassZMin = 1.880f, k_FixedGlassZMax = 1.915f;
        const float k_FixedFrameZMin = 1.900f, k_FixedFrameZMax = 1.950f;

        // The existing Facade_A_DoorBlocker starts at z = 1.96. Everything above ends before
        // it, so the blocker keeps its collider and its renderer and is simply hidden behind
        // the new glazing.
        const float k_BlockerFrontZ = 1.96f;

        // Storefront bay: fixed light | leaf | leaf | fixed light, filling the 2.6 m opening.
        const float k_FixedWidth = 0.58f;
        const float k_LeafWidth = 0.72f;
        const float k_LeafGap = 0.003f;       // centre reveal, so the two leaves never touch

        const float k_GlazingTopY = 2.42f;    // silver header band sits above this
        const float k_CladTopY = 4.24f;       // just proud of the existing 4.20 m facade top

        // ---- Extended storefront -------------------------------------------------------
        // ~28 m of building, entrance centred. The WINGS ARE LOWER THAN THE ENTRANCE BAY, and
        // that is a hard constraint rather than a style choice: the participant stands 2.55 m
        // from this wall with a 1.60 m eye height, so the top of an 84 deg frame only reaches
        // ~4.4 m at the facade plane. Wings at bay height or above erase the sky completely.
        // The "large store" impression therefore comes from WIDTH, and the bay stays the
        // tallest element so the entrance still reads as the focus.
        const float k_FacadeHalfWidth = 14.0f;
        const float k_BayHalfWidth = 2.30f;   // where the existing entrance cladding ends
        const float k_WingTopY = 3.80f;
        const float k_ParapetTopY = 4.00f;
        const float k_SideReturnDepth = 3.40f;  // how far the outer ends come toward the viewer

        // ---- Ground zones --------------------------------------------------------------
        // store -> plaza -> kerb -> road -> distant city.
        const float k_PlazaHalfWidth = 12.0f;
        const float k_PlazaTopY = 0.015f;     // just under the existing slabs (0.020)
        const float k_KerbZ = -6.00f;         // plaza ends / kerb face
        const float k_KerbTopY = 0.135f;
        const float k_RoadTopY = -0.005f;
        const float k_RoadFarZ = -26.0f;

        /// <summary>Skyline volumes, as min/max pairs. Combined into ONE renderer.</summary>
        static readonly (Vector3 min, Vector3 max)[] k_Skyline =
        {
            // The two "Near" blocks are the ones that actually occlude the other experiment
            // areas: Area 0 sits at x = -100 and Areas B and C at x = +100 / +200, all at
            // z ~ 0, so the sightlines to them run almost straight along +/-X from Spawn_A.
            (new Vector3(-62f, 0f, -18f), new Vector3(-42f, 17f, 14f)),
            (new Vector3(42f, 0f, -18f), new Vector3(62f, 16f, 14f)),
            (new Vector3(-96f, 0f, -30f), new Vector3(-66f, 22f, 6f)),
            (new Vector3(66f, 0f, -30f), new Vector3(96f, 21f, 6f)),
            (new Vector3(-40f, 0f, -58f), new Vector3(-14f, 13f, -34f)),
            (new Vector3(14f, 0f, -58f), new Vector3(40f, 14f, -34f)),
        };

        const string k_SkylineMeshPath = k_MeshFolder + "/AreaA_Skyline.mesh";

        /// <summary>Local X the left leaf must reach to be fully open. Not used yet.</summary>
        public const float DoorLeafOpenOffset = k_LeafWidth;

        // ---- Graybox walls -------------------------------------------------------------
        // Their RENDERERS are switched off so Area A reads as open air. The GameObjects and
        // their BoxColliders stay untouched: the colliders are what stop the far interactor
        // selecting through the open sides, which is the documented reason the walls exist.
        static readonly string[] k_GrayboxWalls = { "Wall_A_Left", "Wall_A_Right", "Wall_A_Back" };

        // ---- Exterior props ------------------------------------------------------------
        // Kept to |x| >= 2.0 so none of them can occlude the UI panel (|x| <= 0.71 at z 1.40)
        // or the recognition buttons (|x| <= 0.71 at z ~1.18), and clear of the walking line
        // between Spawn_A (0, 0, -0.7) and the doors.
        const string k_PropFolder = "Assets/Urban_Props_Pack_Rozity/Prefabs/URP/";

        struct PropPlacement
        {
            public string prefab;
            public Vector3 position;
            public float yaw;
            public string name;
        }

        static readonly PropPlacement[] k_Props =
        {
            // Lamps stand on the ROAD side of the kerb, so they belong to the street rather
            // than floating in the plaza. Modern_01 is the contemporary column; Modern_02 read
            // as a traditional park lantern against a contemporary storefront.
            new PropPlacement { prefab = "SM_StreetLight_Modern_01", name = "StreetLight_Left",
                position = new Vector3(-6.00f, 0.005f, k_KerbZ - 0.55f), yaw = 0f },
            new PropPlacement { prefab = "SM_StreetLight_Modern_01", name = "StreetLight_Right",
                position = new Vector3(6.00f, 0.005f, k_KerbZ - 0.55f), yaw = 180f },

            // Beside the entrance, clear of the doorway and well outside the UI corridor.
            new PropPlacement { prefab = "SM_TrashCan_01", name = "TrashCan",
                position = new Vector3(2.75f, 0.016f, 1.35f), yaw = 180f },

            // Long axis along X, so it sits parallel to the facade rather than pointing at it.
            new PropPlacement { prefab = "SM_Park_Bench_02", name = "Bench",
                position = new Vector3(-3.70f, 0.016f, 1.25f), yaw = 180f },
        };

        /// <summary>Props are decoration only; anything above this is a bug, not a style choice.</summary>
        const int k_MaxPropTextureSize = 512;

        /// <summary>What the vendor shipped, restored to textures a prop revision stopped using.</summary>
        const int k_PackDefaultTextureSize = 2048;

        // The Metal_5 painted-panel trial and its two comparison samples lived here. The trial
        // was reviewed and REJECTED, so the code is gone rather than left behind a disabled
        // flag: a dormant switch that can silently recreate a deleted material asset is a trap,
        // not documentation. The comparison renders are kept in
        // Presentation_Evidence/AreaA_Concept/ as the record of the experiment.

        /// <summary>
        /// Objects at or beyond this |x| are exempt from the z >= 1.75 rule.
        ///
        /// That rule protects the UI panel and the response buttons from being occluded and
        /// from having their rays intercepted. Both live within |x| <= 0.71, so geometry out
        /// past 2.0 m is angularly nowhere near either — and carries no collider in any case.
        /// </summary>
        const float k_LateralExemptionX = 2.0f;

        // =================================================================================
        // Menu entry points
        // =================================================================================

        [MenuItem("IKEA_EEG/Visuals/Rebuild Area A Environment", false, 200)]
        public static void RebuildMenu()
        {
            var areaA = FindAreaARoot();
            if (areaA == null)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    $"Could not find '{AreaARootPath}' in the open scene.\n\n" +
                    "Open Assets/IKEA_EEG/Scenes/IKEA_EEG_Experiment.unity first.", "OK");
                return;
            }

            Rebuild(areaA);
            EditorSceneManager.MarkSceneDirty(areaA.gameObject.scene);
            Debug.Log("[IKEA_EEG] Area A visual environment rebuilt. The scene is dirty — save it " +
                      "to keep the change.\n" + Validate());
        }

        [MenuItem("IKEA_EEG/Visuals/Validate Area A Environment", false, 201)]
        public static void ValidateMenu() => Debug.Log(Validate());

        /// <summary>
        /// Batch entry point for validation ALONE:
        /// Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   IkeaEeg.EditorTools.AreaAEnvironmentBuilder.ValidateFromCommandLine
        ///
        /// Opens the experiment scene, prints both validation reports and saves NOTHING.
        /// Exists because the only other batch path that reaches ValidateOpenScene is
        /// ExperimentSceneBuilder.BuildFromCommandLine, which regenerates the entire scene —
        /// far too destructive to run just to check that the scene is still intact.
        /// </summary>
        public static void ValidateFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath, OpenSceneMode.Single);

            Debug.Log(Validate());
            Debug.Log(ExperimentSceneBuilder.ValidateOpenScene());
        }

        /// <summary>
        /// Batch entry point:
        /// Unity.exe -batchmode -quit -projectPath ... -executeMethod
        ///   IkeaEeg.EditorTools.AreaAEnvironmentBuilder.RebuildFromCommandLine
        /// Opens the experiment scene, rebuilds, SAVES, then prints both validation reports.
        /// </summary>
        public static void RebuildFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(ExperimentSceneBuilder.ScenePath,
                OpenSceneMode.Single);

            var areaA = FindAreaARoot();
            if (areaA == null)
            {
                Debug.LogError($"[IKEA_EEG] '{AreaARootPath}' not found — nothing was changed.");
                EditorApplication.Exit(2);
                return;
            }

            Rebuild(areaA);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(Validate());
            Debug.Log(ExperimentSceneBuilder.ValidateOpenScene());
            Debug.Log("[IKEA_EEG] Area A static environment build complete.");
        }

        // =================================================================================
        // Build
        // =================================================================================

        public static void Rebuild(Transform areaARoot)
        {
            var mats = BuildMaterials();

            // Before anything is instantiated, so the props are built against 512 textures
            // rather than being imported at 2K and downsized afterwards.
            ApplyPropTextureBudget();

            // Idempotency: remove any previous payload, prefab instance or plain object alike.
            for (var i = areaARoot.childCount - 1; i >= 0; i--)
            {
                var child = areaARoot.GetChild(i);
                if (child.name == VisualRootName)
                    Object.DestroyImmediate(child.gameObject);
            }

            var root = new GameObject(VisualRootName);
            root.transform.SetParent(areaARoot, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            BuildCladding(root.transform, mats);
            BuildPortal(root.transform, mats);
            BuildSign(root.transform, mats);
            BuildGlazing(root.transform, mats);
            BuildDoors(root.transform, mats);
            BuildGround(root.transform, mats);
            BuildSkyline(root.transform, mats);
            BuildExteriorProps(root.transform);

            EnsureFolder("Assets/IKEA_EEG", "Prefabs");
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath,
                InteractionMode.AutomatedAction);

            if (prefab == null)
                Debug.LogWarning($"[IKEA_EEG] Could not save the Area A visual prefab to {PrefabPath}. " +
                                 "The scene objects were still created.");

            // LAST, and deliberately OUTSIDE the prefab. The walls are siblings of
            // AreaA_Visuals, not children, so this is a scene edit rather than prefab content —
            // which is exactly why it has to be re-applied by this method: a scene rebuild
            // recreates the walls with their renderers switched back on.
            DisableGrayboxWallRenderers(areaARoot);
        }

        /// <summary>
        /// Switches off the three graybox wall renderers and leaves everything else about them
        /// alone: the GameObject stays active, the BoxCollider stays enabled, and neither the
        /// transform nor the name is touched.
        ///
        /// SetActive(false) would be the wrong tool here — it would take the colliders down with
        /// the renderer and let the far interactor escape sideways out of Area A.
        /// </summary>
        public static void DisableGrayboxWallRenderers(Transform areaARoot)
        {
            foreach (var wallName in k_GrayboxWalls)
            {
                var wall = areaARoot.Find(wallName);

                if (wall == null)
                {
                    Debug.LogWarning($"[IKEA_EEG] '{wallName}' not found under Area_A_Entrance; " +
                                     "its renderer could not be disabled.");
                    continue;
                }

                var renderer = wall.GetComponent<MeshRenderer>();

                if (renderer == null)
                    continue;

                if (renderer.enabled)
                {
                    renderer.enabled = false;
                    EditorUtility.SetDirty(renderer);
                }
            }
        }

        // ---- Blue modular cladding ------------------------------------------------------

        static void BuildCladding(Transform parent, Mats m)
        {
            var g = Group("Facade_Cladding", parent);

            // Blue panels laid OVER the existing grey facade. Their back face at z = 1.90 sits
            // inside the grey panel (which starts at 1.875), so there can be no seam or gap
            // between the two and no coplanar pair either.
            Box("Panel_Left", g, V(-2.30f, -0.03f, k_CladZMin), V(-k_DoorHalfWidth, k_CladTopY, k_CladZMax), m.blue);
            Box("Panel_Right", g, V(k_DoorHalfWidth, -0.03f, k_CladZMin), V(2.30f, k_CladTopY, k_CladZMax), m.blue);
            Box("Panel_Above_Entrance", g, V(-k_DoorHalfWidth, 2.88f, k_CladZMin), V(k_DoorHalfWidth, k_CladTopY, k_CladZMax), m.blue);

            // Modular joints. Thin dark strips embedded in the cladding face, matching the
            // panelised look of the reference without a texture.
            const float h = 0.03f;
            foreach (var y in new[] { 1.05f, 2.10f })
            {
                Box($"Seam_H_{y:0.00}_L", g, V(-2.30f, y, k_SeamZMin), V(-k_DoorHalfWidth, y + h, k_SeamZMax), m.charcoal);
                Box($"Seam_H_{y:0.00}_R", g, V(k_DoorHalfWidth, y, k_SeamZMin), V(2.30f, y + h, k_SeamZMax), m.charcoal);
            }

            // Above the sign, so it never crosses the lettering.
            Box("Seam_H_3.80", g, V(-2.30f, 3.80f, k_SeamZMin), V(2.30f, 3.80f + h, k_SeamZMax), m.charcoal);

            Box("Seam_V_L", g, V(-1.80f, -0.03f, k_SeamZMin), V(-1.80f + h, k_CladTopY, k_SeamZMax), m.charcoal);
            Box("Seam_V_R", g, V(1.80f - h, -0.03f, k_SeamZMin), V(1.80f, k_CladTopY, k_SeamZMax), m.charcoal);

            BuildFacadeWings(g, m);
        }

        /// <summary>
        /// Extends the storefront to ~28 m so it reads as one building rather than a 4.6 m card
        /// standing on a plain.
        /// </summary>
        static void BuildFacadeWings(Transform parent, Mats m)
        {
            var g = Group("Facade_Wings", parent);

            Box("Wing_Left", g, V(-k_FacadeHalfWidth, -0.03f, k_CladZMin),
                V(-k_BayHalfWidth, k_WingTopY, k_CladZMax), m.blue);
            Box("Wing_Right", g, V(k_BayHalfWidth, -0.03f, k_CladZMin),
                V(k_FacadeHalfWidth, k_WingTopY, k_CladZMax), m.blue);

            // Panel-tone variation: every other 4.8 m bay in a slightly different blue, a few
            // mm proud. Stops a 28 m wall reading as one flat slab without costing a texture.
            var flip = false;
            for (var x = -k_FacadeHalfWidth; x < k_FacadeHalfWidth - 0.01f; x += 4.8f)
            {
                flip = !flip;

                if (x + 4.8f > -k_BayHalfWidth && x < k_BayHalfWidth)
                    continue;                       // never touch the entrance bay

                if (!flip)
                    continue;

                Box($"Panel_{x:0}", g, V(x, -0.03f, 1.775f),
                    V(Mathf.Min(x + 4.8f, k_FacadeHalfWidth), k_WingTopY, 1.795f), m.blueAlt);
            }

            const float seam = 0.04f;

            foreach (var y in new[] { 1.25f, 2.50f })
            {
                Box($"WingSeam_H_L_{y:0.00}", g, V(-k_FacadeHalfWidth, y, 1.755f),
                    V(-k_BayHalfWidth, y + seam, 1.795f), m.charcoal);
                Box($"WingSeam_H_R_{y:0.00}", g, V(k_BayHalfWidth, y, 1.755f),
                    V(k_FacadeHalfWidth, y + seam, 1.795f), m.charcoal);
            }

            for (var x = -k_FacadeHalfWidth + 4.8f; x < k_FacadeHalfWidth - 0.01f; x += 4.8f)
            {
                if (x > -k_BayHalfWidth - 0.01f && x < k_BayHalfWidth + 0.01f)
                    continue;

                Box($"WingSeam_V_{x:0}", g, V(x, -0.03f, 1.755f),
                    V(x + seam, k_WingTopY, 1.795f), m.charcoal);
            }

            // Metal parapet capping the wings against the sky.
            Box("Parapet_Left", g, V(-k_FacadeHalfWidth, k_WingTopY, 1.775f),
                V(-k_BayHalfWidth, k_ParapetTopY, 1.915f), m.silver);
            Box("Parapet_Right", g, V(k_BayHalfWidth, k_WingTopY, 1.775f),
                V(k_FacadeHalfWidth, k_ParapetTopY, 1.915f), m.silver);

            // Shallow side returns at the far ends. Without them the building is a flat card
            // seen edge-on from any three-quarter view; with them it reads as a volume. Kept
            // 14 m out, so they are nowhere near the participant and enclose nothing.
            var returnFrontZ = k_CladZMin - k_SideReturnDepth;

            Box("Return_Left", g, V(-k_FacadeHalfWidth, -0.03f, returnFrontZ),
                V(-k_FacadeHalfWidth + 0.55f, k_WingTopY, k_CladZMin), m.blueAlt);
            Box("Return_Right", g, V(k_FacadeHalfWidth - 0.55f, -0.03f, returnFrontZ),
                V(k_FacadeHalfWidth, k_WingTopY, k_CladZMin), m.blueAlt);

            Box("Return_Parapet_Left", g, V(-k_FacadeHalfWidth, k_WingTopY, returnFrontZ),
                V(-k_FacadeHalfWidth + 0.60f, k_ParapetTopY, k_CladZMin), m.silver);
            Box("Return_Parapet_Right", g, V(k_FacadeHalfWidth - 0.60f, k_WingTopY, returnFrontZ),
                V(k_FacadeHalfWidth, k_ParapetTopY, k_CladZMin), m.silver);
        }

        // ---- Dark outer entrance portal -------------------------------------------------

        static void BuildPortal(Transform parent, Mats m)
        {
            var g = Group("Entrance_Portal", parent);

            // Jambs and header overlap between y = 2.60 and 2.75 rather than meeting flush:
            // two boxes sharing a face at exactly the same depth is the one arrangement that
            // z-fights, and interpenetration is the cheapest way to never have it.
            Box("Portal_Jamb_Left", g, V(-1.60f, -0.03f, k_PortalZMin), V(-k_DoorHalfWidth, 2.75f, k_PortalZMax), m.charcoal);
            Box("Portal_Jamb_Right", g, V(k_DoorHalfWidth, -0.03f, k_PortalZMin), V(1.60f, 2.75f, k_PortalZMax), m.charcoal);
            Box("Portal_Header", g, V(-1.60f, k_DoorHeight, k_PortalZMin), V(1.60f, 2.92f, k_PortalZMax), m.charcoal);
        }

        // ---- Yellow sign structure ------------------------------------------------------

        static void BuildSign(Transform parent, Mats m)
        {
            var g = Group("Sign_Structure", parent);

            Box("Sign_Band", g, V(-2.30f, 2.98f, k_SignZMin), V(2.30f, 3.16f, k_SignZMax), m.yellow);
            Box("Sign_Post_Left", g, V(-1.20f, 2.86f, 1.760f), V(-1.10f, 3.02f, 1.820f), m.yellow);
            Box("Sign_Post_Right", g, V(1.10f, 2.86f, 1.760f), V(1.20f, 3.02f, 1.820f), m.yellow);

            // The wording is a BAKED MESH, not a text component: the approved architecture
            // forbids MonoBehaviours under this prefab, and TextMeshPro is one. Sign_Text is a
            // single leaf object, so replacing the placeholder with an approved logo or a
            // different wording means swapping one mesh — the facade around it is untouched.
            var capHeight = 0.32f;
            var mesh = BuildSignMesh("FURNITURE & DESIGN", capHeight, k_TextZMax - k_TextZMin);

            var text = new GameObject("Sign_Text");
            text.transform.SetParent(g, false);
            text.transform.localPosition = new Vector3(0f, 3.18f, (k_TextZMin + k_TextZMax) * 0.5f);
            text.AddComponent<MeshFilter>().sharedMesh = mesh;
            text.AddComponent<MeshRenderer>().sharedMaterial = m.yellow;
            MarkStatic(text);
        }

        // ---- Fixed side glazing, header and sill ----------------------------------------

        static void BuildGlazing(Transform parent, Mats m)
        {
            var g = Group("Storefront_Glazing", parent);

            var fixedInnerL = -k_DoorHalfWidth + k_FixedWidth;   // -0.72
            var fixedInnerR = k_DoorHalfWidth - k_FixedWidth;    //  0.72

            // Each light is a silver backing plate with a slightly smaller dark pane in front
            // of it, so the plate reads as the frame. Two boxes instead of five.
            const float reveal = 0.075f;   // how much silver frame shows around each pane

            Box("Glass_Fixed_Left_Frame", g, V(-k_DoorHalfWidth, 0.02f, k_FixedFrameZMin), V(fixedInnerL, 2.44f, k_FixedFrameZMax), m.silver);
            Box("Glass_Fixed_Left_Pane", g, V(-k_DoorHalfWidth + reveal, 0.10f, k_FixedGlassZMin), V(fixedInnerL - reveal, 2.36f, k_FixedGlassZMax), m.glass);

            Box("Glass_Fixed_Right_Frame", g, V(fixedInnerR, 0.02f, k_FixedFrameZMin), V(k_DoorHalfWidth, 2.44f, k_FixedFrameZMax), m.silver);
            Box("Glass_Fixed_Right_Pane", g, V(fixedInnerR + reveal, 0.10f, k_FixedGlassZMin), V(k_DoorHalfWidth - reveal, 2.36f, k_FixedGlassZMax), m.glass);

            // Track housing above the leaves, and the sill they run on. The sill's underside is
            // buried in Floor_A so its bottom face is never coplanar with the floor's top.
            Box("Door_Header", g, V(-k_DoorHalfWidth, k_GlazingTopY, k_HeaderZMin), V(k_DoorHalfWidth, k_DoorHeight, k_HeaderZMax), m.silver);
            Box("Door_Sill", g, V(-k_DoorHalfWidth, -0.02f, k_HeaderZMin), V(k_DoorHalfWidth, 0.06f, k_HeaderZMax), m.silver);

            // Automatic-door sensor, proud of the header.
            Box("Door_Sensor", g, V(-0.22f, 2.44f, k_SensorZMin), V(0.22f, 2.52f, k_SensorZMax), m.charcoal);
        }

        // ---- The two sliding leaves -----------------------------------------------------

        static void BuildDoors(Transform parent, Mats m)
        {
            var g = Group("Doors", parent);

            BuildLeaf("Door_Leaf_Left", g, m, -1);
            BuildLeaf("Door_Leaf_Right", g, m, +1);
        }

        /// <summary>
        /// One leaf, closed. Its ROOT sits at the leaf's centre and its parts are positioned
        /// relative to that root, so a future open/close animation is a single local X
        /// translation of this one transform and needs no change to the geometry.
        ///
        /// Leaves are deliberately NOT marked batching-static: static batching bakes a
        /// renderer's transform into a merged mesh, and a leaf that will later move must keep
        /// its own transform.
        /// </summary>
        static void BuildLeaf(string name, Transform parent, Mats m, int side)
        {
            var inner = side * k_LeafGap;                  // edge at the centre reveal
            var outer = side * (k_LeafWidth);              // edge toward the fixed light
            var centreX = (inner + outer) * 0.5f;

            var leaf = new GameObject(name);
            leaf.transform.SetParent(parent, false);
            leaf.transform.localPosition = new Vector3(centreX, 0f, 0f);

            var halfW = Mathf.Abs(outer - inner) * 0.5f;

            Box("Leaf_Frame", leaf.transform,
                new Vector3(-halfW, 0.02f, k_LeafFrameZMin),
                new Vector3(halfW, k_GlazingTopY, k_LeafFrameZMax), m.silver, markStatic: false);

            Box("Leaf_Pane", leaf.transform,
                new Vector3(-halfW + 0.075f, 0.10f, k_LeafGlassZMin),
                new Vector3(halfW - 0.075f, k_GlazingTopY - 0.09f, k_LeafGlassZMax), m.glass,
                markStatic: false);
        }

        // ---- Ground ---------------------------------------------------------------------

        static void BuildGround(Transform parent, Mats m)
        {
            var g = Group("Ground", parent);

            // ---- Three zones, not one plane ----------------------------------------------
            // The single uniform apron read as an infinite beige void. Replaced with a legible
            // sequence the eye can follow outward: plaza -> kerb -> road -> distant city.

            // Far ground, so nothing ever ends in open sky at the horizon. Dark, and largely
            // hidden behind the road and the skyline; it exists to close the world, not to be
            // looked at. Its top sits BELOW the road so the road always wins where they meet.
            Box("Ground_Base", g, V(-160f, -0.30f, -200f), V(160f, -0.020f, 20f), m.asphalt);

            // ZONE A — entrance plaza. Light concrete, ~24 m wide.
            //
            // It runs PAST the facade line in +Z rather than stopping at it: that is what fully
            // covers Floor_A (whose top face is at y = 0.000, below this at 0.015), so the
            // graybox floor is occluded rather than re-materialed and Floor_A's renderer,
            // material and collider stay completely untouched. The overlap with the storefront
            // is buried inside solid geometry.
            //
            // The existing 16 pavement slabs (top y = 0.020) still sit 5 mm proud, so their
            // joints continue to read — against plaza concrete instead of against grey.
            Box("Plaza_Concrete", g, V(-k_PlazaHalfWidth, -0.10f, k_KerbZ),
                V(k_PlazaHalfWidth, k_PlazaTopY, 2.60f), m.pavement);

            // ZONE B — kerb. The transition is a HEIGHT step (120 mm) rather than a colour
            // change, which is what a real kerb is; the directional light does the rest on the
            // vertical face. The gutter strip below it gives the edge a hard line to read against.
            Box("Kerb", g, V(-k_PlazaHalfWidth, -0.10f, k_KerbZ - 0.28f),
                V(k_PlazaHalfWidth, k_KerbTopY, k_KerbZ), m.pavement);
            Box("Gutter", g, V(-k_PlazaHalfWidth, -0.10f, k_KerbZ - 0.46f),
                V(k_PlazaHalfWidth, k_RoadTopY + 0.004f, k_KerbZ - 0.28f), m.charcoal);

            // ZONE C — road. Dark asphalt, about as wide as the storefront.
            Box("Road_Asphalt", g, V(-k_FacadeHalfWidth, -0.12f, k_RoadFarZ),
                V(k_FacadeHalfWidth, k_RoadTopY, k_KerbZ - 0.46f), m.asphalt);

            // A few painted markings. Bay lines perpendicular to the store, plus one lane line.
            for (var x = -10f; x <= 10.01f; x += 4f)
                Box($"RoadMark_Bay_{x:0}", g, V(x, k_RoadTopY, -13.6f),
                    V(x + 0.14f, k_RoadTopY + 0.004f, -8.2f), m.pavement);

            Box("RoadMark_Lane", g, V(-k_FacadeHalfWidth + 1f, k_RoadTopY, -17.4f),
                V(k_FacadeHalfWidth - 1f, k_RoadTopY + 0.004f, -17.26f), m.pavement);

            // A 4 x 4 grid of light slabs. The joints are GAPS, not extra strips: the darker
            // existing Floor_A shows through them, which costs no geometry and no material.
            const float x0 = -2.30f, x1 = 2.30f;
            const float z0 = -2.05f, z1 = 1.83f;   // stops short of the leaf frames at 1.84
            const float joint = 0.03f;
            const int n = 4;

            var slabW = ((x1 - x0) - joint * (n - 1)) / n;
            var slabD = ((z1 - z0) - joint * (n - 1)) / n;

            for (var ix = 0; ix < n; ix++)
            {
                for (var iz = 0; iz < n; iz++)
                {
                    var sx = x0 + ix * (slabW + joint);
                    var sz = z0 + iz * (slabD + joint);
                    Box($"Pavement_{ix}{iz}", g,
                        V(sx, -0.06f, sz), V(sx + slabW, 0.02f, sz + slabD), m.pavement);
                }
            }

            // Entrance mat, embedded in the pavement so no face is coplanar with it.
            Box("Entrance_Mat", g, V(-1.05f, 0.010f, 0.95f), V(1.05f, 0.032f, 1.80f), m.charcoal);
        }

        // =================================================================================
        // Distant skyline
        // =================================================================================

        /// <summary>
        /// A handful of far-off blocks, combined into ONE renderer.
        ///
        /// Its first job is functional rather than decorative: Area 0 (x = -100), Area B
        /// (x = +100) and Area C (x = +200) all sit at z ~ 0 in this single scene, so once the
        /// Area A graybox walls stopped being drawn they became visible as rectangles on the
        /// horizon. The two "Near" blocks stand across those sightlines.
        ///
        /// Muted and neutral on purpose — it must give depth without competing with the
        /// storefront — and it stays low enough on the eye line to leave the sky open.
        /// </summary>
        static void BuildSkyline(Transform parent, Mats m)
        {
            var mesh = CombineBoxes(k_Skyline, "AreaA_Skyline", k_SkylineMeshPath);

            var go = new GameObject("Skyline");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m.skyline;
            MarkStatic(go);
        }

        /// <summary>Merges a set of boxes into a single saved mesh asset.</summary>
        static Mesh CombineBoxes((Vector3 min, Vector3 max)[] boxes, string meshName, string path)
        {
            var cubeSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var cube = cubeSource.GetComponent<MeshFilter>().sharedMesh;

            var combine = new CombineInstance[boxes.Length];

            for (var i = 0; i < boxes.Length; i++)
            {
                combine[i] = new CombineInstance
                {
                    mesh = cube,
                    transform = Matrix4x4.TRS((boxes[i].min + boxes[i].max) * 0.5f,
                        Quaternion.identity, boxes[i].max - boxes[i].min),
                };
            }

            var mesh = new Mesh { name = meshName };
            mesh.CombineMeshes(combine, true, true);
            mesh.RecalculateBounds();

            Object.DestroyImmediate(cubeSource);

            EnsureFolder("Assets/IKEA_EEG", "Meshes");
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);

            return mesh;
        }

        // =================================================================================
        // Exterior props (third-party Urban Props Pack, used as decoration only)
        // =================================================================================

        /// <summary>
        /// Places the four decorative props.
        ///
        /// Each instance is UNPACKED COMPLETELY straight after instantiation. That is
        /// deliberate: the pack's prefabs each wrap their FBX and add a collider, and removing
        /// a component from a nested prefab instance is stored as a fragile "removed component"
        /// override that can reappear if the pack is ever reimported. Unpacking turns the
        /// instance into plain GameObjects that still reference the pack's shared meshes and
        /// materials, so nothing is duplicated and nothing in the package is modified — but the
        /// collider is gone for good.
        ///
        /// COLLIDERS ARE STRIPPED, NOT DISABLED. The far interactor casts 10 m; decoration must
        /// be incapable of intercepting a ray, not merely switched off.
        /// </summary>
        static void BuildExteriorProps(Transform parent)
        {
            var g = Group("Exterior_Props", parent);

            foreach (var placement in k_Props)
            {
                var path = k_PropFolder + placement.prefab + ".prefab";
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (source == null)
                {
                    Debug.LogWarning($"[IKEA_EEG] Prop prefab not found: {path}. Area A will " +
                                     "build without it.");
                    continue;
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, g);

                if (instance == null)
                    continue;

                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);

                instance.name = placement.name;
                instance.transform.localPosition = placement.position;
                instance.transform.localRotation = Quaternion.Euler(0f, placement.yaw, 0f);

                foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(collider, true);

                // The pack ships none, but a decorative mesh must never bring a realtime light
                // into a scene whose lighting is one directional light and flat ambient.
                foreach (var light in instance.GetComponentsInChildren<Light>(true))
                    Object.DestroyImmediate(light, true);

                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                    MarkStatic(t.gameObject);
            }
        }

        /// <summary>
        /// Caps the textures of the three prop types in use at 512.
        ///
        /// Scoped by asset DEPENDENCY, not by a hand-written list: only textures actually
        /// reachable from those three prefabs are touched, so the other ~59 maps in the pack
        /// keep their import settings. Everything except Max Size is left exactly as the vendor
        /// shipped it — mipmaps, compression, albedo sRGB and the Normal Map texture type are
        /// all read but never written.
        ///
        /// 2048 is far past what these earn: the nearest prop is ~1.8 m away and the furthest
        /// ~6 m, so a 2K map costs about sixteen times the memory of the detail it can show.
        /// </summary>
        static void ApplyPropTextureBudget()
        {
            var prefabPaths = k_Props
                .Select(p => k_PropFolder + p.prefab + ".prefab")
                .Distinct()
                .Where(p => AssetDatabase.LoadAssetAtPath<GameObject>(p) != null)
                .ToArray();

            if (prefabPaths.Length == 0)
                return;

            var inUse = new HashSet<string>(
                AssetDatabase.GetDependencies(prefabPaths, true)
                    .Where(d => AssetDatabase.LoadAssetAtPath<Texture2D>(d) != null));

            var capped = 0;

            foreach (var dependency in inUse)
            {
                var importer = AssetImporter.GetAtPath(dependency) as TextureImporter;

                if (importer == null || importer.maxTextureSize <= k_MaxPropTextureSize)
                    continue;

                importer.maxTextureSize = k_MaxPropTextureSize;
                importer.SaveAndReimport();
                capped++;
            }

            // Restore anything an EARLIER prop selection capped that the current one no longer
            // uses, so the override tracks the props actually in the scene rather than
            // accumulating across revisions. Scoped to this pack's Textures folder.
            var restored = 0;

            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D",
                         new[] { "Assets/Urban_Props_Pack_Rozity/Textures" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                if (inUse.Contains(path))
                    continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;

                if (importer == null || importer.maxTextureSize != k_MaxPropTextureSize)
                    continue;

                importer.maxTextureSize = k_PackDefaultTextureSize;
                importer.SaveAndReimport();
                restored++;
            }

            if (capped > 0 || restored > 0)
                Debug.Log($"[IKEA_EEG] Prop texture budget: {capped} capped at " +
                          $"{k_MaxPropTextureSize} px, {restored} restored to " +
                          $"{k_PackDefaultTextureSize} px (no longer used). Mipmaps, compression, " +
                          "sRGB and normal-map type were not altered.");
        }

        // =================================================================================
        // Sign lettering
        // =================================================================================

        // A stroke font, defined on a normalised box: cap height 1.0, glyph width k_GW.
        // Rects are axis-aligned; diagonals are rects rotated about +Z, which in the
        // participant's view (looking along +Z with +X to their right) is counter-clockwise.
        const float k_GW = 0.62f;
        const float k_GT = 0.155f;
        const float k_AdvanceLetter = k_GW + 0.18f;
        const float k_AdvanceSpace = 0.34f;

        struct Stroke
        {
            public float x, y, w, h, angle;
            public bool rotated;
        }

        static Stroke S(float x, float y, float w, float h) =>
            new Stroke { x = x, y = y, w = w, h = h, rotated = false };

        static Stroke Rot(float cx, float cy, float w, float h, float angle) =>
            new Stroke { x = cx, y = cy, w = w, h = h, angle = angle, rotated = true };

        static List<Stroke> Glyph(char c)
        {
            const float W = k_GW, T = k_GT;
            var mid = 0.5f - T * 0.5f;

            switch (c)
            {
                case 'F': return new List<Stroke> { S(0, 0, T, 1), S(0, 1 - T, W, T), S(0, mid, W * 0.80f, T) };
                case 'U': return new List<Stroke> { S(0, T, T, 1 - T), S(W - T, T, T, 1 - T), S(0, 0, W, T) };
                case 'R': return new List<Stroke> { S(0, 0, T, 1), S(0, 1 - T, W, T), S(W - T, 0.52f, T, 1 - T - 0.52f), S(0, 0.52f, W, T), Rot(0.395f, 0.275f, T, 0.563f, 35f) };
                case 'N': return new List<Stroke> { S(0, 0, T, 1), S(W - T, 0, T, 1), Rot(0.31f, 0.5f, T, 1.0f, 17.2f) };
                case 'I': return new List<Stroke> { S((W - T) * 0.5f, 0, T, 1) };
                case 'T': return new List<Stroke> { S(0, 1 - T, W, T), S((W - T) * 0.5f, 0, T, 1 - T) };
                case 'E': return new List<Stroke> { S(0, 0, T, 1), S(0, 1 - T, W, T), S(0, mid, W * 0.82f, T), S(0, 0, W, T) };
                case 'D': return new List<Stroke> { S(0, 0, T, 1), S(0, 1 - T, W * 0.86f, T), S(0, 0, W * 0.86f, T), S(W - T, T * 0.9f, T, 1 - T * 1.8f) };
                case 'S': return new List<Stroke> { S(0, 1 - T, W, T), S(0, mid, T, 0.5f - T * 0.5f), S(0, mid, W, T), S(W - T, T, T, mid - T), S(0, 0, W, T) };
                case 'G': return new List<Stroke> { S(0, 1 - T, W, T), S(0, T, T, 1 - 2 * T), S(0, 0, W, T), S(W - T, 0, T, 0.46f), S(W * 0.42f, 0.40f, W * 0.58f, T) };
                case '&': return Ampersand();
                default: return new List<Stroke>();
            }
        }

        /// <summary>
        /// The ampersand is the one glyph built from a 5x7 bitmap rather than from strokes.
        /// Every stroke-built version of it read as a different character at this weight; the
        /// bitmap reads correctly, and at 2.7 m its stair-stepping is below what the headset
        /// resolves. Horizontal runs are merged so it costs 13 boxes rather than 20.
        /// </summary>
        static List<Stroke> Ampersand()
        {
            string[] rows = { "01100", "10010", "10100", "01000", "10101", "10010", "01101" };
            const float cell = 1f / 7f;

            var strokes = new List<Stroke>();
            for (var r = 0; r < rows.Length; r++)
            {
                var y = (rows.Length - 1 - r) * cell;
                var c = 0;
                while (c < rows[r].Length)
                {
                    if (rows[r][c] == '1')
                    {
                        var start = c;
                        while (c < rows[r].Length && rows[r][c] == '1') c++;
                        strokes.Add(S(start * cell, y, (c - start) * cell, cell));
                    }
                    else c++;
                }
            }
            return strokes;
        }

        static Mesh BuildSignMesh(string text, float capHeight, float depth)
        {
            // Lay the string out in normalised units first so the total width is known before
            // anything is placed, then centre it on x = 0.
            var placed = new List<Stroke>();
            var pen = 0f;

            foreach (var ch in text)
            {
                if (ch == ' ') { pen += k_AdvanceSpace; continue; }

                foreach (var s in Glyph(ch))
                {
                    var moved = s;
                    moved.x += pen;
                    placed.Add(moved);
                }
                pen += k_AdvanceLetter;
            }

            var width = pen - (k_AdvanceLetter - k_GW);
            var offsetX = -width * 0.5f;

            var cubeSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var cube = cubeSource.GetComponent<MeshFilter>().sharedMesh;

            var combine = new CombineInstance[placed.Count];
            for (var i = 0; i < placed.Count; i++)
            {
                var s = placed[i];
                Vector3 centre, size;
                Quaternion rotation;

                if (s.rotated)
                {
                    centre = new Vector3((s.x + offsetX) * capHeight, s.y * capHeight, 0f);
                    size = new Vector3(s.w * capHeight, s.h * capHeight, depth);
                    rotation = Quaternion.Euler(0f, 0f, s.angle);
                }
                else
                {
                    centre = new Vector3((s.x + s.w * 0.5f + offsetX) * capHeight,
                        (s.y + s.h * 0.5f) * capHeight, 0f);
                    size = new Vector3(s.w * capHeight, s.h * capHeight, depth);
                    rotation = Quaternion.identity;
                }

                combine[i] = new CombineInstance
                {
                    mesh = cube,
                    transform = Matrix4x4.TRS(centre, rotation, size),
                };
            }

            var mesh = new Mesh { name = "AreaA_SignText" };
            mesh.CombineMeshes(combine, true, true);
            mesh.RecalculateBounds();

            Object.DestroyImmediate(cubeSource);

            EnsureFolder("Assets/IKEA_EEG", "Meshes");
            AssetDatabase.DeleteAsset(k_SignMeshPath);
            AssetDatabase.CreateAsset(mesh, k_SignMeshPath);

            return mesh;
        }

        // =================================================================================
        // Materials — new assets only. No existing material is read, edited or replaced,
        // because M_Facade, M_Accent and M_DoorFrame are shared with Area C.
        // =================================================================================

        class Mats
        {
            public Material blue, blueAlt, yellow, silver, charcoal, glass, pavement,
                asphalt, skyline;
        }

        static Mats BuildMaterials()
        {
            return new Mats
            {
                blue = Mat("M_AreaA_Facade_Blue", new Color(0.086f, 0.325f, 0.620f), 0f, 0.28f),
                yellow = Mat("M_AreaA_Accent_Yellow", new Color(0.980f, 0.770f, 0.050f), 0f, 0.32f),
                silver = Mat("M_AreaA_Metal_Silver", new Color(0.780f, 0.800f, 0.830f), 0.55f, 0.62f),

                // Dark grey rather than near-black. At 0.145 the portal, the doors and the
                // existing Doorway_Frame_A behind them merged into one unreadable black mass.
                charcoal = Mat("M_AreaA_Dark_Charcoal", new Color(0.225f, 0.235f, 0.255f), 0f, 0.20f),

                // OPAQUE, not transparent. The existing blue Facade_A_DoorBlocker keeps its
                // renderer, so a see-through pane would show a blue wall where the reference
                // shows a dark showroom. Opaque glass also avoids transparency sorting, which
                // is the expensive and artefact-prone case in stereo.
                //
                // NON-METALLIC and only mid-smooth on purpose. There is no reflection probe in
                // this scene and ambient is flat, so a smooth metallic pane receives almost no
                // ambient light and renders black — which is exactly what the first pass did.
                glass = Mat("M_AreaA_Glass_Dark", new Color(0.215f, 0.260f, 0.315f), 0f, 0.55f),
                pavement = Mat("M_AreaA_Pavement_Light", new Color(0.800f, 0.805f, 0.810f), 0f, 0.12f),

                // Three flat colours added for Block 2. No textures, so the cost is three
                // material slots and nothing else.
                blueAlt = Mat("M_AreaA_Facade_Blue_Alt", new Color(0.070f, 0.290f, 0.565f), 0f, 0.34f),
                asphalt = Mat("M_AreaA_Asphalt", new Color(0.205f, 0.210f, 0.220f), 0f, 0.08f),

                // Muted and slightly blued, so distance reads as atmosphere rather than as more
                // building. It must recede behind the storefront, never compete with it.
                skyline = Mat("M_AreaA_Skyline", new Color(0.585f, 0.620f, 0.670f), 0f, 0.06f),
            };
        }

        static Material Mat(string assetName, Color color, float metallic, float smoothness)
        {
            var path = $"{k_MaterialsFolder}/{assetName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
                Debug.LogWarning("[IKEA_EEG] URP Lit shader not found; Area A materials fall back " +
                                 $"to '{shader?.name}'.");
            }

            if (material == null)
            {
                material = new Material(shader) { name = assetName };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);

            EditorUtility.SetDirty(material);
            return material;
        }

        // =================================================================================
        // Helpers
        // =================================================================================

        static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        static Transform Group(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>
        /// One box, given as min/max corners in the parent's local space.
        ///
        /// The primitive's BoxCollider is destroyed immediately. That is the single most
        /// important line in this file: the far interactor casts 10 m from the controller, and
        /// a collider anywhere in front of the UI would silently swallow selections.
        /// </summary>
        static GameObject Box(string name, Transform parent, Vector3 min, Vector3 max,
            Material material, bool markStatic = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (min + max) * 0.5f;
            go.transform.localScale = max - min;

            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && material != null)
                renderer.sharedMaterial = material;

            if (markStatic)
                MarkStatic(go);

            return go;
        }

        static void MarkStatic(GameObject go)
        {
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic |
                                                      StaticEditorFlags.OccluderStatic |
                                                      StaticEditorFlags.OccludeeStatic);
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }

        static Transform FindAreaARoot()
        {
            var environment = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .FirstOrDefault(t => t.parent == null && t.name == "Environment");

            return environment == null ? null : environment.Find("Area_A_Entrance");
        }

        // =================================================================================
        // Validation
        // =================================================================================

        /// <summary>
        /// Checks the properties this environment is only allowed to have: no behaviour, no
        /// physics, no interaction, and no surface in front of the UI or the response buttons.
        /// </summary>
        public static string Validate()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== AREA A VISUAL ENVIRONMENT VALIDATION =====");

            var areaA = FindAreaARoot();
            if (areaA == null)
            {
                sb.AppendLine($"  FAIL  '{AreaARootPath}' not found in the open scene.");
                return sb.ToString();
            }

            var root = areaA.Find(VisualRootName);
            if (root == null)
            {
                sb.AppendLine($"  FAIL  '{VisualRootName}' not found under Area_A_Entrance.");
                return sb.ToString();
            }

            var problems = 0;

            // ---- placement -------------------------------------------------------------
            if (root.localPosition == Vector3.zero && root.localRotation == Quaternion.identity &&
                root.localScale == Vector3.one)
                sb.AppendLine("  ok    AreaA_Visuals is at local position 0, rotation 0, scale 1");
            else
            {
                sb.AppendLine($"  FAIL  AreaA_Visuals transform is not identity: " +
                              $"pos={root.localPosition} rot={root.localRotation.eulerAngles} scale={root.localScale}");
                problems++;
            }

            // ---- nothing but renderers -------------------------------------------------
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            problems += Expect(sb, "MonoBehaviour under AreaA_Visuals", behaviours.Length, 0);
            problems += Expect(sb, "Collider under AreaA_Visuals", root.GetComponentsInChildren<Collider>(true).Length, 0);
            problems += Expect(sb, "Light under AreaA_Visuals", root.GetComponentsInChildren<Light>(true).Length, 0);
            problems += Expect(sb, "Canvas under AreaA_Visuals", root.GetComponentsInChildren<Canvas>(true).Length, 0);
            problems += Expect(sb, "Rigidbody under AreaA_Visuals", root.GetComponentsInChildren<Rigidbody>(true).Length, 0);
            problems += Expect(sb, "Camera under AreaA_Visuals", root.GetComponentsInChildren<Camera>(true).Length, 0);
            problems += Expect(sb, "AudioSource under AreaA_Visuals", root.GetComponentsInChildren<AudioSource>(true).Length, 0);
            problems += Expect(sb, "Animator under AreaA_Visuals", root.GetComponentsInChildren<Animator>(true).Length, 0);

            // ---- only one new child on Area_A_Entrance ---------------------------------
            var expected = new[]
            {
                "Floor_A", "StoreFacade_A", "Wall_A_Left", "Wall_A_Right", "Wall_A_Back",
                "Spawn_A", "UI_A_Canvas", VisualRootName,
            };

            var unexpected = new List<string>();
            for (var i = 0; i < areaA.childCount; i++)
            {
                var n = areaA.GetChild(i).name;
                if (!expected.Contains(n)) unexpected.Add(n);
            }

            if (unexpected.Count == 0 && areaA.childCount == expected.Length)
                sb.AppendLine($"  ok    Area_A_Entrance has exactly its 7 original children plus {VisualRootName}");
            else
            {
                sb.AppendLine($"  FAIL  Area_A_Entrance children are {areaA.childCount}: " +
                              $"unexpected = [{string.Join(", ", unexpected)}]");
                problems++;
            }

            // ---- clearance -------------------------------------------------------------
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            var violations = 0;
            var minAboveGroundZ = float.MaxValue;
            var maxZ = float.MinValue;

            var exempt = 0;

            foreach (var r in renderers)
            {
                var b = r.bounds;                                // world space; Area A is at the origin
                var isGround = b.max.y <= k_GroundMaxY + 1e-4f;  // RULE 2 objects

                // RULE 1 EXEMPTION: geometry that lies entirely outside the UI corridor cannot
                // occlude a panel spanning |x| <= 0.71, whatever its z. Applied by bounds, so an
                // object that strays back into the corridor is caught rather than assumed safe.
                var isLateral = b.min.x >= k_LateralExemptionX || b.max.x <= -k_LateralExemptionX;

                if (isGround)
                {
                    // Ground is excluded from the door-blocker depth check below as well as
                    // from the front-clearance one: the plaza deliberately runs underneath the
                    // storefront, passing below the blocker rather than through it.
                    continue;
                }

                if (k_ClearanceExemptByName.Contains(r.gameObject.name))
                {
                    exempt++;
                    continue;
                }

                // Entirely behind the participant, so it cannot come between them and the UI.
                if (b.max.z <= k_SpawnZ + 1e-4f)
                {
                    exempt++;
                    continue;
                }

                if (isLateral)
                {
                    exempt++;
                    maxZ = Mathf.Max(maxZ, b.max.z);
                    continue;
                }

                minAboveGroundZ = Mathf.Min(minAboveGroundZ, b.min.z);

                if (b.min.z < k_MinFrontZ - 1e-4f)
                {
                    sb.AppendLine($"  FAIL  '{r.name}' reaches z = {b.min.z:F3} — closer than the " +
                                  $"{k_MinFrontZ:F2} m limit and could occlude the UI.");
                    violations++;
                }

                maxZ = Mathf.Max(maxZ, b.max.z);
            }

            if (violations == 0)
                sb.AppendLine($"  ok    every non-ground surface in the UI corridor starts at or " +
                              $"behind z = {minAboveGroundZ:F3} (limit {k_MinFrontZ:F2}); " +
                              $"{exempt} object(s) exempt by |x| >= {k_LateralExemptionX:F1}");
            problems += violations;

            if (maxZ <= k_BlockerFrontZ + 1e-4f)
                sb.AppendLine($"  ok    nothing reaches the Facade_A_DoorBlocker front face " +
                              $"(max z = {maxZ:F3}, blocker at {k_BlockerFrontZ:F2})");
            else
            {
                sb.AppendLine($"  FAIL  geometry reaches z = {maxZ:F3}, intersecting the door blocker.");
                problems++;
            }

            // ---- the blocker and the doorway are untouched ------------------------------
            var facade = areaA.Find("StoreFacade_A");
            var blocker = facade == null ? null : facade.Find("Facade_A_DoorBlocker");

            if (blocker == null)
            {
                sb.AppendLine("  FAIL  Facade_A_DoorBlocker is missing.");
                problems++;
            }
            else
            {
                var hasCollider = blocker.GetComponent<Collider>() != null;
                var blockerRenderer = blocker.GetComponent<MeshRenderer>();
                var rendererOn = blockerRenderer != null && blockerRenderer.enabled;

                problems += Report(sb, hasCollider, "Facade_A_DoorBlocker still has its collider");
                problems += Report(sb, rendererOn, "Facade_A_DoorBlocker renderer is still enabled");
            }

            // ---- door leaves are separate and animatable --------------------------------
            var doors = root.Find("Doors");
            var leafL = doors == null ? null : doors.Find("Door_Leaf_Left");
            var leafR = doors == null ? null : doors.Find("Door_Leaf_Right");

            problems += Report(sb, leafL != null && leafR != null,
                "the two sliding leaves exist as separate transforms");

            if (leafL != null && leafR != null)
            {
                var batched = leafL.GetComponentsInChildren<MeshRenderer>(true)
                    .Concat(leafR.GetComponentsInChildren<MeshRenderer>(true))
                    .Count(r => GameObjectUtility.GetStaticEditorFlags(r.gameObject)
                                    .HasFlag(StaticEditorFlags.BatchingStatic));

                problems += Report(sb, batched == 0,
                    "leaf renderers are NOT batching-static, so they can be animated later");
            }

            // ---- graybox walls: invisible but still solid -------------------------------
            foreach (var wallName in k_GrayboxWalls)
            {
                var wall = areaA.Find(wallName);

                if (wall == null)
                {
                    sb.AppendLine($"  FAIL  {wallName} is missing from Area_A_Entrance.");
                    problems++;
                    continue;
                }

                var wallRenderer = wall.GetComponent<MeshRenderer>();
                var wallCollider = wall.GetComponent<BoxCollider>();

                problems += Report(sb, wallRenderer != null && !wallRenderer.enabled,
                    $"{wallName}: MeshRenderer is DISABLED");
                problems += Report(sb, wallCollider != null && wallCollider.enabled,
                    $"{wallName}: BoxCollider is still ENABLED");
                problems += Report(sb, wall.gameObject.activeSelf,
                    $"{wallName}: GameObject is still active");
                problems += Report(sb, !wallCollider.isTrigger,
                    $"{wallName}: collider is still solid (not a trigger)");
            }

            // ---- Floor_A is untouched, and no longer visible ----------------------------
            var floor = areaA.Find("Floor_A");

            if (floor == null)
            {
                sb.AppendLine("  FAIL  Floor_A is missing.");
                problems++;
            }
            else
            {
                var floorRenderer = floor.GetComponent<MeshRenderer>();
                var floorCollider = floor.GetComponent<BoxCollider>();

                problems += Report(sb, floorRenderer != null && floorRenderer.enabled,
                    "Floor_A renderer is untouched (still enabled) — it is occluded, not modified");
                problems += Report(sb, floorCollider != null && floorCollider.enabled,
                    "Floor_A collider is still enabled");

                var plaza = root.Find("Ground/Plaza_Concrete");
                var plazaRenderer = plaza == null ? null : plaza.GetComponent<MeshRenderer>();

                if (plazaRenderer == null || floorRenderer == null)
                {
                    sb.AppendLine("  FAIL  the entrance plaza is missing, so Floor_A is exposed.");
                    problems++;
                }
                else
                {
                    var fb = floorRenderer.bounds;
                    var pb = plazaRenderer.bounds;

                    var covers = pb.min.x <= fb.min.x && pb.max.x >= fb.max.x &&
                                 pb.min.z <= fb.min.z && pb.max.z >= fb.max.z &&
                                 pb.max.y > fb.max.y;

                    problems += Report(sb, covers,
                        $"the plaza fully covers Floor_A and sits above it " +
                        $"(plaza top {pb.max.y:F3} > floor top {fb.max.y:F3}) — no graybox floor visible");
                }
            }

            // ---- facade width -----------------------------------------------------------
            var cladding = root.Find("Facade_Cladding");

            if (cladding == null)
            {
                sb.AppendLine("  FAIL  Facade_Cladding is missing.");
                problems++;
            }
            else
            {
                var facadeBounds = new Bounds?();

                foreach (var r in cladding.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (facadeBounds == null) facadeBounds = r.bounds;
                    else { var v = facadeBounds.Value; v.Encapsulate(r.bounds); facadeBounds = v; }
                }

                if (facadeBounds != null)
                {
                    var width = facadeBounds.Value.size.x;
                    var top = facadeBounds.Value.max.y;

                    problems += Report(sb, Mathf.Abs(width - 28f) <= 1.0f,
                        $"the storefront is {width:F2} m wide (target ~28 m), entrance centred " +
                        $"at x = {facadeBounds.Value.center.x:F2}");

                    // The sky test. Anything taller than this at the facade plane fills the
                    // frame from the spawn mark and Area A stops reading as open air.
                    problems += Report(sb, top <= 4.40f,
                        $"the facade tops out at {top:F2} m, below the ~4.4 m ceiling that keeps " +
                        "sky visible from Spawn_A");
                }

                var returns = cladding.GetComponentsInChildren<Transform>(true)
                    .Count(t => t.name.StartsWith("Return_"));
                problems += Report(sb, returns >= 2,
                    $"the outer ends have side returns ({returns} pieces), so the building is a " +
                    "volume rather than a flat card");
            }

            // ---- three readable ground zones --------------------------------------------
            var zones = new[] { "Plaza_Concrete", "Kerb", "Road_Asphalt" };
            var zoneRenderers = new Dictionary<string, MeshRenderer>();

            foreach (var zone in zones)
            {
                var t = root.Find("Ground/" + zone);
                var r = t == null ? null : t.GetComponent<MeshRenderer>();
                if (r != null) zoneRenderers[zone] = r;

                problems += Report(sb, r != null, $"ground zone '{zone}' exists");
            }

            if (zoneRenderers.Count == 3)
            {
                var plazaTop = zoneRenderers["Plaza_Concrete"].bounds.max.y;
                var kerbTop = zoneRenderers["Kerb"].bounds.max.y;
                var roadTop = zoneRenderers["Road_Asphalt"].bounds.max.y;

                problems += Report(sb, kerbTop > plazaTop && plazaTop > roadTop,
                    $"the zones step correctly: road {roadTop:F3} < plaza {plazaTop:F3} < " +
                    $"kerb {kerbTop:F3}");

                problems += Report(sb,
                    zoneRenderers["Road_Asphalt"].sharedMaterial !=
                    zoneRenderers["Plaza_Concrete"].sharedMaterial,
                    "the road and the plaza use different materials, so the surfaces read apart");

                var roadWidth = zoneRenderers["Road_Asphalt"].bounds.size.x;
                problems += Report(sb, roadWidth >= 24f,
                    $"the road is {roadWidth:F1} m wide, comparable to the storefront");
            }

            // ---- skyline hides the other experiment areas -------------------------------
            var skyline = root.Find("Skyline");

            if (skyline == null)
            {
                sb.AppendLine("  FAIL  the distant skyline is missing.");
                problems++;
            }
            else
            {
                problems += Expect(sb, "Skyline renderers", skyline.GetComponentsInChildren<MeshRenderer>(true).Length, 1);
                problems += Expect(sb, "Collider under Skyline", skyline.GetComponentsInChildren<Collider>(true).Length, 0);
                problems += Expect(sb, "Light under Skyline", skyline.GetComponentsInChildren<Light>(true).Length, 0);

                // Ray-cast the actual sightlines rather than trusting the numbers.
                //
                // Tested against each skyline VOLUME separately. The combined mesh's bounds are
                // one enormous AABB spanning the whole scene, so testing that would report a hit
                // for every ray and prove nothing.
                var eye = new Vector3(0f, 1.60f, -0.70f);

                foreach (var (label, target) in new[]
                         {
                             ("Area 0", new Vector3(-100f, 1.5f, 0f)),
                             ("Area B", new Vector3(100f, 1.8f, 0f)),
                             ("Area C", new Vector3(200f, 2.1f, 0f)),
                         })
                {
                    var direction = (target - eye).normalized;
                    var distance = Vector3.Distance(eye, target);
                    var ray = new Ray(eye, direction);
                    var nearest = float.MaxValue;

                    foreach (var (min, max) in k_Skyline)
                    {
                        var b = new Bounds((min + max) * 0.5f, max - min);

                        if (b.IntersectRay(ray, out var hit) && hit < distance)
                            nearest = Mathf.Min(nearest, hit);
                    }

                    problems += Report(sb, nearest < float.MaxValue,
                        $"the sightline from Spawn_A to {label} is blocked by a skyline volume " +
                        $"at {(nearest < float.MaxValue ? nearest.ToString("F0") : "no")} m " +
                        $"of {distance:F0} m");
                }
            }

            // ---- exterior props ---------------------------------------------------------
            var props = root.Find("Exterior_Props");

            if (props == null)
            {
                sb.AppendLine("  FAIL  Exterior_Props group is missing.");
                problems++;
            }
            else
            {
                problems += Expect(sb, "exterior prop instances", props.childCount, k_Props.Length);
                problems += Expect(sb, "Collider under Exterior_Props",
                    props.GetComponentsInChildren<Collider>(true).Length, 0);
                problems += Expect(sb, "Light under Exterior_Props",
                    props.GetComponentsInChildren<Light>(true).Length, 0);

                var badMaterials = 0;
                var oversized = 0;

                foreach (var r in props.GetComponentsInChildren<MeshRenderer>(true))
                {
                    foreach (var material in r.sharedMaterials)
                    {
                        // A null material or a null/error shader is what renders magenta.
                        if (material == null || material.shader == null ||
                            material.shader.name.Contains("InternalErrorShader"))
                        {
                            sb.AppendLine($"  FAIL  '{r.name}' has a missing material or shader " +
                                          "— this is what renders as pink.");
                            badMaterials++;
                            continue;
                        }

                        foreach (var id in material.GetTexturePropertyNameIDs())
                        {
                            var tex = material.GetTexture(id);
                            if (tex != null && Mathf.Max(tex.width, tex.height) > k_MaxPropTextureSize)
                                oversized++;
                        }
                    }
                }

                problems += Report(sb, badMaterials == 0,
                    "every prop material resolves to a real shader (no pink materials)");
                problems += Report(sb, oversized == 0,
                    $"every prop texture is at or below {k_MaxPropTextureSize} px " +
                    $"({oversized} over budget)");

                foreach (Transform prop in props)
                {
                    var b = prop.GetComponentsInChildren<MeshRenderer>(true)
                        .Select(r => r.bounds)
                        .Aggregate(new Bounds?(), (acc, cur) =>
                        {
                            if (acc == null) return cur;
                            var v = acc.Value; v.Encapsulate(cur); return v;
                        });

                    if (b == null)
                        continue;

                    sb.AppendLine($"  info  {prop.name}: pos={prop.localPosition} " +
                                  $"yaw={prop.localEulerAngles.y:F0} " +
                                  $"size=({b.Value.size.x:F2}, {b.Value.size.y:F2}, {b.Value.size.z:F2}) " +
                                  $"x-range=[{b.Value.min.x:F2}, {b.Value.max.x:F2}]");
                }
            }

            // ---- inventory --------------------------------------------------------------
            var materials = renderers.Select(r => r.sharedMaterial)
                .Where(m => m != null).Distinct().ToList();

            sb.AppendLine($"  info  {renderers.Length} renderer(s), " +
                          $"{root.GetComponentsInChildren<Transform>(true).Length} transform(s), " +
                          $"{materials.Count} material(s): " +
                          string.Join(", ", materials.Select(m => m.name).OrderBy(n => n)));

            sb.AppendLine(problems == 0
                ? "[IKEA_EEG] AREA A VISUALS: PASS"
                : $"[IKEA_EEG] AREA A VISUALS: {problems} PROBLEM(S)");

            return sb.ToString();
        }

        static int Expect(StringBuilder sb, string label, int actual, int expected)
        {
            if (actual == expected)
            {
                sb.AppendLine($"  ok    {label}: {actual}");
                return 0;
            }

            sb.AppendLine($"  FAIL  {label}: found {actual}, expected {expected}");
            return 1;
        }

        static int Report(StringBuilder sb, bool ok, string label)
        {
            sb.AppendLine(ok ? $"  ok    {label}" : $"  FAIL  {label}");
            return ok ? 0 : 1;
        }
    }
}
