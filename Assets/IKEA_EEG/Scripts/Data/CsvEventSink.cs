using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using IkeaEeg.Core;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Writes every experimental event to one CSV file per session.
    ///
    /// FILE LOCATION (Windows):
    ///   C:\Users\&lt;you&gt;\AppData\LocalLow\&lt;CompanyName&gt;\IKEA_EEG\IKEA_EEG_Data\&lt;session_id&gt;\events_&lt;session_id&gt;.csv
    /// which is Application.persistentDataPath. The absolute path is printed to the Unity
    /// Console at SESSION_START so it never has to be guessed.
    ///
    /// The stream is flushed after every row: if the Editor is stopped mid-trial or crashes,
    /// everything logged up to that moment is already on disk.
    /// </summary>
    [DisallowMultipleComponent]
    public class CsvEventSink : MonoBehaviour, IEventSink
    {
        [Tooltip("File name prefix. The session id and .csv extension are appended.")]
        [SerializeField] string m_FileNamePrefix = "events";

        StreamWriter m_Writer;
        string m_FilePath = string.Empty;
        int m_RowCount;

        /// <summary>Absolute path of the CSV currently being written (empty if none).</summary>
        public string filePath => m_FilePath;

        public int rowCount => m_RowCount;

        /// <summary>
        /// Column order of the CSV. Keep this in sync with <see cref="BuildRow"/>.
        /// Documented for the offline analysis scripts.
        /// </summary>
        static readonly string[] k_Header =
        {
            "timestamp_absolute",
            "timestamp_relative",
            "session_id",
            "trial_id",
            "experiment_state",
            "room",
            "event_type",
            "object_id",
            "target_color",
            "target_size",
            "target_shape",
            "selected_color",
            "selected_size",
            "selected_shape",
            "correct",
            "response_time_ms",
            "word_index",
            "expected_word",
            "recall_phase",
            "transcript",
            "elapsed_trial_time",
            "notes",

            // ---- Repeatable-protocol columns ---------------------------------------------
            // APPENDED, never inserted: the 22 columns above keep both their names AND their
            // positions, so an analysis script written against the previous format still works
            // unchanged on these files.
            "chair_trial_index",
            "chair_trial_count",
            "difficulty",
            "randomization_seed",
            "word_set_id",
            "clip_name",
            "scheduled_audio_time",
            "confirmed_audio_time",

            // ---- Multi-run columns, also APPENDED --------------------------------------
            // session_id above is this RUN's id; these place it inside a sitting.
            "experiment_session_id",
            "run_index",
            "developer_interrupted",
            "platform_language",

            // APPENDED. The EEG-alignment clock. Every legacy column above keeps its
            // position and its meaning; analyses that never look at EEG are unaffected.
            "lsl_timestamp",

            // ---- Recognition-protocol columns, APPENDED ------------------------------
            // The 35 columns above keep their names AND positions. A script written against
            // the previous format reads these files unchanged; a FreeRecall run leaves every
            // column below empty, exactly as it leaves the chair columns empty outside Area B.
            "protocol_mode",
            "recognition_item_id",
            "recognition_item_class",
            "recognition_phase",
            "recognition_presentation_order",
            "recognition_response",
            "recognition_outcome",
            "recognition_reaction_time_ms",
            "stimulus_onset_time",
            "stimulus_offset_time",
        };

        public void Initialize(SessionContext context)
        {
            Shutdown();  // defensive: never leave a previous stream open

            try
            {
                Directory.CreateDirectory(context.sessionDirectory);
                m_FilePath = Path.Combine(context.sessionDirectory,
                    $"{m_FileNamePrefix}_{context.sessionId}.csv");

                m_Writer = new StreamWriter(m_FilePath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };

                m_Writer.WriteLine(string.Join(",", k_Header));
                m_RowCount = 0;

                Debug.Log($"[IKEA_EEG] CSV file: {m_FilePath}");
            }
            catch (Exception e)
            {
                m_Writer = null;
                Debug.LogError($"[IKEA_EEG] Could not open CSV for writing at '{m_FilePath}': {e.Message}");
            }
        }

        public void OnEvent(ExperimentEvent evt)
        {
            if (m_Writer == null)
                return;

            try
            {
                m_Writer.WriteLine(BuildRow(evt));
                m_RowCount++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] CSV write failed for '{evt.eventType}': {e.Message}");
            }
        }

        public void Shutdown()
        {
            if (m_Writer == null)
                return;

            try
            {
                m_Writer.Flush();
                m_Writer.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] CSV close failed: {e.Message}");
            }
            finally
            {
                m_Writer = null;
            }
        }

        static string BuildRow(ExperimentEvent e)
        {
            var sb = new StringBuilder(256);
            Append(sb, e.timestampAbsolute);
            Append(sb, e.timestampRelative.ToString("F6", CultureInfo.InvariantCulture));
            Append(sb, e.sessionId);
            Append(sb, e.trialId);
            Append(sb, e.experimentState);
            Append(sb, e.room);
            Append(sb, e.eventType);
            Append(sb, e.objectId);
            Append(sb, e.targetColor);
            Append(sb, e.targetSize);
            Append(sb, e.targetShape);
            Append(sb, e.selectedColor);
            Append(sb, e.selectedSize);
            Append(sb, e.selectedShape);
            Append(sb, e.correct);
            Append(sb, e.responseTimeMs);
            Append(sb, e.wordIndex);
            Append(sb, e.expectedWord);
            Append(sb, e.recallPhase);
            Append(sb, e.transcript);
            Append(sb, e.elapsedTrialTime);
            Append(sb, e.notes);
            Append(sb, e.chairTrialIndex);
            Append(sb, e.chairTrialCount);
            Append(sb, e.difficulty);
            Append(sb, e.randomizationSeed);
            Append(sb, e.wordSetId);
            Append(sb, e.clipName);
            Append(sb, e.scheduledAudioTime);
            Append(sb, e.confirmedAudioTime);
            Append(sb, e.experimentSessionId);
            Append(sb, e.runIndex);
            Append(sb, e.developerInterrupted);
            Append(sb, e.platformLanguage);
            Append(sb, e.lslTimestamp);
            Append(sb, e.protocolMode);
            Append(sb, e.recognitionItemId);
            Append(sb, e.recognitionItemClass);
            Append(sb, e.recognitionPhase);
            Append(sb, e.recognitionPresentationOrder);
            Append(sb, e.recognitionResponse);
            Append(sb, e.recognitionOutcome);
            Append(sb, e.recognitionReactionTimeMs);
            Append(sb, e.stimulusOnsetTime);
            Append(sb, e.stimulusOffsetTime, last: true);
            return sb.ToString();
        }

        static void Append(StringBuilder sb, string value, bool last = false)
        {
            sb.Append(Escape(value));
            if (!last)
                sb.Append(',');
        }

        /// <summary>RFC-4180 escaping so free-text notes/transcripts can never break the file.</summary>
        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuotes)
                return value;

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
