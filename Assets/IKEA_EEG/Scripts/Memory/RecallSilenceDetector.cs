using System.Collections.Generic;
using System.Globalization;

namespace IkeaEeg.Memory
{
    /// <summary>
    /// Decides when a recall recording has ended, from a stream of analysis-window amplitudes.
    ///
    /// PURE LOGIC, NO UNITY, NO MICROPHONE. That separation is the point: the rule below is the
    /// part that can silently ruin a recall trial by cutting a participant off mid-answer, and
    /// it is the part that cannot be tested by pressing Play and hoping. Feeding it synthetic
    /// window peaks tests the DECISION exactly; what it cannot test is whether the microphone
    /// produces sensible amplitudes in the headset, which is a headset question.
    ///
    /// THE RULE:
    ///   1. Silence BEFORE speech never stops anything. A participant who takes fifteen seconds
    ///      to begin is not interrupted — the detector is not even armed yet.
    ///   2. Speech "starts" only once <see cref="minimumSpeechDuration"/> of voiced audio has
    ///      ACCUMULATED (windows at or above <see cref="speechStartThreshold"/>). A cough or a
    ///      knocked controller cannot arm it.
    ///   3. Once armed, a CONTINUOUS run of windows below <see cref="silenceThreshold"/>
    ///      lasting <see cref="silenceStopSeconds"/> ends the recording. Any window at or above
    ///      the silence threshold resets that run to zero — which is what makes normal pauses
    ///      between recalled words safe.
    ///   4. The band between the two thresholds is hysteresis: it counts as neither speech nor
    ///      silence, so room tone hovering near the threshold cannot end a recording.
    ///
    /// The detector only ever REQUESTS a stop. The ExperimentManager decides what to do about
    /// it, so the protocol keeps sole authority over when a phase ends.
    /// </summary>
    public class RecallSilenceDetector
    {
        public const float DefaultSilenceStopSeconds = 4f;
        public const float DefaultSpeechStartThreshold = 0.05f;
        public const float DefaultSilenceThreshold = 0.015f;
        public const float DefaultMinimumSpeechDuration = 0.35f;
        public const float DefaultWindowSeconds = 0.1f;

        public float silenceStopSeconds { get; private set; } = DefaultSilenceStopSeconds;
        public float speechStartThreshold { get; private set; } = DefaultSpeechStartThreshold;
        public float silenceThreshold { get; private set; } = DefaultSilenceThreshold;
        public float minimumSpeechDuration { get; private set; } = DefaultMinimumSpeechDuration;
        public float windowSeconds { get; private set; } = DefaultWindowSeconds;

        /// <summary>True once sustained speech has been confirmed.</summary>
        public bool speechDetected { get; private set; }

        /// <summary>Voiced audio accumulated so far, seconds.</summary>
        public float voicedSeconds { get; private set; }

        /// <summary>Length of the CURRENT unbroken silence run, seconds.</summary>
        public float silenceRunSeconds { get; private set; }

        /// <summary>True when the silence criterion has been met. Latches until Reset.</summary>
        public bool stopRequested { get; private set; }

        /// <summary>Set alongside <see cref="stopRequested"/>.</summary>
        public string stopReason { get; private set; } = string.Empty;

        /// <summary>Windows consumed since the last reset. Diagnostic.</summary>
        public int windowsAnalyzed { get; private set; }

        /// <summary>Loudest window peak seen since the last reset. Diagnostic.</summary>
        public float loudestWindowPeak { get; private set; }

        /// <summary>Loudest window RMS seen since the last reset. Diagnostic.</summary>
        public float loudestWindowRms { get; private set; }

        /// <summary>
        /// Longest continuous silence run reached at any point, seconds — NOT just the run in
        /// progress at the end.
        ///
        /// This is the single most diagnostic number when a recording fails to auto-stop: if it
        /// stays far below silenceStopSeconds while the participant was clearly quiet, then the
        /// windows the detector is seeing are not below silenceThreshold, and the threshold (or
        /// the microphone's noise floor) is the problem rather than the timing.
        /// </summary>
        public float maxSilenceRunSeconds { get; private set; }

        /// <summary>Windows classified as speech (peak >= speechStartThreshold).</summary>
        public int speechWindowCount { get; private set; }

        /// <summary>Windows classified as silence (peak &lt; silenceThreshold).</summary>
        public int silenceWindowCount { get; private set; }

        /// <summary>
        /// Windows in the hysteresis band — neither speech nor silence. A large count here
        /// means the microphone's floor sits BETWEEN the two thresholds, which would prevent
        /// the recording from ever auto-stopping.
        /// </summary>
        public int bandWindowCount => windowsAnalyzed - speechWindowCount - silenceWindowCount;

