using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using IkeaEeg.Interaction;

namespace IkeaEeg.Experiment
{
    /// <summary>One named narration clip. Used for the per-colour practice prompts.</summary>
    [System.Serializable]
    public class NarrationClipEntry
    {
        [Tooltip("Lookup key. For the practice prompts this is the colour name, e.g. BLUE.")]
        public string id = string.Empty;

        public AudioClip clip;
    }

    /// <summary>How the session's randomisation seed is chosen.</summary>
    public enum SeedMode
    {
        /// <summary>A new seed per session, derived from the wall clock. The normal setting.</summary>
        NewSeedEachSession,

        /// <summary>Always use <see cref="ExperimentConfig.fixedSeed"/>. For piloting and debugging.</summary>
        FixedSeed,
    }

    /// <summary>
    /// Every tunable parameter of the protocol, in one asset.
    ///
    /// Timings live here rather than as magic numbers inside coroutines so that the protocol
    /// can be changed (and, importantly, *reported*) without reading code. All durations are
    /// in seconds unless the name says otherwise.
    ///
    /// ============================ PROTOTYPE DEFAULTS ============================
    /// The values shipped in this asset are DEVELOPMENT DEFAULTS chosen to make the flow
    /// testable end to end. They are NOT a validated clinical protocol and carry no normative
    /// claim. In particular: three chair trials, the LOW -> MEDIUM -> HIGH sequence, the
    /// distractor-similarity profiles, the word timings, the recall durations and the chair
    /// attribute space are all provisional. Every one of them is editable in this asset without
    /// touching code, and whatever they are set to is written into the session record.
    /// ============================================================================
    ///
    /// Create via: Assets ▸ Create ▸ IKEA_EEG ▸ Experiment Config
    /// </summary>
    [CreateAssetMenu(fileName = "ExperimentConfig_Default",
        menuName = "IKEA_EEG/Experiment Config", order = 0)]
    public class ExperimentConfig : ScriptableObject
    {
        [Header("Area 0 — VR familiarization")]
        [Tooltip("Start the session in the familiarization room. Turn OFF for a researcher fast " +
                 "path straight to Area A; the session is then logged as FAMILIARIZATION_SKIPPED. " +
                 "Skipping changes nothing about the seed, the word set or the difficulty " +
                 "sequence — Area 0 produces no cognitive data at all.")]
        public bool enableFamiliarization = true;

        [Tooltip("Participant-facing familiarization instructions. Must not mention chairs or " +
                 "any memory word — Area 0 may not prime Area A or Area B.")]
        [TextArea(5, 10)]
        public string familiarizationInstructionText =
            "<b>VR Familiarization</b>\n\n" +
            "Before the cognitive test begins, take a moment to learn the controls.\n\n" +
            "Point at the practice objects and select them using either trigger that feels " +
            "comfortable.\n\n" +
            "When you are ready, press START EXPERIMENT.";

        [Tooltip("Practice-task prompt. {COLOR} is replaced with the requested colour.")]
        [TextArea(1, 3)]
        public string practicePromptText = "<b>Practice Selection</b>\n\nSelect the {COLOR} object.";

        [Tooltip("Shown when the participant is practised enough to begin.")]
        [TextArea(1, 3)]
        public string practiceSuccessText = "Great! Selection successful.";

        [Tooltip("How many practice selections count as 'practised enough'. The participant " +
                 "must ALSO have selected the requested colour. Neither condition gates START " +
                 "EXPERIMENT — they only control when it gains its 'ready' emphasis.")]
        [Min(1)]
        public int practiceMinimumSelections = 2;

        [Tooltip("Shown next to START EXPERIMENT once the practice task has been completed. " +
                 "START EXPERIMENT is available before this too — practice is never required.")]
        [TextArea(1, 3)]
        public string practiceReadyText = "You are ready to begin.";

