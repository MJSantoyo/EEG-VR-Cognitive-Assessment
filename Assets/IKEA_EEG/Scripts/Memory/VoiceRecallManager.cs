using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using IkeaEeg.Core;

namespace IkeaEeg.Memory
{
    /// <summary>How the capture device is chosen.</summary>
    public enum MicrophoneSelectionMode
    {
        /// <summary>
        /// Look for a headset microphone by keyword (Oculus / Quest / Meta / Rift / Link).
        /// If none is found, fall back to the system default AND log a warning — the fallback
        /// is always visible in the Console and in the CSV, never silent.
        /// </summary>
        PreferHeadset,

        /// <summary>
        /// Use the device named in Preferred Device. If it is not present, behaviour depends
        /// on Fail If Preferred Device Missing: either refuse to record, or fall back with a
        /// warning. Use this once you know which device you want.
        /// </summary>
        ExplicitDevice,

        /// <summary>
        /// Use whatever Windows reports first. This is what produced the Intel array in the
        /// first test run; kept only so the old behaviour remains reproducible.
        /// </summary>
        SystemDefault,
    }

    /// <summary>Metadata describing one recall recording.</summary>
    public class RecordingInfo
    {
        public string recallPhase = string.Empty;       // IMMEDIATE / DELAYED
        public string sessionId = string.Empty;
        public string trialId = string.Empty;
        public string wavPath = string.Empty;
        public bool audioCaptured;
        public string failureReason = string.Empty;

        /// <summary>Capture device used. Recorded per recording, not just per session.</summary>
        public string deviceName = string.Empty;

        /// <summary>True when this recording used a fallback device rather than the intended one.</summary>
        public bool deviceIsFallback;

        /// <summary>Peak / RMS / dBFS statistics of the captured audio.</summary>
        public WavUtility.SignalStats signal;

        /// <summary>
        /// Why the recording ended: one of <see cref="RecallStopReasons"/>. Set for every
        /// recording that starts, so no recording's ending has to be inferred from its length.
        /// </summary>
        public string stopReason = string.Empty;

        /// <summary>Wall-clock length of the recording, seconds. Same interval as durationMs.</summary>
        public double actualDurationSeconds;

        /// <summary>True when sustained speech was detected during the recording.</summary>
        public bool speechDetected;

        /// <summary>Session-clock time at which speech was first confirmed. 0 if never.</summary>
        public double speechStartRelativeSeconds;

        /// <summary>Continuous silence at the point the recording stopped, seconds.</summary>
        public double trailingSilenceSeconds;

        /// <summary>
        /// True when the capture ran but contains no usable signal. The WAV is still written
        /// for debugging; this must NOT be reported as a successful recording.
        /// </summary>
        public bool signalSilent;

        public double startRelativeSeconds;
        public double endRelativeSeconds;
        public string startAbsolute = string.Empty;
        public string endAbsolute = string.Empty;
        public double durationMs;
        public int sampleRate;
        public int sampleCount;

        public RecallScore score;
        public TranscriptionResult transcription;
    }

    /// <summary>
    /// Captures the participant's spoken recall.
    ///
    /// What it guarantees even with NO speech-to-text installed:
    ///   * microphone audio is recorded to a .wav under the session folder,
    ///   * recording onset and offset are timestamped against the session clock,
    ///   * every recording is tagged with session_id, trial_id and recall phase,
    ///   * a missing/blocked microphone degrades to a logged warning — the trial continues.
    ///
    /// Transcription is delegated to an <see cref="ISpeechTranscriptionProvider"/>. The
    /// default provider does nothing, and the flow is identical either way.
    /// </summary>
    [DisallowMultipleComponent]
    public class VoiceRecallManager : MonoBehaviour
    {
        [Header("Recording")]
        [Tooltip("Capture sample rate. 16 kHz mono is standard for speech recognition.")]
        [SerializeField] int m_SampleRate = 16000;

        [Tooltip("Hard cap on a single recording. The buffer is allocated at this length.")]
        [SerializeField] int m_MaxRecordingSeconds = 60;

        [Header("Microphone device selection")]
        [Tooltip("How the capture device is chosen. Never left to chance — see " +
                 "MicrophoneSelectionMode for what each option does.")]
        [SerializeField] MicrophoneSelectionMode m_SelectionMode = MicrophoneSelectionMode.PreferHeadset;

        [Tooltip("Used when Selection Mode is ExplicitDevice. Must match a name from the " +
                 "device list printed to the Console at start-up. A partial, case-insensitive " +
                 "match is accepted.")]
        [SerializeField] string m_PreferredDevice = string.Empty;

        [Tooltip("Substrings that identify a headset microphone. Matched case-insensitively " +
                 "against the device name. Used by PreferHeadset mode.")]
        [SerializeField]
        List<string> m_HeadsetKeywords = new List<string>
        {
            "oculus", "quest", "meta", "rift", "headset", "vr ", "link",
        };

        [Tooltip("When ON, a missing preferred device is a hard failure and nothing is " +
                 "recorded. When OFF, the system falls back to the default device and logs a " +
                 "loud warning — it never switches silently.")]
        [SerializeField] bool m_FailIfPreferredDeviceMissing;

