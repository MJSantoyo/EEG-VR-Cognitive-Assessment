using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// The Area B production art pass: real furniture prefabs dressed around the chair task.
    ///
    /// WHAT THIS IS. A showroom needs to look like a showroom, and the graybox did not. This
    /// instantiates real prefabs from the project's HouseholdPropsPack into two perimeter
    /// vignettes -- a kitchen display along the left wall, a living-room display along the
    /// right -- plus flat-pack dressing in the back corners and two pendant fixtures. It is
    /// DECORATION ONLY.
    ///
    /// WHAT IT MUST NEVER TOUCH. The six chairs, the six slots, Spawn_B, the UI canvases and
    /// every collider the task depends on. It only ever adds or removes objects under its own
    /// root, and re-materialises the ceiling panel. Everything it creates lives under one
    /// parent so the whole pass can be removed in a single step.
    ///
    /// THE TASK CONE IS PROTECTED BY CONSTRUCTION. The participant stands at Spawn_B
    /// (local z = -2.6) and the chairs occupy |x| <= 3.83 at z = 0.61..2.32. Every sightline
    /// from the participant to a chair therefore stays inside |x| < 3.83. Nothing here is
    /// placed closer to the centre than <see cref="k_KeepClearX"/> = 4.8 m while it is in the
    /// participant's depth range, so no prop can ever stand between the participant and a
    /// target. <see cref="Validate"/> re-checks that afterwards rather than trusting it.
    ///
    /// META QUEST 2 BUDGET. Every prop is a DEC_ variant: no rigidbodies, and each instance
    /// has its colliders stripped -- decoration that kept its colliders would both cost
    /// physics and, worse, block the XR ray before it reached a chair. No realtime light is
    /// added: the pendant fixtures are geometry only, and the room keeps its existing emissive
    /// strips. No transparent or custom-shader material is introduced.
    /// </summary>
    public static class AreaBShowroomDressing
    {
        /// <summary>Everything this pass creates hangs here, so it is removable in one step.</summary>
        public const string DressingRootName = "AreaB_Showroom_Dressing";

        const string k_PropsPack = "Assets/JeffamazedDev";
        const string k_ArchMaterials = "Assets/YughuesFreeArchitecturalMaterials";

        /// <summary>
        /// No decoration may come closer to the room's centre line than this while it sits in
        /// the participant's working depth. The outermost chair is at |x| = 3.83; this leaves
        /// almost a metre of margin beyond it.
        /// </summary>
        const float k_KeepClearX = 4.8f;

        /// <summary>The depth band the task occupies, from the participant to behind the chairs.</summary>
        const float k_TaskZMin = -3.0f;
        const float k_TaskZMax = 3.2f;

        [MenuItem("IKEA_EEG/Visuals/Dress Area B Showroom", false, 210)]
        public static void DressMenu()
        {
            var root = FindAreaBRoot();

            if (root == null)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    "Could not find Area_B_Showroom in the open scene.\n\n" +
                    "Open Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaB.unity first.", "OK");
                return;
            }

            Dress(root);
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            Debug.Log("[IKEA_EEG] Area B dressed. Scene is dirty — save it.\n" + Validate());
        }

        /// <summary>
        /// Batch entry point:
        /// -executeMethod IkeaEeg.EditorTools.AreaBShowroomDressing.DressFromCommandLine
        /// Opens the Area B room scene, dresses it, SAVES, then prints the validation report.
        /// </summary>
        public static void DressFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(
                ExperimentSceneBuilder.AreaBScenePath, OpenSceneMode.Single);

            Dress(FindAreaBRoot());

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log(Validate());
        }

        public static void ValidateFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.AreaBScenePath, OpenSceneMode.Single);
            Debug.Log(Validate());
        }

        static Transform FindAreaBRoot() =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "Area_B_Showroom");

        // =================================================================================
        // Build
        // =================================================================================

        public static void Dress(Transform areaB)
        {
            if (areaB == null)
                return;

            // Idempotent: the previous pass is removed wholesale rather than merged into.
            var existing = areaB.Find(DressingRootName);

            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var root = new GameObject(DressingRootName);
            root.transform.SetParent(areaB, false);
            root.transform.localPosition = Vector3.zero;

            BuildKitchenVignette(root.transform);
            BuildLivingVignette(root.transform);
            BuildFlatPackDressing(root.transform);
            BuildPendantFixtures(root.transform);

            // ---- retail density pass -------------------------------------------------------
            // The vignettes above read as a furnished room. These turn it into a SHOP: the same
            // cabinet and counter modules repeated along the free perimeter, merchandise grouped
            // on the worktops, and the wall the participant faces given a display run of its
            // own. Everything reuses the prefabs and the five materials already loaded above,
            // so the retail density costs meshes and renderers but no new material or shader.
            BuildKitchenDepartmentRun(root.transform);
            BuildFrontWallDisplayRun(root.transform);
            BuildHomewareDepartment(root.transform);
            BuildMerchandise(root.transform);

            ReMaterialiseCeiling(areaB);

            // One sweep over everything created: decoration carries no colliders, no physics
            // and no lights. Done once at the end so nothing can be missed by a placement
            // helper forgetting to ask.
            StripRuntimeCost(root.transform);
        }

        /// <summary>Kitchen display along the LEFT wall (inner face at local x = -6.875).</summary>
        static void BuildKitchenVignette(Transform parent)
        {
            var group = Group("Vignette_Kitchen", parent);

            // Counters are 0.62 m deep; rotated to face the room their depth runs along X, so
            // a centre of -6.50 leaves them clear of the wall without floating.
            Place(group, "DEC_KitchenCounterSink", new Vector3(-6.50f, 0f, 0.15f), 90f);
            Place(group, "DEC_KitchenCounterOven", new Vector3(-6.50f, 0f, 1.35f), 90f);
            Place(group, "DEC_KitchenCounterFourDrawers", new Vector3(-6.50f, 0f, 2.10f), 90f);
            Place(group, "DEC_KitchenCounterCorner", new Vector3(-6.50f, 0f, -0.75f), 90f);

            // Wall cabinets, 0.42 m deep, hung at worktop+0.65.
            Place(group, "DEC_KitchenCabinetDouble", new Vector3(-6.66f, 1.55f, 0.55f), 90f);
            Place(group, "DEC_KitchenCabinetSingle", new Vector3(-6.66f, 1.55f, 1.85f), 90f);

            // DEC_Refrigerator IS DELIBERATELY NOT USED. Its main mesh is 1.38 m^2 drawn with
            // TWO alpha-blended materials (Transparent_70 and Transparent_2, both in render
            // queue 3000), which made it by far the largest source of overdraw in this pass --
            // and on Quest 2 overdraw is the budget that bites first. Placed against the left
            // wall it also sat level with the participant, outside the view from Spawn_B, so
            // it was paying that cost for something nobody would see. The kitchen run reads as
            // a kitchen without it.

            // Worktop dressing. The counters are 0.90 m high.
            Place(group, "DEC_Microwave", new Vector3(-6.45f, 0.90f, 2.10f), 90f);
            Place(group, "DEC_CookingPot", new Vector3(-6.45f, 0.90f, 1.35f), 0f);
        }

        /// <summary>Living-room display along the RIGHT wall (inner face at local x = +6.875).</summary>
        static void BuildLivingVignette(Transform parent)
        {
            var group = Group("Vignette_Living", parent);

            // Sofa is 2.10 x 0.88 x 0.90; rotated to face the room its 0.90 depth runs along X.
            Place(group, "DEC_SofaThreeSeats", new Vector3(6.30f, 0f, 1.20f), -90f);
            Place(group, "DEC_SofaPillow", new Vector3(6.25f, 0.46f, 0.55f), -90f);
            Place(group, "DEC_SofaPillow", new Vector3(6.25f, 0.46f, 1.85f), -90f);

            Place(group, "DEC_CoffeeTable", new Vector3(5.25f, 0f, 1.20f), 90f);

            // Wall-mounted TV opposite the sofa, on the right wall.
            Place(group, "DEC_FlatTVWall_MOUNTED", new Vector3(6.80f, 1.70f, 3.70f), -90f);
        }

        /// <summary>
        /// Flat-pack boxes in the two BACK corners, behind the chair arc.
        ///
        /// Cheap, instantly reads as a furniture warehouse, and sits at z >= 4.8 where it can
        /// never intrude on the task. Stacked in pairs rather than scattered.
        /// </summary>
        static void BuildFlatPackDressing(Transform parent)
        {
            var group = Group("Dressing_FlatPack", parent);

            Place(group, "DEC_CardboardBox_CLOSED", new Vector3(-5.80f, 0f, 5.20f), 12f);
            Place(group, "DEC_CardboardBox_CLOSED", new Vector3(-5.80f, 0.30f, 5.20f), -6f);
            Place(group, "DEC_CardboardBox_OPENED", new Vector3(-5.05f, 0f, 5.35f), 34f);

            Place(group, "DEC_CardboardBox_CLOSED", new Vector3(5.75f, 0f, 5.30f), -18f);
            Place(group, "DEC_CardboardBox_CLOSED", new Vector3(5.75f, 0.30f, 5.30f), 8f);
        }

        /// <summary>
        /// Two pendant fixtures, over the vignettes and NOT over the chairs.
        ///
        /// Geometry only. They add no Light component -- the room is already lit by the three
        /// emissive ceiling strips, and putting realtime lights on Quest 2 to make a fixture
        /// look plausible is the wrong trade. Hung so their lowest point stays above 2.2 m.
        /// </summary>
        static void BuildPendantFixtures(Transform parent)
        {
            var group = Group("Fixtures_Pendant", parent);

            Place(group, "DEC_DiningPendantLight", new Vector3(-6.00f, 3.50f, 1.20f), 0f);
            Place(group, "DEC_DiningPendantLight", new Vector3(6.00f, 3.50f, 1.20f), 0f);
        }

        /// <summary>
        /// Extends the kitchen into a full department run along the LEFT wall.
        ///
        /// A real showroom sells the same module over and over, so this repeats the counter and
        /// wall-cabinet pair the vignette already uses rather than introducing new furniture
        /// types. The left wall runs from z = -6.4 to 6.4 and the original vignette occupies
        /// only z = -0.75..2.40; this fills the two remaining stretches, which is where most of
        /// the room's emptiness was.
        /// </summary>
        static void BuildKitchenDepartmentRun(Transform parent)
        {
            var group = Group("Retail_KitchenRun", parent);

            // Behind the vignette, running toward the front wall.
            Place(group, "DEC_KitchenCounterSingle", new Vector3(-6.50f, 0f, 2.85f), 90f);
            Place(group, "DEC_KitchenCounterDouble", new Vector3(-6.50f, 0f, 3.90f), 90f);
            Place(group, "DEC_KitchenCounterSingle", new Vector3(-6.50f, 0f, 4.95f), 90f);

            Place(group, "DEC_KitchenCabinetSingle", new Vector3(-6.66f, 1.55f, 2.85f), 90f);
            Place(group, "DEC_KitchenCabinetDouble", new Vector3(-6.66f, 1.55f, 3.90f), 90f);
            Place(group, "DEC_KitchenCabinetSingle", new Vector3(-6.66f, 1.55f, 4.95f), 90f);

            // In front of the vignette, toward the back wall behind the participant.
            Place(group, "DEC_KitchenCounterDouble", new Vector3(-6.50f, 0f, -2.05f), 90f);
            Place(group, "DEC_KitchenCounterSingle", new Vector3(-6.50f, 0f, -3.00f), 90f);
            Place(group, "DEC_KitchenCabinetDouble", new Vector3(-6.66f, 1.55f, -2.05f), 90f);

            Place(group, "DEC_KitchenTrashCan", new Vector3(-6.45f, 0f, 5.70f), 90f);
        }

        /// <summary>
        /// A display run across the FRONT wall — the one the participant is looking at.
        ///
        /// This is the single highest-value surface in the room and it was bare. Low display
        /// cabinets with uppers above them give the far wall the layered, stocked look of a
        /// store aisle and add real depth behind the chair arc.
        ///
        /// THE CENTRE IS LEFT ALONE. The instruction and status canvases sit at z = 5.60
        /// spanning x = -1.90..1.90, and the chairs are read against this wall. Nothing here
        /// comes inside |x| = 3.0, so the UI keeps a clear margin on both sides and no cabinet
        /// ever sits directly behind a chair.
        /// </summary>
        static void BuildFrontWallDisplayRun(Transform parent)
        {
            var group = Group("Retail_FrontWallDisplay", parent);

            foreach (var x in new[] { -5.90f, -4.50f, 4.50f, 5.90f })
            {
                Place(group, "DEC_KitchenCabinetDouble", new Vector3(x, 0f, 6.15f), 180f);
                Place(group, "DEC_KitchenCabinetDouble", new Vector3(x, 1.55f, 6.15f), 180f);
            }
        }

        /// <summary>
        /// A storage-and-homeware department on the RIGHT wall, in front of the sofa.
        ///
        /// WHAT THIS REPLACED. This stretch used to be two more televisions plus a standing
        /// one. Screens turned out to be a poor way to fill a wall: they are large flat black
        /// rectangles that read as an empty surface from across the room, and three of them
        /// made the side look like an electronics aisle rather than a home-goods floor. Only
        /// the wall TV above the sofa survives, where it belongs to the living-room vignette
        /// instead of being the whole answer for this side.
        ///
        /// WHAT IT IS NOW. The same low-unit plus upper-unit module the kitchen department
        /// uses, repeated down the wall as storage furniture and stocked with merchandise.
        /// Repetition is what reads as retail, and reusing the module means this whole
        /// department costs no new mesh and no new material.
        ///
        /// The dining table stays as a DISPLAY TABLE, dressed with product so it reads as a
        /// plinth rather than furniture to sit at, and moved inboard to x = 5.55 so it clears
        /// the new storage run behind it. No chair goes near it: every chair-shaped object in
        /// this room is still an experimental target.
        /// </summary>
        static void BuildHomewareDepartment(Transform parent)
        {
            var group = Group("Retail_Homeware", parent);

            // Low storage units, backs to the wall (inner face x = 6.88).
            Place(group, "DEC_KitchenCounterDouble", new Vector3(6.50f, 0f, -1.15f), -90f);
            Place(group, "DEC_KitchenCounterSingle", new Vector3(6.50f, 0f, -2.10f), -90f);
            Place(group, "DEC_KitchenCounterDouble", new Vector3(6.50f, 0f, -3.05f), -90f);

            // Upper shelving above them, giving the wall two stocked levels.
            Place(group, "DEC_KitchenCabinetDouble", new Vector3(6.66f, 1.55f, -1.15f), -90f);
            Place(group, "DEC_KitchenCabinetSingle", new Vector3(6.66f, 1.55f, -2.10f), -90f);
            Place(group, "DEC_KitchenCabinetDouble", new Vector3(6.66f, 1.55f, -3.05f), -90f);

            // The run continues past the shelving as plain storage units, so the wall does not
            // stop dead halfway down.
            Place(group, "DEC_KitchenCounterSingle", new Vector3(6.50f, 0f, -4.00f), -90f);
            Place(group, "DEC_KitchenCabinetSingle", new Vector3(6.66f, 1.55f, -4.00f), -90f);

            // A storage product on the floor at the end of the run.
            Place(group, "DEC_KitchenTrashCan", new Vector3(6.45f, 0f, -4.80f), -90f);

            // The display table, clear of the run behind it.
            Place(group, "DEC_DiningTable", new Vector3(5.55f, 0f, -1.70f), 90f);

            // Packaged product in the corner, the flat-pack read.
            //
            // Kept to a TWO-high stack plus two boxes on the floor. A three-high tower of
            // 0.30 m boxes at mixed rotations read as precarious rather than stacked -- the
            // corners no longer lined up and it looked like clipping, which is worse than
            // leaving the corner emptier.
            Place(group, "DEC_CardboardBox_CLOSED", new Vector3(6.10f, 0f, -5.65f), -14f);
            Place(group, "DEC_CardboardBox_CLOSED", new Vector3(6.10f, 0.30f, -5.65f), -8f);
            Place(group, "DEC_CardboardBox_CLOSED", new Vector3(5.48f, 0f, -5.90f), 26f);
            Place(group, "DEC_CardboardBox_OPENED", new Vector3(5.55f, 0f, -5.15f), -12f);
        }

        /// <summary>
        /// Grouped merchandise on the worktops and the display table.
        ///
        /// Deliberately arranged in small families -- a stack of plates, a pair of mugs, a
        /// bowl -- rather than sprinkled one-per-surface. Grouping is what makes a surface read
        /// as stocked; scattering just reads as mess, and on Quest 2 it would be paying renderer
        /// cost for noise. Counters are 0.90 m high and the display table 0.75 m.
        /// </summary>
        static void BuildMerchandise(Transform parent)
        {
            var group = Group("Retail_Merchandise", parent);

            // Kitchen run, behind the vignette.
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(-6.45f, 0.90f, 2.85f), 0f);
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(-6.45f, 0.92f, 2.85f), 18f);
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(-6.45f, 0.94f, 2.85f), -12f);
            Place(group, "DEC_CerealBowl", new Vector3(-6.45f, 0.90f, 3.20f), 0f);

            Place(group, "DEC_CoffeeMug", new Vector3(-6.50f, 0.90f, 3.70f), 24f);
            Place(group, "DEC_CoffeeMug", new Vector3(-6.36f, 0.90f, 3.95f), -40f);

            Place(group, "DEC_NonStickPan", new Vector3(-6.45f, 0.90f, 4.95f), 90f);
            Place(group, "DEC_DryingRack", new Vector3(-6.45f, 0.90f, -2.05f), 90f);

            // Display table in the homeware department, at its new inboard position.
            Place(group, "DEC_RegularDrinkingGlass_EMPTY", new Vector3(5.45f, 0.75f, -1.35f), 0f);
            Place(group, "DEC_RegularDrinkingGlass_EMPTY", new Vector3(5.62f, 0.75f, -1.48f), 0f);
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(5.55f, 0.75f, -2.00f), 0f);
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(5.55f, 0.77f, -2.00f), 22f);

            // Stock on the homeware storage run. Grouped in families, same as the kitchen: a
            // surface with one object on it looks forgotten, a surface with a group looks
            // stocked, and the group costs the same shared meshes and materials.
            Place(group, "DEC_CookingPot", new Vector3(6.45f, 0.90f, -1.05f), 20f);
            Place(group, "DEC_NonStickPan", new Vector3(6.45f, 0.90f, -1.45f), -90f);

            Place(group, "DEC_CeramicDinnerPlate", new Vector3(6.45f, 0.90f, -2.10f), 0f);
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(6.45f, 0.92f, -2.10f), -15f);
            Place(group, "DEC_CerealBowl", new Vector3(6.45f, 0.94f, -2.10f), 0f);

            Place(group, "DEC_CoffeeMug", new Vector3(6.52f, 0.90f, -2.80f), 35f);
            Place(group, "DEC_CoffeeMug", new Vector3(6.38f, 0.90f, -3.00f), -25f);
            Place(group, "DEC_DryingRack", new Vector3(6.45f, 0.90f, -3.35f), -90f);
        }

        /// <summary>
        /// Swaps the ceiling panel onto the architectural ceiling material.
        ///
        /// A sharedMaterial swap on an object this pass does not own, and the ONLY such swap:
        /// the walls keep M_AreaB_Wall_OffWhite because it stays cleaner behind the UI, and
        /// the floor keeps its existing parquet because the architectural wood flooring tested
        /// darker and coarser and reduced the contrast the chairs depend on.
        /// </summary>
        static void ReMaterialiseCeiling(Transform areaB)
        {
            var panel = areaB.GetComponentsInChildren<MeshRenderer>(true)
                .FirstOrDefault(r => r.name == "Ceiling_Panel");

            var mat = LoadMaterial("M_YFAM_Ceiling");

            if (panel != null && mat != null)
                panel.sharedMaterial = mat;
        }

        // =================================================================================
        // Helpers
        // =================================================================================

        static Transform Group(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static GameObject Place(Transform parent, string prefabName, Vector3 localPosition,
            float yawDegrees)
        {
            var guid = AssetDatabase.FindAssets($"{prefabName} t:Prefab", new[] { k_PropsPack })
                .FirstOrDefault(g => System.IO.Path.GetFileNameWithoutExtension(
                    AssetDatabase.GUIDToAssetPath(g)) == prefabName);

            if (guid == null)
            {
                Debug.LogWarning($"[IKEA_EEG] Art pass: prefab '{prefabName}' not found in " +
                                 $"{k_PropsPack}; that piece was skipped.");
                return null;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);

            instance.transform.localPosition = localPosition;
            instance.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);

            return instance;
        }

        static Material LoadMaterial(string name)
        {
            var guid = AssetDatabase.FindAssets($"{name} t:Material", new[] { k_ArchMaterials })
                .FirstOrDefault();

            return guid == null
                ? null
                : AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
        }

        /// <summary>
        /// Removes every collider, rigidbody and light from the dressing.
        ///
        /// The colliders matter most and not for performance: the HouseholdPropsPack ships
        /// DEC_ prefabs with dozens of mesh colliders each (the sink counter alone has 69), and
        /// a decorative collider standing in the room is something the XR ray can hit INSTEAD
        /// of a chair. Stripping them is what keeps the decoration incapable of touching the
        /// task. Done on the instances, so the third-party prefabs on disk are untouched.
        /// </summary>
        static void StripRuntimeCost(Transform root)
        {
            foreach (var c in root.GetComponentsInChildren<Collider>(true).ToList())
                Object.DestroyImmediate(c, true);

            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true).ToList())
                Object.DestroyImmediate(rb, true);

            foreach (var l in root.GetComponentsInChildren<Light>(true).ToList())
                Object.DestroyImmediate(l, true);
        }

        // =================================================================================
        // Validation
        // =================================================================================

        /// <summary>
        /// The dressing is decoration and stays decoration.
        ///
        /// Asserts the things that would make it something else: a collider the XR ray could
        /// hit, physics, a realtime light, or a prop standing in the participant's line to a
        /// chair. Also re-counts the task objects, because the whole point is that an art pass
        /// leaves them exactly as it found them.
        /// </summary>
        public static string Validate()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== AREA B SHOWROOM DRESSING =====");

            var problems = 0;
            var areaB = FindAreaBRoot();

            if (areaB == null)
            {
                sb.AppendLine("  FAIL  Area_B_Showroom is not in the open scene.");
                return sb.ToString();
            }

            var root = areaB.Find(DressingRootName);

            if (root == null)
            {
                sb.AppendLine($"  FAIL  '{DressingRootName}' is missing.");
                return sb.ToString();
            }

            // ---- cost ------------------------------------------------------------------
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            var props = root.Cast<Transform>().Sum(g => g.childCount);

            var tris = filters.Where(f => f.sharedMesh != null)
                .Sum(f => f.sharedMesh.triangles.Length / 3);

            var mats = renderers.SelectMany(r => r.sharedMaterials)
                .Where(m => m != null).Distinct().ToList();

            sb.AppendLine($"  info  props={props} renderers={renderers.Length} " +
                          $"triangles={tris} uniqueMaterials={mats.Count}");

            foreach (var m in mats)
                sb.AppendLine($"  info  material '{m.name}' shader={m.shader.name}");

            // ---- decoration must stay decoration -----------------------------------------
            problems += Expect(sb, "colliders in the dressing",
                root.GetComponentsInChildren<Collider>(true).Length, 0,
                "a decorative collider can absorb the XR ray meant for a chair");

            problems += Expect(sb, "rigidbodies in the dressing",
                root.GetComponentsInChildren<Rigidbody>(true).Length, 0,
                "decoration must introduce no physics");

            problems += Expect(sb, "realtime lights in the dressing",
                root.GetComponentsInChildren<Light>(true).Length, 0,
                "Quest 2 cannot afford extra realtime lights");

            // ---- nothing may stand in the task cone ---------------------------------------
            var intruders = new List<string>();

            foreach (var r in renderers)
            {
                var b = r.bounds;
                var localMinX = b.min.x - areaB.position.x;
                var localMaxX = b.max.x - areaB.position.x;
                var localMinZ = b.min.z - areaB.position.z;
                var localMaxZ = b.max.z - areaB.position.z;

                var insideDepth = localMaxZ > k_TaskZMin && localMinZ < k_TaskZMax;
                var insideCone = localMinX < k_KeepClearX && localMaxX > -k_KeepClearX;

                if (insideDepth && insideCone)
                    intruders.Add($"{r.name} x=[{localMinX:F2},{localMaxX:F2}] z=[{localMinZ:F2},{localMaxZ:F2}]");
            }

            if (intruders.Count > 0)
            {
                sb.AppendLine($"  FAIL  {intruders.Count} decorative renderer(s) reach inside the " +
                              $"task cone (|x| < {k_KeepClearX} between z {k_TaskZMin} and {k_TaskZMax}):");

                foreach (var i in intruders.Take(8))
                    sb.AppendLine($"           {i}");

                problems += 1;
            }
            else
            {
                sb.AppendLine($"  ok    no decoration inside the task cone " +
                              $"(|x| < {k_KeepClearX}, z {k_TaskZMin}..{k_TaskZMax})");
            }

            // ---- the task itself is untouched ---------------------------------------------
            problems += Expect(sb, "ChairTarget", Object.FindObjectsByType<Interaction.ChairTarget>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length, 6,
                "the art pass must not add or remove a chair");

            problems += Expect(sb, "ChairSlot", Object.FindObjectsByType<Interaction.ChairSlot>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length, 6,
                "the art pass must not add or remove a slot");

            problems += Expect(sb, "SpawnPoint", Object.FindObjectsByType<XR.SpawnPoint>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length, 1,
                "Spawn_B must survive the art pass");

            // ---- no missing meshes or materials -------------------------------------------
            var nullMats = renderers.Count(r => r.sharedMaterials.Any(m => m == null));
            var nullMeshes = filters.Count(f => f.sharedMesh == null);

            problems += Expect(sb, "renderers with a null material", nullMats, 0,
                "a null material renders magenta on device");
            problems += Expect(sb, "mesh filters with no mesh", nullMeshes, 0,
                "a missing mesh means a broken prefab reference");

            sb.AppendLine(problems == 0
                ? "[IKEA_EEG] AREA B DRESSING PASSED — decoration only, task untouched."
                : $"[IKEA_EEG] AREA B DRESSING FOUND {problems} PROBLEM(S).");

            return sb.ToString();
        }

        static int Expect(StringBuilder sb, string label, int actual, int expected, string why)
        {
            if (actual == expected)
            {
                sb.AppendLine($"  ok    {label}: {actual}");
                return 0;
            }

            sb.AppendLine($"  FAIL  {label}: {actual} (expected {expected}) — {why}");
            return 1;
        }
    }
}
