using System;

namespace IkeaEeg.Data
{
    /// <summary>
    /// The uniform time base used for event-aligned EEG analysis.
    ///
    /// WHY THIS EXISTS — measured, not assumed. A live 8 s capture showed AURA delivering
    /// perfectly regular samples (median interval exactly 4.000 ms at an advertised 250 Hz)
    /// inside 9-sample chunks whose ANCHOR timestamps jitter: every anomaly fell on an index
    /// that was a multiple of 9, with backward steps to −30 ms and gaps to +62 ms. The sample
    /// VALUES are sound; only the timestamps attached to chunk boundaries are disturbed.
    ///
    /// WHAT IT DOES: emits a uniform grid — each sample exactly one interval after the last —
    /// whose interval is steered by a sliding least-squares fit of (sample index, raw local
    /// timestamp). The grid is what makes the series strictly increasing BY CONSTRUCTION; the
    /// fit is what keeps it locked to the real sample clock instead of free-running. This is liblsl's own documented approach for
    /// regularly-sampled streams — proc_dejitter is described in the installed binding as "a
    /// smoothing algorithm [applied] to the received time stamps" that needs "a minimum number
    /// of samples ... until the remaining jitter is consistently below 1 ms". It is implemented
    /// here rather than switched on at the inlet for one reason: proc_dejitter REPLACES the
    /// timestamp pull_sample returns, which would destroy the raw remote value that this
    /// project must keep as provenance and that the continuity diagnostic needs in order to
    /// characterise the sender. Doing the same fit alongside keeps all three timestamps.
    ///
    /// WHAT IT EMPHATICALLY DOES NOT DO:
    ///   * it does not touch a single EEG amplitude — it only produces a timestamp;
    ///   * it does not reorder, insert, drop or interpolate samples;
    ///   * it does not force monotonicity by clamping the OUTPUT (proc_monotonize's approach),
    ///     which would hide a bad model rather than fix it. Monotonicity here follows from the
    ///     grid being built forward; what is bounded is how fast the interval may be steered,
    ///     not the timestamp itself.
    ///
    /// A straight line is the right model precisely BECAUSE the acquisition is uniform: at a
    /// fixed sampling rate the true capture time IS linear in sample index, so the fit recovers
    /// the sample clock and the residual is the transport jitter. If the stream were irregular
    /// this class would be the wrong tool, and it says so by refusing to run without a rate.
    /// </summary>
    public class EegAnalysisTimebase
    {
        readonly int m_WindowSize;
        readonly double m_NominalRateHz;

        // Sliding window of (index, timestamp) pairs, as a ring.
        readonly double[] m_Index;
        readonly double[] m_Time;

        int m_Count;
        int m_Head;
        long m_SampleIndex;

        /// <summary>The last timestamp this class emitted. The grid is built forward from it.</summary>
        double m_LastEmitted;

        /// <summary>Slope of the current fit, in seconds per sample.</summary>
        public double secondsPerSample { get; private set; }

        /// <summary>True once enough samples have been seen for a meaningful fit.</summary>
        public bool isReady => m_Count >= Math.Min(m_WindowSize, 32);

        /// <summary>The implied sampling rate of the fitted line, in Hz.</summary>
        public double fittedRateHz => secondsPerSample > 0d ? 1.0 / secondsPerSample : 0d;

        /// <summary>
        /// Largest absolute difference seen between a raw timestamp and its fitted value.
        ///
        /// This IS the jitter the fit is removing, measured rather than claimed. Reported by the
        /// diagnostics so the size of the correction is always visible alongside the result.
        /// </summary>
        public double largestResidualSeconds { get; private set; }

        /// <summary>
        /// Largest gap between an emitted analysis timestamp and the raw local one it came from.
        ///
        /// This is how far the uniform grid has been allowed to sit from the jittered clock. It
        /// replaces the old residual figure, which compared against a line that was itself
        /// moving and so reported the model's instability rather than the signal's jitter.
        /// </summary>
        public double largestDriftSeconds { get; private set; }

        public long samplesSeen => m_SampleIndex;