        [Tooltip("Spoken familiarization narration, one entry per language. Keys are the " +
                 "language codes EN / ES / JA. Generate with IKEA_EEG ▸ Generate " +
                 "Familiarization Narration (local TTS). NOT part of the verbal-memory " +
                 "stimulus set — it uses its own audio path and its own folders.")]
        public List<NarrationClipEntry> familiarizationNarrationClips = new List<NarrationClipEntry>();

        [Tooltip("Spoken lead-in to the practice task, one entry per language (EN / ES / JA).")]
        public List<NarrationClipEntry> practiceIntroNarrationClips = new List<NarrationClipEntry>();

        [Tooltip("One spoken 'Select the X object.' per language AND colour. Keys are " +
                 "'<LANG>_<COLOR>', e.g. ES_BLUE.")]
        public List<NarrationClipEntry> practiceColorNarrationClips = new List<NarrationClipEntry>();

        [Tooltip("Spoken feedback for the practice task: correct, another, incorrect, complete. " +
                 "Keys are '<LANG>_<ID>', e.g. EN_CORRECT. Generate with IKEA_EEG ▸ Generate " +
                 "Familiarization Narration (local TTS). Interaction feedback only — it never " +
                 "carries a cognitive stimulus.")]
        public List<NarrationClipEntry> practiceFeedbackNarrationClips =
            new List<NarrationClipEntry>();

        [Tooltip("Spoken Recognition-mode encoding instructions, one entry per language " +
                 "(EN / ES / JA). This narrates the INSTRUCTION only. The encoding words " +
                 "themselves are visual and are never spoken.")]
        public List<NarrationClipEntry> recognitionInstructionNarrationClips =
            new List<NarrationClipEntry>();

        /// <summary>
        /// A practice-feedback clip for a language and a feedback id, or null.
        ///
        /// Returns NULL rather than another language's clip, exactly as the other narration
        /// lookups do: hearing English feedback in a Spanish session would contradict the
        /// on-screen text, and the caller already degrades to text-only.
        /// </summary>
        public AudioClip GetPracticeFeedbackClip(Localization.ExperimentLanguage language,
            string feedbackId)
        {
            var code = Localization.ExperimentLanguages.ToCode(language);
            return Lookup(practiceFeedbackNarrationClips, $"{code}_{feedbackId}");
        }

        [Tooltip("Spoken GENERAL Area B task instructions, one entry per language. Explains HOW " +
                 "the chair task works. It must NEVER contain a trial's target colour, size or " +
                 "shape — those are what the participant is being asked to work out.")]
        public List<NarrationClipEntry> areaBInstructionNarrationClips =
            new List<NarrationClipEntry>();

        [Tooltip("Spoken Recognition-mode DELAYED instructions, one entry per language. " +
                 "Instruction only — the delayed stimulus words themselves are visual and are " +
                 "never spoken.")]
        public List<NarrationClipEntry> recognitionDelayedNarrationClips =
            new List<NarrationClipEntry>();

        /// <summary>General Area B task narration for a language, or null.</summary>
        public AudioClip GetAreaBInstructionNarration(
            Localization.ExperimentLanguage language) =>
            Lookup(areaBInstructionNarrationClips,
                Localization.ExperimentLanguages.ToCode(language));

        /// <summary>Recognition delayed-phase instructions for a language, or null.</summary>
        public AudioClip GetRecognitionDelayedNarration(
            Localization.ExperimentLanguage language) =>
            Lookup(recognitionDelayedNarrationClips,
                Localization.ExperimentLanguages.ToCode(language));

        /// <summary>Recognition encoding instructions for a language, or null.</summary>
        public AudioClip GetRecognitionInstructionNarration(
            Localization.ExperimentLanguage language) =>
            Lookup(recognitionInstructionNarrationClips,
                Localization.ExperimentLanguages.ToCode(language));

        static AudioClip Lookup(List<NarrationClipEntry> entries, string id)
        {
            if (entries == null || string.IsNullOrEmpty(id))
                return null;

            foreach (var entry in entries)
            {
                if (entry != null && entry.clip != null &&
                    string.Equals(entry.id, id, System.StringComparison.OrdinalIgnoreCase))
                {
                    return entry.clip;
                }
            }

            return null;
        }

