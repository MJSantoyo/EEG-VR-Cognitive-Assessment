using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Engineering thresholds for the cross-channel and degradation quality checks.
    ///
    /// EVERY VALUE HERE IS AN ENGINEERING HEURISTIC, NOT A SCIENTIFIC CONSTANT. None of them is
    /// derived from a published method, none is in microvolts, and none asserts anything about
    /// physiology. They are all RELATIVE — a correlation, a ratio, or a count of windows — which
    /// is what lets them mean the same thing while AURA's amplitude scaling stays unverified.
    ///
    /// They are gathered in one struct so they can be serialized on the live pipeline, passed to
    /// the offline analyzer, and printed in a report. A threshold that cannot be read back from
    /// the output it produced is a threshold nobody can check.
    ///
    /// CHANGING ONE CHANGES WHAT GETS FLAGGED. Do not tune these to make a session look clean.
    /// </summary>
    [Serializable]
    public struct EegQualityThresholds
    {
        /// <summary>
        /// Pearson correlation, on FILTERED samples, above which two channels are called
        /// near-identical.
        ///
        /// WHY 0.99 AND WHY IT IS NOT A SCIENTIFIC CLAIM. Independent scalp electrodes share
        /// common-mode signal and reference, so they correlate — often strongly. What this
        /// number encodes is only that above it, the two traces carry so little independent
        /// variance that treating them as separate measurements is an engineering error
        /// regardless of what the brain was doing. It says a recording is suspect; it does not
        /// say why, and it does not say the data is wrong.
        ///
        /// MUST BE APPLIED TO FILTERED DATA. On raw samples a shared DC offset and slow drift
        /// dominate the correlation and almost any pair clears any threshold, which would make
        /// this fire constantly and mean nothing.
        /// </summary>
        public double nearIdenticalCorrelation;

        /// <summary>
        /// How many channel pairs must exceed <see cref="nearIdenticalCorrelation"/> before the
        /// window is flagged. 1 pair is enough by default: two duplicated inputs is already a
        /// fault worth surfacing.
        /// </summary>
        public int nearIdenticalMinimumPairs;

        /// <summary>
        /// How far, in decades of band power, a channel may drift from ITS OWN established
        /// baseline before that window counts as abnormal.
        ///
        /// Against the channel's own history rather than its neighbours', because that is what
        /// makes this a DEGRADATION test rather than a second outlier test: an electrode that
        /// was fine for two minutes and then moved is abnormal relative to itself even if the
        /// whole cap is noisy.
        /// </summary>
        public double degradationDecades;

        /// <summary>
        /// How many CONSECUTIVE abnormal windows before a channel is declared degraded.
        ///
        /// More than one, so a single blink or a one-off pop does not condemn an electrode for
        /// the rest of a session. This is the "prolonged" in prolonged abnormal power.
        /// </summary>
        public int degradationConsecutiveWindows;

        /// <summary>
        /// How many times its own established range a window's peak-to-peak may reach before it
        /// counts as a sudden large amplitude excursion — the headset-shifts-an-electrode case.
        /// </summary>
        public double excursionRangeRatio;

        /// <summary>
        /// How far a window's range may COLLAPSE, as a fraction of the channel's established
        /// range, before it counts as loss of normal variability. A lead that has come off
        /// often goes quiet rather than loud.
        /// </summary>
        public double variabilityCollapseRatio;

        /// <summary>
        /// Fraction of samples that may repeat the previous value bit-for-bit before the window
        /// is called clipped or stuck. Matches the SaturationLike rule this consolidates.
        /// </summary>
        public double saturationRepeatFraction;

        /// <summary>
        /// A single step this many times the channel's own mean step is a discontinuity.
        /// Matches the AbruptDiscontinuity rule this consolidates.
        /// </summary>
        public double discontinuityStepRatio;

        /// <summary>
        /// How many clean windows a channel must produce before its own baseline is considered
        /// established. Until then no degradation verdict is issued — there is nothing to be
        /// abnormal RELATIVE TO, and judging against an unformed baseline would flag the start
        /// of every session.
        /// </summary>
        public int baselineWindows;

        /// <summary>
        /// How many consecutive clean windows return a degraded channel to healthy.
        ///
        /// Larger than <see cref="degradationConsecutiveWindows"/> on purpose: it should be
        /// harder to declare a channel recovered than to declare it broken, because a
        /// prematurely cleared channel silently re-enters the ROI averages.
        /// </summary>
        public int recoveryWindows;

        /// <summary>
        /// The defaults. Every one is documented on its field above; none is tuned to any
        /// particular recording.
        /// </summary>
        public static EegQualityThresholds Default => new EegQualityThresholds
        {
            nearIdenticalCorrelation = 0.99,
            nearIdenticalMinimumPairs = 1,
            degradationDecades = 2.0,
            degradationConsecutiveWindows = 3,
            excursionRangeRatio = 8.0,
            variabilityCollapseRatio = 0.1,
            saturationRepeatFraction = 0.5,
            discontinuityStepRatio = 20.0,
            baselineWindows = 5,
            recoveryWindows = 5,
        };

        /// <summary>Clamps anything nonsensical to the default, so a bad inspector value cannot disable a check silently.</summary>
        public EegQualityThresholds Sanitised()
        {
            var d = Default;
            var t = this;

            if (!(t.nearIdenticalCorrelation > 0d) || t.nearIdenticalCorrelation > 1d)
                t.nearIdenticalCorrelation = d.nearIdenticalCorrelation;

            if (t.nearIdenticalMinimumPairs < 1)
                t.nearIdenticalMinimumPairs = d.nearIdenticalMinimumPairs;

            if (!(t.degradationDecades > 0d))
                t.degradationDecades = d.degradationDecades;

            if (t.degradationConsecutiveWindows < 1)
                t.degradationConsecutiveWindows = d.degradationConsecutiveWindows;

            if (!(t.excursionRangeRatio > 1d))
                t.excursionRangeRatio = d.excursionRangeRatio;

            if (!(t.variabilityCollapseRatio > 0d) || t.variabilityCollapseRatio >= 1d)
                t.variabilityCollapseRatio = d.variabilityCollapseRatio;

            if (!(t.saturationRepeatFraction > 0d) || t.saturationRepeatFraction > 1d)
                t.saturationRepeatFraction = d.saturationRepeatFraction;

            if (!(t.discontinuityStepRatio > 1d))
                t.discontinuityStepRatio = d.discontinuityStepRatio;

            if (t.baselineWindows < 1)
                t.baselineWindows = d.baselineWindows;

            if (t.recoveryWindows < 1)
                t.recoveryWindows = d.recoveryWindows;

            return t;
        }

        public string Describe()
        {
            var t = Sanitised();

            return string.Format(CultureInfo.InvariantCulture,
                "near-identity r>{0:F4} on >={1} pair(s); degradation {2:F2} decades from the " +
                "channel's own baseline for {3} consecutive window(s); excursion {4:F1}x own " +
                "range; variability collapse below {5:F2}x own range; saturation {6:P0} repeats; " +
                "discontinuity {7:F0}x mean step; baseline after {8} clean window(s); recovery " +
                "after {9} clean window(s)",
                t.nearIdenticalCorrelation, t.nearIdenticalMinimumPairs, t.degradationDecades,
                t.degradationConsecutiveWindows, t.excursionRangeRatio,
                t.variabilityCollapseRatio, t.saturationRepeatFraction, t.discontinuityStepRatio,
                t.baselineWindows, t.recoveryWindows);
        }
    }

    /// <summary>Why a channel was called degraded. Flags, so several can apply at once.</summary>
    [Flags]
    public enum EegDegradationReason
    {
        None = 0,

        /// <summary>Peak-to-peak far beyond the channel's own established range.</summary>
        AmplitudeExcursion = 1 << 0,

        /// <summary>Band power far from the channel's own established baseline, for several windows.</summary>
        ProlongedAbnormalPower = 1 << 1,

        /// <summary>Values repeating bit-for-bit — a stuck ADC or a rail.</summary>
        Saturation = 1 << 2,

        /// <summary>Range collapsed far below the channel's own norm — a lead that has gone quiet.</summary>
        VariabilityLoss = 1 << 3,

        /// <summary>A single step far beyond the channel's own mean step.</summary>
        Discontinuity = 1 << 4,

        /// <summary>The channel does not vary at all.</summary>
        Flatline = 1 << 5,

        /// <summary>NaN or infinity present.</summary>
        NonFinite = 1 << 6,
    }

    /// <summary>
    /// Stateless measurements of one channel over one window, and the rules that read them.
    ///
    /// PURE AND UNITY-FREE, deliberately. This is the one place the per-channel and cross-channel
    /// rules live, so the live pipeline and the offline replay apply the SAME rule rather than two
    /// implementations that could drift apart — which is exactly what happened before this class
    /// existed, when the offline tool carried its own copy of the flat/saturation rules.
    ///
    /// It measures and judges. It never repairs, interpolates, re-references or excludes.
    /// </summary>
    public static class EegChannelQualityRules
    {
        /// <summary>What one window says about one channel, in the signal's own units.</summary>
        public struct ChannelStats
        {
            public double min;
            public double max;
            public double range;
            public double mean;
            public double rms;
            public double meanStep;
            public double maxStep;
            public int maxStepIndex;
            public int repeats;
            public int nonFinite;
            public int sampleCount;

            public double repeatFraction =>
                sampleCount > 1 ? (double)repeats / (sampleCount - 1) : 0d;

            public double stepRatio => meanStep > 0d ? maxStep / meanStep : 0d;
        }

        /// <summary>Measures one channel over a window. No thresholds are applied here.</summary>
        public static ChannelStats Measure(IReadOnlyList<double> series)
        {
            var stats = new ChannelStats
            {
                min = double.MaxValue,
                max = double.MinValue,
                maxStepIndex = -1,
            };

            if (series == null || series.Count == 0)
            {
                stats.min = 0d;
                stats.max = 0d;
                return stats;
            }

            stats.sampleCount = series.Count;

            double sum = 0d, sumSq = 0d, sumStep = 0d;
            var steps = 0;
            var finite = 0;
            var previousFinite = double.NaN;

            for (var i = 0; i < series.Count; i++)
            {
                var v = series[i];

                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    stats.nonFinite++;

                    // A non-finite sample breaks the step chain rather than producing a
                    // meaningless step against it.
                    previousFinite = double.NaN;
                    continue;
                }

                if (v < stats.min) stats.min = v;
                if (v > stats.max) stats.max = v;

                sum += v;
                sumSq += v * v;
                finite++;

                if (!double.IsNaN(previousFinite))
                {
                    if (v == previousFinite)
                        stats.repeats++;

                    var step = Math.Abs(v - previousFinite);
                    sumStep += step;
                    steps++;

                    if (step > stats.maxStep)
                    {
                        stats.maxStep = step;
                        stats.maxStepIndex = i;
                    }
                }

                previousFinite = v;
            }

            if (finite == 0)
            {
                stats.min = 0d;
                stats.max = 0d;
                return stats;
            }

            stats.range = stats.max - stats.min;
            stats.mean = sum / finite;
            stats.rms = Math.Sqrt(sumSq / finite);
            stats.meanStep = steps > 0 ? sumStep / steps : 0d;

            return stats;
        }

        /// <summary>
        /// Pearson correlation of two equal-length series, ignoring index pairs where either is
        /// non-finite.
        ///
        /// Returns NaN when either series is constant: a flat channel has no variance to
        /// correlate, and reporting 0 or 1 there would be an invented answer. Callers must treat
        /// NaN as "no verdict", not as "not similar".
        /// </summary>
        public static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
        {
            if (a == null || b == null)
                return double.NaN;

            var n = Math.Min(a.Count, b.Count);

            if (n < 2)
                return double.NaN;

            double sumA = 0d, sumB = 0d;
            var used = 0;

            for (var i = 0; i < n; i++)
            {
                if (double.IsNaN(a[i]) || double.IsInfinity(a[i]) ||
                    double.IsNaN(b[i]) || double.IsInfinity(b[i]))
                {
                    continue;
                }

                sumA += a[i];
                sumB += b[i];
                used++;
            }

            if (used < 2)
                return double.NaN;

            var meanA = sumA / used;
            var meanB = sumB / used;

            double sab = 0d, saa = 0d, sbb = 0d;

            for (var i = 0; i < n; i++)
            {
                if (double.IsNaN(a[i]) || double.IsInfinity(a[i]) ||
                    double.IsNaN(b[i]) || double.IsInfinity(b[i]))
                {
                    continue;
                }

                var da = a[i] - meanA;
                var db = b[i] - meanB;

                sab += da * db;
                saa += da * da;
                sbb += db * db;
            }

            if (saa <= 0d || sbb <= 0d)
                return double.NaN;

            var r = sab / Math.Sqrt(saa * sbb);

            // Floating-point round-off can push a perfect correlation a hair past 1.
            if (r > 1d) r = 1d;
            if (r < -1d) r = -1d;

            return r;
        }

        /// <summary>One near-identical pair and the correlation that made it one.</summary>
        public struct SimilarPair
        {
            public int channelA;
            public int channelB;
            public double correlation;
        }

        /// <summary>
        /// Finds channel pairs whose FILTERED traces correlate above the threshold.
        ///
        /// ABSOLUTE correlation is used, so an inverted duplicate — the same signal referenced
        /// the other way round — is caught too. A pair whose correlation is NaN (a flat channel)
        /// is skipped rather than counted: Flatline already describes that fault, and calling it
        /// a duplicate as well would be a second, wrong claim about the same defect.
        ///
        /// THE INPUT MUST BE FILTERED. See <see cref="EegQualityThresholds.nearIdenticalCorrelation"/>.
        /// </summary>
        public static List<SimilarPair> FindNearIdenticalPairs(double[][] channelsBySample,
            int channelCount, double threshold)
        {
            var pairs = new List<SimilarPair>();

            if (channelsBySample == null || channelCount < 2)
                return pairs;

            for (var a = 0; a < channelCount; a++)
            {
                for (var b = a + 1; b < channelCount; b++)
                {
                    var r = Correlation(channelsBySample[a], channelsBySample[b]);

                    if (double.IsNaN(r))
                        continue;

                    if (Math.Abs(r) >= threshold)
                    {
                        pairs.Add(new SimilarPair
                        {
                            channelA = a,
                            channelB = b,
                            correlation = r,
                        });
                    }
                }
            }

            return pairs;
        }

        /// <summary>
        /// The degradation reasons visible from ONE window alone, with no history.
        ///
        /// The history-dependent reasons — excursion against the channel's own range, prolonged
        /// abnormal power, variability loss — need a baseline and therefore live in
        /// <see cref="EegChannelHealthTracker"/>. Splitting them this way keeps this function
        /// pure and testable against a single synthetic window.
        /// </summary>
        public static EegDegradationReason EvaluateWindow(ChannelStats stats,
            EegQualityThresholds thresholds)
        {
            var reason = EegDegradationReason.None;

            if (stats.sampleCount < 2)
                return reason;

            if (stats.nonFinite > 0)
                reason |= EegDegradationReason.NonFinite;

            if (stats.range <= 0d)
                reason |= EegDegradationReason.Flatline;

            if (stats.repeatFraction > thresholds.saturationRepeatFraction)
                reason |= EegDegradationReason.Saturation;

            if (stats.meanStep > 0d && stats.stepRatio > thresholds.discontinuityStepRatio)
                reason |= EegDegradationReason.Discontinuity;

            return reason;
        }

        /// <summary>Spells a reason set out in words, for a log line or a report cell.</summary>
        public static string Describe(EegDegradationReason reason)
        {
            if (reason == EegDegradationReason.None)
                return "none";

            var parts = new List<string>();

            if ((reason & EegDegradationReason.AmplitudeExcursion) != 0)
                parts.Add("amplitude excursion");

            if ((reason & EegDegradationReason.ProlongedAbnormalPower) != 0)
                parts.Add("prolonged abnormal power");

            if ((reason & EegDegradationReason.Saturation) != 0)
                parts.Add("saturation/clipping");

            if ((reason & EegDegradationReason.VariabilityLoss) != 0)
                parts.Add("loss of variability");

            if ((reason & EegDegradationReason.Discontinuity) != 0)
                parts.Add("abrupt discontinuity");

            if ((reason & EegDegradationReason.Flatline) != 0)
                parts.Add("flatline");

            if ((reason & EegDegradationReason.NonFinite) != 0)
                parts.Add("non-finite samples");

            return string.Join(" + ", parts);
        }

        /// <summary>Median of a list. Sorts a copy, so the caller's list is untouched.</summary>
        public static double Median(List<double> values)
        {
            if (values == null || values.Count == 0)
                return double.NaN;

            var copy = new List<double>(values);
            copy.Sort();

            var mid = copy.Count / 2;

            return copy.Count % 2 == 1
                ? copy[mid]
                : 0.5 * (copy[mid - 1] + copy[mid]);
        }
    }

    /// <summary>
    /// One recorded change in a channel's health, with everything needed to reconstruct it later.
    ///
    /// Exists because a boolean "P3 is bad" answers none of the questions a researcher actually
    /// has afterwards: WHEN did it start, WHAT went wrong, WHICH derived feature is therefore
    /// unusable, and did it come back. A flag on the current window cannot answer any of those
    /// once the window has moved on.
    /// </summary>
    public class EegQcTransition
    {
        /// <summary>Analysis-clock timestamp of the window that produced the change.</summary>
        public double timestamp;

        public int channelIndex;
        public string electrode = string.Empty;

        /// <summary>True when the channel became degraded; false when it recovered.</summary>
        public bool degraded;

        public EegDegradationReason reason;

        /// <summary>Human-readable reason, including the measured values that triggered it.</summary>
        public string detail = string.Empty;

        /// <summary>ROI features this channel participates in, and which are therefore affected.</summary>
        public string affectedRois = string.Empty;

        public string Format()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "t={0:F3} {1} (ch{2}) {3}: {4}{5}",
                timestamp, electrode, channelIndex + 1,
                degraded ? "DEGRADED" : "RECOVERED",
                detail,
                string.IsNullOrEmpty(affectedRois) ? "" : " | affects " + affectedRois);
        }
    }

    /// <summary>
    /// Tracks each channel's health across a session and decides when one has degraded or
    /// recovered.
    ///
    /// WHY THIS IS STATEFUL AND WINDOW-BY-WINDOW. The failure it exists for is an electrode that
    /// is fine for two minutes and then shifts when the headset moves. Nothing about a single
    /// window can see that: the window after the shift is only "unusual" relative to the windows
    /// before it. So each channel accumulates its OWN baseline from its own clean windows, and is
    /// then judged against that rather than against its neighbours or an absolute number.
    ///
    /// A channel must fail for several consecutive windows before it is declared degraded, and
    /// must pass for more consecutive windows before it is declared recovered. The asymmetry is
    /// deliberate: a prematurely cleared channel silently re-enters the ROI averages, which is
    /// the failure mode this whole class exists to prevent.
    ///
    /// UNITY-FREE, so the offline replay can run the identical state machine over a recorded file
    /// and reconstruct exactly what the live session would have flagged.
    ///
    /// It never excludes a channel from anything, never interpolates and never substitutes. It
    /// reports a state; acting on that state is the caller's decision.
    /// </summary>
    public class EegChannelHealthTracker
    {
        readonly int m_ChannelCount;
        readonly string[] m_Labels;
        EegQualityThresholds m_Thresholds;

        // Per channel, the clean-window history that forms its own baseline.
        readonly List<double>[] m_RangeHistory;
        readonly List<double>[] m_PowerHistory;

        readonly double[] m_BaselineRange;
        readonly double[] m_BaselineLogPower;
        readonly bool[] m_BaselineReady;

        readonly int[] m_ConsecutiveBad;
        readonly int[] m_ConsecutiveGood;
        readonly bool[] m_Degraded;
        readonly EegDegradationReason[] m_Reason;
        readonly double[] m_DegradedSince;
        readonly string[] m_Detail;

        readonly List<EegQcTransition> m_Transitions = new List<EegQcTransition>();

        /// <summary>
        /// How many clean windows are kept per channel when forming a baseline. Bounded so a
        /// long session does not grow these lists without limit, and so the baseline stays a
        /// description of the RECENT past rather than of the whole run.
        /// </summary>
        const int k_MaxHistory = 60;

        public EegChannelHealthTracker(int channelCount, string[] labels,
            EegQualityThresholds thresholds)
        {
            m_ChannelCount = Math.Max(0, channelCount);
            m_Thresholds = thresholds.Sanitised();

            m_Labels = new string[m_ChannelCount];

            for (var c = 0; c < m_ChannelCount; c++)
            {
                m_Labels[c] = labels != null && c < labels.Length && !string.IsNullOrEmpty(labels[c])
                    ? labels[c]
                    : "CH" + (c + 1).ToString(CultureInfo.InvariantCulture);
            }

            m_RangeHistory = new List<double>[m_ChannelCount];
            m_PowerHistory = new List<double>[m_ChannelCount];

            for (var c = 0; c < m_ChannelCount; c++)
            {
                m_RangeHistory[c] = new List<double>();
                m_PowerHistory[c] = new List<double>();
            }

            m_BaselineRange = new double[m_ChannelCount];
            m_BaselineLogPower = new double[m_ChannelCount];
            m_BaselineReady = new bool[m_ChannelCount];
            m_ConsecutiveBad = new int[m_ChannelCount];
            m_ConsecutiveGood = new int[m_ChannelCount];
            m_Degraded = new bool[m_ChannelCount];
            m_Reason = new EegDegradationReason[m_ChannelCount];
            m_DegradedSince = new double[m_ChannelCount];
            m_Detail = new string[m_ChannelCount];

            for (var c = 0; c < m_ChannelCount; c++)
            {
                m_DegradedSince[c] = double.NaN;
                m_Detail[c] = string.Empty;
            }
        }

        public int channelCount => m_ChannelCount;
        public EegQualityThresholds thresholds => m_Thresholds;

        /// <summary>Every health change so far, oldest first.</summary>
        public IReadOnlyList<EegQcTransition> transitions => m_Transitions;

        public bool IsDegraded(int channel) =>
            channel >= 0 && channel < m_ChannelCount && m_Degraded[channel];

        public EegDegradationReason ReasonFor(int channel) =>
            channel >= 0 && channel < m_ChannelCount
                ? m_Reason[channel]
                : EegDegradationReason.None;

        public double DegradedSince(int channel) =>
            channel >= 0 && channel < m_ChannelCount ? m_DegradedSince[channel] : double.NaN;

        public string DetailFor(int channel) =>
            channel >= 0 && channel < m_ChannelCount ? m_Detail[channel] : string.Empty;

        public string LabelOf(int channel) =>
            channel >= 0 && channel < m_ChannelCount ? m_Labels[channel] : string.Empty;

        /// <summary>Indices of every currently degraded channel.</summary>
        public int[] DegradedChannels()
        {
            var list = new List<int>();

            for (var c = 0; c < m_ChannelCount; c++)
            {
                if (m_Degraded[c])
                    list.Add(c);
            }

            return list.ToArray();
        }

        /// <summary>
        /// Feeds one analysed window in and returns any health changes it caused.
        ///
        /// <paramref name="bandPower"/> is a per-channel power for the band the caller cares
        /// about; theta is used by both callers, but nothing here depends on which band it is —
        /// only on it being the SAME band every time, so a channel is compared against its own
        /// like-for-like history.
        ///
        /// Windows the caller already knows are unusable (filter not settled, a timing gap) must
        /// not be submitted: they would poison the baselines with numbers that describe the
        /// recording rather than the electrode.
        /// </summary>
        public List<EegQcTransition> Submit(double timestamp, ChannelStatsSet windowStats,
            double[] bandPower, Func<int, string> roiLookup)
        {
            var changes = new List<EegQcTransition>();

            if (windowStats.stats == null)
                return changes;

            for (var c = 0; c < m_ChannelCount; c++)
            {
                if (c >= windowStats.stats.Length)
                    break;

                var stats = windowStats.stats[c];

                // ---- Reasons visible from this window alone ------------------------------
                var reason = EegChannelQualityRules.EvaluateWindow(stats, m_Thresholds);

                var power = bandPower != null && c < bandPower.Length ? bandPower[c] : double.NaN;
                var logPower = power > 0d && !double.IsNaN(power) ? Math.Log10(power) : double.NaN;

                // ---- Reasons that need the channel's own history -------------------------
                if (m_BaselineReady[c])
                {
                    if (m_BaselineRange[c] > 0d)
                    {
                        var ratio = stats.range / m_BaselineRange[c];

                        if (ratio > m_Thresholds.excursionRangeRatio)
                            reason |= EegDegradationReason.AmplitudeExcursion;

                        // Flatline already covers a dead channel; this is the softer case of a
                        // channel that still moves but far less than it used to.
                        if (stats.range > 0d && ratio < m_Thresholds.variabilityCollapseRatio)
                            reason |= EegDegradationReason.VariabilityLoss;
                    }

                    if (!double.IsNaN(logPower) && !double.IsNaN(m_BaselineLogPower[c]) &&
                        Math.Abs(logPower - m_BaselineLogPower[c]) > m_Thresholds.degradationDecades)
                    {
                        reason |= EegDegradationReason.ProlongedAbnormalPower;
                    }
                }

                var bad = reason != EegDegradationReason.None;

                if (bad)
                {
                    m_ConsecutiveBad[c]++;
                    m_ConsecutiveGood[c] = 0;
                }
                else
                {
                    m_ConsecutiveGood[c]++;
                    m_ConsecutiveBad[c] = 0;

                    // Only CLEAN windows form the baseline. Letting a bad window in would raise
                    // the very threshold that is supposed to catch it — the same mistake the
                    // power-outlier check avoids by using a median.
                    Remember(m_RangeHistory[c], stats.range);

                    if (!double.IsNaN(logPower))
                        Remember(m_PowerHistory[c], logPower);

                    if (!m_BaselineReady[c] &&
                        m_RangeHistory[c].Count >= m_Thresholds.baselineWindows)
                    {
                        m_BaselineRange[c] = EegChannelQualityRules.Median(m_RangeHistory[c]);
                        m_BaselineLogPower[c] = m_PowerHistory[c].Count > 0
                            ? EegChannelQualityRules.Median(m_PowerHistory[c])
                            : double.NaN;
                        m_BaselineReady[c] = true;
                    }
                    else if (m_BaselineReady[c])
                    {
                        // Track slow, legitimate change without letting a fault redefine normal:
                        // the baseline only ever moves on clean windows.
                        m_BaselineRange[c] = EegChannelQualityRules.Median(m_RangeHistory[c]);

                        if (m_PowerHistory[c].Count > 0)
                            m_BaselineLogPower[c] = EegChannelQualityRules.Median(m_PowerHistory[c]);
                    }
                }

                // ---- State transitions ---------------------------------------------------
                if (!m_Degraded[c] && bad &&
                    m_ConsecutiveBad[c] >= m_Thresholds.degradationConsecutiveWindows)
                {
                    m_Degraded[c] = true;
                    m_Reason[c] = reason;

                    // Dated to the FIRST bad window, not the one that crossed the count. The
                    // electrode failed when it started failing; the delay is only how long this
                    // rule waited before being sure.
                    m_DegradedSince[c] = timestamp;

                    m_Detail[c] = BuildDetail(stats, logPower, c, reason);

                    var transition = new EegQcTransition
                    {
                        timestamp = timestamp,
                        channelIndex = c,
                        electrode = m_Labels[c],
                        degraded = true,
                        reason = reason,
                        detail = m_Detail[c],
                        affectedRois = roiLookup != null ? roiLookup(c) : string.Empty,
                    };

                    m_Transitions.Add(transition);
                    changes.Add(transition);
                }
                else if (m_Degraded[c] && !bad &&
                         m_ConsecutiveGood[c] >= m_Thresholds.recoveryWindows)
                {
                    m_Degraded[c] = false;
                    m_Reason[c] = EegDegradationReason.None;
                    m_Detail[c] = string.Empty;

                    var transition = new EegQcTransition
                    {
                        timestamp = timestamp,
                        channelIndex = c,
                        electrode = m_Labels[c],
                        degraded = false,
                        reason = EegDegradationReason.None,
                        detail = "returned to its own baseline for " +
                                 m_Thresholds.recoveryWindows.ToString(CultureInfo.InvariantCulture) +
                                 " consecutive window(s)",
                        affectedRois = roiLookup != null ? roiLookup(c) : string.Empty,
                    };

                    m_Transitions.Add(transition);
                    changes.Add(transition);

                    m_DegradedSince[c] = double.NaN;
                }
                else if (m_Degraded[c] && bad)
                {
                    // Still broken, possibly for a new reason as well. Accumulated rather than
                    // replaced, so the record keeps everything that went wrong.
                    m_Reason[c] |= reason;
                }
            }

            return changes;
        }

        string BuildDetail(EegChannelQualityRules.ChannelStats stats, double logPower, int channel,
            EegDegradationReason reason)
        {
            var text = new StringBuilder();

            text.Append(EegChannelQualityRules.Describe(reason));
            text.Append(" [range ").Append(stats.range.ToString("G4", CultureInfo.InvariantCulture));

            if (m_BaselineReady[channel] && m_BaselineRange[channel] > 0d)
            {
                text.Append(" vs baseline ")
                    .Append(m_BaselineRange[channel].ToString("G4", CultureInfo.InvariantCulture))
                    .Append(" (")
                    .Append((stats.range / m_BaselineRange[channel])
                        .ToString("F2", CultureInfo.InvariantCulture))
                    .Append("x)");
            }

            if (!double.IsNaN(logPower))
            {
                text.Append("; log10 power ")
                    .Append(logPower.ToString("F2", CultureInfo.InvariantCulture));

                if (m_BaselineReady[channel] && !double.IsNaN(m_BaselineLogPower[channel]))
                {
                    text.Append(" vs baseline ")
                        .Append(m_BaselineLogPower[channel].ToString("F2", CultureInfo.InvariantCulture))
                        .Append(" (")
                        .Append((logPower - m_BaselineLogPower[channel])
                            .ToString("+0.00;-0.00", CultureInfo.InvariantCulture))
                        .Append(" decades)");
                }
            }

            if (stats.meanStep > 0d)
            {
                text.Append("; max step ")
                    .Append(stats.stepRatio.ToString("F1", CultureInfo.InvariantCulture))
                    .Append("x mean");
            }

            text.Append(']');
            return text.ToString();
        }

        static void Remember(List<double> history, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            history.Add(value);

            if (history.Count > k_MaxHistory)
                history.RemoveAt(0);
        }
    }

    /// <summary>
    /// The per-channel measurements of one window, bundled so a caller measures once and both the
    /// window checks and the health tracker read the same numbers.
    /// </summary>
    public struct ChannelStatsSet
    {
        public EegChannelQualityRules.ChannelStats[] stats;

        public static ChannelStatsSet Measure(double[][] channelsBySample, int channelCount)
        {
            var set = new ChannelStatsSet
            {
                stats = new EegChannelQualityRules.ChannelStats[Math.Max(0, channelCount)],
            };

            if (channelsBySample == null)
                return set;

            for (var c = 0; c < channelCount && c < channelsBySample.Length; c++)
                set.stats[c] = EegChannelQualityRules.Measure(channelsBySample[c]);

            return set;
        }
    }
}