        // ---- Anchor integrity -------------------------------------------------------------
        //
        // THE FAILURE THIS GUARDS AGAINST, measured on a real recording
        // (S_20260902_131219_r01_27eda8):
        //
        // The emitted grid integrates FORWARD from wherever its first sample landed, and the
        // steering correction is bounded to a fraction of one sample interval. That is exactly
        // right for jitter of a few tens of milliseconds. It is catastrophic for an anchor that
        // is wrong by a whole clock domain: with the first sample seeded in the SENDER's clock
        // (1.24e6 s away, because time_correction was not yet known), every subsequent sample
        // demanded a correction far larger than the clamp allowed, so the clamp saturated
        // NEGATIVE on essentially every sample. The grid then free-ran at
        //     nominal - 0.4*nominal = 2.4 ms  ->  416.7 Hz
        // for the entire recording, while remaining perfectly smooth, strictly monotonic and
        // completely fictional. Closing a 1.24e6 s error at 1.6 ms per sample would need about
        // 7.8e8 samples — roughly 36 days of continuous recording.
        //
        // A model that cannot reach the signal is not tracking it. Rather than integrate
        // forward forever, the grid RE-ANCHORS: it restarts from the current raw local
        // timestamp and drops the stale fit window. Re-anchoring is rare, deliberate and
        // COUNTED, so it can never happen silently.

        /// <summary>
        /// How far the grid may sit from the raw local clock before the anchor is treated as
        /// invalid, in seconds.
        ///
        /// Derived from the nominal rate, never from any particular session: 100 sample
        /// intervals, floored at 0.5 s. Measured transport jitter on this hardware reaches
        /// ~62 ms and clock drift between two machines over the 8 s fit window is sub-millisecond,
        /// so this sits orders of magnitude above anything legitimate and orders of magnitude
        /// below a clock-domain error.
        /// </summary>
        public double reanchorThresholdSeconds { get; }

        /// <summary>
        /// How many times the grid had to be re-anchored. ZERO on a healthy stream.
        ///
        /// Non-zero means the analysis timeline has at least one discontinuity, and any epoch
        /// spanning it is not trustworthy. Surfaced by the receiver and the diagnostics rather
        /// than swallowed.
        /// </summary>
        public int reanchorCount { get; private set; }

        /// <summary>Raw local timestamp at which the most recent re-anchor happened. NaN if none.</summary>
        public double lastReanchorAtSeconds { get; private set; } = double.NaN;

        /// <summary>
        /// True when the emitted grid is currently within the re-anchor threshold of the raw
        /// local clock AND the fitted slope is compatible with the advertised rate.
        ///
        /// This is the flag an epoch consumer should check before trusting an analysis
        /// timestamp. It is deliberately about the CURRENT state — a stream that re-anchored
        /// once and then settled is tracking again, but <see cref="reanchorCount"/> still records
        /// that it happened.
        /// </summary>
        public bool isTracking { get; private set; } = true;

        /// <summary>
        /// Marks the current anchor invalid. The NEXT sample re-seeds the grid from its own raw
        /// local timestamp, and the re-anchor is counted then.
        ///
        /// Deferred rather than immediate on purpose: the caller that detects a domain change —
        /// the receiver noticing time_correction move — does not have a sample in hand, and
        /// seeding from a placeholder would put a second bogus anchor in the timeline before the
        /// first real one. The next Add() always has the right value.
        ///
        /// The sample index is NOT reset. It counts what this instance has seen, and clearing it
        /// would make the diagnostics understate how much data went through.
        /// </summary>
        public void InvalidateAnchor(string reason = "")
        {
            m_NeedsReseed = true;
            m_PendingReseedReason = string.IsNullOrEmpty(reason) ? "anchor lost" : reason;
        }

        bool m_NeedsReseed;
        string m_PendingReseedReason = string.Empty;

        /// <summary>Why the last re-anchor happened. Empty before the first one.</summary>
        public string lastReanchorReason { get; private set; } = string.Empty;

        /// <param name="nominalRateHz">
        /// From the stream metadata. Used only as the starting slope until the fit has data —
        /// never as a substitute for it.
        /// </param>
        /// <param name="windowSeconds">
        /// How much history the fit spans. Long enough to average out chunk-boundary jitter,
        /// short enough to follow genuine drift between the two machines' clocks.
        /// </param>
        public EegAnalysisTimebase(double nominalRateHz, double windowSeconds = 8.0)
        {
            if (nominalRateHz <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(nominalRateHz),
                    "a uniform analysis time base is only meaningful for a regularly-sampled " +
                    "stream; an irregular stream must keep its own timestamps");
            }

            m_NominalRateHz = nominalRateHz;
            secondsPerSample = 1.0 / nominalRateHz;

            // BOTH THRESHOLDS ARE SET FROM MEASUREMENT, not from taste. Replaying the recorded
            // local_raw column of two real sessions through this exact algorithm gives the
            // healthy envelope; the failed session gives the pathological one:
            //
            //                          healthy (2 sessions)      broken session
            //   grid-vs-raw drift      0.205 s and 0.314 s       1.24e6 s
            //   longest saturated run  55 and 86 samples         permanent (27.6% of all samples)
            //
            // The raw local clock legitimately swings about +-0.35 s around its own regression
            // line on this hardware — far more than the +-62 ms the first version of this class
            // assumed — so a tight threshold would re-anchor a HEALTHY stream and inject the
            // very discontinuity it exists to prevent. Both values are therefore placed with
            // more than an order of magnitude of clearance above the healthy envelope, and
            // several orders of magnitude below the failure.
            reanchorThresholdSeconds = Math.Max(5.0, 1000.0 / nominalRateHz);
            m_SaturatedRunLimit = Math.Max(64, (int)Math.Ceiling(nominalRateHz * 2.0));

