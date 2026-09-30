using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// The Area C production art pass: a calm exit lounge for the closing stage.
    ///
    /// AREA C IS NOT A SECOND SHOWROOM. It is a 4.5 x 4.0 m vestibule where the participant
    /// stands at z = -0.70 and reads a UI panel 2.1 m in front of them, through three quiet
    /// moments in a row: delayed recognition, POST_TASK_REST with a fixation point, and the
    /// results screen. Area B's answer -- fill the perimeter with merchandise -- would be
    /// actively wrong here. Retail density in a room this size would crowd the participant and
    /// compete with exactly the surfaces they are supposed to be reading.
    ///
    /// SO THIS PASS IS DELIBERATELY SMALL. Eight props, all against the two side walls, plus
    /// the one piece of structure the room was actually missing: a ceiling.
    ///
    /// THE CEILING IS THE REAL FIX. Area C shipped with walls to 3.0 m, a storefront facade to
    /// 4.2 m, and nothing overhead at all -- looking up showed open sky in what is supposed to
    /// be an interior. That single omission did more to make the room feel unfinished than any
    /// missing prop, and closing it costs three renderers.
    ///
    /// WHAT IT MUST NEVER TOUCH. Spawn_C, UI_C_Canvas, the recognition anchor at (0, 0.95,
    /// 1.18) where the shared response panel parks, and every collider the shell already has.
    /// Everything created here hangs under one root and carries no collider, no rigidbody and
    /// no light.
    /// </summary>
    public static class AreaCExitLoungeDressing
    {
        public const string DressingRootName = "AreaC_ExitLounge_Dressing";

        const string k_PropsPack = "Assets/JeffamazedDev";

        /// <summary>
        /// The participant's reading corridor. Nothing decorative may come inside this in x
        /// while it sits in the depth band below.
        ///
        /// UI_C_Canvas spans x = -0.76..0.76 at z = 1.40 and the recognition pair parks at
        /// x = 0. Holding decoration outside |x| = 1.10 leaves a third of a metre of clear
        /// margin either side of the widest thing they have to read.
        /// </summary>
        const float k_KeepClearX = 1.10f;

        const float k_TaskZMin = -1.00f;
        const float k_TaskZMax = 2.00f;

        // Shell, measured from the scene: inner faces of the side walls and the ceiling height.
        const float k_LeftWallInner = -2.13f;
        const float k_RightWallInner = 2.13f;
        const float k_CeilingY = 3.00f;

        [MenuItem("IKEA_EEG/Visuals/Dress Area C Exit Lounge", false, 211)]
        public static void DressMenu()
        {
            var root = FindAreaCRoot();

            if (root == null)
            {
                EditorUtility.DisplayDialog("IKEA_EEG",
                    "Could not find Area_C_Exit in the open scene.\n\n" +
                    "Open Assets/IKEA_EEG/Scenes/IKEA_EEG_AreaC.unity first.", "OK");
                return;
            }

            Dress(root);
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            Debug.Log("[IKEA_EEG] Area C dressed. Scene is dirty — save it.\n" + Validate());
        }

        /// <summary>
        /// Batch entry point:
        /// -executeMethod IkeaEeg.EditorTools.AreaCExitLoungeDressing.DressFromCommandLine
        /// </summary>
        public static void DressFromCommandLine()
        {
            var scene = EditorSceneManager.OpenScene(
                ExperimentSceneBuilder.AreaCScenePath, OpenSceneMode.Single);

            Dress(FindAreaCRoot());

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log(Validate());
        }

        public static void ValidateFromCommandLine()
        {
            EditorSceneManager.OpenScene(ExperimentSceneBuilder.AreaCScenePath, OpenSceneMode.Single);
            Debug.Log(Validate());
        }

        static Transform FindAreaCRoot() =>
            Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "Area_C_Exit");

        // =================================================================================
        // Build
        // =================================================================================

        public static void Dress(Transform areaC)
        {
            if (areaC == null)
                return;

            var existing = areaC.Find(DressingRootName);

            if (existing != null)
                Object.DestroyImmediate(existing.gameObject);

            var root = new GameObject(DressingRootName);
            root.transform.SetParent(areaC, false);
            root.transform.localPosition = Vector3.zero;

            BuildCeiling(root.transform);
            BuildLoungeSide(root.transform);
            BuildConsoleSide(root.transform);

            ReMaterialiseShell(areaC);

            StripRuntimeCost(root.transform);
        }

        /// <summary>
        /// Closes the room overhead and gives it two soft light lines.
        ///
        /// PRIMITIVES ARE THE RIGHT ANSWER HERE, and the only place this pass uses them: a
        /// ceiling is a flat rectangle, and no prefab in the library is one. The two strips
        /// reuse Area B's emissive light-strip material, so the rooms read as the same
        /// building and the lighting stays free -- they are emissive geometry, not lights.
        ///
        /// Kept to two thin strips rather than Area B's three: this room is a third of the
        /// width, and the brief asks for calmer than Area B, not brighter.
        /// </summary>
        static void BuildCeiling(Transform parent)
        {
            var group = Group("Structure_Ceiling", parent);

            var ceiling = Box(group, "Ceiling_C_Panel",
                new Vector3(0f, k_CeilingY - 0.02f, 0f),
                new Vector3(4.50f, 0.04f, 4.00f),
                LoadMaterial("M_YFAM_Ceiling"));

            if (ceiling == null)
                Debug.LogWarning("[IKEA_EEG] Area C ceiling material not found; panel is untextured.");

            var strip = LoadMaterial("M_AreaB_LightStrip");

            foreach (var x in new[] { -1.10f, 1.10f })
            {
                Box(group, $"Ceiling_C_LightStrip_{x:0.0}",
                    new Vector3(x, k_CeilingY - 0.055f, 0f),
                    new Vector3(0.26f, 0.03f, 3.40f),
                    strip);
            }
        }

        /// <summary>
        /// The LEFT wall: a seating bay, the "lounge" half of an exit lounge.
        ///
        /// One sofa, low and against the wall, reading as somewhere to wait rather than
        /// somewhere to shop. It is 0.88 m tall, so it sits well below the participant's line
        /// to the UI and cannot crowd the panel. No coffee table: at this room's width the
        /// table would have to sit at x = -0.8 and would be the one object actually inside the
        /// reading corridor.
        /// </summary>
        static void BuildLoungeSide(Transform parent)
        {
            var group = Group("Lounge_Left", parent);

            // 0.90 m deep; a centre of -1.62 leaves it clear of the wall inner face at -2.13
            // and keeps its inner edge outside the reading corridor.
            Place(group, "DEC_SofaThreeSeats", new Vector3(-1.62f, 0f, 0.00f), 90f);
            Place(group, "DEC_SofaPillow", new Vector3(-1.66f, 0.46f, -0.62f), 90f);
        }

        /// <summary>
        /// The RIGHT wall: a low console run, the "consultation desk" half.
        ///
        /// Two storage modules with a shelf above and three quiet objects on top. Restraint is
        /// the point: enough to say the room is furnished and cared for, not enough to invite
        /// the participant to look at it during a resting block.
        /// </summary>
        static void BuildConsoleSide(Transform parent)
        {
            var group = Group("Console_Right", parent);

            // 0.62 m deep; a centre of 1.80 keeps it off the wall inner face at 2.13.
            Place(group, "DEC_KitchenCounterSingle", new Vector3(1.80f, 0f, -0.35f), -90f);
            Place(group, "DEC_KitchenCounterSingle", new Vector3(1.80f, 0f, 0.35f), -90f);
            Place(group, "DEC_KitchenCabinetSingle", new Vector3(1.90f, 1.55f, 0.00f), -90f);

            // Worktop is 0.90 m. Three objects, grouped, and nothing that reads as a control.
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(1.80f, 0.90f, -0.35f), 0f);
            Place(group, "DEC_CeramicDinnerPlate", new Vector3(1.80f, 0.92f, -0.35f), 16f);
            Place(group, "DEC_CerealBowl", new Vector3(1.80f, 0.90f, 0.32f), 0f);
        }

        /// <summary>
        /// Gives the shell clean finishes: off-white walls and a light stone floor.
        ///
        /// THIS IS THE BIGGEST SINGLE IMPROVEMENT and it was the thing actually wrong with the
        /// room. Area C still wore the original graybox materials -- M_Wall at 0.62 grey and
        /// M_Floor at 0.42 grey -- while Area A and Area B had both moved on. A room finished
        /// in mid grey reads as heavy and unfinished, which is the opposite of the calm, light
        /// closing space this stage is supposed to be.
        ///
        /// The walls take the SAME off-white Area B uses, so the two rooms read as one
        /// building and no new material enters the project.
        ///
        /// THE FLOOR IS DELIBERATELY LEFT ALONE. Every architectural floor in the library was
        /// rendered into this room and none of them suited it: the "marble" tile is a stained
        /// blue industrial floor, the laminated wood comes out almost black, and the concave
        /// tile is bright green. Against off-white walls the original neutral grey is already
        /// the calm, clean, unbusy surface this stage wants -- swapping in a textured floor
        /// would have added visual noise to the one room whose brief is to have less of it.
        ///
        /// A sharedMaterial swap on the renderers only. The material assets themselves are not
        /// edited, so M_Wall and M_Floor stay exactly as Area A left them.
        /// </summary>
        static void ReMaterialiseShell(Transform areaC)
        {
            var wall = LoadMaterial("M_AreaB_Wall_OffWhite");

            // Asserted, not merely skipped. An earlier revision of this pass did put a stone
            // tile on the floor, and simply deleting that line would have left the tile baked
            // into the saved scene forever -- the pass has to be able to state what the floor
            // IS, not just decline to change it, or it stops being re-runnable.
            var floor = LoadMaterial("M_Floor");

            foreach (var r in areaC.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (wall != null &&
                    (r.name == "Wall_C_Left" || r.name == "Wall_C_Right" || r.name == "Wall_C_Front"))
                {
                    r.sharedMaterial = wall;
                }
                else if (floor != null && r.name == "Floor_C")
                {
                    r.sharedMaterial = floor;
                }
            }
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

        static Material Box(Transform parent, string name, Vector3 localCentre, Vector3 size,
            Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localCentre;
            go.transform.localScale = size;

            var r = go.GetComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (material != null)
                r.sharedMaterial = material;

            return material;
        }

        static GameObject Place(Transform parent, string prefabName, Vector3 localPosition,
            float yawDegrees)
        {
            var guid = AssetDatabase.FindAssets($"{prefabName} t:Prefab", new[] { k_PropsPack })
                .FirstOrDefault(g => System.IO.Path.GetFileNameWithoutExtension(
                    AssetDatabase.GUIDToAssetPath(g)) == prefabName);

            if (guid == null)
            {
                Debug.LogWarning($"[IKEA_EEG] Area C art pass: prefab '{prefabName}' not found; " +
                                 "that piece was skipped.");
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
            var guid = AssetDatabase.FindAssets($"{name} t:Material").FirstOrDefault();

            return guid == null
                ? null
                : AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
        }

        /// <summary>
        /// Strips colliders, rigidbodies and lights from everything this pass created.
        ///
        /// Same reasoning as Area B: the pack's DEC_ prefabs ship with dozens of mesh colliders
        /// each, and in Area C a stray collider would sit between the participant and the
        /// recognition buttons. Instances only -- the third-party prefabs on disk are untouched.
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

        public static string Validate()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== AREA C EXIT LOUNGE =====");

            var problems = 0;
            var areaC = FindAreaCRoot();

            if (areaC == null)
            {
                sb.AppendLine("  FAIL  Area_C_Exit is not in the open scene.");
                return sb.ToString();
            }

            var root = areaC.Find(DressingRootName);

            if (root == null)
            {
                sb.AppendLine($"  FAIL  '{DressingRootName}' is missing.");
                return sb.ToString();
            }

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

            problems += Expect(sb, "colliders in the dressing",
                root.GetComponentsInChildren<Collider>(true).Length, 0,
                "a decorative collider can absorb an XR ray meant for a recognition button");

            problems += Expect(sb, "rigidbodies in the dressing",
                root.GetComponentsInChildren<Rigidbody>(true).Length, 0,
                "decoration must introduce no physics");

            problems += Expect(sb, "realtime lights in the dressing",
                root.GetComponentsInChildren<Light>(true).Length, 0,
                "Quest 2 cannot afford extra realtime lights");

            // ---- the reading corridor stays clear -----------------------------------------
            var intruders = renderers
                .Where(r => r.transform.parent != null &&
                            r.GetComponentInParent<Transform>() != null)
                .Where(r =>
                {
                    // The ceiling spans the room by design and is above everything; exclude it.
                    if (r.name.StartsWith("Ceiling_C_"))
                        return false;

                    var b = r.bounds;
                    var minX = b.min.x - areaC.position.x;
                    var maxX = b.max.x - areaC.position.x;
                    var minZ = b.min.z - areaC.position.z;
                    var maxZ = b.max.z - areaC.position.z;

                    return maxZ > k_TaskZMin && minZ < k_TaskZMax &&
                           minX < k_KeepClearX && maxX > -k_KeepClearX;
                })
                .Select(r => $"{r.name} x=[{r.bounds.min.x - areaC.position.x:F2}," +
                             $"{r.bounds.max.x - areaC.position.x:F2}]")
                .ToList();

            if (intruders.Count > 0)
            {
                sb.AppendLine($"  FAIL  {intruders.Count} decorative renderer(s) inside the " +
                              $"reading corridor (|x| < {k_KeepClearX}, z {k_TaskZMin}..{k_TaskZMax}):");

                foreach (var i in intruders.Take(8))
                    sb.AppendLine($"           {i}");

                problems += 1;
            }
            else
            {
                sb.AppendLine($"  ok    the reading corridor is clear " +
                              $"(|x| < {k_KeepClearX}, z {k_TaskZMin}..{k_TaskZMax})");
            }

            // ---- decoration must not reach the UI or the recognition anchor -----------------
            var uiCanvas = areaC.GetComponentsInChildren<Canvas>(true)
                .FirstOrDefault(c => c.name == "UI_C_Canvas");

            problems += Expect(sb, "UI_C_Canvas present", uiCanvas != null ? 1 : 0, 1,
                "the results and recognition UI lives on it");

            var ctx = Object.FindAnyObjectByType<SceneFlow.AreaCSceneContext>();
            problems += Expect(sb, "AreaCSceneContext present", ctx != null ? 1 : 0, 1,
                "the room binds itself into the persistent systems through it");

            if (ctx != null)
                problems += Expect(sb, "recognition anchor bound",
                    ctx.recognitionAnchor != null ? 1 : 0, 1,
                    "the shared response panel parks on it");

            problems += Expect(sb, "SpawnPoint", Object.FindObjectsByType<XR.SpawnPoint>(
                FindObjectsInactive.Include, FindObjectsSortMode.None).Length, 1,
                "Spawn_C must survive the art pass");

            // ---- nothing broken -------------------------------------------------------------
            problems += Expect(sb, "renderers with a null material",
                renderers.Count(r => r.sharedMaterials.Any(m => m == null)), 0,
                "a null material renders magenta on device");

            problems += Expect(sb, "mesh filters with no mesh",
                filters.Count(f => f.sharedMesh == null), 0,
                "a missing mesh means a broken prefab reference");

            sb.AppendLine(problems == 0
                ? "[IKEA_EEG] AREA C DRESSING PASSED — decoration only, task surfaces untouched."
                : $"[IKEA_EEG] AREA C DRESSING FOUND {problems} PROBLEM(S).");

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
