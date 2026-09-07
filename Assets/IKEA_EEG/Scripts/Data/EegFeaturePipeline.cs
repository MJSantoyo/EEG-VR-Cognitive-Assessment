using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace IkeaEeg.Data
{
    /// <summary>Why a window's features may not be trustworthy. Flags, never repairs.</summary>
    [Flags]
    public enum EegQualityFlags
    {
        None = 0,

        /// <summary>The filter has not yet run long enough for its output to be signal.</summary>
        FilterNotSettled = 1 << 0,

        /// <summary>Fewer samples than the requested window needs.</summary>
        InsufficientSamples = 1 << 1,

        NaNPresent = 1 << 2,
        InfinityPresent = 1 << 3,

        /// <summary>A channel does not vary at all across the window.</summary>
        Flatline = 1 << 4,

        /// <summary>The same value repeats far more than chance allows — a stuck ADC or a rail.</summary>
        SaturationLike = 1 << 5,

        /// <summary>A step between neighbouring samples far larger than the channel's own norm.</summary>
        AbruptDiscontinuity = 1 << 6,

        /// <summary>This window's range is wildly unlike the same channel's usual range.</summary>
        ExtremeDynamicRange = 1 << 7,

        /// <summary>The montage could not supply every electrode an ROI needs.</summary>
        RoiUnresolved = 1 << 8,

        /// <summary>
        /// Two or more channels carried EXACTLY the same signal across the window, or every
        /// channel produced exactly the same band power.
        ///
        /// Independent electrodes on a real head cannot produce bit-identical float sequences:
        /// they differ in position, impedance and noise. When they do match exactly, the cause
        /// is upstream of any interpretation — disconnected leads all reading the reference, an
        /// amplifier test pattern, or a duplicated channel in the transmitted layout.
        ///
        /// This exists because every other check in this class looks at ONE channel at a time.
        /// A run in which all eight electrodes returned identical theta and alpha passed all of
        /// them, because each channel was, individually, a perfectly well-behaved signal. Only
        /// a comparison BETWEEN channels can see it.
        /// </summary>
        IdenticalChannels = 1 << 9,

        /// <summary>
        /// One or more channels carry band power orders of magnitude away from what the other
        /// channels of the same recording are doing.
        ///
        /// Judged against the MEDIAN across channels, never the mean: a single channel that is
        /// 10^6 too large drags a mean past itself and the test then clears the very sample it
        /// was meant to catch. The median and the median absolute deviation are unmoved by a
        /// minority of extreme values, which is exactly the situation this flag exists for.
        ///
        /// The comparison is in log10 space and entirely relative — a ratio against the other
        /// channels of the same window. No µV, no absolute threshold: with the amplitude
        /// scaling unverified, an absolute limit would be a guess wearing a number.
        /// </summary>
        ChannelPowerOutlier = 1 << 10,

        /// <summary>
        /// The window looks like it contains a large non-stationary excursion — a settling
        /// transient, an electrode pop, a movement artefact — rather than stationary EEG.
        ///
        /// Detected by comparing the first and last thirds of the window: a channel whose RMS
        /// collapses or explodes between them is not stationary, and a PSD computed over it
        /// describes the excursion rather than the brain. SUSPECTED, not asserted: the check
        /// cannot tell which physical cause produced the non-stationarity, and does not guess.
        /// </summary>
        TransientArtifactSuspected = 1 << 11,

        /// <summary>
        /// Two or more channels carried almost — but not exactly — the same signal.
        ///
        /// The companion to <see cref="IdenticalChannels"/>, and the reason that flag was not
        /// enough on its own. A recording in which every electrode correlated at r &gt; 0.9999
        /// after filtering passed the bit-identity test cleanly, because no two sequences were
        /// bit-equal: they differed in the last few bits and by a small gain. Nothing else in
        /// this class could see it either, since each channel was individually well behaved.
        ///
        /// WHAT IT MEANS AND WHAT IT DOES NOT. It means the channels carry too little
        /// independent variance to be treated as separate measurements, so any regional contrast
        /// between them is comparing a signal with itself. It does NOT diagnose a cause —
        /// reference configuration, a montage error, a driver fault and a genuinely
        /// common-mode-dominated recording all look like this from here — and it makes no claim
        /// that the data is physiologically wrong.
        ///
        /// The threshold is an engineering heuristic, configurable, and documented on
        /// <see cref="EegQualityThresholds.nearIdenticalCorrelation"/>.
        /// </summary>
        NearIdenticalChannels = 1 << 12,

        /// <summary>
        /// One or more channels have been judged DEGRADED across several consecutive windows —
        /// an electrode that was working and stopped.
        ///
        /// Distinct from every other flag here because it is the only one with MEMORY. All the
        /// others describe the window in front of them; this one describes a channel's history,
        /// and is what catches an electrode that a headset shifts mid-session. The state, the
        /// time it began and the reason live in <see cref="EegChannelHealthTracker"/>.
        /// </summary>
        ChannelDegraded = 1 << 13,
    }

    /// <summary>
    /// The most recent spectral features, as a plain snapshot.
    ///
    /// A VALUE OBJECT, deliberately: the researcher monitor and any future consumer read this
    /// and nothing else, so nothing outside the pipeline reaches into filter state, ring buffers
    /// or LSL internals. Replacing the pipeline later means producing the same snapshot.
    /// </summary>
    public class LatestEegFeatures
    {
        public double analysisTimestamp;
        public double windowStart;
        public double windowEnd;
        public double windowSeconds;
        public int sampleCount;
        public int channelCount;
        public double sampleRateHz;

        public EegQualityFlags quality = EegQualityFlags.None;
        public bool filterReady;

        /// <summary>
        /// False when anything makes these numbers untrustworthy.
        ///
        /// Separate from the flags so a consumer can ask one question. The features are still
        /// present when this is false — flagged, not deleted — so a researcher can inspect what
        /// was rejected and why.
        /// </summary>
        public bool featureValidity;

        /// <summary>Per channel, in stream order. Always kept, never collapsed away.</summary>
        public double[] thetaPerChannel;
        public double[] alphaPerChannel;

        /// <summary>Electrode labels resolved from the montage, parallel to the arrays above.</summary>
        public string[] channelLabels;

        /// <summary>
        /// How many channel PAIRS were bit-identical across the whole window. 0 is the only
        /// value a healthy recording produces.
        /// </summary>
        public int identicalChannelPairs;

        /// <summary>Which channels matched, in montage labels. Empty when none did.</summary>
        public string identicalChannelDetail = string.Empty;

        /// <summary>Channel indices whose band power is an outlier against the others.</summary>
        public int[] powerOutlierChannels = System.Array.Empty<int>();

        /// <summary>Which channels were outliers and by how much. Empty when none were.</summary>
        public string powerOutlierDetail = string.Empty;

        /// <summary>Which channels look non-stationary. Empty when none do.</summary>
        public string transientDetail = string.Empty;

        /// <summary>
        /// Set when a flagged channel is one an ROI actually averages, so the ROI value itself
        /// is contaminated rather than merely sitting in a window that had a problem elsewhere.
        /// </summary>
        public string roiContamination = string.Empty;

        /// <summary>How many channel pairs correlated above the near-identity threshold.</summary>
        public int nearIdenticalPairs;

        /// <summary>Which pairs, and at what correlation. Empty when none did.</summary>
        public string nearIdenticalDetail = string.Empty;

        /// <summary>The highest absolute pairwise correlation seen, for context even when clean.</summary>
        public double highestChannelCorrelation = double.NaN;

        /// <summary>Channel indices currently judged degraded by the health tracker.</summary>
        public int[] degradedChannels = System.Array.Empty<int>();

        /// <summary>Which electrodes are degraded, since when, and why. Empty when none are.</summary>
        public string degradedDetail = string.Empty;

        public double frontalTheta = double.NaN;
        public double posteriorAlpha = double.NaN;

        /// <summary>
        /// True only when BOTH ROI features are valid. Kept as the single question a consumer
        /// can ask; the two fields below say which one failed when it is false.
        /// </summary>
        public bool roiValid;
        public string roiProblem = string.Empty;

        /// <summary>
        /// Per-ROI validity, so one broken electrode does not condemn the other region.
        ///
        /// A single roiValid could only ever say "something is wrong somewhere". When P3 fails,
        /// posterior alpha is unusable and frontal theta is untouched — reporting both as invalid
        /// throws away a good measurement, and reporting both as valid publishes a poisoned one.
        /// </summary>
        public bool frontalThetaValid;

        public bool posteriorAlphaValid;

        /// <summary>Why the frontal ROI is invalid, naming the electrode. Empty when it is valid.</summary>
        public string frontalThetaProblem = string.Empty;

        /// <summary>Why the posterior ROI is invalid, naming the electrode. Empty when it is valid.</summary>
        public string posteriorAlphaProblem = string.Empty;

        /// <summary>EXPLORATORY. Not a validated workload score.</summary>
        public double thetaAlphaRatio = double.NaN;
        public double logThetaAlpha = double.NaN;
        public bool derivedValid;

        public bool baselineAvailable;
        public double deltaThetaDb = double.NaN;
        public double deltaAlphaDb = double.NaN;

        /// <summary>Unit labels, taken from verified provenance — never assumed.</summary>
        public string amplitudeUnits = "AURA native units";
        public string powerUnits = "AURA-native-units²";
        public string psdUnits = "AURA-native-units²/Hz";

        public string QualityText()
        {
            if (quality == EegQualityFlags.None)
                return "PASS";

            var reasons = new List<string>();

            foreach (EegQualityFlags flag in Enum.GetValues(typeof(EegQualityFlags)))
            {
                if (flag != EegQualityFlags.None && (quality & flag) != 0)
                    reasons.Add(flag.ToString());
            }

            return "FLAGGED: " + string.Join(", ", reasons);
        }

        /// <summary>
        /// The flags spelled out in full, one line each, with the specific channels named.
        ///
        /// Separate from <see cref="QualityText"/> because a flag NAME is not a finding: knowing
        /// the window was flagged "ChannelPowerOutlier" does not tell a researcher which
        /// electrode to go and re-seat. Empty when the window is clean.
        /// </summary>
        public string Explain()
        {
            var lines = new List<string>();

            if (!string.IsNullOrEmpty(identicalChannelDetail))
                lines.Add("identical channels: " + identicalChannelDetail);

            if (!string.IsNullOrEmpty(nearIdenticalDetail))
                lines.Add("near-identical channels: " + nearIdenticalDetail);

            if (!string.IsNullOrEmpty(degradedDetail))
                lines.Add("DEGRADED: " + degradedDetail);

            if (!string.IsNullOrEmpty(frontalThetaProblem))
                lines.Add("FRONTAL_THETA invalid: " + frontalThetaProblem);

            if (!string.IsNullOrEmpty(posteriorAlphaProblem))
                lines.Add("POSTERIOR_ALPHA invalid: " + posteriorAlphaProblem);

            if (!string.IsNullOrEmpty(powerOutlierDetail))
                lines.Add("power outlier: " + powerOutlierDetail);

            if (!string.IsNullOrEmpty(transientDetail))
                lines.Add("non-stationary: " + transientDetail);

            if (!string.IsNullOrEmpty(roiContamination))
                lines.Add("ROI CONTAMINATED: " + roiContamination);

            if ((quality & EegQualityFlags.FilterNotSettled) != 0)
                lines.Add("the filter had not settled when this window was cut");

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// The live EEG feature pipeline: continuous preprocessing, quality assessment, Welch PSD
    /// and band features.
    ///
    /// ARCHITECTURE — it OBSERVES the existing acquisition and adds nothing beside it. It
    /// subscribes to the one <see cref="AuraLslReceiver"/>'s sample event; it opens no inlet,
    /// constructs no receiver and never touches the raw ring buffer. Raw amplitudes and all
    /// three raw timestamps stay exactly where they are; the filtered signal lives in a SEPARATE
    /// store, so there is always a path back to the original samples.
    ///
    /// CONTINUOUS FILTERING. Every sample passes through a persistent per-channel filter as it
    /// arrives, once. Windows are cut from the already-filtered history. Filtering each epoch
    /// from zero state would put a settling transient at the start of every window — precisely
    /// where the event of interest sits.
    ///
    /// COST. The filter is 4 biquads per channel per sample: cheap, and it is the only per-sample
    /// work. The FFT runs at WINDOW cadence, on demand, never at sample rate.
    ///
    /// WHAT IT DOES NOT DO: no artefact repair, no interpolation, no re-referencing, no
    /// baseline invented from the first seconds of a run, no interpretation, and no adaptation
    /// of anything in the experiment.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(AuraLslReceiver))]
    public class EegFeaturePipeline : MonoBehaviour
    {
        [Header("Preprocessing")]
        [Tooltip("High-pass cutoff, Hz. Attenuates drift while preserving theta from 4 Hz.")]
        [SerializeField] double m_HighPassHz = 1.0;

        [Tooltip("Low-pass cutoff, Hz. Keeps theta, alpha and low beta; puts 60 Hz mains " +
                 "outside the passband, which is why the notch below is optional.")]
        [SerializeField] double m_LowPassHz = 40.0;

        [Tooltip("OPTIONAL mains notch. OFF by default and REDUNDANT for 1-40 Hz analysis: " +
                 "the low-pass already attenuates 60 Hz and neither theta nor alpha is near " +
                 "it. Provided because it is part of the stated preprocessing protocol.")]
        [SerializeField] bool m_NotchEnabled;

        [Tooltip("Notch centre frequency, Hz. 60 in the Americas, 50 in most of the world. " +
                 "Never inferred from the data — this is a statement about the mains supply.")]
        [SerializeField] double m_NotchHz = EegBandpassFilter.DefaultNotchHz;

        [Tooltip("Notch quality factor. Q = centre / bandwidth, so Q 30 at 60 Hz is a 2 Hz " +
                 "-3 dB bandwidth.")]
        [SerializeField] double m_NotchQ = EegBandpassFilter.DefaultNotchQ;

        [Header("Spectral window")]
        [Tooltip("Default outer analysis window, seconds. 4 s gives ~16 cycles at 4 Hz.")]
        [SerializeField] double m_WindowSeconds = 4.0;

        [Tooltip("Shortest window permitted. Below 2 s there are too few theta cycles for a " +
                 "meaningful estimate.")]
        [SerializeField] double m_MinimumWindowSeconds = 2.0;

        [Tooltip("Welch sub-segment length, seconds. Sets the PHYSICAL frequency resolution.")]
        [SerializeField] double m_WelchSegmentSeconds = 2.0;

        [Header("Montage")]
        [Tooltip("Electrode mapping. Without it, per-channel features are still produced but " +
                 "ROI features are unavailable.")]
        [SerializeField] AuraMontageConfig m_Montage;

        [Header("History")]
        [Tooltip("How much FILTERED signal to keep, seconds. Must exceed the analysis window " +
                 "and the monitor's trace length.")]
        [SerializeField] double m_FilteredHistorySeconds = 30.0;

        [Header("Quality thresholds (ENGINEERING HEURISTICS, not scientific constants)")]
        [Tooltip("Cross-channel similarity and channel-degradation thresholds. Every value is " +
                 "relative — a correlation, a ratio or a count of windows — and none is in " +
                 "microvolts. Documented field by field on EegQualityThresholds. Changing one " +
                 "changes what gets flagged; do not tune them to make a session look clean.")]
        [SerializeField] EegQualityThresholds m_QualityThresholds = EegQualityThresholds.Default;

        [Tooltip("When a channel inside an ROI is degraded, mark that ROI's feature invalid. " +
                 "ON is the safe setting: the alternative publishes a regional average that " +
                 "includes a known-bad electrode. No electrode is ever substituted either way.")]
        [SerializeField] bool m_InvalidateRoiOnDegradedChannel = true;

        AuraLslReceiver m_Receiver;
        EegBandpassFilter m_Filter;
        RawEegRingBuffer m_Filtered;

        long m_SamplesFiltered;
        int m_SettlingSamples;

        double[] m_FilterScratch;

        /// <summary>Per-channel running median absolute step, for unit-free quality thresholds.</summary>
        double[] m_TypicalStep;
        double[] m_TypicalRange;

        /// <summary>
        /// Cross-window channel health. Built with the filter, because it is meaningless without
        /// a channel count, and reset with it, because a new filter means a new recording.
        /// </summary>
        EegChannelHealthTracker m_Health;

        /// <summary>
        /// Every channel health change this session, oldest first — electrode, timestamp, reason,
        /// affected ROI, and recovery.
        ///
        /// Exposed rather than only logged so the researcher monitor, a diagnostic or a future
        /// writer can read the whole history. The live session also prints each transition, so a
        /// run whose player log survives can be reconstructed even without this object.
        /// </summary>
        public IReadOnlyList<EegQcTransition> qcTransitions =>
            m_Health != null
                ? m_Health.transitions
                : (IReadOnlyList<EegQcTransition>)Array.Empty<EegQcTransition>();

        /// <summary>The thresholds actually in force, after clamping. Printed in diagnostics.</summary>
        public EegQualityThresholds qualityThresholds => m_QualityThresholds.Sanitised();

        /// <summary>The channel health tracker, for diagnostics. Null before the first sample.</summary>
        public EegChannelHealthTracker channelHealth => m_Health;

        /// <summary>The latest computed feature snapshot. The monitor reads this and nothing else.</summary>
        public LatestEegFeatures latest { get; private set; }

        /// <summary>The filtered history, for visualisation. Never the analysis of record.</summary>
        public RawEegRingBuffer filteredBuffer => m_Filtered;

        public EegBandpassFilter filter => m_Filter;
        public AuraMontageConfig montage => m_Montage;

        /// <summary>
        /// True once enough samples have passed through the filter for its output to be signal
        /// rather than start-up transient.
        /// </summary>
        public bool filterReady =>
            m_Filter != null && m_SamplesFiltered >= m_SettlingSamples;

        public int settlingSamples => m_SettlingSamples;

        public double settlingSeconds =>
            m_Receiver != null && m_Receiver.metadata.nominalSrate > 0d
                ? m_SettlingSamples / m_Receiver.metadata.nominalSrate
                : 0d;

        public long samplesFiltered => m_SamplesFiltered;

        // ---- Cross-channel quality thresholds ---------------------------------------------
        // All RELATIVE. Nothing here is in µV, and nothing is an absolute amplitude: each is a
        // ratio against the other channels of the same window, or against the same channel
        // earlier in the same window.

        /// <summary>
        /// How far from the across-channel MEDIAN log10 power a channel may sit before it is
        /// called an outlier. 2 decades = a factor of 100.
        ///
        /// Chosen to sit far above real inter-electrode differences — frontal and posterior
        /// electrodes routinely differ by a factor of a few in a band, which is under 1 decade —
        /// and far below the failure being caught, which was 6 decades.
        /// </summary>
        const double k_OutlierDecades = 2.0;

        /// <summary>
        /// Below this many usable channels there is no meaningful "distribution of the others"
        /// for a median to describe, so the outlier test does not run.
        /// </summary>
        const int k_MinimumChannelsForOutlier = 4;

        /// <summary>
        /// How different the first and last thirds of a window's RMS may be before the window
        /// is called non-stationary. 3x in either direction.
        /// </summary>
        const double k_TransientRmsRatio = 3.0;

        // ---- Baseline ---------------------------------------------------------------------

        /// <summary>Minimum baseline length. Shorter cannot give a stable spectral estimate.</summary>
        public const double MinimumBaselineSeconds = 4.0;

        double m_BaselineFrontalTheta = double.NaN;
        double m_BaselinePosteriorAlpha = double.NaN;
        double m_BaselineSeconds;
        string m_BaselineDetail = "no baseline captured";

        /// <summary>True when a baseline has been explicitly captured. NEVER inferred.</summary>
        public bool baselineAvailable =>
            !double.IsNaN(m_BaselineFrontalTheta) && !double.IsNaN(m_BaselinePosteriorAlpha) &&
            m_BaselineFrontalTheta > 0d && m_BaselinePosteriorAlpha > 0d;

        public string baselineDetail => m_BaselineDetail;
        public double baselineSeconds => m_BaselineSeconds;

        void Awake()
        {
            m_Receiver = GetComponent<AuraLslReceiver>();

            if (m_Montage == null)
                m_Montage = AuraMontageConfig.CreateHumanVerifiedDefault();
        }

        void OnEnable()
        {
            EnsureSubscribed();
        }

        /// <summary>
        /// Subscribes to the receiver, idempotently.
        ///
        /// Public because Unity does not run OnEnable for a component added in EDIT mode, so an
        /// Editor diagnostic that builds the pipeline itself must be able to attach it
        /// explicitly. At run time OnEnable calls this and the diagnostic path is unused.
        /// </summary>
        public void EnsureSubscribed()
        {
            if (m_Receiver == null)
                m_Receiver = GetComponent<AuraLslReceiver>();

            if (m_Receiver == null)
                return;

            // Removing first makes a second call harmless rather than double-subscribing.
            m_Receiver.sampleReceived -= OnSample;
            m_Receiver.sampleReceived += OnSample;

            if (m_Montage == null)
                m_Montage = AuraMontageConfig.CreateHumanVerifiedDefault();
        }

        void OnDisable()
        {
            if (m_Receiver != null)
                m_Receiver.sampleReceived -= OnSample;
        }

        /// <summary>
        /// Builds the filter and the filtered store from the stream's OWN metadata.
        ///
        /// Called on the first sample rather than at connect, so the rate and channel count are
        /// whatever the live stream actually advertised.
        /// </summary>
        void EnsureInitialised(int channelCount)
        {
            if (m_Filter != null && m_Filter.channelCount == channelCount)
                return;

            var fs = m_Receiver.metadata.nominalSrate;

            if (fs <= 0d || channelCount <= 0)
                return;

            m_Filter = new EegBandpassFilter(channelCount, fs, m_HighPassHz, m_LowPassHz,
                m_NotchEnabled, m_NotchHz, m_NotchQ);
            m_SettlingSamples = m_Filter.SettlingSamples;
            m_SamplesFiltered = 0;

            m_Filtered = new RawEegRingBuffer(channelCount, fs, m_FilteredHistorySeconds);
            m_FilterScratch = new double[channelCount];
            m_TypicalStep = new double[channelCount];
            m_TypicalRange = new double[channelCount];

            // Health history belongs to a filter configuration and a channel count. Rebuilding
            // the filter means the signal it is judging has changed, so carrying baselines across
            // would compare a channel against a differently-filtered version of itself.
            var labels = new string[channelCount];

            for (var c = 0; c < channelCount; c++)
            {
                labels[c] = m_Montage != null
                    ? m_Montage.LabelOfIndex(c)
                    : "CH" + (c + 1).ToString(CultureInfo.InvariantCulture);
            }

            m_Health = new EegChannelHealthTracker(channelCount, labels,
                m_QualityThresholds.Sanitised());

            Debug.Log($"[IKEA_EEG] EEG preprocessing online.\n{m_Filter.Describe()}\n" +
                      $"  settling: {m_SettlingSamples} samples " +
                      $"({m_SettlingSamples / fs:F2} s) before any window is analysed.");
        }

        /// <summary>
        /// One sample arrived. Filter it and store the RESULT separately from the raw.
        ///
        /// This is the only per-sample work in the pipeline. No FFT, no allocation beyond one
        /// small array per sample for the filtered store, and nothing logged.
        /// </summary>
        void OnSample(RawEegSample sample)
        {
            if (sample.channels == null)
                return;

            EnsureInitialised(sample.channels.Length);

            if (m_Filter == null)
                return;

            var filtered = new double[sample.channels.Length];

            for (var c = 0; c < sample.channels.Length; c++)
            {
                // The RAW value is read, never written.
                filtered[c] = m_Filter.Process(c, sample.channels[c]);

                // Running scale estimates, in the signal's own units, used later for
                // threshold-free quality checks.
                var step = Math.Abs(filtered[c] - m_FilterScratch[c]);
                m_TypicalStep[c] = m_TypicalStep[c] <= 0d
                    ? step
                    : m_TypicalStep[c] * 0.999 + step * 0.001;

                m_FilterScratch[c] = filtered[c];
            }

            m_SamplesFiltered++;

            // Indexed on the SAME analysis timestamp the raw buffer uses, so a window cut from
            // either lines up with the event log.
            m_Filtered.Add(sample.analysisTimestamp, filtered);
        }

        // ---------------------------------------------------------------------------------
        // Analysis
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Computes features over the most recent <paramref name="windowSeconds"/> of FILTERED
        /// signal.
        ///
        /// Runs at window cadence — never per sample. Returns a snapshot even when the window is
        /// unusable, with the reason in its flags: a researcher needs to see what was rejected,
        /// not an empty result.
        /// </summary>
        public LatestEegFeatures AnalyzeLatestWindow(double windowSeconds = 0d)
        {
            var seconds = windowSeconds > 0d ? windowSeconds : m_WindowSeconds;

            if (seconds < m_MinimumWindowSeconds)
                seconds = m_MinimumWindowSeconds;

            var features = new LatestEegFeatures
            {
                windowSeconds = seconds,
                filterReady = filterReady,
                sampleRateHz = m_Receiver != null ? m_Receiver.metadata.nominalSrate : 0d,
            };

            if (m_Montage != null)
            {
                features.amplitudeUnits = m_Montage.amplitudeUnitLabel;
                features.powerUnits = m_Montage.PowerUnitLabel;
                features.psdUnits = m_Montage.PowerSpectralDensityUnitLabel;
            }

            if (m_Filtered == null || m_Filter == null)
            {
                features.quality |= EegQualityFlags.InsufficientSamples;
                features.featureValidity = false;
                latest = features;
                return features;
            }

            // The settling rule: a window overlapping the transient is not signal.
            if (!filterReady)
                features.quality |= EegQualityFlags.FilterNotSettled;

            var stats = m_Filtered.GetStats();

            if (!m_Filtered.TryGetWindow(stats.latestTimestamp - seconds * 0.5,
                    seconds * 0.5, seconds * 0.5, out var window) || window.sampleCount == 0)
            {
                features.quality |= EegQualityFlags.InsufficientSamples;
                features.featureValidity = false;
                latest = features;
                return features;
            }

            features.windowStart = window.firstTimestamp;
            features.windowEnd = window.lastTimestamp;
            features.analysisTimestamp = window.lastTimestamp;
            features.sampleCount = window.sampleCount;
            features.channelCount = window.channelCount;

            var expected = (int)Math.Round(seconds * features.sampleRateHz);

            if (window.sampleCount < expected * 0.9)
                features.quality |= EegQualityFlags.InsufficientSamples;

            // Enough for at least one Welch segment?
            if (window.sampleCount < (int)(m_WelchSegmentSeconds * features.sampleRateHz))
            {
                features.quality |= EegQualityFlags.InsufficientSamples;
                features.featureValidity = false;
                latest = features;
                return features;
            }

            // ---- Per channel ----------------------------------------------------------------
            var channels = window.channelCount;
            features.thetaPerChannel = new double[channels];
            features.alphaPerChannel = new double[channels];
            features.channelLabels = new string[channels];

            var series = new double[window.sampleCount];

            for (var c = 0; c < channels; c++)
            {
                for (var i = 0; i < window.sampleCount; i++)
                    series[i] = window.samples[i][c];

                features.quality |= AssessChannel(series, c);
                features.channelLabels[c] = m_Montage != null
                    ? m_Montage.LabelOfIndex(c)
                    : $"CH{c + 1}";

                try
                {
                    var psd = EegSpectralAnalyzer.Welch(series, features.sampleRateHz,
                        m_WelchSegmentSeconds);

                    features.thetaPerChannel[c] = EegSpectralAnalyzer.BandPower(psd, EegBand.Theta);
                    features.alphaPerChannel[c] = EegSpectralAnalyzer.BandPower(psd, EegBand.Alpha);
                }
                catch (Exception e)
                {
                    features.thetaPerChannel[c] = double.NaN;
                    features.alphaPerChannel[c] = double.NaN;
                    features.quality |= EegQualityFlags.InsufficientSamples;

                    Debug.LogWarning($"[IKEA_EEG] PSD failed on channel {c}: {e.Message}");
                }
            }

            // ---- Between channels, not within one --------------------------------------------
            // Runs after the per-channel loop because it needs the band powers as well as the
            // samples. Labels are already resolved by this point, so the report names electrodes.
            features.quality |= AssessInterChannelIdentity(window, features);
            features.quality |= AssessChannelPowerOutliers(features);
            features.quality |= AssessTransients(window, features);

            // Channel-major once, shared by the similarity check and the health tracker.
            var byChannel = ToChannelMajor(window);

            features.quality |= AssessNearIdenticalChannels(byChannel, channels, features);

            // The only stateful check, and the only one that can see an electrode that WAS
            // working. Live path only — the diagnostic path must not rewrite session history.
            features.quality |= UpdateChannelHealth(byChannel, channels, features);

            // ---- ROI, resolved BY LABEL -------------------------------------------------------
            ComputeRoi(features);

            // An ROI is an AVERAGE over named electrodes. If one of those electrodes is the
            // flagged one, the ROI value carries the fault directly and is not merely adjacent
            // to it — so it must not be presented as a valid feature.
            AssessRoiContamination(features);

            // A degraded electrode revokes the ROI it belongs to, and only that one.
            ApplyChannelHealthToRoi(features);

            // ---- Derived, exploratory ---------------------------------------------------------
            if (features.roiValid && features.frontalTheta > 0d && features.posteriorAlpha > 0d)
            {
                features.thetaAlphaRatio = features.frontalTheta / features.posteriorAlpha;
                features.logThetaAlpha = Math.Log(features.frontalTheta) -
                                         Math.Log(features.posteriorAlpha);
                features.derivedValid = true;
            }

            // ---- Baseline, only if one was explicitly captured --------------------------------
            features.baselineAvailable = baselineAvailable;

            if (baselineAvailable && features.roiValid)
            {
                features.deltaThetaDb =
                    10.0 * Math.Log10(features.frontalTheta / m_BaselineFrontalTheta);
                features.deltaAlphaDb =
                    10.0 * Math.Log10(features.posteriorAlpha / m_BaselinePosteriorAlpha);
            }

            features.featureValidity = features.quality == EegQualityFlags.None &&
                                       features.roiValid;

            latest = features;
            return features;
        }

        /// <summary>
        /// Copies a window into per-channel arrays, which is the shape both the shared rules and
        /// the health tracker want.
        ///
        /// The window stores sample-major (one array per sample, indexed by channel); the rules
        /// need channel-major. Done once per window and reused by every check below rather than
        /// re-walked by each of them.
        /// </summary>
        static double[][] ToChannelMajor(EegWindow window)
        {
            var channels = window.channelCount;
            var byChannel = new double[channels][];

            for (var c = 0; c < channels; c++)
            {
                var series = new double[window.sampleCount];

                for (var i = 0; i < window.sampleCount; i++)
                    series[i] = window.samples[i][c];

                byChannel[c] = series;
            }

            return byChannel;
        }

        /// <summary>
        /// Flags channels that are almost, but not exactly, the same signal.
        ///
        /// STATELESS, so it runs on the diagnostic path as well as the live one. It reads the
        /// FILTERED window — which is what makes the correlation meaningful, since the shared DC
        /// offset and drift that would dominate a raw correlation are already gone.
        ///
        /// The highest correlation is recorded even when nothing is flagged: a recording sitting
        /// just under the threshold is exactly the case a researcher needs to see, and a check
        /// that only ever reports failures hides it.
        /// </summary>
        EegQualityFlags AssessNearIdenticalChannels(double[][] byChannel, int channelCount,
            LatestEegFeatures features)
        {
            features.nearIdenticalPairs = 0;
            features.nearIdenticalDetail = string.Empty;
            features.highestChannelCorrelation = double.NaN;

            if (byChannel == null || channelCount < 2)
                return EegQualityFlags.None;

            var thresholds = m_QualityThresholds.Sanitised();

            var pairs = EegChannelQualityRules.FindNearIdenticalPairs(byChannel, channelCount,
                thresholds.nearIdenticalCorrelation);

            // Highest correlation across ALL pairs, flagged or not.
            var highest = double.NaN;

            for (var a = 0; a < channelCount; a++)
            {
                for (var b = a + 1; b < channelCount; b++)
                {
                    var r = EegChannelQualityRules.Correlation(byChannel[a], byChannel[b]);

                    if (double.IsNaN(r))
                        continue;

                    var magnitude = Math.Abs(r);

                    if (double.IsNaN(highest) || magnitude > highest)
                        highest = magnitude;
                }
            }

            features.highestChannelCorrelation = highest;
            features.nearIdenticalPairs = pairs.Count;

            if (pairs.Count < thresholds.nearIdenticalMinimumPairs)
                return EegQualityFlags.None;

            var detail = new StringBuilder();

            detail.Append(pairs.Count)
                .Append(pairs.Count == 1 ? " channel pair" : " channel pairs")
                .Append(" correlate at or above ")
                .Append(thresholds.nearIdenticalCorrelation.ToString("F4", CultureInfo.InvariantCulture))
                .Append(" on filtered signal (");

            // Bounded: eight channels make 28 pairs and a log line naming all of them is
            // unreadable. The worst few identify the problem; the count above states its extent.
            pairs.Sort((x, y) => Math.Abs(y.correlation).CompareTo(Math.Abs(x.correlation)));

            var shown = Math.Min(4, pairs.Count);

            for (var i = 0; i < shown; i++)
            {
                if (i > 0)
                    detail.Append(", ");

                detail.Append(LabelOf(features, pairs[i].channelA))
                    .Append('~')
                    .Append(LabelOf(features, pairs[i].channelB))
                    .Append(" r=")
                    .Append(pairs[i].correlation.ToString("F5", CultureInfo.InvariantCulture));
            }

            if (pairs.Count > shown)
                detail.Append(", +").Append(pairs.Count - shown).Append(" more");

            detail.Append(')');

            features.nearIdenticalDetail = detail.ToString();
            return EegQualityFlags.NearIdenticalChannels;
        }

        /// <summary>
        /// Feeds this window to the channel health tracker and reports any state change.
        ///
        /// LIVE PATH ONLY. The diagnostic path deliberately does not call this: a tool inspecting
        /// an arbitrary past window must not rewrite the session's health history, and feeding
        /// windows out of order would corrupt the consecutive-window counters the whole state
        /// machine rests on.
        ///
        /// Windows already known to be unusable are not submitted. A window cut before the filter
        /// settled, or one short of samples, says nothing about an electrode, and letting it into
        /// the baselines would make the start of every session look like a fault.
        /// </summary>
        EegQualityFlags UpdateChannelHealth(double[][] byChannel, int channelCount,
            LatestEegFeatures features)
        {
            if (m_Health == null || byChannel == null || channelCount < 1)
                return EegQualityFlags.None;

            var unusable = (features.quality & (EegQualityFlags.FilterNotSettled |
                                                EegQualityFlags.InsufficientSamples)) != 0;

            if (!unusable)
            {
                var statsSet = ChannelStatsSet.Measure(byChannel, channelCount);

                var changes = m_Health.Submit(features.analysisTimestamp, statsSet,
                    features.thetaPerChannel, RoisContaining);

                foreach (var change in changes)
                {
                    // Printed as well as stored: the stored list lives only as long as the
                    // pipeline, while the player log outlives the run.
                    if (change.degraded)
                    {
                        Debug.LogWarning("[IKEA_EEG] EEG QC — " + change.Format());
                    }
                    else
                    {
                        Debug.Log("[IKEA_EEG] EEG QC — " + change.Format());
                    }
                }
            }

            var degraded = m_Health.DegradedChannels();
            features.degradedChannels = degraded;

            if (degraded.Length == 0)
            {
                features.degradedDetail = string.Empty;
                return EegQualityFlags.None;
            }

            var detail = new StringBuilder();

            for (var i = 0; i < degraded.Length; i++)
            {
                if (i > 0)
                    detail.Append("; ");

                var c = degraded[i];
                var since = m_Health.DegradedSince(c);

                detail.Append(LabelOf(features, c)).Append(" since ");

                detail.Append(double.IsNaN(since)
                    ? "unknown"
                    : since.ToString("F3", CultureInfo.InvariantCulture));

                detail.Append(" (").Append(m_Health.DetailFor(c)).Append(')');
            }

            features.degradedDetail = detail.ToString();
            return EegQualityFlags.ChannelDegraded;
        }

        /// <summary>
        /// The ROI names a channel participates in, as a readable list. Empty when it is in none.
        ///
        /// Used to stamp a health transition with the features it invalidates, so the log line
        /// answers "and what does that break" without the reader having to know the montage.
        /// </summary>
        string RoisContaining(int channel)
        {
            if (m_Montage == null)
                return string.Empty;

            var names = new List<string>();

            foreach (var roi in new[]
                     { AuraMontageConfig.FrontalThetaRoi, AuraMontageConfig.PosteriorAlphaRoi })
            {
                var indices = m_Montage.ResolveRoi(roi, out _);

                if (indices == null)
                    continue;

                foreach (var index in indices)
                {
                    if (index != channel)
                        continue;

                    names.Add(roi);
                    break;
                }
            }

            return string.Join(", ", names);
        }

        /// <summary>
        /// Invalidates an ROI feature whose electrodes include a degraded channel.
        ///
        /// PER ROI, not globally: when P3 fails, POSTERIOR_ALPHA is unusable and FRONTAL_THETA is
        /// untouched. Condemning both would discard a sound measurement; passing both would
        /// publish a poisoned one.
        ///
        /// NO SUBSTITUTION. The bad electrode is not dropped and the ROI is not re-averaged over
        /// the survivors: an ROI over two electrodes instead of three is a different measurement
        /// wearing the same name, and silently swapping one for the other is precisely the error
        /// the montage's all-or-nothing rule exists to prevent. The VALUE is still computed and
        /// still present — flagged, not deleted — so a researcher can see what was rejected.
        /// </summary>
        void ApplyChannelHealthToRoi(LatestEegFeatures features)
        {
            if (m_Montage == null || m_Health == null)
                return;

            if (!m_InvalidateRoiOnDegradedChannel)
                return;

            var degraded = features.degradedChannels;

            if (degraded == null || degraded.Length == 0)
                return;

            var degradedSet = new HashSet<int>(degraded);

            CheckRoi(features, AuraMontageConfig.FrontalThetaRoi, degradedSet,
                ref features.frontalThetaValid, ref features.frontalThetaProblem);

            CheckRoi(features, AuraMontageConfig.PosteriorAlphaRoi, degradedSet,
                ref features.posteriorAlphaValid, ref features.posteriorAlphaProblem);

            // The single question a consumer asks stays consistent with the two specific ones.
            features.roiValid = features.frontalThetaValid && features.posteriorAlphaValid;

            if (!features.roiValid && string.IsNullOrEmpty(features.roiProblem))
            {
                features.roiProblem = string.Join("; ", new[]
                    {
                        features.frontalThetaProblem, features.posteriorAlphaProblem,
                    })
                    .Trim(' ', ';');
            }
        }

        void CheckRoi(LatestEegFeatures features, string roi, HashSet<int> degraded,
            ref bool valid, ref string problem)
        {
            if (!valid)
                return;

            var indices = m_Montage.ResolveRoi(roi, out _);

            if (indices == null)
                return;

            foreach (var index in indices)
            {
                if (!degraded.Contains(index))
                    continue;

                valid = false;

                var since = m_Health.DegradedSince(index);

                problem = roi + " averages " + LabelOf(features, index) +
                          ", which has been degraded since t=" +
                          (double.IsNaN(since)
                              ? "unknown"
                              : since.ToString("F3", CultureInfo.InvariantCulture)) +
                          " — " + EegChannelQualityRules.Describe(m_Health.ReasonFor(index)) +
                          ". No electrode was substituted; the value is reported but must not " +
                          "be used.";

                return;
            }
        }

        void ComputeRoi(LatestEegFeatures features)
        {
            if (m_Montage == null)
            {
                features.roiValid = false;
                features.frontalThetaValid = false;
                features.posteriorAlphaValid = false;
                features.roiProblem = "no montage configured";
                features.quality |= EegQualityFlags.RoiUnresolved;
                return;
            }

            var frontal = m_Montage.ResolveRoi(AuraMontageConfig.FrontalThetaRoi, out var fp);
            var posterior = m_Montage.ResolveRoi(AuraMontageConfig.PosteriorAlphaRoi, out var pp);

            if (frontal == null || posterior == null)
            {
                features.roiValid = false;
                features.frontalThetaValid = false;
                features.posteriorAlphaValid = false;
                features.roiProblem = frontal == null ? fp : pp;
                features.quality |= EegQualityFlags.RoiUnresolved;
                return;
            }

            // All-or-nothing: an ROI averaged over fewer electrodes is a different measurement.
            foreach (var index in frontal)
            {
                if (index < 0 || index >= features.thetaPerChannel.Length)
                {
                    features.roiValid = false;
                    features.frontalThetaValid = false;
                    features.posteriorAlphaValid = false;
                    features.roiProblem = $"FRONTAL_THETA needs channel index {index}, which " +
                                          $"this {features.thetaPerChannel.Length}-channel " +
                                          "window does not contain";
                    features.quality |= EegQualityFlags.RoiUnresolved;
                    return;
                }
            }

            foreach (var index in posterior)
            {
                if (index < 0 || index >= features.alphaPerChannel.Length)
                {
                    features.roiValid = false;
                    features.frontalThetaValid = false;
                    features.posteriorAlphaValid = false;
                    features.roiProblem = $"POSTERIOR_ALPHA needs channel index {index}, which " +
                                          $"this {features.alphaPerChannel.Length}-channel " +
                                          "window does not contain";
                    features.quality |= EegQualityFlags.RoiUnresolved;
                    return;
                }
            }

            double theta = 0d, alpha = 0d;

            foreach (var index in frontal)
                theta += features.thetaPerChannel[index];

            foreach (var index in posterior)
                alpha += features.alphaPerChannel[index];

            features.frontalTheta = theta / frontal.Length;
            features.posteriorAlpha = alpha / posterior.Length;

            // Resolvable and computed. Channel health may still revoke either one below.
            features.frontalThetaValid = true;
            features.posteriorAlphaValid = true;
            features.roiValid = true;
        }

        /// <summary>
        /// Quality checks that use NO physical units.
        ///
        /// Every threshold here is relative — to the channel's own variation, or to a count.
        /// A "±100 µV" rule would be meaningless while the scaling of these float32 values is
        /// unverified, and would silently reject or accept the wrong windows.
        ///
        /// Nothing is repaired. A flagged window keeps its data.
        /// </summary>
        EegQualityFlags AssessChannel(double[] series, int channel)
        {
            var flags = EegQualityFlags.None;

            if (series.Length < 2)
                return EegQualityFlags.InsufficientSamples;

            double min = double.MaxValue, max = double.MinValue;
            var repeats = 0;
            var maxStep = 0d;
            double sumStep = 0d;

            for (var i = 0; i < series.Length; i++)
            {
                var v = series[i];

                if (double.IsNaN(v))
                    flags |= EegQualityFlags.NaNPresent;

                if (double.IsInfinity(v))
                    flags |= EegQualityFlags.InfinityPresent;

                if (double.IsNaN(v) || double.IsInfinity(v))
                    continue;

                min = Math.Min(min, v);
                max = Math.Max(max, v);

                if (i > 0)
                {
                    var step = Math.Abs(v - series[i - 1]);
                    sumStep += step;
                    maxStep = Math.Max(maxStep, step);

                    // Bit-identical consecutive values: a live amplifier essentially never
                    // repeats a float exactly, so a run of them means a stuck ADC or a rail.
                    if (v == series[i - 1])
                        repeats++;
                }
            }

            var range = max - min;

            if (range <= 0d)
                flags |= EegQualityFlags.Flatline;

            if (repeats > series.Length / 2)
                flags |= EegQualityFlags.SaturationLike;

            var meanStep = sumStep / Math.Max(1, series.Length - 1);

            // A single step an order of magnitude beyond this channel's own typical step is a
            // discontinuity, whatever the units happen to be.
            if (meanStep > 0d && maxStep > meanStep * 20.0)
                flags |= EegQualityFlags.AbruptDiscontinuity;

            // Compared against this channel's own recent history, not an absolute figure.
            if (m_TypicalRange[channel] <= 0d)
            {
                m_TypicalRange[channel] = range;
            }
            else
            {
                if (range > m_TypicalRange[channel] * 10.0)
                    flags |= EegQualityFlags.ExtremeDynamicRange;

                m_TypicalRange[channel] = m_TypicalRange[channel] * 0.9 + range * 0.1;
            }

            return flags;
        }

        /// <summary>
        /// The one check that needs to see every channel at once: are any of them the same
        /// signal?
        ///
        /// WHY A SEPARATE METHOD. <see cref="AssessChannel"/> is given one series and cannot,
        /// even in principle, notice that the series it saw last time was identical. Eight
        /// duplicated electrodes are eight individually flawless signals. The failure is only
        /// visible in the comparison, so the comparison has to exist on its own.
        ///
        /// TWO INDEPENDENT TESTS, because the collapse can happen at two different depths:
        ///
        ///   1. SAMPLES — a pair of channels bit-identical at every sample in the window. This
        ///      is the direct form: the same signal arriving twice.
        ///   2. BAND POWERS — every channel yielding exactly the same theta AND exactly the
        ///      same alpha. This catches collapses that survive de-meaning: Welch removes each
        ///      segment's mean, so channels differing ONLY by a constant offset produce
        ///      identical spectra while their samples are not identical at all. Test 1 alone
        ///      would miss that.
        ///
        /// EXACT EQUALITY, deliberately. No tolerance, no correlation threshold. Two electrodes
        /// that are merely very similar are a clinical judgement about common-mode signal and
        /// reference placement, and this class has no basis for making it while the amplitude
        /// scaling is unverified. Two electrodes that are bit-identical are an engineering fact.
        /// Flagging only the fact keeps this consistent with the rest of the quality checks,
        /// which are all relative or exact and none of them in µV.
        ///
        /// Nothing is repaired: the features stay, carrying the flag.
        /// </summary>
        EegQualityFlags AssessInterChannelIdentity(EegWindow window, LatestEegFeatures features)
        {
            var channels = window.channelCount;

            // A single channel has no pair to be identical to.
            if (channels < 2 || window.sampleCount < 1)
                return EegQualityFlags.None;

            var duplicates = new List<string>();

            for (var a = 0; a < channels; a++)
            {
                for (var b = a + 1; b < channels; b++)
                {
                    var identical = true;

                    for (var i = 0; i < window.sampleCount; i++)
                    {
                        // Exits on the first difference, so a healthy window costs one
                        // comparison per pair rather than one per sample.
                        if (window.samples[i][a] != window.samples[i][b])
                        {
                            identical = false;
                            break;
                        }
                    }

                    if (identical)
                        duplicates.Add($"{LabelOf(features, a)}={LabelOf(features, b)}");
                }
            }

            features.identicalChannelPairs = duplicates.Count;

            // Test 2: identical DERIVED features, even where the samples differed.
            var powersIdentical = true;

            for (var c = 1; c < channels && powersIdentical; c++)
            {
                if (features.thetaPerChannel[c] != features.thetaPerChannel[0] ||
                    features.alphaPerChannel[c] != features.alphaPerChannel[0])
                {
                    powersIdentical = false;
                }
            }

            // NaN needs no special case here. IEEE says NaN compares unequal to itself, so a
            // channel full of NaN can never be reported as identical to anything — including
            // another NaN channel — by either test above. That is the right outcome: NaNPresent
            // already describes such a window, and calling it a duplicate would be a second,
            // misleading claim about the same defect.

            if (duplicates.Count == 0 && !powersIdentical)
            {
                features.identicalChannelDetail = string.Empty;
                return EegQualityFlags.None;
            }

            var reason = new StringBuilder();

            if (duplicates.Count > 0)
            {
                reason.Append(duplicates.Count == 1 ? "1 channel pair" : $"{duplicates.Count} channel pairs");
                reason.Append(" bit-identical across the window (");
                reason.Append(string.Join(", ", duplicates));
                reason.Append(')');
            }

            if (powersIdentical)
            {
                if (reason.Length > 0)
                    reason.Append("; ");

                reason.Append($"all {channels} channels produced exactly the same theta and alpha");
            }

            features.identicalChannelDetail = reason.ToString();
            return EegQualityFlags.IdenticalChannels;
        }

        /// <summary>
        /// Decides whether a flagged channel actually sits INSIDE one of the reported ROIs.
        ///
        /// The distinction matters. A window in which Fp1 is broken but F3/Fz/F4 are clean still
        /// yields a sound Frontal Theta, because Fp1 is in no ROI. A window in which F3 is broken
        /// yields a Frontal Theta that is the average of two good electrodes and one ruined one —
        /// a contaminated number that would look perfectly plausible on a dashboard.
        ///
        /// Only membership is decided here. Nothing is dropped, substituted or re-averaged:
        /// silently averaging an ROI over the surviving electrodes would quietly change what the
        /// measurement means, and the montage's all-or-nothing rule exists to forbid exactly that.
        /// </summary>
        void AssessRoiContamination(LatestEegFeatures features)
        {
            features.roiContamination = string.Empty;

            if (m_Montage == null || !features.roiValid)
                return;

            // Only the checks that condemn an individual channel's VALUE make an ROI unsound.
            var severe = (features.quality & (EegQualityFlags.ChannelPowerOutlier |
                                              EegQualityFlags.IdenticalChannels |
                                              EegQualityFlags.TransientArtifactSuspected)) != 0;

            if (!severe)
                return;

            var suspect = new HashSet<int>(features.powerOutlierChannels ?? Array.Empty<int>());

            // A duplicated or non-stationary window condemns the channels collectively, so for
            // those flags ROI membership alone settles it.
            var wholeWindow = (features.quality & (EegQualityFlags.IdenticalChannels |
                                                   EegQualityFlags.TransientArtifactSuspected)) != 0;

            var contaminated = new List<string>();

            foreach (var roi in new[]
                     { AuraMontageConfig.FrontalThetaRoi, AuraMontageConfig.PosteriorAlphaRoi })
            {
                var indices = m_Montage.ResolveRoi(roi, out _);

                if (indices == null)
                    continue;

                foreach (var index in indices)
                {
                    if (suspect.Contains(index))
                    {
                        contaminated.Add($"{roi} averages flagged {LabelOf(features, index)}");
                        break;
                    }

                    if (wholeWindow)
                    {
                        contaminated.Add($"{roi} sits in a window flagged across channels");
                        break;
                    }
                }
            }

            if (contaminated.Count > 0)
                features.roiContamination = string.Join("; ", contaminated);
        }

        /// <summary>
        /// Flags channels whose band power sits orders of magnitude away from the rest of the
        /// same recording.
        ///
        /// WHY ROBUST STATISTICS. The failure this exists for produced one channel at 3.6E+07
        /// while its neighbours sat near 4E+01. A mean-and-standard-deviation test is useless
        /// there: the outlier dominates both the mean and the SD it would be compared against,
        /// so it inflates its own acceptance threshold and passes. The median and the median
        /// absolute deviation are determined by the bulk of the channels and do not move when a
        /// minority goes extreme.
        ///
        /// WHY LOG SPACE. Band power spans many decades and is strictly positive; "several
        /// orders of magnitude away" is a statement about ratios, so the comparison is done on
        /// log10(power). A channel is an outlier when it is more than
        /// <see cref="k_OutlierDecades"/> decades from the median — a plain, stated rule rather
        /// than a tuned constant.
        ///
        /// The MAD is reported but deliberately NOT used to scale the threshold: in a healthy
        /// 8-channel recording the MAD is tiny, and scaling by it would flag ordinary
        /// physiological variation between electrodes as a fault.
        /// </summary>
        EegQualityFlags AssessChannelPowerOutliers(LatestEegFeatures features)
        {
            var channels = features.channelCount;

            // With very few channels there is no "distribution of the others" to speak of.
            if (channels < k_MinimumChannelsForOutlier)
                return EegQualityFlags.None;

            var outliers = new List<int>();
            var report = new List<string>();

            // Theta and alpha are judged separately: a channel can be ruined in one band only.
            foreach (var band in new[] { "theta", "alpha" })
            {
                var powers = band == "theta" ? features.thetaPerChannel : features.alphaPerChannel;

                if (powers == null)
                    continue;

                var logs = new List<double>(channels);
                var index = new List<int>(channels);

                for (var c = 0; c < channels; c++)
                {
                    // Non-finite and non-positive powers are other flags' business, and log10
                    // has nothing to say about them.
                    if (double.IsNaN(powers[c]) || double.IsInfinity(powers[c]) || powers[c] <= 0d)
                        continue;

                    logs.Add(Math.Log10(powers[c]));
                    index.Add(c);
                }

                if (logs.Count < k_MinimumChannelsForOutlier)
                    continue;

                var median = Median(logs);

                for (var i = 0; i < logs.Count; i++)
                {
                    var decades = Math.Abs(logs[i] - median);

                    if (decades < k_OutlierDecades)
                        continue;

                    var channel = index[i];

                    if (!outliers.Contains(channel))
                        outliers.Add(channel);

                    report.Add($"{LabelOf(features, channel)} {band} {powers[channel]:E4} " +
                               $"= {decades:F1} decades from the {band} median " +
                               $"{Math.Pow(10.0, median):E4}");
                }
            }

            if (outliers.Count == 0)
            {
                features.powerOutlierChannels = Array.Empty<int>();
                features.powerOutlierDetail = string.Empty;
                return EegQualityFlags.None;
            }

            outliers.Sort();
            features.powerOutlierChannels = outliers.ToArray();
            features.powerOutlierDetail = string.Join("; ", report);

            return EegQualityFlags.ChannelPowerOutlier;
        }

        /// <summary>
        /// Flags channels that are not stationary across the window.
        ///
        /// Compares the RMS of the first third against the last third. A settling transient
        /// decays, so its RMS collapses; an electrode pop or a movement artefact arrives, so its
        /// RMS explodes. Either way the window is not describing steady EEG, and its PSD
        /// describes the excursion instead.
        ///
        /// This is the check that would have caught the original F3 reading at the moment it was
        /// produced, because a high-pass transient sitting on an enormous DC offset is a decaying
        /// signal, not a stationary one.
        ///
        /// SUSPECTED, not diagnosed: the ratio says the window is non-stationary, and says
        /// nothing about which physical cause did it. Naming a cause here would be a guess.
        /// The window is flagged and kept, never repaired.
        /// </summary>
        EegQualityFlags AssessTransients(EegWindow window, LatestEegFeatures features)
        {
            var third = window.sampleCount / 3;

            // Too short to have a "beginning" and an "end" worth comparing.
            if (third < 2)
                return EegQualityFlags.None;

            var report = new List<string>();

            for (var c = 0; c < window.channelCount; c++)
            {
                var firstRms = SegmentRms(window, c, 0, third);
                var lastRms = SegmentRms(window, c, window.sampleCount - third, third);

                if (double.IsNaN(firstRms) || double.IsNaN(lastRms))
                    continue;

                // Both quiet: nothing to compare, and Flatline already covers a dead channel.
                if (firstRms <= 0d || lastRms <= 0d)
                    continue;

                var ratio = Math.Max(firstRms / lastRms, lastRms / firstRms);

                if (ratio < k_TransientRmsRatio)
                    continue;

                var direction = firstRms > lastRms ? "decaying" : "growing";

                report.Add($"{LabelOf(features, c)} {direction} {ratio:F1}x " +
                           $"(first third RMS {firstRms:E3}, last third {lastRms:E3})");
            }

            if (report.Count == 0)
            {
                features.transientDetail = string.Empty;
                return EegQualityFlags.None;
            }

            features.transientDetail = string.Join("; ", report);
            return EegQualityFlags.TransientArtifactSuspected;
        }

        /// <summary>RMS of one channel over a slice of the window. NaN if nothing usable.</summary>
        static double SegmentRms(EegWindow window, int channel, int start, int count)
        {
            var sum = 0d;
            var used = 0;

            for (var i = start; i < start + count && i < window.sampleCount; i++)
            {
                var v = window.samples[i][channel];

                if (double.IsNaN(v) || double.IsInfinity(v))
                    continue;

                sum += v * v;
                used++;
            }

            return used > 0 ? Math.Sqrt(sum / used) : double.NaN;
        }

        /// <summary>
        /// Median of a list. Sorts a COPY — the caller's order is not disturbed.
        ///
        /// Public because the Editor assembly is compiled separately and the self-test pins the
        /// median-vs-mean behaviour directly; `internal` is not visible across that boundary.
        /// </summary>
        public static double Median(List<double> values)
        {
            if (values == null || values.Count == 0)
                return double.NaN;

            var sorted = new List<double>(values);
            sorted.Sort();

            var middle = sorted.Count / 2;

            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) * 0.5;
        }

        /// <summary>The montage label for a channel, falling back to its stream position.</summary>
        static string LabelOf(LatestEegFeatures features, int channel)
        {
            if (features.channelLabels != null &&
                channel < features.channelLabels.Length &&
                !string.IsNullOrEmpty(features.channelLabels[channel]))
            {
                return features.channelLabels[channel];
            }

            return $"CH{channel + 1}";
        }

        // ---------------------------------------------------------------------------------
        // Baseline
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Captures a baseline from an EXPLICIT interval.
        ///
        /// Never inferred. Silently taking the first seconds of a run would make every later
        /// dB value relative to whatever the participant happened to be doing while the headset
        /// settled — which is not a baseline, it is an accident.
        /// </summary>
        public bool CaptureBaseline(double startTimestamp, double endTimestamp, out string detail)
        {
            var seconds = endTimestamp - startTimestamp;

            if (seconds < MinimumBaselineSeconds)
            {
                detail = $"a baseline must be at least {MinimumBaselineSeconds:F0} s; " +
                         $"{seconds:F2} s was requested";
                m_BaselineDetail = detail;
                return false;
            }

            if (!filterReady)
            {
                detail = "the filter has not settled; a baseline taken now would be transient";
                m_BaselineDetail = detail;
                return false;
            }

            var centre = (startTimestamp + endTimestamp) * 0.5;
            var half = seconds * 0.5;

            if (!m_Filtered.TryGetWindow(centre, half, half, out var window) ||
                window.sampleCount < (int)(m_WelchSegmentSeconds * m_Receiver.metadata.nominalSrate))
            {
                detail = "the requested baseline interval is not (or no longer) in the buffer";
                m_BaselineDetail = detail;
                return false;
            }

            var saved = latest;

            var features = AnalyzeWindow(window, m_Receiver.metadata.nominalSrate);

            latest = saved;   // a baseline capture must not overwrite the live snapshot

            if (!features.roiValid)
            {
                detail = $"the baseline window has no valid ROI: {features.roiProblem}";
                m_BaselineDetail = detail;
                return false;
            }

            if (features.quality != EegQualityFlags.None)
            {
                detail = $"the baseline window is flagged ({features.QualityText()}); " +
                         "a contaminated baseline would distort every later dB value";
                m_BaselineDetail = detail;
                return false;
            }

            m_BaselineFrontalTheta = features.frontalTheta;
            m_BaselinePosteriorAlpha = features.posteriorAlpha;
            m_BaselineSeconds = seconds;

            detail = $"baseline captured over {seconds:F2} s: frontal theta " +
                     $"{m_BaselineFrontalTheta:E4}, posterior alpha {m_BaselinePosteriorAlpha:E4} " +
                     $"({features.powerUnits})";

            m_BaselineDetail = detail;
            return true;
        }

        /// <summary>Clears the baseline. Later dB values become unavailable again.</summary>
        public void ClearBaseline()
        {
            m_BaselineFrontalTheta = double.NaN;
            m_BaselinePosteriorAlpha = double.NaN;
            m_BaselineSeconds = 0d;
            m_BaselineDetail = "no baseline captured";
        }

        /// <summary>
        /// Analyses a caller-supplied window WITHOUT touching <see cref="latest"/>.
        ///
        /// Exists so a diagnostic can measure an arbitrary interval of the SAME buffer through
        /// the SAME code the live path uses — which is the only way to compare two moments of one
        /// recording and have the comparison mean anything. It deliberately does not publish its
        /// result: a diagnostic looking at the past must never overwrite the current snapshot
        /// that the monitor and any future consumer are reading.
        /// </summary>
        public LatestEegFeatures AnalyzeWindowForDiagnostics(EegWindow window)
        {
            var fs = m_Receiver != null ? m_Receiver.metadata.nominalSrate : 0d;
            return AnalyzeWindow(window, fs);
        }

        /// <summary>Analyses an already-extracted window. Shared by the live and baseline paths.</summary>
        LatestEegFeatures AnalyzeWindow(EegWindow window, double fs)
        {
            var features = new LatestEegFeatures
            {
                windowStart = window.firstTimestamp,
                windowEnd = window.lastTimestamp,
                analysisTimestamp = window.lastTimestamp,
                windowSeconds = window.lastTimestamp - window.firstTimestamp,
                sampleCount = window.sampleCount,
                channelCount = window.channelCount,
                sampleRateHz = fs,
                filterReady = filterReady,
                thetaPerChannel = new double[window.channelCount],
                alphaPerChannel = new double[window.channelCount],
                channelLabels = new string[window.channelCount],
            };

            if (m_Montage != null)
            {
                features.amplitudeUnits = m_Montage.amplitudeUnitLabel;
                features.powerUnits = m_Montage.PowerUnitLabel;
                features.psdUnits = m_Montage.PowerSpectralDensityUnitLabel;
            }

            var series = new double[window.sampleCount];

            for (var c = 0; c < window.channelCount; c++)
            {
                for (var i = 0; i < window.sampleCount; i++)
                    series[i] = window.samples[i][c];

                features.quality |= AssessChannel(series, c);
                features.channelLabels[c] = m_Montage != null
                    ? m_Montage.LabelOfIndex(c)
                    : $"CH{c + 1}";

                var psd = EegSpectralAnalyzer.Welch(series, fs, m_WelchSegmentSeconds);
                features.thetaPerChannel[c] = EegSpectralAnalyzer.BandPower(psd, EegBand.Theta);
                features.alphaPerChannel[c] = EegSpectralAnalyzer.BandPower(psd, EegBand.Alpha);
            }

            // Same cross-channel check as the live path. A baseline taken from duplicated
            // channels would otherwise become the reference every later dB value is measured
            // against — CaptureBaseline rejects any flagged window, so this closes that door too.
            features.quality |= AssessInterChannelIdentity(window, features);
            features.quality |= AssessChannelPowerOutliers(features);
            features.quality |= AssessTransients(window, features);

            // Stateless, so it is safe here. The health tracker deliberately is NOT updated: this
            // path exists to inspect an arbitrary past window, and feeding windows to the state
            // machine out of order would corrupt its consecutive-window counters and rewrite the
            // session's health history as a side effect of looking at it.
            features.quality |= AssessNearIdenticalChannels(
                ToChannelMajor(window), window.channelCount, features);

            ComputeRoi(features);
            AssessRoiContamination(features);

            // Reads the tracker's CURRENT state without advancing it, so a diagnostic window
            // still reports honestly on an electrode already known to be degraded.
            if (m_Health != null)
            {
                features.degradedChannels = m_Health.DegradedChannels();
                ApplyChannelHealthToRoi(features);
            }

            return features;
        }

        /// <summary>Builder/editor wiring.</summary>
        public void Configure(AuraMontageConfig montage, double highPassHz, double lowPassHz,
            double windowSeconds)
        {
            m_Montage = montage;
            m_HighPassHz = highPassHz;
            m_LowPassHz = lowPassHz;
            m_WindowSeconds = windowSeconds;
        }

        /// <summary>
        /// Turns the optional mains notch on or off, with its centre and Q.
        ///
        /// Separate from <see cref="Configure"/> so that enabling a notch is always a deliberate,
        /// visible act rather than something that rides along inside a general configuration
        /// call. Takes effect on the next filter construction: the filter is rebuilt here so the
        /// change applies immediately, which necessarily RESTARTS SETTLING — a filter with new
        /// coefficients has no valid state, and pretending otherwise would emit a transient as
        /// signal.
        /// </summary>
        public void ConfigureNotch(bool enabled, double notchHz = EegBandpassFilter.DefaultNotchHz,
            double notchQ = EegBandpassFilter.DefaultNotchQ)
        {
            m_NotchEnabled = enabled;
            m_NotchHz = notchHz;
            m_NotchQ = notchQ;

            // Drop the filter so EnsureInitialised rebuilds it with the new chain.
            m_Filter = null;
            m_SamplesFiltered = 0;
        }

        /// <summary>One-line description of the preprocessing, for reports.</summary>
        public string DescribePreprocessing()
        {
            var sb = new StringBuilder();

            if (m_NotchEnabled)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  HP {0:F2} Hz, LP {1:F2} Hz, notch ON at {2:F2} Hz (Q {3:F1}, " +
                    "bandwidth {4:F2} Hz) — OPTIONAL, redundant with the low-pass here",
                    m_HighPassHz, m_LowPassHz, m_NotchHz, m_NotchQ,
                    m_NotchQ > 0d ? m_NotchHz / m_NotchQ : 0d));
            }
            else
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  HP {0:F2} Hz, LP {1:F2} Hz, notch OFF ({2:F0} Hz sits outside the passband)",
                    m_HighPassHz, m_LowPassHz, m_NotchHz));
            }

            sb.AppendLine($"  filter settled: {filterReady} " +
                          $"({m_SamplesFiltered}/{m_SettlingSamples} samples, " +
                          $"{settlingSeconds:F2} s required)");

            if (m_Montage != null)
            {
                sb.AppendLine($"  acquisition notch " +
                              $"{(m_Montage.acquisitionNotchEnabled ? "ON" : "OFF")}, " +
                              $"bandpass {(m_Montage.acquisitionBandpassEnabled ? "ON" : "OFF")} " +
                              $"(source = {m_Montage.filterStateSource})");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
