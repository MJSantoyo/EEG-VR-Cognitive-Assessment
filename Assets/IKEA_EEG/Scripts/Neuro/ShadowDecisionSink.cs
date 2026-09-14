using System.Collections.Generic;
using System.IO;
using UnityEngine;
using IkeaEeg.Core;

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

        [Tooltip("Rows buffered before touching the disk. Shadow mode must never add a frame " +
                 "spike near a stimulus marker.")]
        [SerializeField] int m_FlushEveryRows = 32;

        readonly List<string> m_Pending = new List<string>();

        string m_FilePath;
        bool m_HeaderWritten;

        public string filePath => m_FilePath;
        public int pendingRowCount => m_Pending.Count;

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

        public void Shutdown() => Flush();

        void OnDestroy() => Flush();
    }
}