        /// <summary>
        /// The general narration for a language, or null when that language has none.
        ///
        /// Returns NULL rather than another language's clip: hearing English instructions in a
        /// Japanese session would be worse than hearing none, because the on-screen text would
        /// disagree with the voice.
        /// </summary>
        public AudioClip GetFamiliarizationNarration(Localization.ExperimentLanguage language) =>
            Lookup(familiarizationNarrationClips, Localization.ExperimentLanguages.ToCode(language));

        public AudioClip GetPracticeIntroNarration(Localization.ExperimentLanguage language) =>
            Lookup(practiceIntroNarrationClips, Localization.ExperimentLanguages.ToCode(language));

        /// <summary>
        /// The spoken "Select the X object." clip for a language and colour, or null.
        ///
        /// TYPED, not string-keyed at the call site: the colour arrives as a ChairColor and the
        /// key is built here by <see cref="PracticeColors.NarrationKey"/> — the same helper the
        /// authoring-time generator uses to name the clips. A caller cannot pass a colour name
        /// that does not correspond to a colour, and the requested colour is never inferred from
        /// a clip's file name.
        ///
        /// Returns NULL rather than another language's or another colour's clip.
        /// </summary>
        public AudioClip GetPracticeColorClip(Localization.ExperimentLanguage language,
            ChairColor color)
        {
            return Lookup(practiceColorNarrationClips,
                PracticeColors.NarrationKey(language, color));
        }

        [Tooltip("Participant-facing text shown for the WHOLE recall response window. It means " +
                 "'the microphone is capturing', not 'we have heard you' — it must never depend " +
                 "on the speech detector.")]
        [TextArea(1, 3)]
        public string recordingActiveText = "Recording…";

        [Header("RESTART data safety")]
        [Tooltip("OFF (recommended): a run discarded by RESTART keeps its files and gains a " +
                 "SESSION_DISCARDED_BY_RESTART marker; no summary is generated for it. ON: the " +
                 "current run's folder is DELETED. Deletion is guarded to that one folder, but " +
                 "a mistaken restart then destroys data permanently — hence off by default.")]
        public bool restartDeletesDiscardedRunFiles;

        [Header("Verbal-memory protocol")]
        [Tooltip("Which verbal-memory protocol this run uses.\n\n" +
                 "FreeRecall  — the original task: 5 SPOKEN words, then spoken immediate and " +
                 "delayed free recall. Unchanged and still fully functional.\n\n" +
                 "Recognition — CNS-DERIVED (not CNS-equivalent): 15 VISUAL words, then " +
                 "immediate and delayed SEEN BEFORE / NOT SEEN BEFORE recognition.\n\n" +
                 "The two paths share the state machine but no stimulus code. Switching mode " +
                 "does not migrate data between them.")]
        public VerbalProtocolMode protocolMode = VerbalProtocolMode.Recognition;

        [Header("Word list — FreeRecall protocol")]
        [Tooltip("Configurable word sets. Never hard-code words in UI or logic. " +
                 "Used ONLY when protocolMode is FreeRecall.")]
        public WordListDefinition wordList;

        [Tooltip("Which set from the word list this trial uses.")]
        public int wordSetIndex;

        [Header("Word list — Recognition protocol")]
        [Tooltip("Targets and lures for the recognition protocol. Used ONLY when protocolMode " +
                 "is Recognition. This asset is NOT the CNS word list and must not be described " +
                 "as one; see its own provenance field.")]
        public RecognitionWordList recognitionWordList;

        [Tooltip("How long each VISUAL encoding word stays on screen, seconds. The protocol " +
                 "specifies 2 s.")]
        [Range(0.25f, 10f)]
        public float recognitionWordDisplaySeconds = 2f;

        [Tooltip("Blank gap between one encoding word disappearing and the next appearing, " +
                 "seconds. A non-zero gap makes each onset a discrete event for EEG rather " +
                 "than one word morphing into the next.")]
        [Range(0f, 3f)]
        public float recognitionInterWordGapSeconds = 0.25f;

