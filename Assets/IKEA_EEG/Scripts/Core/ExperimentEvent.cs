using System.Globalization;

namespace IkeaEeg.Core
{
    /// <summary>
    /// One experimental event == one CSV row == one future LSL marker.
    ///
    /// All optional fields are strings so that "not applicable" serialises as an empty cell
    /// rather than as a misleading 0 / -1. Analysis code (Python/MATLAB) can then rely on
    /// "empty means this column does not apply to this event type".
    /// </summary>
    public class ExperimentEvent
    {
        // Timing ------------------------------------------------------------------------
        /// <summary>Wall-clock time, local, ISO-like with milliseconds.</summary>
        public string timestampAbsolute = string.Empty;

        /// <summary>Seconds since SESSION_START, from a monotonic Stopwatch.</summary>
        public double timestampRelative;

        /// <summary>Seconds since the current TRIAL_START (empty before the first trial).</summary>
        public string elapsedTrialTime = string.Empty;

        // Identity ----------------------------------------------------------------------
        public string sessionId = string.Empty;
        public string trialId = string.Empty;
        public string experimentState = string.Empty;
        public string room = RoomNames.None;
        public string eventType = string.Empty;
        public string objectId = string.Empty;

        // Chair task --------------------------------------------------------------------
        public string targetColor = string.Empty;
        public string targetSize = string.Empty;
        public string targetShape = string.Empty;
        public string selectedColor = string.Empty;
        public string selectedSize = string.Empty;
        public string selectedShape = string.Empty;
        public string correct = string.Empty;        // "TRUE" / "FALSE" / ""
        public string responseTimeMs = string.Empty;

        // Memory task -------------------------------------------------------------------
        public string wordIndex = string.Empty;
        public string expectedWord = string.Empty;
        public string recallPhase = string.Empty;
        public string transcript = string.Empty;

        // Free-form ---------------------------------------------------------------------
        public string notes = string.Empty;

        // ---------------------------------------------------------------------------------
        // Protocol columns added by the repeatable-protocol pass. They are appended AFTER
        // 'notes' in the CSV so every column index an existing analysis script already relies
        // on keeps its meaning.
        // ---------------------------------------------------------------------------------

        /// <summary>1-based index of the chair trial within this Area B block. Empty elsewhere.</summary>
        public string chairTrialIndex = string.Empty;

        /// <summary>How many chair trials this run contains. Empty outside the chair task.</summary>
        public string chairTrialCount = string.Empty;

        /// <summary>LOW / MEDIUM / HIGH for chair-trial events.</summary>
        public string difficulty = string.Empty;

        /// <summary>Session randomisation seed. Stamped on EVERY row of the session.</summary>
        public string randomizationSeed = string.Empty;

        /// <summary>Stable id of the word set in use, e.g. SET_A.</summary>
        public string wordSetId = string.Empty;

        /// <summary>Name of the AudioClip that carried this stimulus.</summary>
        public string clipName = string.Empty;

        /// <summary>Absolute DSP time the stimulus was scheduled for (seconds).</summary>
        public string scheduledAudioTime = string.Empty;

        /// <summary>Absolute DSP time the stimulus onset was observed (seconds).</summary>
        public string confirmedAudioTime = string.Empty;

        // ---------------------------------------------------------------------------------
        // Multi-run identifiers. Appended after the protocol columns for the same reason —
        // every column index an existing analysis relies on keeps its meaning.
        //
        //   session_id            = this RUN's unique id (unchanged meaning)
        //   experiment_session_id = shared by every run of one participant sitting
        //   run_index             = 1, 2, 3 ... within that sitting
        // ---------------------------------------------------------------------------------

        /// <summary>Identifier shared across the runs of one participant sitting.</summary>
        public string experimentSessionId = string.Empty;

        /// <summary>1-based run number within the experiment session.</summary>
        public string runIndex = string.Empty;

        /// <summary>TRUE once a developer action has compromised this run.</summary>
        public string developerInterrupted = string.Empty;

        /// <summary>Platform language of this experiment session: EN / ES / JA.</summary>
        public string platformLanguage = string.Empty;

        /// <summary>
        /// This event's time on the LSL CLOCK — liblsl's local_clock(), the same time base the
        /// EEG samples carry.
        ///
        /// WHY A SECOND TIMESTAMP: the existing columns are the behavioural record and are
        /// unchanged — timestamp_relative remains the session clock, and every reaction time is
        /// still measured exactly as before. But the session clock and the EEG stream are two
        /// unrelated clocks, and subtracting one from the other is meaningless. This column is
        /// the ONE value that lets an event be located in the EEG, so a window can be cut
        /// around it without any conversion that could be got wrong.
        ///
        /// Empty when liblsl is not available. It is never filled from Time.time, DateTime.Now
        /// or a stopwatch: a plausible-looking number in this column would silently misalign
        /// every epoch, and an empty one at least says so.
        /// </summary>
        public string lslTimestamp = string.Empty;