        [Header("Signal quality")]
        [Tooltip("Peak amplitude (0-1) below which a recording is treated as silent. " +
                 "0.01 is about -40 dBFS: quieter than any real speech, louder than a " +
                 "digital-silence buffer.")]
        [Range(0.0005f, 0.2f)][SerializeField] float m_SilencePeakThreshold = 0.01f;

        [Header("Adaptive stop (values are pushed in from ExperimentConfig at run time)")]
        [Tooltip("Stop a recall recording once the participant has spoken and then stayed " +
                 "silent for this long. PROTOTYPE DEFAULT — not a validated value.")]
        [SerializeField] float m_RecallSilenceStopSeconds = 4f;

        [Tooltip("Window peak (0-1) that counts as speech.")]
        [SerializeField] float m_SpeechStartThreshold = 0.05f;

        [Tooltip("Window peak (0-1) below which a window counts as silence. Must be lower than " +
                 "the speech threshold; the gap is hysteresis.")]
        [SerializeField] float m_SilenceThreshold = 0.015f;

        [Tooltip("Voiced audio that must accumulate before the silence detector is armed.")]
        [SerializeField] float m_MinimumSpeechDuration = 0.35f;

        [Tooltip("Length of one analysis window. Shorter reacts faster but is noisier; 0.1 s " +
                 "is about the shortest window in which speech energy is meaningful.")]
        [Range(0.05f, 0.5f)][SerializeField] float m_AnalysisWindowSeconds = 0.1f;

        [Tooltip("Run a short capture at session start to prove the microphone delivers signal.")]
        [SerializeField] bool m_RunStartupMicrophoneTest = true;

        [Tooltip("Duration of that startup capture.")]
        [Range(0.5f, 5f)][SerializeField] float m_StartupTestSeconds = 1.5f;

        [Header("Transcription")]
        [Tooltip("Component implementing ISpeechTranscriptionProvider. " +
                 "Defaults to the NullTranscriptionProvider on this GameObject.")]
        [SerializeField] MonoBehaviour m_TranscriptionProviderBehaviour;

        ISpeechTranscriptionProvider m_Provider;

        string m_ActiveDevice = string.Empty;
        string[] m_Devices = Array.Empty<string>();
        string m_SelectionReason = string.Empty;
        bool m_SelectionIsFallback;
        int m_EffectiveSampleRate = 16000;

        bool m_Recording;
        AudioClip m_Clip;
        RecordingInfo m_Current;

        // ---- Adaptive stop state ---------------------------------------------------------
        // The DECISION lives in RecallSilenceDetector (pure, unit-tested). This class only
        // feeds it real microphone windows.
        readonly RecallSilenceDetector m_Detector = new RecallSilenceDetector();
        float[] m_AnalysisWindow = Array.Empty<float>();
        int m_LastAnalyzedSample;

        readonly List<RecordingInfo> m_Recordings = new List<RecordingInfo>();

        /// <summary>Raised once a recording has been stopped, written and (optionally) scored.</summary>
        public event Action<RecordingInfo> recordingCompleted;

        public bool isRecording => m_Recording;
        public IReadOnlyList<RecordingInfo> recordings => m_Recordings;
        public bool microphoneAvailable { get; private set; }
        public string providerName => m_Provider != null ? m_Provider.providerName : "none";

        /// <summary>The capture device actually in use. Empty when none was selected.</summary>
        public string activeDevice => m_ActiveDevice;

        /// <summary>Every capture device the OS reported at start-up.</summary>
        public IReadOnlyList<string> availableDevices => m_Devices;

        /// <summary>Why <see cref="activeDevice"/> was chosen. Written to the CSV.</summary>
        public string selectionReason => m_SelectionReason;

        /// <summary>
        /// True when the intended device was unavailable and a substitute is in use.
        /// A fallback is always logged as a warning — it never happens silently.
        /// </summary>
        public bool selectionIsFallback => m_SelectionIsFallback;

        /// <summary>Sample rate actually used, after clamping to the device's capabilities.</summary>
        public int effectiveSampleRate => m_EffectiveSampleRate;

        /// <summary>Words the participant is expected to recall. Set by the ExperimentManager.</summary>
        public IReadOnlyList<string> expectedWords { get; set; } = Array.Empty<string>();

        // ---------------------------------------------------------------------------------
        // Adaptive stop — public surface
        // ---------------------------------------------------------------------------------

        /// <summary>The decision logic. Exposed so the self test can drive it directly.</summary>
        public RecallSilenceDetector detector => m_Detector;

        /// <summary>True once sustained speech has been detected in the current recording.</summary>
        public bool speechDetected => m_Detector.speechDetected;

        /// <summary>Current run of continuous silence, seconds. Meaningful only after speech.</summary>
        public float silenceSecondsSinceSpeech =>
            m_Detector.speechDetected ? m_Detector.silenceRunSeconds : 0f;

        /// <summary>
        /// True when the detector has decided the recording should end. The ExperimentManager
        /// polls this; the detector never stops the recording itself, so the protocol keeps
        /// sole authority over when a phase ends.
        /// </summary>
        public bool autoStopRequested => m_Detector.stopRequested;

        /// <summary>The reason to report if <see cref="autoStopRequested"/> is true.</summary>
        public string autoStopReason => m_Detector.stopReason;

        public float recallSilenceStopSeconds => m_RecallSilenceStopSeconds;
        public float speechStartThreshold => m_SpeechStartThreshold;
        public float silenceThreshold => m_SilenceThreshold;
        public float minimumSpeechDuration => m_MinimumSpeechDuration;
        public float analysisWindowSeconds => m_AnalysisWindowSeconds;

