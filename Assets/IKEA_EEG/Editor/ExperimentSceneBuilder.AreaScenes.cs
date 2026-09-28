using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit;
using IkeaEeg.Audio;
using IkeaEeg.Core;
using IkeaEeg.Data;
using IkeaEeg.Experiment;
using IkeaEeg.Memory;
using IkeaEeg.Interaction;
using IkeaEeg.SceneFlow;
using IkeaEeg.UI;
using IkeaEeg.XR;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Emits the SPLIT scene set: one bootstrap scene carrying every persistent system, and one
    /// scene per room.
    ///
    /// WHY SPLIT AT ALL. With all four areas in one scene, geometry authored for one room can
    /// reach into another -- which is exactly what happened when Area A's skyline backdrop
    /// stood 3 m inside Area B and hid its left wall, its light strip and part of a chair.
    /// Rooms that are never co-loaded cannot do that to each other.
    ///
    /// WHY THE ROOMS KEEP THEIR WORLD COORDINATES. Area 0 stays at x = -100, Area A at 0,
    /// Area B at +100, Area C at +200. Rebasing each room to the origin would be tidier on
    /// paper and would invalidate every authored offset, both AreaX_Visuals prefabs, the
    /// capture presets and the validated skyline geometry. Nothing is moved.
    ///
    /// This emits scenes. It does NOT switch the project over to them: the runtime transitions
    /// are wired separately, and until they are, <see cref="AreaSceneLoader.multiSceneAvailable"/>
    /// governs whether anything at run time uses them at all.
    /// </summary>
    public static partial class ExperimentSceneBuilder
    {
        public const string BootstrapScenePath =
            ExperimentAssetBuilder.ScenesFolder + "/IKEA_EEG_Bootstrap.unity";

        public const string AreaAScenePath =
            ExperimentAssetBuilder.ScenesFolder + "/IKEA_EEG_AreaA.unity";

        public const string AreaBScenePath =
            ExperimentAssetBuilder.ScenesFolder + "/IKEA_EEG_AreaB.unity";

        public const string AreaCScenePath =
            ExperimentAssetBuilder.ScenesFolder + "/IKEA_EEG_AreaC.unity";

        // =================================================================================
        // Step 1 — the bootstrap scene
        // =================================================================================

        [MenuItem("IKEA_EEG/Scene Split/1. Build Bootstrap Scene", false, 300)]
        public static void BuildBootstrapSceneMenu()
        {
            BuildBootstrapScene();
            Debug.Log(ValidateBootstrapScene());
        }

        /// <summary>
        /// Batch entry point:
        /// -executeMethod IkeaEeg.EditorTools.ExperimentSceneBuilder.BuildBootstrapFromCommandLine
        /// </summary>
        public static void BuildBootstrapFromCommandLine()
        {
            BuildBootstrapScene();
            Debug.Log(ValidateBootstrapScene());
        }

        /// <summary>
        /// Everything that must outlive a room change, and nothing that belongs to a room.
        ///
        /// Built by the SAME <c>WireSystems</c> the combined scene uses, called with every room
        /// argument null. That is deliberate: one wiring implementation, so a persistent system
        /// cannot be configured differently depending on which builder made it. WireSystems
        /// skips exactly the room bindings and creates exactly the persistent objects.
        /// </summary>
        public static void BuildBootstrapScene()
        {
            ExperimentAssetBuilder.EnsureFolders();

            // NOTE ON ASSET CHURN. BuildMaterials goes through CreateOrUpdate, which re-writes
            // every colour whether or not it changed, so the .mat files come back with floats
            // re-serialized (0.41999996 -> 0.42). The values are numerically identical and no
            // material is actually modified. This is pre-existing behaviour shared with the
            // combined-scene Build(), not something the split introduced: revert those files
            // rather than committing the noise. The MaterialSet itself is needed here for the
            // recognition panel and the developer panel, so it cannot simply be skipped.
            var mats = ExperimentAssetBuilder.BuildMaterials();
            var wordList = ExperimentAssetBuilder.BuildWordList();
            var config = ExperimentAssetBuilder.BuildConfig(wordList);
            AssetDatabase.SaveAssets();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildLighting();

            var systemsRoot = new GameObject("Systems");

            var interactionManager = new GameObject("XR Interaction Manager");
            interactionManager.transform.SetParent(systemsRoot.transform);
            interactionManager.AddComponent<XRInteractionManager>();

            BuildEventSystem(systemsRoot.transform);

            var desktopMouse = new GameObject("DesktopMouseInteraction");
            desktopMouse.transform.SetParent(systemsRoot.transform);
            desktopMouse.AddComponent<DesktopMouseInteraction>();

            var xrOrigin = InstantiateXROrigin();

            // The ONE recognition response pair, shared by immediate and delayed recognition.
            // Its two anchors live in the Area A and Area C room scenes and are supplied by
            // their contexts on load, so it is built here with neither.
            var recognitionPanel = BuildRecognitionPanel(systemsRoot.transform, mats, null, null);

            // Every room argument null: persistent systems only.
            WireSystems(systemsRoot.transform, config, mats, xrOrigin,
                spawn0: null, spawnA: null, spawnB: null, spawnC: null,
                chairs: null, chairSlots: null, practiceObjects: null,
                ui0: null, uiA: null, uiB: null, uiC: null,
                recognitionPanel: recognitionPanel);

            AddAreaSceneLoader(systemsRoot.transform);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, BootstrapScenePath);
            AddSceneToBuildSettings(BootstrapScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[IKEA_EEG] Bootstrap scene built and saved to {BootstrapScenePath}");
        }

        /// <summary>
        /// The service that loads and unloads rooms, bound to the systems it will rebind into.
        /// Bound explicitly rather than left to its runtime fallback so the references are
        /// visible in the Inspector and survive as serialized data.
        /// </summary>
        static void AddAreaSceneLoader(Transform systemsRoot)
        {
            var go = new GameObject("AreaSceneLoader");
            go.transform.SetParent(systemsRoot);

            var loader = go.AddComponent<AreaSceneLoader>();

            loader.Bind(
                Object.FindAnyObjectByType<ExperimentManager>(),
                Object.FindAnyObjectByType<ExperimentUIController>(),
                Object.FindAnyObjectByType<ChairSelectionTask>(),
                Object.FindAnyObjectByType<XRRigTeleporter>(),
                Object.FindAnyObjectByType<RecognitionResponsePanel>());

            EditorUtility.SetDirty(loader);
        }

        // =================================================================================
        // Bootstrap validation
        // =================================================================================

        public static void ValidateBootstrapFromCommandLine()
        {
            EditorSceneManager.OpenScene(BootstrapScenePath, OpenSceneMode.Single);
            Debug.Log(ValidateBootstrapScene());
        }

        /// <summary>
        /// The bootstrap scene holds EXACTLY ONE of every persistent system and NO room content.
        ///
        /// Both halves matter. One of each, because a second ExperimentManager, EventLogger or
        /// AURA inlet would mean two sessions, two event streams or two EEG pipelines competing
        /// over one amplifier. No room content, because anything authored here would be loaded
        /// in every room simultaneously -- reintroducing, permanently, the cross-room bleed the
        /// split exists to remove.
        ///
        /// The persistent-system roots are checked for position the same way the room roots are
        /// in the combined scene's validator: the transform guard is not weakened for the
        /// migration, it is extended to cover the new scenes.
        /// </summary>
        public static string ValidateBootstrapScene()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== BOOTSTRAP SCENE VALIDATION =====");

            var problems = 0;

            // ---- exactly one of every persistent system ---------------------------------
            problems += Check(sb, "XROrigin", Object.FindObjectsByType<XROrigin>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "XRInteractionManager", Object.FindObjectsByType<XRInteractionManager>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "EventSystem", Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "EventLogger", Object.FindObjectsByType<EventLogger>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "CsvEventSink", Object.FindObjectsByType<CsvEventSink>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "LslMarkerSink", Object.FindObjectsByType<LslMarkerSink>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "ExperimentManager", Object.FindObjectsByType<ExperimentManager>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "ExperimentUIController", Object.FindObjectsByType<ExperimentUIController>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "ChairSelectionTask", Object.FindObjectsByType<ChairSelectionTask>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "XRRigTeleporter", Object.FindObjectsByType<XRRigTeleporter>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "ExperimentAudio", Object.FindObjectsByType<ExperimentAudio>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "VoiceRecallManager", Object.FindObjectsByType<VoiceRecallManager>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "AuraLslReceiver", Object.FindObjectsByType<AuraLslReceiver>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "EegFeaturePipeline", Object.FindObjectsByType<EegFeaturePipeline>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "RecognitionResponsePanel", Object.FindObjectsByType<RecognitionResponsePanel>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "AreaSceneLoader", Object.FindObjectsByType<AreaSceneLoader>(FindObjectsSortMode.None).Length, 1);

            // ---- and NO room content ----------------------------------------------------
            problems += ExpectAbsent(sb, "SpawnPoint", Object.FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsent(sb, "ChairTarget", Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsent(sb, "ChairSlot", Object.FindObjectsByType<ChairSlot>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsent(sb, "PracticeObject", Object.FindObjectsByType<PracticeObject>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsent(sb, "AreaSceneContext", Object.FindObjectsByType<AreaSceneContext>(FindObjectsSortMode.None).Length);

            foreach (var roomRoot in new[]
                     {
                         "Area_0_Familiarization", "Area_A_Entrance",
                         "Area_B_Showroom", "Area_C_Exit", "Environment",
                     })
            {
                var found = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                    .Any(t => t.name == roomRoot);

                if (found)
                {
                    sb.AppendLine($"  FAIL  '{roomRoot}' is in the bootstrap scene. Room content " +
                                  "here would be loaded during EVERY room and could reach into " +
                                  "any of them.");
                    problems++;
                }
                else
                {
                    sb.AppendLine($"  ok    no '{roomRoot}' in the bootstrap scene");
                }
            }

            // ---- the persistent roots stand where they are authored ----------------------
            problems += CheckPersistentRootTransforms(sb);

            sb.AppendLine(problems == 0
                ? "[IKEA_EEG] BOOTSTRAP VALIDATION PASSED — no structural problems found."
                : $"[IKEA_EEG] BOOTSTRAP VALIDATION FOUND {problems} PROBLEM(S).");

            return sb.ToString();
        }

        // =================================================================================
        // Steps 3-8 — the three room scenes
        // =================================================================================

        [MenuItem("IKEA_EEG/Scene Split/2. Build Area A Scene", false, 301)]
        public static void BuildAreaASceneMenu()
        {
            BuildAreaAScene();
            Debug.Log(ValidateRoomScene(ExperimentArea.AreaA));
        }

        [MenuItem("IKEA_EEG/Scene Split/3. Build Area B Scene", false, 302)]
        public static void BuildAreaBSceneMenu()
        {
            BuildAreaBScene();
            Debug.Log(ValidateRoomScene(ExperimentArea.AreaB));
        }

        [MenuItem("IKEA_EEG/Scene Split/4. Build Area C Scene", false, 303)]
        public static void BuildAreaCSceneMenu()
        {
            BuildAreaCScene();
            Debug.Log(ValidateRoomScene(ExperimentArea.AreaC));
        }

        public static void BuildAreaAFromCommandLine()
        {
            BuildAreaAScene();
            Debug.Log(ValidateRoomScene(ExperimentArea.AreaA));
        }

        public static void BuildAreaBFromCommandLine()
        {
            BuildAreaBScene();
            Debug.Log(ValidateRoomScene(ExperimentArea.AreaB));
        }

        public static void BuildAreaCFromCommandLine()
        {
            BuildAreaCScene();
            Debug.Log(ValidateRoomScene(ExperimentArea.AreaC));
        }

        /// <summary>
        /// Area A's room: the practice room (Area 0) and the entrance (Area A).
        ///
        /// Both are built by the SAME BuildArea0/BuildAreaA the combined scene uses, at the
        /// same world coordinates. The room geometry code is not touched by the split at all;
        /// only where the result is saved changes.
        /// </summary>
        public static void BuildAreaAScene()
        {
            var mats = PrepareRoomAssets();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var environment = new GameObject("Environment");

            var spawn0 = BuildArea0(environment.transform, mats, out var ui0,
                out var practiceObjects);
            var spawnA = BuildAreaA(environment.transform, mats, out var uiA);

            var anchorA = new GameObject("Anchor_Recognition_AreaA");
            anchorA.transform.SetParent(environment.transform, false);
            anchorA.transform.position = k_AreaAOrigin + new Vector3(0f, 0.95f, 1.18f);

            LocalizeRoomCaptions(ui0, uiA, null, null);

            // The storefront dressing, exactly as the combined build preserves it.
            AreaAEnvironmentBuilder.PreserveAfterSceneRebuild(
                environment.transform.Find("Area_A_Entrance"));

            var context = NewContextObject<AreaASceneContext>();
            context.AuthorSpawns(spawn0, spawnA, anchorA.transform);
            context.AuthorArea0Ui(ui0.languagePanel, ui0.languageTitle, ui0.languageEnglishButton,
                ui0.languageSpanishButton, ui0.languageJapaneseButton,
                ui0.panel, ui0.instruction, ui0.practicePrompt, ui0.status, ui0.warning,
                ui0.readyLabel, ui0.startHighlight, ui0.startExperimentButton,
                ui0.skipIntroButton, ui0.replayInstructionsButton, ui0.recenterButton,
                ui0.researcherStatusPanel, ui0.researcherStatusText, practiceObjects);
            context.AuthorAreaAUi(uiA.panel, uiA.title, uiA.instruction, uiA.word,
                uiA.recognitionCounter, uiA.developerCheatsheet, uiA.fixation, uiA.status,
                uiA.warning, uiA.startButton, uiA.enterButton, uiA.recheckAudioButton,
                uiA.recenterButton);

            SaveRoomScene(scene, AreaAScenePath, context);
        }

        /// <summary>Area B's room: the chair-selection showroom and its dressing.</summary>
        public static void BuildAreaBScene()
        {
            var mats = PrepareRoomAssets();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var environment = new GameObject("Environment");

            var spawnB = BuildAreaB(environment.transform, mats, out var uiB, out var chairs,
                out var chairSlots);

            LocalizeRoomCaptions(null, null, uiB, null);

            AreaBEnvironmentBuilder.PreserveAfterSceneRebuild(
                environment.transform.Find("Area_B_Showroom"));

            var context = NewContextObject<AreaBSceneContext>();
            context.AuthorSpawn(spawnB);
            context.AuthorChairs(chairs, chairSlots);
            context.AuthorUi(uiB.instructionPanel, uiB.instruction, uiB.statusPanel, uiB.status,
                uiB.feedback, uiB.warning, uiB.exitButton, uiB.readyButton, uiB.recenterButton,
                uiB.shapeLegend, uiB.instructionOverlay, uiB.overlayText);

            SaveRoomScene(scene, AreaBScenePath, context);
        }

        /// <summary>Area C's room: delayed recognition, post-task rest and the results screen.</summary>
        public static void BuildAreaCScene()
        {
            var mats = PrepareRoomAssets();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var environment = new GameObject("Environment");

            var spawnC = BuildAreaC(environment.transform, mats, out var uiC);

            var anchorC = new GameObject("Anchor_Recognition_AreaC");
            anchorC.transform.SetParent(environment.transform, false);
            anchorC.transform.position = k_AreaCOrigin + new Vector3(0f, 0.95f, 1.18f);

            LocalizeRoomCaptions(null, null, null, uiC);

            var context = NewContextObject<AreaCSceneContext>();
            context.AuthorSpawn(spawnC, anchorC.transform);
            context.AuthorUi(uiC.panel, uiC.instruction, uiC.status, uiC.word,
                uiC.recognitionCounter, uiC.developerCheatsheet, uiC.fixation, uiC.results,
                uiC.warning, uiC.restReadyButton, uiC.restartButton, uiC.newTrialButton,
                uiC.endButton, uiC.recenterButton);

            SaveRoomScene(scene, AreaCScenePath, context);
        }

        static ExperimentAssetBuilder.MaterialSet PrepareRoomAssets()
        {
            ExperimentAssetBuilder.EnsureFolders();
            var mats = ExperimentAssetBuilder.BuildMaterials();
            AssetDatabase.SaveAssets();
            return mats;
        }

        static T NewContextObject<T>() where T : AreaSceneContext
        {
            var go = new GameObject("AreaSceneContext");
            return go.AddComponent<T>();
        }

        static void SaveRoomScene(Scene scene, string path, AreaSceneContext context)
        {
            // Serialized private fields set from code need an explicit dirty flag to survive.
            EditorUtility.SetDirty(context);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, path);
            AddSceneToBuildSettings(path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[IKEA_EEG] Room scene built and saved to {path}");
        }

        // =================================================================================
        // Room validation
        // =================================================================================

        public static void ValidateAreaAFromCommandLine() => ValidateRoomFromCommandLine(ExperimentArea.AreaA);
        public static void ValidateAreaBFromCommandLine() => ValidateRoomFromCommandLine(ExperimentArea.AreaB);
        public static void ValidateAreaCFromCommandLine() => ValidateRoomFromCommandLine(ExperimentArea.AreaC);

        static void ValidateRoomFromCommandLine(ExperimentArea area)
        {
            EditorSceneManager.OpenScene(RoomScenePath(area), OpenSceneMode.Single);
            Debug.Log(ValidateRoomScene(area));
        }

        public static string RoomScenePath(ExperimentArea area)
        {
            switch (area)
            {
                case ExperimentArea.AreaB: return AreaBScenePath;
                case ExperimentArea.AreaC: return AreaCScenePath;
                default: return AreaAScenePath;
            }
        }

        /// <summary>
        /// A room scene holds ITS OWN rooms, standing exactly on their origins, and nothing
        /// belonging to another room or to the persistent layer.
        ///
        /// The "nothing else" half is the entire point of the split: an Area A backdrop that
        /// reached into Area B is impossible once Area A is not loaded while Area B is, but
        /// only if the Area A geometry really is absent from Area B's scene. That is asserted
        /// here rather than assumed.
        ///
        /// The transform-origin guard is applied per room scene with the SAME tolerance and
        /// the same expected coordinates as the combined scene uses. It is not relaxed to let
        /// the migration pass.
        /// </summary>
        public static string ValidateRoomScene(ExperimentArea area)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[IKEA_EEG] ===== {area} ROOM SCENE VALIDATION =====");

            var problems = 0;

            var expected = area == ExperimentArea.AreaA
                ? new[] { ("Area_0_Familiarization", k_Area0Origin), ("Area_A_Entrance", k_AreaAOrigin) }
                : area == ExperimentArea.AreaB
                    ? new[] { ("Area_B_Showroom", k_AreaBOrigin) }
                    : new[] { ("Area_C_Exit", k_AreaCOrigin) };

            var all = new[]
            {
                ("Area_0_Familiarization", k_Area0Origin), ("Area_A_Entrance", k_AreaAOrigin),
                ("Area_B_Showroom", k_AreaBOrigin), ("Area_C_Exit", k_AreaCOrigin),
            };

            var environment = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "Environment" && t.parent == null);

            if (environment == null)
            {
                sb.AppendLine("  FAIL  no root GameObject named 'Environment'.");
                problems++;
            }
            else
            {
                problems += CheckRootAt(sb, environment, "Environment", Vector3.zero);

                foreach (var (name, origin) in expected)
                {
                    var root = environment.Find(name);

                    if (root == null)
                    {
                        sb.AppendLine($"  FAIL  Environment/{name} is missing from this room.");
                        problems++;
                        continue;
                    }

                    problems += CheckRootAt(sb, root, name, origin);
                }

                foreach (var (name, _) in all)
                {
                    if (expected.Any(e => e.Item1 == name))
                        continue;

                    if (environment.Find(name) != null)
                    {
                        sb.AppendLine($"  FAIL  '{name}' is in the {area} scene. Another room's " +
                                      "geometry here can reach into this one — exactly the fault " +
                                      "the split exists to prevent.");
                        problems++;
                    }
                    else
                    {
                        sb.AppendLine($"  ok    no '{name}' in the {area} scene");
                    }
                }
            }

            // ---- exactly one context, of the right type ---------------------------------
            var contexts = Object.FindObjectsByType<AreaSceneContext>(FindObjectsSortMode.None);
            problems += Check(sb, "AreaSceneContext", contexts.Length, 1);

            if (contexts.Length == 1)
            {
                if (contexts[0].area != area)
                {
                    sb.AppendLine($"  FAIL  the context reports {contexts[0].area}, not {area}.");
                    problems++;
                }

                var missing = contexts[0].MissingReferences().ToList();

                if (missing.Count > 0)
                {
                    sb.AppendLine($"  FAIL  the context is missing {missing.Count} reference(s): " +
                                  $"{string.Join(", ", missing)}");
                    problems++;
                }
                else
                {
                    sb.AppendLine("  ok    the context has every reference it declares");
                }
            }

            // ---- and NOTHING persistent -------------------------------------------------
            problems += ExpectAbsentFromRoom(sb, "ExperimentManager", Object.FindObjectsByType<ExperimentManager>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "EventLogger", Object.FindObjectsByType<EventLogger>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "AuraLslReceiver", Object.FindObjectsByType<AuraLslReceiver>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "EegFeaturePipeline", Object.FindObjectsByType<EegFeaturePipeline>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "ExperimentUIController", Object.FindObjectsByType<ExperimentUIController>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "XROrigin", Object.FindObjectsByType<XROrigin>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "XRRigTeleporter", Object.FindObjectsByType<XRRigTeleporter>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "ChairSelectionTask", Object.FindObjectsByType<ChairSelectionTask>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "RecognitionResponsePanel", Object.FindObjectsByType<RecognitionResponsePanel>(FindObjectsSortMode.None).Length);
            problems += ExpectAbsentFromRoom(sb, "AreaSceneLoader", Object.FindObjectsByType<AreaSceneLoader>(FindObjectsSortMode.None).Length);

            sb.AppendLine(problems == 0
                ? $"[IKEA_EEG] {area} ROOM VALIDATION PASSED — no structural problems found."
                : $"[IKEA_EEG] {area} ROOM VALIDATION FOUND {problems} PROBLEM(S).");

            return sb.ToString();
        }

        static int ExpectAbsentFromRoom(StringBuilder sb, string label, int actual)
        {
            if (actual == 0)
            {
                sb.AppendLine($"  ok    no {label} in this room (it is persistent)");
                return 0;
            }

            sb.AppendLine($"  FAIL  {actual} {label}(s) in this room scene. That system is " +
                          "persistent and lives in the bootstrap scene; a copy here would be a " +
                          "SECOND one the moment this room loads.");
            return 1;
        }

        /// <summary>
        /// THE transform-origin guard, in one place.
        ///
        /// Used by the combined scene's validator, by every room scene's validator and by the
        /// bootstrap's Systems root. One implementation means the guard cannot be strict in one
        /// scene and lax in another, and it cannot be quietly weakened for whichever scene is
        /// inconvenient during the migration.
        ///
        /// Checked in WORLD space with a 1 mm tolerance: far tighter than any displacement that
        /// could matter, loose enough that float round-trips through the YAML cannot trip it.
        /// </summary>
        internal static int CheckRootAt(StringBuilder sb, Transform root, string name,
            Vector3 expected)
        {
            const float tolerance = 0.001f;

            if (root == null)
            {
                sb.AppendLine($"  FAIL  '{name}' is missing, so its origin was not checked.");
                return 1;
            }

            var problems = 0;
            var offset = Vector3.Distance(root.position, expected);

            if (offset > tolerance)
            {
                sb.AppendLine($"  FAIL  {name} stands at {root.position:F3} but its authored " +
                              $"origin is {expected:F3} — displaced by {offset:F3} m. Every " +
                              "coordinate measured inside it is wrong by that much, and it may " +
                              "now overlap a neighbouring area.");
                problems++;
            }
            else
            {
                sb.AppendLine($"  ok    {name} stands exactly on its origin {expected:F3}");
            }

            if (Quaternion.Angle(root.rotation, Quaternion.identity) > 0.01f)
            {
                sb.AppendLine($"  FAIL  {name} is rotated to {root.eulerAngles:F2} — the rooms " +
                              "are authored axis-aligned and every offset inside them assumes it.");
                problems++;
            }

            if (Vector3.Distance(root.lossyScale, Vector3.one) > tolerance)
            {
                sb.AppendLine($"  FAIL  {name} has world scale {root.lossyScale:F4}, not 1. " +
                              "A scaled room changes every distance the task depends on.");
                problems++;
            }

            return problems;
        }

        static int ExpectAbsent(StringBuilder sb, string label, int actual)
        {
            if (actual == 0)
            {
                sb.AppendLine($"  ok    no {label} in the bootstrap scene (room content)");
                return 0;
            }

            sb.AppendLine($"  FAIL  {actual} {label}(s) in the bootstrap scene — that is room " +
                          "content and belongs in a room scene.");
            return 1;
        }

        /// <summary>
        /// The bootstrap's own transform guard: the Systems root sits at the world origin,
        /// unrotated and unscaled.
        ///
        /// Same reasoning as the room-origin guard in the combined scene. A displaced Systems
        /// root would move the XR Origin and the shared recognition panel with it, and every
        /// other check here would still pass.
        /// </summary>
        static int CheckPersistentRootTransforms(StringBuilder sb)
        {
            var systems = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "Systems" && t.parent == null);

            // Same guard as the rooms get, through the same helper: a displaced Systems root
            // carries the XR Origin and the shared recognition panel with it.
            return CheckRootAt(sb, systems, "Systems", Vector3.zero);
        }
    }
}