            m_WindowSize = Math.Max(64, (int)Math.Ceiling(nominalRateHz * windowSeconds));
            m_Index = new double[m_WindowSize];
            m_Time = new double[m_WindowSize];
        }

        /// <summary>
        /// Adds one raw local timestamp and returns the ANALYSIS timestamp for that sample.
        ///
        /// The returned value is one steered interval after the previous one, so the series is
        /// uniform and strictly increasing by construction — while the raw value that went in is
        /// left untouched for the caller to keep.
        /// </summary>
        public double Add(double localRawTimestamp)
        {
            // ---- ANCHOR GUARD, before anything else -----------------------------------------
            //
            // Asked FIRST, because every line below assumes the grid is somewhere near the
            // clock it is meant to be tracking. If it is not, the fit window is stale, the
            // steering is saturated, and one more forward integration just extends a fiction.
            //
            // The comparison is against the RAW LOCAL timestamp — the value that defines the
            // domain the analysis timeline is contractually required to stay in.
            if (m_SampleIndex > 0 && !m_NeedsReseed)
            {
                var lost = Math.Abs(m_LastEmitted - localRawTimestamp);

                if (lost > reanchorThresholdSeconds)
                {
                    InvalidateAnchor(
                        $"grid was {lost:F3} s from the raw local clock, beyond the " +
                        $"{reanchorThresholdSeconds:F3} s threshold");
                }
            }

            if (m_NeedsReseed)
            {
                m_NeedsReseed = false;
                m_Count = 0;
                m_Head = 0;
                secondsPerSample = 1.0 / m_NominalRateHz;
                m_SaturatedRun = 0;

                m_LastEmitted = localRawTimestamp;
                lastReanchorAtSeconds = localRawTimestamp;
                lastReanchorReason = m_PendingReseedReason;
                reanchorCount++;
                isTracking = true;
            }

            var index = (double)m_SampleIndex;
            m_SampleIndex++;

            // Store in the ring.
            m_Index[m_Head] = index;
            m_Time[m_Head] = localRawTimestamp;
            m_Head = (m_Head + 1) % m_WindowSize;

            if (m_Count < m_WindowSize)
                m_Count++;

            // Least-squares fit of t = a + b*n over the window.
            //
            // Before there is enough history the raw value is returned unchanged: a fit through
            // three jittered points would be worse than no fit at all, and pretending otherwise
            // would put the least trustworthy timestamps at the start of every recording.
            if (m_Count < 2)
            {
                m_LastEmitted = localRawTimestamp;
                return localRawTimestamp;
            }

            double sumN = 0d, sumT = 0d;

            for (var i = 0; i < m_Count; i++)
            {
                sumN += m_Index[i];
                sumT += m_Time[i];
            }

            var meanN = sumN / m_Count;
            var meanT = sumT / m_Count;

            double covariance = 0d, variance = 0d;

            for (var i = 0; i < m_Count; i++)
            {
                var dn = m_Index[i] - meanN;
                covariance += dn * (m_Time[i] - meanT);
                variance += dn * dn;
            }

            // Degenerate window (every sample at the same index) cannot happen in practice, but
            // dividing by zero would poison every later timestamp.
            if (variance > 0d)
            {
                var slope = covariance / variance;

                // A slope that disagrees wildly with the advertised rate means the window is
                // dominated by something other than the sample clock — a long dropout, say. The
                // nominal rate is the safer estimate in that case, and the disagreement is
                // visible in the residual.
                // Accepted only within 10%% of the ADVERTISED rate. The stream states its own
                // sampling rate, so a fitted slope far from it is evidence that the window is
                // dominated by something other than the sample clock — a dropout, or the
                // connect-time backlog draining faster than real time. The old guard allowed
                // up to 10x nominal, which let a bad window stretch one step to 28 ms.
                var nominal = 1.0 / m_NominalRateHz;

                if (slope > nominal * 0.9 && slope < nominal * 1.1)
                    secondsPerSample = slope;
            }

            var intercept = meanT - secondsPerSample * meanN;
            var fitted = intercept + secondsPerSample * index;

            var residual = Math.Abs(fitted - localRawTimestamp);
            if (residual > largestResidualSeconds)
                largestResidualSeconds = residual;

            // ---- Emit INCREMENTALLY, not by evaluating the line ------------------------------
            //
            // WHY THE PREVIOUS VERSION WENT BACKWARDS: it returned intercept + slope*index, and
            // BOTH intercept and slope are refitted on every sample. Consecutive outputs
            // therefore came from two DIFFERENT lines, so the step between them was
            //     (a[n+1] - a[n]) + b[n+1]*(n+1) - b[n]*n
            // rather than simply b. Nothing in that expression is constrained to be positive:
            // when a badly jittered chunk anchor entered or left the sliding window the line
            // shifted down by more than one sample interval and the emitted timestamp went back.
            // Measured: 3 negative steps, worst -4.604 ms — almost exactly one sample interval,
            // which is the signature of the line moving under the evaluation point.
            //
            // THE FIX IS THE MODEL, NOT A CLAMP ON THE OUTPUT. The sequence is uniformly
            // sampled, so the correct statement is "each sample is one interval after the last".
            // That is emitted directly, and the fitted line is used only to STEER the interval —
            // a bounded correction that keeps the grid locked to the measured clock without
            // letting a single jittered anchor move the grid more than a fraction of one step.
            //
            // Because the adjustment is bounded to a fraction of the interval, the step is
            // always in [0.6, 1.4] x secondsPerSample and therefore strictly positive BY
            // CONSTRUCTION. Monotonicity is a property of the model, not something imposed
            // afterwards, and no timestamp is ever clamped, discarded or reordered.
            const double k_MaxAdjustFraction = 0.4;

            var predicted = m_LastEmitted + secondsPerSample;
            var adjust = fitted - predicted;
            // Bounded against the NOMINAL interval, not the current slope: the bound has to be
            // a quantity that cannot itself drift, or a bad slope widens its own licence.
            var limit = (1.0 / m_NominalRateHz) * k_MaxAdjustFraction;

            if (adjust > limit)
                adjust = limit;
            else if (adjust < -limit)
                adjust = -limit;

            m_LastEmitted = predicted + adjust;

            var drift = Math.Abs(m_LastEmitted - localRawTimestamp);
            if (drift > largestDriftSeconds)
                largestDriftSeconds = drift;

            // ---- Live integrity ---------------------------------------------------------------
            // Two independent ways the grid can stop representing the signal, both cheap to test
            // on every sample and both invisible in the output itself:
            //
            //   * it has wandered away from the raw clock (about to re-anchor), or
            //   * the steering is saturated, which is what a grid free-running at the wrong rate
            //     looks like from the inside. A saturated step means the fit is asking for a
            //     correction the clamp will not grant, so the emitted rate is NOT the fitted rate.
            //
            // Neither is fatal for one sample — jitter saturates the clamp occasionally and that
            // is the clamp doing its job. What is reported is the CURRENT state, and the
            // diagnostics pair it with reanchorCount for the history.
            var saturated = Math.Abs(adjust) >= limit;

            m_SaturatedRun = saturated ? m_SaturatedRun + 1 : 0;

            if (m_SaturatedRun > m_SaturatedRunLimit)
                totalSaturatedRuns++;

            isTracking = drift <= reanchorThresholdSeconds &&
                         m_SaturatedRun <= m_SaturatedRunLimit;

            return m_LastEmitted;
        }