        /// <summary>
        /// Pushes the protocol's thresholds in from the ExperimentConfig, so the values that
        /// govern a session live in the config asset (and therefore in the session record)
        /// rather than only on this component.
        /// </summary>
        public void ConfigureAdaptiveStop(float silenceStopSeconds, float speechStart,
            float silence, float minimumSpeech)
        {
            m_RecallSilenceStopSeconds = silenceStopSeconds;
            m_SpeechStartThreshold = speechStart;
            m_SilenceThreshold = silence;
            m_MinimumSpeechDuration = minimumSpeech;

            if (!m_Detector.Configure(silenceStopSeconds, speechStart, silence, minimumSpeech,
                    m_AnalysisWindowSeconds))
            {
                // Crossed thresholds: a window could classify as both speech and silence and
                // behaviour would depend on evaluation order. The detector fell back to its
                // own defaults; mirror them here so the logged values are the real ones.
                Debug.LogError($"[IKEA_EEG] speechStartThreshold ({speechStart}) must exceed " +
                               $"silenceThreshold ({silence}). Using the detector defaults " +
                               $"({m_Detector.speechStartThreshold} / " +
                               $"{m_Detector.silenceThreshold}) for this session.");

                m_SpeechStartThreshold = m_Detector.speechStartThreshold;
                m_SilenceThreshold = m_Detector.silenceThreshold;
            }
        }

        /// <summary>
        /// Analyses whatever the microphone has captured since the last call and updates the
        /// speech/silence state. Called every frame while recording.
        ///
        /// THE RULE THIS IMPLEMENTS, stated once:
        ///   * silence BEFORE speech never ends a recording — a participant who takes fifteen
        ///     seconds to start talking is not interrupted;
        ///   * speech is only "started" after minimumSpeechDuration of voiced audio has
        ///     accumulated, so a cough or a controller knock cannot arm the detector;
        ///   * after that, a CONTINUOUS run of silence lasting recallSilenceStopSeconds ends
        ///     the recording. Any window at or above the silence threshold resets that run to
        ///     zero, which is what protects normal pauses between recalled words.
        /// </summary>
        void Update()
        {
            if (!m_Recording || m_Current == null || m_Clip == null || !microphoneAvailable)
                return;

            if (m_Detector.stopRequested)
                return;

            var windowSamples = m_AnalysisWindow.Length;
            if (windowSamples == 0)
                return;

            var position = Microphone.GetPosition(m_ActiveDevice);
            if (position < 0)
                return;

            // Consume every whole window that has become available since the last frame. The
            // loop matters: at 0.1 s windows and a frame hitch, more than one can be ready.
            var guard = 0;
            while (position - m_LastAnalyzedSample >= windowSamples && guard++ < 64)
            {
                var offset = m_LastAnalyzedSample;
                m_LastAnalyzedSample += windowSamples;

                if (offset + windowSamples > m_Clip.samples)
                    break;      // buffer exhausted; the max-duration fallback takes over

                if (!m_Clip.GetData(m_AnalysisWindow, offset))
                    break;

                AnalyzeWindow(m_AnalysisWindow, windowSamples);
            }
        }

        void AnalyzeWindow(float[] window, int count)
        {
            var stats = WavUtility.AnalyzeSignal(window, count);

            // The decision belongs to the detector; this class only supplies measurements.
            // RMS is passed for the record only — no decision uses it.
            if (!m_Detector.PushWindow(stats.peak, stats.rms))
                return;

            // Speech was confirmed on exactly this window.
            var logger = EventLogger.Instance;
            if (m_Current != null)
            {
                m_Current.speechDetected = true;
                m_Current.speechStartRelativeSeconds =
                    logger != null ? logger.clock.RelativeSeconds() : 0d;
            }

            LogSpeechDetected(stats);
        }

        void LogSpeechDetected(WavUtility.SignalStats stats)
        {
            var logger = EventLogger.Instance;
            if (logger == null || m_Current == null)
                return;

            logger.Log(EventTypes.RecallSpeechDetected, e =>
            {
                e.recallPhase = m_Current.recallPhase;
                e.notes = string.Format(CultureInfo.InvariantCulture,
                    "voiced_seconds={0:F2}; min_speech_s={1:F2}; window_peak={2:F4}; " +
                    "speech_threshold={3:F4}; seconds_into_recording={4:F3}",
                    m_Detector.voicedSeconds, m_Detector.minimumSpeechDuration, stats.peak,
                    m_Detector.speechStartThreshold,
                    logger.clock.RelativeSeconds() - m_Current.startRelativeSeconds);
            });
        }

        /// <summary>Clears the detector for a new recording.</summary>
        void ResetAdaptiveStopState()
        {
            m_LastAnalyzedSample = 0;

            // Re-apply the thresholds so the detector's window length matches the buffer we are
            // about to allocate, then clear its state.
            m_Detector.Configure(m_RecallSilenceStopSeconds, m_SpeechStartThreshold,
                m_SilenceThreshold, m_MinimumSpeechDuration, m_AnalysisWindowSeconds);
            m_Detector.Reset();

            var windowSamples = Mathf.Max(64,
                Mathf.RoundToInt(m_AnalysisWindowSeconds * m_EffectiveSampleRate));

            if (m_AnalysisWindow.Length != windowSamples)
                m_AnalysisWindow = new float[windowSamples];
        }

