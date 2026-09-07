using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using IkeaEeg.Audio;
using IkeaEeg.Core;
using IkeaEeg.Data;
using IkeaEeg.Interaction;
using IkeaEeg.Localization;
using IkeaEeg.Memory;
using IkeaEeg.UI;
using IkeaEeg.XR;

namespace IkeaEeg.Experiment
{
    /// <summary>
    /// The central authority of the experiment.
    ///
    /// It owns the state machine, owns the ONLY coroutines that advance the protocol, and
    /// tells every other system what to do. No other script schedules a delay, decides a
    /// transition or logs a lifecycle event. If you want to know when something happens,
    /// this file is the only place you need to read.
    ///
    /// Flow (each arrow is either a timed step or a participant button press):
    ///
    ///   Idle --Start--> AreaAInstructions -> WordEncoding -> [beep] -> ImmediateRecall
    ///        -> ReadyForAreaB --Enter Showroom-->
    ///
    ///           +-- ChairInstruction -> ChairSelection -> ChairTrialFeedback --+
    ///           |                                                              |
    ///           +---- ChairInterTrialInterval &lt;--- (repeat x chairTrialsPerRun)
    ///
    ///        -> ReadyForAreaC --Exit--> DelayedRecall -> Results
    ///        --Restart--> a NEW session with a NEW seed   |   --End--> Ended
    ///
    /// The chair block is a LOOP inside Area B: the participant never leaves the showroom
    /// between chair trials, and no button press is required between them — only a configurable
    /// feedback period and inter-trial interval.
    /// </summary>
    [DisallowMultipleComponent]
    public class ExperimentManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] ExperimentConfig m_Config;

        [Header("Systems")]
        [SerializeField] EventLogger m_Logger;
        [SerializeField] ExperimentUIController m_UI;
        [SerializeField] XRRigTeleporter m_Teleporter;
        [SerializeField] ChairSelectionTask m_ChairTask;
        [SerializeField] VoiceRecallManager m_Voice;
        [SerializeField] ExperimentAudio m_Audio;

        [Tooltip("Area 0 practice objects. Selections are diagnostic only and never enter any " +
                 "cognitive metric.")]
        [SerializeField] List<PracticeObject> m_PracticeObjects = new List<PracticeObject>();

        [Tooltip("Developer-only area navigation. Never used by a participant; any jump made " +
                 "from it marks the run DEVELOPER_INTERRUPTED.")]
        [SerializeField] DeveloperNavigation m_DeveloperNavigation;

        [Tooltip("Optional. Only used to report the marker outlet's state into the session " +
                 "record; the sink itself is driven by the EventBus.")]
        [SerializeField] LslMarkerSink m_LslSink;

        [Tooltip("Optional. Owns the raw EEG receiver and writes one EEG file per run. When " +
                 "absent, or when no amplifier is connected, the behavioural experiment runs " +
                 "exactly as it always has.")]
        [SerializeField] EegRunRecorder m_EegRecorder;

        [Header("Behaviour")]
        [Tooltip("Begin the session automatically when the scene starts (recommended). " +
                 "SESSION_START is logged at that moment; the trial still waits for Start.")]
        [SerializeField] bool m_AutoBeginSessionOnStart = true;

        [Tooltip("How long to wait for the headset to report a tracked pose before giving up " +
                 "and aligning anyway.")]
        [SerializeField] float m_HeadPoseTimeoutSeconds = 3f;

        [Tooltip("Refuse to start a trial when auditory stimuli cannot be delivered. Turn OFF " +
                 "only to debug the flow without audio — data collected that way is not valid.")]
        [SerializeField] bool m_BlockTrialWhenAudioUnavailable = true;

        ExperimentState m_State = ExperimentState.Idle;
        Coroutine m_Flow;

        /// <summary>Set when stimuli cannot be delivered; START is refused while true.</summary>
        bool m_StimulusBlocked;
        string m_StimulusBlockReason = string.Empty;

        readonly List<string> m_TrialWords = new List<string>();

        // ---- Recognition protocol state ---------------------------------------------------
        // Kept per phase rather than in one list: the immediate and delayed phases are separate
        // measurements over overlapping words, and merging them would make an item's outcome
        // ambiguous about which phase produced it.
        readonly List<string> m_RecognitionTargets = new List<string>();
        readonly List<RecognitionItem> m_ImmediateRecognitionItems = new List<RecognitionItem>();
        readonly List<RecognitionItem> m_DelayedRecognitionItems = new List<RecognitionItem>();

        /// <summary>The answer for the item currently on screen, or None while waiting.</summary>
        RecognitionResponse m_PendingRecognitionResponse = RecognitionResponse.None;

        [Tooltip("TEMPORARY DIAGNOSTIC. Logs [RecognitionTiming] lines showing how long the gap " +
                 "between an accepted response and the next stimulus actually is. Measurement " +
                 "only — it changes no timing and no experimental behaviour.")]
        [SerializeField] bool m_LogRecognitionTiming = true;

        /// <summary>Realtime seconds when the response wait exited. -1 when not applicable.</summary>
        double m_RecognitionWaitExitedRealtime = -1d;

        /// <summary>Realtime seconds when the response was first seen by the wait loop.</summary>
        double m_RecognitionResponseAcceptedRealtime = -1d;

        // ---- Developer QA cheatsheet (NOT participant functionality) ----------------------
        //
        // A visual aid for checking, in the headset, that the sequence and the response buttons
        // behave: it names the current item TARGET or LURE and states which answer is correct.
        //
        // IT TELLS THE WEARER THE ANSWER. That is why it is gated by an explicit config flag
        // that defaults to FALSE, starts hidden even when the gate is open, and resets OFF at
        // every phase boundary. Nothing here reads or writes a response, an outcome, a time or
        // an event — the toggle owns one bool and one label, and that is the whole mechanism.

        /// <summary>True while the developer overlay is toggled on. Never true when gated off.</summary>
        bool m_DeveloperCheatsheetOn;

        /// <summary>
        /// The current item's QA line, rebuilt at each onset and shown only while the overlay is
        /// on. Held rather than recomputed so toggling mid-item cannot ask an item that has
        /// already gone.
        /// </summary>
        string m_DeveloperCheatsheetText = string.Empty;

        /// <summary>
        /// The toggle input: the RIGHT controller's B button (secondaryButton).
        ///
        /// WHY B. The audit of what this project already uses: the index and grip triggers are
        /// Select (every participant response, every chair, every UI button); both thumbstick
        /// CLICKS held together for 2 s open developer navigation; the thumbsticks themselves
        /// still drive snap/continuous turn. The right controller's A button carries the XRI
        /// asset's own 'Jump' binding — inert here because free locomotion is disabled, but a
        /// real binding that would collide if locomotion were ever re-enabled. B is bound to
        /// nothing at all, in the XRI asset or in any script in this project.
        ///
        /// Created HERE, in code, at run time — the shared XRI input asset, the Input System
        /// configuration and the controller prefabs are untouched. This is the same approach
        /// DeveloperNavigation already uses, for the same reason.
        /// </summary>
        UnityEngine.InputSystem.InputAction m_DeveloperCheatsheetToggle;

        /// <summary>Edge detection, so holding B does not strobe the overlay every frame.</summary>
        bool m_CheatsheetTogglePressedLastFrame;

        /// <summary>True when the config gate allows the overlay to be toggled at all.</summary>
        bool developerCheatsheetGateOpen =>
            m_Config != null && m_Config.enableRecognitionDeveloperCheatsheet;

        /// <summary>Overlay state, for the self test.</summary>
        public bool developerCheatsheetOn => m_DeveloperCheatsheetOn;

        [Tooltip("The SEEN BEFORE / NOT SEEN BEFORE response panel. Resolved from the scene " +
                 "when unassigned; the Recognition protocol cannot run without it.")]
        [SerializeField] Interaction.RecognitionResponsePanel m_RecognitionPanel;

        /// <summary>Scene-builder wiring for the recognition response panel.</summary>
        public void SetRecognitionPanel(Interaction.RecognitionResponsePanel panel)
        {
            m_RecognitionPanel = panel;
        }

        public IReadOnlyList<RecognitionItem> immediateRecognitionItems =>
            m_ImmediateRecognitionItems;

        public IReadOnlyList<RecognitionItem> delayedRecognitionItems =>
            m_DelayedRecognitionItems;
        readonly TrialResult m_Result = new TrialResult();
        readonly SessionResults m_SessionResults = new SessionResults();

        // ---- Chair block ----------------------------------------------------------------
        readonly List<ChairTrialPlan> m_ChairPlans = new List<ChairTrialPlan>();

        /// <summary>False when the legacy single fixed target is in use instead of the generator.</summary>
        bool m_UsingGeneratedTrials = true;

        /// <summary>The chair trial currently being run, or null between trials.</summary>
        ChairTrialResult m_CurrentChairTrial;

        /// <summary>Set by the selection callback; the chair-trial coroutine waits on it.</summary>
        bool m_ChairSelectionReceived;

        /// <summary>
        /// True once the general Area B instructions have been read and acknowledged in this
        /// block. Cleared only by a restart or an explicit block reset — trials 2+ never see the
        /// instruction screen again.
        /// </summary>
        bool m_AreaBInstructionsShown;

        /// <summary>Set by the READY button; the instruction coroutine waits on it.</summary>
        bool m_AreaBReadyPressed;

        // ---- Randomisation ---------------------------------------------------------------
        long m_SessionSeed;
        bool m_SeedIsReplay;

        // ---- Area 0 ----------------------------------------------------------------------
        /// <summary>How many practice selections were made. Diagnostic only, never scored.</summary>
        int m_PracticeSelectionCount;

        /// <summary>Session-clock time at which Area 0 was entered.</summary>
        double m_FamiliarizationStartSeconds;

        /// <summary>COMPLETED / SKIPPED / empty until Area 0 is left.</summary>
        string m_FamiliarizationOutcome = string.Empty;

        /// <summary>The practice object the participant is currently asked to select.</summary>
        PracticeObject m_PracticeTarget;

        /// <summary>
        /// THE canonical practice target colour, chosen exactly once per Area 0 entry.
        ///
        /// SINGLE SOURCE OF TRUTH. The written prompt, the localized colour label, the narration
        /// clip, the log line and the correctness comparison are all derived from THIS value.
        /// Nothing else picks a practice colour, and no second variable holds one — which is
        /// what previously let the voice ask for one colour while the panel named another.
        /// </summary>
        ChairColor m_PracticeTargetColor;

        /// <summary>False before a practice target has been chosen for this Area 0 visit.</summary>
        bool m_HasPracticeTarget;

        /// <summary>The spoken familiarization sequence, so a replay can cancel one in flight.</summary>
        Coroutine m_NarrationFlow;

        /// <summary>
        /// Incremented every time participant narration is cancelled.
        ///
        /// A coroutine parked in a WaitForSeconds cannot be stopped mid-yield by anything except
        /// StopCoroutine, and StopCoroutine on a null handle is a silent no-op. The epoch is the
        /// belt to that braces: a narration sequence captures the epoch it started in and abandons
        /// itself the moment the epoch moves, so a cancelled sequence can never resume and speak
        /// the next segment into the following room.
        /// </summary>
        int m_NarrationEpoch;

        /// <summary>The area the participant is currently in, so a CHANGE can be detected.</summary>
        ExperimentArea m_CurrentArea = ExperimentArea.AreaA;
        bool m_CurrentAreaKnown;

        /// <summary>
        /// True once the run has been ABORTED. One-way for that run: no participant-facing
        /// process may start, resume or restart from this state.
        /// </summary>
        bool m_RunAborted;

        /// <summary>False when the selected language has no verbal-memory word set.</summary>
        bool m_WordSetLanguageOk;

        /// <summary>
        /// True once END has finalised the session. One-way: nothing may start a new run, and
        /// the current run may not be finalised a second time.
        /// </summary>
        bool m_SessionEnded;

        /// <summary>True once the requested practice object has been selected (PHASE 1 done).</summary>
        bool m_PracticeSucceeded;

        /// <summary>
        /// True once a DIFFERENT-coloured object has been selected after the requested one
        /// (PHASE 2 done).
        ///
        /// The two phases test different things and therefore have different pass conditions.
        /// Phase 1 asks "can you follow a specific instruction?"; phase 2 asks "can you operate
        /// the controller again, on your own choice of object?". Collapsing them into one
        /// counter is what produced the bug this fixes: the second selection was still being
        /// judged against the first phase's requested colour, so choosing any other colour —
        /// exactly what the participant had just been asked to do — was called incorrect.
        /// </summary>
        bool m_PracticeSecondSelectionDone;

        public ExperimentState state => m_State;
        public TrialResult currentResult => m_Result;
        public SessionResults sessionResults => m_SessionResults;

        /// <summary>Practice selections made in Area 0. Diagnostic only — never a metric.</summary>
        public int practiceSelectionCount => m_PracticeSelectionCount;

        /// <summary>COMPLETED / SKIPPED, or empty while Area 0 has not been left yet.</summary>
        public string familiarizationOutcome => m_FamiliarizationOutcome;

        /// <summary>True once the requested practice object was selected. Never a score.</summary>
        public bool practiceSucceeded => m_PracticeSucceeded;

        /// <summary>
        /// True when the participant has practised enough to be shown the "ready" emphasis:
        /// at least the configured number of selections AND the requested colour. START
        /// EXPERIMENT is available whether or not this is true.
        /// </summary>
        public bool practiceReady =>
            m_PracticeSucceeded &&
            m_PracticeSecondSelectionDone &&
            m_PracticeSelectionCount >= (m_Config != null
                ? Mathf.Max(1, m_Config.practiceMinimumSelections)
                : 2);

        /// <summary>True once the second, free-choice selection has been made.</summary>
        public bool practiceSecondSelectionDone => m_PracticeSecondSelectionDone;

        /// <summary>
        /// Which familiarization step the participant is on. Derived from the phase flags so it
        /// can never disagree with them.
        /// </summary>
        public PracticePhase practicePhase =>
            !m_PracticeSucceeded ? PracticePhase.RequestedColorSelection
            : !practiceReady ? PracticePhase.DifferentColorSelection
            : PracticePhase.Complete;

        /// <summary>The practice object currently requested, or null outside Area 0.</summary>
        public PracticeObject practiceTarget => m_PracticeTarget;

        /// <summary>THE canonical requested practice colour. Everything shown or spoken derives from it.</summary>
        public ChairColor practiceTargetColor => m_PracticeTargetColor;

        /// <summary>True once a practice target has been chosen for this Area 0 visit.</summary>
        public bool hasPracticeTarget => m_HasPracticeTarget;

        /// <summary>True once ABORT has stopped this run. Nothing participant-facing may resume.</summary>
        public bool runAborted => m_RunAborted;
        public IReadOnlyList<string> trialWords => m_TrialWords;

        /// <summary>Seed driving this session's randomisation. Written to every CSV row.</summary>
        public long sessionSeed => m_SessionSeed;

        /// <summary>The generated chair trials for this run, in presentation order.</summary>
        public IReadOnlyList<ChairTrialPlan> chairPlans => m_ChairPlans;

        // ---------------------------------------------------------------------------------
        // Unity lifecycle
        // ---------------------------------------------------------------------------------

        void Awake()
        {
            if (m_Logger == null)
                m_Logger = FindAnyObjectByType<EventLogger>();

            // Bound to the standard XR controller layout so it works with whatever controller
            // profile OpenXR has active, without naming a device. Two bindings because runtimes
            // differ in how they surface the face button.
            m_DeveloperCheatsheetToggle = new UnityEngine.InputSystem.InputAction(
                "IKEA_DevCheatsheet_Toggle",
                UnityEngine.InputSystem.InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");

            // Called through the extension class explicitly rather than via a `using`, so this
            // file does not pull the whole Input System namespace in beside the experiment types.
            UnityEngine.InputSystem.InputActionSetupExtensions.AddBinding(
                m_DeveloperCheatsheetToggle, "<XRController>{RightHand}/{SecondaryButton}");
        }

        void OnEnable()
        {
            if (m_UI != null)
            {
                m_UI.languageSelected += OnLanguageSelected;
                m_UI.startExperimentPressed += OnStartExperimentPressed;
                m_UI.skipIntroPressed += OnSkipIntroPressed;
                m_UI.replayInstructionsPressed += OnReplayInstructionsPressed;
                m_UI.startPressed += OnStartPressed;
                m_UI.enterAreaBPressed += OnEnterAreaBPressed;
                m_UI.readyPressed += OnReadyPressed;
                m_UI.exitToAreaCPressed += OnExitToAreaCPressed;
                m_UI.restartPressed += OnRestartPressed;
                m_UI.newTrialPressed += OnNewTrialPressed;
                m_UI.endPressed += OnEndPressed;
                m_UI.recenterPressed += OnRecenterPressed;
                m_UI.recheckAudioPressed += OnRecheckAudioPressed;
            }

            // THE RECOGNITION RESPONSE PATH.
            //
            // THIS IS THE BUG THAT MADE EVERY ANSWER APPEAR TO "HANG" FOR SECONDS. The
            // subscription used to be made in Bind(), which is called by the scene BUILDER in
            // the Editor. C# events are not serialized, so at run time this manager was never
            // listening: OnRecognitionResponse never fired, m_PendingRecognitionResponse stayed
            // None, and the item loop therefore ran its FULL recognitionResponseTimeoutSeconds
            // (15 s) on every single item. The delay the participant experienced was simply
            // whatever remained of those 15 s after they pressed.
            //
            // It also meant every answer was recorded as RECOGNITION_ITEM_TIMEOUT / NO_RESPONSE.
            //
            // It belongs here, with every other runtime subscription in this class.
            if (m_RecognitionPanel == null)
                m_RecognitionPanel = FindAnyObjectByType<Interaction.RecognitionResponsePanel>();

            if (m_RecognitionPanel != null)
            {
                // Removed first so a re-enable cannot double-subscribe and count one press twice.
                m_RecognitionPanel.responseSelected -= OnRecognitionResponse;
                m_RecognitionPanel.responseSelected += OnRecognitionResponse;
            }

            if (m_ChairTask != null)
            {
                m_ChairTask.selectionMade += OnChairSelectionMade;
                m_ChairTask.chairHovered += OnChairHovered;
            }

            if (m_Voice != null)
                m_Voice.recordingCompleted += OnRecordingCompleted;

            foreach (var practice in m_PracticeObjects)
            {
                if (practice != null)
                    practice.practiceSelected += OnPracticeObjectSelected;
            }

            if (m_DeveloperNavigation != null)
            {
                m_DeveloperNavigation.menuOpened += OnDeveloperMenuOpened;
                m_DeveloperNavigation.menuClosed += OnDeveloperMenuClosed;
            }

            if (m_UI != null)
            {
                m_UI.developerGoToArea += OnDeveloperAreaJump;
                m_UI.developerCloseMenu += OnDeveloperCloseRequested;
                m_UI.developerAbortSession += OnDeveloperAbortSession;
                m_UI.developerReturnToLanguage += OnDeveloperReturnToLanguage;
            }

            // Enabled unconditionally; the GATE is checked when the press is read, not here, so
            // a config change between runs takes effect without re-enabling anything. With the
            // gate closed the press is read and discarded.
            m_DeveloperCheatsheetToggle?.Enable();
        }

        void Update()
        {
            PollDeveloperCheatsheetToggle();
        }

        /// <summary>
        /// Reads the B button and flips the overlay. Runs every frame and does nothing else.
        ///
        /// WHAT THIS METHOD CANNOT DO, by construction: it never touches
        /// m_PendingRecognitionResponse, never calls OnRecognitionResponse, never reaches
        /// m_RecognitionPanel, never plays a cue and never logs. The only state it writes is one
        /// bool and one label. A press with the gate closed, or outside a recognition item, is
        /// read and thrown away.
        ///
        /// RISING EDGE ONLY, so holding the button does not strobe the overlay at frame rate.
        /// </summary>
        void PollDeveloperCheatsheetToggle()
        {
            var pressed = m_DeveloperCheatsheetToggle != null &&
                          m_DeveloperCheatsheetToggle.enabled &&
                          m_DeveloperCheatsheetToggle.IsPressed();

            var rising = pressed && !m_CheatsheetTogglePressedLastFrame;
            m_CheatsheetTogglePressedLastFrame = pressed;

            if (!rising)
                return;

            // THE GATE. Not a UI-visibility check — with the flag off the overlay cannot be
            // switched on at all, and the press does nothing.
            if (!developerCheatsheetGateOpen)
                return;

            // And only while an item is actually on screen: there is nothing to reveal
            // otherwise, and an overlay that could be raised in Area B or on the results screen
            // would be a way to leave it on by accident.
            if (!InActiveRecognitionItem())
                return;

            SetDeveloperCheatsheetVisible(!m_DeveloperCheatsheetOn);
        }

        /// <summary>
        /// True only while a recognition item is being presented and answered.
        ///
        /// The state check is what keeps the overlay out of Area B, the results screen, the
        /// encoding phase and FreeRecall — FreeRecall never enters either recognition state.
        /// </summary>
        bool InActiveRecognitionItem()
        {
            return m_DeveloperCheatsheetText.Length > 0 &&
                   (m_State == ExperimentState.ImmediateRecognition ||
                    m_State == ExperimentState.DelayedRecognition);
        }

        /// <summary>Applies the overlay's visibility to the UI. The single place that does.</summary>
        void SetDeveloperCheatsheetVisible(bool visible)
        {
            m_DeveloperCheatsheetOn = visible && developerCheatsheetGateOpen;

            if (m_UI == null)
                return;

            if (m_DeveloperCheatsheetOn)
                m_UI.SetDeveloperCheatsheet(m_DeveloperCheatsheetText);

            m_UI.ShowDeveloperCheatsheet(m_DeveloperCheatsheetOn);
        }

        /// <summary>
        /// Takes the overlay down and forgets the item, at a phase boundary or on leaving
        /// recognition.
        ///
        /// RESET OFF AT EVERY PHASE BOUNDARY — the safer of the two options the brief offered.
        /// An overlay that survived from immediate into delayed recognition would be one an
        /// operator could leave on without ever choosing to, and it would be showing a
        /// participant the answers during the phase this protocol most depends on. Two
        /// deliberate presses per session is a small price for that.
        /// </summary>
        void ResetDeveloperCheatsheet()
        {
            m_DeveloperCheatsheetOn = false;
            m_DeveloperCheatsheetText = string.Empty;

            if (m_UI != null)
                m_UI.ShowDeveloperCheatsheet(false);
        }

        void OnDisable()
        {
            m_DeveloperCheatsheetToggle?.Disable();
            m_CheatsheetTogglePressedLastFrame = false;

            if (m_RecognitionPanel != null)
                m_RecognitionPanel.responseSelected -= OnRecognitionResponse;

            if (m_UI != null)
            {
                m_UI.languageSelected -= OnLanguageSelected;
                m_UI.startExperimentPressed -= OnStartExperimentPressed;
                m_UI.skipIntroPressed -= OnSkipIntroPressed;
                m_UI.replayInstructionsPressed -= OnReplayInstructionsPressed;
                m_UI.startPressed -= OnStartPressed;
                m_UI.enterAreaBPressed -= OnEnterAreaBPressed;
                m_UI.readyPressed -= OnReadyPressed;
                m_UI.exitToAreaCPressed -= OnExitToAreaCPressed;
                m_UI.restartPressed -= OnRestartPressed;
                m_UI.newTrialPressed -= OnNewTrialPressed;
                m_UI.endPressed -= OnEndPressed;
                m_UI.recenterPressed -= OnRecenterPressed;
                m_UI.recheckAudioPressed -= OnRecheckAudioPressed;
            }

            if (m_ChairTask != null)
            {
                m_ChairTask.selectionMade -= OnChairSelectionMade;
                m_ChairTask.chairHovered -= OnChairHovered;
            }

            if (m_Voice != null)
                m_Voice.recordingCompleted -= OnRecordingCompleted;

            foreach (var practice in m_PracticeObjects)
            {
                if (practice != null)
                    practice.practiceSelected -= OnPracticeObjectSelected;
            }

            if (m_DeveloperNavigation != null)
            {
                m_DeveloperNavigation.menuOpened -= OnDeveloperMenuOpened;
                m_DeveloperNavigation.menuClosed -= OnDeveloperMenuClosed;
            }

            if (m_UI != null)
            {
                m_UI.developerGoToArea -= OnDeveloperAreaJump;
                m_UI.developerCloseMenu -= OnDeveloperCloseRequested;
                m_UI.developerAbortSession -= OnDeveloperAbortSession;
                m_UI.developerReturnToLanguage -= OnDeveloperReturnToLanguage;
            }
        }

        IEnumerator Start()
        {
            // Wait for the XR camera to report a real tracked pose before doing anything
            // positional. One frame was not enough: the first teleport could run against a
            // head pose still at the origin, which is why start-up orientation was sometimes
            // rotated. The Recenter button remains available if it still lands off-axis.
            yield return WaitForValidHeadPose();

            // The session must begin BEFORE any validation runs: sinks are only initialised
            // at SESSION_START, so anything logged earlier would be silently dropped and the
            // hardware warnings would never reach the CSV.
            if (m_AutoBeginSessionOnStart && m_Logger != null)
                BeginSession(ChooseSeed(), isReplay: false);

            PrintSessionBanner();
            LogHardwareDiagnostics();
            ValidateSetup();

            if (m_Voice != null)
                yield return m_Voice.RunStartupMicrophoneTest();

            PrepareTrial();

            // LANGUAGE FIRST. Nothing cognitive — and nothing with words in it — is shown until
            // the participant has chosen the language every later instruction will be given in.
            EnterLanguageSelection("session start");

            // Decide whether the trial may run at all. Done last so the Area A UI already
            // exists to display the warning on.
            EvaluateStimulusReadiness(logToConsole: true);
        }

        /// <summary>
        /// Polls until the XR camera has moved away from its untracked origin pose, or until
        /// a timeout. Without this the first orientation align can use a stale pose.
        /// </summary>
        IEnumerator WaitForValidHeadPose()
        {
            var camera = m_Teleporter != null ? Camera.main : null;
            var deadline = Time.realtimeSinceStartup + m_HeadPoseTimeoutSeconds;

            // A tracked headset reports a head height well above the rig origin almost
            // immediately; an untracked one sits at exactly zero.
            while (Time.realtimeSinceStartup < deadline)
            {
                if (camera != null && camera.transform.localPosition.sqrMagnitude > 0.0001f)
                    break;

                yield return null;
            }

            // A couple of extra frames so the pose has settled rather than being the first
            // sample after tracking acquisition.
            yield return null;
            yield return null;
        }

        // ---------------------------------------------------------------------------------
        // Session + randomisation
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Picks the seed for the next session.
        ///
        /// A session's entire stimulus sequence is a pure function of this number, so it is the
        /// one value that has to survive into the data file. It goes into SESSION_START, into
        /// every CSV row, into the summary CSV and into the researcher summary.
        /// </summary>
        long ChooseSeed()
        {
            if (m_Config != null && m_Config.seedMode == SeedMode.FixedSeed)
                return m_Config.fixedSeed;

            // Wall clock plus a GUID: unique per session even if two are started in the same
            // tick, and positive so it reads cleanly in a spreadsheet.
            var ticks = DateTime.Now.Ticks;
            var noise = Guid.NewGuid().GetHashCode();
            var seed = Math.Abs(ticks ^ ((long)noise << 21));

            return seed == 0 ? 1L : seed;
        }

        /// <summary>
        /// Opens a session with a specific seed: seed first, THEN BeginSession, so that
        /// SESSION_START itself already carries the seed it belongs to.
        /// </summary>
        void BeginSession(long seed, bool isReplay)
        {
            if (m_Logger == null)
                return;

            m_SessionSeed = seed;
            m_SeedIsReplay = isReplay;
            m_Logger.randomizationSeed = seed;
            m_Logger.BeginSession();

            m_SessionResults.Reset();
            m_SessionResults.sessionId = m_Logger.sessionId;
            m_SessionResults.randomizationSeed = seed;
            m_SessionResults.isSeedReplay = isReplay;
            m_SessionResults.sessionDirectory = m_Logger.sessionDirectory;

            if (m_Config != null)
            {
                m_SessionResults.wordSetId = m_Config.GetTrialWordSetId();
                m_SessionResults.protocolDescription = m_Config.DescribeProtocol();
            }

            // Raw EEG for THIS run, into this run's own folder. A no-op when no EEG component
            // is present or no amplifier is connected — the behavioural session is never
            // blocked, delayed or altered by the state of the EEG path.
            m_EegRecorder?.BeginRun(m_Logger.sessionDirectory, m_Logger.experimentSessionId,
                m_Logger.sessionId, m_Logger.runIndex,
                ExperimentLocalization.languageCode);

            Debug.Log($"[IKEA_EEG] Randomization seed for this session: {seed}" +
                      (isReplay ? "   (REPLAY of a previous seed)" : string.Empty));

            Log(EventTypes.StateChanged, e => e.notes =
                $"session_opened; seed={seed.ToString(CultureInfo.InvariantCulture)}; " +
                $"seed_is_replay={(isReplay ? "TRUE" : "FALSE")}; " +
                $"seed_mode={(m_Config != null ? m_Config.seedMode.ToString() : "unknown")}; " +
                $"{(m_Config != null ? m_Config.DescribeProtocol() : string.Empty)}");

            LogMarkerTransportStatus();
        }

        /// <summary>
        /// Records the state of the LSL marker outlet as part of the session.
        ///
        /// The sink cannot log this itself: sinks are initialised from inside BeginSession,
        /// before the logger is ready to accept events. Doing it here also keeps sinks strictly
        /// passive — nothing that receives events also produces them.
        /// </summary>
        void LogMarkerTransportStatus()
        {
            if (m_LslSink == null)
                m_LslSink = FindAnyObjectByType<LslMarkerSink>();

            if (m_LslSink == null)
            {
                Log(EventTypes.LslStatus, e =>
                {
                    e.correct = "FALSE";
                    e.notes = "lsl_state=UNAVAILABLE; detail=no LslMarkerSink component in the scene";
                });

                Debug.LogWarning("[IKEA_EEG] LSL_UNAVAILABLE — no LslMarkerSink in the scene. " +
                                 "The behavioural session is unaffected.");
                return;
            }

            var status = m_LslSink.DescribeStatus();
            var active = m_LslSink.isTransmitting;

            Log(EventTypes.LslStatus, e =>
            {
                e.correct = active ? "TRUE" : "FALSE";
                e.objectId = m_LslSink.streamName;
                e.notes = status;
            });

            if (!active)
                Debug.LogWarning($"[IKEA_EEG] LSL_UNAVAILABLE — {status}");
        }

        void PrintSessionBanner()
        {
            if (m_Logger == null)
                return;

            var mic = m_Voice != null && !string.IsNullOrEmpty(m_Voice.activeDevice)
                ? m_Voice.activeDevice
                : "NONE";

            var micNote = m_Voice != null && m_Voice.selectionIsFallback
                ? "   (FALLBACK — not the intended device)"
                : string.Empty;

            var audioFolder = m_Voice != null ? m_Voice.RecordingFolder() : "n/a";

            Debug.Log(
                "\n==================== IKEA_EEG SESSION ====================\n" +
                $"SESSION ID:\n    {m_Logger.sessionId}\n\n" +
                $"RANDOMIZATION SEED:\n    {m_SessionSeed}" +
                (m_SeedIsReplay ? "   (REPLAY)" : string.Empty) + "\n\n" +
                $"PROTOCOL (prototype defaults, see ExperimentConfig):\n    " +
                $"{(m_Config != null ? m_Config.DescribeProtocol() : "no config")}\n\n" +
                $"MICROPHONE USED:\n    {mic}{micNote}\n\n" +
                $"AUDIO RECORDING FOLDER:\n    {audioFolder}\n\n" +
                $"CSV FOLDER:\n    {m_Logger.sessionDirectory}\n" +
                "==========================================================\n");
        }

        /// <summary>
        /// Records the state of the two hardware paths the experiment depends on — audio out
        /// and microphone in — as part of the session record, so a silent beep or a wrong
        /// microphone is visible in the data rather than only in a Console someone may not
        /// have been watching.
        /// </summary>
        void LogHardwareDiagnostics()
        {
            if (m_Audio != null)
            {
                var healthy = m_Audio.RunDiagnostics(logToConsole: true);
                Log(EventTypes.AudioDiagnostics, e =>
                    e.notes = $"audio_path_ok={(healthy ? "TRUE" : "FALSE")}; " +
                              m_Audio.diagnosticsSummary);
            }
            else
            {
                Log(EventTypes.Warning, e =>
                    e.notes = "no ExperimentAudio assigned: no auditory stimuli will be played");
            }

            LogSpokenClipInventory();

            if (m_Voice != null)
                m_Voice.LogDeviceSelection();
        }

        /// <summary>
        /// Reports every spoken-word clip that will actually be used this trial, from the same
        /// lookup the encoding loop uses. This is the check that would have caught a word
        /// whose recording was missing at runtime.
        /// </summary>
        void LogSpokenClipInventory()
        {
            if (m_Config == null || m_Config.wordList == null)
                return;

            var words = m_Config.GetTrialWords();
            var report = new System.Text.StringBuilder();
            report.AppendLine($"[IKEA_EEG] Spoken word inventory for set " +
                              $"'{m_Config.GetTrialWordSetName()}' ({words.Count} words):");

            if (m_Audio != null)
                report.AppendLine($"    playback voice: {m_Audio.DescribeSource()}");

            for (var i = 0; i < words.Count; i++)
            {
                var clip = m_Config.wordList.GetWordClip(m_Config.wordSetIndex, i);
                var usable = ExperimentAudio.IsClipUsable(clip, out var reason);
                var description = ExperimentAudio.DescribeClip(clip);

                report.AppendLine($"    [{i + 1}] {words[i],-10} {(usable ? "OK " : "BAD")} {description}" +
                                  (usable ? string.Empty : $"  <-- {reason}"));

                var index = i;
                var word = words[i];
                Log(EventTypes.SpokenClipInventory, e =>
                {
                    e.wordIndex = (index + 1).ToString(CultureInfo.InvariantCulture);
                    e.expectedWord = word;
                    e.objectId = clip != null ? clip.name : "NULL";
                    e.correct = usable ? "TRUE" : "FALSE";
                    e.notes = usable
                        ? description
                        : $"{description}; problem={reason}";
                });
            }

            Debug.Log(report.ToString().TrimEnd());
        }

        // ---------------------------------------------------------------------------------
        // Stimulus readiness gate
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Decides whether the auditory stimuli can actually be delivered, and blocks the
        /// trial if not.
        ///
        /// Two categories are reported separately because only one of them is ours to fix:
        ///   A. things Unity can see and we can fix (missing clip, muted listener, muted Game
        ///      view, no AudioListener);
        ///   B. Windows/Quest Link output routing, which Unity cannot inspect OR change —
        ///      no Unity API selects the OS playback endpoint. That one is always flagged as
        ///      unverifiable rather than assumed good.
        /// </summary>
        public bool EvaluateStimulusReadiness(bool logToConsole)
        {
            var problems = new List<string>();

            if (m_Audio == null)
            {
                problems.Add("no ExperimentAudio component");
            }
            else if (!m_Audio.RunDiagnostics(logToConsole))
            {
                problems.Add(m_Audio.diagnosticsSummary);
            }

            if (m_Config == null || m_Config.wordList == null)
            {
                problems.Add("no word list configured");
            }
            else
            {
                var words = m_Config.GetTrialWords();
                for (var i = 0; i < words.Count; i++)
                {
                    var clip = m_Config.wordList.GetWordClip(m_Config.wordSetIndex, i);
                    if (!ExperimentAudio.IsClipUsable(clip, out var reason))
                        problems.Add($"word {i + 1} '{words[i]}': {reason}");
                }
            }

            m_StimulusBlocked = problems.Count > 0 && m_BlockTrialWhenAudioUnavailable;
            m_StimulusBlockReason = string.Join("; ", problems);

            if (problems.Count > 0)
            {
                Debug.LogError($"[IKEA_EEG] AUDITORY STIMULI CANNOT BE DELIVERED — " +
                               $"{(m_StimulusBlocked ? "the trial is BLOCKED" : "continuing anyway (blocking disabled)")}.\n" +
                               $"    {m_StimulusBlockReason}");

                Log(EventTypes.TrialBlocked, e =>
                {
                    e.correct = "FALSE";
                    e.notes = $"blocked={(m_StimulusBlocked ? "TRUE" : "FALSE")}; {m_StimulusBlockReason}";
                });
            }

            UpdateReadinessUI();
            return !m_StimulusBlocked;
        }

        void UpdateReadinessUI()
        {
            if (m_UI == null)
                return;

            if (m_StimulusBlocked)
            {
                m_UI.SetWarning("<color=#FF6666>" +
                                ExperimentLocalization.Get(LocKeys.AudioUnavailable) + "</color>");
                m_UI.SetAreaAStatus(m_StimulusBlockReason);
                m_UI.ShowStartButton(false);
                m_UI.ShowRecheckAudioButton(true);
            }
            else if (!m_WordSetLanguageOk && ExperimentLocalization.hasLanguage)
            {
                // The language has no verbal-memory word set. Blocked rather than run with
                // another language's words — see ApplyLanguageToWordList.
                m_UI.SetWarning("<color=#FF6666>" +
                                ExperimentLocalization.Get(LocKeys.NoWordSetForLanguage) +
                                "</color>");
                m_UI.ShowStartButton(false);
                m_UI.ShowRecheckAudioButton(false);
            }
            else
            {
                // No participant-facing warning while the audio path is healthy.
                //
                // An operator-facing audio-routing reminder used to be shown here, to the
                // participant, on every screen, for the whole session. Routing has been correct
                // throughout headset testing and the reminder is obsolete. The BLOCKING warning
                // above is unaffected: if stimuli genuinely cannot be delivered, that still
                // appears and START is still refused.
                m_UI.SetWarning(string.Empty);
                m_UI.ShowRecheckAudioButton(false);

                if (m_State == ExperimentState.Idle)
                    m_UI.ShowStartButton(true);
            }
        }

        void OnRecheckAudioPressed()
        {
            Log(EventTypes.AudioRecheck, e => e.notes = $"previous_reason={m_StimulusBlockReason}");

            if (EvaluateStimulusReadiness(logToConsole: true))
                Debug.Log("[IKEA_EEG] Audio re-check passed — the trial can now start.");
        }

        void OnRecenterPressed()
        {
            if (m_Teleporter == null)
                return;

            m_Teleporter.Recenter();
            Debug.Log($"[IKEA_EEG] Recentred in {m_Teleporter.currentArea}.");
        }

        // ---------------------------------------------------------------------------------
        // Validation
        // ---------------------------------------------------------------------------------

        void ValidateSetup()
        {
            if (m_Config == null)
            {
                Debug.LogError("[IKEA_EEG] ExperimentManager has no ExperimentConfig assigned. " +
                               "The trial cannot run.");
                return;
            }

            if (!m_Config.Validate(out var problem))
                Debug.LogError($"[IKEA_EEG] ExperimentConfig problem: {problem}");

            // Structural integrity of the whole word list: five ordered words per set, five
            // usable clips, no nulls, unique ids.
            if (m_Config.wordList != null &&
                !m_Config.wordList.ValidateAllSets(out var setProblem))
            {
                Debug.LogError($"[IKEA_EEG] WORD LIST PROBLEM: {setProblem}");
                Log(EventTypes.Warning, e => e.notes = $"word_list_invalid: {setProblem}");
            }

            // A missing spoken recording means the participant hears nothing at all for that
            // word — the encoding phase would silently deliver no stimulus. Fail loudly.
            if (m_Config.wordList != null &&
                !m_Config.wordList.ValidateClips(m_Config.wordSetIndex, out var clipProblem))
            {
                Debug.LogError($"[IKEA_EEG] SPOKEN WORD CLIPS MISSING: {clipProblem}");
                Log(EventTypes.Warning, e => e.notes = $"missing_spoken_clips: {clipProblem}");
            }

            if (m_ChairTask != null && !m_UsingGeneratedTrials)
            {
                // Legacy fixed-target mode only: with generated trials the target is set per
                // trial from the plan, and uniqueness is guaranteed by the generator.
                m_ChairTask.SetTarget(m_Config.targetChair);

                if (!m_ChairTask.ValidateTargetIsUnique(out var matches))
                {
                    Debug.LogError($"[IKEA_EEG] The target chair {m_Config.targetChair} matches " +
                                   $"{matches} chairs in Area B (expected exactly 1). " +
                                   "Correctness would be ambiguous — fix the chair attributes " +
                                   "or the target in ExperimentConfig.");
                }
            }

            if (m_ChairTask != null && m_ChairTask.slots.Count != m_ChairTask.chairs.Count)
            {
                Debug.LogError($"[IKEA_EEG] Area B has {m_ChairTask.slots.Count} chair slot(s) " +
                               $"for {m_ChairTask.chairs.Count} chair(s). They must match — " +
                               "re-run IKEA_EEG > Build Experiment Scene.");
            }

            if (m_Voice != null && !m_Voice.microphoneAvailable)
            {
                Log(EventTypes.Warning, e => e.notes =
                    "No microphone detected: recall audio will not be captured. " +
                    "Recall timestamps are still logged.");
            }
        }

        // ---------------------------------------------------------------------------------
        // State machine plumbing
        // ---------------------------------------------------------------------------------

        void SetState(ExperimentState next)
        {
            if (m_State == next)
                return;

            var previous = m_State;
            m_State = next;

            if (m_Logger != null)
                m_Logger.currentState = next.ToString();

            Log(EventTypes.StateChanged, e => e.notes = $"from={previous}; to={next}");
        }

        /// <summary>
        /// Records the room the participant is now in.
        ///
        /// THE SINGLE CHOKE POINT FOR ROOM-SCOPED AUDIO. Every area change in the project goes
        /// through here — the participant's own buttons, the developer jumps, RESTART, NEW TRIAL
        /// and the return to the language screen — so cancelling the previous room's narration
        /// here means no transition can be added later that forgets to. The explicit calls at the
        /// individual transitions remain as well, because they can name a more useful reason.
        /// </summary>
        void SetRoom(ExperimentArea area)
        {
            if (m_CurrentAreaKnown && m_CurrentArea != area)
            {
                StopParticipantNarration(
                    $"room transition {m_CurrentArea} -> {area}");
            }

            m_CurrentArea = area;
            m_CurrentAreaKnown = true;

            if (m_Logger != null)
                m_Logger.currentRoom = XRRigTeleporter.ToRoomName(area);
        }

        /// <summary>
        /// Stops ALL participant-facing instructional narration immediately, and prevents any
        /// sequence already in flight from continuing.
        ///
        /// WHAT IT STOPS: the familiarization intro, the practice lead-in, the practice colour
        /// prompt, a REPLAY INSTRUCTIONS playback, and any narration segment that was queued but
        /// had not started yet.
        ///
        /// WHAT IT DELIBERATELY DOES NOT STOP: anything experiment-critical. The five memory
        /// words, the recall beep, the chair-selection feedback and the Area A instruction cue
        /// are all AudioOwnership.Cognitive and are never registered as cancellable, so normal
        /// Area A encoding and the recall beep are unaffected however often this is called. The
        /// only thing that ends those is the run itself ending.
        ///
        /// Three mechanisms, because each one alone has a hole: the coroutine is stopped (but a
        /// coroutine parked in a yield may already have scheduled its clip), the epoch is moved
        /// (so a sequence that somehow survives abandons itself at its next check), and the audio
        /// layer silences the voices that are actually sounding.
        /// </summary>
        void StopParticipantNarration(string reason)
        {
            // Moved FIRST, so anything that wakes up during this method already sees a stale
            // epoch and refuses to speak.
            m_NarrationEpoch++;

            if (m_NarrationFlow != null)
            {
                StopCoroutine(m_NarrationFlow);
                m_NarrationFlow = null;
            }

            var stopped = m_Audio != null ? m_Audio.StopNarration(reason) : 0;

            if (stopped <= 0)
                return;

            Log(EventTypes.NarrationStopped, e => e.notes =
                $"reason={reason}; clips_stopped={stopped}; " +
                "scope=participant_instructional_narration_only; " +
                "cognitive_audio_untouched=TRUE");
        }

        void Log(string eventType, System.Action<ExperimentEvent> configure = null)
        {
            if (m_Logger != null)
                m_Logger.Log(eventType, configure);
        }

        void StopFlow()
        {
            if (m_Flow != null)
            {
                StopCoroutine(m_Flow);
                m_Flow = null;
            }
        }

        // ---------------------------------------------------------------------------------
        // Trial preparation / idle
        // ---------------------------------------------------------------------------------

        /// <summary>Loads the word list and generates the chair block for the next run.</summary>
        void PrepareTrial()
        {
            m_Result.Reset();
            m_TrialWords.Clear();
            m_CurrentChairTrial = null;
            m_ChairSelectionReceived = false;

            // A NEW RUN STARTS WITH NO RECOGNITION ITEMS.
            //
            // Each phase already clears its own store before refilling it, which is enough while
            // every run reaches both phases. It is NOT enough when one does not — an aborted run,
            // or a developer jump straight to Area C — because the store would still hold the
            // PREVIOUS run's items and the next capture would attribute them to this run.
            // PrepareTrial is reached from restart, abort and NEW TRIAL, so clearing here covers
            // every way a run can begin.
            m_ImmediateRecognitionItems.Clear();
            m_DelayedRecognitionItems.Clear();
            m_RecognitionTargets.Clear();

            // The Area B block starts over: the general instructions will be shown again and
            // READY will be required before trial 1. PrepareTrial is reached from a restart and
            // from an abort, which are exactly the two cases where that must happen.
            ResetAreaBBlock();

            // Which of the language's sets THIS run presents. Placed here so NEW TRIAL — which
            // keeps the language and never re-runs the language screen — still advances to the
            // next list instead of re-presenting one the participant has already learned.
            SelectWordSetForRun();

            if (m_Config != null)
            {
                m_TrialWords.AddRange(m_Config.GetTrialWords());
                m_Result.wordSetName = m_Config.GetTrialWordSetName();
                m_Result.chairTrialsPlanned = m_Config.chairTrialsPerRun;

                if (m_ChairTask != null && !m_Config.useGeneratedChairTrials)
                    m_ChairTask.SetTarget(m_Config.targetChair);
            }

            m_Result.presentedWords.AddRange(m_TrialWords);

            if (m_Voice != null)
                m_Voice.expectedWords = m_TrialWords;

            if (m_ChairTask != null)
                m_ChairTask.ResetTask();

            PrepareChairBlock();
        }

        /// <summary>
        /// Clears the Area B block's instruction/readiness state, so the general task
        /// instructions are presented once more and READY is required before the first trial.
        ///
        /// Public so a researcher can deliberately reset the block; the participant flow only
        /// reaches it through PrepareTrial (restart / abort).
        /// </summary>
        public void ResetAreaBBlock()
        {
            m_AreaBInstructionsShown = false;
            m_AreaBReadyPressed = false;
        }

        /// <summary>True once the participant has acknowledged the Area B task instructions.</summary>
        public bool areaBInstructionsShown => m_AreaBInstructionsShown;

        /// <summary>
        /// Generates every chair trial of the run up front, from the session seed.
        ///
        /// Generating the WHOLE block before the participant enters Area B (rather than one
        /// trial at a time) means an impossible configuration is caught before the run starts
        /// instead of half-way through it, and it makes the run's stimulus sequence a single
        /// reproducible object that can be printed, compared and replayed.
        /// </summary>
        void PrepareChairBlock()
        {
            m_ChairPlans.Clear();
            m_SessionResults.chairTrials.Clear();
            m_UsingGeneratedTrials = m_Config != null && m_Config.useGeneratedChairTrials;

            if (m_Config == null || m_ChairTask == null)
                return;

            if (!m_UsingGeneratedTrials)
            {
                Debug.LogWarning("[IKEA_EEG] Generated chair trials are OFF: every trial uses the " +
                                 $"fixed target {m_Config.targetChair} and the scene's authored " +
                                 "chair layout. This reproduces the single-trial MVP behaviour.");
                return;
            }

            var chairIds = m_ChairTask.GetChairIds();
            var sequence = m_Config.BuildDifficultySequence();

            if (!ChairTrialGenerator.TryGenerateBlock(m_SessionSeed, sequence,
                    m_Config.difficultyProfiles, chairIds, m_ChairTask.slots.Count,
                    out var plans, out var problem))
            {
                // Refuse to run a chair block we cannot guarantee. Falling back to "something
                // roughly similar" would put unlabelled, non-reproducible trials in the data.
                Debug.LogError($"[IKEA_EEG] CHAIR TRIAL GENERATION FAILED — {problem}. " +
                               "Area B will present no trials until the configuration is fixed.");

                Log(EventTypes.Warning, e => e.notes = $"chair_generation_failed: {problem}");
                return;
            }

            m_ChairPlans.AddRange(plans);

            // Put the room into the first trial's configuration immediately, so the showroom's
            // visual state is fully determined by the seed from the moment the block is
            // prepared — including straight after a restart, while the participant is still in
            // Area A. RunChairTrial re-applies it; the operation is idempotent.
            if (m_ChairPlans.Count > 0 &&
                !m_ChairTask.ApplyTrialPlan(m_ChairPlans[0], out var applyProblem))
            {
                Debug.LogError($"[IKEA_EEG] Could not apply the first chair trial to the room: " +
                               $"{applyProblem}");
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine($"[IKEA_EEG] Chair block generated from seed {m_SessionSeed} " +
                              $"({m_ChairPlans.Count} trial(s)):");

            foreach (var plan in m_ChairPlans)
                report.AppendLine("    " + plan.Describe());

            Debug.Log(report.ToString().TrimEnd());
        }

        // ---------------------------------------------------------------------------------
        // AREA 0 — VR familiarization
        //
        // Everything below is instructional. No word is presented, no chair exists, no
        // response time is measured, and the only events produced are diagnostic. Area 0
        // cannot affect the seed, the word set, the difficulty sequence or any metric —
        // PrepareTrial has already run by the time it is entered, and nothing here touches it.
        // ---------------------------------------------------------------------------------

        // ---------------------------------------------------------------------------------
        // LANGUAGE SELECTION — before everything
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Shows the language screen. The participant stays here until they choose.
        ///
        /// Placed before Area 0 because every instruction, spoken clip and attribute name that
        /// follows depends on the answer — presenting anything first would mean presenting it
        /// in a language the participant may not read.
        /// </summary>
        void EnterLanguageSelection(string reason)
        {
            // BEFORE the language state is touched. A clip belonging to the outgoing language
            // must never still be sounding while the language screen is up, and must never carry
            // over into the language chosen next.
            StopParticipantNarration($"language selection entered: {reason}");

            SetState(ExperimentState.LanguageSelection);
            SetRoom(ExperimentArea.Familiarization);

            // A new choice is being made, so no language is in force until it is made.
            ExperimentLocalization.ClearLanguage();

            if (m_Teleporter != null)
                m_Teleporter.TeleportTo(ExperimentArea.Familiarization);

            if (m_UI != null)
            {
                m_UI.ResetUI();
                m_UI.ShowArea(ExperimentArea.Familiarization);
                m_UI.ShowLanguagePanel(true);
                m_UI.ShowFamiliarizationPanel(false);
                m_UI.ShowStartButton(false);
            }

            Log(EventTypes.LanguageSelectionShown, e => e.notes =
                $"reason={reason}; languages=EN,ES,JA; no cognitive content shown yet");

            Debug.Log($"[IKEA_EEG] Language selection shown ({reason}).");
        }

        /// <summary>
        /// A language was chosen. It governs the whole experiment session from here: every run
        /// of this sitting is presented in it, and NEW TRIAL never asks again.
        /// </summary>
        void OnLanguageSelected(ExperimentLanguage selected)
        {
            if (m_State != ExperimentState.LanguageSelection)
                return;

            ExperimentLocalization.SetLanguage(selected);

            Log(EventTypes.LanguageSelected, e =>
            {
                e.objectId = ExperimentLanguages.ToCode(selected);
                e.notes = $"platform_language={ExperimentLanguages.ToCode(selected)}; " +
                          $"native_name={ExperimentLanguages.NativeName(selected)}; " +
                          "applies_to_whole_experiment_session=TRUE";
            });

            Debug.Log($"[IKEA_EEG] Language selected: {ExperimentLanguages.ToCode(selected)} " +
                      $"({ExperimentLanguages.NativeName(selected)}). It now governs every " +
                      "participant-facing string and spoken instruction for this experiment " +
                      "session.");

            if (m_UI != null)
                m_UI.ShowLanguagePanel(false);

            // The word list must exist in THIS language before any cognitive content begins.
            ApplyLanguageToWordList();

            if (m_Config != null && m_Config.enableFamiliarization)
                EnterFamiliarization();
            else
                SkipFamiliarization("disabled in ExperimentConfig");

            EvaluateStimulusReadiness(logToConsole: false);
        }

        /// <summary>
        /// Points the config at a word set belonging to the selected language, and records
        /// whether one exists at all.
        ///
        /// NO FALLBACK, BY DESIGN. If the language has no word set the run is blocked rather
        /// than run with English words: a Spanish session that encodes English words is not a
        /// slightly worse Spanish session, it is uninterpretable data.
        /// </summary>
        void ApplyLanguageToWordList()
        {
            m_WordSetLanguageOk = false;

            if (m_Config == null || m_Config.wordList == null)
                return;

            var resolved = ResolveWordSetForCurrentRun(out var reuse);

            if (resolved < 0)
            {
                Debug.LogError($"[IKEA_EEG] NO VERBAL-MEMORY WORD SET for " +
                               $"{ExperimentLocalization.languageCode}. The cognitive run is " +
                               "BLOCKED. English words must not be presented inside a " +
                               "non-English session — define a word set for this language.");

                Log(EventTypes.Warning, e => e.notes =
                    $"no_word_set_for_language={ExperimentLocalization.languageCode}; " +
                    "run_blocked=TRUE; no_english_fallback_applied=TRUE");

                return;
            }

            ApplyResolvedWordSet(resolved, reuse, "language selected");

            PrepareTrial();
        }

        /// <summary>
        /// Which of the current language's word sets THIS RUN should present.
        ///
        /// WHY THIS EXISTS: NEW TRIAL keeps the language and starts another run in the same
        /// sitting. Presenting the same five words again would measure recognition of a list the
        /// participant has already learned, not new encoding — so each run of a sitting advances
        /// to the next set of that language.
        ///
        /// The rule is run_index -> (run_index - 1) modulo the number of sets available in that
        /// language. Deterministic and reconstructible from the CSV alone: nothing is shuffled,
        /// and no state is carried that an analysis would have to guess at.
        ///
        /// WHEN THE POOL RUNS OUT the modulo wraps and words ARE reused — with Japanese having
        /// three sets, run 4 of a sitting necessarily repeats run 1's list. That is a real
        /// limitation of the available validated source material, not something to paper over:
        /// <paramref name="reuse"/> reports it and it is logged on the run.
        ///
        /// Returns -1 when the language has no set at all, which blocks the run exactly as
        /// before.
        /// </summary>
        int ResolveWordSetForCurrentRun(out bool reuse)
        {
            reuse = false;

            if (m_Config == null || m_Config.wordList == null)
                return -1;

            var candidates = m_Config.wordList.GetSetIndicesForLanguage(
                ExperimentLocalization.language);

            if (candidates.Count == 0)
                return -1;

            // Run 1 is the first set. Before a session exists, runIndex is 0 and this still
            // lands on the first set.
            var runIndex = m_Logger != null ? Mathf.Max(1, m_Logger.runIndex) : 1;
            var position = (runIndex - 1) % candidates.Count;

            reuse = runIndex > candidates.Count;

            return candidates[position];
        }

        /// <summary>Applies a resolved set and records exactly what it is, for the data.</summary>
        void ApplyResolvedWordSet(int resolved, bool reuse, string reason)
        {
            m_Config.wordSetIndex = resolved;
            m_WordSetLanguageOk = true;

            var set = m_Config.wordList.GetSet(resolved);

            if (set == null)
                return;

            var candidates = m_Config.wordList.GetSetIndicesForLanguage(
                ExperimentLocalization.language);

            // Everything an analysis needs to reconstruct the exact stimulus, in one row —
            // including the honest statement that a five-word subset of a fifteen-word
            // published form is not that form.
            Log(EventTypes.WordSetSelected, e =>
            {
                e.wordSetId = set.EffectiveId();
                e.notes = $"reason={reason}; " +
                          $"platform_language={ExperimentLocalization.languageCode}; " +
                          $"run_index={(m_Logger != null ? m_Logger.runIndex : 0)}; " +
                          $"sets_available_for_language={candidates.Count}; " +
                          $"word_set_reused_in_session={(reuse ? "TRUE" : "FALSE")}; " +
                          $"validated_for_research={(set.validatedForResearch ? "TRUE" : "FALSE")}; " +
                          $"{set.DescribeProvenance()}; " +
                          $"validation_note={set.validationNote}";
            });

            if (reuse)
            {
                Debug.LogWarning($"[IKEA_EEG] WORD SET REUSED — run " +
                                 $"{(m_Logger != null ? m_Logger.runIndex : 0)} of this sitting " +
                                 $"is presenting '{set.EffectiveId()}' again because " +
                                 $"{ExperimentLocalization.languageCode} has only " +
                                 $"{candidates.Count} set(s). The participant has already heard " +
                                 "these words in this sitting; treat this run's recall as " +
                                 "contaminated by prior exposure.");
            }

            Debug.Log($"[IKEA_EEG] Word set for {ExperimentLocalization.languageCode}: " +
                      $"'{set.EffectiveId()}' " +
                      $"(validated_for_research={(set.validatedForResearch ? "TRUE" : "FALSE")}). " +
                      $"{set.DescribeProvenance()}");
        }

        /// <summary>
        /// Re-selects the word set for a NEW run of the same sitting.
        ///
        /// Called from the run-preparation path so NEW TRIAL advances the list. It changes only
        /// which set is chosen — the run identifiers, the seed, the chair generation and every
        /// timing semantic are untouched.
        /// </summary>
        void SelectWordSetForRun()
        {
            if (m_Config == null || m_Config.wordList == null ||
                !ExperimentLocalization.hasLanguage)
            {
                return;
            }

            var resolved = ResolveWordSetForCurrentRun(out var reuse);

            if (resolved < 0)
            {
                m_WordSetLanguageOk = false;
                return;
            }

            ApplyResolvedWordSet(resolved, reuse, "run prepared");
        }

        /// <summary>Places the participant in Area 0 and shows the familiarization panel.</summary>
        void EnterFamiliarization()
        {
            SetState(ExperimentState.Familiarization);
            SetRoom(ExperimentArea.Familiarization);

            m_PracticeSelectionCount = 0;
            m_PracticeSucceeded = false;
            m_PracticeSecondSelectionDone = false;
            m_FamiliarizationOutcome = string.Empty;
            m_FamiliarizationStartSeconds = m_Logger != null ? m_Logger.clock.RelativeSeconds() : 0d;

            foreach (var practice in m_PracticeObjects)
            {
                if (practice != null)
                    practice.ResetPractice();
            }

            ChoosePracticeTarget();

            if (m_Teleporter != null)
                m_Teleporter.TeleportTo(ExperimentArea.Familiarization);

            if (m_UI != null)
            {
                m_UI.ResetUI();
                m_UI.ShowArea(ExperimentArea.Familiarization);
                m_UI.SetFamiliarizationInstruction(m_Config != null
                    ? ExperimentLocalization.Get(LocKeys.FamiliarizationInstructions)
                    : string.Empty);
                m_UI.SetFamiliarizationStatus(string.Empty);

                // START EXPERIMENT is available from the first moment — practice is never
                // required. Its "ready" emphasis is off until the practice task succeeds.
                m_UI.ShowStartExperimentButton(true);
                m_UI.SetStartExperimentReadyLabel(m_Config != null
                    ? ExperimentLocalization.Get(LocKeys.PracticeReady)
                    : "You are ready to begin.");
                m_UI.SetStartExperimentReady(false);

                m_UI.ShowSkipIntroButton(true);
                m_UI.ShowReplayInstructionsButton(true);
                m_UI.ShowStartButton(false);

                // Reserved researcher surface: stays hidden and empty. No EEG, marker or
                // signal-quality value exists yet and none may be invented here.
                m_UI.ShowResearcherStatusPanel(false);
                m_UI.SetResearcherStatusText(string.Empty);
            }

            Log(EventTypes.FamiliarizationStart, e =>
                e.notes = "area_0_vr_familiarization; not part of cognitive scoring; " +
                          $"practice_objects={m_PracticeObjects.Count}");

            PlayFamiliarizationNarration("area 0 entered");

            Debug.Log("[IKEA_EEG] Area 0 (VR familiarization) entered. Nothing here is scored.");
        }

        /// <summary>
        /// Plays the spoken familiarization instructions ONCE.
        ///
        /// Deliberately separate from the verbal-memory stimulus path: it is an ordinary cue
        /// clip played through the existing ExperimentAudio, carries no memory word, and is
        /// never scheduled or confirmed like a WORD_PRESENTED stimulus. It also never loops —
        /// the participant replays it on demand with the REPLAY INSTRUCTIONS button.
        /// </summary>
        void PlayFamiliarizationNarration(string reason)
        {
            if (m_Audio == null || m_Config == null)
                return;

            // Whatever is already speaking belongs to a sequence this call replaces. Silenced
            // BEFORE the new one is scheduled, so a REPLAY can never overlap the playback it is
            // replacing and Area 0 can never have two instructional voices at once.
            StopParticipantNarration($"narration restarted: {reason}");

            if (m_State != ExperimentState.Familiarization)
                return;

            if (m_Config.GetFamiliarizationNarration(ExperimentLocalization.language) == null)
            {
                // Text-only is a supported state: a language whose narration has not been
                // generated still runs, readable on the panel. It is never given another
                // language's voice.
                Debug.LogWarning("[IKEA_EEG] No familiarization narration for " +
                                 $"{ExperimentLocalization.languageCode}. The instructions are " +
                                 "readable on the panel but will not be spoken, and NO other " +
                                 "language's narration is substituted. Generate it with " +
                                 "IKEA_EEG ▸ Generate Familiarization Narration (local TTS).");
                return;
            }

            // One coroutine owns the whole spoken sequence, so a replay cannot overlap a
            // sequence already in progress.
            m_NarrationFlow = StartCoroutine(RunFamiliarizationNarration(reason));
        }

        /// <summary>
        /// Speaks the familiarization instructions, then the practice lead-in, then the
        /// requested colour — in that order, each waiting for the previous clip to finish.
        ///
        /// Sequenced rather than fired together because they are one continuous explanation:
        /// overlapping them would make all three unintelligible. It plays ONCE per entry and
        /// never loops; REPLAY INSTRUCTIONS restarts the sequence from the top.
        /// </summary>
        IEnumerator RunFamiliarizationNarration(string reason)
        {
            // The epoch this sequence belongs to. If narration is cancelled for ANY reason while
            // this coroutine is parked in a yield, the epoch moves and every remaining segment is
            // abandoned rather than spoken into whatever room the participant is now standing in.
            var epoch = m_NarrationEpoch;

            // Every clip comes from the language in force. There is no cross-language fallback:
            // the voice must agree with the text on the panel.
            var lang = ExperimentLocalization.language;

            var clips = new List<(AudioClip clip, string label)>
            {
                (m_Config.GetFamiliarizationNarration(lang), "FamiliarizationNarration"),
                (m_Config.GetPracticeIntroNarration(lang), "PracticeIntroNarration"),
            };

            // The colour prompt is resolved from THE canonical target colour — the same value the
            // panel's text was written from. Not from a separate draw, not from a name, not from
            // a clip's file name. One value, both outputs.
            if (m_HasPracticeTarget)
            {
                var colorClip = m_Config.GetPracticeColorClip(lang, m_PracticeTargetColor);
                if (colorClip != null)
                {
                    clips.Add((colorClip,
                        $"PracticePrompt_{PracticeColors.CanonicalName(m_PracticeTargetColor)}"));
                }
            }

            foreach (var (clip, label) in clips)
            {
                if (clip == null)
                    continue;

                // Three ways this sequence can have been overtaken: it was cancelled (epoch), the
                // participant left Area 0 (state), or the run was aborted. Checked BEFORE
                // anything is scheduled, so a stale segment is never even queued.
                if (epoch != m_NarrationEpoch || m_RunAborted ||
                    m_State != ExperimentState.Familiarization)
                {
                    yield break;
                }

                var handle = m_Audio.PlayClip(AudioCue.InstructionCue, clip,
                    AudioOwnership.Narration, "AREA_0_FAMILIARIZATION");

                StartCoroutine(ConfirmCueAsync(handle, label));

                var clipName = clip.name;
                Log(EventTypes.StateChanged, e =>
                {
                    e.objectId = clipName;
                    e.notes = $"familiarization_narration_played; segment={label}; " +
                              $"reason={reason}; not_a_memory_stimulus=TRUE; " +
                              "audio_ownership=NARRATION";
                });

                // Wait out the clip plus a short beat, so the segments do not run together.
                yield return new WaitForSeconds(clip.length + 0.35f);
            }

            if (epoch == m_NarrationEpoch)
                m_NarrationFlow = null;
        }

        /// <summary>
        /// Feedback ids for the practice narration clips. Shared by the generator that WRITES
        /// the clips and the runtime that PLAYS them, so a clip can never be looked up under a
        /// name nothing produced.
        /// </summary>
        public static class PracticeFeedbackIds
        {
            public const string Correct = "CORRECT";
            public const string Another = "ANOTHER";
            public const string Incorrect = "INCORRECT";
            public const string Complete = "COMPLETE";
        }

        /// <summary>
        /// Speaks one or more short feedback clips, in order, without overlapping.
        ///
        /// WHY IT REUSES THE NARRATION FLOW. Practice feedback fires on a participant action,
        /// which can arrive while the familiarization narration is still speaking, and can
        /// arrive again a second later. Playing clips directly would let two voices talk over
        /// each other — exactly what A1 forbids. Routing through <see cref="m_NarrationFlow"/>
        /// and bumping <see cref="m_NarrationEpoch"/> means a newer piece of feedback CANCELS an
        /// older one instead of colliding with it, which is the right behaviour: the participant
        /// needs to hear the response to what they just did, not the response to what they did
        /// before it.
        ///
        /// Clips are AudioOwnership.Narration, so leaving Area 0 stops them — the same rule the
        /// instructions already follow.
        /// </summary>
        void SpeakPracticeFeedback(params string[] feedbackIds)
        {
            if (m_Audio == null || m_Config == null || feedbackIds == null)
                return;

            var lang = ExperimentLocalization.language;
            var clips = new List<(AudioClip clip, string label)>();

            foreach (var id in feedbackIds)
            {
                var clip = m_Config.GetPracticeFeedbackClip(lang, id);

                // A language with no generated feedback clip simply runs text-only. No other
                // language's voice is ever substituted.
                if (clip != null)
                    clips.Add((clip, $"PracticeFeedback_{id}"));
            }

            if (clips.Count == 0)
                return;

            StopParticipantNarration("practice feedback");
            m_NarrationFlow = StartCoroutine(RunNarrationSequence(clips, "practice feedback",
                ExperimentState.Familiarization));
        }

        /// <summary>
        /// Speaks the Recognition-mode encoding instructions.
        ///
        /// The INSTRUCTION is narrated; the WORDS never are. This is called from the Area A
        /// instruction phase, before the first word is displayed, and nothing inside the
        /// encoding loop plays narration at all.
        /// </summary>
        /// <returns>
        /// Seconds of speech started, or 0 when nothing was spoken. The caller uses this to keep
        /// the instruction on screen until the voice has finished — see RunAreaA.
        /// </returns>
        float SpeakRecognitionInstructions()
        {
            if (m_Audio == null || m_Config == null)
                return 0f;

            var clip = m_Config.GetRecognitionInstructionNarration(ExperimentLocalization.language);

            if (clip == null)
            {
                Debug.LogWarning("[IKEA_EEG] No Recognition instruction narration for " +
                                 $"{ExperimentLocalization.languageCode}. The instruction is " +
                                 "readable on the panel but will not be spoken, and NO other " +
                                 "language's narration is substituted. Generate it with " +
                                 "IKEA_EEG ▸ Generate Familiarization Narration (local TTS).");
                return 0f;
            }

            StopParticipantNarration("recognition instructions");
            m_NarrationFlow = StartCoroutine(RunNarrationSequence(
                new List<(AudioClip, string)> { (clip, "RecognitionEncodingInstructions") },
                "recognition encoding instructions", ExperimentState.AreaAInstructions));

            return clip.length;
        }

        /// <summary>
        /// Speaks the GENERAL Area B task instructions.
        ///
        /// INSTRUCTIONS ONLY. The clip explains how the chair task works and deliberately names
        /// no colour, size or shape — the per-trial target must be read off the panel and solved
        /// visually, so speaking it would hand the participant the answer. The generator's script
        /// for this clip is written to the same rule.
        /// </summary>
        void SpeakAreaBInstructions()
        {
            if (m_Audio == null || m_Config == null)
                return;

            var clip = m_Config.GetAreaBInstructionNarration(ExperimentLocalization.language);

            if (clip == null)
            {
                Debug.LogWarning("[IKEA_EEG] No Area B instruction narration for " +
                                 $"{ExperimentLocalization.languageCode}. The instructions are " +
                                 "readable on the overlay but will not be spoken, and NO other " +
                                 "language's narration is substituted.");
                return;
            }

            StopParticipantNarration("area B instructions");
            m_NarrationFlow = StartCoroutine(RunNarrationSequence(
                new List<(AudioClip, string)> { (clip, "AreaBTaskInstructions") },
                "area B task instructions", ExperimentState.AreaBInstructions));
        }

        /// <summary>
        /// Speaks the Recognition DELAYED-phase instructions.
        ///
        /// Instruction only — the delayed stimulus words are visual and are never spoken.
        /// </summary>
        /// <returns>Seconds of speech started, or 0 when nothing was spoken.</returns>
        float SpeakDelayedRecognitionInstructions()
        {
            if (m_Audio == null || m_Config == null)
                return 0f;

            var clip = m_Config.GetRecognitionDelayedNarration(ExperimentLocalization.language);

            if (clip == null)
            {
                Debug.LogWarning("[IKEA_EEG] No Recognition delayed instruction narration for " +
                                 $"{ExperimentLocalization.languageCode}. The instruction is " +
                                 "readable on the panel but will not be spoken.");
                return 0f;
            }

            StopParticipantNarration("delayed recognition instructions");
            m_NarrationFlow = StartCoroutine(RunNarrationSequence(
                new List<(AudioClip, string)> { (clip, "RecognitionDelayedInstructions") },
                "delayed recognition instructions", ExperimentState.DelayedRecognition));

            return clip.length;
        }

        /// <summary>
        /// Plays a list of clips in order, each waiting for the previous to finish.
        ///
        /// Extracted from the familiarization narration so the same sequencing, the same epoch
        /// guard and the same ownership apply to every spoken sequence in the project. The
        /// <paramref name="requiredState"/> is checked before each segment: a sequence that was
        /// overtaken by a room change is abandoned rather than spoken into a room the
        /// participant has already left.
        /// </summary>
        IEnumerator RunNarrationSequence(List<(AudioClip clip, string label)> clips,
            string reason, ExperimentState requiredState)
        {
            var epoch = m_NarrationEpoch;

            foreach (var (clip, label) in clips)
            {
                if (clip == null)
                    continue;

                if (epoch != m_NarrationEpoch || m_RunAborted || m_State != requiredState)
                    yield break;

                var handle = m_Audio.PlayClip(AudioCue.InstructionCue, clip,
                    AudioOwnership.Narration, requiredState.ToString().ToUpperInvariant());

                StartCoroutine(ConfirmCueAsync(handle, label));

                var clipName = clip.name;
                Log(EventTypes.StateChanged, e =>
                {
                    e.objectId = clipName;
                    e.notes = $"narration_played; segment={label}; reason={reason}; " +
                              "not_a_memory_stimulus=TRUE; audio_ownership=NARRATION";
                });

                yield return new WaitForSeconds(clip.length + 0.2f);
            }

            if (epoch == m_NarrationEpoch)
                m_NarrationFlow = null;
        }

        /// <summary>REPLAY INSTRUCTIONS in Area 0. Plays the narration once more.</summary>
        void OnReplayInstructionsPressed()
        {
            if (m_State != ExperimentState.Familiarization)
                return;

            PlayFamiliarizationNarration("replay requested");
        }

        /// <summary>
        /// Picks which practice object to ask for, using a random source that belongs to
        /// familiarization ALONE.
        ///
        /// It deliberately does NOT use the session's DeterministicRandom: the experiment's seed
        /// governs the cognitive stimuli, and a practice prompt must not be able to advance it
        /// or otherwise couple a tutorial choice to the trial sequence.
        /// </summary>
        void ChoosePracticeTarget()
        {
            m_PracticeTarget = null;
            m_HasPracticeTarget = false;

            var candidates = new List<PracticeObject>();
            foreach (var practice in m_PracticeObjects)
            {
                if (practice != null)
                    candidates.Add(practice);
            }

            if (candidates.Count == 0)
                return;

            // ---- THE SINGLE DRAW ------------------------------------------------------------
            // Chosen ONCE, here, and nowhere else. Everything below reads m_PracticeTargetColor;
            // nothing downstream draws again, re-derives the colour from a string, or infers it
            // from a clip name. That is the whole fix for "the voice said blue and the panel said
            // red": there is now only one place a practice colour can come from.
            m_PracticeTarget = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            m_PracticeTargetColor = m_PracticeTarget.color;
            m_HasPracticeTarget = true;

            // Derivation 1 — the CANONICAL name, for the log and the narration key.
            var canonical = PracticeColors.CanonicalName(m_PracticeTargetColor);

            // Derivation 2 — the LOCALIZED name, for what the participant reads.
            var localized = ExperimentLocalization.PracticeColorName(m_PracticeTargetColor);

            if (m_UI != null)
            {
                m_UI.SetPracticePrompt(
                    "<b>" + ExperimentLocalization.Get(LocKeys.PracticeSelection) + "</b>\n\n" +
                    ExperimentLocalization.Format(LocKeys.SelectColor, "COLOR", localized));
            }

            // Derivation 3 — the narration clip, resolved from the same enum value. Reported
            // here so a missing prompt is visible in the data at the moment the target is set,
            // rather than inferred later from a silence.
            var clip = m_Config != null
                ? m_Config.GetPracticeColorClip(ExperimentLocalization.language,
                    m_PracticeTargetColor)
                : null;

            Log(EventTypes.PracticeTargetPresented, e =>
            {
                e.objectId = m_PracticeTarget.objectId;
                e.notes = $"requested_color={canonical}; " +
                          $"displayed_color={localized}; " +
                          $"narration_clip={(clip != null ? clip.name : "none")}; " +
                          $"language={ExperimentLocalization.languageCode}; " +
                          $"options={candidates.Count}; " +
                          "single_canonical_target=TRUE; text_and_audio_same_source=TRUE; " +
                          "familiarization_only_random_source=TRUE; " +
                          "does_not_touch_session_seed=TRUE; diagnostic_only=TRUE";
            });
        }

        /// <summary>A practice object was selected. Diagnostic only, never scored.</summary>
        void OnPracticeObjectSelected(PracticeObject practice)
        {
            // Gated on the state so a stray selection outside Area 0 can never be recorded as
            // practice — and, equally, so practice can never be recorded as a chair trial.
            if (m_State != ExperimentState.Familiarization || practice == null)
                return;

            // Derivation 4 — correctness, compared on the SAME canonical value the prompt and the
            // narration were built from. Not on the object reference and not on a name, so what
            // counts as correct is exactly what was asked for.
            var isTarget = m_HasPracticeTarget && practice.color == m_PracticeTargetColor;

            // ---- TWO PHASES, TWO DIFFERENT PASS CONDITIONS ---------------------------------
            //
            // THE BUG THIS FIXES: the second selection was still judged against the requested
            // colour, so after being told "select another one" the participant was told that
            // selecting another colour was wrong. The instruction and the test disagreed.
            //
            // Phase 1 passes on the REQUESTED colour.
            // Phase 2 passes on ANY colour that is NOT the requested one — no second instruction
            //         is given, so there is nothing else it could legitimately be checked against.
            var phase = practicePhase;

            var qualifies = phase == PracticePhase.RequestedColorSelection
                ? isTarget
                : !isTarget;

            // A selection that qualifies for the CURRENT phase advances the task. Nothing else
            // does — a wrong phase-1 colour and a repeated phase-1 colour in phase 2 both leave
            // the requirement exactly where it was. Every selection is still LOGGED either way.
            if (qualifies)
                m_PracticeSelectionCount++;

            if (qualifies && phase == PracticePhase.DifferentColorSelection)
                m_PracticeSecondSelectionDone = true;

            Log(EventTypes.PracticeObjectSelected, e =>
            {
                e.objectId = practice.objectId;
                e.correct = isTarget ? "TRUE" : "FALSE";
                e.notes = $"practice_selection_index={m_PracticeSelectionCount}; " +
                          $"selected_color={practice.colorName}; " +
                          $"requested_color={(m_HasPracticeTarget ? PracticeColors.CanonicalName(m_PracticeTargetColor) : "none")}; " +
                          $"matched_request={(isTarget ? "TRUE" : "FALSE")}; " +
                          $"practice_phase={phase.ToString().ToUpperInvariant()}; " +
                          $"qualified_for_phase={(qualifies ? "TRUE" : "FALSE")}; " +
                          $"counted_toward_practice={(qualifies ? "TRUE" : "FALSE")}; " +
                          "diagnostic_only=TRUE; excluded_from_all_cognitive_metrics=TRUE";
            });

            // ---- Only the MOST RECENT selection is highlighted ------------------------------
            // Every object is selectable and every selection is acknowledged the same way, so
            // the room behaves like an obvious toy rather than a test. Clearing the others
            // first means there is never more than one thing pulsing.
            foreach (var other in m_PracticeObjects)
            {
                if (other != null && other != practice)
                    other.SetActiveHighlight(false);
            }

            practice.SetActiveHighlight(true);

            if (isTarget && !m_PracticeSucceeded)
            {
                practice.MarkSuccess();
                m_PracticeSucceeded = true;

                Log(EventTypes.PracticeSuccess, e =>
                {
                    e.objectId = practice.objectId;
                    e.correct = "TRUE";
                    e.notes = $"requested_color={practice.colorName}; " +
                              $"attempts={m_PracticeSelectionCount}; " +
                              "diagnostic_only=TRUE; not_a_cognitive_score=TRUE";
                });
            }
            else if (isTarget)
            {
                practice.MarkSuccess();
            }

            UpdatePracticeReadiness(practice, qualifies, phase);
        }

        /// <summary>
        /// Applies the two-phase practice rule, then speaks and shows the matching feedback.
        ///
        /// ONE DECISION, TWO OUTPUTS. The voice and the status line are chosen here, together,
        /// from the same values — so they cannot contradict each other. They did contradict each
        /// other before: the participant was told "select another one" and then told that doing
        /// so was incorrect.
        ///
        /// <paramref name="qualified"/> means "this selection satisfied THE PHASE IT WAS MADE
        /// IN", which is not the same as "matched the requested colour". In phase 2 a
        /// different colour is precisely what qualifies.
        ///
        /// Readiness only changes the EMPHASIS on START EXPERIMENT. The button is available from
        /// the moment Area 0 opens and is never gated — the participant always starts the
        /// experiment themselves.
        /// </summary>
        void UpdatePracticeReadiness(PracticeObject lastSelected, bool qualified,
            PracticePhase phaseAtSelection)
        {
            var ready = practiceReady;

            // ---- Spoken feedback ------------------------------------------------------------
            // Announced BEFORE the early return on m_UI == null: feedback is participant-facing
            // and must not silently depend on a UI reference the audio path does not need.
            if (ready)
            {
                // Both phases are done. This says they MAY begin; it does not begin anything.
                SpeakPracticeFeedback(PracticeFeedbackIds.Correct, PracticeFeedbackIds.Complete);
            }
            else if (qualified)
            {
                // Phase 1 satisfied. "Good job. Now select another one."
                SpeakPracticeFeedback(PracticeFeedbackIds.Correct, PracticeFeedbackIds.Another);
            }
            else if (phaseAtSelection == PracticePhase.RequestedColorSelection)
            {
                // The requested colour was asked for and something else was chosen. This is the
                // only case that is genuinely WRONG, and the only one that gets negative feedback.
                SpeakPracticeFeedback(PracticeFeedbackIds.Incorrect);
            }
            else
            {
                // Phase 2, and they picked the requested colour again. That is not an error —
                // it simply is not "another one" — so it re-prompts instead of scolding.
                SpeakPracticeFeedback(PracticeFeedbackIds.Another);
            }

            if (m_UI == null)
                return;

            if (ready)
            {
                m_UI.SetFamiliarizationStatus(
                    ExperimentLocalization.Get(LocKeys.PracticeFeedbackComplete));
                m_UI.SetStartExperimentReady(true);
                return;
            }

            m_UI.SetStartExperimentReady(false);

            if (qualified)
            {
                // Phase 1 done, phase 2 outstanding: ask for a different-coloured object.
                m_UI.SetFamiliarizationStatus(
                    ExperimentLocalization.Get(LocKeys.PracticeFeedbackAnother));
                return;
            }

            if (phaseAtSelection == PracticePhase.RequestedColorSelection)
            {
                // Still on phase 1. Name what they picked and what was asked for — the target
                // name comes from the canonical colour, exactly as the prompt above it did.
                m_UI.SetFamiliarizationStatus(ExperimentLocalization.Format(
                    LocKeys.PracticeTryTarget,
                    "SELECTED", lastSelected.LocalizedColorName(),
                    "TARGET", ExperimentLocalization.PracticeColorName(m_PracticeTargetColor)));
                return;
            }

            // Phase 2, same colour again.
            m_UI.SetFamiliarizationStatus(
                ExperimentLocalization.Get(LocKeys.PracticeFeedbackAnother));
        }


        /// <summary>START EXPERIMENT in Area 0: familiarization was completed.</summary>
        void OnStartExperimentPressed()
        {
            if (m_State != ExperimentState.Familiarization)
                return;

            LeaveFamiliarization(EventTypes.FamiliarizationComplete, "COMPLETED",
                "participant pressed START EXPERIMENT");
        }

        /// <summary>SKIP INTRO in Area 0: familiarization was bypassed.</summary>
        void OnSkipIntroPressed()
        {
            if (m_State != ExperimentState.Familiarization)
                return;

            LeaveFamiliarization(EventTypes.FamiliarizationSkipped, "SKIPPED",
                "participant/researcher pressed SKIP INTRO");
        }

        /// <summary>
        /// Area 0 was never entered. Logged as SKIPPED so the session record distinguishes
        /// "familiarization was bypassed" from "familiarization never existed".
        /// </summary>
        void SkipFamiliarization(string reason)
        {
            m_PracticeSelectionCount = 0;
            m_FamiliarizationOutcome = "SKIPPED";

            Log(EventTypes.FamiliarizationSkipped, e =>
                e.notes = $"reason={reason}; area_0_not_entered; practice_selections=0; " +
                          "no cognitive data affected");

            EnterIdleAtAreaA();
        }

        void LeaveFamiliarization(string eventType, string outcome, string reason)
        {
            // Area 0 owns the instructional narration. The moment the participant leaves it —
            // by START EXPERIMENT or by SKIP INTRO — it stops mid-sentence rather than following
            // them into Area A and talking over the task instructions.
            StopParticipantNarration($"left Area 0: {reason}");

            m_FamiliarizationOutcome = outcome;

            var duration = (m_Logger != null ? m_Logger.clock.RelativeSeconds() : 0d)
                           - m_FamiliarizationStartSeconds;

            Log(eventType, e =>
            {
                e.correct = m_PracticeSelectionCount > 0 ? "TRUE" : "FALSE";
                e.notes = string.Format(CultureInfo.InvariantCulture,
                    "outcome={0}; reason={1}; familiarization_duration_s={2:F3}; " +
                    "practice_selections={3}; practiced_at_least_once={4}; " +
                    "diagnostic_only=TRUE; excluded_from_all_cognitive_metrics=TRUE",
                    outcome, reason, duration, m_PracticeSelectionCount,
                    m_PracticeSelectionCount > 0 ? "TRUE" : "FALSE");
            });

            Debug.Log($"[IKEA_EEG] Familiarization {outcome} after {duration:F1} s with " +
                      $"{m_PracticeSelectionCount} practice selection(s). " +
                      "None of this enters any cognitive metric.");

            // Straight to the existing Area A idle screen. Its START button and the
            // stimulus-readiness gate are untouched, so the validated Area A entry path is
            // exactly what it was before Area 0 existed.
            EnterIdleAtAreaA();
            EvaluateStimulusReadiness(logToConsole: false);
        }

        void EnterIdleAtAreaA()
        {
            SetState(ExperimentState.Idle);
            SetRoom(ExperimentArea.AreaA);

            if (m_Teleporter != null)
                m_Teleporter.TeleportTo(ExperimentArea.AreaA);

            if (m_UI != null)
            {
                m_UI.ResetUI();
                m_UI.ShowLanguagePanel(false);
                m_UI.SetAreaATitle(ExperimentLocalization.Get(LocKeys.AreaATitle));
                m_UI.SetAreaAInstruction(ExperimentLocalization.Get(LocKeys.AreaAWelcome));
                m_UI.SetAreaAStatus(string.Empty);
                m_UI.ShowStartButton(true);
            }
        }

        // ---------------------------------------------------------------------------------
        // Button handlers — each is gated on the state it belongs to, so a stray press in
        // the wrong phase can never advance the protocol.
        // ---------------------------------------------------------------------------------

        void OnStartPressed()
        {
            if (m_State != ExperimentState.Idle)
                return;

            if (m_Config == null)
            {
                Debug.LogError("[IKEA_EEG] Cannot start: no ExperimentConfig assigned to the " +
                               "ExperimentManager.");
                return;
            }

            // Re-evaluate at the moment of pressing START, not just at scene load: the
            // operator may have fixed (or broken) audio routing in between.
            if (!EvaluateStimulusReadiness(logToConsole: true))
            {
                Debug.LogError("[IKEA_EEG] START refused — auditory stimuli cannot be delivered. " +
                               $"{m_StimulusBlockReason}");

                Log(EventTypes.TrialBlocked, e =>
                {
                    e.correct = "FALSE";
                    e.notes = $"START pressed while blocked; {m_StimulusBlockReason}";
                });

                return;
            }

            if (m_Logger != null && !m_Logger.sessionActive)
            {
                // The session was ended (or never auto-started); open a fresh one, and with it
                // a fresh seed and a freshly generated chair block.
                BeginSession(ChooseSeed(), isReplay: false);
                PrepareTrial();
            }

            if (m_Logger != null)
                m_Logger.BeginTrial();

            m_Result.trialId = m_Logger != null ? m_Logger.trialId : string.Empty;

            SetRoom(ExperimentArea.AreaA);
            Log(EventTypes.AreaAEnter, e =>
            {
                e.wordSetId = m_Config.GetTrialWordSetId();
                e.notes = $"word_set={m_Result.wordSetName}; {m_Config.DescribeProtocol()}";
            });

            StopFlow();
            m_Flow = StartCoroutine(RunAreaA());
        }

        void OnEnterAreaBPressed()
        {
            if (m_State != ExperimentState.ReadyForAreaB)
                return;

            if (m_UI != null)
                m_UI.ShowEnterAreaBButton(false);

            SetRoom(ExperimentArea.AreaB);

            if (m_Teleporter != null)
                m_Teleporter.TeleportTo(ExperimentArea.AreaB);

            if (m_UI != null)
                m_UI.ShowArea(ExperimentArea.AreaB);

            var plannedTrials = m_UsingGeneratedTrials
                ? m_ChairPlans.Count
                : Mathf.Max(1, m_Config.chairTrialsPerRun);

            Log(EventTypes.AreaBEnter, e =>
            {
                e.chairTrialCount = plannedTrials.ToString(CultureInfo.InvariantCulture);
                e.notes = $"chair_trials_planned={plannedTrials}; " +
                          $"generated={(m_UsingGeneratedTrials ? "TRUE" : "FALSE")}";
            });

            StopFlow();
            m_Flow = StartCoroutine(RunAreaB());
        }

        void OnExitToAreaCPressed()
        {
            if (m_State != ExperimentState.ReadyForAreaC)
                return;

            if (m_UI != null)
                m_UI.ShowExitToAreaCButton(false);

            SetRoom(ExperimentArea.AreaC);

            if (m_Teleporter != null)
                m_Teleporter.TeleportTo(ExperimentArea.AreaC);

            if (m_UI != null)
                m_UI.ShowArea(ExperimentArea.AreaC);

            Log(EventTypes.AreaCEnter);

            StopFlow();
            m_Flow = StartCoroutine(RunAreaC());
        }

        /// <summary>
        /// READY on the general Area B instruction screen. Gated on the state, so it can only
        /// ever end the instruction period — it can never skip or restart a trial.
        /// </summary>
        void OnReadyPressed()
        {
            if (m_State != ExperimentState.AreaBInstructions)
                return;

            // IDEMPOTENT. RunAreaBInstructions only notices this flag on its next frame, and it
            // hides the READY button after that — so for one or two frames the button is still
            // live while the state still permits a press. Returning early means a fast double
            // press cannot bump the narration epoch a second time or log a second stop.
            if (m_AreaBReadyPressed)
                return;

            // The instruction narration is cancelled AT THE PRESS, not when the coroutine wakes.
            //
            // Without this the clip kept talking over the start of the first chair trial:
            // narration cancellation is hooked to SetRoom, which fires on AREA changes, and
            // READY is a transition WITHIN Area B — so nothing was cancelling it and the
            // participant had to wait out the recording they had just dismissed.
            //
            // Reuses the existing three-layer stop rather than touching the audio layer:
            // it is a no-op when nothing is sounding, and it logs nothing in that case.
            StopParticipantNarration("area B READY pressed");

            m_AreaBReadyPressed = true;
        }

        // ---------------------------------------------------------------------------------
        // Developer navigation — NOT participant functionality
        // ---------------------------------------------------------------------------------

        void OnDeveloperMenuOpened()
        {
            // Opening the menu ALONE changes nothing about the data: no metric, no state, no
            // seed. Only a jump invalidates the run.
            Log(EventTypes.DeveloperNavigationOpened, e => e.notes =
                $"opened_from_state={m_State}; opened_from_room=" +
                $"{(m_Logger != null ? m_Logger.currentRoom : "unknown")}; " +
                "no_metric_affected_by_opening=TRUE");

            // Participant interaction is suspended while the menu is up, so a stray ray cannot
            // select a chair behind the panel and record a response nobody made.
            if (m_ChairTask != null && m_ChairTask.selectionOpen)
                m_ChairTask.CloseSelection();
        }

        void OnDeveloperMenuClosed()
        {
            Log(EventTypes.DeveloperNavigationClosed, e =>
                e.notes = $"closed_at_state={m_State}");
        }

        void OnDeveloperCloseRequested()
        {
            m_DeveloperNavigation?.Close();
        }

        /// <summary>
        /// A developer jumped to an area. The run is permanently marked as interrupted: it may
        /// still be useful for inspection, but it is no longer a valid participant protocol run
        /// and the data says so on every subsequent row.
        /// </summary>
        void OnDeveloperAreaJump(ExperimentArea area)
        {
            if (m_RunAborted || m_SessionEnded)
            {
                // The run's files are closed. Jumping into an area would restart participant
                // coroutines with nowhere to write, and would break the promise that an aborted
                // run is terminal. The recovery path is RETURN TO LANGUAGE SELECTION, which
                // opens a fresh session first.
                Debug.LogWarning("[IKEA_EEG] Developer area jump refused: this run is " +
                                 (m_RunAborted ? "ABORTED" : "ENDED") + ". Use RETURN TO " +
                                 "LANGUAGE SELECTION to begin a new session.");

                m_DeveloperNavigation?.Close();
                return;
            }

            var fromState = m_State;
            var fromRoom = m_Logger != null ? m_Logger.currentRoom : "unknown";

            m_Logger?.MarkDeveloperInterrupted($"developer jumped to {area} from {fromState}");

            Log(EventTypes.DeveloperAreaJump, e =>
            {
                e.correct = "FALSE";
                e.notes = $"from_state={fromState}; from_room={fromRoom}; to_area={area}; " +
                          "developer_interrupted=TRUE; " +
                          "run_is_not_a_valid_participant_protocol_run=TRUE";
            });

            m_DeveloperNavigation?.Close();

            // Stop whatever the protocol was doing; a jumped-into area must not inherit a
            // coroutine — or a voice — from the area that was abandoned.
            StopFlow();
            StopParticipantNarration($"developer jumped to {area}");

            if (m_Voice != null && m_Voice.isRecording)
                m_Voice.AbortRecording($"developer jumped to {area}");

            if (m_ChairTask != null)
            {
                m_ChairTask.CloseSelection();
                m_ChairTask.ResetTask();
            }

            m_CurrentChairTrial = null;
            m_ChairSelectionReceived = false;

            switch (area)
            {
                case ExperimentArea.Familiarization:
                    ResetAreaBBlock();
                    EnterFamiliarization();
                    break;

                case ExperimentArea.AreaA:
                    ResetAreaBBlock();
                    EnterIdleAtAreaA();
                    EvaluateStimulusReadiness(logToConsole: false);
                    break;

                case ExperimentArea.AreaB:
                    SetRoom(ExperimentArea.AreaB);
                    m_Teleporter?.TeleportTo(ExperimentArea.AreaB);
                    m_UI?.ShowArea(ExperimentArea.AreaB);
                    SetState(ExperimentState.ReadyForAreaB);
                    m_Flow = StartCoroutine(RunAreaB());
                    break;

                case ExperimentArea.AreaC:
                    SetRoom(ExperimentArea.AreaC);
                    m_Teleporter?.TeleportTo(ExperimentArea.AreaC);
                    m_UI?.ShowArea(ExperimentArea.AreaC);
                    m_Flow = StartCoroutine(RunAreaC());
                    break;
            }

            Debug.LogWarning($"[IKEA_EEG] DEVELOPER AREA JUMP -> {area}. This run is now marked " +
                             "DEVELOPER_INTERRUPTED and must not be reported as participant data.");
        }

        /// <summary>
        /// RETURN TO LANGUAGE SELECTION from the developer menu.
        ///
        /// A localization-testing tool, not a participant control — the participant has no way
        /// to change language once a run is under way. Using it during an active run marks that
        /// run DEVELOPER_INTERRUPTED through the existing architecture, because a run whose
        /// language changed part-way through is not valid experimental data.
        /// </summary>
        void OnDeveloperReturnToLanguage()
        {
            // THE RECOVERY PATH out of an aborted or ended run. Both have closed their files, so
            // a new experiment session must be opened before anything can be logged again —
            // otherwise the next run would write into nothing. The aborted run itself is left
            // exactly as it was written; nothing is deleted or reopened.
            if (m_RunAborted || m_SessionEnded)
            {
                var recoveringFrom = m_RunAborted ? "ABORTED" : "ENDED";

                m_DeveloperNavigation?.Close();
                StopParticipantNarration("recovering from an aborted/ended run");

                m_RunAborted = false;
                m_SessionEnded = false;

                m_Logger?.BeginExperimentSession();

                RestartSession(ChooseSeed(), isReplay: false,
                    reason: $"developer recovered from a {recoveringFrom} run",
                    returnToFamiliarization: true, returnToLanguageSelection: true);

                Debug.LogWarning($"[IKEA_EEG] Developer recovered from a {recoveringFrom} run. " +
                                 "A NEW experiment session was opened; the previous run's data " +
                                 "is closed, marked and untouched.");
                return;
            }

            var wasRunning = m_Logger != null && m_Logger.sessionActive &&
                             m_State != ExperimentState.LanguageSelection &&
                             m_State != ExperimentState.Ended;

            if (wasRunning)
            {
                m_Logger.MarkDeveloperInterrupted(
                    "developer returned to the language screen mid-run");
            }

            Log(EventTypes.DeveloperAreaJump, e =>
            {
                e.correct = "FALSE";
                e.notes = $"from_state={m_State}; to=LANGUAGE_SELECTION; " +
                          $"was_active_run={(wasRunning ? "TRUE" : "FALSE")}; " +
                          "developer_localization_tool=TRUE";
            });

            m_DeveloperNavigation?.Close();
            StopFlow();

            // Silenced here, well before EnterLanguageSelection clears the language: a sentence
            // in the outgoing language must not still be playing over the language screen, and
            // must certainly not run on into whatever language is chosen next.
            StopParticipantNarration("developer returned to language selection");

            if (m_Voice != null && m_Voice.isRecording)
                m_Voice.AbortRecording("developer returned to language selection");

            if (m_ChairTask != null)
            {
                m_ChairTask.CloseSelection();
                m_ChairTask.ResetTask();
            }

            m_CurrentChairTrial = null;
            m_ChairSelectionReceived = false;
            ResetAreaBBlock();

            EnterLanguageSelection("developer returned to language selection");
        }

        /// <summary>ABORT SESSION from the developer menu: close the run down immediately.</summary>
        void OnDeveloperAbortSession()
        {
            m_DeveloperNavigation?.Close();

            AbortRun("developer aborted the session");
        }

        // ---------------------------------------------------------------------------------
        // ABORT — a hard stop of every participant-facing process
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// ABORT: stops EVERYTHING the participant is currently subject to, in one call, and
        /// leaves the run in a stable terminal state.
        ///
        /// This is not "return to the menu". After it, nothing cognitive is running anywhere: no
        /// narration, no queued narration, no protocol coroutine, no response window, no feedback
        /// or inter-trial timer, no microphone, no pending transition. The order below is
        /// deliberate — everything that can still PRODUCE something is stopped before anything is
        /// written, so nothing can be appended to the record after the abort has been logged.
        ///
        /// DATA SAFETY (see also the individual steps):
        ///   * an in-flight recording is CLOSED PROPERLY, not discarded — if it holds valid audio
        ///     the WAV is written, and it is marked ABORTED rather than left with no ending;
        ///   * an unanswered chair trial is INVALIDATED, never scored as incorrect, and its
        ///     partial response time never reaches the mean or median;
        ///   * previously completed runs are untouched — nothing here deletes anything.
        /// </summary>
        public void AbortRun(string reason)
        {
            if (m_RunAborted)
            {
                // Already aborted. A second ABORT must not re-log, re-finalise, or produce a
                // second terminal screen.
                Debug.Log("[IKEA_EEG] ABORT ignored: this run is already aborted.");
                return;
            }

            if (m_SessionEnded)
            {
                Debug.Log("[IKEA_EEG] ABORT ignored: the session has already ended normally.");
                return;
            }

            var fromState = m_State;
            var fromRoom = m_Logger != null ? m_Logger.currentRoom : "unknown";

            // Set FIRST. Every coroutine, callback and narration segment checks it, so from this
            // line on nothing new can be started by anything that is still unwinding.
            m_RunAborted = true;

            // ---- 1. Silence ------------------------------------------------------------------
            // Narration and anything queued behind it. Done before the state changes so the
            // cancellation reason is the abort rather than an incidental room change.
            StopParticipantNarration($"aborted: {reason}");

            // ---- 2. Stop every participant-facing coroutine ---------------------------------
            // m_Flow is the single protocol coroutine: instructions, encoding, the recall window,
            // the chair block, the feedback period and the inter-trial interval are all inside it,
            // so stopping it stops every timed participant process at once.
            //
            // Deliberately targeted rather than a blanket stop of every coroutine on this
            // component: the audio-confirmation coroutines must be allowed to finish, because
            // their job is to record whether stimuli already delivered were actually heard.
            StopFlow();

            // ---- 3. Close the response window ------------------------------------------------
            if (m_ChairTask != null)
            {
                m_ChairTask.CloseSelection();
                m_ChairTask.ResetTask();
            }

            m_ChairSelectionReceived = false;

            // ---- 4. Microphone: stop SAFELY, keeping any valid audio -------------------------
            var recordingWasActive = m_Voice != null && m_Voice.isRecording;

            if (recordingWasActive)
            {
                // The SAVING stop path, not the discarding one: it ends the device, writes the
                // WAV when there are samples, analyses the signal and logs the save. It is given
                // the ABORTED stop reason, so termination_reason=ABORTED appears in the data
                // while the participant's speech itself is preserved. The cancel-without-a-result
                // path would have thrown that audio away.
                m_Voice.StopRecording(RecallStopReasons.Aborted);
            }

            // ---- 5. Invalidate an unanswered chair trial ------------------------------------
            var trialWasActive = m_CurrentChairTrial != null && !m_CurrentChairTrial.selectionMade;

            if (trialWasActive)
            {
                // INVALID, not incorrect. A trial the participant never got to answer says
                // nothing about them; counting it wrong would depress accuracy for an
                // operator action, and its partial response time would poison the mean and the
                // median. SessionResults excludes invalid trials from both.
                m_CurrentChairTrial.valid = false;
                m_CurrentChairTrial.invalidReason = $"ABORTED: {reason}";
                m_SessionResults.chairTrials.Add(m_CurrentChairTrial);
            }

            m_CurrentChairTrial = null;

            // ---- 6. Mark the run through the EXISTING invalid/interrupted architecture -------
            // Same mechanism a developer area jump uses: every subsequent row carries it, so an
            // aborted run can never be mistaken for a clean participant run.
            m_Logger?.MarkDeveloperInterrupted($"run aborted: {reason}");

            SetState(ExperimentState.Aborted);

            // ---- 7. Record what was interrupted ---------------------------------------------
            var encodingIncomplete = fromState == ExperimentState.WordEncoding ||
                                     fromState == ExperimentState.AreaAInstructions;

            var recallIncomplete = fromState == ExperimentState.ImmediateRecall ||
                                   fromState == ExperimentState.DelayedRecall;

            Log(EventTypes.RunAborted, e =>
            {
                e.correct = "FALSE";
                e.notes = $"reason={reason}; from_state={fromState}; from_room={fromRoom}; " +
                          $"narration_stopped=TRUE; " +
                          $"recording_active_at_abort={(recordingWasActive ? "TRUE" : "FALSE")}; " +
                          $"recording_termination_reason={(recordingWasActive ? RecallStopReasons.Aborted : "n/a")}; " +
                          $"chair_trial_active_at_abort={(trialWasActive ? "TRUE" : "FALSE")}; " +
                          $"chair_trial_marked=INVALID; chair_trial_counted_incorrect=FALSE; " +
                          $"chair_trial_rt_excluded_from_mean_and_median=TRUE; " +
                          $"encoding_incomplete={(encodingIncomplete ? "TRUE" : "FALSE")}; " +
                          $"recall_incomplete={(recallIncomplete ? "TRUE" : "FALSE")}; " +
                          "run_is_not_a_valid_participant_protocol_run=TRUE; " +
                          "previous_completed_runs_untouched=TRUE";
            });

            if (encodingIncomplete || recallIncomplete)
            {
                Log(EventTypes.Warning, e => e.notes =
                    $"memory_phase_interrupted_by_abort; phase={fromState}; " +
                    "no_complete_encoding_or_recall_for_this_run=TRUE");
            }

            // ---- 8. Close the run's files ----------------------------------------------------
            // The run is written out and closed, marked interrupted, so what DID happen survives.
            // Nothing is deleted: previously completed runs and this run's own partial data are
            // both retained.
            if (m_Logger != null && m_Logger.sessionActive)
                m_Logger.EndSession($"aborted: {reason}");

            // ---- 9. One stable terminal screen ----------------------------------------------
            ShowAbortedState();

            Debug.LogWarning($"[IKEA_EEG] RUN ABORTED ({reason}) from {fromState}. All " +
                             "participant-facing processes stopped: narration, protocol " +
                             "coroutine, response window, microphone" +
                             (recordingWasActive ? " (recording closed and kept)" : "") +
                             (trialWasActive ? ", active chair trial invalidated" : "") +
                             ". This run must not be reported as participant data.");
        }

        /// <summary>
        /// The aborted screen: one message, and no way for a participant to start anything.
        ///
        /// It reuses the Area C panel exactly as the ended screen does, so there is ONE terminal
        /// surface in the project rather than two competing ones. RECENTER stays — a participant
        /// still wearing the headset may need to re-align, and it cannot touch experiment state.
        /// Developer navigation is unaffected: it is a gesture, not a participant control.
        /// </summary>
        void ShowAbortedState()
        {
            if (m_UI == null)
                return;

            m_UI.ShowArea(ExperimentArea.AreaC);

            m_UI.SetAreaCInstruction(ExperimentLocalization.Get(LocKeys.ExperimentAborted));
            m_UI.SetAreaCStatus(string.Empty);
            m_UI.SetResults(ExperimentLocalization.Get(LocKeys.ExperimentAbortedDetail));

            // Nothing that could begin, resume or finalise a run.
            m_UI.ShowRestartButton(false);
            m_UI.ShowNewTrialButton(false);
            m_UI.ShowEndButton(false);
            m_UI.ShowStartButton(false);
            m_UI.ShowEnterAreaBButton(false);
            m_UI.ShowExitToAreaCButton(false);
            m_UI.ShowReadyButton(false);
            m_UI.ShowStartExperimentButton(false);
            m_UI.ShowSkipIntroButton(false);
            m_UI.ShowReplayInstructionsButton(false);
            m_UI.ShowLanguagePanel(false);
            m_UI.ShowRecheckAudioButton(false);
            m_UI.SetWarning(string.Empty);

            m_UI.ShowRecenterButtons(true);
        }

        void OnRestartPressed()
        {
            RestartTrial();
        }

        void OnNewTrialPressed()
        {
            // Only from the results screen: a NEW TRIAL part-way through a run would finalise
            // an incomplete run as though it were a result.
            if (m_State != ExperimentState.Results)
                return;

            StartNewRun();
        }

        void OnEndPressed()
        {
            EndSession();
        }

        // ---------------------------------------------------------------------------------
        // AREA A — instructions, word encoding, immediate recall
        // ---------------------------------------------------------------------------------

        IEnumerator RunAreaA()
        {
            SetState(ExperimentState.AreaAInstructions);

            // PROTOCOL-AWARE INSTRUCTIONS. This used to always show AreaAInstructions —
            // "You will hear five words" — which is correct for FreeRecall and flatly wrong for
            // Recognition, where the participant SEES fifteen words and hears none of them. The
            // routing is the fix; neither string was edited, because each is still right for its
            // own protocol.
            var isRecognition = m_Config.protocolMode == VerbalProtocolMode.Recognition;

            var instructionKey = isRecognition
                ? LocKeys.RecognitionEncodingInstructions
                : LocKeys.AreaAInstructions;

            if (m_UI != null)
            {
                m_UI.ShowStartButton(false);
                m_UI.ClearWordDisplay();
                m_UI.SetAreaAInstruction(ExperimentLocalization.Get(instructionKey));
                m_UI.SetAreaAStatus(string.Empty);
            }

            // The instruction is SPOKEN as well as shown. Instructions may be narrated; the
            // encoding WORDS may not. That distinction is the whole paradigm, and it is kept by
            // narrating here — before any word is displayed — and never inside the encoding loop.
            var narrationSeconds = isRecognition ? SpeakRecognitionInstructions() : 0f;

            if (m_Audio != null)
                StartCoroutine(ConfirmCueAsync(m_Audio.PlayCue(AudioCue.InstructionCue),
                    "AreaAInstructionCue"));

            // HOLD UNTIL THE VOICE HAS FINISHED. The configured instruction window is 8 s and
            // the spoken Recognition instruction is longer than that, so the first word used to
            // appear while the participant was still being told what the task was — the
            // instruction and the first stimulus overlapped.
            //
            // The configured duration is NOT changed; it stays the floor. This only refuses to
            // start the stimuli early, which is a property the protocol requires either way:
            // an encoding list must not begin before its instructions have been delivered.
            var instructionSeconds = Mathf.Max(m_Config.instructionDurationSeconds,
                narrationSeconds + 0.5f);

            yield return new WaitForSeconds(instructionSeconds);

            // ---- Protocol fork ------------------------------------------------------------
            // The Recognition protocol replaces the whole of the rest of Area A: visual
            // encoding, then immediate recognition, then the same ReadyForAreaB hand-off.
            //
            // Placed as an early return rather than wrapping the code below in an else, so the
            // FreeRecall path underneath is left character-for-character as it was. That path
            // is hardware-verified and is the one thing here that must not acquire a new bug.
            if (m_Config.protocolMode == VerbalProtocolMode.Recognition)
            {
                yield return RunRecognitionEncoding();

                if (m_State == ExperimentState.Aborted)
                {
                    m_Flow = null;
                    yield break;
                }

                yield return RunRecognitionPhase(RecognitionPhase.Immediate,
                    ExperimentState.ImmediateRecognition,
                    EventTypes.ImmediateRecognitionStart,
                    EventTypes.ImmediateRecognitionEnd,
                    SetAreaAInstructionAndStatus);

                if (m_State == ExperimentState.Aborted)
                {
                    m_Flow = null;
                    yield break;
                }

                SetState(ExperimentState.ReadyForAreaB);

                if (m_UI != null)
                {
                    m_UI.ClearWordDisplay();
                    m_UI.SetAreaAInstruction(ExperimentLocalization.Get(LocKeys.ReadyForAreaB));
                    m_UI.SetAreaAStatus(string.Empty);
                    m_UI.ShowEnterAreaBButton(true);
                }

                m_Flow = null;
                yield break;
            }

            // ---- Word encoding -----------------------------------------------------------
            SetState(ExperimentState.WordEncoding);

            // THE TARGET WORDS ARE NEVER SHOWN. This is a verbal memory paradigm: the words
            // are delivered as speech only. The screen carries a single static prompt for the
            // whole encoding phase and does not change per word — a per-word visual change
            // would both leak the item count/timing and inject a visual evoked response on
            // top of the auditory one we care about.
            if (m_UI != null)
            {
                m_UI.ClearWordDisplay();
                m_UI.SetAreaAInstruction(ExperimentLocalization.Get(LocKeys.EncodingListen));
                m_UI.SetAreaAStatus(string.Empty);
            }

            // Last gate before any word is presented. Reaching encoding with an unusable clip
            // would mean logging WORD_PRESENTED for a stimulus that was never delivered.
            var clipsReady = true;
            for (var i = 0; i < m_TrialWords.Count; i++)
            {
                var clip = m_Config.wordList != null
                    ? m_Config.wordList.GetWordClip(m_Config.wordSetIndex, i)
                    : null;

                if (ExperimentAudio.IsClipUsable(clip, out var reason))
                    continue;

                clipsReady = false;
                var index = i;
                var word = m_TrialWords[i];
                Log(EventTypes.AudioStimulusFailed, e =>
                {
                    e.objectId = $"SpokenWord:{word}";
                    e.wordIndex = (index + 1).ToString(CultureInfo.InvariantCulture);
                    e.expectedWord = word;
                    e.notes = $"audio_confirmed=FALSE; source=WordListDefinition[" +
                              $"{m_Config.GetTrialWordSetName()}][{index}]; reason={reason}";
                });
            }

            if (!clipsReady)
            {
                Debug.LogError("[IKEA_EEG] Encoding ABORTED — one or more spoken word clips are " +
                               "unusable. See the AUDIO_STIMULUS_FAILED rows for which.");

                EvaluateStimulusReadiness(logToConsole: true);
                AbortTrial("spoken word clip missing or empty");
                yield break;
            }

            Log(EventTypes.WordEncodingStart, e =>
            {
                e.wordSetId = m_Config.GetTrialWordSetId();
                e.notes = $"word_count={m_TrialWords.Count}; word_set={m_Result.wordSetName}; " +
                          $"modality=AUDITORY_ONLY; words_shown_visually=FALSE; " +
                          $"all_clips_present={(clipsReady ? "TRUE" : "FALSE")}";
            });

            // Give the prompt a moment to be read before the first word arrives.
            yield return new WaitForSeconds(m_Config.preFirstWordDelaySeconds);

            for (var i = 0; i < m_TrialWords.Count; i++)
            {
                var word = m_TrialWords[i];
                var spokenClip = m_Config.wordList != null
                    ? m_Config.wordList.GetWordClip(m_Config.wordSetIndex, i)
                    : null;

                AudioCueHandle spoken = null;

                if (m_Audio != null)
                {
                    spoken = m_Audio.PlayClip(AudioCue.SpokenWord, spokenClip);

                    // Wait for the clip's REAL onset on the DSP clock. WORD_PRESENTED is
                    // stamped here, so the marker corresponds to the moment the spoken word
                    // actually begins — not to entering this loop iteration.
                    while (!m_Audio.HasOnsetPassed(spoken))
                        yield return null;

                    m_Audio.ConfirmPlayback(spoken);
                }

                var index = i;
                var handle = spoken;
                var clipName = spokenClip != null ? spokenClip.name : "NONE";
                var wordSetId = m_Config.GetTrialWordSetId();
                Log(EventTypes.WordPresented, e =>
                {
                    e.wordIndex = (index + 1).ToString(CultureInfo.InvariantCulture);
                    e.expectedWord = word;
                    e.recallPhase = string.Empty;
                    e.objectId = clipName;

                    // The six fields that make a word presentation independently checkable:
                    // which set, which position, which word, which recording, when it was
                    // scheduled and when its onset was actually observed.
                    e.wordSetId = wordSetId;
                    e.clipName = clipName;
                    e.scheduledAudioTime = handle != null
                        ? handle.scheduledDspTime.ToString("F6", CultureInfo.InvariantCulture)
                        : string.Empty;
                    e.confirmedAudioTime = handle != null && handle.confirmed
                        ? handle.confirmedDspTime.ToString("F6", CultureInfo.InvariantCulture)
                        : string.Empty;

                    e.notes = $"presentation_index={index + 1}/{m_TrialWords.Count}; " +
                              $"modality=SPOKEN; " +
                              (handle != null
                                  ? handle.ToNotes()
                                  : "audio_confirmed=FALSE; reason=no ExperimentAudio assigned");
                });

                ReportStimulusFailure(handle, $"SpokenWord:{word}", string.Empty);

                // Hold for the length of the recording itself, then the inter-word gap, so
                // pacing follows the speech rather than a fixed display duration.
                var spokenLength = handle != null && handle.clipLength > 0f
                    ? handle.clipLength
                    : m_Config.wordDisplaySeconds;

                yield return new WaitForSeconds(spokenLength);

                if (i < m_TrialWords.Count - 1)
                    yield return new WaitForSeconds(m_Config.interWordGapSeconds);
            }

            Log(EventTypes.WordEncodingEnd);

            if (m_UI != null)
            {
                m_UI.ClearWordDisplay();
                m_UI.SetAreaAStatus(string.Empty);
            }

            yield return new WaitForSeconds(m_Config.preBeepDelaySeconds);

            // ---- Immediate recall ---------------------------------------------------------
            yield return RunRecall(
                RecallPhases.Immediate,
                EventTypes.ImmediateRecallBeep,
                EventTypes.ImmediateRecallStart,
                EventTypes.ImmediateRecallEnd,
                ExperimentState.ImmediateRecall,
                m_Config.immediateRecallMaxDuration,
                ExperimentLocalization.Get(LocKeys.ImmediateRecallPrompt),
                SetAreaAInstructionAndStatus);

            // ---- Ready to move on ---------------------------------------------------------
            SetState(ExperimentState.ReadyForAreaB);

            if (m_UI != null)
            {
                m_UI.SetAreaAInstruction(ExperimentLocalization.Get(LocKeys.ReadyForAreaB));
                m_UI.SetAreaAStatus(string.Empty);
                m_UI.ShowEnterAreaBButton(true);
            }

            m_Flow = null;
        }

        /// <summary>
        /// Pushes the adaptive-stop thresholds from the config into the recorder before each
        /// recording, so the values that governed a session are the ones in the config asset —
        /// and therefore the ones written into the session record.
        /// </summary>
        void ApplyRecallDetectionSettings()
        {
            if (m_Voice == null || m_Config == null)
                return;

            m_Voice.ConfigureAdaptiveStop(
                m_Config.recallSilenceStopSeconds,
                m_Config.speechStartThreshold,
                m_Config.silenceThreshold,
                m_Config.minimumSpeechDuration);
        }

        // ---------------------------------------------------------------------------------
        // Recognition protocol
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// VISUAL encoding: each target word is displayed alone for a configured time, with a
        /// short transition beep at each change, and is never spoken.
        ///
        /// THE MODALITY IS THE POINT. In FreeRecall the words are audio-only and the screen
        /// never changes; here it is the exact opposite, and the two must not blur into each
        /// other. Nothing in this method plays a word clip, and
        /// <see cref="ExperimentAudio.PlayCue"/> is only ever called with
        /// <see cref="AudioCue.StimulusTransition"/> — a contentless marker that a new word is
        /// present. Spoken INSTRUCTIONS elsewhere are unaffected.
        ///
        /// ONSET AND OFFSET ARE BOTH LOGGED, as separate events with their own timestamps. A
        /// visual stimulus has a real duration, and an EEG epoch cut around "the word appeared"
        /// is a different epoch from one cut around "the word disappeared"; recording only one
        /// would force the other to be inferred from a nominal duration that frame timing does
        /// not actually guarantee.
        /// </summary>
        IEnumerator RunRecognitionEncoding()
        {
            SetState(ExperimentState.WordEncoding);

            var list = m_Config.recognitionWordList;

            if (list == null || list.targetCount == 0)
            {
                AbortTrial("no recognition word list configured (targets are empty)");
                yield break;
            }

            if (!list.Validate(RecognitionPhase.Immediate, out var problem))
            {
                AbortTrial($"recognition word list is not usable: {problem}");
                yield break;
            }

            m_RecognitionTargets.Clear();
            m_RecognitionTargets.AddRange(list.targetWords);

            // The presented words go into the run record under the same field FreeRecall uses,
            // so the session summary and the CSV need no protocol-specific special case.
            m_TrialWords.Clear();
            m_TrialWords.AddRange(list.targetWords);
            m_Result.presentedWords.Clear();
            m_Result.presentedWords.AddRange(m_TrialWords);

            if (m_UI != null)
            {
                m_UI.ClearWordDisplay();

                // "Watch carefully", never "Listen carefully": these stimuli are visual and are
                // never spoken. EncodingListen stays untouched for the FreeRecall path.
                m_UI.SetAreaAInstruction(
                    ExperimentLocalization.Get(LocKeys.RecognitionEncodingWatch));
                m_UI.SetAreaAStatus(string.Empty);
            }

            Log(EventTypes.WordEncodingStart, e =>
            {
                e.protocolMode = ProtocolModeTag();
                e.notes = $"word_count={m_TrialWords.Count}; modality=VISUAL_ONLY; " +
                          $"words_spoken_aloud=FALSE; " +
                          $"display_seconds={m_Config.recognitionWordDisplaySeconds:F2}; " +
                          $"gap_seconds={m_Config.recognitionInterWordGapSeconds:F2}; " +
                          list.DescribeProvenance(RecognitionPhase.Immediate);
            });

            yield return new WaitForSeconds(m_Config.preFirstWordDelaySeconds);

            for (var i = 0; i < m_TrialWords.Count; i++)
            {
                if (m_State == ExperimentState.Aborted)
                    yield break;

                var word = m_TrialWords[i];

                // The transition cue fires WITH the word appearing, not before it: it marks the
                // change rather than predicting it.
                if (m_Audio != null)
                    m_Audio.PlayCue(AudioCue.StimulusTransition);

                if (m_UI != null)
                    m_UI.SetWordDisplay(word);

                var onset = m_Logger != null ? m_Logger.ElapsedSessionSeconds() : 0d;
                var index = i;

                Log(EventTypes.WordPresented, e =>
                {
                    e.protocolMode = ProtocolModeTag();
                    e.wordIndex = (index + 1).ToString(CultureInfo.InvariantCulture);
                    e.expectedWord = word;
                    e.recognitionItemId = $"TARGET_{index + 1:00}";
                    e.recognitionItemClass = RecognitionItemClass.Target.ToString().ToUpperInvariant();
                    e.stimulusOnsetTime = onset.ToString("F6", CultureInfo.InvariantCulture);
                    e.notes = $"presentation_index={index + 1}/{m_TrialWords.Count}; " +
                              "modality=VISUAL; spoken=FALSE";
                });

                yield return new WaitForSeconds(m_Config.recognitionWordDisplaySeconds);

                if (m_UI != null)
                    m_UI.ClearWordDisplay();

                var offset = m_Logger != null ? m_Logger.ElapsedSessionSeconds() : 0d;

                Log(EventTypes.WordOffset, e =>
                {
                    e.protocolMode = ProtocolModeTag();
                    e.wordIndex = (index + 1).ToString(CultureInfo.InvariantCulture);
                    e.expectedWord = word;
                    e.recognitionItemId = $"TARGET_{index + 1:00}";
                    e.recognitionItemClass = RecognitionItemClass.Target.ToString().ToUpperInvariant();
                    e.stimulusOnsetTime = onset.ToString("F6", CultureInfo.InvariantCulture);
                    e.stimulusOffsetTime = offset.ToString("F6", CultureInfo.InvariantCulture);
                    e.notes = $"displayed_seconds={(offset - onset):F3}";
                });

                if (i < m_TrialWords.Count - 1)
                    yield return new WaitForSeconds(m_Config.recognitionInterWordGapSeconds);
            }

            Log(EventTypes.WordEncodingEnd, e => e.protocolMode = ProtocolModeTag());

            if (m_UI != null)
                m_UI.ClearWordDisplay();
        }

        /// <summary>
        /// One recognition phase: every target mixed with that phase's lures, shuffled, then
        /// presented one at a time for a SEEN BEFORE / NOT SEEN BEFORE answer.
        ///
        /// Shared by the immediate and delayed phases because they are the same procedure over
        /// a different item set — writing them twice would be two places for the classification
        /// to drift apart.
        /// </summary>
        IEnumerator RunRecognitionPhase(RecognitionPhase phase, ExperimentState state,
            string startEvent, string endEvent, System.Action<string, string> setText)
        {
            SetState(state);

            var list = m_Config.recognitionWordList;

            if (list == null)
            {
                AbortTrial($"{phase} recognition cannot run: no recognition word list");
                yield break;
            }

            if (!list.Validate(phase, out var problem))
            {
                AbortTrial($"{phase} recognition cannot run: {problem}");
                yield break;
            }

            // Seeded from the session seed and the phase, so the order is reproducible from the
            // run record AND differs between the immediate and delayed phases.
            var seed = unchecked((int)(m_SessionSeed + (phase == RecognitionPhase.Immediate ? 17 : 43)));
            var items = RecognitionSequence.Build(list, phase, seed);

            var store = phase == RecognitionPhase.Immediate
                ? m_ImmediateRecognitionItems
                : m_DelayedRecognitionItems;

            store.Clear();
            store.AddRange(items);

            Log(startEvent, e =>
            {
                e.protocolMode = ProtocolModeTag();
                e.recognitionPhase = phase.ToString().ToUpperInvariant();
                e.randomizationSeed = seed.ToString(CultureInfo.InvariantCulture);
                e.notes = $"item_count={items.Count}; target_count={list.targetCount}; " +
                          $"lure_count={list.LureCount(phase)}; " +
                          list.DescribeProvenance(phase);
            });

            setText?.Invoke(ExperimentLocalization.Get(LocKeys.RecognitionPrompt), string.Empty);

            // OFF AT EVERY PHASE BOUNDARY. Entering a phase always starts with the developer
            // overlay down, whatever it was doing in the previous one.
            ResetDeveloperCheatsheet();

            if (m_UI != null)
                m_UI.ShowRecognitionCounter(true);

            if (m_RecognitionPanel != null)
                m_RecognitionPanel.ShowForPhase(phase);

            for (var i = 0; i < items.Count; i++)
            {
                if (m_State == ExperimentState.Aborted)
                    break;

                // 1-BASED FOR THE PARTICIPANT, from the loop index rather than from
                // presentationOrder: the loop index is what is actually being presented, and
                // reading it here leaves RecognitionItem.presentationOrder's 0-based meaning
                // exactly as the CSV and every analysis already rely on. items.Count is the
                // phase's OWN length — nothing here knows or assumes 30, so a change to the
                // delayed composition is followed automatically.
                yield return RunRecognitionItem(items[i], phase, i + 1, items.Count);
            }

            if (m_RecognitionPanel != null)
                m_RecognitionPanel.Show(false);

            if (m_UI != null)
            {
                m_UI.ClearWordDisplay();
                m_UI.ShowRecognitionCounter(false);
            }

            // And off again on the way out, so nothing survives into Area B or the results.
            ResetDeveloperCheatsheet();

            Log(endEvent, e =>
            {
                e.protocolMode = ProtocolModeTag();
                e.recognitionPhase = phase.ToString().ToUpperInvariant();

                // Transparent tallies only. Deliberately NOT a composite or derived score.
                e.notes = RecognitionSequence.Summarise(store);
            });
        }

        /// <summary>
        /// One recognition item: show it, arm the buttons, wait for exactly one answer.
        ///
        /// TIMEOUT IS NOT AN ERROR. An unanswered item is logged as NO_RESPONSE and the sequence
        /// moves on; it is never silently scored as a miss or a false alarm, because "did not
        /// answer" and "answered wrongly" are different facts about the participant.
        /// </summary>
        IEnumerator RunRecognitionItem(RecognitionItem item, RecognitionPhase phase,
            int itemNumber, int itemTotal)
        {
            m_PendingRecognitionResponse = RecognitionResponse.None;

            // Cleared with the pending response so a stale stamp from the previous item can
            // never be attributed to this one.
            m_RecognitionResponseAcceptedRealtime = -1d;

            if (m_Audio != null)
                m_Audio.PlayCue(AudioCue.StimulusTransition);

            // The QA line for THIS item, built before it is shown so a toggle at any point
            // during the item describes the item actually on screen. Derived from
            // item.itemClass — the existing source of truth — and from nothing else. It creates
            // no classifier: the Target/Lure -> correct-answer mapping here is a restatement of
            // what RecognitionItem.outcome already encodes, used for display only, and no code
            // path reads it back.
            m_DeveloperCheatsheetText = BuildDeveloperCheatsheetText(item);

            if (m_UI != null)
            {
                m_UI.SetWordDisplay(item.word);

                // WITH the word, in the same frame — the counter must never describe the
                // previous item. Numbers come from the loop, not from any stored position.
                m_UI.SetRecognitionCounter(ExperimentLocalization.Format(
                    LocKeys.RecognitionItemProgress,
                    "N", itemNumber.ToString(CultureInfo.InvariantCulture),
                    "TOTAL", itemTotal.ToString(CultureInfo.InvariantCulture)));

                // Only repaints the overlay if it is already on; it never raises it by itself.
                if (m_DeveloperCheatsheetOn)
                    SetDeveloperCheatsheetVisible(true);
            }

            var stimulusShownRealtime = Time.realtimeSinceStartupAsDouble;

            // The number this whole fix is about: from the previous item's response leaving the
            // wait loop, to this item's word actually being on screen.
            if (m_LogRecognitionTiming && m_RecognitionWaitExitedRealtime >= 0d)
            {
                var responseToNextMs =
                    (stimulusShownRealtime - m_RecognitionResponseAcceptedRealtime) * 1000d;
                var waitExitToNextMs =
                    (stimulusShownRealtime - m_RecognitionWaitExitedRealtime) * 1000d;

                Debug.Log($"[RecognitionTiming] response_to_next_ms={responseToNextMs:F1}; " +
                          $"wait_exit_to_next_ms={waitExitToNextMs:F1}; " +
                          $"next_stimulus={item.itemId}");
            }

            m_RecognitionWaitExitedRealtime = -1d;

            item.onsetTime = m_Logger != null ? m_Logger.ElapsedSessionSeconds() : 0d;

            if (m_RecognitionPanel != null)
                m_RecognitionPanel.Arm();

            Log(EventTypes.RecognitionItemOnset, e =>
            {
                e.protocolMode = ProtocolModeTag();
                e.expectedWord = item.word;
                e.recognitionItemId = item.itemId;
                e.recognitionItemClass = item.itemClass.ToString().ToUpperInvariant();
                e.recognitionPhase = phase.ToString().ToUpperInvariant();
                e.recognitionPresentationOrder =
                    (item.presentationOrder + 1).ToString(CultureInfo.InvariantCulture);
                e.stimulusOnsetTime = item.onsetTime.ToString("F6", CultureInfo.InvariantCulture);
            });

            var deadline = Time.time + m_Config.recognitionResponseTimeoutSeconds;

            while (m_PendingRecognitionResponse == RecognitionResponse.None &&
                   Time.time < deadline &&
                   m_State != ExperimentState.Aborted)
            {
                yield return null;
            }

            var waitExitedRealtime = Time.realtimeSinceStartupAsDouble;
            m_RecognitionWaitExitedRealtime = waitExitedRealtime;

            if (m_LogRecognitionTiming)
            {
                var answered = m_PendingRecognitionResponse != RecognitionResponse.None;

                // accepted_to_exit is the cost of OUR control flow between the answer arriving
                // and the wait releasing. It should be a single frame; anything larger means
                // something is still blocking the loop.
                var acceptedToExitMs = answered && m_RecognitionResponseAcceptedRealtime >= 0d
                    ? (waitExitedRealtime - m_RecognitionResponseAcceptedRealtime) * 1000d
                    : -1d;

                Debug.Log($"[RecognitionTiming] item={item.itemId}; " +
                          $"answered={(answered ? "TRUE" : "FALSE")}; " +
                          $"stimulus_to_exit_ms={(waitExitedRealtime - stimulusShownRealtime) * 1000d:F1}; " +
                          $"accepted_to_wait_exit_ms={acceptedToExitMs:F1}");
            }

            if (m_RecognitionPanel != null)
                m_RecognitionPanel.Disarm();

            item.response = m_PendingRecognitionResponse;
            item.responseTime = m_Logger != null ? m_Logger.ElapsedSessionSeconds() : 0d;

            if (item.response == RecognitionResponse.None)
            {
                Log(EventTypes.RecognitionItemTimeout, e =>
                {
                    e.protocolMode = ProtocolModeTag();
                    e.expectedWord = item.word;
                    e.recognitionItemId = item.itemId;
                    e.recognitionItemClass = item.itemClass.ToString().ToUpperInvariant();
                    e.recognitionPhase = phase.ToString().ToUpperInvariant();
                    e.recognitionPresentationOrder =
                        (item.presentationOrder + 1).ToString(CultureInfo.InvariantCulture);
                    e.recognitionResponse = "NONE";
                    e.recognitionOutcome = RecognitionOutcome.NoResponse.ToString().ToUpperInvariant();
                    e.notes = $"no response within " +
                              $"{m_Config.recognitionResponseTimeoutSeconds:F1} s; " +
                              "NOT scored as an error";
                });
            }
            else
            {
                // Registration only — the same sound whatever the answer was.
                if (m_Audio != null)
                    m_Audio.PlayCue(AudioCue.ResponseConfirm);

                Log(EventTypes.RecognitionResponse, e =>
                {
                    e.protocolMode = ProtocolModeTag();
                    e.expectedWord = item.word;
                    e.recognitionItemId = item.itemId;
                    e.recognitionItemClass = item.itemClass.ToString().ToUpperInvariant();
                    e.recognitionPhase = phase.ToString().ToUpperInvariant();
                    e.recognitionPresentationOrder =
                        (item.presentationOrder + 1).ToString(CultureInfo.InvariantCulture);
                    e.recognitionResponse = item.response.ToString().ToUpperInvariant();
                    e.recognitionOutcome = item.outcome.ToString().ToUpperInvariant();
                    e.recognitionReactionTimeMs =
                        item.reactionTimeMs.ToString("F1", CultureInfo.InvariantCulture);
                    e.responseTimeMs =
                        item.reactionTimeMs.ToString("F1", CultureInfo.InvariantCulture);
                    e.stimulusOnsetTime =
                        item.onsetTime.ToString("F6", CultureInfo.InvariantCulture);
                    e.notes = item.ToNotes();
                });
            }

            if (m_UI != null)
                m_UI.ClearWordDisplay();

            // ONE FRAME, not a timed pause.
            //
            // The deliberate 0.15 s inter-item gap is GONE: at ~30 items it accumulated into a
            // stretch of blank screen that the headset test reported as tiring, and it was never
            // a methodological parameter — only a crude way of separating one item from the next.
            //
            // A single frame is the minimum this loop technically needs. It lets the Disarm above
            // and the ClearWordDisplay take effect before the next iteration arms new buttons and
            // writes a new word, so the two items can never be mutated within one frame. At
            // 72 Hz that is ~14 ms — below the threshold at which a blank interval is perceived.
            //
            // Input safety no longer depends on this delay at all: RecognitionResponseButton
            // holds back its own arming until the trigger is released, so a still-held press
            // cannot answer the next word however quickly it appears.
            yield return null;
        }

        /// <summary>
        /// The developer QA line for one item: what it is, and what the correct answer would be.
        ///
        /// READ-ONLY over <see cref="RecognitionItem.itemClass"/>. It builds a string and
        /// returns it. It does not classify, does not store, does not compare against a response
        /// and is never consulted when an outcome is computed — <c>RecognitionItem.outcome</c>
        /// remains the only thing that decides Hit / Miss / CorrectRejection / FalseAlarm, and
        /// it is a derived property that cannot be written to.
        ///
        /// English literal and NOT localized, deliberately: it is a developer tool, like the
        /// developer navigation panel, and translating it would imply a participant might read it.
        /// </summary>
        static string BuildDeveloperCheatsheetText(RecognitionItem item)
        {
            if (item == null)
                return string.Empty;

            var isTarget = item.itemClass == RecognitionItemClass.Target;

            return "DEVELOPER QA\n" +
                   (isTarget ? "TARGET" : "LURE") + "\n" +
                   "Correct: " + (isTarget ? "SEEN BEFORE" : "NOT SEEN BEFORE");
        }

        /// <summary>
        /// Receives a button press. Records only the FIRST answer for the current item; the
        /// buttons latch themselves as well, so a duplicate has to get past both.
        /// </summary>
        void OnRecognitionResponse(RecognitionResponse response)
        {
            if (m_PendingRecognitionResponse != RecognitionResponse.None)
                return;

            m_PendingRecognitionResponse = response;

            // Stamped at the instant the answer arrives, BEFORE the coroutine has noticed it,
            // so the measurement includes any latency inside our own control flow rather than
            // hiding it.
            m_RecognitionResponseAcceptedRealtime = Time.realtimeSinceStartupAsDouble;
        }

        /// <summary>The protocol tag written to every row, so a file states its own protocol.</summary>
        string ProtocolModeTag() =>
            m_Config != null && m_Config.protocolMode == VerbalProtocolMode.Recognition
                ? "RECOGNITION"
                : "FREE_RECALL";

        void SetAreaAInstructionAndStatus(string instruction, string status)
        {
            if (m_UI == null)
                return;

            if (instruction != null)
                m_UI.SetAreaAInstruction(instruction);

            if (status != null)
                m_UI.SetAreaAStatus(status);
        }

        void SetAreaCInstructionAndStatus(string instruction, string status)
        {
            if (m_UI == null)
                return;

            if (instruction != null)
                m_UI.SetAreaCInstruction(instruction);

            if (status != null)
                m_UI.SetAreaCStatus(status);
        }

        /// <summary>
        /// Shared recall procedure: beep, start recording, count down, stop recording.
        /// Immediate and delayed recall differ only in their event names and duration, so
        /// they share one implementation — the two phases can never drift apart.
        /// </summary>
        IEnumerator RunRecall(string recallPhase, string beepEvent, string startEvent,
            string endEvent, ExperimentState recallState, float maxDurationSeconds,
            string promptText, System.Action<string, string> setText)
        {
            // The beep marks the moment the participant is told to start speaking, so it is
            // logged when the sound ACTUALLY starts, not when this state is entered. If the
            // audio path is broken the event is still logged — silently dropping it would
            // hide the problem — but it carries audio_confirmed=FALSE and is accompanied by
            // an AUDIO_STIMULUS_FAILED row, so no analysis can mistake it for a real beep.
            AudioCueHandle beep = null;

            if (m_Audio != null)
            {
                beep = m_Audio.PlayCue(AudioCue.RecallBeep);

                while (!m_Audio.HasOnsetPassed(beep))
                    yield return null;

                m_Audio.ConfirmPlayback(beep);
            }

            var beepHandle = beep;
            Log(beepEvent, e =>
            {
                e.recallPhase = recallPhase;
                e.notes = beepHandle != null
                    ? beepHandle.ToNotes()
                    : "audio_confirmed=FALSE; reason=no ExperimentAudio assigned";
            });

            ReportStimulusFailure(beepHandle, "RecallBeep", recallPhase);

            // Let the beep finish before recording starts, so the participant's first word is
            // not captured over the top of the cue.
            if (beepHandle != null && beepHandle.clipLength > 0f)
                yield return new WaitForSeconds(beepHandle.clipLength);

            SetState(recallState);

            if (m_Voice != null)
            {
                m_Voice.expectedWords = m_TrialWords;
                ApplyRecallDetectionSettings();
                m_Voice.StartRecording(recallPhase);
            }

            // RECORDING UI = "the microphone is capturing", NOT "we have heard you".
            //
            // Set here, in the same statement block that starts the capture, so it is on screen
            // from the first frame of the response window. It previously depended on the speech
            // detector, which made the participant-facing UI report an INTERNAL detector state:
            // someone who had not yet been heard saw a different message from someone who had,
            // and the message appeared to arrive late. The detector's state is now never shown
            // to the participant at all — it only appears in the diagnostics.
            setText?.Invoke(promptText, ExperimentLocalization.Get(LocKeys.Recording));

            Log(startEvent, e =>
            {
                e.recallPhase = recallPhase;
                e.expectedWord = string.Join(" ", m_TrialWords);
                e.notes = string.Format(CultureInfo.InvariantCulture,
                    "max_duration_s={0:F1}; stop_rule=speech_then_{1:F1}s_silence_or_max_duration; " +
                    "speech_threshold={2:F4}; silence_threshold={3:F4}; min_speech_s={4:F2}",
                    maxDurationSeconds, m_Config.recallSilenceStopSeconds,
                    m_Config.speechStartThreshold, m_Config.silenceThreshold,
                    m_Config.minimumSpeechDuration);
            });

            // ---- Wait for whichever comes first: the silence detector or the hard maximum ----
            //
            // Polled every frame rather than on a coarse tick, so the recording ends promptly
            // once the criterion is met. The detector can only ever ARM after speech, so a
            // participant who is still thinking is never cut off — see RecallSilenceDetector.
            var elapsed = 0f;
            var stopReason = RecallStopReasons.MaxDuration;

            while (elapsed < maxDurationSeconds)
            {
                if (m_Voice != null && m_Voice.autoStopRequested)
                {
                    stopReason = string.IsNullOrEmpty(m_Voice.autoStopReason)
                        ? RecallStopReasons.SilenceAfterSpeech
                        : m_Voice.autoStopReason;
                    break;
                }

                // A restart, an abort or the session ending pulls the flow out from under us.
                if (m_State != recallState)
                    yield break;

                yield return null;
                elapsed += Time.deltaTime;
            }

            // The status text is deliberately NOT touched inside the loop. It says "Recording…"
            // for the whole response window and nothing the detector does changes it — no
            // countdown either, since with an adaptive stop the remaining time is not knowable
            // and a number that turns out to be wrong is worse than no number.

            RecordingInfo info = null;
            if (m_Voice != null)
                info = m_Voice.StopRecording(stopReason);

            Log(endEvent, e =>
            {
                e.recallPhase = recallPhase;
                e.notes = info != null
                    ? string.Format(CultureInfo.InvariantCulture,
                        "audio_captured={0}; duration_ms={1:F1}; " +
                        "actual_recording_duration_s={2:F3}; termination_reason={3}; " +
                        "speech_detected={4}; max_duration_s={5:F1}",
                        info.audioCaptured ? "TRUE" : "FALSE", info.durationMs,
                        info.actualDurationSeconds, info.stopReason,
                        info.speechDetected ? "TRUE" : "FALSE", maxDurationSeconds)
                    : "no VoiceRecallManager assigned";
            });

            setText?.Invoke(null, ExperimentLocalization.Get(LocKeys.RecordingComplete));
        }

        // ---------------------------------------------------------------------------------
        // AREA B — chair instruction and selection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The Area B block: chairTrialsPerRun chair trials, back to back, without leaving the
        /// showroom and without a button press between them.
        /// </summary>
        IEnumerator RunAreaB()
        {
            // ---- General task instructions, ONCE per block ---------------------------------
            // The participant learns the task before any target exists. Nothing here is timed
            // and no response clock runs, so however long they take to read costs the data
            // nothing — the interval is recorded as its own AREA_B_INSTRUCTIONS_ONSET ->
            // AREA_B_READY pair rather than being buried inside a trial's response time.
            if (!m_AreaBInstructionsShown)
                yield return RunAreaBInstructions();

            var trialCount = m_UsingGeneratedTrials
                ? m_ChairPlans.Count
                : Mathf.Max(1, m_Config.chairTrialsPerRun);

            if (trialCount == 0)
            {
                // Generation failed earlier and was already reported. Do not fabricate a trial;
                // let the participant move on so the session (and its recall data) survives.
                Debug.LogError("[IKEA_EEG] Area B has no chair trials to run. Skipping to the " +
                               "Area C transition.");

                Log(EventTypes.Warning, e => e.notes = "area_b_entered_with_zero_chair_trials");
                yield return FinishChairBlock(0);
                yield break;
            }

            for (var i = 0; i < trialCount; i++)
            {
                yield return RunChairTrial(i, trialCount);

                // A restart/end/abort during a trial takes the flow out from under us.
                if (m_State == ExperimentState.Ended || m_State == ExperimentState.Idle)
                    yield break;

                if (i < trialCount - 1)
                {
                    SetState(ExperimentState.ChairInterTrialInterval);

                    if (m_UI != null)
                    {
                        m_UI.SetAreaBFeedback(string.Empty);
                        m_UI.SetAreaBStatus(ExperimentLocalization.Get(LocKeys.NextTask));
                    }

                    yield return new WaitForSeconds(m_Config.interTrialIntervalSeconds);
                }
            }

            yield return FinishChairBlock(trialCount);

            m_Flow = null;
        }

        /// <summary>
        /// The general executive-task instruction screen. Shown once per Area B block and gated
        /// by READY; trials 2+ never see it again (only a restart or a block reset brings it
        /// back).
        /// </summary>
        IEnumerator RunAreaBInstructions()
        {
            SetState(ExperimentState.AreaBInstructions);

            m_AreaBReadyPressed = false;

            if (m_UI != null)
            {
                // The large overlay carries the task text and the shape definitions, standing
                // between the participant and the chairs. The far instruction panel is cleared
                // so the two cannot be read at once.
                m_UI.SetAreaBOverlayText(ExperimentLocalization.Get(LocKeys.ExecutiveTaskInstructions));
                m_UI.ShowAreaBInstructionOverlay(true);

                m_UI.SetChairInstruction(string.Empty);
                m_UI.SetAreaBFeedback(string.Empty);
                m_UI.SetAreaBStatus(string.Empty);
                m_UI.ShowExitToAreaCButton(false);
                m_UI.ShowReadyButton(true);

                m_UI.ShowShapeLegend(m_Config.showShapeLegendOnInstructions);
            }

            // The GENERAL task instructions are narrated. This explains HOW the task works and
            // contains no colour, size or shape: the per-trial target is presented visually by
            // SetChairInstruction and is never spoken, because working it out IS the task.
            //
            // READY stays participant-controlled — nothing here starts the block.
            SpeakAreaBInstructions();

            // One frame so the panel is actually on screen before its onset is timestamped.
            yield return new WaitForEndOfFrame();

            var onsetSeconds = m_Logger != null ? m_Logger.clock.RelativeSeconds() : 0d;

            Log(EventTypes.AreaBInstructionsOnset, e =>
            {
                e.chairTrialCount = (m_UsingGeneratedTrials
                    ? m_ChairPlans.Count
                    : Mathf.Max(1, m_Config.chairTrialsPerRun))
                    .ToString(CultureInfo.InvariantCulture);
                e.notes = "general executive-task instructions shown; no target presented; " +
                          "no response timer running";
            });

            while (!m_AreaBReadyPressed)
            {
                // A restart or session end pulls the participant out of Area B entirely.
                if (m_State != ExperimentState.AreaBInstructions)
                    yield break;

                yield return null;
            }

            var readingSeconds = (m_Logger != null ? m_Logger.clock.RelativeSeconds() : 0d)
                                 - onsetSeconds;

            Log(EventTypes.AreaBReady, e => e.notes = string.Format(CultureInfo.InvariantCulture,
                "instruction_reading_duration_s={0:F3}; excluded_from_response_time=TRUE",
                readingSeconds));

            m_AreaBInstructionsShown = true;

            if (m_UI != null)
            {
                m_UI.ShowReadyButton(false);
                m_UI.SetChairInstruction(string.Empty);

                // Overlay AND legend down for the rest of the block. The chairs become visible
                // at this moment and not before, and from here on the participant works from
                // the three target attributes alone.
                m_UI.ShowAreaBInstructionOverlay(false);
                m_UI.ShowShapeLegend(false);
            }

            Debug.Log($"[IKEA_EEG] Area B instructions read in {readingSeconds:F2} s " +
                      "(excluded from every chair response time).");
        }

        /// <summary>One chair trial: prepare, present target, open selection, score, feed back.</summary>
        IEnumerator RunChairTrial(int index, int trialCount)
        {
            var trialNumber = index + 1;
            var plan = m_UsingGeneratedTrials && index < m_ChairPlans.Count
                ? m_ChairPlans[index]
                : null;

            SetState(ExperimentState.ChairInstruction);

            // ---- Prepare -------------------------------------------------------------------
            m_ChairTask.ResetTask();

            if (plan != null)
            {
                if (!m_ChairTask.ApplyTrialPlan(plan, out var applyProblem))
                {
                    Debug.LogError($"[IKEA_EEG] Chair trial {trialNumber} could not be applied to " +
                                   $"the room: {applyProblem}. The trial is skipped and recorded " +
                                   "as invalid.");

                    Log(EventTypes.Warning, e =>
                    {
                        e.chairTrialIndex = trialNumber.ToString(CultureInfo.InvariantCulture);
                        e.chairTrialCount = trialCount.ToString(CultureInfo.InvariantCulture);
                        e.notes = $"chair_trial_not_applied: {applyProblem}";
                    });

                    RecordInvalidChairTrial(plan, trialNumber, trialCount, applyProblem);
                    yield break;
                }
            }
            else
            {
                // Legacy mode: the authored layout stays as built and the fixed target is used.
                m_ChairTask.SetTarget(m_Config.targetChair);
            }

            var target = m_ChairTask.targetSpec;
            var difficultyLabel = plan != null ? plan.DifficultyLabelUpper() : string.Empty;

            // The three attributes, stacked, with a short header. No explanatory text: the task
            // was explained on the READY screen and the response clock runs while this is read.
            var instruction = m_ChairTask.BuildTargetText(ExperimentLocalization.Get(LocKeys.ChairTargetHeader));

            m_CurrentChairTrial = new ChairTrialResult
            {
                trialIndex = trialNumber,
                trialCount = trialCount,
                difficulty = plan?.difficulty ?? DifficultyLevel.Medium,
                target = target,
                targetChairId = plan?.targetChairId ?? FindTargetChairId(target),
                targetSlotIndex = plan?.targetSlotIndex ?? -1,
                trialSeed = plan?.trialSeed ?? 0L,
                valid = false,
                invalidReason = "no response yet",
            };

            void StampTrial(ExperimentEvent e)
            {
                e.chairTrialIndex = trialNumber.ToString(CultureInfo.InvariantCulture);
                e.chairTrialCount = trialCount.ToString(CultureInfo.InvariantCulture);
                e.difficulty = difficultyLabel;
                e.targetColor = target.color.ToString();
                e.targetSize = target.size.ToString();
                e.targetShape = target.shape.ToString();
            }

            Log(EventTypes.ChairTrialPrepared, e =>
            {
                StampTrial(e);
                e.objectId = m_CurrentChairTrial.targetChairId;
                e.notes = plan != null
                    ? plan.Describe()
                    : $"legacy fixed target {target}; authored layout unchanged";
            });

            // ---- Pre-target interval --------------------------------------------------------
            // The chairs are already arranged; the target is not shown yet. This gap is not
            // part of the response time — it only separates one trial's feedback from the next
            // trial's stimulus.
            if (m_UI != null)
            {
                m_UI.SetChairInstruction(string.Empty);
                m_UI.SetAreaBFeedback(string.Empty);
                m_UI.SetAreaBStatus(ExperimentLocalization.Format(LocKeys.TaskProgress,
                    "N", trialNumber.ToString(CultureInfo.InvariantCulture),
                    "TOTAL", trialCount.ToString(CultureInfo.InvariantCulture)));
                m_UI.ShowExitToAreaCButton(false);
                m_UI.ShowReadyButton(false);
            }

            Log(EventTypes.ChairTrialStart, StampTrial);

            if (m_Config.playChairInstructionCue && m_Audio != null)
                StartCoroutine(ConfirmCueAsync(m_Audio.PlayCue(AudioCue.InstructionCue),
                    $"ChairInstructionCue_T{trialNumber}"));

            if (m_Config.preTargetIntervalSeconds > 0f)
                yield return new WaitForSeconds(m_Config.preTargetIntervalSeconds);

            // ---- TARGET ONSET = RESPONSE TIME ZERO -------------------------------------------
            //
            // RESPONSE TIME IS DEFINED AS: the interval from the moment the three target
            // attributes become visible to the participant, to the moment the first valid chair
            // selection is registered.
            //
            // It therefore EXCLUDES: the general task instructions and the time spent reading
            // them, the pre-target interval, and everything before entering Area B. It INCLUDES
            // the time spent reading the three attributes themselves, because that reading is
            // part of the task on every trial and is the same demand on every trial.
            //
            // The text is written, then one frame is allowed to elapse so the canvas has
            // actually rendered it, and only then is onset stamped and the clock started. The
            // two events are logged on the SAME frame with no yield between them, and the
            // measured offset between them is recorded on the timer event.
            if (m_UI != null)
            {
                m_UI.SetChairInstruction(instruction);
                m_UI.SetAreaBStatus(string.Empty);
            }

            yield return new WaitForEndOfFrame();

            var targetOnsetSeconds = m_Logger != null ? m_Logger.clock.RelativeSeconds() : 0d;

            Log(EventTypes.ChairTargetOnset, e =>
            {
                StampTrial(e);
                e.notes = $"target=\"{instruction}\"; response_time_zero=TRUE";
            });

            // Preserved for continuity with the pre-multi-trial data: CHAIR_INSTRUCTION_ONSET
            // now marks the TARGET onset (it used to mark a target shown some seconds before
            // selection opened). Same moment as CHAIR_TARGET_ONSET.
            Log(EventTypes.ChairInstructionOnset, e =>
            {
                StampTrial(e);
                e.notes = $"instruction=\"{instruction}\"; " +
                          "semantics=target_onset (identical to CHAIR_TARGET_ONSET)";
            });

            Log(EventTypes.ChairInstructionComplete, e =>
            {
                StampTrial(e);
                e.notes = "retained for continuity; the target stays on screen for the whole " +
                          "response window";
            });

            // ---- Selection ------------------------------------------------------------------
            SetState(ExperimentState.ChairSelection);

            m_ChairSelectionReceived = false;

            // BeginSelection restarts the response timer in its first line, so this reading is
            // taken immediately before the call rather than after it.
            var timerStartSeconds = m_Logger != null ? m_Logger.clock.RelativeSeconds() : 0d;
            m_ChairTask.BeginSelection();

            Log(EventTypes.ChairSelectionTimerStart, e =>
            {
                StampTrial(e);
                e.notes = string.Format(CultureInfo.InvariantCulture,
                    "target_onset_to_timer_start_ms={0:F2}; rt_definition=target_onset_to_selection",
                    (timerStartSeconds - targetOnsetSeconds) * 1000d);
            });

            // No response deadline: the protocol does not time out a chair trial. If one is
            // wanted later it belongs here, as an explicit configurable duration.
            while (!m_ChairSelectionReceived)
            {
                if (m_State != ExperimentState.ChairSelection)
                    yield break;      // restarted, ended or aborted from outside

                yield return null;
            }

            // ---- Feedback + trial end --------------------------------------------------------
            SetState(ExperimentState.ChairTrialFeedback);

            if (m_UI != null)
            {
                m_UI.SetAreaBStatus(string.Empty);

                var feedback = ExperimentLocalization.Get(LocKeys.ChairSelected);
                if (m_Config.showCorrectnessToParticipant && m_CurrentChairTrial != null)
                {
                    feedback = (m_CurrentChairTrial.correct
                        ? ExperimentLocalization.Get(LocKeys.AnswerCorrect)
                        : ExperimentLocalization.Get(LocKeys.AnswerIncorrect)) + feedback;
                }

                m_UI.SetAreaBFeedback(feedback);
            }

            yield return new WaitForSeconds(m_Config.chairFeedbackDurationSeconds);

            var completed = m_CurrentChairTrial;
            Log(EventTypes.ChairTrialEnd, e =>
            {
                StampTrial(e);

                if (completed != null && completed.selectionMade)
                {
                    e.objectId = completed.selectedChairId;
                    e.selectedColor = completed.selected.color.ToString();
                    e.selectedSize = completed.selected.size.ToString();
                    e.selectedShape = completed.selected.shape.ToString();
                    e.correct = completed.correct ? "TRUE" : "FALSE";
                    e.responseTimeMs = completed.responseTimeMs.ToString("F1", CultureInfo.InvariantCulture);
                    e.notes = $"response_time_s=" +
                              $"{completed.responseTimeSeconds.ToString("F4", CultureInfo.InvariantCulture)}; " +
                              $"attribute_matches={completed.attributeMatchCount}/3; " +
                              $"trial_valid={(completed.valid ? "TRUE" : "FALSE")}";
                }
                else
                {
                    e.notes = "no response recorded for this chair trial";
                }
            });

            m_CurrentChairTrial = null;
        }

        /// <summary>Shows the end-of-block message and opens the Area C transition.</summary>
        IEnumerator FinishChairBlock(int trialCount)
        {
            var correct = m_SessionResults.chairTrialsCorrect;
            var scored = m_SessionResults.chairTrialsScored;

            Log(EventTypes.ChairBlockComplete, e =>
            {
                e.chairTrialCount = trialCount.ToString(CultureInfo.InvariantCulture);
                e.notes = $"chair_trials_completed={scored}/{trialCount}; " +
                          $"chair_trials_correct={correct}";
            });

            SetState(ExperimentState.ReadyForAreaC);

            if (m_UI != null)
            {
                // Participant-facing result: heading + "N / M correct" over the VALID scored
                // trials, and nothing else. Researcher detail stays in the researcher summary.
                m_UI.SetChairInstruction(m_SessionResults.BuildParticipantBlockResult(
                    ExperimentLocalization.Get(LocKeys.ExecutiveTaskComplete),
                    ExperimentLocalization.Get(LocKeys.BlockFooter)));

                m_UI.SetAreaBStatus(string.Empty);
                m_UI.SetAreaBFeedback(string.Empty);
                m_UI.ShowReadyButton(false);
                m_UI.ShowExitToAreaCButton(true);
            }

            yield break;
        }

        /// <summary>Records a chair trial that never ran, so the block's trial count stays honest.</summary>
        void RecordInvalidChairTrial(ChairTrialPlan plan, int trialNumber, int trialCount,
            string reason)
        {
            m_SessionResults.chairTrials.Add(new ChairTrialResult
            {
                trialIndex = trialNumber,
                trialCount = trialCount,
                difficulty = plan?.difficulty ?? DifficultyLevel.Medium,
                target = plan?.target ?? m_ChairTask.targetSpec,
                targetChairId = plan?.targetChairId ?? string.Empty,
                targetSlotIndex = plan?.targetSlotIndex ?? -1,
                trialSeed = plan?.trialSeed ?? 0L,
                selectionMade = false,
                valid = false,
                invalidReason = reason,
            });
        }

        string FindTargetChairId(ChairSpec target)
        {
            if (m_ChairTask == null)
                return string.Empty;

            foreach (var chair in m_ChairTask.chairs)
            {
                if (chair != null && chair.spec.Matches(target))
                    return chair.chairId;
            }

            return string.Empty;
        }

        void OnChairHovered(ChairTarget chair)
        {
            if (m_Config == null || !m_Config.logChairHoverEvents || chair == null)
                return;

            var trial = m_CurrentChairTrial;
            Log(EventTypes.ChairHoverEnter, e =>
            {
                e.objectId = chair.chairId;
                e.selectedColor = chair.spec.color.ToString();
                e.selectedSize = chair.spec.size.ToString();
                e.selectedShape = chair.spec.shape.ToString();

                if (trial != null)
                {
                    e.chairTrialIndex = trial.trialIndex.ToString(CultureInfo.InvariantCulture);
                    e.chairTrialCount = trial.trialCount.ToString(CultureInfo.InvariantCulture);
                    e.difficulty = trial.DifficultyLabel();
                }
            });
        }

        void OnChairSelectionMade(ChairSelectionResult result)
        {
            if (m_State != ExperimentState.ChairSelection)
                return;

            if (m_Audio != null)
                StartCoroutine(ConfirmCueAsync(m_Audio.PlayCue(AudioCue.ChairSelection),
                    "ChairSelectionFeedback"));

            m_Result.chairSelectionMade = true;
            m_Result.chairResult = result;
            m_Result.chairTrialsCompleted++;

            var trial = m_CurrentChairTrial;
            var trialIndex = trial?.trialIndex ?? 0;
            var trialCount = trial?.trialCount ?? 0;
            var difficultyLabel = trial != null ? trial.DifficultyLabel() : string.Empty;

            if (trial != null)
            {
                trial.selectionMade = true;
                trial.selectedChairId = result.chairId;
                trial.selected = result.selected;
                trial.attributeMatchCount = result.attributeMatchCount;
                trial.correct = result.correct;
                trial.responseTimeMs = result.responseTimeMs;
                trial.responseTimeSeconds = result.responseTimeMs / 1000d;
                trial.valid = true;
                trial.invalidReason = string.Empty;

                m_SessionResults.chairTrials.Add(trial);
            }

            var rt = result.responseTimeMs.ToString("F1", CultureInfo.InvariantCulture);

            void Fill(ExperimentEvent e)
            {
                FillChairFields(e, result, rt);

                if (trial == null)
                    return;

                e.chairTrialIndex = trialIndex.ToString(CultureInfo.InvariantCulture);
                e.chairTrialCount = trialCount.ToString(CultureInfo.InvariantCulture);
                e.difficulty = difficultyLabel;
            }

            Log(EventTypes.ChairSelected, Fill);
            Log(result.correct ? EventTypes.ChairCorrect : EventTypes.ChairIncorrect, Fill);
            Log(EventTypes.ChairSelectionEnd, e =>
            {
                Fill(e);
                e.notes = $"attribute_matches={result.attributeMatchCount}/3; " +
                          "one selection per chair trial; further input is ignored";
            });

            // Hand control back to the chair-trial coroutine, which owns the timing from here.
            m_ChairSelectionReceived = true;
        }

        /// <summary>
        /// Emits an AUDIO_STIMULUS_FAILED row when a cue did not reach the participant.
        /// Kept separate from the onset event so that analysis can filter compromised trials
        /// with a single event_type query.
        /// </summary>
        void ReportStimulusFailure(AudioCueHandle handle, string stimulusId, string recallPhase)
        {
            if (handle != null && handle.confirmed)
                return;

            Log(EventTypes.AudioStimulusFailed, e =>
            {
                e.objectId = stimulusId;
                e.recallPhase = recallPhase;
                e.notes = handle != null
                    ? handle.ToNotes()
                    : "audio_confirmed=FALSE; reason=no ExperimentAudio assigned";
            });
        }

        /// <summary>
        /// Confirms a non-timing-critical cue (instruction, selection feedback) one frame
        /// after it was scheduled, without holding up the state machine.
        /// </summary>
        IEnumerator ConfirmCueAsync(AudioCueHandle handle, string stimulusId)
        {
            if (handle == null || m_Audio == null)
                yield break;

            while (!m_Audio.HasOnsetPassed(handle))
                yield return null;

            m_Audio.ConfirmPlayback(handle);
            ReportStimulusFailure(handle, stimulusId, string.Empty);
        }

        static void FillChairFields(ExperimentEvent e, ChairSelectionResult result, string responseTimeMs)
        {
            e.objectId = result.chairId;
            e.targetColor = result.target.color.ToString();
            e.targetSize = result.target.size.ToString();
            e.targetShape = result.target.shape.ToString();
            e.selectedColor = result.selected.color.ToString();
            e.selectedSize = result.selected.size.ToString();
            e.selectedShape = result.selected.shape.ToString();
            e.correct = result.correct ? "TRUE" : "FALSE";
            e.responseTimeMs = responseTimeMs;
        }

        // ---------------------------------------------------------------------------------
        // AREA C — delayed recall and results
        // ---------------------------------------------------------------------------------

        IEnumerator RunAreaC()
        {
            // The Recognition protocol substitutes delayed RECOGNITION for delayed recall.
            // Everything after it — run duration capture, results, the participant panel — is
            // shared, so this branch rejoins the common path rather than duplicating it.
            if (m_Config.protocolMode == VerbalProtocolMode.Recognition)
            {
                yield return RunDelayedRecognitionAreaC();

                if (m_State == ExperimentState.Aborted)
                {
                    m_Flow = null;
                    yield break;
                }
            }
            else
            {
                yield return RunFreeRecallAreaC();

                if (m_State == ExperimentState.Aborted)
                {
                    m_Flow = null;
                    yield break;
                }
            }

            // ---- Results -------------------------------------------------------------------
            // Captured HERE, before the participant panel below is built. The value used to be
            // written only while publishing, which happens at the end of this method — so the
            // panel read the field while it still held its post-Reset zero and printed
            // "0.000 s". Moving the capture earlier is the whole fix.
            CaptureRunDuration("cognitive run complete");

            // Captured HERE for the same reason the duration is: FinishAreaC builds the
            // participant panel from m_SessionResults, so anything the panel reads has to hold
            // this run's value BEFORE that call, not after it.
            CaptureRecognitionResults();

            FinishAreaC();
        }

        /// <summary>
        /// Area C for the Recognition protocol: instructions, then delayed recognition.
        ///
        /// The delayed item set is whatever the asset configures — its size is deliberately not
        /// fixed here, because the item-level structure of delayed recognition has not been
        /// established and hard-coding 15 or 30 would be inventing methodology.
        /// </summary>
        IEnumerator RunDelayedRecognitionAreaC()
        {
            SetState(ExperimentState.DelayedRecognition);

            if (m_UI != null)
            {
                // RECOGNITION wording, not the free-recall text. AreaCInstructions says
                // "repeat the five words that were presented" — spoken free recall — and is
                // still correct for FreeRecall, which is why it is routed around rather than
                // rewritten.
                m_UI.SetAreaCInstruction(
                    ExperimentLocalization.Get(LocKeys.RecognitionDelayedInstructions));
                m_UI.SetAreaCStatus(string.Empty);
                m_UI.SetResults(string.Empty);
                m_UI.ShowRestartButton(false);
                m_UI.ShowEndButton(false);
            }

            // The INSTRUCTION is narrated; the delayed stimulus words never are. Spoken here,
            // before the phase starts, and nothing inside the item loop plays narration at all.
            var delayedNarrationSeconds = SpeakDelayedRecognitionInstructions();

            if (m_Audio != null)
                StartCoroutine(ConfirmCueAsync(m_Audio.PlayCue(AudioCue.InstructionCue),
                    "AreaCInstructionCue"));

            // Same rule as Area A: hold the instruction until the voice has finished rather than
            // cutting it off at a configured duration. The configured value stays the floor.
            yield return new WaitForSeconds(Mathf.Max(
                m_Config.delayedInstructionDurationSeconds, delayedNarrationSeconds + 0.5f));

            yield return RunRecognitionPhase(RecognitionPhase.Delayed,
                ExperimentState.DelayedRecognition,
                EventTypes.DelayedRecognitionStart,
                EventTypes.DelayedRecognitionEnd,
                SetAreaCInstructionAndStatus);
        }

        /// <summary>
        /// Area C for the FreeRecall protocol. Extracted VERBATIM from the original RunAreaC so
        /// the two protocols can fork without the delayed-recall path changing at all.
        /// </summary>
        IEnumerator RunFreeRecallAreaC()
        {
            SetState(ExperimentState.DelayedRecall);

            if (m_UI != null)
            {
                m_UI.SetAreaCInstruction(ExperimentLocalization.Get(LocKeys.AreaCInstructions));
                m_UI.SetAreaCStatus(string.Empty);
                m_UI.SetResults(string.Empty);
                m_UI.ShowRestartButton(false);
                m_UI.ShowEndButton(false);
            }

            if (m_Audio != null)
                StartCoroutine(ConfirmCueAsync(m_Audio.PlayCue(AudioCue.InstructionCue),
                    "AreaCInstructionCue"));

            yield return new WaitForSeconds(m_Config.delayedInstructionDurationSeconds);

            yield return RunRecall(
                RecallPhases.Delayed,
                EventTypes.DelayedRecallBeep,
                EventTypes.DelayedRecallStart,
                EventTypes.DelayedRecallEnd,
                ExperimentState.DelayedRecall,
                m_Config.delayedRecallMaxDuration,
                ExperimentLocalization.Get(LocKeys.DelayedRecallPrompt),
                SetAreaCInstructionAndStatus);
        }

        /// <summary>
        /// Everything Area C does AFTER the verbal-memory phase, shared by both protocols:
        /// results, the participant panel, publishing. Unchanged from the original code.
        ///
        /// Plain method, not a coroutine: this tail of the original RunAreaC contained no yield,
        /// so making it IEnumerator would add a frame boundary that the original did not have.
        /// </summary>
        void FinishAreaC()
        {
            SetState(ExperimentState.Results);
            ResetDeveloperCheatsheet();

            if (m_Logger != null)
                m_Logger.EndTrial();

            // Participant-facing panel: neutral, no accuracy, no per-trial scoring. The
            // researcher's numbers go to the Console and to files, not into the headset.
            if (m_UI != null)
            {
                // The Area C stimulus label shares the vertical band the summary occupies, so
                // it is emptied before the summary is written. RunRecognitionPhase already
                // clears it on its normal exit; this is the guard for every other way of
                // arriving here (FreeRecall, or a phase that ended early), and it costs nothing
                // when the label is already empty.
                m_UI.ClearWordDisplay();

                // Neither recognition overlay may reach the results screen. The phase already
                // takes both down; this is the guard for every other route into these results.
                m_UI.ShowRecognitionCounter(false);
                m_UI.SetAreaCInstruction(ExperimentLocalization.Get(LocKeys.RunComplete));
                m_UI.SetAreaCStatus(string.Empty);
                // Leads with correct chair selections and names the run, so run 2 does not read
                // as a repeat of run 1.
                // runsInSession is 0: the participant is told which run they just finished, not
                // how many the sitting will eventually contain (which is not decided yet).
                m_UI.SetResults(m_SessionResults.BuildParticipantRunSummary(
                    ExperimentLocalization.Get(LocKeys.ExecutiveTaskComplete),
                    m_Logger != null ? m_Logger.runIndex : 0,
                    0));

                // Three distinct actions, each with its own meaning spelled out on the button.
                m_UI.ShowRestartButton(true);
                m_UI.ShowNewTrialButton(true);
                m_UI.ShowEndButton(true);
            }

            PublishSessionResults("participant flow complete");

            m_Flow = null;
        }

        // ---------------------------------------------------------------------------------
        // Results reporting
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Reads THE run duration, once, and gives every consumer the same number.
        ///
        /// AUTHORITATIVE DEFINITION — the duration of the COGNITIVE RUN:
        ///     from TRIAL_START, logged when START is pressed in Area A,
        ///     to the moment the Area C flow completes and the results are reached.
        ///
        /// It therefore excludes the language screen and Area 0 familiarization, which are not
        /// part of the cognitive protocol and whose length depends on how long a participant
        /// chose to practise.
        ///
        /// NO SECOND TIMING SYSTEM. The value comes from the EventLogger's existing per-run
        /// IntervalTimer — the same stopwatch that already stamps trial_duration_ms onto
        /// TRIAL_END — read exactly once here and written to both the participant-facing and the
        /// saved fields, so the panel and the files cannot disagree. Time.time is not consulted;
        /// it is frame-quantised and would be a competing clock.
        ///
        /// Multi-run: BeginTrial restarts that timer at every run's START, so run 2's duration is
        /// measured from run 2's own beginning and never inherits run 1's.
        /// </summary>
        /// <summary>
        /// Copies this run's recognition tallies into <see cref="m_SessionResults"/>.
        ///
        /// THE SOURCE OF TRUTH DOES NOT MOVE. The counts come from the manager's own
        /// <c>m_ImmediateRecognitionItems</c> / <c>m_DelayedRecognitionItems</c>, through the
        /// single <see cref="RecognitionPhaseResults.Recount"/> implementation that also feeds
        /// the *_RECOGNITION_END event notes. Nothing is re-derived here, nothing re-classifies
        /// an item, and the event-notes string is NOT parsed back into numbers.
        ///
        /// FREERECALL IS EXPLICITLY EMPTIED rather than left alone: a sitting can switch protocol
        /// between runs, and a stale recognition tally under a FreeRecall run's identity would be
        /// worse than no tally at all.
        ///
        /// Safe to call more than once — it is idempotent, which is what lets both the results
        /// panel and a mid-run publish read consistent values.
        /// </summary>
        void CaptureRecognitionResults()
        {
            m_SessionResults.protocolMode = m_Config != null
                ? m_Config.protocolMode
                : VerbalProtocolMode.FreeRecall;

            if (m_SessionResults.protocolMode != VerbalProtocolMode.Recognition)
            {
                m_SessionResults.immediateRecognition.Reset();
                m_SessionResults.delayedRecognition.Reset();
                return;
            }

            m_SessionResults.immediateRecognition.Recount(m_ImmediateRecognitionItems);
            m_SessionResults.delayedRecognition.Recount(m_DelayedRecognitionItems);
        }

        void CaptureRunDuration(string reason)
        {
            if (m_Logger == null)
                return;

            // ONE read. Two reads of a running stopwatch would already be two different numbers.
            var seconds = m_Logger.ElapsedTrialSeconds();

            m_Result.totalTrialSeconds = seconds;
            m_SessionResults.totalExperimentDurationSeconds = seconds;

            Log(EventTypes.StateChanged, e => e.notes = string.Format(CultureInfo.InvariantCulture,
                "run_duration_captured; reason={0}; run_duration_s={1:F3}; " +
                "run_index={2}; measured_from=TRIAL_START(area_a_start); " +
                "measured_to=RESULTS; excludes_language_selection=TRUE; " +
                "excludes_familiarization=TRUE",
                reason, seconds, m_Logger.runIndex));
        }

        /// <summary>
        /// Finalises the session-level results, logs SESSION_SUMMARY, prints the researcher
        /// summary and writes the derived files.
        ///
        /// Safe to call more than once (end of flow, then End pressed): the files are simply
        /// rewritten from the same data.
        /// </summary>
        void PublishSessionResults(string reason)
        {
            if (m_Logger == null)
                return;

            m_SessionResults.sessionId = m_Logger.sessionId;
            m_SessionResults.randomizationSeed = m_SessionSeed;
            m_SessionResults.isSeedReplay = m_SeedIsReplay;
            m_SessionResults.sessionDirectory = m_Logger.sessionDirectory;

            // The completed run already captured its duration; the saved files must report the
            // SAME number the participant was shown, so it is not recomputed here.
            //
            // It used to be overwritten with ElapsedSessionSeconds(), which measures from
            // SESSION_START — i.e. it also counted the language screen and the whole of Area 0.
            // A run that is published without ever reaching the results screen (a RESTART part
            // way through) has nothing captured, so it falls back to the same run clock rather
            // than to a different one, and reports what the cognitive run had managed so far.
            if (m_SessionResults.totalExperimentDurationSeconds <= 0d)
                m_SessionResults.totalExperimentDurationSeconds = m_Logger.ElapsedTrialSeconds();

            if (m_Config != null)
            {
                m_SessionResults.wordSetId = m_Config.GetTrialWordSetId();
                m_SessionResults.protocolDescription = m_Config.DescribeProtocol();
            }

            // A run can be published without ever reaching the results screen — a RESTART part
            // way through writes out what that run did manage. Capturing again here means the
            // files report whatever recognition items actually ran, rather than the zeros a
            // never-captured aggregate would hold. Idempotent when the panel already captured.
            CaptureRecognitionResults();

            m_SessionResults.csvPath = FindCsvPath();
            m_SessionResults.audioFolder = m_Voice != null ? m_Voice.RecordingFolder() : string.Empty;
            m_SessionResults.immediateRecallStatus = DescribeRecallStatus(m_Result.immediateRecall);
            m_SessionResults.delayedRecallStatus = DescribeRecallStatus(m_Result.delayedRecall);

            Log(EventTypes.SessionSummary, e =>
            {
                e.chairTrialCount = m_SessionResults.chairTrialsTotal
                    .ToString(CultureInfo.InvariantCulture);
                e.notes = $"reason={reason}; {m_SessionResults.ToEventNotes()}";
            });

            // The researcher summary goes to the Console and to a file next to the data —
            // deliberately NOT onto the participant's panel.
            Debug.Log("\n" + m_SessionResults.BuildResearcherSummary());

            var directory = m_Logger.sessionDirectory;
            SessionSummaryWriter.WriteSessionSummary(m_SessionResults, directory);
            SessionSummaryWriter.WriteResearcherSummary(m_SessionResults, directory);
            SessionSummaryWriter.WriteManualScoringTemplate(m_SessionResults, m_TrialWords,
                m_Voice != null ? m_Voice.recordings : null, directory);
        }

        string FindCsvPath()
        {
            if (m_Logger == null)
                return string.Empty;

            foreach (var sink in m_Logger.bus.sinks)
            {
                if (sink is CsvEventSink csv)
                    return csv.filePath;
            }

            return string.Empty;
        }

        /// <summary>
        /// Status of one recall recording for the researcher summary.
        ///
        /// "recording saved" and "not scored" are separate statements and both are made: the
        /// audio existing says nothing about whether anyone has scored it, and with the null
        /// transcription provider nothing has.
        /// </summary>
        static string DescribeRecallStatus(RecordingInfo info)
        {
            if (info == null)
                return "no recording / not scored";

            if (!info.audioCaptured)
                return $"NOT captured ({info.failureReason}) / not scored";

            if (info.signalSilent)
                return $"recording saved but SILENT ({info.signal.peakDbfs:F0} dBFS peak) / not scored";

            var scored = info.score != null && info.score.scored
                ? info.score.ToNotesString()
                : "not scored (no transcription provider — score offline from the WAV)";

            return $"recording saved ({info.wavPath}) / {scored}";
        }

        void OnRecordingCompleted(RecordingInfo info)
        {
            if (info == null)
                return;

            if (info.recallPhase == RecallPhases.Immediate)
                m_Result.immediateRecall = info;
            else if (info.recallPhase == RecallPhases.Delayed)
                m_Result.delayedRecall = info;
        }

        // ---------------------------------------------------------------------------------
        // Reset / end
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Stops the trial part-way because a stimulus could not be delivered. The session
        /// and CSV continue so the failure is on record; the participant is returned to Area A
        /// and START stays refused until the problem is fixed and re-checked.
        /// </summary>
        void AbortTrial(string reason)
        {
            if (m_RunAborted || m_SessionEnded)
                return;

            StopFlow();

            // A stimulus failure ends the trial, so the instructional voice for it goes too.
            StopParticipantNarration($"trial stopped: {reason}");

            // Aborting stops the phase coroutine mid-item, so neither recognition overlay gets
            // taken down by the normal exit. Both go here.
            ResetDeveloperCheatsheet();

            if (m_UI != null)
                m_UI.ShowRecognitionCounter(false);

            // A chair trial interrupted mid-response is recorded as INVALID rather than
            // discarded: the session-level averages must exclude it, and a reader of the data
            // must be able to see that it happened.
            if (m_CurrentChairTrial != null && !m_CurrentChairTrial.selectionMade)
            {
                m_CurrentChairTrial.valid = false;
                m_CurrentChairTrial.invalidReason = reason;
                m_SessionResults.chairTrials.Add(m_CurrentChairTrial);
                m_CurrentChairTrial = null;
            }

            m_ChairSelectionReceived = false;

            if (m_Voice != null)
                m_Voice.AbortRecording(reason);

            if (m_ChairTask != null)
            {
                m_ChairTask.CloseSelection();
                m_ChairTask.ResetTask();
            }

            if (m_Logger != null)
                m_Logger.EndTrial();

            Log(EventTypes.TrialBlocked, e =>
            {
                e.correct = "FALSE";
                e.notes = $"trial aborted mid-flow; reason={reason}";
            });

            PrepareTrial();
            EnterIdleAtAreaA();
            EvaluateStimulusReadiness(logToConsole: false);

            if (m_UI != null)
                m_UI.SetAreaAStatus($"Trial stopped: {reason}");
        }

        /// <summary>
        /// RESTART — starts a completely NEW SESSION with a NEW SEED.
        ///
        /// Everything is reset: randomisation state, the chair block and its trial counter, the
        /// difficulty sequence, the chair layout and every chair's visual state, the target, the
        /// timers, the memory state, the audio state, the UI, the results and the experiment
        /// state. A new session id means a new CSV, a new audio folder and a new marker outlet
        /// session, so the previous run's data is closed and left untouched rather than being
        /// appended to or overwritten.
        /// </summary>
        /// <summary>
        /// RESTART — "discard this run and start over from the tutorial".
        ///
        /// The current run's data is DISCARDED: it is excluded from the normal analysis output
        /// (no session summary, no researcher summary) and its folder is marked
        /// SESSION_DISCARDED_BY_RESTART. By default nothing is deleted — see SessionDiscardGuard
        /// for why retention is the default and what deletion is restricted to.
        ///
        /// A restart is treated as a NEW PARTICIPANT: a new experiment session, run 1, a new
        /// seed, and back to Area 0.
        /// </summary>
        public void RestartTrial()
        {
            if (m_SessionEnded)
            {
                Debug.Log("[IKEA_EEG] RESTART ignored: the session has ended.");
                return;
            }

            if (m_RunAborted)
            {
                // The aborted screen is terminal for the participant. Nothing may restart the
                // protocol from it — recovery is a deliberate developer action.
                Debug.Log("[IKEA_EEG] RESTART ignored: this run was ABORTED. No run was created " +
                          "and no files were written.");
                return;
            }

            DiscardCurrentRun("restart pressed");

            if (m_Logger != null)
                m_Logger.BeginExperimentSession();

            // A RESTART means a NEW PARTICIPANT, who may not read the previous one's language.
            // The language is therefore cleared and re-asked — unlike NEW TRIAL, which keeps it.
            RestartSession(ChooseSeed(), isReplay: false, reason: "restart pressed",
                returnToFamiliarization: true, returnToLanguageSelection: true);
        }

        /// <summary>
        /// NEW TRIAL — "save this run and begin another trial".
        ///
        /// The completed run is finalised normally (CSV, WAV, summaries all kept), then a NEW
        /// run begins in the SAME experiment session with the next run_index and a new seed,
        /// starting directly at Area A. Familiarization is not repeated — the participant has
        /// already done it.
        /// </summary>
        public void StartNewRun()
        {
            if (m_Logger == null)
                return;

            if (m_SessionEnded)
            {
                Debug.Log("[IKEA_EEG] NEW TRIAL ignored: the session has ended. No run was " +
                          "created and no files were written.");
                return;
            }

            if (m_RunAborted)
            {
                Debug.Log("[IKEA_EEG] NEW TRIAL ignored: this run was ABORTED. No run was " +
                          "created and no files were written.");
                return;
            }

            var previousRunId = m_Logger.sessionId;
            var previousRunIndex = m_Logger.runIndex;

            FinalizeCurrentRun("new trial requested");

            // New run, same sitting. The seed is new so the chair block differs from the
            // previous run; the experiment session id is preserved so the runs stay related.
            //
            // The LANGUAGE is deliberately preserved too: it belongs to the experiment session,
            // and the participant has already answered that question. NEW TRIAL never returns
            // to the language screen.
            RestartSession(ChooseSeed(), isReplay: false, reason: "new trial",
                returnToFamiliarization: false, returnToLanguageSelection: false);

            Log(EventTypes.NewRunStarted, e => e.notes =
                $"previous_run_session_id={previousRunId}; previous_run_index={previousRunIndex}; " +
                $"new_run_index={m_Logger.runIndex}; " +
                $"experiment_session_id={m_Logger.experimentSessionId}; " +
                "familiarization_repeated=FALSE; previous_run_data_preserved=TRUE");

            Debug.Log($"[IKEA_EEG] NEW TRIAL — run {m_Logger.runIndex} of experiment session " +
                      $"{m_Logger.experimentSessionId}. Run {previousRunIndex} " +
                      $"({previousRunId}) was finalised and kept.");
        }

        /// <summary>
        /// Writes out the current run's data and closes it. Used by NEW TRIAL and END — the two
        /// paths where the run is a real, kept result.
        /// </summary>
        void FinalizeCurrentRun(string reason)
        {
            if (m_Logger == null || !m_Logger.sessionActive)
                return;

            PublishSessionResults(reason);

            Log(EventTypes.RunFinalized, e => e.notes =
                $"reason={reason}; run_index={m_Logger.runIndex}; " +
                $"run_session_id={m_Logger.sessionId}; " +
                $"experiment_session_id={m_Logger.experimentSessionId}; " +
                $"developer_interrupted={(m_Logger.developerInterrupted ? "TRUE" : "FALSE")}; " +
                "data_preserved=TRUE");

            // The run's EEG file is closed with the run, so each run's folder holds a
            // complete file rather than one that keeps growing into the next run.
            m_EegRecorder?.EndRun();

            m_Logger.EndSession(reason);
        }

        /// <summary>
        /// Marks the current run discarded and closes it WITHOUT generating summaries, so it
        /// does not enter the normal analysis output.
        /// </summary>
        void DiscardCurrentRun(string reason)
        {
            if (m_Logger == null || !m_Logger.sessionActive)
                return;

            var runId = m_Logger.sessionId;
            var directory = m_Logger.sessionDirectory;
            var experimentSessionId = m_Logger.experimentSessionId;
            var runIndex = m_Logger.runIndex;

            // Logged BEFORE the session closes, so the discard is recorded inside the very file
            // it describes.
            Log(EventTypes.RunDiscarded, e =>
            {
                e.correct = "FALSE";
                e.notes = $"reason={reason}; run_index={runIndex}; run_session_id={runId}; " +
                          $"status={SessionDiscardGuard.DiscardedStatus}; " +
                          "excluded_from_normal_data_output=TRUE; " +
                          "no_session_summary_generated=TRUE";
            });

            m_Logger.EndSession($"discarded: {reason}");

            // Only after the writers have released the files.
            var deleted = false;
            var detail = "files retained and marked";

            if (m_Config != null && m_Config.restartDeletesDiscardedRunFiles)
            {
                deleted = SessionDiscardGuard.TryDeleteRunDirectory(directory, runId,
                    m_Logger.rootFolderName, out detail);
            }

            if (!deleted)
            {
                SessionDiscardGuard.MarkRunDiscarded(directory, runId, m_Logger.rootFolderName,
                    reason, experimentSessionId, runIndex);
            }

            // The in-memory results go too, so nothing from the discarded run can be carried
            // into the next one's summary.
            m_SessionResults.Reset();
            m_Result.Reset();

            Debug.Log($"[IKEA_EEG] Run {runId} DISCARDED ({reason}); {detail}. No summary was " +
                      "generated for it.");
        }

        /// <summary>
        /// RESEARCHER/DEBUG CONTROL — starts a new session that REPLAYS the current seed.
        ///
        /// Same seed, so the identical target sequence, chair attributes, slot arrangement and
        /// difficulty sequence are regenerated; new session id, so the replay is recorded as its
        /// own dataset and nothing from the original run is modified. This is the control used
        /// to demonstrate reproducibility, not part of the participant protocol.
        /// </summary>
        public void ReplaySameSeed()
        {
            var seed = m_SessionSeed != 0
                ? m_SessionSeed
                : (m_Config != null ? m_Config.fixedSeed : 1L);

            // Researcher fast path: straight to Area A, skipping familiarization. It exists
            // only on the Editor menu, so the participant-facing flow stays a single button.
            RestartSession(seed, isReplay: true, reason: "replay same seed",
                returnToFamiliarization: false, returnToLanguageSelection: false);
        }

        void RestartSession(long seed, bool isReplay, string reason,
            bool returnToFamiliarization, bool returnToLanguageSelection)
        {
            StopFlow();

            // The previous run's instructional voice belongs to the previous run. RESTART and
            // NEW TRIAL both pass through here, so neither can leave one talking.
            StopParticipantNarration($"session restarted: {reason}");

            // A NEW run is beginning, so the previous one's abort no longer applies. The aborted
            // run's own data keeps its marks; this only stops the new run inheriting the flag.
            m_RunAborted = false;

            // ---- Tear the running trial down -------------------------------------------------
            if (m_Voice != null)
            {
                m_Voice.AbortRecording(reason);
                m_Voice.ResetForNewTrial();
            }

            if (m_ChairTask != null)
            {
                m_ChairTask.CloseSelection();
                m_ChairTask.ResetTask();
            }

            m_CurrentChairTrial = null;
            m_ChairSelectionReceived = false;
            m_ChairPlans.Clear();

            Log(EventTypes.TrialReset, e => e.notes =
                $"reset_from_state={m_State}; reason={reason}; " +
                $"new_seed={seed.ToString(CultureInfo.InvariantCulture)}; " +
                $"seed_is_replay={(isReplay ? "TRUE" : "FALSE")}");

            // ---- Close the old session and open a new one -------------------------------------
            if (m_Logger != null && m_Logger.sessionActive)
            {
                // Write out what the aborted session did produce before closing its files.
                PublishSessionResults($"session restarted ({reason})");
                m_Logger.EndSession(reason);
            }

            m_Result.Reset();
            m_SessionResults.Reset();

            if (m_Logger != null)
                BeginSession(seed, isReplay);

            // ---- Back to a clean pre-trial state ---------------------------------------------
            PrepareTrial();

            if (returnToLanguageSelection)
            {
                // New participant: ask for the language again before anything else.
                EnterLanguageSelection(reason);
                Debug.Log($"[IKEA_EEG] RESTART complete — new experiment session, language " +
                          "cleared, returned to the language screen.");
                return;
            }

            var toFamiliarization = returnToFamiliarization &&
                                    m_Config != null && m_Config.enableFamiliarization;

            if (toFamiliarization)
            {
                EnterFamiliarization();
                EvaluateStimulusReadiness(logToConsole: false);
            }
            else
            {
                SkipFamiliarization(returnToFamiliarization
                    ? "familiarization disabled in ExperimentConfig"
                    : $"researcher fast path ({reason})");

                EvaluateStimulusReadiness(logToConsole: false);
            }

            Debug.Log($"[IKEA_EEG] RESTART complete — new session " +
                      $"{(m_Logger != null ? m_Logger.sessionId : "n/a")} with seed {seed}" +
                      (isReplay ? " (REPLAY of the previous seed)" : " (new seed)") + ".");
        }

        /// <summary>
        /// END — "save and finish". The participant flow stops here, permanently.
        ///
        /// The run is finalised and written exactly as before. What changed: the action area is
        /// replaced by a static completion state and the three run-management buttons are taken
        /// away, so no further run can be created from the participant UI. Pressing END twice,
        /// or reaching it from a stale UI, cannot finalise the run a second time.
        /// </summary>
        public void EndSession()
        {
            if (m_SessionEnded)
            {
                // Already ended. Do nothing at all — no second finalisation, no second
                // RUN_FINALIZED row, no new files.
                Debug.Log("[IKEA_EEG] END pressed again; the session is already ended. " +
                          "Nothing was written and no run was created.");
                return;
            }

            if (m_RunAborted)
            {
                // The abort already closed the run's files. Finalising now would write a second
                // ending over a record that already has one.
                Debug.Log("[IKEA_EEG] END ignored: this run was ABORTED and already closed. " +
                          "Nothing was written.");
                return;
            }

            m_SessionEnded = true;

            StopFlow();
            StopParticipantNarration("session ended");

            if (m_Voice != null)
                m_Voice.AbortRecording("session ended");

            if (m_ChairTask != null)
                m_ChairTask.CloseSelection();

            SetState(ExperimentState.Ended);

            // END keeps its existing behaviour: finalise and write everything, then stop.
            // Routed through FinalizeCurrentRun so it emits RUN_FINALIZED like NEW TRIAL does.
            FinalizeCurrentRun("ended by participant/researcher");

            ShowEndedState();
        }

        /// <summary>
        /// The terminal screen: a completion message and no way to start anything.
        ///
        /// RECENTER is deliberately kept — a participant still wearing the headset may need to
        /// re-align — but it only moves the rig and cannot touch experiment state.
        /// </summary>
        void ShowEndedState()
        {
            if (m_UI == null)
                return;

            m_UI.SetAreaCInstruction(ExperimentLocalization.Get(LocKeys.SessionComplete));
            m_UI.SetAreaCStatus(string.Empty);
            m_UI.SetResults(ExperimentLocalization.Get(LocKeys.SessionCompleteThanks));

            // Every route to another run is removed from the participant UI.
            m_UI.ShowRestartButton(false);
            m_UI.ShowNewTrialButton(false);
            m_UI.ShowEndButton(false);

            m_UI.ShowStartButton(false);
            m_UI.ShowEnterAreaBButton(false);
            m_UI.ShowExitToAreaCButton(false);
            m_UI.ShowReadyButton(false);
            m_UI.ShowStartExperimentButton(false);
            m_UI.ShowSkipIntroButton(false);
            m_UI.ShowReplayInstructionsButton(false);
            m_UI.ShowLanguagePanel(false);

            // Harmless and still useful.
            m_UI.ShowRecenterButtons(true);
        }

        /// <summary>True once END has finalised the session. Nothing may restart it.</summary>
        public bool sessionEnded => m_SessionEnded;

        // ---------------------------------------------------------------------------------
        // Builder wiring
        // ---------------------------------------------------------------------------------

        public void Bind(ExperimentConfig config, EventLogger logger, ExperimentUIController ui,
            XRRigTeleporter teleporter, ChairSelectionTask chairTask, VoiceRecallManager voice,
            ExperimentAudio audio, LslMarkerSink lslSink = null,
            IEnumerable<PracticeObject> practiceObjects = null,
            DeveloperNavigation developerNavigation = null)
        {
            if (practiceObjects != null)
            {
                m_PracticeObjects.Clear();
                m_PracticeObjects.AddRange(practiceObjects);
            }

            m_DeveloperNavigation = developerNavigation;

            m_Config = config;
            m_Logger = logger;
            m_UI = ui;
            m_Teleporter = teleporter;
            m_ChairTask = chairTask;
            m_Voice = voice;
            m_Audio = audio;
            m_LslSink = lslSink;

            // Resolved from the scene rather than passed: the EEG path is optional, and a
            // scene built before it existed must keep binding cleanly.
            if (m_EegRecorder == null)
                m_EegRecorder = FindAnyObjectByType<EegRunRecorder>();

            // Same reasoning for the recognition panel: a scene built before the Recognition
            // protocol existed still binds, and only a Recognition run needs the panel.
            if (m_RecognitionPanel == null)
                m_RecognitionPanel = FindAnyObjectByType<Interaction.RecognitionResponsePanel>();

            if (m_RecognitionPanel != null)
            {
                // NOTE: the response SUBSCRIPTION is deliberately NOT made here — see OnEnable.
                // Bind() runs in the Editor from the scene builder, and a C# event subscription
                // is not serialized, so anything wired here simply does not exist at run time.
                m_RecognitionPanel.Show(false);
            }
            else if (m_Config != null && m_Config.protocolMode == VerbalProtocolMode.Recognition)
            {
                Debug.LogWarning("[IKEA_EEG] Protocol mode is Recognition but no " +
                                 "RecognitionResponsePanel is in the scene. Rebuild the scene " +
                                 "(IKEA_EEG > Build Experiment Scene) or switch protocolMode " +
                                 "to FreeRecall.");
            }
        }
    }
}
