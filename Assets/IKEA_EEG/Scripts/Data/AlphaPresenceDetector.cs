using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Configuration of the binary parietal-alpha detector. Every value is an ENGINEERING DECISION
    /// unless its comment names a publication, and even then the publication used it as a choice
    /// for its own data — none of these is a universal constant.
    ///
    /// WHAT THE OUTPUT MEANS, AND NOTHING MORE:
    ///   1  = a spectral peak inside the alpha search band rises above this window's own
    ///        aperiodic (1/f-like) background by more than the threshold, on enough posterior
    ///        channels;
    ///   0  = the window is valid and that criterion is not met ("not detectable under this
    ///        criterion" — NOT "no alpha in the brain");
    ///   empty = the window is invalid, with an explicit reason.
    /// No class is a statement about attention, relaxation, workload or cognitive state.
    ///
    /// STATUS IN THE PROJECT: an EXPERIMENTAL shadow-only feature, recorded beside the continuous
    /// alpha measures so conditions can be compared later. It is NOT the selected workload
    /// definition and is not among those under evaluation (theta/alpha ratio, frontal theta alone);
    /// it is NOT a workload label: nothing maps 1/0 to high or low load, and the workload estimator stays
    /// INDETERMINATE. An eyes-open/eyes-closed recording can verify the DETECTOR; validating any
    /// link to workload needs tasks with manipulated load, performance and perceived effort.
    /// </summary>
    [Serializable]
    public struct AlphaPresenceConfig
    {
        /// <summary>Compute the detector on every analysed window. Observational only.</summary>
        public bool enabled;

        /// <summary>Electrodes evaluated, by montage LABEL, comma separated.</summary>
        public string channels;

        /// <summary>
        /// Band searched for a peak, Hz. 7-13 Hz is the alpha search window W used by
        /// Corcoran et al. (2018, Psychophysiology, doi:10.1111/psyp.13064) for peak alpha
        /// frequency. Reused here as the search range, not as a detection threshold.
        /// </summary>
        public double searchLowHz;
        public double searchHighHz;

        /// <summary>Band whose absolute power is REPORTED (not used to decide). 8-12 Hz = EegBand.Alpha.</summary>
        public double powerLowHz;
        public double powerHighHz;

        /// <summary>
        /// Aperiodic fit range and the band excluded from it, Hz. Engineering: 3-30 Hz stays
        /// clear of the 1 Hz high-pass and 40 Hz low-pass roll-off of the project filter (and of
        /// the 60 Hz notch); 7-14 Hz is excluded so the alpha peak does not lift the background.
        /// The idea of fitting the aperiodic component in log-log space and measuring peaks
        /// above it follows Donoghue et al. (2020, Nat Neurosci, doi:10.1038/s41593-020-00744-x)
        /// in a deliberately simplified form (power law without knee, no Gaussian peak model).
        /// </summary>
        public double fitLowHz;
        public double fitHighHz;
        public double fitExcludeLowHz;
        public double fitExcludeHighHz;

        /// <summary>
        /// Second-pass robust fit: fit points whose residual exceeds this many robust SDs above
        /// the first fit (e.g. a beta peak) are dropped before refitting. Engineering.
        /// </summary>
        public double fitOutlierSd;

        /// <summary>
        /// Half-width, in bins, of the moving mean applied to the flattened spectrum before the
        /// peak is read (1 = 3 bins ≈ 1.5 Hz at the project's 0.49 Hz grid). Engineering; the
        /// smoothing-before-peak idea parallels the Savitzky-Golay step of Corcoran et al.
        /// </summary>
        public int smoothingHalfWidthBins;

        /// <summary>
        /// Relative threshold: peak height must exceed this many robust SDs of the flattened
        /// spectrum's fit residuals. The FORM (height in SD units of the aperiodic-removed
        /// spectrum) mirrors specparam's peak_threshold; the VALUE is calibrated by this project
        /// against synthetic aperiodic noise (target ≤ 5 % false detections per channel), see
        /// the alpha-presence report. It is not taken from any paper.
        /// </summary>
        public double thresholdSd;

        /// <summary>Absolute floor on peak height, log10 power units (0.1 = +26 %). Engineering.</summary>
        public double minPeakHeightLog10;

        /// <summary>Fewest valid channels for a window decision, and fewest detecting channels for a 1.</summary>
        public int minValidChannels;
        public int minDetectingChannels;

        /// <summary>Fewest Welch segments a channel PSD must average (variance guard). Engineering.</summary>
        public int minWelchSegments;

        /// <summary>
        /// Fewest valid fit points required for the aperiodic regression. Engineering.
        /// </summary>
        public int minFitPoints;

        /// <summary>
        /// 1.1.0 (2026-10-09): per-channel reasons for non-finite input, filter recovery and raw
        /// (pre-filter) saturation; raw columns appended to the CSV. Spectral method and
        /// thresholds unchanged from 1.0.0.
        /// </summary>
        public const string Version = "alpha-presence-1.1.0";

        public static AlphaPresenceConfig Default => new AlphaPresenceConfig
        {
            enabled = true,
            channels = "P3,Pz,P4",
            searchLowHz = 7.0,
            searchHighHz = 13.0,
            powerLowHz = 8.0,
            powerHighHz = 12.0,
            fitLowHz = 3.0,
            fitHighHz = 30.0,
            fitExcludeLowHz = 7.0,
            fitExcludeHighHz = 14.0,
            fitOutlierSd = 2.0,
            smoothingHalfWidthBins = 1,

            // CALIBRATED, 2026-10-08, on SYNTHETIC aperiodic noise only (no alpha): the smallest k,
            // in 0.1 steps, keeping per-channel false detections ≤ 5 % in the worst of 12 null
            // conditions (1/f^1, ^1.5, ^2, each with/without a 10 % white floor; 2- and 3-segment
            // Welch windows), seeds 1000-1011, through this project's filter and Welch. Worst case
            // at 2.3: 4.5 %. Fixed BEFORE any evaluation seed or recorded session was analysed.
            thresholdSd = 2.3,
            minPeakHeightLog10 = 0.1,
            minValidChannels = 2,
            minDetectingChannels = 2,
            minWelchSegments = 2,
            minFitPoints = 8,
        };

        /// <summary>Clamps nonsensical values back to the default so a bad inspector value cannot silently disable a check.</summary>
        public AlphaPresenceConfig Sanitised()
        {
            var d = Default;
            var c = this;

            if (string.IsNullOrWhiteSpace(c.channels)) c.channels = d.channels;
            if (!(c.searchLowHz > 0d) || !(c.searchHighHz > c.searchLowHz)) { c.searchLowHz = d.searchLowHz; c.searchHighHz = d.searchHighHz; }
            if (!(c.powerLowHz > 0d) || !(c.powerHighHz > c.powerLowHz)) { c.powerLowHz = d.powerLowHz; c.powerHighHz = d.powerHighHz; }
            if (!(c.fitLowHz > 0d) || !(c.fitHighHz > c.fitLowHz)) { c.fitLowHz = d.fitLowHz; c.fitHighHz = d.fitHighHz; }
            if (!(c.fitExcludeHighHz > c.fitExcludeLowHz)) { c.fitExcludeLowHz = d.fitExcludeLowHz; c.fitExcludeHighHz = d.fitExcludeHighHz; }
            if (!(c.fitOutlierSd > 0d)) c.fitOutlierSd = d.fitOutlierSd;
            if (c.smoothingHalfWidthBins < 0) c.smoothingHalfWidthBins = d.smoothingHalfWidthBins;
            if (!(c.thresholdSd > 0d)) c.thresholdSd = d.thresholdSd;
            if (!(c.minPeakHeightLog10 >= 0d)) c.minPeakHeightLog10 = d.minPeakHeightLog10;
            if (c.minValidChannels < 1) c.minValidChannels = d.minValidChannels;
            if (c.minDetectingChannels < 1) c.minDetectingChannels = d.minDetectingChannels;
            if (c.minWelchSegments < 1) c.minWelchSegments = d.minWelchSegments;
            if (c.minFitPoints < 3) c.minFitPoints = d.minFitPoints;

            return c;
        }

        public string[] ChannelLabels()
        {
            var parts = (channels ?? string.Empty).Split(',');
            var list = new List<string>();

            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }

            return list.ToArray();
        }

        /// <summary>One line, written into every output row, so a result carries its own configuration.</summary>
        public string Describe()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Format(c,
                "{0};channels={1};search={2}-{3}Hz;power={4}-{5}Hz;fit={6}-{7}Hz excl {8}-{9}Hz;" +
                "fitOutlierSd={10};smooth=±{11}bin;k={12};hmin={13}log10;minValid={14};minDetect={15};" +
                "minWelchSeg={16};minFitPts={17}",
                Version, channels.Replace(",", "/"), searchLowHz, searchHighHz, powerLowHz, powerHighHz,
                fitLowHz, fitHighHz, fitExcludeLowHz, fitExcludeHighHz, fitOutlierSd,
                smoothingHalfWidthBins, thresholdSd, minPeakHeightLog10, minValidChannels,
                minDetectingChannels, minWelchSegments, minFitPoints);
        }
    }

    /// <summary>The evidence ONE channel provides in ONE window. Computed before any aggregation.</summary>
    public sealed class AlphaChannelEvidence
    {
        public string label = string.Empty;
        public int channelIndex = -1;

        public bool valid;
        public string invalidReason = string.Empty;

        /// <summary>Absolute band power over powerLowHz-powerHighHz, signal units² (reported only).</summary>
        public double alphaPower = double.NaN;

        /// <summary>
        /// CONTINUOUS measure: mean height of the flattened spectrum (log10 PSD minus the aperiodic
        /// fit) across powerLowHz-powerHighHz. 0 = alpha band exactly at the background; positive =
        /// above it. Unlike absolute band power it is not driven by broadband or 1/f shifts. Reported
        /// for comparing conditions; it is not a workload measure by itself.
        /// </summary>
        public double alphaAboveAperiodicLog10 = double.NaN;

        /// <summary>Frequency of the strongest local maximum of the smoothed flattened spectrum inside the search band.</summary>
        public double peakFrequencyHz = double.NaN;

        /// <summary>Its height above the aperiodic fit, log10 power units (×10 = dB).</summary>
        public double peakHeightLog10 = double.NaN;

        /// <summary>Robust SD (1.4826·MAD) of the fit residuals, log10 units.</summary>
        public double noiseSdLog10 = double.NaN;

        /// <summary>max(thresholdSd·noiseSd, minPeakHeightLog10).</summary>
        public double thresholdLog10 = double.NaN;

        /// <summary>peakHeight / noiseSd — the evidence on the scale the threshold uses.</summary>
        public double peakZ = double.NaN;

        public double aperiodicOffset = double.NaN;
        public double aperiodicExponent = double.NaN;
        public double fitR2 = double.NaN;
        public int fitPoints;
        public int welchSegments;

        /// <summary>True when a local maximum exists in the search band (not at its edge bins).</summary>
        public bool hasLocalMaximum;

        public bool detected;

        /// <summary>Non-finite RAW samples of this channel in the window.</summary>
        public int rawNonFinite;

        /// <summary>Fraction of bit-identical consecutive RAW samples; NaN when the raw window was unavailable.</summary>
        public double rawRepeatFraction = double.NaN;

        /// <summary>RAW samples at a documented amplifier limit; -1 when the limits are undocumented (not checked).</summary>
        public int rawAtLimit = -1;
    }

    /// <summary>One window's binary result, with everything needed to audit it later.</summary>
    public sealed class AlphaPresenceResult
    {
        public double windowStart = double.NaN;
        public double windowEnd = double.NaN;
        public int sampleCount;

        public bool valid;

        /// <summary>1 or 0 when valid; null when invalid. Never a substituted 0.</summary>
        public int? result;

        public string invalidReason = string.Empty;

        public string[] channelsRequested = Array.Empty<string>();
        public AlphaChannelEvidence[] evidence = Array.Empty<AlphaChannelEvidence>();

        public int validChannels;
        public int detectingChannels;

        /// <summary>Median peak frequency of the DETECTING channels; NaN when none detect.</summary>
        public double aggregatePeakHz = double.NaN;

        public string configDescription = string.Empty;

        /// <summary>The amplifier-limit status the raw check ran under.</summary>
        public string amplifierLimitsStatus = string.Empty;

        public string ChannelsUsed()
        {
            var used = new List<string>();
            foreach (var e in evidence) if (e.valid) used.Add(e.label);
            return string.Join("/", used);
        }
    }

    /// <summary>
    /// Window-level context the pipeline already computed and the detector must respect. Read,
    /// never re-decided: the detector does not relax or tighten any existing quality rule.
    /// </summary>
    public struct AlphaWindowContext
    {
        public bool filterSettled;
        public bool insufficientSamples;
        public int[] degradedChannels;
        public int[] powerOutlierChannels;
        public int[] transientChannels;
        public EegQualityThresholds thresholds;

        /// <summary>Channels with a non-finite RAW sample inside the window.</summary>
        public int[] nonFiniteInputChannels;

        /// <summary>Channels whose filtered samples are quarantined (NaN) inside the window.</summary>
        public int[] filterQuarantineChannels;

        /// <summary>Raw (pre-filter) check per stream channel; null when unavailable.</summary>
        public RawChannelCheck[] rawChecks;

        /// <summary>The amplifier-limit status the raw check ran under, for the output row.</summary>
        public string amplifierLimitsStatus;
    }

    /// <summary>
    /// BINARY PARIETAL ALPHA DETECTION — a spectral peak over the window's own aperiodic
    /// background, per channel, then aggregated.
    ///
    /// PURE AND UNITY-FREE so the live pipeline, the offline replay and the synthetic tests run
    /// the identical code. It reuses the PSD the pipeline already computed for the window (no
    /// second filter, no second spectrum) and the existing per-channel quality rules
    /// (EegChannelQualityRules). It never repairs, re-references, interpolates or excludes data
    /// from anything else; its result changes no other feature, flag or decision.
    ///
    /// Presence is NOT "alpha power > 0": band power is always positive for any noisy signal.
    /// A window is 1 only when the alpha search band contains a local spectral maximum that
    /// stands above the power-law background fitted OUTSIDE that band, by more than the
    /// configured threshold, on enough channels.
    /// </summary>
    public static class AlphaPresenceDetector
    {
        /// <summary>The spectral evidence of one channel. Validity of the SIGNAL is decided by the caller.</summary>
        public static AlphaChannelEvidence EvaluateSpectrum(PsdResult psd, AlphaPresenceConfig config)
        {
            var cfg = config.Sanitised();
            var e = new AlphaChannelEvidence { valid = true };

            if (psd?.psd == null || psd.frequencies == null)
            {
                e.valid = false;
                e.invalidReason = "NO_SPECTRUM";
                return e;
            }

            e.welchSegments = psd.segmentCount;
            e.alphaPower = EegSpectralAnalyzer.BandPower(psd, cfg.powerLowHz, cfg.powerHighHz);

            if (psd.segmentCount < cfg.minWelchSegments)
            {
                e.valid = false;
                e.invalidReason = "TOO_FEW_WELCH_SEGMENTS(" + psd.segmentCount.ToString(CultureInfo.InvariantCulture) + ")";
                return e;
            }

            var f = psd.frequencies;
            var n = f.Length;
            var logP = new double[n];

            for (var k = 0; k < n; k++)
                logP[k] = psd.psd[k] > 0d ? Math.Log10(psd.psd[k]) : double.NaN;

            // ---- Aperiodic fit: log10 P = b + m·log10 f, outside the excluded band ----------
            var xs = new List<double>();
            var ys = new List<double>();

            for (var k = 0; k < n; k++)
            {
                if (f[k] < cfg.fitLowHz || f[k] > cfg.fitHighHz) continue;
                if (f[k] >= cfg.fitExcludeLowHz && f[k] <= cfg.fitExcludeHighHz) continue;
                if (double.IsNaN(logP[k]) || double.IsInfinity(logP[k])) continue;

                xs.Add(Math.Log10(f[k]));
                ys.Add(logP[k]);
            }

            if (xs.Count < cfg.minFitPoints || !Fit(xs, ys, out var b, out var m))
            {
                e.valid = false;
                e.invalidReason = "APERIODIC_FIT_FAILED";
                return e;
            }

            // Second pass: drop positive outliers (other peaks) and refit.
            var sd1 = RobustSd(Residuals(xs, ys, b, m));
            var xs2 = new List<double>();
            var ys2 = new List<double>();

            for (var i = 0; i < xs.Count; i++)
            {
                if (ys[i] - (b + m * xs[i]) > cfg.fitOutlierSd * sd1) continue;
                xs2.Add(xs[i]);
                ys2.Add(ys[i]);
            }

            if (xs2.Count >= cfg.minFitPoints && Fit(xs2, ys2, out var b2, out var m2))
            {
                b = b2;
                m = m2;
                xs = xs2;
                ys = ys2;
            }

            var residuals = Residuals(xs, ys, b, m);
            var sd = RobustSd(residuals);

            e.fitPoints = xs.Count;
            e.aperiodicOffset = b;
            e.aperiodicExponent = -m;
            e.fitR2 = R2(ys, residuals);
            e.noiseSdLog10 = sd;

            if (!(sd > 0d) || double.IsNaN(sd) || double.IsInfinity(sd))
            {
                e.valid = false;
                e.invalidReason = "DEGENERATE_SPECTRUM";
                return e;
            }

            // ---- Flattened, smoothed spectrum in the search band ----------------------------
            var flat = new double[n];
            for (var k = 0; k < n; k++)
                flat[k] = f[k] > 0d ? logP[k] - (b + m * Math.Log10(f[k])) : double.NaN;

            double bandSum = 0d;
            var bandCount = 0;
            for (var k = 0; k < n; k++)
            {
                if (f[k] < cfg.powerLowHz || f[k] > cfg.powerHighHz || double.IsNaN(flat[k])) continue;
                bandSum += flat[k];
                bandCount++;
            }
            e.alphaAboveAperiodicLog10 = bandCount > 0 ? bandSum / bandCount : double.NaN;

            var h = cfg.smoothingHalfWidthBins;
            var smooth = new double[n];

            for (var k = 0; k < n; k++)
            {
                double s = 0d;
                var c = 0;

                for (var j = Math.Max(1, k - h); j <= Math.Min(n - 1, k + h); j++)
                {
                    if (double.IsNaN(flat[j])) continue;
                    s += flat[j];
                    c++;
                }

                smooth[k] = c > 0 ? s / c : double.NaN;
            }

            var lo = -1;
            var hi = -1;

            for (var k = 0; k < n; k++)
            {
                if (f[k] < cfg.searchLowHz || f[k] > cfg.searchHighHz) continue;
                if (lo < 0) lo = k;
                hi = k;
            }

            var best = -1;

            if (lo >= 0)
            {
                // Strictly interior local maxima: a maximum on the band edge is the slope of
                // something outside the band, not a peak inside it.
                for (var k = lo + 1; k <= hi - 1; k++)
                {
                    if (!(smooth[k] > smooth[k - 1]) || !(smooth[k] >= smooth[k + 1])) continue;
                    if (best < 0 || smooth[k] > smooth[best]) best = k;
                }
            }

            e.thresholdLog10 = Math.Max(cfg.thresholdSd * sd, cfg.minPeakHeightLog10);

            if (best < 0)
            {
                e.hasLocalMaximum = false;
                e.detected = false;
                return e;
            }

            e.hasLocalMaximum = true;
            e.peakFrequencyHz = f[best];
            e.peakHeightLog10 = smooth[best];
            e.peakZ = smooth[best] / sd;
            e.detected = smooth[best] > e.thresholdLog10;
            return e;
        }

        /// <summary>
        /// The full window decision. <paramref name="channelsBySample"/> is the FILTERED window,
        /// channel-major, exactly as the pipeline analysed it; <paramref name="psds"/> are the
        /// pipeline's own Welch spectra of those channels (index = stream channel).
        /// </summary>
        public static AlphaPresenceResult Evaluate(AlphaPresenceConfig config, string[] streamLabels,
            double[][] channelsBySample, PsdResult[] psds, AlphaWindowContext context,
            double windowStart, double windowEnd, int sampleCount)
        {
            var cfg = config.Sanitised();
            var requested = cfg.ChannelLabels();

            var r = new AlphaPresenceResult
            {
                windowStart = windowStart,
                windowEnd = windowEnd,
                sampleCount = sampleCount,
                channelsRequested = requested,
                configDescription = cfg.Describe(),
                amplifierLimitsStatus = context.amplifierLimitsStatus ?? string.Empty,
            };

            // ---- Window-level gates, in the order a reader would check them -----------------
            if (!context.filterSettled)
                return Invalid(r, "FILTER_NOT_SETTLED");

            if (context.insufficientSamples)
                return Invalid(r, "INSUFFICIENT_SAMPLES");

            if (channelsBySample == null || psds == null)
                return Invalid(r, "NO_WINDOW_DATA");

            var evidence = new AlphaChannelEvidence[requested.Length];
            var thresholds = context.thresholds.Sanitised();

            for (var i = 0; i < requested.Length; i++)
            {
                var index = IndexOf(streamLabels, requested[i]);

                if (index < 0 || index >= channelsBySample.Length || index >= psds.Length)
                {
                    evidence[i] = new AlphaChannelEvidence
                    {
                        label = requested[i], valid = false, invalidReason = "CHANNEL_NOT_IN_MONTAGE",
                    };
                    continue;
                }

                // Signal validity first, from the SHARED per-channel rules the pipeline uses.
                // What happened BEFORE the filter is named first, because it explains the rest:
                // a non-finite input also leaves NaN in the filtered window.
                var stats = EegChannelQualityRules.Measure(channelsBySample[index]);
                var qc = EegChannelQualityRules.EvaluateWindow(stats, thresholds);
                var raw = context.rawChecks != null && index < context.rawChecks.Length
                    ? context.rawChecks[index]
                    : default;
                string reason = null;

                if (Contains(context.nonFiniteInputChannels, index))
                    reason = "CHANNEL_NONFINITE_INPUT";
                else if (Contains(context.filterQuarantineChannels, index))
                    reason = "CHANNEL_FILTER_RECOVERY";
                else if (raw.atAmplifierLimit)
                    reason = "CHANNEL_RAW_AT_AMPLIFIER_LIMIT";
                else if (raw.stuck)
                    reason = "CHANNEL_RAW_STUCK_VALUE";
                else if (qc != EegDegradationReason.None)
                    reason = "CHANNEL_QC(" + EegChannelQualityRules.Describe(qc) + ")";
                else if (Contains(context.degradedChannels, index))
                    reason = "CHANNEL_DEGRADED";
                else if (Contains(context.powerOutlierChannels, index))
                    reason = "CHANNEL_POWER_OUTLIER";
                else if (Contains(context.transientChannels, index))
                    reason = "CHANNEL_TRANSIENT";

                var e = EvaluateSpectrum(psds[index], cfg);
                e.label = requested[i];
                e.channelIndex = index;
                e.rawNonFinite = raw.nonFinite;
                e.rawRepeatFraction = context.rawChecks != null ? raw.repeatFraction : double.NaN;
                e.rawAtLimit = raw.limitsChecked ? raw.atLimit : -1;

                if (reason != null)
                {
                    // The spectral numbers stay visible; the channel just cannot vote.
                    e.valid = false;
                    e.invalidReason = reason;
                    e.detected = false;
                }

                evidence[i] = e;
            }

            r.evidence = evidence;

            // ---- Independence of the evidence: the n-of-m vote assumes it ---------------------
            for (var a = 0; a < evidence.Length; a++)
            {
                for (var b = a + 1; b < evidence.Length; b++)
                {
                    if (!evidence[a].valid || !evidence[b].valid) continue;

                    var rho = EegChannelQualityRules.Correlation(
                        channelsBySample[evidence[a].channelIndex], channelsBySample[evidence[b].channelIndex]);

                    if (!double.IsNaN(rho) && Math.Abs(rho) >= thresholds.nearIdenticalCorrelation)
                    {
                        CountVotes(r);
                        return Invalid(r, "CHANNELS_NOT_INDEPENDENT(" + evidence[a].label + "~" +
                                          evidence[b].label + " r=" +
                                          rho.ToString("F4", CultureInfo.InvariantCulture) + ")");
                    }
                }
            }

            CountVotes(r);

            if (r.validChannels < cfg.minValidChannels)
            {
                return Invalid(r, "TOO_FEW_VALID_CHANNELS(" +
                                  r.validChannels.ToString(CultureInfo.InvariantCulture) + "<" +
                                  cfg.minValidChannels.ToString(CultureInfo.InvariantCulture) + ")");
            }

            r.valid = true;
            r.result = r.detectingChannels >= cfg.minDetectingChannels ? 1 : 0;

            var freqs = new List<double>();
            foreach (var e in evidence)
                if (e.valid && e.detected) freqs.Add(e.peakFrequencyHz);

            r.aggregatePeakHz = freqs.Count > 0 ? EegChannelQualityRules.Median(freqs) : double.NaN;
            return r;
        }

        /// <summary>A result for a window that never reached the spectra (early pipeline exits).</summary>
        public static AlphaPresenceResult InvalidWindow(AlphaPresenceConfig config, string reason,
            double windowStart, double windowEnd, int sampleCount)
        {
            var cfg = config.Sanitised();
            return Invalid(new AlphaPresenceResult
            {
                windowStart = windowStart, windowEnd = windowEnd, sampleCount = sampleCount,
                channelsRequested = cfg.ChannelLabels(), configDescription = cfg.Describe(),
            }, reason);
        }

        // ---------------------------------------------------------------------------------
        // CSV — one row per window. Shared by the live sink and the offline tools.
        // ---------------------------------------------------------------------------------

        static readonly string[] k_ChannelColumns =
        {
            "valid", "reason", "alpha_power", "alpha_above_aperiodic_log10", "peak_hz", "peak_height_log10", "noise_sd_log10",
            "threshold_log10", "peak_z", "aperiodic_offset", "aperiodic_exponent", "fit_r2", "welch_segments", "detected",
        };

        public static string CsvHeader(AlphaPresenceConfig config)
        {
            var sb = new StringBuilder(
                "window_index,lsl_start,lsl_end,n_samples,pipeline_quality,alpha_result,invalid_reason," +
                "channels_requested,channels_used,valid_channels,detecting_channels,aggregate_peak_hz");

            foreach (var label in config.Sanitised().ChannelLabels())
                foreach (var col in k_ChannelColumns)
                    sb.Append(',').Append(label).Append('_').Append(col);

            sb.Append(",config");

            // 1.1.0: appended after config so every earlier column keeps its position.
            sb.Append(",amplifier_limits");

            foreach (var label in config.Sanitised().ChannelLabels())
                foreach (var col in k_RawColumns)
                    sb.Append(',').Append(label).Append('_').Append(col);

            return sb.ToString();
        }

        static readonly string[] k_RawColumns = { "raw_nonfinite", "raw_repeat_fraction", "raw_at_limit" };

        public static string ToCsvRow(long windowIndex, AlphaPresenceResult r, string pipelineQuality)
        {
            var c = CultureInfo.InvariantCulture;
            string N(double v, string fmt = "R") => double.IsNaN(v) || double.IsInfinity(v) ? string.Empty : v.ToString(fmt, c);
            string Q(string s) => "\"" + (s ?? string.Empty).Replace("\"", "\"\"") + "\"";

            var sb = new StringBuilder();
            sb.Append(windowIndex.ToString(c)).Append(',')
              .Append(N(r.windowStart)).Append(',').Append(N(r.windowEnd)).Append(',')
              .Append(r.sampleCount.ToString(c)).Append(',')
              .Append(Q(pipelineQuality)).Append(',')
              .Append(r.result.HasValue ? r.result.Value.ToString(c) : string.Empty).Append(',')
              .Append(Q(r.invalidReason)).Append(',')
              .Append(string.Join("/", r.channelsRequested)).Append(',')
              .Append(r.ChannelsUsed()).Append(',')
              .Append(r.validChannels.ToString(c)).Append(',')
              .Append(r.detectingChannels.ToString(c)).Append(',')
              .Append(N(r.aggregatePeakHz));

            foreach (var label in r.channelsRequested)
            {
                AlphaChannelEvidence e = null;
                foreach (var x in r.evidence) if (x.label == label) { e = x; break; }

                if (e == null)
                {
                    for (var i = 0; i < k_ChannelColumns.Length; i++) sb.Append(',');
                    continue;
                }

                sb.Append(',').Append(e.valid ? "1" : "0")
                  .Append(',').Append(Q(e.invalidReason))
                  .Append(',').Append(N(e.alphaPower))
                  .Append(',').Append(N(e.alphaAboveAperiodicLog10))
                  .Append(',').Append(N(e.peakFrequencyHz))
                  .Append(',').Append(N(e.peakHeightLog10))
                  .Append(',').Append(N(e.noiseSdLog10))
                  .Append(',').Append(N(e.thresholdLog10))
                  .Append(',').Append(N(e.peakZ))
                  .Append(',').Append(N(e.aperiodicOffset))
                  .Append(',').Append(N(e.aperiodicExponent))
                  .Append(',').Append(N(e.fitR2))
                  .Append(',').Append(e.welchSegments.ToString(c))
                  .Append(',').Append(e.detected ? "1" : "0");
            }

            sb.Append(',').Append(Q(r.configDescription));
            sb.Append(',').Append(Q(r.amplifierLimitsStatus));

            foreach (var label in r.channelsRequested)
            {
                AlphaChannelEvidence e = null;
                foreach (var x in r.evidence) if (x.label == label) { e = x; break; }

                if (e == null || e.channelIndex < 0)
                {
                    for (var i = 0; i < k_RawColumns.Length; i++) sb.Append(',');
                    continue;
                }

                sb.Append(',').Append(e.rawNonFinite.ToString(c))
                  .Append(',').Append(N(e.rawRepeatFraction))
                  .Append(',').Append(e.rawAtLimit >= 0 ? e.rawAtLimit.ToString(c) : string.Empty);
            }

            return sb.ToString();
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

        static AlphaPresenceResult Invalid(AlphaPresenceResult r, string reason)
        {
            r.valid = false;
            r.result = null;
            r.invalidReason = reason;
            r.aggregatePeakHz = double.NaN;
            return r;
        }

        static void CountVotes(AlphaPresenceResult r)
        {
            r.validChannels = 0;
            r.detectingChannels = 0;

            foreach (var e in r.evidence)
            {
                if (!e.valid) continue;
                r.validChannels++;
                if (e.detected) r.detectingChannels++;
            }
        }

        static int IndexOf(string[] labels, string label)
        {
            if (labels == null) return -1;

            for (var i = 0; i < labels.Length; i++)
                if (string.Equals(labels[i], label, StringComparison.OrdinalIgnoreCase)) return i;

            return -1;
        }

        static bool Contains(int[] set, int value)
        {
            if (set == null) return false;
            foreach (var v in set) if (v == value) return true;
            return false;
        }

        static bool Fit(List<double> xs, List<double> ys, out double intercept, out double slope)
        {
            intercept = slope = double.NaN;
            var n = xs.Count;
            if (n < 2) return false;

            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (var i = 0; i < n; i++) { sx += xs[i]; sy += ys[i]; sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i]; }

            var den = n * sxx - sx * sx;
            if (!(Math.Abs(den) > 1e-12)) return false;

            slope = (n * sxy - sx * sy) / den;
            intercept = (sy - slope * sx) / n;
            return !double.IsNaN(slope) && !double.IsInfinity(slope);
        }

        static List<double> Residuals(List<double> xs, List<double> ys, double b, double m)
        {
            var r = new List<double>(xs.Count);
            for (var i = 0; i < xs.Count; i++) r.Add(ys[i] - (b + m * xs[i]));
            return r;
        }

        static double RobustSd(List<double> values)
        {
            if (values.Count == 0) return double.NaN;
            var med = EegChannelQualityRules.Median(values);
            var dev = new List<double>(values.Count);
            foreach (var v in values) dev.Add(Math.Abs(v - med));
            return 1.4826 * EegChannelQualityRules.Median(dev);
        }

        static double R2(List<double> ys, List<double> residuals)
        {
            double mean = 0;
            foreach (var y in ys) mean += y;
            mean /= ys.Count;

            double ssTot = 0, ssRes = 0;
            for (var i = 0; i < ys.Count; i++) { ssTot += (ys[i] - mean) * (ys[i] - mean); ssRes += residuals[i] * residuals[i]; }
            return ssTot > 0 ? 1.0 - ssRes / ssTot : double.NaN;
        }
    }
}