        /// <summary>Live detector state, for the Console and the stop event's notes.</summary>
        public string DescribeAdaptiveStopState() => m_Detector.Describe();

        void Awake()
        {
            EnsureProvider();
            DetectMicrophone();
        }

        /// <summary>
        /// Resolves the transcription provider, falling back to the null one.
        ///
        /// Called from Awake at run time and again defensively before it is used, because Awake
        /// does not run in the Editor outside Play Mode — and a recording that reached the
        /// transcription step with no provider would throw at the exact moment a participant's
        /// audio was being finalised.
        /// </summary>
        void EnsureProvider()
        {
            if (m_Provider != null)
                return;

            m_Provider = m_TranscriptionProviderBehaviour as ISpeechTranscriptionProvider;

            if (m_Provider == null)
                m_Provider = GetComponent<ISpeechTranscriptionProvider>();

            if (m_Provider == null)
            {
                m_Provider = gameObject.AddComponent<NullTranscriptionProvider>();
                Debug.Log("[IKEA_EEG] No transcription provider assigned; using " +
                          "NullTranscriptionProvider (audio is still recorded and timestamped).");
            }
        }

        void OnDisable()
        {
            if (m_Recording)
                AbortRecording("component disabled");
        }

        /// <summary>
        /// Enumerates the capture devices, chooses one according to
        /// <see cref="MicrophoneSelectionMode"/>, and reports exactly what it picked and why.
        ///
        /// The point of all this: over Quest Link, Windows exposes BOTH the PC's built-in
        /// microphone array and the headset microphone. Taking Microphone.devices[0] gets
        /// whichever Windows happens to list first — in the first test run that was the
        /// laptop's Intel array, not the headset. Which microphone recorded a participant is
        /// an experimental variable, so it is chosen deliberately and written to the CSV.
        /// </summary>
        public void DetectMicrophone()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // On a standalone Quest build the mic needs a runtime permission. In the Editor
            // over Quest Link the PC-side device list is used and no permission prompt occurs.
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    UnityEngine.Android.Permission.Microphone);
            }
