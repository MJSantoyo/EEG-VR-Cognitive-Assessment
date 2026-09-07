namespace IkeaEeg.Core
{
    /// <summary>
    /// Canonical names for every experimental event.
    ///
    /// IMPORTANT: these exact strings are written to the CSV *and* are intended to be sent
    /// verbatim as LSL markers later on. Never rename one without also updating the analysis
    /// scripts, and never let a subsystem invent its own ad-hoc event string.
    /// </summary>
    public static class EventTypes
    {
        // ---- Session / trial lifecycle -------------------------------------------------
        public const string SessionStart = "SESSION_START";
        public const string SessionEnd = "SESSION_END";
        public const string TrialStart = "TRIAL_START";
        public const string TrialEnd = "TRIAL_END";

        // ---- Language selection ------------------------------------------------------------
        /// <summary>The language screen was presented. Nothing cognitive has happened yet.</summary>
        public const string LanguageSelectionShown = "LANGUAGE_SELECTION_SHOWN";

        /// <summary>
        /// The participant chose a platform language. It governs every participant-facing
        /// string and every spoken instruction for the whole experiment session.
        /// </summary>
        public const string LanguageSelected = "LANGUAGE_SELECTED";

        // ---- Area 0: VR familiarization --------------------------------------------------
        // DIAGNOSTIC / SESSION EVENTS ONLY. Nothing produced in Area 0 enters chair accuracy,
        // response-time averages, memory scoring or any cognitive metric. The practice objects
        // are deliberately unrelated to every later stimulus.
        public const string FamiliarizationStart = "FAMILIARIZATION_START";

        /// <summary>A practice object was selected. Diagnostic only — never a trial.</summary>
        public const string PracticeObjectSelected = "PRACTICE_OBJECT_SELECTED";

        /// <summary>The participant chose to begin the experiment from Area 0.</summary>
        public const string FamiliarizationComplete = "FAMILIARIZATION_COMPLETE";

        /// <summary>Area 0 was bypassed (researcher fast path, or disabled in the config).</summary>
        public const string FamiliarizationSkipped = "FAMILIARIZATION_SKIPPED";

        /// <summary>
        /// Which verbal-memory word set this run will present, and its full provenance.
        ///
        /// Emitted once per run so the exact stimulus is reconstructible from the CSV alone,
        /// without the project's assets — including whether the list is a subset of a larger
        /// published form and whether it repeats one already heard in this sitting.
        /// </summary>
        public const string WordSetSelected = "WORD_SET_SELECTED";

        /// <summary>The practice task asked for a specific target colour. Diagnostic only.</summary>
        public const string PracticeTargetPresented = "PRACTICE_TARGET_PRESENTED";

        /// <summary>The requested practice object was selected. Diagnostic only, never scored.</summary>
        public const string PracticeSuccess = "PRACTICE_SUCCESS";

        // ---- Participant narration ------------------------------------------------------------
        // Instructional speech only. Never a memory stimulus — the five words and the recall beep
        // are experiment-critical audio and are not covered by these events.

        /// <summary>
        /// Room/phase-owned narration was cancelled: a room change, RESTART, NEW TRIAL, END,
        /// ABORT, a developer jump, or a return to the language screen. Recorded because "the
        /// participant stopped hearing the instructions here" is part of what they were given.
        /// </summary>
        public const string NarrationStopped = "NARRATION_STOPPED";

        // ---- Run lifecycle -----------------------------------------------------------------
        // One RUN is one Area A -> B -> C pass. Several runs can share one experiment session.

        /// <summary>
        /// The run was ABORTED: every participant-facing process was stopped at once and the run
        /// entered its terminal aborted state. Distinct from RUN_DISCARDED (RESTART, which begins
        /// another run) and RUN_FINALIZED (a run that completed).
        /// </summary>
        public const string RunAborted = "RUN_ABORTED";

        /// <summary>A run ended normally and its data was written. NEW TRIAL and END both emit it.</summary>
        public const string RunFinalized = "RUN_FINALIZED";

        /// <summary>
        /// A run was discarded by RESTART. Its raw files are RETAINED and marked, never deleted
        /// — see SessionDiscardMarker.
        /// </summary>
        public const string RunDiscarded = "RUN_DISCARDED";

        /// <summary>A new run began within the same experiment session (NEW TRIAL).</summary>
        public const string NewRunStarted = "NEW_RUN_STARTED";

        // ---- Developer navigation ----------------------------------------------------------
        // Developer-only. Present in the data precisely so a developer-affected run can never be
        // mistaken for a clean participant run.

        public const string DeveloperNavigationOpened = "DEVELOPER_NAVIGATION_OPENED";
        public const string DeveloperNavigationClosed = "DEVELOPER_NAVIGATION_CLOSED";

        /// <summary>A developer jumped areas. Marks the run DEVELOPER_INTERRUPTED.</summary>
        public const string DeveloperAreaJump = "DEVELOPER_AREA_JUMP";

        // ---- Area A: encoding + immediate recall ---------------------------------------
        public const string AreaAEnter = "AREA_A_ENTER";
        public const string WordEncodingStart = "WORD_ENCODING_START";
        public const string WordPresented = "WORD_PRESENTED";
        public const string WordEncodingEnd = "WORD_ENCODING_END";

        // ---- Recognition protocol ---------------------------------------------------------
        // APPENDED event types. The existing WORD_ENCODING_START / WORD_PRESENTED /
        // WORD_ENCODING_END are REUSED for the visual encoding sequence rather than renamed:
        // they already mean "the encoding phase began", "one stimulus was delivered" and "the
        // encoding phase ended", which is exactly what still happens. Renaming them would break
        // every analysis script written against existing files for no gain in meaning. The
        // change of modality is carried in the event's own fields, not in its name.

        /// <summary>A visual encoding word was removed from the display. Pairs with WORD_PRESENTED.</summary>
        public const string WordOffset = "WORD_OFFSET";

        public const string ImmediateRecognitionStart = "IMMEDIATE_RECOGNITION_START";
        public const string ImmediateRecognitionEnd = "IMMEDIATE_RECOGNITION_END";
        public const string DelayedRecognitionStart = "DELAYED_RECOGNITION_START";
        public const string DelayedRecognitionEnd = "DELAYED_RECOGNITION_END";

        /// <summary>One recognition word appeared. Carries item id, class and order.</summary>
        public const string RecognitionItemOnset = "RECOGNITION_ITEM_ONSET";

        /// <summary>
        /// The participant answered. Carries the response, the derived outcome and the
        /// reaction time measured from that item's own onset.
        /// </summary>
        public const string RecognitionResponse = "RECOGNITION_RESPONSE";

        /// <summary>An item ended with no response recorded. Never counted as an error.</summary>
        public const string RecognitionItemTimeout = "RECOGNITION_ITEM_TIMEOUT";

        public const string ImmediateRecallBeep = "IMMEDIATE_RECALL_BEEP";
        public const string ImmediateRecallStart = "IMMEDIATE_RECALL_START";
        public const string ImmediateRecallEnd = "IMMEDIATE_RECALL_END";

        /// <summary>
        /// Sustained speech was detected in a recall recording. Emitted at most once per
        /// recording; its absence means the participant was never heard to speak.
        /// </summary>
        public const string RecallSpeechDetected = "RECALL_SPEECH_DETECTED";

        /// <summary>
        /// A recall recording stopped, and why. Carries termination_reason (one of
        /// SILENCE_AFTER_SPEECH / MAX_DURATION / MANUAL / ABORTED) and
        /// actual_recording_duration_s.
        /// </summary>
        public const string RecallRecordingStop = "RECALL_RECORDING_STOP";

        // ---- Area B: chair selection ----------------------------------------------------
        // One Area B visit contains chairTrialsPerRun chair trials. Every event from
        // CHAIR_TRIAL_PREPARED to CHAIR_TRIAL_END carries chair_trial_index and difficulty.
        public const string AreaBEnter = "AREA_B_ENTER";

        /// <summary>
        /// The GENERAL executive-task instructions appeared. Shown once per Area B block, before
        /// any target. The interval from here to AREA_B_READY is instruction-reading time and is
        /// deliberately NOT part of any chair response time.
        /// </summary>
        public const string AreaBInstructionsOnset = "AREA_B_INSTRUCTIONS_ONSET";

        /// <summary>The participant pressed READY, ending the instruction-reading period.</summary>
        public const string AreaBReady = "AREA_B_READY";

        /// <summary>The layout and target for one chair trial were generated and applied.</summary>
        public const string ChairTrialPrepared = "CHAIR_TRIAL_PREPARED";

        /// <summary>
        /// THE STIMULUS ONSET of one chair trial: the moment the three target attributes became
        /// visible to the participant. Response time is measured from here.
        ///
        /// CHAIR_INSTRUCTION_ONSET is emitted at the same moment and carries the same meaning;
        /// both exist so that analyses written against either name work. See the RT definition
        /// in ExperimentManager.RunChairTrial.
        /// </summary>
        public const string ChairTargetOnset = "CHAIR_TARGET_ONSET";

        /// <summary>One chair trial begins. Paired with CHAIR_TRIAL_END.</summary>
        public const string ChairTrialStart = "CHAIR_TRIAL_START";

        /// <summary>One chair trial is over, including its feedback and inter-trial interval.</summary>
        public const string ChairTrialEnd = "CHAIR_TRIAL_END";

        /// <summary>All chair trials of this run are done; the Area C transition is enabled.</summary>
        public const string ChairBlockComplete = "CHAIR_BLOCK_COMPLETE";

        /// <summary>
        /// The controller ray entered a chair. Diagnostic only, and OFF by default: it fires on
        /// every ray sweep, so it is opt-in via ExperimentConfig.logChairHoverEvents.
        /// </summary>
        public const string ChairHoverEnter = "CHAIR_HOVER_ENTER";

        public const string ChairInstructionOnset = "CHAIR_INSTRUCTION_ONSET";
        public const string ChairInstructionComplete = "CHAIR_INSTRUCTION_COMPLETE";
        public const string ChairSelectionTimerStart = "CHAIR_SELECTION_TIMER_START";
        public const string ChairSelected = "CHAIR_SELECTED";
        public const string ChairCorrect = "CHAIR_CORRECT";
        public const string ChairIncorrect = "CHAIR_INCORRECT";
        public const string ChairSelectionEnd = "CHAIR_SELECTION_END";

        // ---- Area C: delayed recall -----------------------------------------------------
        public const string AreaCEnter = "AREA_C_ENTER";
        public const string DelayedRecallBeep = "DELAYED_RECALL_BEEP";
        public const string DelayedRecallStart = "DELAYED_RECALL_START";
        public const string DelayedRecallEnd = "DELAYED_RECALL_END";

        // ---- Hardware / stimulus integrity ------------------------------------------------
        /// <summary>Which microphone device was chosen, and why.</summary>
        public const string MicrophoneDeviceSelected = "MICROPHONE_DEVICE_SELECTED";

        /// <summary>State of the audio output path, logged once at session start.</summary>
        public const string AudioDiagnostics = "AUDIO_DIAGNOSTICS";

        /// <summary>Per-clip report of the spoken word stimuli, logged once at session start.</summary>
        public const string SpokenClipInventory = "SPOKEN_CLIP_INVENTORY";

        /// <summary>The trial was prevented from starting because stimuli cannot be delivered.</summary>
        public const string TrialBlocked = "TRIAL_BLOCKED";

        /// <summary>A blocking problem was re-checked by the operator.</summary>
        public const string AudioRecheck = "AUDIO_RECHECK";

        /// <summary>Result of the short microphone capture performed at session start.</summary>
        public const string MicrophoneTest = "MICROPHONE_TEST";

        /// <summary>
        /// A recording completed but contains no usable signal. The WAV is still written for
        /// debugging; the recording must not be treated as a successful capture.
        /// </summary>
        public const string MicrophoneSignalSilent = "MICROPHONE_SIGNAL_SILENT";

        /// <summary>The participant re-aligned themselves with the room's intended forward.</summary>
        public const string Recenter = "RECENTER";

        /// <summary>
        /// An auditory stimulus was requested but could NOT be delivered to the participant.
        /// The corresponding stimulus-onset event is still logged, but flagged as unheard —
        /// trials containing this event must be treated as compromised.
        /// </summary>
        public const string AudioStimulusFailed = "AUDIO_STIMULUS_FAILED";

        // ---- Marker transport -------------------------------------------------------------
        /// <summary>
        /// State of the LSL marker outlet, logged once per session right after SESSION_START.
        /// Its notes carry lsl_state=ACTIVE|UNAVAILABLE|DISABLED. ACTIVE is only ever written
        /// when a real outlet was created — never as an assumption.
        /// </summary>
        public const string LslStatus = "LSL_STATUS";

        // ---- Session-level results ---------------------------------------------------------
        /// <summary>Aggregated behavioural results, logged once when the participant flow ends.</summary>
        public const string SessionSummary = "SESSION_SUMMARY";

        // ---- Supporting / diagnostic ----------------------------------------------------
        public const string StateChanged = "STATE_CHANGED";
        public const string TeleportToArea = "TELEPORT_TO_AREA";
        public const string RecordingSaved = "RECORDING_SAVED";
        public const string RecallScored = "RECALL_SCORED";
        public const string TrialReset = "TRIAL_RESET";
        public const string Warning = "WARNING";
    }

    /// <summary>Room / area labels used in the <c>room</c> CSV column.</summary>
    public static class RoomNames
    {
        public const string None = "NONE";

        /// <summary>Area 0 — VR familiarization. Never part of cognitive scoring.</summary>
        public const string Familiarization = "AREA_0";

        public const string AreaA = "AREA_A";
        public const string AreaB = "AREA_B";
        public const string AreaC = "AREA_C";
    }

    /// <summary>Values used in the <c>recall_phase</c> CSV column.</summary>
    public static class RecallPhases
    {
        public const string Immediate = "IMMEDIATE";
        public const string Delayed = "DELAYED";
    }

    /// <summary>
    /// Why a recall recording ended. Written as <c>termination_reason=</c> in the notes of
    /// RECALL_RECORDING_STOP.
    ///
    /// These are exhaustive: every recording that starts ends with exactly one of them, so an
    /// analysis can partition recordings without inferring anything from durations.
    /// </summary>
    public static class RecallStopReasons
    {
        /// <summary>Speech was detected, then the silence threshold elapsed. The normal case.</summary>
        public const string SilenceAfterSpeech = "SILENCE_AFTER_SPEECH";

        /// <summary>The hard maximum duration was reached. Also what happens if nobody speaks.</summary>
        public const string MaxDuration = "MAX_DURATION";

        /// <summary>Stopped by the protocol/operator rather than by the detector.</summary>
        public const string Manual = "MANUAL";

        /// <summary>Cancelled: restart, session end, or a stimulus failure. No usable trial.</summary>
        public const string Aborted = "ABORTED";
    }
}
