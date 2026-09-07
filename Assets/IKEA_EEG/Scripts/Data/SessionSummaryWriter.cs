using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using IkeaEeg.Experiment;
using IkeaEeg.Memory;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Writes the three derived files that sit next to the raw event CSV.
    ///
    ///   session_summary_&lt;session_id&gt;.csv   one row per chair trial, analysis-shaped
    ///   researcher_summary_&lt;session_id&gt;.txt the human-readable debrief
    ///   manual_recall_scoring_&lt;session_id&gt;.csv  an empty form for offline recall scoring
    ///
    /// NONE of these replaces anything. The event CSV and the WAV files remain the
    /// authoritative record; everything here is derived from them and can be deleted and
    /// regenerated without losing data. A failure to write any of them is logged and swallowed:
    /// losing a convenience file must never cost a session.
    /// </summary>
    public static class SessionSummaryWriter
    {
        public const string SummaryPrefix = "session_summary";
        public const string ResearcherPrefix = "researcher_summary";
        public const string ManualScoringPrefix = "manual_recall_scoring";

        /// <summary>
        /// One row per chair trial, with the session-level metadata repeated on every row.
        ///
        /// Repeating the metadata is deliberate: this is a tidy/long table that can be
        /// concatenated across sessions and grouped, without a join against a second file.
        /// </summary>
        static readonly string[] k_SummaryHeader =
        {
            // ---- session level (identical on every row) ----------------------------------
            "session_id",
            "randomization_seed",
            "seed_is_replay",
            "word_set_id",
            "protocol",
            "chair_trials_total",
            "chair_trials_scored",
            "chair_trials_correct",
            "chair_accuracy",
            "mean_response_time_s",
            "median_response_time_s",
            "mean_response_time_ms",
            "median_response_time_ms",
            "total_experiment_duration_s",
            "total_experiment_duration_ms",
            "immediate_recall_status",
            "delayed_recall_status",
            "events_csv_path",
            "audio_folder",

            // ---- chair trial level -------------------------------------------------------
            "chair_trial_index",
            "chair_trial_count",
            "difficulty",
            "trial_seed",
            "target_color",
            "target_size",
            "target_shape",
            "target_chair_id",
            "target_slot_index",
            "selected_chair_id",
            "selected_color",
            "selected_size",
            "selected_shape",
            "attribute_match_count",
            "correct",
            "response_time_s",
            "response_time_ms",
            "trial_valid",
            "invalid_reason",

            // ---- Recognition session totals, APPENDED --------------------------------------
            // Session-level like the first block, so they repeat on every chair-trial row and
            // this stays a tidy/long table that can be concatenated and grouped without a join.
            //
            // APPENDED AFTER invalid_reason, never inserted: every column above keeps its name
            // AND its index, so a script written against the previous format reads these files
            // unchanged. This is the DERIVED summary file — the event CSV schema is untouched.
            //
            // IMMEDIATE and DELAYED are separate columns throughout. They are never summed here,
            // for the same reason they are never summed on screen.
            //
            // RAW COUNTS ONLY. There is no accuracy, no proportion, no d', no composite and no
            // score of any kind: those are analysis decisions, and a column named like a result
            // would be read as one. Everything here can be recomputed from the per-item rows of
            // events_<session_id>.csv, which remains the authoritative record.
            "protocol_mode",
            "immediate_recognition_hits",
            "immediate_recognition_misses",
            "immediate_recognition_correct_rejections",
            "immediate_recognition_false_alarms",
            "immediate_recognition_no_response",
            "immediate_recognition_items",
            "immediate_recognition_targets",
            "immediate_recognition_lures",
            "delayed_recognition_hits",
            "delayed_recognition_misses",
            "delayed_recognition_correct_rejections",
            "delayed_recognition_false_alarms",
            "delayed_recognition_no_response",
            "delayed_recognition_items",
            "delayed_recognition_targets",
            "delayed_recognition_lures",
        };

        public static string WriteSessionSummary(SessionResults results, string directory)
        {
            if (results == null || string.IsNullOrEmpty(directory))
                return string.Empty;

            var path = Path.Combine(directory, $"{SummaryPrefix}_{results.sessionId}.csv");

            try
            {
                Directory.CreateDirectory(directory);

                var sb = new StringBuilder(1024);
                sb.AppendLine(string.Join(",", k_SummaryHeader));

                if (results.chairTrials.Count == 0)
                {
                    // Still emit one row so a session with no chair trials is visible in the
                    // aggregate rather than silently absent.
                    sb.AppendLine(BuildRow(results, null));
                }
                else
                {
                    foreach (var trial in results.chairTrials)
                        sb.AppendLine(BuildRow(results, trial));
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[IKEA_EEG] Session summary CSV: {path}");
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Could not write the session summary CSV to " +
                               $"'{path}': {e.Message}");
                return string.Empty;
            }
        }

        static string BuildRow(SessionResults results, ChairTrialResult trial)
        {
            var sb = new StringBuilder(256);

            Append(sb, results.sessionId);
            Append(sb, results.randomizationSeed.ToString(CultureInfo.InvariantCulture));
            Append(sb, results.isSeedReplay ? "TRUE" : "FALSE");
            Append(sb, results.wordSetId);
            Append(sb, results.protocolDescription);
            Append(sb, results.chairTrialsTotal.ToString(CultureInfo.InvariantCulture));
            Append(sb, results.chairTrialsScored.ToString(CultureInfo.InvariantCulture));
            Append(sb, results.chairTrialsCorrect.ToString(CultureInfo.InvariantCulture));
            Append(sb, SessionResults.FormatRatio(results.chairAccuracy));
            Append(sb, SessionResults.FormatSeconds(results.meanResponseTimeSeconds));
            Append(sb, SessionResults.FormatSeconds(results.medianResponseTimeSeconds));
            Append(sb, FormatMs(results.meanResponseTimeMs));
            Append(sb, FormatMs(results.medianResponseTimeMs));
            Append(sb, results.totalExperimentDurationSeconds.ToString("F3", CultureInfo.InvariantCulture));
            Append(sb, (results.totalExperimentDurationSeconds * 1000d).ToString("F1", CultureInfo.InvariantCulture));
            Append(sb, results.immediateRecallStatus);
            Append(sb, results.delayedRecallStatus);
            Append(sb, results.csvPath);
            Append(sb, results.audioFolder);

            if (trial == null)
            {
                // Session-level row with no trial: every trial column stays empty rather than 0.
                for (var i = 0; i < 19; i++)
                    Append(sb, string.Empty);

                AppendRecognitionTotals(sb, results);
                return sb.ToString();
            }

            Append(sb, trial.trialIndex.ToString(CultureInfo.InvariantCulture));
            Append(sb, trial.trialCount.ToString(CultureInfo.InvariantCulture));
            Append(sb, trial.DifficultyLabel());
            Append(sb, trial.trialSeed.ToString(CultureInfo.InvariantCulture));
            Append(sb, trial.target.color.ToString());
            Append(sb, trial.target.size.ToString());
            Append(sb, trial.target.shape.ToString());
            Append(sb, trial.targetChairId);
            Append(sb, trial.targetSlotIndex.ToString(CultureInfo.InvariantCulture));

            if (trial.selectionMade)
            {
                Append(sb, trial.selectedChairId);
                Append(sb, trial.selected.color.ToString());
                Append(sb, trial.selected.size.ToString());
                Append(sb, trial.selected.shape.ToString());
                Append(sb, trial.attributeMatchCount.ToString(CultureInfo.InvariantCulture));
                Append(sb, trial.correct ? "TRUE" : "FALSE");
                Append(sb, trial.responseTimeSeconds.ToString("F4", CultureInfo.InvariantCulture));
                Append(sb, trial.responseTimeMs.ToString("F1", CultureInfo.InvariantCulture));
            }
            else
            {
                // No response: nothing is asserted about what was chosen or how long it took.
                for (var i = 0; i < 8; i++)
                    Append(sb, string.Empty);
            }

            Append(sb, trial.valid ? "TRUE" : "FALSE");
            Append(sb, trial.invalidReason);

            AppendRecognitionTotals(sb, results);
            return sb.ToString();
        }

        /// <summary>
        /// The 17 appended recognition columns, identical on every row of a session.
        ///
        /// A FreeRecall run leaves all sixteen count columns EMPTY rather than writing zeros —
        /// exactly as the chair columns are left empty outside Area B. "This protocol has no
        /// recognition phase" and "this phase produced zero hits" are different facts, and a 0
        /// would make them indistinguishable in an aggregate across sessions.
        /// </summary>
        static void AppendRecognitionTotals(StringBuilder sb, SessionResults results)
        {
            Append(sb, results.protocolMode.ToString().ToUpperInvariant());

            AppendPhase(sb, results.immediateRecognition, last: false);
            AppendPhase(sb, results.delayedRecognition, last: true);
        }

        static void AppendPhase(StringBuilder sb, RecognitionPhaseResults phase, bool last)
        {
            if (phase == null || !phase.hasData)
            {
                for (var i = 0; i < 8; i++)
                    Append(sb, string.Empty, last: last && i == 7);

                return;
            }

            Append(sb, phase.hits.ToString(CultureInfo.InvariantCulture));
            Append(sb, phase.misses.ToString(CultureInfo.InvariantCulture));
            Append(sb, phase.correctRejections.ToString(CultureInfo.InvariantCulture));
            Append(sb, phase.falseAlarms.ToString(CultureInfo.InvariantCulture));
            Append(sb, phase.noResponse.ToString(CultureInfo.InvariantCulture));
            Append(sb, phase.itemCount.ToString(CultureInfo.InvariantCulture));
            Append(sb, phase.targetCount.ToString(CultureInfo.InvariantCulture));
            Append(sb, phase.lureCount.ToString(CultureInfo.InvariantCulture), last: last);
        }

        // ---------------------------------------------------------------------------------
        // Researcher summary
        // ---------------------------------------------------------------------------------

        public static string WriteResearcherSummary(SessionResults results, string directory)
        {
            if (results == null || string.IsNullOrEmpty(directory))
                return string.Empty;

            var path = Path.Combine(directory, $"{ResearcherPrefix}_{results.sessionId}.txt");

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, results.BuildResearcherSummary(), new UTF8Encoding(false));
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Could not write the researcher summary to " +
                               $"'{path}': {e.Message}");
                return string.Empty;
            }
        }

        // ---------------------------------------------------------------------------------
        // Manual recall scoring form
        // ---------------------------------------------------------------------------------

        static readonly string[] k_ManualScoringHeader =
        {
            "session_id",
            "trial_id",
            "recall_phase",
            "wav_path",
            "expected_words",
            "audio_captured",
            // Everything below is left BLANK for the researcher to fill in offline.
            "transcript",
            "number_correct",
            "order_correct",
            "intrusions",
            "omissions",
            "scored_by",
            "scored_at",
            "notes",
        };

        /// <summary>
        /// Writes an empty scoring form listing this session's recall recordings.
        ///
        /// This is how recall gets scored while no transcription provider exists: a human fills
        /// in the blank columns from the WAV files. It is a SEPARATE file on purpose — attaching
        /// scores must never involve editing the raw event CSV or touching the audio, so the
        /// original record stays exactly as the session produced it and the scoring is
        /// attributable and reversible.
        /// </summary>
        public static string WriteManualScoringTemplate(SessionResults results,
            IReadOnlyList<string> expectedWords, IEnumerable<RecordingInfo> recordings,
            string directory)
        {
            if (results == null || string.IsNullOrEmpty(directory))
                return string.Empty;

            var path = Path.Combine(directory, $"{ManualScoringPrefix}_{results.sessionId}.csv");

            try
            {
                Directory.CreateDirectory(directory);

                var sb = new StringBuilder(512);
                sb.AppendLine(string.Join(",", k_ManualScoringHeader));

                var expected = expectedWords != null ? string.Join(" ", expectedWords) : string.Empty;
                var rows = 0;

                if (recordings != null)
                {
                    foreach (var info in recordings)
                    {
                        if (info == null)
                            continue;

                        var row = new StringBuilder(256);
                        Append(row, results.sessionId);
                        Append(row, info.trialId);
                        Append(row, info.recallPhase);
                        Append(row, info.wavPath);
                        Append(row, expected);
                        Append(row, info.audioCaptured ? "TRUE" : "FALSE");

                        // transcript .. notes: eight deliberately empty cells.
                        for (var i = 0; i < 8; i++)
                            Append(row, string.Empty, last: i == 7);

                        sb.AppendLine(row.ToString());
                        rows++;
                    }
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Debug.Log($"[IKEA_EEG] Manual recall scoring form ({rows} recording(s)): {path}");
                return path;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Could not write the manual scoring form to " +
                               $"'{path}': {e.Message}");
                return string.Empty;
            }
        }

        // ---------------------------------------------------------------------------------

        static string FormatMs(double milliseconds)
        {
            return double.IsNaN(milliseconds)
                ? "NA"
                : milliseconds.ToString("F1", CultureInfo.InvariantCulture);
        }

        static void Append(StringBuilder sb, string value, bool last = false)
        {
            sb.Append(Escape(value));
            if (!last)
                sb.Append(',');
        }

        /// <summary>RFC-4180 escaping, identical to the event CSV's.</summary>
        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