#endif
            m_Devices = Microphone.devices ?? Array.Empty<string>();
            microphoneAvailable = m_Devices.Length > 0;
            m_ActiveDevice = string.Empty;
            m_SelectionReason = string.Empty;
            m_SelectionIsFallback = false;

            if (!microphoneAvailable)
            {
                m_SelectionReason = "no capture device present";
                Debug.LogWarning("[IKEA_EEG] No microphone device detected. Recall audio will " +
                                 "NOT be captured, but the experiment will run normally and all " +
                                 "recall timestamps are still logged.");
                return;
            }

            // --- Always print the full device list, with capabilities ----------------------
            var listing = new StringBuilder();
            listing.AppendLine($"[IKEA_EEG] {m_Devices.Length} microphone device(s) available:");
            for (var i = 0; i < m_Devices.Length; i++)
            {
                Microphone.GetDeviceCaps(m_Devices[i], out var minFreq, out var maxFreq);
                var range = minFreq == 0 && maxFreq == 0
                    ? "any sample rate"
                    : $"{minFreq}-{maxFreq} Hz";
                listing.AppendLine($"    [{i}] \"{m_Devices[i]}\"  ({range})" +
                                   (LooksLikeHeadset(m_Devices[i]) ? "   <-- looks like a headset mic" : string.Empty));
            }

            Debug.Log(listing.ToString().TrimEnd());

            // --- Choose --------------------------------------------------------------------
            switch (m_SelectionMode)
            {
                case MicrophoneSelectionMode.ExplicitDevice:
                    ResolveExplicitDevice();
                    break;

                case MicrophoneSelectionMode.PreferHeadset:
                    ResolveHeadsetDevice();
                    break;

                default:
                    m_ActiveDevice = m_Devices[0];
                    m_SelectionReason = "SystemDefault mode: first device reported by the OS";
                    break;
            }

            if (string.IsNullOrEmpty(m_ActiveDevice))
            {
                microphoneAvailable = false;
                Debug.LogError($"[IKEA_EEG] MICROPHONE NOT SELECTED — {m_SelectionReason}. " +
                               "Recall audio will NOT be captured. Set Selection Mode or " +
                               "Preferred Device on the VoiceRecallManager component.");
                return;
            }

            // --- Sample rate must be one the device actually supports -----------------------
            m_EffectiveSampleRate = m_SampleRate;
            Microphone.GetDeviceCaps(m_ActiveDevice, out var deviceMin, out var deviceMax);
            if (!(deviceMin == 0 && deviceMax == 0))
            {
                var clamped = Mathf.Clamp(m_SampleRate, deviceMin, deviceMax);
                if (clamped != m_SampleRate)
                {
                    Debug.LogWarning($"[IKEA_EEG] '{m_ActiveDevice}' does not support " +
                                     $"{m_SampleRate} Hz (supports {deviceMin}-{deviceMax} Hz). " +
                                     $"Recording at {clamped} Hz instead.");
                    m_EffectiveSampleRate = clamped;
                }
            }

            var severity = m_SelectionIsFallback ? "FALLBACK" : "selected";
            var message = $"[IKEA_EEG] Microphone {severity}: \"{m_ActiveDevice}\" " +
                          $"@ {m_EffectiveSampleRate} Hz mono. Reason: {m_SelectionReason}";

            if (m_SelectionIsFallback)
                Debug.LogWarning(message);
            else
                Debug.Log(message);
        }

        void ResolveExplicitDevice()
        {
            if (string.IsNullOrWhiteSpace(m_PreferredDevice))
            {
                m_SelectionReason = "ExplicitDevice mode but Preferred Device is empty";
                if (!m_FailIfPreferredDeviceMissing)
                    FallBackToDefault(m_SelectionReason);
                return;
            }

            var match = FindDevice(m_PreferredDevice);
            if (match != null)
            {
                m_ActiveDevice = match;
                m_SelectionReason = $"ExplicitDevice mode: matched \"{m_PreferredDevice}\"";
                return;
            }

            var reason = $"preferred device \"{m_PreferredDevice}\" is not connected";
            m_SelectionReason = reason;

            if (m_FailIfPreferredDeviceMissing)
                return;    // leaves m_ActiveDevice empty -> hard failure, logged by the caller

            FallBackToDefault(reason);
        }

        void ResolveHeadsetDevice()
        {
            foreach (var device in m_Devices)
            {
                if (!LooksLikeHeadset(device))
                    continue;

                m_ActiveDevice = device;
                m_SelectionReason = "PreferHeadset mode: device name matched a headset keyword";
                return;
            }

            FallBackToDefault("PreferHeadset mode found no device whose name matches a headset " +
                              $"keyword ({string.Join(", ", m_HeadsetKeywords)})");
        }

        void FallBackToDefault(string reason)
        {
            m_ActiveDevice = m_Devices[0];
            m_SelectionIsFallback = true;
            m_SelectionReason = $"{reason} -> fell back to the first device reported by the OS";

            Debug.LogWarning($"[IKEA_EEG] MICROPHONE FALLBACK: {reason}.\n" +
                             $"[IKEA_EEG] Now using \"{m_ActiveDevice}\", which may NOT be the " +
                             "headset microphone. If that is wrong, set Selection Mode to " +
                             "ExplicitDevice and copy the intended name from the device list " +
                             "printed above.");
        }

        string FindDevice(string query)
        {
            foreach (var device in m_Devices)
            {
                if (string.Equals(device, query, StringComparison.OrdinalIgnoreCase))
                    return device;
            }

            // Partial match, so the researcher can type "Oculus" instead of the full
            // OS-supplied name (which on this machine is partly in Japanese).
            foreach (var device in m_Devices)
            {
                if (device.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return device;
            }

            return null;
        }

        bool LooksLikeHeadset(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return false;

            foreach (var keyword in m_HeadsetKeywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) &&
                    deviceName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Absolute folder the recall WAV files are written to.</summary>
        public string RecordingFolder()
        {
            var logger = EventLogger.Instance;
            var root = logger != null && !string.IsNullOrEmpty(logger.sessionDirectory)
                ? logger.sessionDirectory
                : Application.persistentDataPath;

            return Path.Combine(root, "audio");
        }

        /// <summary>Writes the device decision to the CSV so it is part of the session record.</summary>
        public void LogDeviceSelection()
        {
            var logger = EventLogger.Instance;
            if (logger == null)
                return;

            logger.Log(EventTypes.MicrophoneDeviceSelected, e =>
            {
                e.objectId = m_ActiveDevice;
                e.correct = m_SelectionIsFallback ? "FALSE" : "TRUE";   // TRUE = intended device
                e.notes = $"mode={m_SelectionMode}; fallback={(m_SelectionIsFallback ? "TRUE" : "FALSE")}; " +
                          $"sample_rate={m_EffectiveSampleRate}; reason={m_SelectionReason}; " +
                          $"available=[{string.Join(" | ", m_Devices)}]";
            });
        }

        // ---------------------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Starts recording for the given recall phase.
        /// </summary>
        /// <param name="recallPhase">
        /// <see cref="RecallPhases.Immediate"/> or <see cref="RecallPhases.Delayed"/>.
        /// </param>
        public void StartRecording(string recallPhase)
        {
            if (m_Recording)
            {
                Debug.LogWarning("[IKEA_EEG] StartRecording called while already recording; " +
                                 "stopping the previous recording first.");
                StopRecording();
            }

            var logger = EventLogger.Instance;

            m_Current = new RecordingInfo
            {
                recallPhase = recallPhase,
                sessionId = logger != null ? logger.sessionId : string.Empty,
                trialId = logger != null ? logger.trialId : string.Empty,
                sampleRate = m_EffectiveSampleRate,
                deviceName = m_ActiveDevice,
                deviceIsFallback = m_SelectionIsFallback,
                startRelativeSeconds = logger != null ? logger.clock.RelativeSeconds() : 0d,
                startAbsolute = logger != null ? logger.clock.AbsoluteNowString() : string.Empty,
            };

            ResetAdaptiveStopState();

            if (!microphoneAvailable)
            {
                m_Current.audioCaptured = false;
                m_Current.failureReason = "no microphone device";
                m_Recording = true;   // still track the interval so the timestamps are logged
                return;
            }

            try
            {
                m_Clip = Microphone.Start(m_ActiveDevice, false, m_MaxRecordingSeconds,
                    m_EffectiveSampleRate);
                m_Recording = true;
            }
            catch (Exception e)
            {
                m_Clip = null;
                m_Recording = true;
                m_Current.audioCaptured = false;
                m_Current.failureReason = $"Microphone.Start failed: {e.Message}";
                Debug.LogWarning($"[IKEA_EEG] {m_Current.failureReason}");
            }
        }

        /// <summary>
        /// Stops recording, writes the .wav, timestamps the offset and hands the clip to the
        /// transcription provider. Returns the metadata (also raised via
        /// <see cref="recordingCompleted"/>).
        /// </summary>
        /// <param name="stopReason">
        /// One of <see cref="RecallStopReasons"/>. Defaults to MANUAL — the detector and the
        /// max-duration fallback pass their own reason, so a recording never ends with an
        /// unexplained reason.
        /// </param>
        public RecordingInfo StopRecording(string stopReason = RecallStopReasons.Manual)
        {
            if (!m_Recording || m_Current == null)
                return null;

            var logger = EventLogger.Instance;
            var info = m_Current;

            info.endRelativeSeconds = logger != null ? logger.clock.RelativeSeconds() : 0d;
            info.endAbsolute = logger != null ? logger.clock.AbsoluteNowString() : string.Empty;
            info.durationMs = (info.endRelativeSeconds - info.startRelativeSeconds) * 1000d;
            info.actualDurationSeconds = info.durationMs / 1000d;
            info.stopReason = stopReason;
            info.speechDetected = m_Detector.speechDetected;
            info.trailingSilenceSeconds = m_Detector.speechDetected
                ? m_Detector.silenceRunSeconds
                : 0d;

            var detectorState = DescribeAdaptiveStopState();

            m_Recording = false;
            m_Current = null;

            AudioClip capturedClip = null;

            if (microphoneAvailable && m_Clip != null)
            {
                var samplePosition = Microphone.GetPosition(m_ActiveDevice);
                Microphone.End(m_ActiveDevice);

                capturedClip = m_Clip;
                m_Clip = null;

                info.sampleCount = Mathf.Max(0, samplePosition);
                info.wavPath = BuildWavPath(info);

                var written = info.sampleCount > 0 &&
                              WavUtility.Save(info.wavPath, capturedClip, info.sampleCount);

                // Measure what was actually captured. A full buffer of zeros is a FAILED
                // recording even though every previous check would have passed it.
                if (info.sampleCount > 0)
                {
                    var channels = Mathf.Max(1, capturedClip.channels);
                    var raw = new float[capturedClip.samples * channels];
                    capturedClip.GetData(raw, 0);
                    info.signal = WavUtility.AnalyzeSignal(raw, info.sampleCount * channels);
                    info.signalSilent = info.signal.IsSilent(m_SilencePeakThreshold);
                }
                else
                {
                    info.signalSilent = true;
                }

                // audioCaptured now means "we have usable audio", not merely "bytes exist".
                info.audioCaptured = written && !info.signalSilent;

                if (!written)
                {
                    info.failureReason = info.sampleCount == 0
                        ? "microphone returned zero samples"
                        : "wav write failed";
                    info.wavPath = string.Empty;
                }
                else if (info.signalSilent)
                {
                    info.failureReason = $"recording is silent " +
                                         $"(peak {info.signal.peak:F5} / {info.signal.peakDbfs:F1} dBFS, " +
                                         $"below threshold {m_SilencePeakThreshold:F5})";
                    LogSignalSilent(info);
                }
            }

            // The stop event is logged BEFORE the save/scoring events so the CSV reads in the
            // order things happened: the recording stopped, then it was written, then scored.
            LogRecordingStop(info, detectorState);
            LogRecordingSaved(info);

            // Transcription (a no-op with the default provider) then scoring.
            EnsureProvider();
            m_Provider.Transcribe(capturedClip, info.wavPath, result =>
            {
                info.transcription = result;

                var transcribedWords = result != null && result.isAvailable
                    ? result.words
                    : null;

                // The scoring source is recorded with the score: a number produced by a future
                // local transcriber and one typed in by a human must never be indistinguishable
                // in the data.
                info.score = RecallScorer.Score(new List<string>(expectedWords), transcribedWords,
                    result != null && result.isAvailable ? result.providerName : string.Empty);

                LogRecallScored(info);
                recordingCompleted?.Invoke(info);
            });

            m_Recordings.Add(info);
            return info;
        }

        /// <summary>Cancels an in-flight recording without producing a result.</summary>
        public void AbortRecording(string reason)
        {
            if (!m_Recording)
                return;

            if (microphoneAvailable && Microphone.IsRecording(m_ActiveDevice))
                Microphone.End(m_ActiveDevice);

            var aborted = m_Current;
            var detectorState = DescribeAdaptiveStopState();

            m_Recording = false;
            m_Clip = null;
            m_Current = null;

            // An aborted recording still gets a stop event. Otherwise a recording that started
            // would have no ending in the data, and its absence would have to be inferred.
            if (aborted != null)
            {
                var logger = EventLogger.Instance;
                aborted.stopReason = RecallStopReasons.Aborted;
                aborted.speechDetected = m_Detector.speechDetected;
                aborted.endRelativeSeconds = logger != null ? logger.clock.RelativeSeconds() : 0d;
                aborted.actualDurationSeconds =
                    aborted.endRelativeSeconds - aborted.startRelativeSeconds;
                aborted.failureReason = $"aborted: {reason}";

                LogRecordingStop(aborted, $"{detectorState}; abort_reason={reason}");
            }

            ResetAdaptiveStopState();

            Debug.Log($"[IKEA_EEG] Recording aborted ({reason}).");
        }

        /// <summary>Clears recording state for a new trial. Does not delete files on disk.</summary>
        public void ResetForNewTrial()
        {
            AbortRecording("trial reset");
            m_Recordings.Clear();
        }

        // ---------------------------------------------------------------------------------
        // Internals
        // ---------------------------------------------------------------------------------

        string BuildWavPath(RecordingInfo info)
        {
            var logger = EventLogger.Instance;
            var root = logger != null && !string.IsNullOrEmpty(logger.sessionDirectory)
                ? logger.sessionDirectory
                : Application.persistentDataPath;

            var audioDir = Path.Combine(root, "audio");
            var trial = string.IsNullOrEmpty(info.trialId) ? "T000" : info.trialId;
            var phase = info.recallPhase.ToLowerInvariant();

            return Path.Combine(audioDir, $"{trial}_{phase}_recall.wav");
        }

        /// <summary>
        /// Emits MICROPHONE_SIGNAL_SILENT. Kept as its own event so a single event_type query
        /// finds every recall whose audio is unusable, without parsing free text.
        /// </summary>
        void LogSignalSilent(RecordingInfo info)
        {
            Debug.LogError($"[IKEA_EEG] MICROPHONE SIGNAL SILENT — \"{info.deviceName}\" produced " +
                           $"{info.sampleCount} samples with no usable signal.\n" +
                           $"    {info.signal.ToNotes(m_SilencePeakThreshold)}\n" +
                           $"    duration={info.durationMs:F0} ms, rate={info.sampleRate} Hz\n" +
                           "    The WAV was still written for debugging. Check that the Quest " +
                           "microphone is not muted and that Windows privacy settings allow " +
                           "microphone access.");

            var logger = EventLogger.Instance;
            if (logger == null)
                return;

            logger.Log(EventTypes.MicrophoneSignalSilent, e =>
            {
                e.recallPhase = info.recallPhase;
                e.objectId = info.deviceName;
                e.notes = string.Format(CultureInfo.InvariantCulture,
                    "device={0}; duration_ms={1:F1}; sample_rate={2}; {3}",
                    info.deviceName, info.durationMs, info.sampleRate,
                    info.signal.ToNotes(m_SilencePeakThreshold));
            });
        }

        // ---------------------------------------------------------------------------------
        // Startup microphone health check
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Records a short buffer and measures it, so a device that exists but delivers
        /// silence is caught before the participant has spoken into it for 20 seconds.
        /// </summary>
        public IEnumerator RunStartupMicrophoneTest()
        {
            if (!m_RunStartupMicrophoneTest || !microphoneAvailable)
                yield break;

            AudioClip testClip = null;

            try
            {
                testClip = Microphone.Start(m_ActiveDevice, false,
                    Mathf.CeilToInt(m_StartupTestSeconds) + 1, m_EffectiveSampleRate);
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Microphone test could not start: {e.Message}");
                yield break;
            }

            yield return new WaitForSeconds(m_StartupTestSeconds);

            var position = Microphone.GetPosition(m_ActiveDevice);
            Microphone.End(m_ActiveDevice);

            var stats = new WavUtility.SignalStats();

            if (testClip != null && position > 0)
            {
                var channels = Mathf.Max(1, testClip.channels);
                var raw = new float[testClip.samples * channels];
                testClip.GetData(raw, 0);
                stats = WavUtility.AnalyzeSignal(raw, position * channels);
            }

            startupTestStats = stats;
            startupTestPassed = position > 0 && !stats.IsSilent(m_SilencePeakThreshold);

            // A quiet room legitimately produces a low peak, so a failed test is reported as a
            // warning to check, not as a hard error.
            if (startupTestPassed)
            {
                Debug.Log($"[IKEA_EEG] Microphone test PASSED on \"{m_ActiveDevice}\": " +
                          $"{stats.ToNotes(m_SilencePeakThreshold)}");
            }
            else
            {
                Debug.LogWarning($"[IKEA_EEG] \"{m_ActiveDevice}\" selected but no usable signal " +
                                 $"detected in a {m_StartupTestSeconds:F1} s test capture.\n" +
                                 $"    {stats.ToNotes(m_SilencePeakThreshold)}\n" +
                                 "    If this is the Quest microphone, check it is not muted and " +
                                 "that Windows microphone privacy access is enabled. " +
                                 "(A completely silent room can also cause this.)");
            }

            var logger = EventLogger.Instance;
            logger?.Log(EventTypes.MicrophoneTest, e =>
            {
                e.objectId = m_ActiveDevice;
                e.correct = startupTestPassed ? "TRUE" : "FALSE";
                e.notes = $"device={m_ActiveDevice}; test_seconds={m_StartupTestSeconds:F1}; " +
                          $"{stats.ToNotes(m_SilencePeakThreshold)}";
            });
        }

        /// <summary>Signal statistics from the session-start microphone test.</summary>
        public WavUtility.SignalStats startupTestStats { get; private set; }

        /// <summary>False when the startup capture produced no usable signal.</summary>
        public bool startupTestPassed { get; private set; }

        public float silencePeakThreshold => m_SilencePeakThreshold;

        /// <summary>
        /// Emits RECALL_RECORDING_STOP: how long the recording actually ran and why it ended.
        ///
        /// This is separate from RECORDING_SAVED because "why did this recording end" and
        /// "what was written to disk" are different questions, and only the first one can
        /// distinguish a participant who finished early from one who ran out of time.
        ///
        /// The stored WAV is never modified or trimmed on the basis of any of this — it stays
        /// exactly as captured.
        /// </summary>
        void LogRecordingStop(RecordingInfo info, string detectorState)
        {
            var logger = EventLogger.Instance;
            if (logger == null)
                return;

            logger.Log(EventTypes.RecallRecordingStop, e =>
            {
                e.recallPhase = info.recallPhase;
                e.correct = info.speechDetected ? "TRUE" : "FALSE";
                // The detector's own state is nested inside detector[...] so its keys cannot be
                // confused with the top-level ones by a naive key=value parser.
                //
                // EVERYTHING NEEDED TO DIAGNOSE A FAILED AUTO-STOP IS IN THIS ONE ROW: the
                // device, the thresholds in force, the measured level distribution of the
                // recording, how long the longest silence run actually got, and a plain-text
                // verdict. Nothing here is inferred — every number was measured during this
                // recording.
                e.notes = string.Format(CultureInfo.InvariantCulture,
                    "termination_reason={0}; actual_recording_duration_s={1:F3}; " +
                    "speech_detected={2}; speech_start_relative_s={3:F3}; " +
                    "trailing_silence_s={4:F2}; max_silence_run_s={5:F2}; " +
                    "microphone_device={6}; device_is_fallback={7}; sample_rate={8}; " +
                    "verdict={9}; detector[{10}]; levels[{11}]",
                    string.IsNullOrEmpty(info.stopReason) ? RecallStopReasons.Manual : info.stopReason,
                    info.actualDurationSeconds,
                    info.speechDetected ? "TRUE" : "FALSE",
                    info.speechStartRelativeSeconds > 0d
                        ? info.speechStartRelativeSeconds - info.startRelativeSeconds
                        : 0d,
                    info.trailingSilenceSeconds,
                    m_Detector.maxSilenceRunSeconds,
                    string.IsNullOrEmpty(info.deviceName) ? "none" : info.deviceName,
                    info.deviceIsFallback ? "TRUE" : "FALSE",
                    info.sampleRate,
                    m_Detector.DiagnoseNoAutoStop(),
                    detectorState,
                    m_Detector.DescribeLevelDistribution());
            });

            var message = $"[IKEA_EEG] {info.recallPhase} recall stopped after " +
                          $"{info.actualDurationSeconds:F2} s — {info.stopReason}.\n" +
                          $"    verdict: {m_Detector.DiagnoseNoAutoStop()}\n" +
                          $"    levels:  {m_Detector.DescribeLevelDistribution()}";

            // A recording that ran to the hard maximum is the symptom of a detector that could
            // not fire. Surface it as a warning with the measurements attached rather than
            // leaving it to be noticed in the CSV afterwards.
            if (info.stopReason == RecallStopReasons.MaxDuration)
                Debug.LogWarning(message);
            else
                Debug.Log(message);
        }

        void LogRecordingSaved(RecordingInfo info)
        {
            var logger = EventLogger.Instance;
            if (logger == null)
                return;

            logger.Log(EventTypes.RecordingSaved, e =>
            {
                e.recallPhase = info.recallPhase;
                e.objectId = info.audioCaptured ? Path.GetFileName(info.wavPath) : string.Empty;
                e.notes = string.Format(CultureInfo.InvariantCulture,
                    "audio_captured={0}; signal_silent={1}; duration_ms={2:F1}; sample_rate={3}; " +
                    "samples={4}; device={5}; device_is_fallback={6}; {7}; path={8}; provider={9}{10}",
                    info.audioCaptured ? "TRUE" : "FALSE",
                    info.signalSilent ? "TRUE" : "FALSE",
                    info.durationMs,
                    info.sampleRate,
                    info.sampleCount,
                    string.IsNullOrEmpty(info.deviceName) ? "none" : info.deviceName,
                    info.deviceIsFallback ? "TRUE" : "FALSE",
                    info.signal.ToNotes(m_SilencePeakThreshold),
                    string.IsNullOrEmpty(info.wavPath) ? "none" : info.wavPath,
                    providerName,
                    string.IsNullOrEmpty(info.failureReason) ? string.Empty : $"; reason={info.failureReason}");
            });
        }

        void LogRecallScored(RecordingInfo info)
        {
            var logger = EventLogger.Instance;
            if (logger == null || info.score == null)
                return;

            logger.Log(EventTypes.RecallScored, e =>
            {
                e.recallPhase = info.recallPhase;
                e.expectedWord = info.score.ExpectedWordsCsv();
                e.transcript = info.score.scored ? info.score.TranscribedWordsCsv() : string.Empty;
                e.correct = info.score.scored
                    ? (info.score.wordOrderCorrect ? "TRUE" : "FALSE")
                    : string.Empty;
                e.notes = info.score.scored
                    ? $"{info.score.ToNotesString()}; intrusion_words={info.score.IntrusionsCsv()}"
                    : $"not_scored; reason={info.transcription?.unavailableReason ?? "no provider"}";
            });
        }
    }
}