        // ---- Recognition-protocol columns, APPENDED ------------------------------------
        // Every one of these is empty for a FreeRecall run and for every event that is not a
        // recognition item, exactly as the chair columns are empty outside Area B. Appending
        // rather than reusing existing columns keeps files from the two protocols readable by
        // the same script: a FreeRecall file is byte-identical in its first 35 columns to one
        // produced before this protocol existed.

        /// <summary>FREE_RECALL or RECOGNITION — which protocol produced this row.</summary>
        public string protocolMode = string.Empty;

        /// <summary>Stable item id, e.g. TARGET_03 / IMM_LURE_07. Independent of shuffle order.</summary>
        public string recognitionItemId = string.Empty;

        /// <summary>TARGET or LURE. What the item actually was, regardless of the answer.</summary>
        public string recognitionItemClass = string.Empty;

        /// <summary>IMMEDIATE or DELAYED.</summary>
        public string recognitionPhase = string.Empty;

        /// <summary>1-based position in the randomised sequence actually presented.</summary>
        public string recognitionPresentationOrder = string.Empty;

        /// <summary>SEEN_BEFORE, NOT_SEEN_BEFORE, or NONE.</summary>
        public string recognitionResponse = string.Empty;

        /// <summary>HIT, MISS, CORRECT_REJECTION, FALSE_ALARM or NO_RESPONSE. Derived.</summary>
        public string recognitionOutcome = string.Empty;

        /// <summary>Milliseconds from THIS item's onset to its response. Empty if unanswered.</summary>
        public string recognitionReactionTimeMs = string.Empty;

        /// <summary>Stimulus onset time on the session clock, seconds.</summary>
        public string stimulusOnsetTime = string.Empty;

        /// <summary>Stimulus offset time on the session clock, seconds.</summary>
        public string stimulusOffsetTime = string.Empty;

        /// <summary>Resets every field so instances can be pooled/reused safely.</summary>
        public void Clear()
        {
            timestampAbsolute = string.Empty;
            timestampRelative = 0d;
            elapsedTrialTime = string.Empty;
            sessionId = string.Empty;
            trialId = string.Empty;
            experimentState = string.Empty;
            room = RoomNames.None;
            eventType = string.Empty;
            objectId = string.Empty;
            targetColor = targetSize = targetShape = string.Empty;
            selectedColor = selectedSize = selectedShape = string.Empty;
            correct = string.Empty;
            responseTimeMs = string.Empty;
            wordIndex = string.Empty;
            expectedWord = string.Empty;
            recallPhase = string.Empty;
            transcript = string.Empty;
            notes = string.Empty;
            chairTrialIndex = string.Empty;
            chairTrialCount = string.Empty;
            difficulty = string.Empty;
            wordSetId = string.Empty;
            clipName = string.Empty;
            scheduledAudioTime = string.Empty;
            confirmedAudioTime = string.Empty;
            experimentSessionId = string.Empty;
            runIndex = string.Empty;
            developerInterrupted = string.Empty;
            platformLanguage = string.Empty;
            lslTimestamp = string.Empty;
            protocolMode = string.Empty;
            recognitionItemId = string.Empty;
            recognitionItemClass = string.Empty;
            recognitionPhase = string.Empty;
            recognitionPresentationOrder = string.Empty;
            recognitionResponse = string.Empty;
            recognitionOutcome = string.Empty;
            recognitionReactionTimeMs = string.Empty;
            stimulusOnsetTime = string.Empty;
            stimulusOffsetTime = string.Empty;

            // Cleared like everything else; the EventLogger re-stamps it on every row of the
            // session immediately after Clear(), so no row can be written without a seed.
            randomizationSeed = string.Empty;
        }

        /// <summary>Compact one-line form used for the Unity Console.</summary>
        public string ToConsoleString()
        {
            var extra = string.Empty;
            if (!string.IsNullOrEmpty(chairTrialIndex))
                extra += $" chair_trial={chairTrialIndex}/{chairTrialCount} [{difficulty}]";
            if (!string.IsNullOrEmpty(objectId)) extra += $" object={objectId}";
            if (!string.IsNullOrEmpty(expectedWord)) extra += $" word[{wordIndex}]={expectedWord}";
            if (!string.IsNullOrEmpty(correct)) extra += $" correct={correct}";
            if (!string.IsNullOrEmpty(responseTimeMs)) extra += $" rt={responseTimeMs}ms";
            if (!string.IsNullOrEmpty(notes)) extra += $" notes=\"{notes}\"";

            return string.Format(CultureInfo.InvariantCulture,
                "[IKEA_EEG] t={0:F3}s {1} state={2} room={3}{4}",
                timestampRelative, eventType, experimentState, room, extra);
        }
    }
}
