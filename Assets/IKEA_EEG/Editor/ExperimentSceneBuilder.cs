using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Feedback;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.UI;
using IkeaEeg.Audio;
using IkeaEeg.Core;
using IkeaEeg.Data;
using IkeaEeg.Experiment;
using IkeaEeg.Interaction;
using IkeaEeg.Localization;
using IkeaEeg.Memory;
using IkeaEeg.UI;
using IkeaEeg.XR;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Generates the complete experiment scene from code.
    ///
    /// WHY A BUILDER INSTEAD OF A HAND-AUTHORED SCENE: every inspector reference is wired
    /// here, in a file that is reviewable and re-runnable. Regenerating the scene after a
    /// script change is one menu click, and there is no list of "things you must remember to
    /// drag into the inspector".
    ///
    /// The existing project is not touched: the XR Origin comes from the untouched XRI
    /// Starter Assets prefab, and no package, project setting or existing scene is modified.
    /// </summary>
    public static class ExperimentSceneBuilder
    {
        public const string ScenePath = ExperimentAssetBuilder.ScenesFolder + "/IKEA_EEG_Experiment.unity";

        // GUID of Assets/Samples/XR Interaction Toolkit/3.4.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab
        const string k_XROriginPrefabGuid = "f6336ac4ac8b4d34bc5072418cdc62a0";

        // ---- Area layout ----------------------------------------------------------------
        // The three areas are 100 m apart with no geometry between them: physically walking
        // from one to another is impossible, which is exactly the requirement.
        // Area 0 sits 100 m BEFORE Area A, on the same spacing as every other area, so no
        // existing area moved and no existing spawn/UI geometry changed.
        static readonly Vector3 k_Area0Origin = new Vector3(-100f, 0f, 0f);
        static readonly Vector3 k_AreaAOrigin = new Vector3(0f, 0f, 0f);
        static readonly Vector3 k_AreaBOrigin = new Vector3(100f, 0f, 0f);
        static readonly Vector3 k_AreaCOrigin = new Vector3(200f, 0f, 0f);

        const float k_WallThickness = 0.25f;
        const float k_OuterWallHeight = 3.0f;
        const float k_ShowroomWallHeight = 3.6f;

        /// <summary>
        /// Resting length of the controller pointer rays. Matches the far caster's 10 m range
        /// so the drawn ray represents what can actually be hit.
        /// </summary>
        const float k_PointerRayLength = 10f;

        // Areas A and C are small vestibules, not rooms: the participant reads a panel and
        // presses a button, so anything larger just adds walking and makes the text distant.
        const float k_VestibuleWidth = 4.5f;
        const float k_VestibuleDepth = 4.0f;

        // ---- Chair definitions ----------------------------------------------------------
        // Distractors are deliberately built so that several share TWO of the three target
        // attributes: the task then requires conjunctive attention rather than spotting the
        // only blue object in the room.
        static readonly (string id, ChairSpec spec)[] k_Chairs =
        {
            ("Chair_01", new ChairSpec(ChairColor.Red,    ChairSize.Small,  ChairShape.Slatted)),
            ("Chair_02", new ChairSpec(ChairColor.Blue,   ChairSize.Large,  ChairShape.Slatted)),
            ("Chair_03", new ChairSpec(ChairColor.Green,  ChairSize.Large,  ChairShape.Solid)),
            ("Chair_04", new ChairSpec(ChairColor.Blue,   ChairSize.Large,  ChairShape.Solid)),   // TARGET
            ("Chair_05", new ChairSpec(ChairColor.Blue,   ChairSize.Small,  ChairShape.Solid)),
            ("Chair_06", new ChairSpec(ChairColor.Yellow, ChairSize.Medium, ChairShape.Curved)),
        };

        // =================================================================================
        // Menu entry points
        // =================================================================================

        [MenuItem("IKEA_EEG/Build Experiment Scene", false, 0)]
        public static void BuildMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Build();
        }

        [MenuItem("IKEA_EEG/Validate Experiment Scene", false, 20)]
        public static void ValidateMenu()
        {
            var report = ValidateOpenScene();
            Debug.Log(report);
        }

        [MenuItem("IKEA_EEG/Print Scene Hierarchy", false, 21)]
        public static void PrintHierarchyMenu()
        {
            Debug.Log(DumpHierarchy());
        }

        /// <summary>Batch-mode entry point: Unity.exe -executeMethod IkeaEeg.EditorTools.ExperimentSceneBuilder.BuildFromCommandLine</summary>
        public static void BuildFromCommandLine()
        {
            Build();
            Debug.Log(ValidateOpenScene());
            Debug.Log(DumpHierarchy());
        }

        // =================================================================================
        // Build
        // =================================================================================

        public static void Build()
        {
            EditorUtility.DisplayProgressBar("IKEA_EEG", "Creating assets…", 0.1f);

            try
            {
                ExperimentAssetBuilder.EnsureFolders();
                var mats = ExperimentAssetBuilder.BuildMaterials();
                var wordList = ExperimentAssetBuilder.BuildWordList();
                var config = ExperimentAssetBuilder.BuildConfig(wordList);
                ExperimentAssetBuilder.WriteDataReadme();
                AssetDatabase.SaveAssets();

                EditorUtility.DisplayProgressBar("IKEA_EEG", "Creating scene…", 0.3f);

                var scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);

                BuildLighting();

                var systemsRoot = new GameObject("Systems");

                var interactionManager = new GameObject("XR Interaction Manager");
                interactionManager.transform.SetParent(systemsRoot.transform);
                interactionManager.AddComponent<XRInteractionManager>();

                BuildEventSystem(systemsRoot.transform);

                EditorUtility.DisplayProgressBar("IKEA_EEG", "Instantiating XR Origin…", 0.45f);
                var xrOrigin = InstantiateXROrigin();

                EditorUtility.DisplayProgressBar("IKEA_EEG", "Building environment…", 0.6f);

                var environment = new GameObject("Environment");

                var spawn0 = BuildArea0(environment.transform, mats, out var ui0,
                    out var practiceObjects);
                var spawnA = BuildAreaA(environment.transform, mats, out var uiA);
                var spawnB = BuildAreaB(environment.transform, mats, out var uiB, out var chairs,
                    out var chairSlots);
                var spawnC = BuildAreaC(environment.transform, mats, out var uiC);

                // The recognition response pair. Anchored in front of the Area A and Area C
                // panels, low in the visual field, and relocated between them at run time.
                // Y/Z chosen to sit below the word display and within comfortable reach of the
                // far ray from either spawn point.
                // THREE SEPARATED ZONES, in world metres:
                //   stimulus word   y ~1.71  z 1.40  (on the area panel, top/central)
                //   Recenter        y ~1.24  z 1.40  far left of the panel (peripheral)
                //   response pair   y  0.95  z 1.18  lower central, in front of the panel
                //
                // Lowered from 1.05 and brought 7 cm nearer so the pair clears the Recenter
                // control above it and cannot occlude the word, which sits ~76 cm higher.
                var anchorA = new GameObject("Anchor_Recognition_AreaA");
                anchorA.transform.SetParent(environment.transform, false);
                anchorA.transform.position = k_AreaAOrigin + new Vector3(0f, 0.95f, 1.18f);

                var anchorC = new GameObject("Anchor_Recognition_AreaC");
                anchorC.transform.SetParent(environment.transform, false);
                anchorC.transform.position = k_AreaCOrigin + new Vector3(0f, 0.95f, 1.18f);

                var recognitionPanel = BuildRecognitionPanel(environment.transform, mats,
                    anchorA.transform, anchorC.transform);

                EditorUtility.DisplayProgressBar("IKEA_EEG", "Wiring systems…", 0.8f);

                WireSystems(systemsRoot.transform, config, mats, xrOrigin, spawn0, spawnA, spawnB,
                    spawnC, chairs, chairSlots, practiceObjects, ui0, uiA, uiB, uiC,
                    recognitionPanel);

                // Start the participant in Area 0 even before Play, so the Scene view opens
                // where the session now begins.
                if (xrOrigin != null)
                    xrOrigin.transform.SetPositionAndRotation(
                        spawn0.transform.position, spawn0.transform.rotation);

                EditorUtility.DisplayProgressBar("IKEA_EEG", "Saving…", 0.95f);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AddSceneToBuildSettings(ScenePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[IKEA_EEG] Scene built and saved to {ScenePath}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static void BuildLighting()
        {
            // Flat ambient keeps the grey prototype boxes readable without a lightmap bake.
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.46f, 0.47f, 0.52f);
            RenderSettings.fog = false;

            var lightGo = new GameObject("Directional Light");
            lightGo.transform.SetPositionAndRotation(
                new Vector3(0f, 8f, 0f), Quaternion.Euler(50f, -30f, 0f));

            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.color = new Color(1f, 0.97f, 0.92f);
        }

        static void BuildEventSystem(Transform parent)
        {
            var go = new GameObject("EventSystem");
            go.transform.SetParent(parent);
            go.AddComponent<UnityEngine.EventSystems.EventSystem>();

            // XRUIInputModule is what lets the controller ray drive uGUI buttons. This is the
            // same module the VR template's own scenes use.
            go.AddComponent<XRUIInputModule>();
        }

        // =================================================================================
        // XR Origin
        // =================================================================================

        static XROrigin InstantiateXROrigin()
        {
            var path = AssetDatabase.GUIDToAssetPath(k_XROriginPrefabGuid);

            if (string.IsNullOrEmpty(path))
            {
                path = "Assets/Samples/XR Interaction Toolkit/3.4.1/Starter Assets/Prefabs/" +
                       "XR Origin (XR Rig).prefab";
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogError($"[IKEA_EEG] Could not find the XR Origin prefab at '{path}'. " +
                               "Import the XR Interaction Toolkit Starter Assets sample, then " +
                               "re-run the builder.");
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "XR Origin (XR Rig)";

            var origin = instance.GetComponent<XROrigin>();
            if (origin == null)
                Debug.LogError("[IKEA_EEG] The instantiated rig has no XROrigin component.");

            DisableFreeLocomotion(instance);
            ConfigureStablePointerRays(instance);
            DisableHaptics(instance);
            RebindSelectToIndexTrigger(instance);

            return origin;
        }

        /// <summary>
        /// ROOT CAUSE OF THE UNSTABLE / "WHIP TO EXTEND" RAY.
        ///
        /// The Starter Assets line visual ships with:
        ///     lineDynamicsMode      = RetractOnHitLoss
        ///     restingVisualLineLength = 0.25 m
        ///     retractDelay          = 1 s
        ///
        /// So the visible ray is not a pointer of fixed length — it is a rubber band. Whenever
        /// a controller is NOT hitting something, its line retracts to 25 cm after one second.
        /// That produces every symptom reported:
        ///   * the ray "starts very short" — nothing is under it at rest;
        ///   * a "whip" makes it extend — sweeping fast crosses a target, scores a hit, and
        ///     the line springs out to the hit distance;
        ///   * "one long, one short" — the controller pointing at a chair has a hit and stays
        ///     extended, the idle one has no hit and retracts. They were never coupled; each
        ///     is independently reacting to whether it currently hits anything.
        ///
        /// The far caster itself was always 10 m and always enabled, so interaction technically
        /// worked — but you cannot aim with a 25 cm stub, which made it feel broken.
        ///
        /// Fix (scene instance only): Traditional mode with a long resting length, so both rays
        /// are always drawn at full length and never depend on controller motion.
        /// </summary>
        static void ConfigureStablePointerRays(GameObject rig)
        {
            var configured = 0;

            foreach (var visual in rig.GetComponentsInChildren<CurveVisualController>(true))
            {
                visual.lineDynamicsMode = LineDynamicsMode.Traditional;
                visual.restingVisualLineLength = k_PointerRayLength;
                visual.retractDelay = float.MaxValue;   // never retract
                EditorUtility.SetDirty(visual);
                configured++;
            }

            // Both interactors keep far casting on and stay independent of each other.
            foreach (var interactor in rig.GetComponentsInChildren<NearFarInteractor>(true))
            {
                interactor.enableFarCasting = true;

                // Near (grab-at-hand) casting competes with far pointing for this task and is
                // not needed: the chairs are selection targets, never grabbed.
                interactor.enableNearCasting = false;
                EditorUtility.SetDirty(interactor);
            }

            // The poke interactor sits ahead of the ray in each controller's interaction group,
            // so it can claim the interaction first. Not needed for a pointing-only experiment.
            var pokesDisabled = 0;
            foreach (var poke in rig.GetComponentsInChildren<XRPokeInteractor>(true))
            {
                poke.gameObject.SetActive(false);
                pokesDisabled++;
            }

            Debug.Log($"[IKEA_EEG] Pointer rays stabilised on the scene rig instance: " +
                      $"{configured} line visual(s) set to Traditional @ {k_PointerRayLength} m " +
                      $"(was RetractOnHitLoss @ 0.25 m), near-casting off, " +
                      $"{pokesDisabled} poke interactor(s) disabled.");
        }

        /// <summary>
        /// Removes controller vibration. EEG work should not carry an uncontrolled
        /// somatosensory stimulus every time the participant hovers or selects a chair.
        ///
        /// SimpleHapticFeedback is what converts hover/select events into impulses, so
        /// disabling those components removes the vibration while leaving the visual highlight
        /// and the auditory selection cue untouched.
        /// </summary>
        static void DisableHaptics(GameObject rig)
        {
            var disabled = 0;

            foreach (var feedback in rig.GetComponentsInChildren<SimpleHapticFeedback>(true))
            {
                feedback.enabled = false;
                EditorUtility.SetDirty(feedback);
                disabled++;
            }

            // Belt and braces: the player is what actually sends the impulse to the device.
            foreach (var player in rig.GetComponentsInChildren<HapticImpulsePlayer>(true))
            {
                player.enabled = false;
                EditorUtility.SetDirty(player);
            }

            Debug.Log($"[IKEA_EEG] Haptics disabled on the scene rig instance: " +
                      $"{disabled} SimpleHapticFeedback component(s) plus their impulse players.");
        }

        /// <summary>
        /// Moves SELECT from the side grip to the index trigger.
        ///
        /// The shared XRI action asset binds "Select" to {GripButton} and "UI Press" to
        /// {TriggerButton} — which is why UI buttons already worked with the trigger while
        /// chairs needed the grip. That asset is shared with the template's own scenes, so it
        /// is NOT edited. Instead each interactor's select reader is switched to
        /// InputSourceMode.InputAction with a locally-defined action bound to the trigger,
        /// which lives entirely inside this scene.
        /// </summary>
        static void RebindSelectToIndexTrigger(GameObject rig)
        {
            foreach (var interactor in rig.GetComponentsInChildren<NearFarInteractor>(true))
            {
                var isLeft = interactor.GetComponentInParent<Transform>() != null &&
                             interactor.transform.parent != null &&
                             interactor.transform.parent.name.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0;

                var hand = isLeft ? "LeftHand" : "RightHand";
                var label = isLeft ? "Left" : "Right";

                // ---- DUAL-TRIGGER SELECT ---------------------------------------------------
                //
                // Usability testing found participants unsure which trigger to squeeze, so BOTH
                // now select. The implementation is deliberately the smallest one available:
                // the index trigger and the grip are TWO BINDINGS ON ONE ACTION, not two
                // actions. That is what makes duplicate selection impossible by construction —
                // a button action tracks the single most-actuated control, so it performs once
                // when the first control crosses the press threshold and does NOT perform again
                // when a second control is pressed while the first is held. There is one action,
                // therefore one Select, therefore one selection.
                //
                // Nothing shared is touched: this replaces the reader on the SCENE INSTANCE's
                // interactors only. The shared XRI input asset, the Input System configuration
                // and the controller prefabs are all left exactly as they were.
                var performed = new InputAction(
                    $"IKEA_Select_{label}", InputActionType.Button,
                    $"<XRController>{{{hand}}}/triggerPressed");

                // Grip / side trigger — the second way to say "select".
                performed.AddBinding($"<XRController>{{{hand}}}/gripPressed");

                // Hand-tracking fallback so a pinch still selects if hands are ever used.
                performed.AddBinding($"<MetaAimHand>{{{hand}}}/indexPressed");

                var value = new InputAction(
                    $"IKEA_SelectValue_{label}", InputActionType.Value,
                    $"<XRController>{{{hand}}}/trigger");

                value.AddBinding($"<XRController>{{{hand}}}/grip");

                interactor.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
                interactor.selectInput.inputActionPerformed = performed;
                interactor.selectInput.inputActionValue = value;

                // ---- The SAME treatment for UI presses --------------------------------------
                // Otherwise the grip would select chairs but not press buttons, which is
                // exactly the kind of inconsistency the change is meant to remove.
                // uiPressInput is a per-interactor reader, so this is still scene-local.
                var uiPerformed = new InputAction(
                    $"IKEA_UIPress_{label}", InputActionType.Button,
                    $"<XRController>{{{hand}}}/triggerPressed");

                uiPerformed.AddBinding($"<XRController>{{{hand}}}/gripPressed");
                uiPerformed.AddBinding($"<MetaAimHand>{{{hand}}}/indexPressed");

                var uiValue = new InputAction(
                    $"IKEA_UIPressValue_{label}", InputActionType.Value,
                    $"<XRController>{{{hand}}}/trigger");

                uiValue.AddBinding($"<XRController>{{{hand}}}/grip");

                interactor.uiPressInput.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
                interactor.uiPressInput.inputActionPerformed = uiPerformed;
                interactor.uiPressInput.inputActionValue = uiValue;

                EditorUtility.SetDirty(interactor);

                Debug.Log($"[IKEA_EEG] {label} controller SELECT and UI PRESS bound to BOTH " +
                          $"<XRController>{{{hand}}}/triggerPressed (index) and " +
                          $"<XRController>{{{hand}}}/gripPressed (grip), as two bindings on one " +
                          "action so a single press can only ever produce one selection. " +
                          "The shared XRI input asset was not modified.");
            }
        }

        /// <summary>
        /// Turns off joystick movement and teleportation on the SCENE INSTANCE only.
        ///
        /// The prefab asset is never modified. Snap/continuous turning and gravity stay on:
        /// the participant can still turn to look around and stays grounded, but the ONLY way
        /// to change area is the button that drives <see cref="XRRigTeleporter"/> — which is
        /// what the protocol requires.
        /// </summary>
        static void DisableFreeLocomotion(GameObject rig)
        {
            var disabled = new List<string>();

            foreach (var provider in rig.GetComponentsInChildren<LocomotionProvider>(true))
            {
                var typeName = provider.GetType().Name;

                var isTurn = typeName.Contains("Turn");
                var isGravity = typeName.Contains("Gravity");
                if (isTurn || isGravity)
                    continue;

                provider.enabled = false;
                disabled.Add(typeName);
            }

            // Also hide the teleport interactors so no teleport arc is drawn when the
            // thumbstick is pushed (the provider is off, so it would do nothing anyway).
            foreach (var child in rig.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.IndexOf("Teleport Interactor", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    child.gameObject.SetActive(false);
                    disabled.Add(child.name);
                }
            }

            Debug.Log("[IKEA_EEG] Free locomotion disabled on the scene's rig instance " +
                      $"(prefab untouched): {string.Join(", ", disabled)}");
        }

        // =================================================================================
        // AREA 0 — VR familiarization
        // =================================================================================

        /// <summary>
        /// The practice room. Visually distinct from every other area (a light room with a
        /// coloured floor stripe) so nobody can confuse it with the experiment.
        ///
        /// CONTENT RULES, enforced by what is built here:
        ///   * NO chairs and NO ChairTarget — Area 0 cannot prime the Area B search;
        ///   * NO memory word appears anywhere — Area 0 cannot prime Area A recall;
        ///   * practice objects are a sphere, a cube and a large UI button: shapes with no
        ///     relationship to any later stimulus dimension;
        ///   * the controller diagram lives HERE and nowhere else, so it cannot appear during
        ///     the cognitive task.
        /// </summary>
        static SpawnPoint BuildArea0(Transform parent, ExperimentAssetBuilder.MaterialSet mats,
            out Area0Ui ui, out List<PracticeObject> practiceObjects)
        {
            var root = new GameObject("Area_0_Familiarization");
            root.transform.SetParent(parent);
            root.transform.position = k_Area0Origin;

            const float width = 6.0f;
            const float depth = 5.5f;

            CreateBox("Floor_0", root.transform, new Vector3(0f, -0.05f, 0f),
                new Vector3(width, 0.1f, depth), mats.floor);

            // A blue stripe across the floor: the one visual cue that says "this is the
            // practice room, not the experiment".
            CreateBox("Floor_0_Stripe", root.transform, new Vector3(0f, 0.005f, -1.6f),
                new Vector3(width - 0.6f, 0.02f, 0.35f), mats.accent);

            CreateBox("Ceiling_0", root.transform, new Vector3(0f, k_OuterWallHeight, 0f),
                new Vector3(width, 0.1f, depth), mats.ceiling);

            CreateBox("Wall_0_Left", root.transform,
                new Vector3(-width * 0.5f, k_OuterWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_OuterWallHeight, depth), mats.wall);
            CreateBox("Wall_0_Right", root.transform,
                new Vector3(width * 0.5f, k_OuterWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_OuterWallHeight, depth), mats.wall);
            CreateBox("Wall_0_Back", root.transform,
                new Vector3(0f, k_OuterWallHeight * 0.5f, -depth * 0.5f),
                new Vector3(width, k_OuterWallHeight, k_WallThickness), mats.wall);
            CreateBox("Wall_0_Front", root.transform,
                new Vector3(0f, k_OuterWallHeight * 0.5f, depth * 0.5f),
                new Vector3(width, k_OuterWallHeight, k_WallThickness), mats.wall);

            var spawnZ = -1.6f;
            var spawn = CreateSpawn("Spawn_0", root.transform,
                new Vector3(0f, 0f, spawnZ), ExperimentArea.Familiarization);

            // ---- Practice objects ----------------------------------------------------------
            // Two 3D objects at a comfortable pointing distance, plus a large UI button on the
            // panel. Between them the participant practises the two things Area B needs:
            // pointing at a world object, and pressing a UI button.
            var practiceRoot = new GameObject("PracticeObjects");
            practiceRoot.transform.SetParent(root.transform, false);

            // FOUR objects in four highly distinguishable colours. The practice task names one
            // colour and the participant selects it — a concrete goal rather than "select
            // something". The colours are drawn from the chair palette purely for material
            // reuse; the OBJECTS are spheres, share no attribute dimension with the chair task
            // (no size or shape variation), and stand in a different room.
            //
            // The colour is carried ONCE, as a ChairColor. The object id and the participant-
            // facing label are both derived from it, so a practice object cannot be built whose
            // name, material and spoken prompt disagree — which is exactly what happened when the
            // label was a separate hard-coded English string.
            var practiceColors = new (ChairColor color, float x)[]
            {
                (ChairColor.Red, -1.20f),
                (ChairColor.Blue, -0.40f),
                (ChairColor.Yellow, 0.40f),
                (ChairColor.Green, 1.20f),
            };

            practiceObjects = new List<PracticeObject>();

            // The objects sit LOW and CLOSE, entirely below the instruction panel's lower edge,
            // so they cannot occlude any of the text. Previously they were at panel height and
            // in front of it, which is exactly what hid the instructions.
            const float practiceY = 0.95f;
            var practiceZ = spawnZ + 2.15f;

            foreach (var (color, x) in practiceColors)
            {
                practiceObjects.Add(BuildPracticeObject(practiceRoot.transform,
                    new Vector3(x, practiceY, practiceZ), 0.30f, mats, color));
            }

            // ---- Controller help -----------------------------------------------------------
            // Well to the LEFT of the instruction panel (which spans x -0.88..0.88) with its
            // callouts stacked underneath the models rather than beside them, so the whole help
            // occupies its own column and cannot reach the panel.
            BuildControllerHelp(root.transform, new Vector3(-2.05f, 1.60f, spawnZ + 1.9f),
                spawnZ, mats);

            // ---- UI --------------------------------------------------------------------------
            // Raised and enlarged so its lower edge clears the practice objects entirely, and
            // so each block of text gets its own band with real space between them.
            //
            // Vertical budget, panel half-height 700 px:
            //   +680 .. +260   general instructions
            //   +215           divider
            //   +170 ..  +20   practice prompt
            //    -20 .. -100   status line
            //   -150 .. -400   START EXPERIMENT (frame + button + ready label)
            //   -450 .. -560   secondary buttons
            //   -600 .. -690   recenter
            var canvas = CreateWorldCanvas("UI_0_Canvas", root.transform,
                new Vector3(0f, 2.05f, spawnZ + 2.95f), new Vector2(1600f, 1400f), mats,
                spawnZ, 0.0011f);

            ui = new Area0Ui
            {
                panel = canvas.gameObject,
                instruction = CreateText("Txt_FamiliarizationInstruction", canvas.transform,
                    new Vector2(0f, 470f), new Vector2(1500f, 420f), string.Empty, 50f,
                    TextAlignmentOptions.Center, Color.white, autoSizeMin: 28f),
                practicePrompt = CreateText("Txt_PracticePrompt", canvas.transform,
                    new Vector2(0f, 95f), new Vector2(1500f, 150f), string.Empty, 58f,
                    TextAlignmentOptions.Center, new Color(1f, 0.95f, 0.6f), autoSizeMin: 36f),
                status = CreateText("Txt_FamiliarizationStatus", canvas.transform,
                    new Vector2(0f, -60f), new Vector2(1500f, 90f), string.Empty, 42f,
                    TextAlignmentOptions.Center, new Color(0.6f, 1f, 0.7f), autoSizeMin: 28f),
                warning = CreateText("Txt_Warning", canvas.transform, new Vector2(0f, -700f),
                    new Vector2(1500f, 60f), string.Empty, 28f,
                    TextAlignmentOptions.Center, new Color(1f, 0.82f, 0.5f)),
            };

            // A horizontal rule between the general instructions and the practice task, so the
            // two read as separate sections rather than one run-on block. This is what the
            // yellow practice prompt was previously colliding with.
            CreatePanelFrame("Divider_Practice", canvas.transform, new Vector2(0f, 215f),
                new Vector2(1300f, 4f), new Color(0.45f, 0.50f, 0.60f, 0.85f));

            // ---- PRIMARY ACTION: START EXPERIMENT -------------------------------------------
            // Dominant by SIZE, POSITION and a highlight frame — not by colour alone. It is more
            // than twice the area of SKIP INTRO, sits on its own with clear separation above and
            // below, and gains a bright frame plus a "You are ready to begin." label once the
            // practice task succeeds.
            ui.startHighlight = CreatePanelFrame("Frame_StartExperiment", canvas.transform,
                new Vector2(0f, -240f), new Vector2(1060f, 210f),
                new Color(0.25f, 1f, 0.55f, 0.85f));
            ui.startHighlight.SetActive(false);

            ui.startExperimentButton = CreateButton("Btn_StartExperiment", canvas.transform,
                new Vector2(0f, -240f), new Vector2(980f, 170f), "START EXPERIMENT",
                new Color(0.10f, 0.52f, 0.28f), 70f);

            ui.readyLabel = CreateText("Txt_StartExperimentReady", canvas.transform,
                new Vector2(0f, -360f), new Vector2(1200f, 70f), string.Empty, 40f,
                TextAlignmentOptions.Center, new Color(0.55f, 1f, 0.7f), autoSizeMin: 28f);
            ui.readyLabel.gameObject.SetActive(false);

            // ---- SECONDARY ACTIONS ----------------------------------------------------------
            // Smaller, lower, muted, and clearly separated from the primary action.
            ui.replayInstructionsButton = CreateButton("Btn_ReplayInstructions", canvas.transform,
                new Vector2(-270f, -505f), new Vector2(490f, 95f), "REPLAY INSTRUCTIONS",
                new Color(0.28f, 0.30f, 0.36f), 34f);

            ui.skipIntroButton = CreateButton("Btn_SkipIntro", canvas.transform,
                new Vector2(270f, -505f), new Vector2(490f, 95f), "SKIP INTRO",
                new Color(0.34f, 0.30f, 0.30f), 34f);

            ui.recenterButton = CreateButton("Btn_Recenter_0", canvas.transform,
                new Vector2(0f, -640f), new Vector2(380f, 85f), "RECENTER",
                new Color(0.30f, 0.30f, 0.36f), 32f);

            // NOTE: the practice task is the FOUR coloured objects and nothing else. The
            // separate "practice button" that used to live here was removed — with a named
            // target colour the task now has one correct answer, and a fifth selectable thing
            // that is not one of the four would only muddy it. Pressing UI is still practised:
            // START EXPERIMENT, SKIP INTRO and REPLAY INSTRUCTIONS are all UI buttons.

            // ---- LANGUAGE SELECTION ----------------------------------------------------------
            // The first screen a participant sees. It lives in Area 0 because that is where the
            // session starts, but it is its own panel: while it is up the familiarization panel
            // is hidden, so the participant is looking at exactly three choices and nothing
            // they cannot yet read.
            var languageCanvas = CreateWorldCanvas("UI_LanguageSelection", root.transform,
                new Vector3(0f, 1.95f, spawnZ + 2.6f), new Vector2(1500f, 1500f), mats,
                spawnZ, 0.0012f);

            ui.languagePanel = languageCanvas.gameObject;

            // The title is shown in ALL THREE languages at once — before a choice is made,
            // there is no "current language" to show it in, and a participant must be able to
            // recognise the screen whichever language they read.
            ui.languageTitle = CreateText("Txt_LanguageTitle", languageCanvas.transform,
                new Vector2(0f, 560f), new Vector2(1400f, 320f),
                "Select Language\n<size=70%>Seleccione el idioma\n言語を選んでください</size>", 76f,
                TextAlignmentOptions.Center, Color.white, autoSizeMin: 44f);

            // Three large buttons in one vertical column, each labelled in ITS OWN language.
            ui.languageEnglishButton = CreateButton("Btn_Language_EN", languageCanvas.transform,
                new Vector2(0f, 190f), new Vector2(1100f, 210f),
                ExperimentLanguages.NativeName(ExperimentLanguage.English),
                new Color(0.16f, 0.34f, 0.58f), 88f);

            ui.languageSpanishButton = CreateButton("Btn_Language_ES", languageCanvas.transform,
                new Vector2(0f, -60f), new Vector2(1100f, 210f),
                ExperimentLanguages.NativeName(ExperimentLanguage.Spanish),
                new Color(0.16f, 0.34f, 0.58f), 88f);

            ui.languageJapaneseButton = CreateButton("Btn_Language_JA", languageCanvas.transform,
                new Vector2(0f, -310f), new Vector2(1100f, 210f),
                ExperimentLanguages.NativeName(ExperimentLanguage.Japanese),
                new Color(0.16f, 0.34f, 0.58f), 88f);

            languageCanvas.gameObject.SetActive(false);

            // ---- Researcher-only status surface (reserved, inactive) -------------------------
            // Placed low and behind the participant's reading line, hidden by default. It shows
            // NOTHING in this pass: no EEG stream, no marker status and no signal quality
            // exists yet, and inventing a value here would be worse than showing none.
            var researcherCanvas = CreateWorldCanvas("UI_0_ResearcherStatus", root.transform,
                new Vector3(2.3f, 1.1f, spawnZ + 2.2f), new Vector2(900f, 500f), mats,
                spawnZ, 0.0011f);

            ui.researcherStatusPanel = researcherCanvas.gameObject;
            ui.researcherStatusText = CreateText("Txt_ResearcherStatus",
                researcherCanvas.transform, Vector2.zero, new Vector2(840f, 440f),
                string.Empty, 34f, TextAlignmentOptions.TopLeft,
                new Color(0.8f, 0.85f, 0.9f), autoSizeMin: 24f);

            researcherCanvas.gameObject.SetActive(false);

            return spawn;
        }

        /// <summary>
        /// One selectable practice object. Same interaction contract as a chair —
        /// XRSimpleInteractable, no Rigidbody, colliders registered up front — so what the
        /// participant learns here transfers exactly to Area B.
        /// </summary>
        static PracticeObject BuildPracticeObject(Transform parent,
            Vector3 localPosition, float size, ExperimentAssetBuilder.MaterialSet mats,
            ChairColor color)
        {
            // Derived, not passed: one ChairColor decides the object's name, its material, its
            // label and its spoken prompt.
            var id = $"Practice_{color}";

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = id;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = Vector3.one * size;

            var baseMaterial = mats.chairColors[color];
            ApplyMaterial(go, baseMaterial);

            // A short post so the object floats at pointing height without a physics body.
            CreateBox($"{id}_Post", parent, new Vector3(localPosition.x, localPosition.y * 0.5f,
                    localPosition.z), new Vector3(0.06f, localPosition.y, 0.06f), mats.doorFrame);

            // The colour name in text, so the task never depends on colour perception alone.
            //
            // ABOVE the object and 0.30 m nearer the participant. It used to sit BELOW, at the
            // same depth, which put it inside the support post — the label and the post
            // intersected and neither was readable. Nothing occupies the space above an object,
            // and the forward offset keeps the text clearly in front of every post.
            var label = CreateWorldCanvas($"UI_{id}_Label", parent,
                new Vector3(localPosition.x, localPosition.y + 0.30f, localPosition.z - 0.30f),
                new Vector2(460f, 140f), mats, -1.6f, 0.0013f);

            // LOCALIZED, not a hard-coded English word. The key is built from the same ChairColor
            // the object is painted with, so the label always names the colour the participant is
            // actually looking at, in the language they chose.
            var labelText = CreateText($"Txt_{id}", label.transform, Vector2.zero,
                new Vector2(400f, 110f), string.Empty, 72f,
                TextAlignmentOptions.Center, Color.white);

            LocalizeText(labelText, LocKeys.PracticeColorPrefix + color);

            var interactable = go.AddComponent<XRSimpleInteractable>();
            interactable.colliders.Clear();
            interactable.colliders.AddRange(go.GetComponentsInChildren<Collider>(true));

            var practice = go.AddComponent<PracticeObject>();
            practice.Configure(id, color, baseMaterial, mats.chairHover, mats.practiceNeutral,
                mats.practiceSuccess);
            practice.SetRenderers(go.GetComponentsInChildren<Renderer>(true));
            practice.SetInteractable(interactable);

            EditorUtility.SetDirty(practice);
            EditorUtility.SetDirty(interactable);
            return practice;
        }

        /// <summary>
        /// A static, non-interactive diagram of a controller with its three inputs labelled.
        ///
        /// WHY NOT THE VR TEMPLATE'S CALLOUT SYSTEM: the template's controller tooltips
        /// (Callout + CalloutGazeController in VRTemplateAssets) attach to children of the
        /// controller MODEL, which XRI instantiates at run time from the controller prefab.
        /// Wiring them would mean modifying the XR Origin / controller prefabs — a protected
        /// system — and would risk the validated ray, select and haptics configuration. This
        /// diagram is built from primitives inside Area 0, touches nothing shared, exists in
        /// exactly one place in the world and is trivially removable.
        /// </summary>
        // GUID of Assets/Samples/XR Interaction Toolkit/3.4.1/Starter Assets/Models/
        // UniversalController.fbx — the SAME model asset the rig's controller prefabs display.
        const string k_ControllerModelGuid = "147ae308eec018b40a7b312ae58f44c7";

        /// <summary>
        /// The Area 0 controller help: a NON-TRACKED copy of the real controller model with
        /// labelled callouts pointing at its three inputs.
        ///
        /// WHY A COPY OF THE MODEL RATHER THAN THE LIVE CONTROLLER:
        /// The participant must recognise the thing in the picture as the thing in their hand,
        /// so it uses UniversalController.fbx — the same model asset the rig's controller
        /// prefabs display. But it is instantiated as a plain scene object in Area 0: nothing
        /// is parented to the XR Origin, no controller prefab is opened or edited, and no
        /// tracking, interactor or OpenXR profile is involved. The live controllers are
        /// untouched by construction.
        ///
        /// The template's Callout/CalloutGazeController system was considered and rejected: it
        /// attaches to children of the runtime-spawned controller model, which would mean
        /// editing the controller prefab — a protected system.
        ///
        /// If the model asset is ever missing, this falls back to a labelled placeholder rather
        /// than failing the build.
        /// </summary>
        static void BuildControllerHelp(Transform parent, Vector3 localPosition, float viewerZ,
            ExperimentAssetBuilder.MaterialSet mats)
        {
            var root = new GameObject("ControllerDiagram");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPosition;

            var toViewer = new Vector3(0f, localPosition.y, viewerZ) - localPosition;
            toViewer.y = 0f;
            root.transform.localRotation = toViewer.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(-toViewer.normalized, Vector3.up)
                : Quaternion.identity;

            var modelPath = AssetDatabase.GUIDToAssetPath(k_ControllerModelGuid);
            var model = string.IsNullOrEmpty(modelPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);

            if (model == null)
            {
                Debug.LogWarning("[IKEA_EEG] The controller model asset could not be found; the " +
                                 "Area 0 help falls back to a labelled placeholder. Expected: " +
                                 "Starter Assets/Models/UniversalController.fbx");
            }

            // Both hands, so the participant sees the orientation of the controller they are
            // actually holding whichever hand they look at.
            BuildControllerHelpUnit(root.transform, model, "Left", new Vector3(-0.30f, 0f, 0f),
                mats);
            BuildControllerHelpUnit(root.transform, model, "Right", new Vector3(0.30f, 0f, 0f),
                mats);

            // ---- Callout labels -------------------------------------------------------------
            // BELOW the models, not beside them. Sideways labels pushed the help's footprint
            // into the instruction panel; stacking keeps the whole thing inside one narrow
            // column that ends well clear of the panel's left edge.
            //
            // ONLY THE TWO CONTROLS A PARTICIPANT NEEDS. The thumbstick callout was removed:
            // the participant never has to navigate, and the developer gesture that does use
            // the thumbsticks is deliberately undocumented — putting it on a participant-facing
            // panel would advertise a developer tool.
            var labelCanvas = CreateWorldCanvas("UI_ControllerDiagramLabels", root.transform,
                Vector3.zero, new Vector2(1000f, 800f), mats, viewerZ, 0.0011f);

            labelCanvas.transform.localRotation = Quaternion.identity;
            labelCanvas.transform.localPosition = new Vector3(0f, -0.72f, 0f);

            // ALL FOUR captions are localized. They were the last participant-facing English
            // literals left in Area 0: the diagram itself is language-neutral, but everything
            // written on it has to be readable by the participant who chose Spanish or Japanese.
            //
            // The coloured bullet is passed as a FORMAT rather than being part of the string —
            // it is decoration that ties the caption to the marker on the model, and it must not
            // be something a translation can accidentally drop or reorder.
            LocalizeText(
                CreateText("Txt_DiagramTitle", labelCanvas.transform, new Vector2(0f, 310f),
                    new Vector2(960f, 110f), string.Empty, 58f,
                    TextAlignmentOptions.Center, new Color(0.55f, 0.85f, 1f)),
                LocKeys.ControllerTitle);

            LocalizeText(
                CreateText("Txt_IndexTrigger", labelCanvas.transform, new Vector2(0f, 130f),
                    new Vector2(960f, 170f), string.Empty,
                    52f, TextAlignmentOptions.Center, Color.white, autoSizeMin: 32f),
                LocKeys.ControllerIndexTrigger, "<color=#4E9BFF>●</color>  {0}");

            LocalizeText(
                CreateText("Txt_GripTrigger", labelCanvas.transform, new Vector2(0f, -60f),
                    new Vector2(960f, 170f), string.Empty,
                    52f, TextAlignmentOptions.Center, Color.white, autoSizeMin: 32f),
                LocKeys.ControllerGripTrigger, "<color=#33E38A>●</color>  {0}");

            LocalizeText(
                CreateText("Txt_DiagramFooter", labelCanvas.transform, new Vector2(0f, -260f),
                    new Vector2(960f, 140f), string.Empty,
                    42f, TextAlignmentOptions.Center, new Color(0.75f, 0.9f, 0.78f),
                    autoSizeMin: 28f),
                LocKeys.ControllerFooter);
        }

        /// <summary>
        /// One hand's controller display: the model (or a placeholder), three coloured markers
        /// on its inputs, and a hand label.
        /// </summary>
        static void BuildControllerHelpUnit(Transform parent, GameObject model, string hand,
            Vector3 localPosition, ExperimentAssetBuilder.MaterialSet mats)
        {
            var unit = new GameObject($"Controller_{hand}");
            unit.transform.SetParent(parent, false);
            unit.transform.localPosition = localPosition;

            // Tilted so the front face, the side and the top are all visible at once —
            // a controller seen edge-on tells the participant nothing.
            unit.transform.localRotation = Quaternion.Euler(-25f, hand == "Left" ? 25f : -25f, 0f);

            if (model != null)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, unit.transform);
                instance.name = $"ControllerModel_{hand}";
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                // Larger than life so the shape and the marked inputs read clearly from the
                // spawn point rather than being a distant silhouette.
                instance.transform.localScale = Vector3.one * 2.6f;

                // A display object, never an interactable: strip anything that could be hit by
                // a ray or driven by tracking. The SOURCE asset is untouched — this only edits
                // the instance that was just created in Area 0.
                foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(collider);

                foreach (var body in instance.GetComponentsInChildren<Rigidbody>(true))
                    Object.DestroyImmediate(body);
            }
            else
            {
                // Fallback placeholder so Area 0 still explains the inputs.
                CreateBox($"Placeholder_{hand}", unit.transform, new Vector3(0f, -0.06f, 0f),
                    new Vector3(0.09f, 0.20f, 0.09f), mats.doorFrame, markStatic: false);
            }

            // Markers for the TWO participant-relevant inputs only, colour-matched to their
            // callout bullets. There is deliberately no thumbstick marker — see the callout
            // comment in BuildControllerHelp.
            CreateSphereMarker($"Marker_IndexTrigger_{hand}", unit.transform,
                new Vector3(0f, 0.020f, -0.072f), 0.040f, mats.markerIndexTrigger);
            CreateSphereMarker($"Marker_GripTrigger_{hand}", unit.transform,
                new Vector3(hand == "Left" ? 0.068f : -0.068f, -0.072f, 0f), 0.038f,
                mats.markerGripTrigger);

            var handCanvas = CreateWorldCanvas($"UI_ControllerHand_{hand}", unit.transform,
                new Vector3(0f, -0.30f, 0f), new Vector2(400f, 120f), mats, -1.6f, 0.0009f);

            handCanvas.transform.localRotation = Quaternion.identity;

            // LEFT / RIGHT is participant-facing too — IZQUIERDA / DERECHA / 左 / 右.
            LocalizeText(
                CreateText($"Txt_Hand_{hand}", handCanvas.transform, Vector2.zero,
                    new Vector2(380f, 100f), string.Empty, 60f,
                    TextAlignmentOptions.Center, new Color(0.8f, 0.85f, 0.9f)),
                hand == "Left" ? LocKeys.HandLeft : LocKeys.HandRight);
        }

        static void CreateSphereMarker(string name, Transform parent, Vector3 localPosition,
            float diameter, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = Vector3.one * diameter;

            ApplyMaterial(go, material);

            // Markers are decoration on a display object: nothing may point at them.
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);
        }

        // =================================================================================
        // AREA A — entrance
        // =================================================================================

        static SpawnPoint BuildAreaA(Transform parent, ExperimentAssetBuilder.MaterialSet mats,
            out AreaAUi ui)
        {
            var root = new GameObject("Area_A_Entrance");
            root.transform.SetParent(parent);
            root.transform.position = k_AreaAOrigin;

            const float width = k_VestibuleWidth;
            const float depth = k_VestibuleDepth;

            CreateBox("Floor_A", root.transform, new Vector3(0f, -0.05f, 0f),
                new Vector3(width, 0.1f, depth), mats.floor);

            // Facade of the store the participant is standing in front of, with a doorway.
            BuildFacadeWithDoorway("A", root.transform, new Vector3(0f, 0f, depth * 0.5f),
                width, mats);

            // Low side walls so the space reads as an entrance vestibule and nothing can be
            // walked past; they also stop the far interactor selecting through open sides.
            CreateBox("Wall_A_Left", root.transform,
                new Vector3(-width * 0.5f, k_OuterWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_OuterWallHeight, depth), mats.wall);
            CreateBox("Wall_A_Right", root.transform,
                new Vector3(width * 0.5f, k_OuterWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_OuterWallHeight, depth), mats.wall);
            CreateBox("Wall_A_Back", root.transform,
                new Vector3(0f, k_OuterWallHeight * 0.5f, -depth * 0.5f),
                new Vector3(width, k_OuterWallHeight, k_WallThickness), mats.wall);

            // Standing just inside the space, ~2.1 m from the panel: close enough to read
            // comfortably, with room behind for room-scale movement.
            var spawn = CreateSpawn("Spawn_A", root.transform,
                new Vector3(0f, 0f, -0.7f), ExperimentArea.AreaA);

            // ---- UI ----------------------------------------------------------------------
            var canvas = CreateWorldCanvas("UI_A_Canvas", root.transform,
                new Vector3(0f, 1.65f, 1.4f), new Vector2(1500f, 1150f), mats,
                spawn.transform.localPosition.z, 0.00095f);

            ui = new AreaAUi
            {
                panel = canvas.gameObject,
                title = CreateText("Txt_Title", canvas.transform, new Vector2(0f, 480f),
                    new Vector2(1400f, 110f), "COGNITIVE ASSESSMENT", 68f,
                    TextAlignmentOptions.Center, new Color(0.55f, 0.85f, 1f)),
                instruction = CreateText("Txt_Instruction", canvas.transform, new Vector2(0f, 250f),
                    new Vector2(1360f, 360f), string.Empty, 54f,
                    TextAlignmentOptions.Center, Color.white),
                word = CreateText("Txt_WordDisplay", canvas.transform, new Vector2(0f, 60f),
                    new Vector2(1360f, 200f), string.Empty, 130f,
                    TextAlignmentOptions.Center, new Color(1f, 0.93f, 0.5f)),

                // PROGRESS COUNTER — "Item 7 / 30".
                //
                // Sits in the free band between the recognition prompt (centred at +250) and
                // the stimulus word (centred at +60, ~65 px of glyph half-height, so its top is
                // near +125). At +170 with a 55 px rect it clears both.
                //
                // 34 pt against the word's 130 pt, and a muted blue-grey against the word's warm
                // yellow: it has to be findable when looked for and ignorable when not. The
                // stimulus is the task; this is orientation.
                recognitionCounter = CreateText("Txt_RecognitionCounter", canvas.transform,
                    new Vector2(0f, 170f), new Vector2(700f, 55f), string.Empty, 34f,
                    TextAlignmentOptions.Center, new Color(0.62f, 0.70f, 0.80f)),

                // DEVELOPER QA OVERLAY — NOT PARTICIPANT FUNCTIONALITY.
                //
                // Placed over the Area A status band (+/-110), which the recognition phase sets
                // to empty and leaves empty for its whole duration, so nothing participant-facing
                // shares this space while an item is on screen.
                //
                // Deliberately MAGENTA. Nothing else in the participant UI uses this colour, so
                // an overlay left on by accident is unmistakable in a headset and on a recording
                // rather than blending into the experiment.
                developerCheatsheet = CreateText("Txt_DevCheatsheet_A", canvas.transform,
                    new Vector2(0f, -120f), new Vector2(900f, 160f), string.Empty, 34f,
                    TextAlignmentOptions.Center, new Color(1f, 0.35f, 0.85f)),
                status = CreateText("Txt_Status", canvas.transform, new Vector2(0f, -110f),
                    new Vector2(1360f, 140f), string.Empty, 42f,
                    TextAlignmentOptions.Center, new Color(0.75f, 0.85f, 0.75f)),
                warning = CreateText("Txt_Warning", canvas.transform, new Vector2(0f, -270f),
                    new Vector2(1400f, 190f), string.Empty, 40f,
                    TextAlignmentOptions.Center, new Color(1f, 0.82f, 0.5f)),
                startButton = CreateButton("Btn_Start", canvas.transform, new Vector2(0f, -430f),
                    new Vector2(560f, 130f), "START", new Color(0.10f, 0.45f, 0.75f)),
                enterButton = CreateButton("Btn_EnterAreaB", canvas.transform, new Vector2(0f, -430f),
                    new Vector2(700f, 130f), "ENTER SHOWROOM", new Color(0.13f, 0.55f, 0.30f)),
                recheckAudioButton = CreateButton("Btn_RecheckAudio", canvas.transform,
                    new Vector2(0f, -430f), new Vector2(700f, 130f), "RE-CHECK AUDIO",
                    new Color(0.65f, 0.42f, 0.10f)),
                // MOVED TO THE LEFT PERIPHERY (was centred at y -560).
                //
                // The Recognition response buttons occupy the lower-central zone in world space
                // in front of this panel, and the centred Recenter used to land inside them:
                // both spanned roughly y 1.07-1.18 m and overlapping x, so on the headset the
                // Recenter instruction sat on top of a response target. Recenter is a utility
                // and the response is the task, so the utility is what moves aside.
                recenterButton = CreateButton("Btn_Recenter_A", canvas.transform,
                    new Vector2(-580f, -430f), new Vector2(420f, 100f), "RECENTER",
                    new Color(0.30f, 0.30f, 0.36f)),
            };

            ui.enterButton.gameObject.SetActive(false);
            ui.recheckAudioButton.gameObject.SetActive(false);

            // BOTH RECOGNITION OVERLAYS START INACTIVE, not merely empty. The manager raises the
            // counter when a phase begins; the developer overlay additionally needs its config
            // gate open AND a deliberate button press. Authoring them inactive means a build in
            // which the manager never runs still shows the participant neither of them.
            ui.recognitionCounter.gameObject.SetActive(false);
            ui.developerCheatsheet.gameObject.SetActive(false);

            return spawn;
        }

        // =================================================================================
        // AREA B — showroom
        // =================================================================================

        static SpawnPoint BuildAreaB(Transform parent, ExperimentAssetBuilder.MaterialSet mats,
            out AreaBUi ui, out List<ChairTarget> chairs, out List<ChairSlot> slots)
        {
            var root = new GameObject("Area_B_Showroom");
            root.transform.SetParent(parent);
            root.transform.position = k_AreaBOrigin;

            const float width = 14f;
            const float depth = 13f;

            CreateBox("Floor_B", root.transform, new Vector3(0f, -0.05f, 0f),
                new Vector3(width, 0.1f, depth), mats.floor);
            CreateBox("Ceiling_B", root.transform, new Vector3(0f, k_ShowroomWallHeight, 0f),
                new Vector3(width, 0.1f, depth), mats.ceiling);

            CreateBox("Wall_B_Front", root.transform,
                new Vector3(0f, k_ShowroomWallHeight * 0.5f, depth * 0.5f),
                new Vector3(width, k_ShowroomWallHeight, k_WallThickness), mats.wall);
            CreateBox("Wall_B_Back", root.transform,
                new Vector3(0f, k_ShowroomWallHeight * 0.5f, -depth * 0.5f),
                new Vector3(width, k_ShowroomWallHeight, k_WallThickness), mats.wall);
            CreateBox("Wall_B_Left", root.transform,
                new Vector3(-width * 0.5f, k_ShowroomWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_ShowroomWallHeight, depth), mats.wall);
            CreateBox("Wall_B_Right", root.transform,
                new Vector3(width * 0.5f, k_ShowroomWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_ShowroomWallHeight, depth), mats.wall);

            var spawnZ = -2.6f;
            var spawn = CreateSpawn("Spawn_B", root.transform,
                new Vector3(0f, 0f, spawnZ), ExperimentArea.AreaB);

            // ---- Chair slots ---------------------------------------------------------------
            // The six FIXED positions a chair can occupy. Randomisation assigns chairs to these
            // slots; it never invents coordinates. See ChairSlot for why.
            //
            // The slots sit on a single ARC centred on the spawn point, not in rows. Two
            // reasons, both of which would otherwise break the task:
            //   * on an arc no chair occludes another, so every chair is equally pointable —
            //     in a 2x3 grid the back row is hidden behind the front row from the ray's
            //     point of view and could not be selected at all;
            //   * every slot is the same distance from the participant, so ray-pointing
            //     difficulty (and therefore response time) is not confounded with which
            //     slot happens to hold the target.
            var slotRoot = new GameObject("ChairSlots");
            slotRoot.transform.SetParent(root.transform, false);

            const float arcRadius = 5.0f;
            var arcAngles = new[] { -50f, -30f, -10f, 10f, 30f, 50f };
            var viewPoint = new Vector3(0f, 0f, spawnZ);

            slots = new List<ChairSlot>();

            for (var i = 0; i < arcAngles.Length; i++)
            {
                var angle = arcAngles[i] * Mathf.Deg2Rad;
                var position = new Vector3(
                    Mathf.Sin(angle) * arcRadius,
                    0f,
                    spawnZ + Mathf.Cos(angle) * arcRadius);

                var slotGo = new GameObject($"ChairSlot_{i:D2}");
                slotGo.transform.SetParent(slotRoot.transform, false);
                slotGo.transform.localPosition = position;

                // The occupant faces the participant. Authored on the SLOT so a chair moved
                // here in any trial presents the same aspect. The chair body is modelled with
                // its backrest at -Z, so its front is +Z and pointing +Z at the spawn shows the
                // seat with the backrest behind it.
                var toSpawn = viewPoint - position;
                toSpawn.y = 0f;
                slotGo.transform.localRotation = toSpawn.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(toSpawn.normalized, Vector3.up)
                    : Quaternion.Euler(0f, 180f, 0f);

                var slot = slotGo.AddComponent<ChairSlot>();
                slot.SetSlotIndex(i);
                slots.Add(slot);
            }

            // ---- Chairs -------------------------------------------------------------------
            var chairRoot = new GameObject("Chairs");
            chairRoot.transform.SetParent(root.transform, false);

            chairs = new List<ChairTarget>();

            for (var i = 0; i < k_Chairs.Length; i++)
            {
                var (id, spec) = k_Chairs[i];
                var chair = BuildChair(id, spec, chairRoot.transform, slots[i], mats);
                chairs.Add(chair);
            }

            // ---- UI ----------------------------------------------------------------------
            // Both panels sit in front of the far wall, ~8.2 m from the spawn point. The
            // NearFarInteractor's curve caster reaches 10 m by default, so the Exit button
            // stays within reach even if the participant physically steps back a little.
            const float uiZ = 5.6f;

            // Persistent instruction, mounted high so it stays legible over the chairs for the
            // whole selection phase. At ~8.2 m the previous 2.09 m panel was too small to read
            // comfortably, so both panels are scaled up substantially: the instruction panel is
            // now ~3.8 m wide with ~26 cm tall glyphs, which subtends roughly twice the visual
            // angle it did before.
            const float bigScale = 0.0020f;

            var instructionCanvas = CreateWorldCanvas("UI_B_Instruction", root.transform,
                new Vector3(0f, 2.85f, uiZ), new Vector2(1900f, 560f), mats, spawnZ, bigScale);

            var statusCanvas = CreateWorldCanvas("UI_B_Status", root.transform,
                new Vector3(0f, 1.45f, uiZ), new Vector2(1700f, 760f), mats, spawnZ, bigScale);

            ui = new AreaBUi
            {
                instructionPanel = instructionCanvas.gameObject,
                // Carries the general task instructions before READY and the trial target after
                // it. Auto-sizing lets the same label hold a seven-line paragraph and a
                // three-word target: the paragraph shrinks to fit, the target renders large.
                instruction = CreateText("Txt_ChairInstruction", instructionCanvas.transform,
                    Vector2.zero, new Vector2(1830f, 500f), string.Empty, 130f,
                    TextAlignmentOptions.Center, new Color(1f, 0.95f, 0.6f), autoSizeMin: 42f),

                statusPanel = statusCanvas.gameObject,
                status = CreateText("Txt_Status", statusCanvas.transform, new Vector2(0f, 250f),
                    new Vector2(1620f, 200f), string.Empty, 70f,
                    TextAlignmentOptions.Center, Color.white),
                feedback = CreateText("Txt_Feedback", statusCanvas.transform, new Vector2(0f, 70f),
                    new Vector2(1620f, 200f), string.Empty, 76f,
                    TextAlignmentOptions.Center, new Color(0.6f, 1f, 0.7f)),
                warning = CreateText("Txt_Warning", statusCanvas.transform, new Vector2(0f, -100f),
                    new Vector2(1620f, 140f), string.Empty, 50f,
                    TextAlignmentOptions.Center, new Color(1f, 0.82f, 0.5f)),
                exitButton = CreateButton("Btn_ExitToAreaC", statusCanvas.transform,
                    new Vector2(0f, -240f), new Vector2(760f, 150f), "EXIT SHOWROOM",
                    new Color(0.13f, 0.55f, 0.30f), 64f),

                recenterButton = CreateButton("Btn_Recenter_B", statusCanvas.transform,
                    new Vector2(0f, -400f), new Vector2(480f, 110f), "RECENTER",
                    new Color(0.30f, 0.30f, 0.36f), 50f),
            };

            ui.exitButton.gameObject.SetActive(false);

            // ---- Instruction overlay ---------------------------------------------------------
            // A LARGE surface 2.2 m in front of the participant. At that distance it fills the
            // view and stands between them and the chairs — deliberately, so the task is
            // understood before the stimuli are ever seen. Font sizes here are set for
            // older-adult legibility: ~9 cm glyphs at 2.2 m.
            const float overlayZ = -0.4f;      // 2.2 m in front of Spawn_B at z = -2.6

            var overlayCanvas = CreateWorldCanvas("UI_B_InstructionOverlay", root.transform,
                new Vector3(0f, 1.95f, overlayZ), new Vector2(1700f, 1250f), mats, spawnZ,
                0.0016f);

            ui.instructionOverlay = overlayCanvas.gameObject;

            ui.overlayText = CreateText("Txt_AreaBOverlay", overlayCanvas.transform,
                new Vector2(0f, 330f), new Vector2(1600f, 520f), string.Empty, 78f,
                TextAlignmentOptions.Center, Color.white, autoSizeMin: 46f);

            // READY lives ON the overlay. It gates the block, and the overlay stands in front
            // of everything else in Area B — a READY button on the far panel would be behind
            // the very surface the participant is reading.
            LocalizeText(
                CreateText("Txt_AreaBOverlayLegendHint", overlayCanvas.transform,
                    new Vector2(0f, -20f), new Vector2(1600f, 120f), string.Empty, 52f,
                    TextAlignmentOptions.Center, new Color(0.75f, 0.85f, 0.95f),
                    autoSizeMin: 34f),
                LocKeys.ShapeLegendHint);

            // ZONE 2 — READY, lifted from y -420 to -240 so it clears the legend panel below
            // it (legend top 1.36 m, READY bottom 1.43 m) and no longer dominates the lower
            // half of the view on its own.
            ui.readyButton = CreateButton("Btn_Ready", overlayCanvas.transform,
                new Vector2(0f, -240f), new Vector2(760f, 150f), "READY",
                new Color(0.10f, 0.45f, 0.75f), 76f);

            // ---- Shape-category legend -------------------------------------------------------
            // NEARER the participant and LOWER than the overlay, not tucked against it.
            //
            // The two used to share a depth, which is why the shape examples appeared to sit on
            // top of the instruction text. Moving the legend to 1.5 m and dropping it to
            // knee-height puts it BELOW the overlay's lower edge in the participant's view —
            // the separation is angular, so it holds however tall the reader is. Being closer
            // also makes the examples larger, which is what they needed.
            // ZONE 3 — the shape legend, lifted into the useful field of view.
            //
            // It was at y 0.25 m and 1.5 m out, which put it roughly 45 degrees below the eye
            // line: the participant had to look at the floor to read it, which is what the
            // headset test reported. At y 1.30 m it sits just under the READY button on the
            // instruction surface and is read with a small downward glance.
            //
            // z is 0.10 m NEARER the participant than the overlay (overlayZ = -0.4), so the
            // legend panel is never occluded by the instruction surface behind it.
            ui.shapeLegend = BuildShapeLegend(root.transform,
                new Vector3(0f, 1.05f, overlayZ - 0.10f), spawnZ, mats);
            ui.shapeLegend.SetActive(false);

            overlayCanvas.gameObject.SetActive(false);

            return spawn;
        }

        /// <summary>
        /// A small non-interactive key to the three shape categories: one simplified BACKREST
        /// per category, with its label underneath.
        ///
        /// It shows backrests rather than whole chairs on purpose. The backrest is what the
        /// category actually names, and simplified examples cannot be mistaken for the six
        /// chairs of the current trial — the legend is a definition of the words, not a hint
        /// about which chair to pick.
        ///
        /// It has NO colliders and NO interactable, so it can never be selected, and it is
        /// hidden for the entire response phase (see ExperimentUIController.ShowShapeLegend).
        /// </summary>
        /// <summary>
        /// The three shape categories, as a self-contained reference panel.
        ///
        /// WHAT WAS WRONG BEFORE (both verified in the built scene):
        ///   * It sat at world y 0.25 m — about 45 degrees BELOW the eye line at this distance,
        ///     which is why it read as "too low / outside the useful view" on the headset.
        ///   * Its labels were placed at x = +/-1100 px on a canvas only 2000 px wide
        ///     (+/-1000), so the outer two were clipped by the canvas edge.
        ///   * At 1.5 m column spacing the whole thing spanned ~3.5 m, far wider than the
        ///     instruction surface behind it.
        ///
        /// It is now a compact panel on its own backing quad, sitting just in front of the
        /// instruction overlay at eye height. The backing is what stops the examples reading as
        /// three more selectable chairs floating in the room.
        ///
        /// Still a PICTURE, never a target: every collider is stripped, so no interactor can
        /// hover or select it.
        /// </summary>
        static GameObject BuildShapeLegend(Transform parent, Vector3 localPosition, float viewerZ,
            ExperimentAssetBuilder.MaterialSet mats)
        {
            var root = new GameObject("ShapeLegend");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPosition;

            var material = mats.chairColors[ChairColor.White];

            // 0.62 m between columns => ~1.5 m overall, comfortably inside the 2.72 m
            // instruction surface behind it and well within a comfortable field of view.
            const float spacing = 0.62f;

            // A dark backing quad BEHIND the examples, so the white shapes read against a flat
            // surface instead of against the showroom and the real chairs.
            // Centred on the legend's own origin and 0.62 m tall, so the whole panel spans
            // root.y +/-0.31 and its placement can be reasoned about from one number.
            var backing = CreateBox("LegendPanel", root.transform, new Vector3(0f, 0f, 0.06f),
                new Vector3(1.76f, 0.62f, 0.02f), mats.uiPanel, markStatic: false);

            for (var i = 0; i < 3; i++)
            {
                var shape = (ChairShape)i;
                var x = (i - 1) * spacing;

                var entry = new GameObject($"Legend_{shape}");
                entry.transform.SetParent(root.transform, false);
                entry.transform.localPosition = new Vector3(x, 0f, 0f);

                // Each example is a BACKREST only — never a whole chair — drawn at roughly
                // 0.34 x 0.30 m so it is legible at ~2 m without crowding its neighbours.
                switch (shape)
                {
                    case ChairShape.Solid:
                        // One continuous filled panel. Nothing breaks its surface.
                        CreateBox("Back_Panel", entry.transform, new Vector3(0f, 0.09f, 0f),
                            new Vector3(0.34f, 0.28f, 0.04f), material, markStatic: false);
                        break;

                    case ChairShape.Slatted:
                        // Two uprights and THREE rails. The gaps are the whole point, so the
                        // rails are thin and the spaces between them are wider than the rails.
                        CreateBox("Post_L", entry.transform, new Vector3(-0.15f, 0.09f, 0f),
                            new Vector3(0.04f, 0.28f, 0.04f), material, markStatic: false);
                        CreateBox("Post_R", entry.transform, new Vector3(0.15f, 0.09f, 0f),
                            new Vector3(0.04f, 0.28f, 0.04f), material, markStatic: false);
                        CreateBox("Rail_Top", entry.transform, new Vector3(0f, 0.20f, 0f),
                            new Vector3(0.34f, 0.045f, 0.04f), material, markStatic: false);
                        CreateBox("Rail_Mid", entry.transform, new Vector3(0f, 0.09f, 0f),
                            new Vector3(0.34f, 0.045f, 0.04f), material, markStatic: false);
                        CreateBox("Rail_Low", entry.transform, new Vector3(0f, -0.02f, 0f),
                            new Vector3(0.34f, 0.045f, 0.04f), material, markStatic: false);
                        break;

                    case ChairShape.Curved:
                        // A HORIZONTAL cylinder: its round profile is visible head-on, so it
                        // cannot be mistaken for the flat rectangular panel on the left. A
                        // vertical cylinder read as a post rather than as a curved back.
                        var curved = CreateCylinder("Back_Curved", entry.transform,
                            new Vector3(0f, 0.09f, 0f),
                            new Vector3(0.30f, 0.17f, 0.30f), material);
                        curved.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                        break;
                }
            }

            // Colliders on ANY part of the legend — examples or backing — would make it
            // pointable. Stripped from the whole subtree after construction, once.
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(collider);

            // ---- Labels ---------------------------------------------------------------------
            // metresPerPixel 0.0016 matches the instruction overlay, so glyph sizes are
            // consistent between the two surfaces.
            const float metresPerPixel = 0.0016f;

            // THE CANVAS MUST SIT BEHIND THE EXAMPLES.
            //
            // THE BUG THIS FIXES: CreateWorldCanvas attaches a full-rect Background image at
            // rgba(0.05, 0.06, 0.09, 0.94) — 94% opaque. At z -0.02 that 2.08 x 0.74 m plate
            // stood IN FRONT of the backrest examples (which span z -0.02..+0.02) and painted
            // over them, so SOLID and SLATTED appeared cut in half or missing. CURVED looked
            // fine only by accident: its rotated cylinder is 0.30 m deep and poked through the
            // plate.
            //
            // At z +0.045 the plate sits behind the examples and in front of the backing panel
            // (front face 0.05), so it now works AS the label plate instead of hiding the shapes.
            var canvas = CreateWorldCanvas("UI_B_ShapeLegend", root.transform,
                new Vector3(0f, 0f, 0.045f), new Vector2(1300f, 460f), mats, viewerZ,
                metresPerPixel);

            LocalizeText(
                CreateText("Txt_LegendTitle", canvas.transform, new Vector2(0f, 176f),
                    new Vector2(1240f, 80f), string.Empty, 46f,
                    TextAlignmentOptions.Center, new Color(0.75f, 0.85f, 0.95f),
                    autoSizeMin: 30f),
                LocKeys.ShapeLegendTitle);

            for (var i = 0; i < 3; i++)
            {
                var shape = (ChairShape)i;

                // Column centres in PIXELS, from the same spacing the geometry used, so the
                // label is always under its own example.
                var x = (i - 1) * (spacing / metresPerPixel);

                // 380 px wide at +/-387 px spans +/-577 — inside the canvas half-width of 650.
                // The previous layout put 700 px boxes at +/-750 px and clipped the outer two.
                LocalizeText(
                    CreateText($"Txt_Legend_{shape}", canvas.transform, new Vector2(x, -122f),
                        new Vector2(380f, 90f), string.Empty, 54f,
                        TextAlignmentOptions.Center, new Color(1f, 0.95f, 0.6f),
                        autoSizeMin: 30f),
                    LocKeys.ShapePrefix + shape);
            }

            return root;
        }

        // =================================================================================
        // AREA C — exit
        // =================================================================================

        static SpawnPoint BuildAreaC(Transform parent, ExperimentAssetBuilder.MaterialSet mats,
            out AreaCUi ui)
        {
            var root = new GameObject("Area_C_Exit");
            root.transform.SetParent(parent);
            root.transform.position = k_AreaCOrigin;

            const float width = k_VestibuleWidth;
            const float depth = k_VestibuleDepth;

            CreateBox("Floor_C", root.transform, new Vector3(0f, -0.05f, 0f),
                new Vector3(width, 0.1f, depth), mats.floor);

            // The exit doorway is behind the participant: they have just left the showroom.
            BuildFacadeWithDoorway("C", root.transform, new Vector3(0f, 0f, -depth * 0.5f),
                width, mats);

            CreateBox("Wall_C_Left", root.transform,
                new Vector3(-width * 0.5f, k_OuterWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_OuterWallHeight, depth), mats.wall);
            CreateBox("Wall_C_Right", root.transform,
                new Vector3(width * 0.5f, k_OuterWallHeight * 0.5f, 0f),
                new Vector3(k_WallThickness, k_OuterWallHeight, depth), mats.wall);
            CreateBox("Wall_C_Front", root.transform,
                new Vector3(0f, k_OuterWallHeight * 0.5f, depth * 0.5f),
                new Vector3(width, k_OuterWallHeight, k_WallThickness), mats.wall);

            var spawn = CreateSpawn("Spawn_C", root.transform,
                new Vector3(0f, 0f, -0.7f), ExperimentArea.AreaC);

            // Taller again. The results block grew when the Session Summary statistics were
            // restored — eleven lines rather than three — and at the previous height its text
            // overflowed its 420 px rect and reached down into NEW TRIAL. The panel is made
            // taller and the button group is moved down; the TEXT IS NOT SHRUNK, because this
            // screen has to stay readable for an older participant.
            //
            // The results rect is generous on purpose: Spanish runs longer than English and
            // Japanese sets taller lines, so the block must fit its worst case, not its best.
            //
            // The results rect is sized for the WORST language, not for English. Measured with
            // the real summary text: English needs ~800 px and Japanese ~910 — its taller line
            // boxes are why an 840 px rect was still not enough — so the block gets 980.
            //
            // Vertical budget, panel half-height 1150 px:
            //   +1060 .. +890   instruction
            //   +870 ..  +790   recall status
            //   +770 ..  -210   results  (980 px — the Session Summary block)
            //            -250          divider
            //            -210 .. -285  >>> 75 px clear gap <<<
            //   -285 ..  -415   NEW TRIAL
            //   -445 ..  -575   RESTART
            //   -605 ..  -735   END
            //            -800          divider
            //   -855 ..  -945   recenter (small, visually separate)
            //            -1030         warning
            //
            // 2300 px x 0.00095 = 2.185 m tall, centred at 1.55 m, so it spans 0.46..2.64 m —
            // clear of the 3.0 m ceiling and still readable from the spawn point 2.1 m away.
            var canvas = CreateWorldCanvas("UI_C_Canvas", root.transform,
                new Vector3(0f, 1.55f, 1.4f), new Vector2(1600f, 2300f), mats,
                spawn.transform.localPosition.z, 0.00095f);

            ui = new AreaCUi
            {
                panel = canvas.gameObject,
                instruction = CreateText("Txt_Instruction", canvas.transform, new Vector2(0f, 975f),
                    new Vector2(1520f, 170f), string.Empty, 56f,
                    TextAlignmentOptions.Center, Color.white, autoSizeMin: 36f),
                status = CreateText("Txt_RecallStatus", canvas.transform, new Vector2(0f, 830f),
                    new Vector2(1520f, 80f), string.Empty, 46f,
                    TextAlignmentOptions.Center, new Color(0.75f, 0.85f, 0.75f)),

                // THE DELAYED RECOGNITION STIMULUS.
                //
                // THE BUG THIS FIXES: the delayed word was written only to the Area A label.
                // Each area owns its own canvas in its own room and ShowArea deactivates all
                // but the current one, so during Area C that label is a disabled object 25 m
                // behind the participant. The instruction, the narration and both response
                // buttons all appeared; the word itself was set on something invisible.
                //
                // y = +165 is not an arbitrary slot. It reproduces the Area A word's WORLD
                // height: Area A's canvas is centred at 1.65 m with the word at +60 px, which
                // at 0.00095 m/px is 1.707 m; this canvas is centred at 1.55 m, so
                // (1.707 - 1.55) / 0.00095 = 165. Both canvases sit 2.1 m from their spawn
                // point at the same scale with the same 130 pt type, so the immediate and
                // delayed stimuli subtend the SAME visual angle at the SAME eye height. Two
                // phases of one recognition memory measure should not differ in how hard their
                // words are to see.
                //
                // It shares the vertical band the results block occupies, and that is safe:
                // RunRecognitionPhase clears the word before Area C ever writes a summary, so
                // the two are never on screen together. It clears the response pair (0.95 m)
                // by 0.76 m, exactly as in Area A.
                word = CreateText("Txt_WordDisplay_C", canvas.transform, new Vector2(0f, 165f),
                    new Vector2(1520f, 200f), string.Empty, 130f,
                    TextAlignmentOptions.Center, new Color(1f, 0.93f, 0.5f)),

                // The Area C twins of the two Area A overlays. Their y values are DERIVED from
                // the Area A ones so both areas present the same thing at the same world height,
                // exactly as the stimulus word does:
                //
                //   counter    Area A +170 -> world 1.65 + 170*0.00095 = 1.8115 m
                //                            -> Area C (1.8115 - 1.55) / 0.00095 = +275
                //   cheatsheet Area A -120 -> world 1.65 - 120*0.00095 = 1.5360 m
                //                            -> Area C (1.5360 - 1.55) / 0.00095 = -15
                //
                // The counter keeps the same +110 px offset above the stimulus word that it has
                // in Area A (275 - 165 = 170 - 60), so the two phases look identical.
                //
                // Both share the vertical band of the results block, which is safe for the same
                // reason the word is: they are hidden before Area C ever writes a summary.
                recognitionCounter = CreateText("Txt_RecognitionCounter_C", canvas.transform,
                    new Vector2(0f, 275f), new Vector2(700f, 55f), string.Empty, 34f,
                    TextAlignmentOptions.Center, new Color(0.62f, 0.70f, 0.80f)),

                developerCheatsheet = CreateText("Txt_DevCheatsheet_C", canvas.transform,
                    new Vector2(0f, -15f), new Vector2(900f, 160f), string.Empty, 34f,
                    TextAlignmentOptions.Center, new Color(1f, 0.35f, 0.85f)),

                results = CreateText("Txt_Results", canvas.transform, new Vector2(0f, 280f),
                    new Vector2(1520f, 980f), string.Empty, 52f,
                    TextAlignmentOptions.Center, new Color(0.95f, 0.95f, 0.95f), autoSizeMin: 34f),
                warning = CreateText("Txt_Warning", canvas.transform, new Vector2(0f, -1030f),
                    new Vector2(1520f, 80f), string.Empty, 32f,
                    TextAlignmentOptions.Center, new Color(1f, 0.82f, 0.5f)),
                // THREE distinct actions, each labelled with what it DOES rather than only what
                // it is called. They are stacked rather than side by side so each gets a full
                // line for its explanation, and no two can be confused at a glance.
                newTrialButton = CreateButton("Btn_NewTrial", canvas.transform,
                    new Vector2(0f, -350f), new Vector2(1180f, 130f),
                    "NEW TRIAL\n<size=55%>Save this run and begin another trial</size>",
                    new Color(0.13f, 0.50f, 0.30f), 46f),
                restartButton = CreateButton("Btn_Restart", canvas.transform,
                    new Vector2(0f, -510f), new Vector2(1180f, 130f),
                    "RESTART\n<size=55%>Discard this run and start over from the tutorial</size>",
                    new Color(0.45f, 0.35f, 0.12f), 46f),
                endButton = CreateButton("Btn_End", canvas.transform,
                    new Vector2(0f, -670f), new Vector2(1180f, 130f),
                    "END\n<size=55%>Save and finish</size>",
                    new Color(0.45f, 0.20f, 0.18f), 46f),

                // Recenter is a utility, not a run-management choice. It sits below a divider,
                // smaller and muted, so it cannot be mistaken for — or overlap — the three
                // decisions above it.
                recenterButton = CreateButton("Btn_Recenter_C", canvas.transform,
                    new Vector2(0f, -900f), new Vector2(360f, 90f), "RECENTER",
                    new Color(0.28f, 0.28f, 0.34f), 36f),
            };

            // A rule between the summary and the decisions, so the two read as separate sections
            // rather than one column of text that happens to end in buttons.
            CreatePanelFrame("Divider_Summary", canvas.transform, new Vector2(0f, -250f),
                new Vector2(1300f, 4f), new Color(0.40f, 0.44f, 0.52f, 0.65f));

            CreatePanelFrame("Divider_RunActions", canvas.transform, new Vector2(0f, -800f),
                new Vector2(1200f, 4f), new Color(0.40f, 0.44f, 0.52f, 0.8f));

            ui.restartButton.gameObject.SetActive(false);
            ui.newTrialButton.gameObject.SetActive(false);
            ui.endButton.gameObject.SetActive(false);

            // Same rule as Area A: inactive until the manager decides otherwise.
            ui.recognitionCounter.gameObject.SetActive(false);
            ui.developerCheatsheet.gameObject.SetActive(false);

            return spawn;
        }

        // =================================================================================
        // Wiring
        // =================================================================================

        static void WireSystems(Transform systemsRoot, ExperimentConfig config,
            ExperimentAssetBuilder.MaterialSet mats, XROrigin xrOrigin,
            SpawnPoint spawn0, SpawnPoint spawnA, SpawnPoint spawnB, SpawnPoint spawnC,
            List<ChairTarget> chairs, List<ChairSlot> chairSlots,
            List<PracticeObject> practiceObjects,
            Area0Ui ui0, AreaAUi uiA, AreaBUi uiB, AreaCUi uiC,
            RecognitionResponsePanel recognitionPanel)
        {
            var expRoot = new GameObject("ExperimentSystems");
            expRoot.transform.SetParent(systemsRoot);

            // ---- Logger + sinks (all on ONE GameObject so auto-registration works) --------
            var loggerGo = new GameObject("EventLogger");
            loggerGo.transform.SetParent(expRoot.transform);
            var logger = loggerGo.AddComponent<EventLogger>();
            loggerGo.AddComponent<CsvEventSink>();
            loggerGo.AddComponent<UnityConsoleEventSink>();

            // Real LSL marker outlet. It binds to liblsl at run time if the library is present
            // and reports LSL_UNAVAILABLE if it is not — either way the session runs.
            var lslSink = loggerGo.AddComponent<LslMarkerSink>();
            lslSink.Configure(true, "IKEA_EEG_Markers", "Markers", "IKEA_EEG_Unity_Markers");

            // ---- Audio --------------------------------------------------------------------
            var audioGo = new GameObject("ExperimentAudio");
            audioGo.transform.SetParent(expRoot.transform);
            var audio = audioGo.AddComponent<ExperimentAudio>();

            // ---- Raw EEG (optional; dormant without an amplifier) --------------------
            // ONE receiver for the whole project. Nothing else creates an AURA inlet.
            var eegGo = new GameObject("EegAcquisition");
            eegGo.transform.SetParent(expRoot.transform);
            var eegReceiver = eegGo.AddComponent<AuraLslReceiver>();
            eegReceiver.Configure(resolveTimeoutSeconds: 3f, receiveContinuously: true);
            eegReceiver.ConfigureBuffer(bufferSamples: true, bufferSeconds: 60f);
            var eegRecorder = eegGo.AddComponent<EegRunRecorder>();
            eegRecorder.Configure(connectOnStart: true, recordToDisk: true, fileName: "raw_eeg.csv");

            // Live preprocessing + spectral features, on the SAME GameObject so it observes the
            // one receiver above. It opens no inlet of its own and never touches the raw buffer:
            // it subscribes to the receiver's sample event and keeps its filtered copy separately.
            //
            // Without this component the runtime produces no features at all and the Researcher
            // EEG Monitor reports "no EEG pipeline in the open scene", because it deliberately
            // creates nothing and only displays what already exists.
            //
            // Montage is left null: EegFeaturePipeline falls back to
            // AuraMontageConfig.CreateHumanVerifiedDefault(), which carries the human-verified
            // mapping. Assigning a wrong asset here would silently change what an ROI means.
            var eegPipeline = eegGo.AddComponent<EegFeaturePipeline>();
            eegPipeline.Configure(montage: null, highPassHz: 1.0, lowPassHz: 40.0,
                windowSeconds: 4.0);

            // ---- Voice --------------------------------------------------------------------
            var voiceGo = new GameObject("VoiceRecallManager");
            voiceGo.transform.SetParent(expRoot.transform);
            var voice = voiceGo.AddComponent<VoiceRecallManager>();
            voiceGo.AddComponent<NullTranscriptionProvider>();

            // ---- Chair task ---------------------------------------------------------------
            var chairTaskGo = new GameObject("ChairSelectionTask");
            chairTaskGo.transform.SetParent(expRoot.transform);
            var chairTask = chairTaskGo.AddComponent<ChairSelectionTask>();
            chairTask.SetChairs(chairs);
            chairTask.SetSlots(chairSlots);
            chairTask.SetTarget(config.targetChair);

            // One material per ChairColor, in enum order, so a generated trial can dress any
            // chair in any colour without the chair holding a palette of its own.
            var colorMaterials = new List<Material>();
            foreach (ChairColor color in System.Enum.GetValues(typeof(ChairColor)))
                colorMaterials.Add(mats.chairColors[color]);

            chairTask.SetColorMaterials(colorMaterials);

            if (!chairTask.ValidateTargetIsUnique(out var matchCount))
            {
                Debug.LogError($"[IKEA_EEG] The authored layout's target chair {config.targetChair} " +
                               $"matches {matchCount} chairs (expected exactly 1). This affects " +
                               "the legacy fixed-target mode only; generated trials guarantee " +
                               "uniqueness themselves.");
            }

            // ---- Teleporter ----------------------------------------------------------------
            var teleporterGo = new GameObject("XRRigTeleporter");
            teleporterGo.transform.SetParent(expRoot.transform);
            var teleporter = teleporterGo.AddComponent<XRRigTeleporter>();
            teleporter.SetXROrigin(xrOrigin);
            teleporter.SetSpawnPoints(spawnA, spawnB, spawnC, spawn0);

            // ---- UI ---------------------------------------------------------------------
            var uiGo = new GameObject("ExperimentUIController");
            uiGo.transform.SetParent(expRoot.transform);
            var ui = uiGo.AddComponent<ExperimentUIController>();

            ui.BindLanguagePanel(ui0.languagePanel, ui0.languageTitle, ui0.languageEnglishButton,
                ui0.languageSpanishButton, ui0.languageJapaneseButton);
            ui.BindFamiliarization(ui0.panel, ui0.instruction, ui0.status,
                ui0.startExperimentButton, ui0.skipIntroButton,
                ui0.researcherStatusPanel, ui0.researcherStatusText,
                ui0.practicePrompt, ui0.readyLabel, ui0.startHighlight,
                ui0.replayInstructionsButton);
            ui.BindShapeLegend(uiB.shapeLegend);
            ui.BindAreaA(uiA.panel, uiA.title, uiA.instruction, uiA.word, uiA.status,
                uiA.startButton, uiA.enterButton, uiA.recognitionCounter,
                uiA.developerCheatsheet);
            ui.BindAreaB(uiB.instructionPanel, uiB.instruction, uiB.statusPanel, uiB.status,
                uiB.feedback, uiB.exitButton, uiB.readyButton, uiB.instructionOverlay,
                uiB.overlayText);
            ui.BindAreaC(uiC.panel, uiC.instruction, uiC.status, uiC.results,
                uiC.restartButton, uiC.endButton, uiC.newTrialButton, uiC.word,
                uiC.recognitionCounter, uiC.developerCheatsheet);
            ui.BindShared(uiA.recenterButton, uiB.recenterButton, uiC.recenterButton,
                uiA.warning, uiB.warning, uiC.warning, uiA.recheckAudioButton,
                ui0.recenterButton, ui0.warning);

            // ---- Localized static captions -------------------------------------------------
            // Every participant-facing button caption is bound to a key here, so the whole UI
            // switches language in one assignment and no caption can be left in the previous
            // language. The three language buttons are deliberately NOT bound: each always
            // shows its own language's name.
            LocalizeButton(ui0.startExperimentButton, LocKeys.StartExperiment);
            LocalizeButton(ui0.skipIntroButton, LocKeys.SkipIntro);
            LocalizeButton(ui0.replayInstructionsButton, LocKeys.ReplayInstructions);
            LocalizeButton(ui0.recenterButton, LocKeys.Recenter);
            LocalizeText(ui0.readyLabel, LocKeys.PracticeReady);

            LocalizeButton(uiA.recheckAudioButton, LocKeys.RecheckAudio);
            LocalizeButton(uiA.startButton, LocKeys.Start);
            LocalizeButton(uiA.enterButton, LocKeys.EnterShowroom);
            LocalizeButton(uiA.recenterButton, LocKeys.Recenter);
            LocalizeText(uiA.title, LocKeys.AreaATitle);

            LocalizeButton(uiB.readyButton, LocKeys.Ready);
            LocalizeButton(uiB.exitButton, LocKeys.ExitShowroom);
            LocalizeButton(uiB.recenterButton, LocKeys.Recenter);

            LocalizeButton(uiC.newTrialButton, LocKeys.NewTrial, LocKeys.NewTrialSubtitle);
            LocalizeButton(uiC.restartButton, LocKeys.Restart, LocKeys.RestartSubtitle);
            LocalizeButton(uiC.endButton, LocKeys.End, LocKeys.EndSubtitle);
            LocalizeButton(uiC.recenterButton, LocKeys.Recenter);

            // ---- Developer navigation (NOT participant functionality) ----------------------
            var devNavGo = new GameObject("DeveloperNavigation");
            devNavGo.transform.SetParent(expRoot.transform);
            var devNav = devNavGo.AddComponent<DeveloperNavigation>();

            var devPanel = BuildDeveloperPanel(devNavGo.transform, mats, ui);
            devNav.SetPanel(devPanel);

            // ---- Manager -------------------------------------------------------------------
            var managerGo = new GameObject("ExperimentManager");
            managerGo.transform.SetParent(expRoot.transform);
            var manager = managerGo.AddComponent<ExperimentManager>();
            manager.Bind(config, logger, ui, teleporter, chairTask, voice, audio, lslSink,
                practiceObjects, devNav);

            // Assigned explicitly rather than left to the runtime FindAnyObjectByType fallback,
            // so the reference is visible in the Inspector and survives as serialized data.
            manager.SetRecognitionPanel(recognitionPanel);

            // Serialized private fields set from code need an explicit dirty flag to survive
            // the scene save.
            foreach (var o in new UnityEngine.Object[]
                     { logger, audio, voice, chairTask, teleporter, ui, manager, lslSink, devNav,
                       eegReceiver, eegRecorder, eegPipeline })
            {
                EditorUtility.SetDirty(o);
            }
        }

        /// <summary>
        /// The developer navigation panel. Inactive from the moment it is built and shown only
        /// by the deliberate two-thumbstick gesture, so a participant can never encounter it.
        ///
        /// It is a free-standing world-space canvas that DeveloperNavigation repositions in
        /// front of the camera when opened. Nothing is parented to the XR rig.
        /// </summary>
        static GameObject BuildDeveloperPanel(Transform parent,
            ExperimentAssetBuilder.MaterialSet mats, ExperimentUIController ui)
        {
            // viewerZ is irrelevant here — the panel is oriented at runtime when it opens.
            var canvas = CreateWorldCanvas("UI_DeveloperNavigation", parent,
                Vector3.zero, new Vector2(1100f, 1250f), mats, 0f, 0.0010f);

            CreateText("Txt_DevTitle", canvas.transform, new Vector2(0f, 520f),
                new Vector2(1040f, 150f),
                "DEVELOPER NAVIGATION\n<size=50%>Not for participant use</size>", 60f,
                TextAlignmentOptions.Center, new Color(1f, 0.6f, 0.4f));

            CreateText("Txt_DevWarning", canvas.transform, new Vector2(0f, 370f),
                new Vector2(1040f, 140f),
                "Jumping areas marks this run\nDEVELOPER_INTERRUPTED.", 38f,
                TextAlignmentOptions.Center, new Color(1f, 0.85f, 0.5f), autoSizeMin: 26f);

            var area0 = CreateButton("Btn_Dev_Area0", canvas.transform, new Vector2(0f, 220f),
                new Vector2(900f, 110f), "GO TO — AREA 0 (Familiarization)",
                new Color(0.22f, 0.30f, 0.42f), 40f);
            var areaA = CreateButton("Btn_Dev_AreaA", canvas.transform, new Vector2(0f, 90f),
                new Vector2(900f, 110f), "GO TO — AREA A (Memory)",
                new Color(0.22f, 0.30f, 0.42f), 40f);
            var areaB = CreateButton("Btn_Dev_AreaB", canvas.transform, new Vector2(0f, -40f),
                new Vector2(900f, 110f), "GO TO — AREA B (Chairs)",
                new Color(0.22f, 0.30f, 0.42f), 40f);
            var areaC = CreateButton("Btn_Dev_AreaC", canvas.transform, new Vector2(0f, -170f),
                new Vector2(900f, 110f), "GO TO — AREA C (Delayed recall)",
                new Color(0.22f, 0.30f, 0.42f), 40f);

            var close = CreateButton("Btn_Dev_Close", canvas.transform, new Vector2(0f, -350f),
                new Vector2(900f, 110f), "CLOSE MENU", new Color(0.28f, 0.30f, 0.36f), 40f);

            var abort = CreateButton("Btn_Dev_Abort", canvas.transform, new Vector2(0f, -490f),
                new Vector2(900f, 110f), "ABORT SESSION", new Color(0.55f, 0.20f, 0.18f), 40f);

            // Researcher-only: return to the language screen for localization testing. It is
            // on the DEVELOPER panel, so a participant can never reach it.
            var language = CreateButton("Btn_Dev_Language", canvas.transform,
                new Vector2(0f, -620f), new Vector2(900f, 100f),
                "RETURN TO LANGUAGE SELECTION", new Color(0.30f, 0.26f, 0.44f), 36f);

            ui.BindDeveloperPanel(area0, areaA, areaB, areaC, close, abort, language);

            canvas.gameObject.SetActive(false);
            return canvas.gameObject;
        }

        // =================================================================================
        // Geometry helpers
        // =================================================================================

        /// <summary>
        /// The SEEN BEFORE / NOT SEEN BEFORE response pair for the Recognition protocol.
        ///
        /// WORLD-SPACE BOXES, not canvas buttons, and deliberately so: they reuse
        /// <see cref="XRSimpleInteractable"/> and the far-ray select that the chairs and the
        /// practice objects already use and that is verified on hardware. No new interaction
        /// package, no new input action, and no change to the XR rig.
        ///
        /// SIZE AND PLACEMENT ARE PROTOCOL, NOT DECORATION. The targets are 0.62 m wide and
        /// 0.26 m tall, side by side low in the visual field, so that answering costs a coarse
        /// point rather than careful aiming — reaction time is being measured, and time spent
        /// aiming would be recorded as time spent deciding. They sit below the word so reading
        /// the stimulus and reaching the answer do not compete for the same screen space.
        ///
        /// Built ONCE and relocated between Area A and Area C by the panel itself; two panels
        /// would let the manager bind to the wrong one.
        /// </summary>
        static RecognitionResponsePanel BuildRecognitionPanel(Transform parent,
            ExperimentAssetBuilder.MaterialSet mats, Transform areaAAnchor, Transform areaCAnchor)
        {
            var root = new GameObject("RecognitionResponsePanel");
            root.transform.SetParent(parent, false);

            var panel = root.AddComponent<RecognitionResponsePanel>();

            // THE VISUALS ARE A CHILD, and only the child is hidden. The root carrying the
            // component MUST stay active: FindAnyObjectByType — which is how ExperimentManager
            // binds the panel — does not return components on inactive GameObjects. Hiding the
            // root would leave the manager unable to find the panel at all, so the participant
            // would answer buttons nobody was listening to. Hiding a child costs nothing and
            // keeps the component discoverable.
            var visuals = new GameObject("Visuals");
            visuals.transform.SetParent(root.transform, false);

            // 0.80 m apart (was 0.72): each face is 0.62 m wide, so this leaves an 18 cm gap
            // between them — wide enough that a coarse point cannot land ambiguously between
            // the two answers.
            var seen = BuildRecognitionButton("Btn_SeenBefore", visuals.transform,
                new Vector3(-0.40f, 0f, 0f), RecognitionResponse.SeenBefore,
                LocKeys.RecognitionSeenBefore, mats);

            var notSeen = BuildRecognitionButton("Btn_NotSeenBefore", visuals.transform,
                new Vector3(0.40f, 0f, 0f), RecognitionResponse.NotSeenBefore,
                LocKeys.RecognitionNotSeenBefore, mats);

            panel.Configure(seen, notSeen, visuals, areaAAnchor, areaCAnchor);

            // Hidden until a recognition phase asks for it — the CHILD, never the root.
            visuals.SetActive(false);

            EditorUtility.SetDirty(panel);
            return panel;
        }

        static RecognitionResponseButton BuildRecognitionButton(string id, Transform parent,
            Vector3 localPosition, RecognitionResponse response, string locKey,
            ExperimentAssetBuilder.MaterialSet mats)
        {
            // AN UNSCALED HOLDER SITS BETWEEN THE BUTTON AND ITS LABEL.
            //
            // THE BUG THIS FIXES: the label canvas used to be a child of the box itself, and the
            // box is a primitive cube scaled to (0.62, 0.26, 0.06). A child inherits that
            // non-uniform scale, so the canvas's own metres-per-pixel scale was multiplied by
            // 0.62 across, 0.26 down and 0.06 deep — squashing the glyphs — and its -0.05 m
            // forward offset became -0.05 x 0.06 = -3 mm, which is INSIDE a box 60 mm deep.
            // The text was rendered, correct, localized, and buried in the geometry, which is
            // why the buttons appeared as blank blue slabs on the headset.
            //
            // The holder stays at scale 1, so the label is positioned and sized in real metres
            // and the box's dimensions can change without moving the text.
            var holder = new GameObject(id);
            holder.transform.SetParent(parent, false);
            holder.transform.localPosition = localPosition;

            var go = CreateBox($"{id}_Surface", holder.transform, Vector3.zero,
                new Vector3(0.62f, 0.26f, 0.06f), mats.practiceNeutral, markStatic: false);

            // 4 cm in FRONT of the box face (the participant is on the -Z side), in unscaled
            // metres, so the label can never be swallowed by the surface behind it.
            // viewerZ MUST be behind the label, not at 0.
            //
            // THE BUG THIS FIXES: CreateWorldCanvas orients the canvas by
            // (localPosition - viewer), and a world-space Canvas is readable from its local -Z
            // side, so its forward must point AWAY from the participant. Passing viewerZ = 0
            // with a label at z = -0.04 gave (0,0,-0.04): forward pointed -Z, i.e. straight at
            // the participant — the opposite of every other panel in the project — so the text
            // rendered back-to-front and read as inverted on the headset.
            //
            // -1 m simply places the notional viewer in front of the label, which is where the
            // participant actually is; the magnitude does not matter, only the sign of the
            // resulting vector.
            var labelCanvas = CreateWorldCanvas($"UI_{id}", holder.transform,
                new Vector3(0f, 0f, -0.04f), new Vector2(700f, 260f), mats, -1f, 0.0008f);

            var label = CreateText($"Txt_{id}", labelCanvas.transform, Vector2.zero,
                new Vector2(660f, 220f), string.Empty, 78f,
                TextAlignmentOptions.Center, Color.white);

            // Auto-sizing down to 40 pt so a longer localized label ("NOT SEEN BEFORE", and any
            // future translation) shrinks to fit instead of overflowing the 0.62 m face.
            label.enableAutoSizing = true;
            label.fontSizeMin = 40f;
            label.fontSizeMax = 78f;

            LocalizeText(label, locKey);

            var interactable = go.AddComponent<XRSimpleInteractable>();
            interactable.colliders.Clear();
            interactable.colliders.AddRange(go.GetComponentsInChildren<Collider>(true));

            var button = go.AddComponent<RecognitionResponseButton>();
            button.Configure(response, interactable, go.GetComponent<Renderer>());

            EditorUtility.SetDirty(button);
            EditorUtility.SetDirty(interactable);
            return button;
        }

        static GameObject CreateBox(string name, Transform parent, Vector3 localPosition,
            Vector3 size, Material material, bool markStatic = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = size;

            // Primitives ship with a BoxCollider — exactly the "real collider" the walls,
            // floors and furniture need. It is left in place deliberately.
            ApplyMaterial(go, material);

            // Chair parts must NOT be marked static: static batching merges their meshes, and
            // a merged renderer does not reliably show the per-chair material swap used for
            // selection feedback. Only the fixed environment is batched.
            if (markStatic)
            {
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic |
                                                           StaticEditorFlags.OccluderStatic |
                                                           StaticEditorFlags.OccludeeStatic);
            }

            return go;
        }

        static GameObject CreateCylinder(string name, Transform parent, Vector3 localPosition,
            Vector3 size, Material material, Vector3 euler = default)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(euler);

            // Unity's cylinder is 2 units tall, so the Y scale is half the desired height.
            go.transform.localScale = new Vector3(size.x, size.y * 0.5f, size.z);

            ApplyMaterial(go, material);
            return go;
        }

        static void ApplyMaterial(GameObject go, Material material)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && material != null)
                renderer.sharedMaterial = material;
        }

        static SpawnPoint CreateSpawn(string name, Transform parent, Vector3 localPosition,
            ExperimentArea area)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.identity;   // faces +Z, toward the UI

            var spawn = go.AddComponent<SpawnPoint>();
            spawn.SetArea(area);
            return spawn;
        }

        /// <summary>Store facade with a doorway opening: two side panels, a lintel, and a frame.</summary>
        static void BuildFacadeWithDoorway(string suffix, Transform parent, Vector3 localPosition,
            float width, ExperimentAssetBuilder.MaterialSet mats)
        {
            var root = new GameObject($"StoreFacade_{suffix}");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPosition;

            const float doorWidth = 2.6f;
            const float doorHeight = 2.6f;
            const float height = 4.2f;

            var sidePanelWidth = (width - doorWidth) * 0.5f;

            CreateBox($"Facade_{suffix}_Left", root.transform,
                new Vector3(-(doorWidth + sidePanelWidth) * 0.5f, height * 0.5f, 0f),
                new Vector3(sidePanelWidth, height, k_WallThickness), mats.facade);

            CreateBox($"Facade_{suffix}_Right", root.transform,
                new Vector3((doorWidth + sidePanelWidth) * 0.5f, height * 0.5f, 0f),
                new Vector3(sidePanelWidth, height, k_WallThickness), mats.facade);

            CreateBox($"Facade_{suffix}_Lintel", root.transform,
                new Vector3(0f, doorHeight + (height - doorHeight) * 0.5f, 0f),
                new Vector3(doorWidth, height - doorHeight, k_WallThickness), mats.facade);

            // Doorway frame — purely visual: the opening is never walkable in the protocol,
            // area changes happen through the UI button.
            var frame = new GameObject($"Doorway_Frame_{suffix}");
            frame.transform.SetParent(root.transform, false);

            CreateBox("Frame_Left", frame.transform,
                new Vector3(-doorWidth * 0.5f, doorHeight * 0.5f, 0f),
                new Vector3(0.18f, doorHeight, 0.35f), mats.doorFrame);
            CreateBox("Frame_Right", frame.transform,
                new Vector3(doorWidth * 0.5f, doorHeight * 0.5f, 0f),
                new Vector3(0.18f, doorHeight, 0.35f), mats.doorFrame);
            CreateBox("Frame_Top", frame.transform,
                new Vector3(0f, doorHeight, 0f),
                new Vector3(doorWidth + 0.18f, 0.18f, 0.35f), mats.doorFrame);

            // A blocker fills the opening so the doorway is visually an opening but physically
            // solid: nobody can walk between the visual concepts of entrance and showroom.
            CreateBox($"Facade_{suffix}_DoorBlocker", root.transform,
                new Vector3(0f, doorHeight * 0.5f, 0f),
                new Vector3(doorWidth, doorHeight, 0.08f), mats.accent);
        }

        // =================================================================================
        // Chair construction
        // =================================================================================

        /// <summary>
        /// Builds one chair with ALL THREE silhouettes pre-made underneath it.
        ///
        /// A trial changes a chair's shape by activating one of these bodies — it never creates
        /// or destroys geometry. That matters because the XRSimpleInteractable and its collider
        /// list are registered with the XRInteractionManager once, at scene load, and must stay
        /// registered for the whole session: re-registering interactables mid-session is what
        /// previously left chairs pointable-at but unselectable.
        ///
        /// The interactable's collider list is therefore filled EXPLICITLY with the colliders of
        /// all three bodies, including the inactive ones. Unity's automatic collection (which
        /// runs in Awake only when the list is empty) would see just the active body and the
        /// chair would stop being selectable the moment its shape changed. Physics ignores
        /// colliders on inactive objects, so only the visible body can ever be hit.
        /// </summary>
        static ChairTarget BuildChair(string id, ChairSpec initialSpec, Transform parent,
            ChairSlot slot, ExperimentAssetBuilder.MaterialSet mats)
        {
            var root = new GameObject(id);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(slot.transform.position, slot.transform.rotation);

            var material = mats.chairColors[initialSpec.color];
            var variants = new List<ChairTarget.ShapeVariant>();

            foreach (ChairShape shape in System.Enum.GetValues(typeof(ChairShape)))
            {
                var variantRoot = new GameObject($"Body_{shape}");
                variantRoot.transform.SetParent(root.transform, false);
                variantRoot.transform.localPosition = Vector3.zero;
                variantRoot.transform.localRotation = Quaternion.identity;

                switch (shape)
                {
                    case ChairShape.Solid:
                        BuildSolidChair(variantRoot.transform, material);
                        break;
                    case ChairShape.Slatted:
                        BuildSlattedChair(variantRoot.transform, material);
                        break;
                    case ChairShape.Curved:
                        BuildCurvedChair(variantRoot.transform, material);
                        break;
                }

                variants.Add(new ChairTarget.ShapeVariant
                {
                    shape = shape,
                    root = variantRoot,
                    renderers = new List<Renderer>(variantRoot.GetComponentsInChildren<Renderer>(true)),
                });
            }

            // XRSimpleInteractable, NOT XRGrabInteractable: the chair can be pointed at and
            // selected but never picked up or moved. No Rigidbody is added, so the colliders
            // stay static.
            var interactable = root.AddComponent<XRSimpleInteractable>();
            interactable.colliders.Clear();
            interactable.colliders.AddRange(root.GetComponentsInChildren<Collider>(true));

            var chair = root.AddComponent<ChairTarget>();
            chair.Configure(id, initialSpec, material, mats.chairHover, mats.chairCorrect,
                mats.chairIncorrect);
            chair.SetShapeVariants(variants);
            chair.SetInteractable(interactable);

            // Leaves exactly one body active and sets the size scale and colour.
            chair.ApplySpec(initialSpec, material);

            EditorUtility.SetDirty(chair);
            EditorUtility.SetDirty(interactable);
            return chair;
        }

        // Chair parts pass markStatic:false — see CreateBox for why static batching would
        // break the selection-feedback material swap.
        //
        // THE THREE BODIES AND THEIR LABELS. The geometry below is unchanged; only the names
        // changed, because each body already differs from the other two in one directly
        // visible way — the structure of its backrest:
        //
        //   SOLID   one continuous flat back panel, no gaps, no curvature
        //   SLATTED two posts and two horizontal rails, with visible gaps between them
        //   CURVED  cylinders throughout, so the back is a curved surface
        //
        // A participant can classify a chair by looking at its back: round -> CURVED, gaps ->
        // SLATTED, one unbroken flat panel -> SOLID. No aesthetic judgement is required, which
        // is exactly what "Modern" and "Classic" did require.

        /// <summary>SOLID: the backrest is a single continuous flat panel.</summary>
        static void BuildSolidChair(Transform parent, Material material)
        {
            CreateBox("Seat", parent, new Vector3(0f, 0.44f, 0f), new Vector3(0.46f, 0.06f, 0.46f), material, false);
            CreateBox("Back", parent, new Vector3(0f, 0.69f, -0.20f), new Vector3(0.44f, 0.46f, 0.05f), material, false);
            CreateLegs(parent, material, 0.19f, new Vector3(0.045f, 0.44f, 0.045f), 0.22f);
        }

        /// <summary>SLATTED: the backrest is separate horizontal rails with gaps between them.</summary>
        static void BuildSlattedChair(Transform parent, Material material)
        {
            CreateBox("Seat", parent, new Vector3(0f, 0.45f, 0f), new Vector3(0.50f, 0.08f, 0.50f), material, false);
            CreateBox("Back_Post_L", parent, new Vector3(-0.20f, 0.74f, -0.21f), new Vector3(0.06f, 0.50f, 0.06f), material, false);
            CreateBox("Back_Post_R", parent, new Vector3(0.20f, 0.74f, -0.21f), new Vector3(0.06f, 0.50f, 0.06f), material, false);
            // THREE rails, evenly spaced, with gaps wider than the rails themselves. Two rails
            // read as "a back with a line across it" at 2-3 m; three read unambiguously as
            // slats. Geometry only — collider and interactable semantics are untouched, and the
            // interactable's collider list is rebuilt with the scene.
            CreateBox("Back_Rail_Top", parent, new Vector3(0f, 0.96f, -0.21f), new Vector3(0.46f, 0.07f, 0.06f), material, false);
            CreateBox("Back_Rail_Mid", parent, new Vector3(0f, 0.80f, -0.21f), new Vector3(0.44f, 0.07f, 0.06f), material, false);
            CreateBox("Back_Rail_Low", parent, new Vector3(0f, 0.64f, -0.21f), new Vector3(0.42f, 0.07f, 0.06f), material, false);
            CreateLegs(parent, material, 0.20f, new Vector3(0.06f, 0.45f, 0.06f), 0.225f);
        }

        /// <summary>CURVED: every part is cylindrical, so the back is a curved surface.</summary>
        static void BuildCurvedChair(Transform parent, Material material)
        {
            CreateCylinder("Seat", parent, new Vector3(0f, 0.44f, 0f),
                new Vector3(0.54f, 0.08f, 0.54f), material);
            // Depth 0.10 -> 0.22: at 0.10 the cylinder was flattened almost to a slab and read
            // like the SOLID panel head-on. A deeper section shows its curvature from the front.
            CreateCylinder("Back", parent, new Vector3(0f, 0.70f, -0.22f),
                new Vector3(0.46f, 0.48f, 0.22f), material);

            var offsets = new[]
            {
                new Vector3(-0.17f, 0.22f, -0.17f),
                new Vector3(0.17f, 0.22f, -0.17f),
                new Vector3(-0.17f, 0.22f, 0.17f),
                new Vector3(0.17f, 0.22f, 0.17f),
            };

            for (var i = 0; i < offsets.Length; i++)
            {
                CreateCylinder($"Leg_{i + 1}", parent, offsets[i],
                    new Vector3(0.05f, 0.44f, 0.05f), material);
            }
        }

        static void CreateLegs(Transform parent, Material material, float offset,
            Vector3 size, float height)
        {
            var offsets = new[]
            {
                new Vector3(-offset, height, -offset),
                new Vector3(offset, height, -offset),
                new Vector3(-offset, height, offset),
                new Vector3(offset, height, offset),
            };

            for (var i = 0; i < offsets.Length; i++)
                CreateBox($"Leg_{i + 1}", parent, offsets[i], size, material, false);
        }

        // =================================================================================
        // UI helpers
        // =================================================================================

        /// <param name="viewerZ">
        /// Local Z of the spawn point the panel is meant to be read from. Used only to orient
        /// the canvas so its readable face points at the participant.
        /// </param>
        /// <param name="metresPerPixel">
        /// World scale of the canvas. 1500 px at 0.0011 is ~1.65 m wide; Area B uses 0.0020
        /// so its panels read clearly from ~8 m.
        /// </param>
        static Canvas CreateWorldCanvas(string name, Transform parent, Vector3 localPosition,
            Vector2 sizePx, ExperimentAssetBuilder.MaterialSet mats, float viewerZ,
            float metresPerPixel = 0.0011f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.localPosition = localPosition;

            // ORIENTATION — this was previously wrong and rendered every panel mirrored.
            //
            // A world-space Canvas is readable from its local -Z side: the stock Unity setup
            // (camera at -Z looking toward +Z, canvas at the origin with identity rotation)
            // reads correctly, and there the viewer sits on the canvas's -Z side. So the
            // canvas's local FORWARD must point AWAY from the viewer, not at them.
            //
            // Rotating 180° to "face" the participant does the exact opposite and flips the
            // text. All spawns look toward +Z and all panels are placed at +Z of their spawn,
            // so forward must stay +Z. Expressed as a LookRotation away from the viewer so the
            // intent survives any future repositioning.
            var viewerLocalPosition = new Vector3(localPosition.x, localPosition.y, viewerZ);
            var awayFromViewer = localPosition - viewerLocalPosition;
            rect.localRotation = awayFromViewer.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(awayFromViewer.normalized, Vector3.up)
                : Quaternion.identity;

            rect.sizeDelta = sizePx;
            rect.localScale = Vector3.one * metresPerPixel;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 3f;

            // Both raycasters: TrackedDeviceGraphicRaycaster is what the XR controller ray
            // uses; GraphicRaycaster keeps the panel usable with a mouse in the Game view.
            go.AddComponent<GraphicRaycaster>();
            go.AddComponent<TrackedDeviceGraphicRaycaster>();

            // Background
            var bg = new GameObject("Background", typeof(RectTransform));
            bg.transform.SetParent(go.transform, false);
            var bgRect = (RectTransform)bg.transform;
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.05f, 0.06f, 0.09f, 0.94f);
            bgImage.raycastTarget = false;

            return canvas;
        }

        /// <param name="autoSizeMin">
        /// When > 0, the label auto-sizes between this and <paramref name="fontSize"/>. Used for
        /// the Area B panel, which has to carry both a multi-line instruction paragraph and a
        /// three-word target without either overflowing or being needlessly small.
        /// </param>
        static TextMeshProUGUI CreateText(string name, Transform parent, Vector2 anchoredPosition,
            Vector2 size, string text, float fontSize, TextAlignmentOptions alignment, Color color,
            float autoSizeMin = 0f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = color;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.Normal;

            if (autoSizeMin > 0f)
            {
                label.enableAutoSizing = true;
                label.fontSizeMin = autoSizeMin;
                label.fontSizeMax = fontSize;
            }

            return label;
        }

        /// <summary>
        /// A solid coloured rectangle behind another element, used as a highlight frame.
        ///
        /// Prominence is carried by SIZE, POSITION and this frame — never by colour alone, so
        /// the primary action stays distinguishable to a colour-blind participant.
        /// </summary>
        static GameObject CreatePanelFrame(string name, Transform parent, Vector2 anchoredPosition,
            Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;   // decoration; never intercepts the ray

            // Behind whatever it frames.
            go.transform.SetAsFirstSibling();
            return go;
        }

        /// <summary>
        /// Binds a button's caption to a localization key, so it follows the selected language
        /// without any code touching it again.
        ///
        /// Used for every static caption. Dynamic text (a target specification, a status line)
        /// is written by the ExperimentManager from the same service.
        /// </summary>
        static void LocalizeButton(Button button, string key, string subtitleKey = "")
        {
            if (button == null)
                return;

            var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null)
                return;

            var localized = label.gameObject.AddComponent<LocalizedText>();
            localized.SetKey(key, subtitleKey);
            EditorUtility.SetDirty(localized);
        }

        /// <summary>
        /// Binds a plain label to a localization key.
        /// </summary>
        /// <param name="format">
        /// Optional wrapper where {0} is the localized string — used for the controller callouts,
        /// whose coloured bullet is decoration rather than text and must NOT be part of the
        /// translated string.
        /// </param>
        static void LocalizeText(TextMeshProUGUI label, string key, string format = "")
        {
            if (label == null)
                return;

            var localized = label.gameObject.AddComponent<LocalizedText>();
            localized.SetKey(key, string.Empty, format);
            EditorUtility.SetDirty(localized);
        }

        static Button CreateButton(string name, Transform parent, Vector2 anchoredPosition,
            Vector2 size, string label, Color backgroundColor, float fontSize = 54f)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var image = go.AddComponent<Image>();
            image.color = backgroundColor;
            image.raycastTarget = true;

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.35f, 1.35f, 1.35f, 1f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.05f;
            button.colors = colors;

            var text = CreateText("Label", go.transform, Vector2.zero,
                size - new Vector2(30f, 20f), label, fontSize, TextAlignmentOptions.Center, Color.white);
            text.fontStyle = FontStyles.Bold;

            return button;
        }

        // =================================================================================
        // Validation & reporting
        // =================================================================================

        public static string ValidateOpenScene()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== SCENE VALIDATION =====");

            var problems = 0;

            problems += Check(sb, "XROrigin", Object.FindObjectsByType<XROrigin>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "XRInteractionManager", Object.FindObjectsByType<XRInteractionManager>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "EventSystem", Object.FindObjectsByType<UnityEngine.EventSystems.EventSystem>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "XRUIInputModule", Object.FindObjectsByType<XRUIInputModule>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "EventLogger", Object.FindObjectsByType<EventLogger>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "CsvEventSink", Object.FindObjectsByType<CsvEventSink>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "ExperimentManager", Object.FindObjectsByType<ExperimentManager>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "ExperimentUIController", Object.FindObjectsByType<ExperimentUIController>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "VoiceRecallManager", Object.FindObjectsByType<VoiceRecallManager>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "ExperimentAudio", Object.FindObjectsByType<ExperimentAudio>(FindObjectsSortMode.None).Length, 1);
            problems += Check(sb, "XRRigTeleporter", Object.FindObjectsByType<XRRigTeleporter>(FindObjectsSortMode.None).Length, 1);
            // 4 spawns: Area 0 (familiarization) plus A, B and C.
            problems += Check(sb, "SpawnPoint", Object.FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).Length, 4);
            problems += Check(sb, "PracticeObject", Object.FindObjectsByType<PracticeObject>(FindObjectsSortMode.None).Length, 4);
            problems += Check(sb, "ChairTarget", Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None).Length, 6);
            problems += Check(sb, "ChairSlot", Object.FindObjectsByType<ChairSlot>(FindObjectsSortMode.None).Length, 6);
            problems += Check(sb, "LslMarkerSink", Object.FindObjectsByType<LslMarkerSink>(FindObjectsSortMode.None).Length, 1);
            // 10 interactables: 6 chairs + 4 Area 0 practice objects.
            problems += Check(sb, "XRSimpleInteractable", Object.FindObjectsByType<XRSimpleInteractable>(FindObjectsSortMode.None).Length, 10);

            // 12 ACTIVE raycasters: Area 0 panel, 4 practice colour labels, the controller-help
            // labels, 2 hand labels, Area A, Area B instruction, Area B status, Area C.
            // The Area B instruction overlay, the shape legend, the researcher status panel and
            // the developer panel all start INACTIVE and so are not counted here.
            problems += Check(sb, "TrackedDeviceGraphicRaycaster", Object.FindObjectsByType<TrackedDeviceGraphicRaycaster>(FindObjectsSortMode.None).Length, 12);

            // Chairs must not be grabbable or movable.
            var grabbables = Object.FindObjectsByType<XRGrabInteractable>(FindObjectsSortMode.None).Length;
            if (grabbables > 0)
            {
                sb.AppendLine($"  FAIL  {grabbables} XRGrabInteractable(s) found — chairs must not be grabbable.");
                problems++;
            }
            else
            {
                sb.AppendLine("  ok    no XRGrabInteractable in the scene (chairs are selection-only)");
            }

            var rigidbodies = Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None)
                .Count(c => c.GetComponentInChildren<Rigidbody>() != null);
            if (rigidbodies > 0)
            {
                sb.AppendLine($"  FAIL  {rigidbodies} chair(s) have a Rigidbody — chairs must be static.");
                problems++;
            }
            else
            {
                sb.AppendLine("  ok    no Rigidbody on any chair (chairs cannot be pushed)");
            }

            // Exactly one chair must match the target.
            var task = Object.FindAnyObjectByType<ChairSelectionTask>();
            if (task != null)
            {
                if (task.ValidateTargetIsUnique(out var matches))
                    sb.AppendLine($"  ok    target {task.targetSpec} is matched by exactly 1 chair");
                else
                {
                    sb.AppendLine($"  FAIL  target {task.targetSpec} is matched by {matches} chairs (expected 1)");
                    problems++;
                }

                sb.AppendLine($"  info  instruction text: \"{task.BuildInstructionText()}\"");
            }

            // Colliders on environment
            var colliderCount = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length;
            sb.AppendLine($"  info  {colliderCount} colliders in the scene (walls, floors, chairs)");

            problems += CheckInteractorReach(sb);
            problems += CheckFamiliarizationContainment(sb);
            problems += CheckChairSlots(sb);
            problems += CheckShapeVariants(sb);
            problems += CheckCanvasOrientation(sb);

            // Locomotion state
            var providers = Object.FindObjectsByType<LocomotionProvider>(FindObjectsSortMode.None);
            foreach (var p in providers)
                sb.AppendLine($"  info  LocomotionProvider {p.GetType().Name}: enabled={p.enabled}");

            problems += CheckPointerAndInputConfiguration(sb);

            sb.AppendLine(problems == 0
                ? "[IKEA_EEG] VALIDATION PASSED — no structural problems found."
                : $"[IKEA_EEG] VALIDATION FOUND {problems} PROBLEM(S).");

            return sb.ToString();
        }

        /// <summary>
        /// Everything the participant must point at has to be inside the NearFarInteractor's
        /// curve-cast range (10 m by default) and must not be hidden behind another chair.
        /// Both are easy to break by nudging a transform, and both would only show up as
        /// "the ray does nothing" once someone is already wearing the headset — so they are
        /// checked here instead.
        /// </summary>
        static int CheckInteractorReach(StringBuilder sb)
        {
            const float castDistance = 10f;      // CurveInteractionCaster default
            const float eyeHeight = 1.6f;
            var problems = 0;

            var spawns = Object.FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None)
                .ToDictionary(s => s.area, s => s.transform.position);

            // --- Buttons ------------------------------------------------------------------
            // Inactive buttons MUST be included: every button except Start begins the trial
            // hidden, and those are precisely the ones whose reach is easy to get wrong.
            foreach (var button in Object.FindObjectsByType<Button>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var area = AreaOfPosition(button.transform.position);
                if (!spawns.TryGetValue(area, out var spawnPos))
                    continue;

                var eye = spawnPos + Vector3.up * eyeHeight;
                var distance = Vector3.Distance(eye, button.transform.position);

                if (distance > castDistance)
                {
                    sb.AppendLine($"  FAIL  button '{button.name}' is {distance:F1} m from " +
                                  $"{area} spawn — beyond the {castDistance} m ray range");
                    problems++;
                }
                else
                {
                    sb.AppendLine($"  ok    button '{button.name}': {distance:F1} m from " +
                                  $"{area} spawn (limit {castDistance} m)");
                }
            }

            // --- Chairs: in range and mutually non-occluding --------------------------------
            var chairs = Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None)
                .OrderBy(c => c.chairId)
                .ToArray();

            if (!spawns.TryGetValue(ExperimentArea.AreaB, out var spawnB))
                return problems;

            var eyeB = spawnB + Vector3.up * eyeHeight;

            foreach (var chair in chairs)
            {
                var bounds = ChairBounds(chair);
                var distance = Vector3.Distance(eyeB, bounds.center);

                if (distance > castDistance)
                {
                    sb.AppendLine($"  FAIL  {chair.chairId} is {distance:F1} m from the Area B " +
                                  $"spawn — beyond the {castDistance} m ray range");
                    problems++;
                    continue;
                }

                // Is any OTHER chair's bounding box on the line of sight to this one?
                var direction = (bounds.center - eyeB).normalized;
                var blockedBy = chairs
                    .Where(other => other != chair)
                    .FirstOrDefault(other =>
                    {
                        var otherBounds = ChairBounds(other);
                        if (!otherBounds.IntersectRay(new Ray(eyeB, direction), out var hitDistance))
                            return false;

                        // Only counts as occlusion if it is genuinely in front.
                        return hitDistance < distance - 0.35f;
                    });

                if (blockedBy != null)
                {
                    sb.AppendLine($"  FAIL  {chair.chairId} ({distance:F1} m) is occluded by " +
                                  $"{blockedBy.chairId} from the Area B spawn — it could not be " +
                                  "pointed at");
                    problems++;
                }
                else
                {
                    sb.AppendLine($"  ok    {chair.chairId}: {distance:F1} m, unobstructed line " +
                                  "of sight from the Area B spawn");
                }
            }

            return problems;
        }

        /// <summary>
        /// Area 0 must stay in Area 0.
        ///
        /// Two things could quietly leak into the cognitive task and both would matter: the
        /// controller-help diagram (a permanent instructional aid) and the practice objects
        /// (interactable things that are not chairs). Both are geometry in the world rather
        /// than state, so the only way to be sure is to check where they physically are.
        ///
        /// Also verified here: Area 0 contains no chair and no chair slot, so the practice room
        /// cannot prime the Area B search.
        /// </summary>
        static int CheckFamiliarizationContainment(StringBuilder sb)
        {
            var problems = 0;

            var diagrams = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(t => t.name == "ControllerDiagram")
                .ToArray();

            if (diagrams.Length == 0)
            {
                sb.AppendLine("  FAIL  no ControllerDiagram found — the Area 0 controller help " +
                              "is missing");
                problems++;
            }

            foreach (var diagram in diagrams)
            {
                var area = AreaOfPosition(diagram.position);
                if (area == ExperimentArea.Familiarization)
                {
                    sb.AppendLine("  ok    controller-help diagram is inside Area 0 only " +
                                  "(never visible during the cognitive task)");
                }
                else
                {
                    sb.AppendLine($"  FAIL  a ControllerDiagram is in {area} — controller help " +
                                  "must exist only in Area 0");
                    problems++;
                }
            }

            foreach (var practice in Object.FindObjectsByType<PracticeObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var area = AreaOfPosition(practice.transform.position);
                if (area == ExperimentArea.Familiarization)
                {
                    sb.AppendLine($"  ok    practice object '{practice.objectId}' is inside Area 0");
                }
                else
                {
                    sb.AppendLine($"  FAIL  practice object '{practice.objectId}' is in {area} — " +
                                  "practice objects must exist only in Area 0");
                    problems++;
                }
            }

            // No chair and no chair slot may exist in Area 0: the practice room must not
            // present, or hint at, the Area B stimulus set.
            var chairsInArea0 = Object.FindObjectsByType<ChairTarget>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Count(c => AreaOfPosition(c.transform.position) == ExperimentArea.Familiarization);

            var slotsInArea0 = Object.FindObjectsByType<ChairSlot>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Count(s => AreaOfPosition(s.transform.position) == ExperimentArea.Familiarization);

            if (chairsInArea0 == 0 && slotsInArea0 == 0)
            {
                sb.AppendLine("  ok    Area 0 contains no chair and no chair slot " +
                              "(cannot prime the Area B search)");
            }
            else
            {
                sb.AppendLine($"  FAIL  Area 0 contains {chairsInArea0} chair(s) and " +
                              $"{slotsInArea0} slot(s) — it must contain neither");
                problems++;
            }

            return problems;
        }

        /// <summary>
        /// The slot layout is what randomisation draws from, so its geometric guarantees are
        /// checked independently of which chair currently stands where:
        ///   * the slots do not overlap (a chair is at most ~0.8 m wide at the largest size);
        ///   * every slot is inside the interactor's 10 m reach from Spawn_B;
        ///   * every slot has a clear line of sight from Spawn_B past the other slots;
        ///   * the slots are at comparable viewing distances, so response time is not
        ///     confounded by which slot the target landed on.
        /// </summary>
        static int CheckChairSlots(StringBuilder sb)
        {
            const float castDistance = 10f;
            const float eyeHeight = 1.6f;

            // Largest chair: base footprint ~0.55 m scaled by the Large factor (1.35).
            const float chairRadius = 0.45f;
            const float minSeparation = chairRadius * 2f;

            var problems = 0;

            var slots = Object.FindObjectsByType<ChairSlot>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .OrderBy(s => s.slotIndex)
                .ToArray();

            if (slots.Length == 0)
            {
                sb.AppendLine("  FAIL  no ChairSlot found in Area B");
                return 1;
            }

            var indices = new HashSet<int>();
            foreach (var slot in slots)
            {
                if (!indices.Add(slot.slotIndex))
                {
                    sb.AppendLine($"  FAIL  slot index {slot.slotIndex} is used more than once");
                    problems++;
                }
            }

            // --- Pairwise separation ---------------------------------------------------------
            for (var i = 0; i < slots.Length; i++)
            {
                for (var j = i + 1; j < slots.Length; j++)
                {
                    var distance = Vector3.Distance(slots[i].transform.position,
                        slots[j].transform.position);

                    if (distance < minSeparation)
                    {
                        sb.AppendLine($"  FAIL  slots {slots[i].slotIndex} and {slots[j].slotIndex} " +
                                      $"are {distance:F2} m apart — chairs would overlap " +
                                      $"(need >= {minSeparation:F2} m)");
                        problems++;
                    }
                }
            }

            if (problems == 0)
                sb.AppendLine($"  ok    {slots.Length} chair slots, none closer than " +
                              $"{minSeparation:F2} m to another");

            // --- Reach, visibility and comparable distance ------------------------------------
            var spawns = Object.FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None)
                .ToDictionary(s => s.area, s => s.transform.position);

            if (!spawns.TryGetValue(ExperimentArea.AreaB, out var spawnB))
                return problems;

            var eye = spawnB + Vector3.up * eyeHeight;
            var distances = new List<float>();

            foreach (var slot in slots)
            {
                // A chair standing in the slot, approximated by a seat-height box.
                var center = slot.transform.position + Vector3.up * 0.55f;
                var distance = Vector3.Distance(eye, center);
                distances.Add(distance);

                if (distance > castDistance)
                {
                    sb.AppendLine($"  FAIL  slot {slot.slotIndex} is {distance:F1} m from the " +
                                  $"Area B spawn — beyond the {castDistance} m ray range");
                    problems++;
                    continue;
                }

                var direction = (center - eye).normalized;
                var blocked = slots
                    .Where(other => other != slot)
                    .FirstOrDefault(other =>
                    {
                        var otherBounds = new Bounds(
                            other.transform.position + Vector3.up * 0.55f,
                            new Vector3(chairRadius * 2f, 1.1f, chairRadius * 2f));

                        return otherBounds.IntersectRay(new Ray(eye, direction), out var hit) &&
                               hit < distance - 0.35f;
                    });

                if (blocked != null)
                {
                    sb.AppendLine($"  FAIL  slot {slot.slotIndex} ({distance:F1} m) is occluded by " +
                                  $"slot {blocked.slotIndex} from the Area B spawn");
                    problems++;
                }
                else
                {
                    sb.AppendLine($"  ok    slot {slot.slotIndex}: {distance:F1} m, unobstructed " +
                                  "from the Area B spawn");
                }
            }

            if (distances.Count > 1)
            {
                var spread = distances.Max() - distances.Min();
                if (spread > 1.0f)
                {
                    sb.AppendLine($"  FAIL  slot viewing distances span {spread:F2} m — too " +
                                  "unequal; response time would depend on which slot the target " +
                                  "landed in");
                    problems++;
                }
                else
                {
                    sb.AppendLine($"  ok    slot viewing distances within {spread:F2} m of each " +
                                  "other (comparable across slots)");
                }
            }

            return problems;
        }

        /// <summary>
        /// Every chair must carry all three silhouettes and register the colliders of all of
        /// them, otherwise a chair stops being selectable the moment a trial changes its shape.
        /// </summary>
        static int CheckShapeVariants(StringBuilder sb)
        {
            var problems = 0;

            foreach (var chair in Object.FindObjectsByType<ChairTarget>(FindObjectsSortMode.None)
                         .OrderBy(c => c.chairId))
            {
                var variants = chair.shapeVariants;
                var shapes = variants.Where(v => v?.root != null).Select(v => v.shape).Distinct().Count();
                var expected = System.Enum.GetValues(typeof(ChairShape)).Length;

                if (shapes != expected)
                {
                    sb.AppendLine($"  FAIL  {chair.chairId} has {shapes} shape variant(s); " +
                                  $"expected {expected}");
                    problems++;
                    continue;
                }

                var active = variants.Count(v => v?.root != null && v.root.activeSelf);
                if (active != 1)
                {
                    sb.AppendLine($"  FAIL  {chair.chairId} has {active} active body; exactly 1 " +
                                  "must be visible");
                    problems++;
                    continue;
                }

                var interactable = chair.GetComponent<XRSimpleInteractable>();
                var registered = interactable != null ? interactable.colliders.Count : 0;
                var total = chair.GetComponentsInChildren<Collider>(true).Length;

                if (interactable == null || registered < total)
                {
                    sb.AppendLine($"  FAIL  {chair.chairId} registers {registered} of {total} " +
                                  "colliders on its interactable — it would become unselectable " +
                                  "after a shape change");
                    problems++;
                }
                else
                {
                    sb.AppendLine($"  ok    {chair.chairId}: {shapes} shape variants, 1 active, " +
                                  $"{registered} collider(s) registered for all of them");
                }
            }

            return problems;
        }

        /// <summary>
        /// A world-space canvas is readable from its local -Z side, so its forward must point
        /// AWAY from the participant. Getting this backwards renders every panel mirrored —
        /// which is invisible in the Scene view (you are usually looking at it from the front)
        /// and only shows up once someone is in the headset. Hence the automated check.
        /// </summary>
        static int CheckCanvasOrientation(StringBuilder sb)
        {
            const float eyeHeight = 1.6f;
            var problems = 0;

            var spawns = Object.FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None)
                .ToDictionary(s => s.area, s => s.transform.position);

            foreach (var canvas in Object.FindObjectsByType<Canvas>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (canvas.renderMode != RenderMode.WorldSpace)
                    continue;

                var area = AreaOfPosition(canvas.transform.position);
                if (!spawns.TryGetValue(area, out var spawnPos))
                    continue;

                var eye = spawnPos + Vector3.up * eyeHeight;
                var toViewer = (eye - canvas.transform.position).normalized;

                // Readable when the canvas forward points away from the viewer, i.e. the
                // dot product of forward and the direction to the viewer is negative.
                var facing = Vector3.Dot(canvas.transform.forward, toViewer);

                if (facing < -0.2f)
                {
                    sb.AppendLine($"  ok    canvas '{canvas.name}' is readable from the " +
                                  $"{area} spawn (facing={facing:F2})");
                }
                else
                {
                    sb.AppendLine($"  FAIL  canvas '{canvas.name}' is BACKWARDS from the " +
                                  $"{area} spawn (facing={facing:F2}; needs < -0.2). " +
                                  "Its text will appear mirrored in the headset.");
                    problems++;
                }
            }

            return problems;
        }

        /// <summary>
        /// Guards the interaction fixes: stable rays, no haptics, trigger-to-select. All three
        /// are single-property changes that a scene rebuild could silently lose.
        /// </summary>
        static int CheckPointerAndInputConfiguration(StringBuilder sb)
        {
            var problems = 0;

            foreach (var visual in Object.FindObjectsByType<CurveVisualController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (visual.lineDynamicsMode == LineDynamicsMode.Traditional &&
                    visual.restingVisualLineLength >= k_PointerRayLength - 0.01f)
                {
                    sb.AppendLine($"  ok    ray '{visual.transform.parent?.parent?.name}': " +
                                  $"{visual.lineDynamicsMode} @ {visual.restingVisualLineLength} m " +
                                  "(stable, no whip needed)");
                }
                else
                {
                    sb.AppendLine($"  FAIL  ray '{visual.transform.parent?.parent?.name}': " +
                                  $"{visual.lineDynamicsMode} @ {visual.restingVisualLineLength} m " +
                                  "— will retract and require a whip motion");
                    problems++;
                }
            }

            var activeHaptics = Object.FindObjectsByType<SimpleHapticFeedback>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(h => h.enabled);

            if (activeHaptics == 0)
                sb.AppendLine("  ok    no enabled SimpleHapticFeedback (controller vibration off)");
            else
            {
                sb.AppendLine($"  FAIL  {activeHaptics} SimpleHapticFeedback component(s) still enabled");
                problems++;
            }

            foreach (var interactor in Object.FindObjectsByType<NearFarInteractor>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var name = interactor.transform.parent?.name;

                problems += CheckDualTriggerReader(sb, interactor.selectInput, name, "select");
                problems += CheckDualTriggerReader(sb, interactor.uiPressInput, name, "UI press");
            }

            var activePokes = Object.FindObjectsByType<XRPokeInteractor>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(p => p.gameObject.activeInHierarchy);
            sb.AppendLine($"  info  {activePokes} poke interactor(s) active (0 expected for " +
                          "pointing-only interaction)");

            return problems;
        }

        /// <summary>
        /// Verifies one input reader accepts BOTH the index trigger and the grip, and that they
        /// are two bindings on ONE action.
        ///
        /// The single-action requirement is the whole duplicate-selection guarantee: two
        /// separate actions would each fire their own Select and a participant squeezing both
        /// would register two selections. Counting actions is therefore the check that matters,
        /// not merely "grip appears somewhere".
        /// </summary>
        static int CheckDualTriggerReader(StringBuilder sb, XRInputButtonReader reader,
            string interactorName, string label)
        {
            if (reader == null)
            {
                sb.AppendLine($"  FAIL  {label} on '{interactorName}' has no input reader");
                return 1;
            }

            var mode = reader.inputSourceMode;
            var performed = reader.inputActionPerformed;

            if (mode != XRInputButtonReader.InputSourceMode.InputAction || performed == null)
            {
                sb.AppendLine($"  FAIL  {label} on '{interactorName}' is mode={mode} with no " +
                              "local action — expected a scene-local InputAction");
                return 1;
            }

            var paths = performed.bindings.Select(b => b.path).ToArray();
            var hasIndex = paths.Any(p => p.IndexOf("triggerPressed", System.StringComparison.OrdinalIgnoreCase) >= 0);
            var hasGrip = paths.Any(p => p.IndexOf("gripPressed", System.StringComparison.OrdinalIgnoreCase) >= 0);

            if (!hasIndex || !hasGrip)
            {
                sb.AppendLine($"  FAIL  {label} on '{interactorName}' has index={hasIndex} " +
                              $"grip={hasGrip} — both triggers must select. Bindings: " +
                              $"{string.Join(", ", paths)}");
                return 1;
            }

            sb.AppendLine($"  ok    {label} on '{interactorName}': index trigger AND grip, as " +
                          $"{paths.Length} bindings on ONE action '{performed.name}' " +
                          "(one press = one selection)");
            return 0;
        }

        static Bounds ChairBounds(ChairTarget chair)
        {
            var renderers = chair.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(chair.transform.position, Vector3.one * 0.5f);

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            return bounds;
        }

        static ExperimentArea AreaOfPosition(Vector3 worldPosition)
        {
            if (worldPosition.x > 150f) return ExperimentArea.AreaC;
            if (worldPosition.x > 50f) return ExperimentArea.AreaB;

            // Area 0 sits at x = -100, before Area A at the origin.
            if (worldPosition.x < -50f) return ExperimentArea.Familiarization;

            return ExperimentArea.AreaA;
        }

        static int Check(StringBuilder sb, string label, int actual, int expected)
        {
            if (actual == expected)
            {
                sb.AppendLine($"  ok    {label}: {actual}");
                return 0;
            }

            sb.AppendLine($"  FAIL  {label}: found {actual}, expected {expected}");
            return 1;
        }

        public static string DumpHierarchy()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[IKEA_EEG] ===== SCENE HIERARCHY =====");

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                DumpTransform(root.transform, sb, 0);

            return sb.ToString();
        }

        static void DumpTransform(Transform t, StringBuilder sb, int depth)
        {
            // Chair internals and UI internals are noise in a structural dump.
            var collapse = t.GetComponent<ChairTarget>() != null;

            sb.Append(new string(' ', depth * 2)).Append("- ").Append(t.name);

            var components = t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name)
                .ToArray();

            if (components.Length > 0)
                sb.Append("  [").Append(string.Join(", ", components)).Append(']');

            sb.AppendLine();

            if (collapse)
            {
                sb.Append(new string(' ', (depth + 1) * 2))
                  .AppendLine($"- (…{t.childCount} primitive parts)");
                return;
            }

            for (var i = 0; i < t.childCount; i++)
                DumpTransform(t.GetChild(i), sb, depth + 1);
        }

        static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scenePath))
                return;

            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        // =================================================================================
        // UI reference bundles
        // =================================================================================

        class Area0Ui
        {
            public GameObject languagePanel;
            public TextMeshProUGUI languageTitle;
            public Button languageEnglishButton;
            public Button languageSpanishButton;
            public Button languageJapaneseButton;

            public GameObject panel;
            public TextMeshProUGUI instruction;
            public TextMeshProUGUI practicePrompt;
            public TextMeshProUGUI status;
            public TextMeshProUGUI warning;
            public TextMeshProUGUI readyLabel;
            public GameObject startHighlight;
            public Button startExperimentButton;
            public Button skipIntroButton;
            public Button replayInstructionsButton;
            public Button recenterButton;
            public GameObject researcherStatusPanel;
            public TextMeshProUGUI researcherStatusText;
        }

        class AreaAUi
        {
            public GameObject panel;
            public TextMeshProUGUI title;
            public TextMeshProUGUI instruction;
            public TextMeshProUGUI word;

            /// <summary>Participant-facing "Item X / N" progress line.</summary>
            public TextMeshProUGUI recognitionCounter;

            /// <summary>DEVELOPER QA ONLY. Gated by config; hidden by default.</summary>
            public TextMeshProUGUI developerCheatsheet;
            public TextMeshProUGUI status;
            public TextMeshProUGUI warning;
            public Button startButton;
            public Button enterButton;
            public Button recheckAudioButton;
            public Button recenterButton;
        }

        class AreaBUi
        {
            public GameObject instructionPanel;
            public TextMeshProUGUI instruction;
            public GameObject statusPanel;
            public TextMeshProUGUI status;
            public TextMeshProUGUI feedback;
            public TextMeshProUGUI warning;
            public Button exitButton;
            public Button readyButton;
            public Button recenterButton;
            public GameObject shapeLegend;
            public GameObject instructionOverlay;
            public TextMeshProUGUI overlayText;
        }

        class AreaCUi
        {
            public GameObject panel;
            public TextMeshProUGUI instruction;
            public TextMeshProUGUI status;

            /// <summary>The DELAYED recognition stimulus label. See BuildAreaC.</summary>
            public TextMeshProUGUI word;

            /// <summary>Area C twin of the progress line.</summary>
            public TextMeshProUGUI recognitionCounter;

            /// <summary>Area C twin of the developer QA overlay.</summary>
            public TextMeshProUGUI developerCheatsheet;

            public TextMeshProUGUI results;
            public TextMeshProUGUI warning;
            public Button restartButton;
            public Button newTrialButton;
            public Button endButton;
            public Button recenterButton;
        }
    }
}