        /// <summary>Consecutive samples on which the steering hit its bound.</summary>
        int m_SaturatedRun;

        /// <summary>
        /// How long the steering may stay saturated before the grid counts as not tracking.
        ///
        /// One sample interval's worth of jitter can saturate the clamp for a handful of samples
        /// while the grid catches up; a grid that is saturated for a quarter of a second is not
        /// catching up, it is free-running.
        /// </summary>
        readonly int m_SaturatedRunLimit;

        /// <summary>How many times the steering stayed saturated long enough to count as free-running.</summary>
        public int totalSaturatedRuns { get; private set; }

        /// <summary>Clears the fit. Used when a new run or a new connection begins.</summary>
        public void Reset()
        {
            m_Count = 0;
            m_Head = 0;
            m_SampleIndex = 0;
            largestResidualSeconds = 0d;
            largestDriftSeconds = 0d;
            m_LastEmitted = 0d;
            secondsPerSample = 1.0 / m_NominalRateHz;

            // The integrity history belongs to the stream that produced it. A reset means a new
            // stream, a new run or a reconnect, and carrying a previous recording's re-anchor
            // count forward would make the next one look damaged.
            reanchorCount = 0;
            totalSaturatedRuns = 0;
            lastReanchorAtSeconds = double.NaN;
            lastReanchorReason = string.Empty;
            m_SaturatedRun = 0;
            m_NeedsReseed = false;
            m_PendingReseedReason = string.Empty;
            isTracking = true;
        }
    }
}