        // Every window's measurements, so the real microphone's level distribution can be
        // recovered after a session instead of being guessed at. 20 s at 0.1 s windows is 200
        // entries; the cap only exists so a pathological configuration cannot grow unbounded.
        const int k_MaxRecordedWindows = 4096;
        readonly List<float> m_WindowPeaks = new List<float>(256);
        readonly List<float> m_WindowRms = new List<float>(256);

        /// <summary>
        /// Seconds of audio consumed since the last reset, derived from the window count.
        /// Independent of wall-clock time, so tests are deterministic.
        /// </summary>
        public float analyzedSeconds => windowsAnalyzed * windowSeconds;

        /// <summary>
        /// Applies the protocol's thresholds. Crossed thresholds are rejected: a window must
        /// never be able to classify as both speech and silence.
        /// </summary>
        /// <returns>False when the values were rejected and defaults were used instead.</returns>
        public bool Configure(float silenceStop, float speechStart, float silence,
            float minimumSpeech, float window = DefaultWindowSeconds)
        {
            windowSeconds = window > 0f ? window : DefaultWindowSeconds;
            silenceStopSeconds = silenceStop > 0f ? silenceStop : DefaultSilenceStopSeconds;
            minimumSpeechDuration = minimumSpeech >= 0f ? minimumSpeech : DefaultMinimumSpeechDuration;

            if (speechStart <= silence)
            {
                speechStartThreshold = DefaultSpeechStartThreshold;
                silenceThreshold = DefaultSilenceThreshold;
                return false;
            }

            speechStartThreshold = speechStart;
            silenceThreshold = silence;
            return true;
        }

        public void Reset()
        {
            speechDetected = false;
            voicedSeconds = 0f;
            silenceRunSeconds = 0f;
            stopRequested = false;
            stopReason = string.Empty;
            windowsAnalyzed = 0;
            loudestWindowPeak = 0f;
            loudestWindowRms = 0f;
            maxSilenceRunSeconds = 0f;
            speechWindowCount = 0;
            silenceWindowCount = 0;
            m_WindowPeaks.Clear();
            m_WindowRms.Clear();
        }

        /// <summary>
        /// Consumes one analysis window, given its peak amplitude (0-1).
        /// </summary>
        /// <returns>True at the exact window on which speech was first confirmed.</returns>
        /// <param name="rms">
        /// Optional RMS of the same window. Not used by any decision — recorded purely so a
        /// real microphone's level distribution can be inspected afterwards.
        /// </param>
        public bool PushWindow(float peak, float rms = 0f)
        {
            windowsAnalyzed++;

            if (peak > loudestWindowPeak)
                loudestWindowPeak = peak;

            if (rms > loudestWindowRms)
                loudestWindowRms = rms;

            if (m_WindowPeaks.Count < k_MaxRecordedWindows)
            {
                m_WindowPeaks.Add(peak);
                m_WindowRms.Add(rms);
            }

            // Classification counters are updated for EVERY window, including after a stop has
            // been requested, so the record describes the whole recording rather than only the
            // part before the decision.
            if (peak >= speechStartThreshold)
                speechWindowCount++;
            else if (peak < silenceThreshold)
                silenceWindowCount++;

            if (stopRequested)
                return false;

            if (!speechDetected)
            {
                // Arming phase. Silence here is ignored entirely — rule 1.
                if (peak < speechStartThreshold)
                    return false;

                voicedSeconds += windowSeconds;

                if (voicedSeconds + 1e-6f < minimumSpeechDuration)
                    return false;

                speechDetected = true;
                silenceRunSeconds = 0f;
                return true;
            }

            if (peak < silenceThreshold)
            {
                silenceRunSeconds += windowSeconds;

                if (silenceRunSeconds > maxSilenceRunSeconds)
                    maxSilenceRunSeconds = silenceRunSeconds;

                if (silenceRunSeconds + 1e-6f >= silenceStopSeconds)
                {
                    stopRequested = true;
                    stopReason = Core.RecallStopReasons.SilenceAfterSpeech;
                }
            }
            else
            {
                // At or above the silence threshold — including the hysteresis band — so the
                // run restarts. This is what protects pauses between recalled words.
                silenceRunSeconds = 0f;
            }

            return false;
        }

