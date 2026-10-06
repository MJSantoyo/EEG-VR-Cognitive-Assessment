using System.Collections.Generic;
using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// What an interval's usable windows add up to. Immutable.
    ///
    /// THE STATISTIC IS A PLACEHOLDER. A per-interval median of the ROI band powers is written
    /// so the plumbing carries a number end to end, and because a median is at least robust to a
    /// single stray window. It is NOT an approved aggregation: no rule says which statistic, over
    /// which windows, with what minimum count, represents a rest period or a trial. The method
    /// string travels with the values into every row so nobody mistakes one for the other.
    /// </summary>
    public readonly struct FeatureWindowSummary
    {
        public const string PlaceholderAggregation = "PLACEHOLDER_MEDIAN_UNAPPROVED";

        /// <summary>Windows that fell entirely inside the interval.</summary>
        public readonly int windowsOffered;

        /// <summary>Of those, windows <see cref="EegWindowScreen"/> accepted.</summary>
        public readonly int windowsValid;

        /// <summary>Placeholder median of frontal theta over valid windows; NaN when none.</summary>
        public readonly double thetaFc;

        /// <summary>Placeholder median of posterior alpha over valid windows; NaN when none.</summary>
        public readonly double alphaPost;

        public readonly string aggregationMethod;

        public FeatureWindowSummary(int windowsOffered, int windowsValid, double thetaFc,
            double alphaPost, string aggregationMethod)
        {
            this.windowsOffered = windowsOffered;
            this.windowsValid = windowsValid;
            this.thetaFc = thetaFc;
            this.alphaPost = alphaPost;
            this.aggregationMethod = aggregationMethod;
        }

        public int windowsRejected => windowsOffered - windowsValid;
        public bool hasValidWindows => windowsValid > 0;

        public static FeatureWindowSummary Empty =>
            new FeatureWindowSummary(0, 0, double.NaN, double.NaN, PlaceholderAggregation);
    }

    /// <summary>
    /// Collects the feature windows that lie inside ONE explicit interval on the analysis clock.
    ///
    /// MEMBERSHIP IS BY TIMESTAMP, NOT BY ARRIVAL. A window counts only when
    /// <c>windowStart &gt;= open</c> and, once the interval has closed, <c>windowEnd &lt;= close</c>.
    /// The bounds come from the events' lsl_timestamp, which is the same local LSL clock the
    /// window timestamps are on (see AuraLslReceiver), so no clock is converted here. A window
    /// that straddles a boundary belongs to neither side.
    ///
    /// LATE WINDOWS. The pipeline publishes a window some time after its last sample, so a
    /// window lying wholly inside the interval can arrive after the closing event. The collector
    /// therefore stays receptive after <see cref="Close"/> until a window ending beyond the close
    /// arrives (<see cref="IsSettled"/>): windows are published in time order, so nothing inside
    /// the interval can follow that one.
    /// </summary>
    public sealed class IntervalWindowCollector
    {
        readonly List<double> m_Theta = new List<double>();
        readonly List<double> m_Alpha = new List<double>();
        int m_Offered;

        public double openLsl { get; private set; } = double.NaN;
        public double closeLsl { get; private set; } = double.NaN;

        public bool isOpen => !double.IsNaN(openLsl);
        public bool isClosed => !double.IsNaN(closeLsl);

        /// <summary>Starts a fresh interval. A NaN bound (no LSL clock) means nothing can count.</summary>
        public void Open(double openLslTimestamp)
        {
            Clear();
            openLsl = openLslTimestamp;
        }

        public void Close(double closeLslTimestamp) => closeLsl = closeLslTimestamp;

        public void Offer(LatestEegFeatures features)
        {
            if (features == null || !isOpen)
                return;

            if (features.windowStart < openLsl)
                return;

            if (isClosed && features.windowEnd > closeLsl)
                return;

            m_Offered++;

            if (!EegWindowScreen.IsUsable(features))
                return;

            m_Theta.Add(features.frontalTheta);
            m_Alpha.Add(features.posteriorAlpha);
        }

        /// <summary>True once closed and a window ending after the close has been seen.</summary>
        public bool IsSettled(double lastWindowEnd) =>
            isClosed && !double.IsNaN(lastWindowEnd) && lastWindowEnd > closeLsl;

        public FeatureWindowSummary Summarize() =>
            new FeatureWindowSummary(m_Offered, m_Theta.Count, Median(m_Theta), Median(m_Alpha),
                FeatureWindowSummary.PlaceholderAggregation);

        public void Clear()
        {
            m_Theta.Clear();
            m_Alpha.Clear();
            m_Offered = 0;
            openLsl = double.NaN;
            closeLsl = double.NaN;
        }

        static double Median(List<double> values)
        {
            if (values.Count == 0)
                return double.NaN;

            var sorted = new List<double>(values);
            sorted.Sort();

            var middle = sorted.Count / 2;

            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) * 0.5;
        }
    }
}
