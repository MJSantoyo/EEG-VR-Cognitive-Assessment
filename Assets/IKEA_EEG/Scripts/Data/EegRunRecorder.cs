using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

                WriteSessionMetadata(sessionDirectory, experimentSessionId, runSessionId, runIndex);
            }
            else
            {
                Debug.LogError($"[IKEA_EEG] Could not open the raw EEG file: {problem}. The run " +
                               "continues and all behavioural data is still written.");

                m_Recorder = null;
            }
        }

        public const string MetadataFileName = "eeg_session_metadata.txt";

        /// <summary>
        /// Writes a SIBLING metadata file next to raw_eeg.csv describing how this run's EEG was
        /// processed in Unity: stream, effective filters, montage and code version.
        ///
        /// It never touches raw_eeg.csv, whose header and columns stay exactly as RawEegRecorder
        /// writes them, and it never overwrites an existing file. Failure is logged and ignored:
        /// a metadata file must not be able to stop a run.
        /// </summary>
        void WriteSessionMetadata(string sessionDirectory, string experimentSessionId,
            string runSessionId, int runIndex)
        {
            try
            {
                var c = CultureInfo.InvariantCulture;
                var meta = m_Receiver.metadata;
                var pipeline = GetComponent<EegFeaturePipeline>();
                var sb = new StringBuilder();

                sb.AppendLine("# IKEA_EEG EEG session metadata, written when this run's raw_eeg.csv was opened.");
                sb.AppendLine("# raw_eeg.csv holds the samples EXACTLY as received from LSL, BEFORE any Unity filter.");
                sb.AppendLine("# The filters below apply only to the analysis path (features, QC, shadow files).");
                sb.AppendLine("written_utc=" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", c));
                sb.AppendLine("experiment_session_id=" + experimentSessionId);
                sb.AppendLine("run_session_id=" + runSessionId);
                sb.AppendLine("run_index=" + runIndex.ToString(c));
                sb.AppendLine("raw_eeg_file=" + m_FileName);
                sb.AppendLine("raw_eeg_content=as received from LSL; no filter, notch, scaling or re-referencing");
                sb.AppendLine();

                sb.AppendLine("## Stream");
                sb.AppendLine("stream_name=" + meta.name);
                sb.AppendLine("stream_type=" + meta.type);
                sb.AppendLine("stream_source_id=" + meta.sourceId);
                sb.AppendLine("channel_count=" + meta.channelCount.ToString(c));
                sb.AppendLine("nominal_sample_rate_hz=" + meta.nominalSrate.ToString("R", c));
                sb.AppendLine("channel_format=" + meta.channelFormat);
                sb.AppendLine("lsl_time_correction_s=" + m_Receiver.timeCorrection.ToString("R", c));
                sb.AppendLine();

                sb.AppendLine("## Unity preprocessing (EegFeaturePipeline)");
                sb.AppendLine(pipeline != null
                    ? pipeline.DescribeEffectiveConfiguration()
                    : "unavailable (no EegFeaturePipeline on this object)");
                sb.AppendLine();

                sb.AppendLine("## Montage (AuraMontageConfig)");
                var montage = pipeline != null ? pipeline.montage : null;

                if (montage != null)
                {
                    sb.AppendLine(montage.DescribeProvenance());

                    for (var i = 0; i < meta.channelCount; i++)
                        sb.AppendLine($"ch{i + 1}={montage.LabelOfIndex(i)}");

                    foreach (var roi in montage.regions)
                    {
                        if (roi != null)
                            sb.AppendLine($"roi_{roi.roiName}={string.Join(",", roi.labels)}");
                    }
                }
                else
                {
                    sb.AppendLine("unavailable");
                }

                sb.AppendLine();
                sb.AppendLine("## Code version");
                AppendCodeVersion(sb);

                // Never overwrite: a second run in the same folder gets its own numbered file.
                var path = Path.Combine(sessionDirectory, MetadataFileName);

                for (var n = 1; File.Exists(path); n++)
                {
                    path = Path.Combine(sessionDirectory,
                        Path.GetFileNameWithoutExtension(MetadataFileName) + "_" + n.ToString(c) + ".txt");
                }

                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(sb.ToString());
                }

                Debug.Log("[IKEA_EEG] EEG session metadata written: " + path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IKEA_EEG] EEG session metadata could not be written: " + e.Message +
                                 ". The run continues; raw_eeg.csv is unaffected.");
            }
        }

        /// <summary>
        /// Unity version, git commit, and SHA-256 of the source files that shape the EEG path.
        ///
        /// The hashes exist because the commit alone cannot show uncommitted edits, which this
        /// runtime cannot detect: identical hashes mean identical code. The sources (and .git)
        /// are only present when running inside the project, i.e. in the Editor; elsewhere the
        /// lines say "unavailable" rather than guessing.
        /// </summary>
        static void AppendCodeVersion(StringBuilder sb)
        {
            sb.AppendLine("unity_version=" + Application.unityVersion);
            sb.AppendLine("application_version=" + Application.version);

            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            var gitDir = Path.Combine(projectRoot, ".git");

            try
            {
                var head = File.Exists(Path.Combine(gitDir, "HEAD"))
                    ? File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim()
                    : string.Empty;

                if (head.StartsWith("ref: ", StringComparison.Ordinal))
                {
                    var reference = head.Substring(5).Trim();
                    sb.AppendLine("git_branch=" + reference.Replace("refs/heads/", string.Empty));
                    sb.AppendLine("git_commit=" + ResolveGitRef(gitDir, reference));
                }
                else
                {
                    sb.AppendLine("git_branch=" + (head.Length > 0 ? "(detached)" : "unavailable"));
                    sb.AppendLine("git_commit=" + (head.Length > 0 ? head : "unavailable"));
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("git_commit=unavailable (" + e.Message + ")");
            }

            sb.AppendLine("git_uncommitted_changes=not checked at runtime; compare the source hashes below");

            foreach (var relative in new[]
                     {
                         "Assets/IKEA_EEG/Scripts/Data/EegFeaturePipeline.cs",
                         "Assets/IKEA_EEG/Scripts/Data/EegBandpassFilter.cs",
                         "Assets/IKEA_EEG/Scripts/Data/EegChannelQualityRules.cs",
                         "Assets/IKEA_EEG/Scripts/Data/EegSpectralAnalyzer.cs",
                         "Assets/IKEA_EEG/Scripts/Data/AuraLslReceiver.cs",
                         "Assets/IKEA_EEG/Scripts/Data/RawEegRingBuffer.cs",
                         "Assets/IKEA_EEG/Scripts/Data/RawEegRecorder.cs",
                         "Assets/IKEA_EEG/Scripts/Data/EegRunRecorder.cs",
                         "Assets/IKEA_EEG/Scripts/Data/AuraMontageConfig.cs",
                         "Assets/IKEA_EEG/Scenes/IKEA_EEG_Bootstrap.unity",
                     })
            {
                var full = Path.Combine(projectRoot, relative);
                sb.AppendLine("sha256 " + relative + "=" + (File.Exists(full) ? Sha256Of(full) : "unavailable"));
            }
        }

        static string ResolveGitRef(string gitDir, string reference)
        {
            var loose = Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(loose))
                return File.ReadAllText(loose).Trim();

            var packed = Path.Combine(gitDir, "packed-refs");

            if (File.Exists(packed))
            {
                foreach (var line in File.ReadAllLines(packed))
                {
                    if (line.EndsWith(" " + reference, StringComparison.Ordinal))
                        return line.Substring(0, line.IndexOf(' '));
                }
            }

            return "unavailable";
        }

        static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var hash = sha.ComputeHash(stream);
                var text = new StringBuilder(hash.Length * 2);

                foreach (var b in hash)
                    text.Append(b.ToString("x2", CultureInfo.InvariantCulture));

                return text.ToString();
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
