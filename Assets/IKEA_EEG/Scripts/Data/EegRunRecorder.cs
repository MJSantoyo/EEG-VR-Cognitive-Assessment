using UnityEngine;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Ties the raw EEG path to the RUN lifecycle: connect once, buffer continuously, and write
    /// one EEG file per run alongside that run's CSV and WAV.
    ///
    /// WHY A SEPARATE COMPONENT: the ExperimentManager owns the protocol, and EEG acquisition is
    /// not part of the protocol. Keeping the wiring here means the manager gains two calls —
    /// "a run started", "a run ended" — and knows nothing about streams, buffers or files. If
    /// AURA is absent the manager's calls are no-ops and the behavioural experiment runs exactly
    /// as it always has.
    ///
    /// IT NEVER BLOCKS THE EXPERIMENT. A missing or silent EEG stream is reported and then
    /// ignored: a participant session must not fail because an amplifier is off.
    ///
    /// NO PROCESSING. Samples are buffered and written exactly as received.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AuraLslReceiver))]
    public class EegRunRecorder : MonoBehaviour
    {
        [Tooltip("Try to connect to AURA when the scene starts. Off leaves the whole EEG path " +
                 "dormant and the behavioural experiment completely unaffected.")]
        [SerializeField] bool m_ConnectOnStart = true;

        [Tooltip("Write this run's raw EEG into the run's own session folder.")]
        [SerializeField] bool m_RecordToDisk = true;

        [Tooltip("File name inside the run folder.")]
        [SerializeField] string m_FileName = "raw_eeg.csv";

        AuraLslReceiver m_Receiver;
        RawEegRecorder m_Recorder;

        /// <summary>The shared receiver. There is exactly one; nothing else creates another.</summary>
        public AuraLslReceiver receiver => m_Receiver;

        /// <summary>True while EEG is being written for the current run.</summary>
        public bool isRecording => m_Recorder != null && m_Recorder.isRecording;

        /// <summary>Where the current run's EEG is going, or empty.</summary>
        public string filePath => m_Recorder != null ? m_Recorder.filePath : string.Empty;

        void Awake()
        {
            m_Receiver = GetComponent<AuraLslReceiver>();
        }

        void Start()
        {
            if (!m_ConnectOnStart)
                return;

            // Failure here is normal and harmless: no amplifier, no EEG, same experiment.
            if (m_Receiver.Connect(out var problem))
            {
                Debug.Log($"[IKEA_EEG] EEG connected: {m_Receiver.metadata}");
            }
            else
            {
                Debug.LogWarning($"[IKEA_EEG] No EEG this session ({problem}). The behavioural " +
                                 "experiment is unaffected and every other output is unchanged.");
            }
        }

        /// <summary>
        /// A run has begun: clear the history and open this run's EEG file.
        ///
        /// The buffer is cleared so the run's continuity statistics describe THIS run, and so a
        /// window cut early in the run cannot reach back into the previous one.
        /// </summary>
        public void BeginRun(string sessionDirectory, string experimentSessionId,
            string runSessionId, int runIndex, string platformLanguage)
        {
            EndRun();

            if (m_Receiver == null || !m_Receiver.isConnected)
                return;

            m_Receiver.buffer?.Clear();

            if (!m_RecordToDisk)
                return;

            m_Recorder = new RawEegRecorder();

            if (m_Recorder.Start(sessionDirectory, m_FileName, m_Receiver.metadata,
                    experimentSessionId, runSessionId, runIndex, platformLanguage,
                    out var problem))
            {
                // The receiver feeds it from here on; nothing else has to be told.
                m_Receiver.recorder = m_Recorder;

                Debug.Log($"[IKEA_EEG] Raw EEG recording to {m_Recorder.filePath} " +
                          $"({m_Receiver.metadata.channelCount} channels @ " +
                          $"{m_Receiver.metadata.nominalSrate:F0} Hz, values written exactly as " +
                          "received — no filtering or scaling).");
            }
            else
            {
                Debug.LogError($"[IKEA_EEG] Could not open the raw EEG file: {problem}. The run " +
                               "continues and all behavioural data is still written.");

                m_Recorder = null;
            }
        }

        /// <summary>The run has ended: drain the backlog and close the file.</summary>
        public void EndRun()
        {
            if (m_Recorder == null)
                return;

            if (m_Receiver != null)
                m_Receiver.recorder = null;

            m_Recorder.Stop();
            m_Recorder.Dispose();
            m_Recorder = null;
        }

        void OnDisable()
        {
            EndRun();
        }

        /// <summary>Builder wiring.</summary>
        public void Configure(bool connectOnStart, bool recordToDisk, string fileName)
        {
            m_ConnectOnStart = connectOnStart;
            m_RecordToDisk = recordToDisk;
            m_FileName = fileName;
        }
    }
}
