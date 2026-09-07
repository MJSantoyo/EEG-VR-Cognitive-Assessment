using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using IkeaEeg.Experiment;
using IkeaEeg.Interaction;
using IkeaEeg.Localization;
using IkeaEeg.UI;
using IkeaEeg.XR;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// EDITOR-ONLY presentation screenshot capture. Produces a complete set of 1920x1080 PNGs
    /// of the REAL project UI and environments, for documentation and presentations.
    ///
    /// WHY EDIT MODE AND NOT PLAY MODE:
    /// entering Play Mode starts the real session pipeline — CsvEventSink, EegRunRecorder,
    /// RawEegRecorder, SessionSummaryWriter and VoiceRecallManager all produce files, and the
    /// run is logged as a real session. Manufacturing session artefacts in order to take a
    /// screenshot is not acceptable, so this tool never enters Play Mode.
    ///
    /// WHY A SCENE COPY:
    /// the production scene at <see cref="k_SourceScenePath"/> is COPIED to a temporary asset,
    /// the copy is opened, and only the copy is ever modified. The original scene file is not
    /// opened dirty, not saved and not touched — the tool hashes it before and after and says
    /// so in its report. The copy and its folder are deleted when the capture finishes, and the
    /// scene that was open beforehand is reopened.
    ///
    /// WHERE THE CONTENT COMES FROM:
    /// nothing here authors participant-facing text or invents an interface. Every string is
    /// read from LocalizationTable through ExperimentLocalization, exactly as the running
    /// experiment reads it, or is a literal already baked into the scene by the scene builder
    /// (the language screen's title and its three native-language button labels). Area B
    /// layouts come from ChairTrialGenerator seeded with the project's own ExperimentConfig and
    /// are applied through ChairSelectionTask.ApplyTrialPlan — the same call chain the
    /// ExperimentManager makes.
    ///
    /// The ONLY thing this file authors is the three panel captions on the composite sheet,
    /// which are the captions the presentation asked for and describe the panels rather than
    /// claiming to be participant-facing text.
    ///
    /// STATES REPRODUCED — each is a real state of the real state machine:
    ///   00  language screen              ExperimentUIController.ShowLanguagePanel(true)
    ///   01  immediate recall             RunRecall() during ExperimentState.ImmediateRecall
    ///   02  chair selection              RunChairTrial() during ExperimentState.ChairSelection
    ///   03  Area A idle                  EnterIdleAtAreaA(), ExperimentState.Idle
    ///   04  word encoding                RunAreaA(), ExperimentState.WordEncoding
    ///   05  chair selection, wide        as 02, different generated trial, no hover
    ///   06  chair selection, highlighted as 02, different generated trial, hover on target
    ///   07  delayed recall               RunAreaC() -> RunRecall(), ExperimentState.DelayedRecall
    ///   08  session ended                ShowEndedState(), ExperimentState.Ended
    ///   09/10/11  the three environments, in the states above
    ///   12  composite contact sheet built from three real renders
    ///
    /// NO WORD IS EVER DISPLAYED. The five words are an auditory-only stimulus and
    /// ExperimentUIController exposes only ClearWordDisplay() — there is no setter for a word.
    ///
    /// NO RESULTS ARE EVER FABRICATED. ExperimentState.Results fills its panel from
    /// SessionResults.BuildParticipantRunSummary(), which needs a real completed run, so shot 08
    /// uses ExperimentState.Ended instead: a real terminal state whose entire content is two
    /// localized strings and no numbers.
    ///
    /// NOTHING IN THIS FILE IS REFERENCED BY RUNTIME CODE. It lives in an Editor folder, so it
    /// is excluded from every build. Deleting it returns the project to its previous state.
    /// </summary>
    public static class PresentationCaptureTool
    {
        // ---------------------------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------------------------

        const string k_SourceScenePath = "Assets/IKEA_EEG/Scenes/IKEA_EEG_Experiment.unity";
        const string k_ConfigPath = "Assets/IKEA_EEG/Data/ExperimentConfig_Default.asset";
        const string k_TempFolderName = "IKEA_EEG_PresentationCapture_TEMP";
        const string k_TempFolderPath = "Assets/" + k_TempFolderName;
        const string k_TempScenePath = k_TempFolderPath + "/IKEA_EEG_Experiment_CAPTURE.unity";
        const string k_OutputFolderName = "Presentation_Evidence";
        const string k_CameraName = "__IKEA_EEG_PRESENTATION_CAMERA__";
        const string k_TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

        /// <summary>TMP's fallback list as it was before the capture. Restored verbatim.</summary>
        static List<TMP_FontAsset> s_SavedFallbacks;

        const int k_Width = 1920;
        const int k_Height = 1080;

        /// <summary>
        /// Shots 01 and 02 have been reviewed and approved. When true they are left exactly as
        /// they are on disk rather than re-rendered, so an approved image can never be silently
        /// replaced by a re-run of this tool. Set false to regenerate them.
        /// </summary>
        const bool k_PreserveApprovedShots = true;

        // --- Area root names, used to place environment cameras in each room's own frame ---
        const string k_Area0Root = "Area_0_Familiarization";
        const string k_AreaARoot = "Area_A_Entrance";
        const string k_AreaBRoot = "Area_B_Showroom";
        const string k_AreaCRoot = "Area_C_Exit";

        // ---------------------------------------------------------------------------------
        // Camera placements
        //
        // All positions are LOCAL to the area root, so they follow the authored geometry rather
        // than hard-coding world coordinates. The rooms they have to stay inside:
        //   Area 0  6.0 x 5.5, walls 3.0, ceiling closed. Spawn_0 at z = -1.6.
        //   Area A  4.5 x 4.0, walls 3.0, NO ceiling.     Spawn_A at z = -0.7.
        //   Area B 14.0 x 13.0, walls 3.6, ceiling closed. Spawn_B at z = -2.6, chairs on a
        //          5 m arc at +/-50, +/-30, +/-10 degrees.
        //   Area C  4.5 x 4.0, walls 3.0, NO ceiling.     Spawn_C at z = -0.7.
        //
        // Area A and Area C are 4 m deep, so a camera can only stand about 1.1 m behind the
        // spawn mark before it is inside the back wall. Those two views are widened with field
        // of view, never with distance. Area A and C have no ceiling, so their environment
        // shots can look in from above the wall line; Area B is closed, so its environment shot
        // is an interior three-quarter view.
        // ---------------------------------------------------------------------------------

        struct Shot
        {
            public Vector3 localPosition;
            public Vector3 localLookAt;
            public float fieldOfView;

            public Shot(Vector3 position, Vector3 lookAt, float fov)
            {
                localPosition = position;
                localLookAt = lookAt;
                fieldOfView = fov;
            }
        }

        // Area 0 — the language screen. UI_LanguageSelection sits at (0, 1.95, 1.0), 1.8 m square.
        // Framed above the four practice objects, which stand on the floor of the same room and
        // belong to the NEXT screen, not this one.
        // The four practice objects belong to the NEXT screen and are distracting here, but they
        // cannot simply be cropped at the panel's edge: their labels sit ABOVE each object, at
        // y = 1.25 on z = 0.25, which is HIGHER than the panel's own bottom edge at y = 1.05 and
        // nearer the camera. The frame is therefore closed on the lowest thing that matters —
        // the bottom of the Japanese button at y = 1.45 — which is the first framing that clears
        // the labels. Panel content spans 1.45 to 2.85, so nothing on the screen is lost.
        static readonly Shot k_Shot00 = new Shot(
            new Vector3(0f, 2.10f, -2.25f), new Vector3(0f, 2.10f, 1.0f), 26f);

        // Area A — UI_A_Canvas sits at (0, 1.65, 1.4), about 1.65 x 1.27 m.
        static readonly Shot k_Shot01 = new Shot(
            new Vector3(0f, 1.65f, -1.70f), new Vector3(0f, 1.65f, 1.4f), 38f);

        static readonly Shot k_Shot03 = new Shot(
            new Vector3(0f, 1.65f, -1.75f), new Vector3(0f, 1.58f, 1.4f), 50f);

        static readonly Shot k_Shot04 = k_Shot01;

        // Area A's environment shot is taken from INSIDE the room, from a raised back corner.
        // An exterior bird's-eye looks appealing on paper but does not work here: the back wall
        // is 3 m tall and only 2 m from the room's centre, so any camera outside it low enough
        // to see the facade is looking at the back of that wall. It would have to climb past
        // 9 m to clear the wall line, by which point the shot is a plan view of a grey box.
        static readonly Shot k_Shot09 = new Shot(
            new Vector3(-1.85f, 2.55f, -1.75f), new Vector3(0.35f, 1.05f, 1.3f), 62f);

        // Area B — the chair arc plus both wall panels. Spawn_B is at z = -2.6.
        static readonly Shot k_Shot02 = new Shot(
            new Vector3(0f, 1.78f, -6.00f), new Vector3(0f, 1.41f, 5.6f), 45f);

        static readonly Shot k_Shot05 = new Shot(
            new Vector3(0f, 2.05f, -6.15f), new Vector3(0f, 1.10f, 5.6f), 52f);

        // Closer and off the centre line, so the highlighted chair reads as the subject of the
        // shot rather than one of six. The two chairs on the far right fall outside the frame;
        // that is deliberate, and cleanly outside rather than sliced at the edge.
        static readonly Shot k_Shot06 = new Shot(
            new Vector3(-2.2f, 1.70f, -4.20f), new Vector3(-1.2f, 1.35f, 3.0f), 44f);

        static readonly Shot k_Shot10 = new Shot(
            new Vector3(-4.8f, 2.75f, -5.6f), new Vector3(0.8f, 1.15f, 1.8f), 54f);

        // Area C — UI_C_Canvas sits at (0, 1.55, 1.4) and is 2.19 m tall because it is sized for
        // the eleven-line Session Summary that appears at the END of a run. During delayed recall
        // most of that height is genuinely blank, and there is no framing that hides it: the
        // blank runs from just under the status line down to the RECENTER button, so cropping to
        // the text throws the room away and still shows the void. The whole panel is framed
        // instead, with the vestibule around it, and the empty area is left as what it is.
        static readonly Shot k_Shot07 = new Shot(
            new Vector3(0f, 1.62f, -1.75f), new Vector3(0f, 1.66f, 1.4f), 48f);

        static readonly Shot k_Shot08 = k_Shot07;

        // Interior raised corner, for the same reason as Area A: the room is 4 m deep with 3 m
        // walls, so there is no exterior vantage point that is not looking at a wall.
        static readonly Shot k_Shot11 = new Shot(
            new Vector3(-1.85f, 2.55f, -1.70f), new Vector3(0.30f, 1.30f, 1.3f), 62f);

        // --- Composite sheet ---------------------------------------------------------------
        // Three 4:3 panels in one row. Three landscape panels cannot tile a 16:9 frame without
        // some margin above and below; 4:3 is the tallest shape that still holds Area B's chair
        // arc, which spans about 66 degrees of azimuth and clips immediately in anything
        // narrower. Panels are rendered at 2x and downsampled by the RawImage for clean edges.
        const int k_PanelWidth = 613;
        const int k_PanelHeight = 460;
        const int k_PanelGutter = 20;
        const int k_PanelLabelHeight = 56;
        const int k_PanelLabelGap = 12;
        const float k_PanelAspect = (float)k_PanelWidth / k_PanelHeight;

        const string k_LabelA = "Area A — Entry and Encoding";
        const string k_LabelB = "Area B — Selection Task";
        const string k_LabelC = "Area C — Delayed Recall and Completion";

        // The panel cameras are separate placements, not the 16:9 ones with a different aspect:
        // field of view is vertical, so narrowing the frame to 4:3 cuts the horizontal view and
        // would slice the outer chairs off Area B's panel.
        static readonly Shot k_PanelShotA = k_Shot03;

        static readonly Shot k_PanelShotB = new Shot(
            new Vector3(0f, 2.00f, -6.20f), new Vector3(0f, 1.20f, 5.6f), 56f);

        static readonly Shot k_PanelShotC = k_Shot07;

        // ---------------------------------------------------------------------------------
        // Entry points
        // ---------------------------------------------------------------------------------

        [MenuItem("IKEA_EEG/Presentation/Capture Presentation Screenshots (1920x1080)", false, 400)]
        public static void CaptureFromMenu()
        {
            var ok = Capture(out var report);
            Debug.Log(report);
            EditorUtility.DisplayDialog(
                ok ? "IKEA_EEG presentation capture" : "IKEA_EEG presentation capture — PROBLEM",
                report, "OK");
        }

        /// <summary>
        /// Batch entry point:
        ///   Unity.exe -batchmode -projectPath &lt;project&gt; -quit
        ///             -executeMethod IkeaEeg.EditorTools.PresentationCaptureTool.CaptureFromCommandLine
        /// Do NOT pass -nographics: rendering needs a graphics device.
        /// </summary>
        public static void CaptureFromCommandLine()
        {
            var ok = Capture(out var report);
            Debug.Log(report);

            if (!ok)
            {
                Debug.LogError("[IKEA_EEG] Presentation capture FAILED.");
                EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------------------------
        // Capture
        // ---------------------------------------------------------------------------------

        static bool Capture(out string report)
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine("[IKEA_EEG] Presentation capture");

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report = log.Append("ABORTED: the Editor is in Play Mode. Exit Play Mode first.")
                    .ToString();
                return false;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(k_SourceScenePath) == null)
            {
                report = log.Append($"ABORTED: source scene not found at {k_SourceScenePath}.")
                    .ToString();
                return false;
            }

            // Refuse to run over unsaved work. Restoring the previously open scene at the end
            // means reopening it, which would discard whatever is unsaved right now.
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (!open.isDirty)
                    continue;

                report = log.Append($"ABORTED: the open scene '{open.name}' has unsaved changes. " +
                                    "Save or discard them first — this tool reopens the scene " +
                                    "when it finishes and would otherwise lose them.").ToString();
                return false;
            }

            var previousScenePath = SceneManager.GetActiveScene().path;
            var sourceHashBefore = HashFile(k_SourceScenePath);
            var tmpHashBefore = HashFile(k_TmpSettingsPath);

            var outputFolder = Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName, k_OutputFolderName);

            var succeeded = false;

            // Panel textures for the composite sheet, grabbed while each area is already in the
            // state its panel is meant to show. Released in the finally block whatever happens.
            RenderTexture panelA = null;
            RenderTexture panelB = null;
            RenderTexture panelC = null;

            try
            {
                Directory.CreateDirectory(outputFolder);

                // ---- Throwaway copy of the production scene ---------------------------------
                DeleteTempFolder();
                AssetDatabase.CreateFolder("Assets", k_TempFolderName);

                if (!AssetDatabase.CopyAsset(k_SourceScenePath, k_TempScenePath))
                {
                    report = log.Append($"ABORTED: could not copy the scene to {k_TempScenePath}.")
                        .ToString();
                    return false;
                }

                AssetDatabase.Refresh();
                log.AppendLine($"  copied scene  -> {k_TempScenePath}");

                var scene = EditorSceneManager.OpenScene(k_TempScenePath, OpenSceneMode.Single);
                if (!scene.IsValid())
                {
                    report = log.Append("ABORTED: the scene copy could not be opened.").ToString();
                    return false;
                }

                // Every participant-facing string in the capture comes from the table. English
                // is chosen explicitly rather than inherited from whatever ran last.
                ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

                var ui = UnityEngine.Object.FindAnyObjectByType<ExperimentUIController>(
                    FindObjectsInactive.Include);

                if (ui == null)
                {
                    report = log.Append("ABORTED: no ExperimentUIController in the scene.")
                        .ToString();
                    return false;
                }

                // Quiet every surface no capture owns, so nothing left over from the authored
                // scene state leaks into a frame.
                ui.ShowLanguagePanel(false);
                ui.SetWarning(string.Empty);
                ui.ShowRecheckAudioButton(false);
                ui.ShowResearcherStatusPanel(false);
                HideDeveloperPanel();

                CaptureLanguageScreen(ui, outputFolder, log);
                CaptureAreaA(ui, outputFolder, log, ref panelA);
                CaptureAreaB(ui, outputFolder, log, ref panelB);
                CaptureAreaC(ui, outputFolder, log, ref panelC);
                CaptureComposite(panelA, panelB, panelC, outputFolder, log);

                succeeded = true;
            }
            catch (Exception e)
            {
                log.AppendLine($"ABORTED with an exception: {e}");
            }
            finally
            {
                Release(ref panelA);
                Release(ref panelB);
                Release(ref panelC);

                // ---- Restore ---------------------------------------------------------------
                ExperimentLocalization.ClearLanguage();

                // Flush the COPY's changes to the COPY's own file. Reopening a scene over a
                // dirty one prompts the user to save, and answering that prompt wrongly is the
                // one way this tool could cost work. Writing the throwaway file avoids the
                // prompt entirely; it is deleted a few lines below and the production scene is
                // not involved. The path is asserted first so this can only ever write the copy.
                var temp = SceneManager.GetActiveScene();
                if (temp.IsValid() && temp.isDirty && temp.path == k_TempScenePath)
                    EditorSceneManager.SaveScene(temp, k_TempScenePath);

                if (!string.IsNullOrEmpty(previousScenePath) &&
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(previousScenePath) != null)
                {
                    EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
                }
                else if (SceneManager.GetActiveScene().path == k_TempScenePath)
                {
                    // Batch mode starts with no scene open. Leave a real one open rather than
                    // the copy we are about to delete.
                    EditorSceneManager.OpenScene(k_SourceScenePath, OpenSceneMode.Single);
                }

                DeleteTempFolder();
                AssetDatabase.Refresh();
            }

            var sourceHashAfter = HashFile(k_SourceScenePath);
            var tmpHashAfter = HashFile(k_TmpSettingsPath);

            log.AppendLine($"  source scene md5 before = {sourceHashBefore}");
            log.AppendLine($"  source scene md5 after  = {sourceHashAfter}");
            log.AppendLine(sourceHashBefore == sourceHashAfter
                ? "  PRODUCTION SCENE UNCHANGED."
                : "  WARNING: the production scene file changed. Investigate before committing.");

            log.AppendLine($"  TMP Settings md5 before = {tmpHashBefore}");
            log.AppendLine($"  TMP Settings md5 after  = {tmpHashAfter}");
            log.AppendLine(tmpHashBefore == tmpHashAfter
                ? "  TMP SETTINGS UNCHANGED."
                : "  WARNING: TMP Settings changed. The Japanese fallback was not fully undone.");

            log.Append($"  output folder = {outputFolder}");

            report = log.ToString();
            return succeeded && sourceHashBefore == sourceHashAfter && tmpHashBefore == tmpHashAfter;
        }

        // ---------------------------------------------------------------------------------
        // 00 — language selection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The first screen a participant sees. Its title carries all three languages at once
        /// and each button is labelled in its own language — both are literals the scene builder
        /// baked into the scene, not text produced here.
        /// </summary>
        static void CaptureLanguageScreen(ExperimentUIController ui, string outputFolder,
            System.Text.StringBuilder log)
        {
            var root = FindRoot(k_Area0Root);
            if (root == null)
            {
                log.AppendLine($"  00  SKIPPED: {k_Area0Root} not found.");
                return;
            }

            ui.ShowArea(ExperimentArea.Familiarization);
            ui.ShowLanguagePanel(true);

            BeginJapaneseGlyphs(log);

            try
            {
                Save(root, k_Shot00, outputFolder, "00_Language_Selection.png", log,
                    "language screen (EN / ES / JA)");
            }
            finally
            {
                EndJapaneseGlyphs();
            }

            ui.ShowLanguagePanel(false);
        }

        /// <summary>
        /// Makes the Japanese button label render instead of coming out as empty boxes.
        ///
        /// The scene's font asset has no CJK glyphs. At run time the experiment solves this in
        /// ExperimentLocalization.EnsureJapaneseFontFallback(), which builds a dynamic font asset
        /// from an installed system font and pushes it onto TMP_Settings.fallbackFontAssets. In
        /// Play Mode that list is a runtime copy and the change evaporates; in Edit Mode it is
        /// the live TMP Settings ASSET, and the font asset it inserts exists only in memory — so
        /// leaving it there would put a reference to a non-existent asset into a project file.
        ///
        /// The list is therefore snapshotted first and restored exactly afterwards, the settings
        /// object is un-dirtied, and the tool's report hashes TMP Settings.asset before and after
        /// so the claim that it is untouched is checked rather than asserted.
        /// </summary>
        static void BeginJapaneseGlyphs(System.Text.StringBuilder log)
        {
            var fallbacks = TMP_Settings.fallbackFontAssets;
            s_SavedFallbacks = fallbacks != null ? new List<TMP_FontAsset>(fallbacks) : null;

            if (ExperimentLocalization.EnsureJapaneseFontFallback())
            {
                RefreshAllText();
                log.AppendLine($"  Japanese glyphs: {ExperimentLocalization.fontFallbackDetail}");
            }
            else
            {
                log.AppendLine($"  Japanese glyphs UNAVAILABLE: " +
                               $"{ExperimentLocalization.fontFallbackDetail}");
            }
        }

        static void EndJapaneseGlyphs()
        {
            var fallbacks = TMP_Settings.fallbackFontAssets;

            if (fallbacks != null && s_SavedFallbacks != null)
            {
                fallbacks.Clear();
                fallbacks.AddRange(s_SavedFallbacks);
            }

            s_SavedFallbacks = null;

            if (TMP_Settings.instance != null)
                EditorUtility.ClearDirty(TMP_Settings.instance);

            // EnsureJapaneseFontFallback latches a "ready" flag. Clearing it keeps a second run
            // in the same Editor session honest — otherwise it would skip rebuilding the
            // fallback we just removed and quietly produce boxes again.
            ExperimentLocalization.ResetForTesting();
            ExperimentLocalization.SetLanguage(ExperimentLanguage.English);

            RefreshAllText();
        }

        /// <summary>
        /// Re-resolves every label's glyphs. TMP caches generated meshes, so a font asset added
        /// or removed after a label has drawn once does not reach it without this.
        /// </summary>
        static void RefreshAllText()
        {
            foreach (var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (text == null)
                    continue;

                text.SetAllDirty();
                text.ForceMeshUpdate(true, true);
            }
        }

        // ---------------------------------------------------------------------------------
        // Area A — 03, 04, 01, 09
        // ---------------------------------------------------------------------------------

        static void CaptureAreaA(ExperimentUIController ui, string outputFolder,
            System.Text.StringBuilder log, ref RenderTexture panelA)
        {
            var root = FindRoot(k_AreaARoot);
            if (root == null)
            {
                log.AppendLine($"  03/04/01/09  SKIPPED: {k_AreaARoot} not found.");
                return;
            }

            ui.ShowArea(ExperimentArea.AreaA);
            ui.ClearWordDisplay();
            ui.ShowRecenterButtons(true);
            ui.SetAreaATitle(ExperimentLocalization.Get(LocKeys.AreaATitle));

            // ---- 03 + 09: ExperimentState.Idle, exactly as EnterIdleAtAreaA() sets it -------
            ui.SetAreaAInstruction(ExperimentLocalization.Get(LocKeys.AreaAWelcome));
            ui.SetAreaAStatus(string.Empty);
            ui.ShowStartButton(true);
            ui.ShowEnterAreaBButton(false);

            Save(root, k_Shot03, outputFolder, "03_Area_A_Experiment_Start.png", log,
                "state=Idle (START offered)");

            Save(root, k_Shot09, outputFolder, "09_Area_A_Environment.png", log,
                "environment, state=Idle");

            // ---- 04: ExperimentState.WordEncoding, exactly as RunAreaA() sets it ------------
            // The screen carries ONE static prompt for the whole encoding phase and does not
            // change per word — a per-word visual change would leak the item count and timing
            // and inject a visual evoked response on top of the auditory one.
            ui.SetAreaAInstruction(ExperimentLocalization.Get(LocKeys.EncodingListen));
            ui.SetAreaAStatus(string.Empty);
            ui.ShowStartButton(false);
            ui.ClearWordDisplay();

            Save(root, k_Shot04, outputFolder, "04_Area_A_Encoding_Instruction.png", log,
                "state=WordEncoding (no word displayed — auditory-only stimulus)");

            panelA = RenderPanel(root, k_PanelShotA);

            // ---- 01: ExperimentState.ImmediateRecall, as RunRecall() sets it ----------------
            ui.SetAreaAInstruction(ExperimentLocalization.Get(LocKeys.ImmediateRecallPrompt));
            ui.SetAreaAStatus(ExperimentLocalization.Get(LocKeys.Recording));

            Save(root, k_Shot01, outputFolder, "01_VR_Verbal_Memory.png", log,
                "state=ImmediateRecall", approved: true);
        }

        // ---------------------------------------------------------------------------------
        // Area B — 05, 10, 02, 06
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Reproduces ExperimentState.ChairSelection exactly as RunChairTrial() sets it up: the
        /// generated layout applied to the room, the target panel showing the three attributes,
        /// status and feedback blank, and the overlay and legend down.
        ///
        /// Three DIFFERENT trials of the project's own generated block are used, so the three
        /// Area B shots are not the same picture three times. Each is a real trial the runtime
        /// would produce from the config's fixed seed.
        /// </summary>
        static void CaptureAreaB(ExperimentUIController ui, string outputFolder,
            System.Text.StringBuilder log, ref RenderTexture panelB)
        {
            var root = FindRoot(k_AreaBRoot);
            var task = UnityEngine.Object.FindAnyObjectByType<ChairSelectionTask>(
                FindObjectsInactive.Include);
            var config = AssetDatabase.LoadAssetAtPath<ExperimentConfig>(k_ConfigPath);

            if (root == null || task == null || config == null)
            {
                log.AppendLine("  05/10/02/06  SKIPPED: Area B root, ChairSelectionTask or " +
                               "ExperimentConfig_Default not found.");
                return;
            }

            ui.ShowArea(ExperimentArea.AreaB);

            // The overlay and legend belong to the pre-READY instruction screen and are down for
            // the whole rest of the block.
            ui.ShowAreaBInstructionOverlay(false);
            ui.ShowShapeLegend(false);
            ui.ShowReadyButton(false);
            ui.ShowExitToAreaCButton(false);
            ui.SetAreaBStatus(string.Empty);
            ui.SetAreaBFeedback(string.Empty);

            if (!ChairTrialGenerator.TryGenerateBlock(config.fixedSeed,
                    config.BuildDifficultySequence(), config.difficultyProfiles,
                    task.GetChairIds(), task.slots?.Count ?? 0, out var plans, out var problem))
            {
                log.AppendLine($"  05/10/02/06  SKIPPED: trial generation failed: {problem}");
                return;
            }

            for (var i = 0; i < plans.Count; i++)
                log.AppendLine($"  generated trial {i}: {plans[i].Describe()}");

            // ---- 05 + 10: trial 1 (LOW), no hover ------------------------------------------
            // The wide overview and the environment shot use the LOW-difficulty trial: its
            // distractors share fewest attributes with the target, so six visibly different
            // chairs read clearly at a distance. No chair is highlighted, so the audience can
            // check the target panel against the room themselves.
            if (ApplyTrial(task, ui, plans, 0, log))
            {
                Save(root, k_Shot05, outputFolder, "05_Area_B_Showroom_Overview.png", log,
                    $"state=ChairSelection, trial 1/3 LOW, target={plans[0].target}, no hover");

                Save(root, k_Shot10, outputFolder, "10_Area_B_Environment.png", log,
                    "environment, six chairs, trial 1/3 LOW");
            }

            // ---- 02: trial 2 (MEDIUM), hover on target -------------------------------------
            if (!k_PreserveApprovedShots || !File.Exists(
                    Path.Combine(outputFolder, "02_VR_Selection_Task.png")))
            {
                if (ApplyTrial(task, ui, plans, 1, log))
                {
                    task.BeginSelection();
                    ApplyHoverHighlight(task.FindChair(plans[1].targetChairId), log);

                    Save(root, k_Shot02, outputFolder, "02_VR_Selection_Task.png", log,
                        $"state=ChairSelection, trial 2/3 MEDIUM, target={plans[1].target}",
                        approved: true);
                }
            }
            else
            {
                log.AppendLine("  02  PRESERVED: approved file left untouched on disk.");
            }

            // ---- 06 + composite panel B: trial 2 (MEDIUM), hover on target ------------------
            //
            // WHY THIS TRIAL AND NOT THE HIGH ONE. M_Chair_Hover is pale yellow and REPLACES the
            // chair's own colour, so a highlighted chair stops showing the colour the target
            // panel names. Any trial that leaves another chair matching that colour therefore
            // reads, to someone who does not know the hover convention, as a highlight sitting
            // on the wrong chair — the HIGH trial asks for GREEN and leaves a green distractor
            // standing three slots away, and the LOW trial asks for WHITE while the hover makes
            // the target look like the yellow chair beside it.
            //
            // Trial 2 asks for BLUE and its five distractors are white, white, white, green and
            // red. Once the target is repainted nothing else in the room is blue and nothing
            // else is pale yellow, so the highlight can only be read as a highlight. The chair's
            // size and shape still verify against the panel.
            const int highlightTrial = 1;

            if (ApplyTrial(task, ui, plans, highlightTrial, log))
            {
                task.BeginSelection();
                ApplyHoverHighlight(task.FindChair(plans[highlightTrial].targetChairId), log);

                Save(root, k_Shot06, outputFolder, "06_Area_B_Selected_Chair.png", log,
                    $"state=ChairSelection, trial 2/3 MEDIUM, target={plans[highlightTrial].target}" +
                    $", hover on {plans[highlightTrial].targetChairId}");

                panelB = RenderPanel(root, k_PanelShotB);
            }
        }

        /// <summary>
        /// Applies one generated trial through the project's own call chain and writes the
        /// target panel from the task itself, so the text and the room can never disagree.
        /// </summary>
        static bool ApplyTrial(ChairSelectionTask task, ExperimentUIController ui,
            IReadOnlyList<ChairTrialPlan> plans, int index, System.Text.StringBuilder log)
        {
            if (index < 0 || index >= plans.Count)
            {
                log.AppendLine($"  trial {index} is outside the generated block.");
                return false;
            }

            if (!task.ApplyTrialPlan(plans[index], out var problem))
            {
                log.AppendLine($"  trial {index} not applicable: {problem}");
                return false;
            }

            ui.SetChairInstruction(
                task.BuildTargetText(ExperimentLocalization.Get(LocKeys.ChairTargetHeader)));
            ui.SetAreaBStatus(string.Empty);
            ui.SetAreaBFeedback(string.Empty);
            return true;
        }

        // ---------------------------------------------------------------------------------
        // Area C — 07, 11, 08
        // ---------------------------------------------------------------------------------

        static void CaptureAreaC(ExperimentUIController ui, string outputFolder,
            System.Text.StringBuilder log, ref RenderTexture panelC)
        {
            var root = FindRoot(k_AreaCRoot);
            if (root == null)
            {
                log.AppendLine($"  07/11/08  SKIPPED: {k_AreaCRoot} not found.");
                return;
            }

            ui.ShowArea(ExperimentArea.AreaC);
            ui.ShowRecenterButtons(true);

            // ---- 07 + 11: ExperimentState.DelayedRecall, as RunAreaC() -> RunRecall() -------
            ui.SetAreaCInstruction(ExperimentLocalization.Get(LocKeys.DelayedRecallPrompt));
            ui.SetAreaCStatus(ExperimentLocalization.Get(LocKeys.Recording));
            ui.SetResults(string.Empty);
            ui.ShowRestartButton(false);
            ui.ShowNewTrialButton(false);
            ui.ShowEndButton(false);

            Save(root, k_Shot07, outputFolder, "07_Area_C_Delayed_Recall.png", log,
                "state=DelayedRecall (recording)");

            panelC = RenderPanel(root, k_PanelShotC);

            Save(root, k_Shot11, outputFolder, "11_Area_C_Environment.png", log,
                "environment, state=DelayedRecall");

            // ---- 08: ExperimentState.Ended, exactly as ShowEndedState() sets it -------------
            // NOT ExperimentState.Results. The results panel is filled from
            // SessionResults.BuildParticipantRunSummary(), which requires a completed run, and
            // inventing those numbers is not acceptable. Ended is a real terminal state whose
            // whole content is two localized strings and no data at all.
            ui.SetAreaCInstruction(ExperimentLocalization.Get(LocKeys.SessionComplete));
            ui.SetAreaCStatus(string.Empty);
            ui.SetResults(ExperimentLocalization.Get(LocKeys.SessionCompleteThanks));
            ui.ShowRestartButton(false);
            ui.ShowNewTrialButton(false);
            ui.ShowEndButton(false);

            Save(root, k_Shot08, outputFolder, "08_Area_C_Results_or_End.png", log,
                "state=Ended (no run summary — nothing is fabricated)");
        }

        // ---------------------------------------------------------------------------------
        // 12 — composite contact sheet
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Builds the three-panel sheet from three REAL renders taken while each area was in the
        /// state its caption names.
        ///
        /// It is deliberately a contact sheet and NOT a single wide shot of all three rooms.
        /// The areas sit 100 m apart on the X axis with nothing between them — there are no
        /// corridors, and a single camera that appeared to show a continuous space would be
        /// claiming a layout the project does not have.
        ///
        /// The sheet is assembled as a real Unity world-space canvas in the throwaway scene and
        /// photographed with an orthographic camera, so the captions are rendered by TextMeshPro
        /// using the scene's own font asset rather than drawn into pixels by hand.
        /// </summary>
        static void CaptureComposite(RenderTexture a, RenderTexture b, RenderTexture c,
            string outputFolder, System.Text.StringBuilder log)
        {
            if (a == null || b == null || c == null)
            {
                log.AppendLine("  12  SKIPPED: one or more area panels were not rendered.");
                return;
            }

            var font = FindSceneFont();
            if (font == null)
            {
                log.AppendLine("  12  SKIPPED: no TMP font asset found in the scene to caption " +
                               "the panels with.");
                return;
            }

            // Far below the rooms, so the sheet's own camera can see nothing else.
            var sheetOrigin = new Vector3(0f, -1000f, 0f);

            var canvasGo = new GameObject("__IKEA_EEG_COMPOSITE_SHEET__",
                typeof(RectTransform), typeof(Canvas)) { hideFlags = HideFlags.DontSave };

            try
            {
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;

                var canvasRect = canvasGo.GetComponent<RectTransform>();
                canvasRect.sizeDelta = new Vector2(k_Width, k_Height);
                canvasGo.transform.position = sheetOrigin;
                canvasGo.transform.rotation = Quaternion.identity;
                canvasGo.transform.localScale = Vector3.one * 0.01f;

                AddImage(canvasRect, "Background", Vector2.zero,
                    new Vector2(k_Width, k_Height), new Color(0.078f, 0.086f, 0.106f));

                // One row of three panels, centred as a block so the margins above and below
                // match. Row width = 3 panels + 2 gutters.
                var rowWidth = 3 * k_PanelWidth + 2 * k_PanelGutter;
                var blockHeight = k_PanelHeight + k_PanelLabelGap + k_PanelLabelHeight;

                var firstCentreX = -rowWidth * 0.5f + k_PanelWidth * 0.5f;
                var imageCentreY = blockHeight * 0.5f - k_PanelHeight * 0.5f;
                var labelCentreY = imageCentreY - k_PanelHeight * 0.5f
                                   - k_PanelLabelGap - k_PanelLabelHeight * 0.5f;

                var textures = new[] { a, b, c };
                var captions = new[] { k_LabelA, k_LabelB, k_LabelC };

                for (var i = 0; i < 3; i++)
                {
                    var x = firstCentreX + i * (k_PanelWidth + k_PanelGutter);

                    // A thin frame behind each panel so the render reads as a plate rather than
                    // bleeding into the background.
                    AddImage(canvasRect, $"Frame_{i}", new Vector2(x, imageCentreY),
                        new Vector2(k_PanelWidth + 6, k_PanelHeight + 6),
                        new Color(0.28f, 0.31f, 0.36f));

                    AddRawImage(canvasRect, $"Panel_{i}", new Vector2(x, imageCentreY),
                        new Vector2(k_PanelWidth, k_PanelHeight), textures[i]);

                    AddLabel(canvasRect, $"Caption_{i}", new Vector2(x, labelCentreY),
                        new Vector2(k_PanelWidth, k_PanelLabelHeight), captions[i], font);
                }

                Canvas.ForceUpdateCanvases();

                var cameraPosition = sheetOrigin + new Vector3(0f, 0f, -10f);
                RenderOrthographic(cameraPosition, k_Height * 0.01f * 0.5f,
                    Path.Combine(outputFolder, "12_Three_Areas_Overview.png"));

                log.AppendLine("  12_Three_Areas_Overview        three real renders, captioned " +
                               "-> 12_Three_Areas_Overview.png");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasGo);
            }
        }

        static void AddImage(RectTransform parent, string name, Vector2 anchoredPosition,
            Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image))
            {
                hideFlags = HideFlags.DontSave,
            };

            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            go.GetComponent<Image>().color = color;
        }

        static void AddRawImage(RectTransform parent, string name, Vector2 anchoredPosition,
            Vector2 size, Texture texture)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(RawImage))
            {
                hideFlags = HideFlags.DontSave,
            };

            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            go.GetComponent<RawImage>().texture = texture;
        }

        static void AddLabel(RectTransform parent, string name, Vector2 anchoredPosition,
            Vector2 size, string text, TMP_FontAsset font)
        {
            var go = new GameObject(name, typeof(RectTransform)) { hideFlags = HideFlags.DontSave };

            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            var label = go.AddComponent<TextMeshProUGUI>();
            label.font = font;
            label.text = text;
            label.fontSize = 30f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 18f;
            label.fontSizeMax = 30f;
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(0.93f, 0.94f, 0.96f);
            label.ForceMeshUpdate();
        }

        /// <summary>
        /// The font the scene's own labels use, so the captions match the project rather than
        /// introducing a typeface from somewhere else.
        /// </summary>
        static TMP_FontAsset FindSceneFont()
        {
            foreach (var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (text != null && text.font != null)
                    return text.font;
            }

            return null;
        }

        // ---------------------------------------------------------------------------------
        // Chair highlight
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Puts one chair into its hover look.
        ///
        /// ChairTarget applies this material from OnHoverEntered, which only an XRI hover event
        /// can raise — and no interactor exists outside Play Mode. The material and the renderer
        /// list are therefore read off the component and applied the same way ChairTarget's own
        /// ApplyMaterial does. Both are the component's real serialized values; nothing is
        /// substituted, and this happens on the throwaway scene copy only.
        ///
        /// NOTE: the hover material replaces the chair's colour, so a highlighted chair no
        /// longer shows the colour named in the target panel. That is the project's own
        /// behaviour in the headset, not an artefact of this capture.
        /// </summary>
        static void ApplyHoverHighlight(ChairTarget chair, System.Text.StringBuilder log)
        {
            if (chair == null)
            {
                log.AppendLine("  hover highlight: target chair not found — chairs left at rest.");
                return;
            }

            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = typeof(ChairTarget);

            var hoverMaterial = type.GetField("m_HoverMaterial", flags)?.GetValue(chair)
                as Material;

            var renderers = type.GetField("m_Renderers", flags)?.GetValue(chair)
                as List<Renderer>;

            if (hoverMaterial == null || renderers == null || renderers.Count == 0)
            {
                log.AppendLine("  hover highlight: material or renderers unavailable — " +
                               "chairs left at rest.");
                return;
            }

            foreach (var renderer in renderers)
            {
                if (renderer != null)
                    renderer.sharedMaterial = hoverMaterial;
            }

            log.AppendLine($"  hover highlight: {chair.chairId} using '{hoverMaterial.name}' " +
                           $"across {renderers.Count} renderer(s).");
        }

        // ---------------------------------------------------------------------------------
        // Rendering
        // ---------------------------------------------------------------------------------

        /// <summary>Renders one 1920x1080 PNG and reports it into the log.</summary>
        static void Save(Transform areaRoot, Shot shot, string outputFolder, string fileName,
            System.Text.StringBuilder log, string note, bool approved = false)
        {
            if (approved && k_PreserveApprovedShots &&
                File.Exists(Path.Combine(outputFolder, fileName)))
            {
                log.AppendLine($"  {fileName,-34}  PRESERVED: approved file left untouched.");
                return;
            }

            var rt = RenderShot(areaRoot, shot, k_Width, k_Height,
                (float)k_Width / k_Height);

            try
            {
                WritePng(rt, Path.Combine(outputFolder, fileName));
                log.AppendLine($"  {fileName,-34}  {note}");
            }
            finally
            {
                Release(ref rt);
            }
        }

        /// <summary>Renders a composite panel at 2x for downsampling by the RawImage.</summary>
        static RenderTexture RenderPanel(Transform areaRoot, Shot shot)
        {
            return RenderShot(areaRoot, shot, k_PanelWidth * 2, k_PanelHeight * 2, k_PanelAspect);
        }

        /// <summary>
        /// Renders one shot, positioned and aimed in the area root's own local frame so the
        /// placements follow the authored geometry rather than hard-coded world coordinates.
        /// </summary>
        static RenderTexture RenderShot(Transform areaRoot, Shot shot, int width, int height,
            float aspect)
        {
            var position = areaRoot.TransformPoint(shot.localPosition);
            var lookAt = areaRoot.TransformPoint(shot.localLookAt);

            var direction = lookAt - position;
            var rotation = direction.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(direction.normalized, Vector3.up)
                : areaRoot.rotation;

            return Render(position, rotation, camera =>
            {
                camera.orthographic = false;
                camera.fieldOfView = shot.fieldOfView;
                camera.aspect = aspect;
                camera.clearFlags = CameraClearFlags.Skybox;
            }, width, height);
        }

        /// <summary>Renders the composite sheet's canvas with an orthographic camera.</summary>
        static void RenderOrthographic(Vector3 position, float orthographicSize, string path)
        {
            var rt = Render(position, Quaternion.identity, camera =>
            {
                camera.orthographic = true;
                camera.orthographicSize = orthographicSize;
                camera.aspect = (float)k_Width / k_Height;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.078f, 0.086f, 0.106f);

                // Only the sheet, which sits 10 units ahead, can be in range.
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 20f;
            }, k_Width, k_Height);

            try
            {
                WritePng(rt, path);
            }
            finally
            {
                Release(ref rt);
            }
        }

        /// <summary>
        /// Renders from a temporary camera into a fresh RenderTexture.
        ///
        /// The camera carries HideFlags.DontSave and is destroyed immediately afterwards, so it
        /// cannot survive into any saved asset even if this method throws.
        /// </summary>
        static RenderTexture Render(Vector3 position, Quaternion rotation,
            Action<Camera> configure, int width, int height)
        {
            var go = new GameObject(k_CameraName) { hideFlags = HideFlags.DontSave };
            RenderTexture rt = null;

            try
            {
                go.transform.SetPositionAndRotation(position, rotation);

                var camera = go.AddComponent<Camera>();
                camera.cullingMask = ~0;
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 500f;
                camera.allowHDR = true;
                configure(camera);

                var urp = go.GetComponent<UniversalAdditionalCameraData>()
                          ?? go.AddComponent<UniversalAdditionalCameraData>();
                urp.renderShadows = true;
                urp.renderPostProcessing = false;

                rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB)
                {
                    antiAliasing = 4,
                };
                rt.Create();

                if (!TrySubmitRenderRequest(camera, rt))
                {
                    camera.targetTexture = rt;
                    camera.Render();
                    camera.targetTexture = null;
                }

                var result = rt;
                rt = null;      // ownership passes to the caller
                return result;
            }
            finally
            {
                Release(ref rt);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static void WritePng(RenderTexture rt, string path)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D texture = null;

            try
            {
                texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                texture.Apply();

                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;

                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// URP's explicit single-camera render request. Preferred over Camera.Render(), which
        /// URP only supports through a compatibility path. Falls back if unavailable.
        /// </summary>
        static bool TrySubmitRenderRequest(Camera camera, RenderTexture destination)
        {
            try
            {
                var request = new UniversalRenderPipeline.SingleCameraRequest
                {
                    destination = destination,
                };

                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                    return false;

                RenderPipeline.SubmitRenderRequest(camera, request);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[IKEA_EEG] URP render request unavailable ({e.GetType().Name}); " +
                                 "falling back to Camera.Render().");
                return false;
            }
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null)
                return;

            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            rt = null;
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds an area root by name, including inactive objects. The areas are nested under an
        /// environment root, so this walks every transform rather than using GameObject.Find.
        /// </summary>
        static Transform FindRoot(string name)
        {
            foreach (var transform in UnityEngine.Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (transform != null && transform.name == name)
                    return transform;
            }

            return null;
        }

        /// <summary>The developer navigation panel is researcher-only and never in a capture.</summary>
        static void HideDeveloperPanel()
        {
            foreach (var nav in UnityEngine.Object.FindObjectsByType<DeveloperNavigation>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var panel = typeof(DeveloperNavigation)
                    .GetField("m_Panel", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(nav) as GameObject;

                if (panel != null && panel.activeSelf)
                    panel.SetActive(false);
            }
        }

        static void DeleteTempFolder()
        {
            if (AssetDatabase.IsValidFolder(k_TempFolderPath))
                AssetDatabase.DeleteAsset(k_TempFolderPath);
        }

        static string HashFile(string assetPath)
        {
            var full = Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, assetPath);

            if (!File.Exists(full))
                return "<missing>";

            using var md5 = System.Security.Cryptography.MD5.Create();
            using var stream = File.OpenRead(full);
            return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty)
                .ToLowerInvariant();
        }
    }
}