        /// <summary>
        /// The measured level distribution of this recording.
        ///
        /// WHY PERCENTILES: the question "why did this recording not auto-stop?" is really the
        /// question "where is this microphone's noise floor relative to silenceThreshold?".
        /// The 10th percentile of window peaks is a good estimate of that floor — it is what
        /// the quietest moments actually measured. If p10_peak sits ABOVE silenceThreshold,
        /// then no window ever counted as silence and the detector could never have fired,
        /// which is a threshold/microphone problem, not a timing one.
        ///
        /// Reported alongside RMS because peak is transient-sensitive: a single sample spike in
        /// a 0.1 s window makes the whole window "not silent". If p50_peak is far above
        /// p50_rms, that is the signature of a spiky floor rather than a genuinely loud room.
        /// </summary>
        public string DescribeLevelDistribution()
        {
            if (m_WindowPeaks.Count == 0)
                return "no_windows_analyzed=TRUE";

            var peaks = new List<float>(m_WindowPeaks);
            var rms = new List<float>(m_WindowRms);
            peaks.Sort();
            rms.Sort();

            return string.Format(CultureInfo.InvariantCulture,
                "p10_peak={0:F5}; p50_peak={1:F5}; p90_peak={2:F5}; max_peak={3:F5}; " +
                "p10_rms={4:F5}; p50_rms={5:F5}; p90_rms={6:F5}; max_rms={7:F5}; " +
                "speech_windows={8}; silence_windows={9}; band_windows={10}; " +
                "estimated_noise_floor_peak={0:F5}; floor_below_silence_threshold={11}",
                Percentile(peaks, 0.10f), Percentile(peaks, 0.50f), Percentile(peaks, 0.90f),
                peaks[peaks.Count - 1],
                Percentile(rms, 0.10f), Percentile(rms, 0.50f), Percentile(rms, 0.90f),
                rms[rms.Count - 1],
                speechWindowCount, silenceWindowCount, bandWindowCount,
                Percentile(peaks, 0.10f) < silenceThreshold ? "TRUE" : "FALSE");
        }

        /// <summary>
        /// The one-line verdict on why a recording ended the way it did. Written into the stop
        /// event so the cause is in the data rather than needing to be reconstructed.
        /// </summary>
        public string DiagnoseNoAutoStop()
        {
            if (windowsAnalyzed == 0)
            {
                return "NO_WINDOWS_ANALYZED — the detector never received audio. The microphone " +
                       "buffer is not advancing as expected; thresholds are irrelevant here.";
            }

            if (!speechDetected)
            {
                return $"SPEECH_NEVER_DETECTED — no window reached speechStartThreshold " +
                       $"({speechStartThreshold:F4}); loudest window peak was " +
                       $"{loudestWindowPeak:F5}. If the participant did speak, the threshold is " +
                       "above this microphone's speech level.";
            }

            if (stopRequested)
                return "AUTO_STOP_FIRED — speech then sustained silence, as designed.";

            if (m_WindowPeaks.Count > 0)
            {
                var peaks = new List<float>(m_WindowPeaks);
                peaks.Sort();
                var floor = Percentile(peaks, 0.10f);

                if (floor >= silenceThreshold)
                {
                    return $"NOISE_FLOOR_ABOVE_SILENCE_THRESHOLD — the quietest windows measured " +
                           $"~{floor:F5}, at or above silenceThreshold ({silenceThreshold:F4}), " +
                           "so no window ever counted as silence. Raise silenceThreshold above " +
                           "the measured floor (but keep it below speech level).";
                }
            }

            return $"SILENCE_TOO_SHORT — silence was detected but never ran for " +
                   $"{silenceStopSeconds:F1} s uninterrupted; the longest run was " +
                   $"{maxSilenceRunSeconds:F2} s. The floor is spiky: single loud samples keep " +
                   "resetting the run.";
        }

        /// <summary>Nearest-rank percentile. Plain C# so this class stays Unity-free.</summary>
        static float Percentile(List<float> sorted, float fraction)
        {
            if (sorted.Count == 0)
                return 0f;

            var index = (int)System.Math.Round(fraction * (sorted.Count - 1));

            if (index < 0)
                index = 0;
            else if (index >= sorted.Count)
                index = sorted.Count - 1;

            return sorted[index];
        }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "speech_detected={0}; voiced_s={1:F2}; trailing_silence_s={2:F2}; " +
                "windows={3}; loudest_window_peak={4:F4}; speech_threshold={5:F4}; " +
                "silence_threshold={6:F4}; silence_stop_s={7:F2}; min_speech_s={8:F2}; " +
                "window_s={9:F3}",
                speechDetected ? "TRUE" : "FALSE", voicedSeconds, silenceRunSeconds,
                windowsAnalyzed, loudestWindowPeak, speechStartThreshold, silenceThreshold,
                silenceStopSeconds, minimumSpeechDuration, windowSeconds);
        }
    }
}