        [Tooltip("Maximum time to wait for a response to one recognition item, seconds. On " +
                 "expiry the item is logged with NO RESPONSE and the sequence advances — an " +
                 "unanswered item is never scored as an error.")]
        [Range(1f, 60f)]
        public float recognitionResponseTimeoutSeconds = 15f;

        [Tooltip("DEPRECATED - NO LONGER USED BY THE RECOGNITION TASK. " +
                 "This was the deliberate blank interval between one recognition item and the " +
                 "next. It has been removed from the item loop: the next word now appears on " +
                 "the following frame, and input safety is handled by an input-release guard " +
                 "on the response buttons instead of by waiting. " +
                 "The FIELD is retained, not deleted, so that existing ExperimentConfig assets " +
                 "and any archived session records that quote it still deserialize and still " +
                 "read the same. Changing it now has NO effect on the protocol.")]
        [Range(0f, 2f)]
        public float recognitionInterItemGapSeconds = 0.15f;

        [Tooltip("DEVELOPER / QA ONLY — LEAVE OFF FOR EVERY PARTICIPANT RUN.\n\n" +
                 "Allows a hidden overlay that names the CURRENT recognition item as TARGET or " +
                 "LURE and states which answer would be correct. It is a testing aid for " +
                 "checking that the sequence and the response buttons behave, and it tells the " +
                 "wearer the answer.\n\n" +
                 "This flag is the gate, not the visibility: with it off the overlay cannot be " +
                 "toggled on, is never populated and its labels stay empty. Turning it on only " +
                 "ENABLES the toggle — the overlay still starts hidden and must be switched on " +
                 "deliberately with the right controller's B button.\n\n" +
                 "It never answers, never advances an item and never touches scoring, timing, " +
                 "the CSV, the markers or the EEG.")]
        public bool enableRecognitionDeveloperCheatsheet;

        [Header("Area A — instructions & encoding")]
        [Tooltip("How long the Area A instruction panel is shown before the words start.")]
        public float instructionDurationSeconds = 8f;

        [Tooltip("Fallback duration for a word, used ONLY if its spoken recording is missing. " +
                 "Normally pacing follows the length of the recording itself.")]
        public float wordDisplaySeconds = 2f;

        [Tooltip("Silent gap between the end of one spoken word and the start of the next.")]
        public float interWordGapSeconds = 0.9f;

        [Tooltip("Pause between the 'listen' prompt appearing and the first spoken word.")]
        public float preFirstWordDelaySeconds = 1.5f;

        [Tooltip("Pause between the last spoken word and the recall beep.")]
        public float preBeepDelaySeconds = 1f;

        [Header("Recall — duration")]
        [Tooltip("HARD MAXIMUM for the immediate recall recording. The recording normally ends " +
                 "earlier, when the participant has spoken and then fallen silent for " +
                 "Recall Silence Stop Seconds. This is the safety fallback, and also what " +
                 "happens if the participant never speaks.")]
        [FormerlySerializedAs("immediateRecallSeconds")]
        public float immediateRecallMaxDuration = 20f;

        [Tooltip("HARD MAXIMUM for the delayed recall recording. Same rule as the immediate one.")]
        [FormerlySerializedAs("delayedRecallSeconds")]
        public float delayedRecallMaxDuration = 20f;

        [Header("Recall — adaptive stop (PROTOTYPE DEFAULTS, not validated)")]
        [Tooltip("Continuous silence, AFTER speech has been detected, that ends the recording. " +
                 "Must be long enough to survive normal pauses between recalled words.")]
        [Range(1f, 15f)]
        public float recallSilenceStopSeconds = 4f;

        [Tooltip("Peak amplitude (0-1) an analysis window must reach to count as speech. " +
                 "Deliberately well above the silence threshold so breath and room noise do " +
                 "not qualify. ~0.05 is about -26 dBFS.")]
        [Range(0.005f, 0.5f)]
        public float speechStartThreshold = 0.05f;

