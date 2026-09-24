using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using IkeaEeg.Core;
using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// Writes shadow-mode decisions to their OWN file, beside the session's raw EEG.
    ///
    /// A separate file on purpose. The behavioural CSV is the experiment's record and its
    /// columns are relied on by existing analysis; shadow mode is an unvalidated observer and
    /// must not widen, reorder or otherwise disturb that schema. Keeping it apart also means
    /// deleting every shadow output leaves the experiment's data untouched.
    ///
    /// Implements <see cref="IEventSink"/> only to receive the session directory through the
    /// existing lifecycle. It ignores every experimental event: <see cref="OnEvent"/> is
    /// deliberately empty, so no behavioural event can influence a decision.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShadowDecisionSink : MonoBehaviour, IEventSink
    {
        public const string FileName = "shadow_decisions.csv";

        /// <summary>
        /// Session-end diagnostic summary. A SIBLING file, never a column.
        ///
        /// The rejection tally previously existed only in the Unity Console, which means only
        /// in Editor.log — a file the next Editor launch overwrites. The evidence that named
        /// NearIdenticalChannels as the cause of a whole run survived by luck. This persists
        /// the same strings next to the data they describe, and leaves the frozen 15-column
        /// shadow_decisions.csv untouched.
        /// </summary>
        public const string DiagnosticsFileName = "shadow_diagnostics.txt";

        [Tooltip("Rows buffered before touching the disk. Shadow mode must never add a frame " +
                 "spike near a stimulus marker.")]
        [SerializeField] int m_FlushEveryRows = 32;

        readonly List<string> m_Pending = new List<string>();

        string m_FilePath;
        bool m_HeaderWritten;

        public string filePath => m_FilePath;
        public int pendingRowCount => m_Pending.Count;

        /// <summary>
        /// Rows handed to this sink. ZERO after a run with EEG connected means nothing upstream
        /// ever produced a decision — the file will hold a header and nothing else, because the
        /// header is written on Initialize and rows only here.
        /// </summary>
        public long rowsWritten { get; private set; }

        public void Initialize(SessionContext context)
        {
            if (context == null || string.IsNullOrEmpty(context.sessionDirectory))
                return;

            try
            {
                Directory.CreateDirectory(context.sessionDirectory);
                m_FilePath = Path.Combine(context.sessionDirectory, FileName);
                m_HeaderWritten = false;
                m_Pending.Clear();
            }
            catch (IOException e)
            {
                // A failure here must never take the session down: shadow mode is an observer.
                Debug.LogWarning($"[IKEA_EEG] Shadow decision sink could not open its file: {e.Message}");
                m_FilePath = null;
            }
        }

        /// <summary>
        /// Intentionally empty. Shadow mode observes EEG, never behaviour; reading experiment
        /// events here would create exactly the coupling this component is built to avoid.
        /// </summary>
        public void OnEvent(ExperimentEvent evt) { }

        public void Write(ShadowDecision decision)
        {
            rowsWritten++;

            if (string.IsNullOrEmpty(m_FilePath))
                return;

            m_Pending.Add(decision.ToCsvRow());

            if (m_Pending.Count >= Mathf.Max(1, m_FlushEveryRows))
                Flush();
        }

        public void Flush()
        {
            if (string.IsNullOrEmpty(m_FilePath) || m_Pending.Count == 0 && m_HeaderWritten)
                return;

            try
            {
                if (!m_HeaderWritten)
                {
                    File.AppendAllText(m_FilePath, ShadowDecision.CsvHeader + "\n");
                    m_HeaderWritten = true;
                }

                if (m_Pending.Count > 0)
                {
                    File.AppendAllText(m_FilePath, string.Join("\n", m_Pending) + "\n");
                    m_Pending.Clear();
                }
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[IKEA_EEG] Shadow decision sink could not write: {e.Message}");
            }
        }

        public void Shutdown()
        {
            Flush();
            WriteDiagnostics();

            // Stated plainly at session end, so an empty file is never a silent outcome.
            if (rowsWritten == 0)
            {
                Debug.LogWarning("[IKEA_EEG] Shadow decisions: 0 rows written. " +
                                 "shadow_decisions.csv holds only its header. Either no EEG " +
                                 "arrived this session, or nothing upstream published a feature " +
                                 "window — check the pipeline's windowsPublished count.");
                return;
            }

            Debug.Log($"[IKEA_EEG] Shadow decisions: {rowsWritten} row(s) written to " +
                      $"{m_FilePath}.");
        }

        /// <summary>
        /// Writes the session-end diagnostic summary beside the decisions file.
        ///
        /// OBSERVATIONAL. Every value is read from a counter that already existed; nothing is
        /// computed, nothing is judged, and no decision is revisited. Written on EVERY session,
        /// including one that produced zero rows — that is the case where it is worth most.
        ///
        /// Failure here is logged and swallowed: a diagnostic file must never be able to take
        /// a session down.
        /// </summary>
        void WriteDiagnostics()
        {
            if (string.IsNullOrEmpty(m_FilePath))
                return;

            var directory = Path.GetDirectoryName(m_FilePath);

            if (string.IsNullOrEmpty(directory))
                return;

            // The controller is NOT on this GameObject. ExperimentSceneBuilder puts the
            // sink on the EventLogger object and the controller on the EEG object, so a
            // GetComponent here found nothing and every counter the controller owns --
            // windows_observed, decisions_generated and the whole rejection breakdown --
            // was written as "unavailable" in the first real session. Located the same way
            // as the receiver and the pipeline, which were correct from the start.
            var controller = GetComponent<ShadowModeController>();

            if (controller == null)
                controller = FindAnyObjectByType<ShadowModeController>();

            var receiver = FindAnyObjectByType<AuraLslReceiver>();
            var pipeline = FindAnyObjectByType<EegFeaturePipeline>();

            var text = new StringBuilder();

            text.AppendLine("# IKEA_EEG shadow-mode session diagnostics");
            text.AppendLine("# Observational summary of what the shadow observer saw.");
            text.AppendLine("# It approves nothing, and no value here fed any decision.");
            text.AppendLine("# The decisions themselves are in " + FileName + ".");
            text.AppendLine();

            text.AppendLine("written_utc            = " +
                System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture));
            text.AppendLine("controller_version     = " + ShadowModeController.ControllerVersion);
            text.AppendLine();

            text.AppendLine("## Counters");
            text.AppendLine("samples_received       = " +
                (receiver != null ? receiver.samplesReceived.ToString(CultureInfo.InvariantCulture)
                                  : "unavailable (no receiver in scene)"));
            text.AppendLine("windows_published      = " +
                (pipeline != null ? pipeline.windowsPublished.ToString(CultureInfo.InvariantCulture)
                                  : "unavailable (no pipeline in scene)"));
            text.AppendLine("windows_observed       = " +
                (controller != null ? controller.windowsObserved.ToString(CultureInfo.InvariantCulture)
                                    : "unavailable (no controller on this object)"));
            text.AppendLine("decisions_generated    = " +
                (controller != null ? controller.decisionsGenerated.ToString(CultureInfo.InvariantCulture)
                                    : "unavailable (no controller on this object)"));
            text.AppendLine("rows_written           = " +
                rowsWritten.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();

            text.AppendLine("## Grouped rejection breakdown");
            text.AppendLine("# Grouped by diagnostic CAUSE. The raw sample count is deliberately");
            text.AppendLine("# not part of the grouping key: it jitters between otherwise");
            text.AppendLine("# identical windows and would fragment one cause across many rows.");
            text.AppendLine();
            text.AppendLine(controller != null
                ? controller.DescribeRejections()
                : "unavailable (no controller on this object)");

            try
            {
                File.WriteAllText(Path.Combine(directory, DiagnosticsFileName), text.ToString());
            }
            catch (IOException e)
            {
                Debug.LogWarning("[IKEA_EEG] Shadow diagnostics could not be written: " +
                                 e.Message);
            }
        }

        void OnDestroy() => Flush();
    }
}
