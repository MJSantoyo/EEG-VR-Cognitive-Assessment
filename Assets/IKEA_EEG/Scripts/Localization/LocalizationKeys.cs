namespace IkeaEeg.Localization
{
    /// <summary>
    /// Stable keys for every participant-facing string.
    ///
    /// Code never contains a participant-facing literal and never branches on the language.
    /// It asks for a KEY; the table decides what that means in the language in force. Adding a
    /// fourth language is then one column in one file, not a search through the state machine.
    ///
    /// Keys are also what the self test checks for completeness — every key here must have an
    /// entry in all three languages.
    /// </summary>
    public static class LocKeys
    {
        // ---- Language selection --------------------------------------------------------
        public const string LanguageSelectTitle = "LANGUAGE_SELECT_TITLE";

        // ---- Area 0: familiarization ----------------------------------------------------
        public const string FamiliarizationInstructions = "FAMILIARIZATION_INSTRUCTIONS";
        public const string PracticeSelection = "PRACTICE_SELECTION";
        public const string SelectColor = "SELECT_COLOR";                 // {COLOR}
        public const string PracticeSuccess = "PRACTICE_SUCCESS";
        public const string PracticeReady = "PRACTICE_READY";
        public const string PracticeTryTarget = "PRACTICE_TRY_TARGET";    // {SELECTED} {TARGET}
        public const string PracticeMoreOne = "PRACTICE_MORE_ONE";
        public const string PracticeMoreMany = "PRACTICE_MORE_MANY";      // {N}
        public const string StartExperiment = "START_EXPERIMENT";
        public const string ExperimentAborted = "EXPERIMENT_ABORTED";
        public const string ExperimentAbortedDetail = "EXPERIMENT_ABORTED_DETAIL";
        public const string SkipIntro = "SKIP_INTRO";
        public const string ReplayInstructions = "REPLAY_INSTRUCTIONS";
        public const string Recenter = "RECENTER";

        // ---- Controller help -------------------------------------------------------------
        public const string ControllerTitle = "CONTROLLER_TITLE";
        public const string ControllerIndexTrigger = "CONTROLLER_INDEX_TRIGGER";
        public const string ControllerGripTrigger = "CONTROLLER_GRIP_TRIGGER";
        public const string ControllerFooter = "CONTROLLER_FOOTER";
        public const string HandLeft = "HAND_LEFT";
        public const string HandRight = "HAND_RIGHT";

        // ---- Area A: verbal memory --------------------------------------------------------
        public const string AreaATitle = "AREA_A_TITLE";
        public const string AreaAWelcome = "AREA_A_WELCOME";
        public const string AreaAInstructions = "AREA_A_INSTRUCTIONS";
        public const string EncodingListen = "ENCODING_LISTEN";

        // ---- Recognition protocol ---------------------------------------------------------
        // SEPARATE KEYS, not edits to the FreeRecall ones. AreaAInstructions and EncodingListen
        // describe an auditory task ("you will hear five words", "listen carefully") and remain
        // exactly right for FreeRecall, which is still selectable. Rewriting them in place would
        // have silently changed the FreeRecall protocol's participant-facing wording.

        /// <summary>
        /// Recognition-mode encoding instructions: 15 words, seen not heard, tested later.
        ///
        /// ENGLISH-ONLY this iteration. The ES/JA slots deliberately hold the English string —
        /// see RecognitionPrompt for why an invented translation is worse than an obvious gap.
        /// </summary>
        public const string RecognitionEncodingInstructions = "RECOGNITION_ENCODING_INSTRUCTIONS";

        /// <summary>Shown during the visual encoding sequence itself. Replaces "Listen carefully."</summary>
        public const string RecognitionEncodingWatch = "RECOGNITION_ENCODING_WATCH";

        /// <summary>
        /// Area C instructions for the RECOGNITION protocol, shown before delayed recognition.
        ///
        /// Separate from AreaCInstructions, which describes SPOKEN free recall ("repeat the five
        /// words that were presented") and stays exactly right for FreeRecall. English-only this
        /// iteration, like the other Recognition strings.
        /// </summary>
        public const string RecognitionDelayedInstructions = "RECOGNITION_DELAYED_INSTRUCTIONS";

        // ---- Familiarization feedback -----------------------------------------------------

        /// <summary>Spoken/shown after a CORRECT practice selection.</summary>
        public const string PracticeFeedbackCorrect = "PRACTICE_FEEDBACK_CORRECT";

        /// <summary>Follows the correct-feedback when more practice selections are still needed.</summary>
        public const string PracticeFeedbackAnother = "PRACTICE_FEEDBACK_ANOTHER";

        /// <summary>Spoken/shown after an INCORRECT practice selection.</summary>
        public const string PracticeFeedbackIncorrect = "PRACTICE_FEEDBACK_INCORRECT";

        /// <summary>Spoken/shown once the practice requirement is fully met.</summary>
        public const string PracticeFeedbackComplete = "PRACTICE_FEEDBACK_COMPLETE";
        public const string ImmediateRecallPrompt = "IMMEDIATE_RECALL_PROMPT";

        /// <summary>
        /// Prompt shown during a recognition phase.
        ///
        /// ENGLISH-ONLY FOR THIS ITERATION, by instruction. The ES/JA entries deliberately carry
        /// the English string rather than a machine translation: a participant-facing clinical
        /// instruction must be written by someone who speaks the language, and an invented
        /// translation would look finished while quietly changing what was asked. The
        /// localization architecture is untouched, so filling these in later is a table edit.
        /// </summary>
        public const string RecognitionPrompt = "RECOGNITION_PROMPT";

        /// <summary>Label on the SEEN BEFORE response button. English-only this iteration.</summary>
        public const string RecognitionSeenBefore = "RECOGNITION_SEEN_BEFORE";

        /// <summary>Label on the NOT SEEN BEFORE response button. English-only this iteration.</summary>
        public const string RecognitionNotSeenBefore = "RECOGNITION_NOT_SEEN_BEFORE";

        /// <summary>
        /// "Item {N} / {TOTAL}" — the participant's position in the current recognition phase.
        ///
        /// {TOTAL} is filled from the length of the sequence actually being presented, so a
        /// change to the delayed composition is followed with no edit here.
        ///
        /// English-only this iteration, like every other Recognition string.
        /// </summary>
        public const string RecognitionItemProgress = "RECOGNITION_ITEM_PROGRESS";
        public const string ReadyForAreaB = "READY_FOR_AREA_B";
        public const string Start = "START";
        public const string EnterShowroom = "ENTER_SHOWROOM";
        public const string Recording = "RECORDING";
        public const string RecordingComplete = "RECORDING_COMPLETE";

        // ---- Area B: executive task --------------------------------------------------------
        public const string ExecutiveTaskInstructions = "EXECUTIVE_TASK_INSTRUCTIONS";
        public const string ShapeLegendHint = "SHAPE_LEGEND_HINT";
        public const string ShapeLegendTitle = "SHAPE_LEGEND_TITLE";
        public const string Ready = "READY";
        public const string ChairTargetHeader = "CHAIR_TARGET_HEADER";
        public const string TaskProgress = "TASK_PROGRESS";               // {N} {TOTAL}
        public const string PointAndSelect = "POINT_AND_SELECT";
        public const string NextTask = "NEXT_TASK";
        public const string ChairSelected = "CHAIR_SELECTED";
        public const string AnswerCorrect = "ANSWER_CORRECT";
        public const string AnswerIncorrect = "ANSWER_INCORRECT";
        public const string ExecutiveTaskComplete = "EXECUTIVE_TASK_COMPLETE";
        public const string NCorrect = "N_CORRECT";                       // {N} {TOTAL}
        public const string TasksNotCompleted = "TASKS_NOT_COMPLETED";    // {N}
        public const string NoResponsesRecorded = "NO_RESPONSES_RECORDED";
        public const string BlockFooter = "BLOCK_FOOTER";
        public const string ExitShowroom = "EXIT_SHOWROOM";

        // ---- Area C: delayed recall ---------------------------------------------------------
        public const string AreaCInstructions = "AREA_C_INSTRUCTIONS";
        public const string DelayedRecallPrompt = "DELAYED_RECALL_PROMPT";
        public const string RunComplete = "RUN_COMPLETE";

        // ---- Results -------------------------------------------------------------------------
        public const string ResultsRun = "RESULTS_RUN";                   // {N}
        public const string SessionSummaryHeading = "SESSION_SUMMARY_HEADING";
        public const string StatExecutiveTask = "STAT_EXECUTIVE_TASK";
        public const string StatMeanResponseTime = "STAT_MEAN_RESPONSE_TIME";
        public const string StatMedianResponseTime = "STAT_MEDIAN_RESPONSE_TIME";
        public const string StatTotalDuration = "STAT_TOTAL_DURATION";
        public const string StatImmediateRecall = "STAT_IMMEDIATE_RECALL";
        public const string StatDelayedRecall = "STAT_DELAYED_RECALL";
        public const string RecordingSaved = "RECORDING_SAVED";
        public const string NotAvailable = "NOT_AVAILABLE";

        // ---- Results: Recognition protocol ----------------------------------------------------
        // ENGLISH-ONLY THIS ITERATION, like every other Recognition string. The ES and JA slots
        // deliberately hold the English text; see RecognitionPrompt for why an obvious gap is
        // preferable to a machine translation that looks finished.
        //
        // The StatImmediateRecall / StatDelayedRecall keys above are NOT reused for these. Their
        // strings say "verbal recall", which is a different task from recognition, and they are
        // still correct for the protocol that owns them.

        /// <summary>Heading of the immediate recognition block on the results screen.</summary>
        public const string RecognitionResultsImmediate = "RECOGNITION_RESULTS_IMMEDIATE";

        /// <summary>Heading of the delayed recognition block. Never merged with the immediate one.</summary>
        public const string RecognitionResultsDelayed = "RECOGNITION_RESULTS_DELAYED";

        /// <summary>Label for hits + correct rejections. Never called a score.</summary>
        public const string RecognitionCorrectResponses = "RECOGNITION_CORRECT_RESPONSES";

        /// <summary>
        /// "{N} of {TOTAL} answered". The denominator is spelled out because unanswered items
        /// are deliberately excluded from it — an unanswered item is not scored as an error.
        /// </summary>
        public const string RecognitionAnsweredOf = "RECOGNITION_ANSWERED_OF";

        public const string RecognitionHits = "RECOGNITION_HITS";
        public const string RecognitionMisses = "RECOGNITION_MISSES";
        public const string RecognitionCorrectRejections = "RECOGNITION_CORRECT_REJECTIONS";
        public const string RecognitionFalseAlarms = "RECOGNITION_FALSE_ALARMS";
        public const string RecognitionNoResponse = "RECOGNITION_NO_RESPONSE";
        public const string RecognitionTotalItems = "RECOGNITION_TOTAL_ITEMS";

        /// <summary>Shown when a phase ran but nothing was answered. Distinct from the chair one.</summary>
        public const string RecognitionNoAnswersRecorded = "RECOGNITION_NO_ANSWERS_RECORDED";

        // ---- Run management --------------------------------------------------------------------
        public const string NewTrial = "NEW_TRIAL";
        public const string NewTrialSubtitle = "NEW_TRIAL_SUBTITLE";
        public const string Restart = "RESTART";
        public const string RestartSubtitle = "RESTART_SUBTITLE";
        public const string End = "END";
        public const string EndSubtitle = "END_SUBTITLE";
        public const string SessionComplete = "SESSION_COMPLETE";
        public const string SessionCompleteThanks = "SESSION_COMPLETE_THANKS";

        // ---- Blocking states -----------------------------------------------------------------
        public const string AudioUnavailable = "AUDIO_UNAVAILABLE";

        /// <summary>
        /// The button offered beside the audio-unavailable warning. Participant-facing: it shares
        /// a screen with a localized blocking message, so it cannot be left in English.
        /// </summary>
        public const string RecheckAudio = "RECHECK_AUDIO";

        public const string NoWordSetForLanguage = "NO_WORD_SET_FOR_LANGUAGE";

        // ---- Chair attributes ------------------------------------------------------------------
        // Prefix + the INTERNAL enum name, e.g. COLOR_Blue / SIZE_Large / SHAPE_Slatted.
        // The enum values themselves never change — only what the participant is shown.
        public const string ColorPrefix = "COLOR_";
        public const string SizePrefix = "SIZE_";
        public const string ShapePrefix = "SHAPE_";

        // ---- Area 0 practice colours -------------------------------------------------------------
        // Prefix + the INTERNAL ChairColor enum name, e.g. PRACTICE_COLOR_Red.
        //
        // SEPARATE FROM THE CHAIR COLOURS ON PURPOSE. The chair terms agree grammatically with
        // "silla" (feminine in Spanish: ROJA, AMARILLA); the practice objects are not chairs, so
        // they take the neutral/masculine form the practice sentence needs (ROJO, AMARILLO). The
        // Japanese practice terms are the short colour words (黄) rather than the chair form
        // (黄色). Sharing one key would force one of the two rooms to read wrongly.
        public const string PracticeColorPrefix = "PRACTICE_COLOR_";
    }
}