        [Tooltip("Peak amplitude (0-1) below which an analysis window counts as silence. " +
                 "Lower than the speech threshold on purpose: the gap between the two is " +
                 "hysteresis, and anything inside it counts as neither and does not end the " +
                 "recording. ~0.015 is about -36 dBFS.")]
        [Range(0.001f, 0.2f)]
        public float silenceThreshold = 0.015f;

        [Tooltip("How much voiced audio must accumulate before the recording is allowed to " +
                 "stop on silence at all. Guards against a cough or a bump arming the detector.")]
        [Range(0.05f, 3f)]
        public float minimumSpeechDuration = 0.35f;

        [Tooltip("How long the Area C delayed-recall instruction is shown before the beep.")]
        public float delayedInstructionDurationSeconds = 7f;

        [Header("Randomisation")]
        [Tooltip("NewSeedEachSession for real runs; FixedSeed to replay one configuration " +
                 "while developing. The seed used is written to SESSION_START, to every CSV " +
                 "row and to the researcher summary either way.")]
        public SeedMode seedMode = SeedMode.NewSeedEachSession;

        [Tooltip("Seed used when Seed Mode is FixedSeed. Also the seed the 'Replay Same Seed' " +
                 "researcher control starts from if no session has run yet.")]
        public long fixedSeed = 20250812L;

        [Header("Area B — chair task")]
        [Tooltip("PROTOTYPE DEFAULT (3). How many chair-selection trials one Area B visit " +
                 "contains. The participant stays in Area B for all of them.")]
        [Min(1)]
        public int chairTrialsPerRun = 3;

        [Tooltip("PROTOTYPE DEFAULT (LOW, MEDIUM, HIGH). Difficulty of each chair trial, in " +
                 "order. If it is shorter than Chair Trials Per Run the sequence repeats; if " +
                 "it is longer, the extra entries are unused.")]
        public List<DifficultyLevel> difficultySequence = new List<DifficultyLevel>
        {
            DifficultyLevel.Low,
            DifficultyLevel.Medium,
            DifficultyLevel.High,
        };

        [Tooltip("What each difficulty level means, expressed as how similar the distractors " +
                 "are to the target. See DifficultyProfile.")]
        public List<DifficultyProfile> difficultyProfiles = DifficultyProfile.CreateDefaults();

        [Tooltip("Legacy fixed target, used only when Use Generated Chair Trials is off. The " +
                 "generated protocol ignores it.")]
        public ChairSpec targetChair = new ChairSpec(ChairColor.Blue, ChairSize.Large, ChairShape.Solid);

        [Tooltip("ON: every trial's target and layout come from the seeded generator (the " +
                 "repeatable protocol). OFF: the single fixed Target Chair is used for every " +
                 "trial, which reproduces the original one-trial MVP behaviour.")]
        public bool useGeneratedChairTrials = true;

        [Tooltip("Blank interval between the chairs being arranged for a trial and the target " +
                 "specification appearing. It is NOT part of the response time — the timer " +
                 "starts at target onset — so it only paces the trial.")]
        public float preTargetIntervalSeconds = 0.75f;

        [Tooltip("OBSOLETE — no longer used. It used to hold the target on screen before " +
                 "selection opened, which put reading time inside the response time. The task " +
                 "instructions are now a separate READY-gated screen and the response timer " +
                 "starts at target onset. Kept only so existing config assets do not lose the " +
                 "value; delete it once no protocol references it.")]
        public float chairInstructionDurationSeconds = 5f;

        [Tooltip("How long the brief post-selection feedback stays on screen.")]
        public float chairFeedbackDurationSeconds = 1.5f;

        [Tooltip("Blank interval between the end of one chair trial and the instruction of the " +
                 "next. Keeps the selection response from bleeding into the next instruction.")]
        public float interTrialIntervalSeconds = 1.5f;

        [Tooltip("Play an auditory cue when the chair instruction appears.")]
        public bool playChairInstructionCue = true;

        [Tooltip("Log a CHAIR_HOVER_ENTER row every time the ray enters a chair. OFF by " +
                 "default: it fires on every ray sweep and adds a lot of rows. Turn it on when " +
                 "diagnosing pointing behaviour.")]
        public bool logChairHoverEvents;

        [Header("Text shown to the participant")]
        [TextArea(3, 6)]
        public string areaAInstructionText =
            "You will hear five words. Remember them.\n\n" +
            "After the beep, repeat the five words in the same order.";

        [Tooltip("Shown for the WHOLE encoding phase. It must never contain the target words " +
                 "— they are delivered by audio only.")]
        [TextArea(2, 4)]
        public string encodingListenPromptText =
            "Listen carefully.";

        [TextArea(2, 4)]
        public string immediateRecallPromptText =
            "Please repeat the five words now.";

        [TextArea(2, 4)]
        public string readyForAreaBText =
            "Well done.\n\nPress ENTER SHOWROOM to continue.";

        [Tooltip("GENERAL executive-task instructions, shown ONCE on entering Area B and gated " +
                 "by the READY button. It must NOT contain a target: the participant reads this " +
                 "before any trial exists, and no response timer is running.")]
        [TextArea(5, 10)]
        public string areaBGeneralInstructionText =
            "<b>Executive Task</b>\n\n" +
            "You will be shown a set of characteristics.\n\n" +
            "Select the ONE chair that matches\nALL THREE characteristics.\n\n" +
            "Use the controller trigger to select.\n\n" +
            "Press READY when you understand the task.";

        [Tooltip("Heading above the three target attributes. Kept short — this is on screen " +
                 "while the response timer runs.")]
        [TextArea(1, 3)]
        public string chairTargetHeaderText = "Find the chair that is:";

        [Tooltip("Show the shape-category legend on the Area B GENERAL instruction screen. " +
                 "The legend is NEVER shown during a trial: during a trial the participant sees " +
                 "only the three target attributes, so the legend cannot act as a visual aid " +
                 "while the response timer is running.")]
        public bool showShapeLegendOnInstructions = true;

        [Tooltip("Brief post-selection feedback. Keep it short: it sits inside the interval " +
                 "between encoding and delayed recall.")]
        [TextArea(2, 4)]
        public string chairSelectedFeedbackText =
            "Selection recorded.";

        [Tooltip("Heading of the end-of-block result. The accuracy line is generated from the " +
                 "valid scored trials and appended underneath.")]
        [TextArea(1, 3)]
        public string executiveTaskCompleteText = "Executive task complete";

        [TextArea(3, 6)]
        public string areaCInstructionText =
            "Once you hear the beep, repeat the five words that were presented " +
            "at the beginning of the session.";

        [TextArea(2, 4)]
        public string delayedRecallPromptText =
            "Please repeat the five words now.";

        [Header("Feedback policy")]
        [Tooltip("If off, the participant is not told whether the chair was correct " +
                 "(the chair still changes colour and the CSV still records correctness).")]
        public bool showCorrectnessToParticipant;

        [TextArea(2, 4)]
        [Tooltip("Footer under the end-of-block accuracy line. Participant-facing only — no " +
                 "reaction times, no seed, no target details, no file paths.")]
        public string chairBlockCompleteText =
            "Press EXIT SHOWROOM to continue.";

        /// <summary>Words for this trial, from the configured set.</summary>
        public System.Collections.Generic.IReadOnlyList<string> GetTrialWords()
        {
            return wordList != null
                ? wordList.GetWords(wordSetIndex)
                : System.Array.Empty<string>();
        }

        public string GetTrialWordSetName()
        {
            return wordList != null ? wordList.GetSetName(wordSetIndex) : string.Empty;
        }

        /// <summary>Stable id of the word set in use, written to the CSV word_set_id column.</summary>
        public string GetTrialWordSetId()
        {
            return wordList != null ? wordList.GetSetId(wordSetIndex) : string.Empty;
        }

        /// <summary>
        /// The difficulty of each chair trial, expanded to exactly
        /// <see cref="chairTrialsPerRun"/> entries.
        ///
        /// A sequence shorter than the trial count repeats (LOW, MEDIUM, HIGH over 5 trials
        /// gives LOW, MEDIUM, HIGH, LOW, MEDIUM) rather than failing or silently shortening the
        /// run — so changing only the trial count is always a valid edit.
        /// </summary>
        public List<DifficultyLevel> BuildDifficultySequence()
        {
            var result = new List<DifficultyLevel>(Mathf.Max(1, chairTrialsPerRun));

            if (difficultySequence == null || difficultySequence.Count == 0)
            {
                for (var i = 0; i < chairTrialsPerRun; i++)
                    result.Add(DifficultyLevel.Medium);

                return result;
            }

            for (var i = 0; i < chairTrialsPerRun; i++)
                result.Add(difficultySequence[i % difficultySequence.Count]);

            return result;
        }

        /// <summary>Sanity-checks the config at start-up so problems surface before a run.</summary>
        public bool Validate(out string problem)
        {
            if (wordList == null)
            {
                problem = "ExperimentConfig has no WordListDefinition assigned.";
                return false;
            }

            if (!wordList.Validate(out problem))
                return false;

            if (chairTrialsPerRun < 1)
            {
                problem = $"chairTrialsPerRun is {chairTrialsPerRun}; at least 1 is required.";
                return false;
            }

            // The two thresholds must not cross, or a window could count as speech AND silence
            // and the detector's behaviour would depend on evaluation order.
            if (speechStartThreshold <= silenceThreshold)
            {
                problem = $"speechStartThreshold ({speechStartThreshold}) must be greater than " +
                          $"silenceThreshold ({silenceThreshold}); the gap between them is the " +
                          "hysteresis that stops room noise from ending a recording.";
                return false;
            }

            if (recallSilenceStopSeconds >= immediateRecallMaxDuration ||
                recallSilenceStopSeconds >= delayedRecallMaxDuration)
            {
                problem = $"recallSilenceStopSeconds ({recallSilenceStopSeconds}) is not shorter " +
                          "than a recall maximum duration, so the adaptive stop could never fire.";
                return false;
            }

            // Every difficulty the run will actually use must have a profile, and each profile
            // must be able to produce an unambiguous six-chair trial.
            foreach (var level in BuildDifficultySequence())
            {
                var profile = ChairTrialGenerator.FindProfile(difficultyProfiles, level);

                if (profile == null)
                {
                    problem = $"the difficulty sequence uses {level} but no profile defines it.";
                    return false;
                }

                if (!profile.Validate(6, out problem))
                    return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// One-line record of the parameters that define this protocol run, for the session log
        /// and the researcher summary. What was configured has to be recoverable from the data.
        /// </summary>
        public string DescribeProtocol()
        {
            var sequence = BuildDifficultySequence();
            var levels = new List<string>(sequence.Count);
            foreach (var level in sequence)
                levels.Add(level.ToString().ToUpperInvariant());

            return $"chair_trials={chairTrialsPerRun}; " +
                   $"difficulty_sequence={string.Join("->", levels)}; " +
                   $"generated_trials={(useGeneratedChairTrials ? "TRUE" : "FALSE")}; " +
                   $"chair_feedback_s={chairFeedbackDurationSeconds}; " +
                   $"iti_s={interTrialIntervalSeconds}; " +
                   $"pre_target_s={preTargetIntervalSeconds}; " +
                   $"word_set={GetTrialWordSetId()}; " +
                   $"inter_word_gap_s={interWordGapSeconds}; " +
                   $"instruction_s={instructionDurationSeconds}; " +
                   $"immediate_recall_max_s={immediateRecallMaxDuration}; " +
                   $"delayed_recall_max_s={delayedRecallMaxDuration}; " +
                   $"recall_silence_stop_s={recallSilenceStopSeconds}; " +
                   $"speech_start_threshold={speechStartThreshold}; " +
                   $"silence_threshold={silenceThreshold}; " +
                   $"min_speech_s={minimumSpeechDuration}";
        }
    }
}
