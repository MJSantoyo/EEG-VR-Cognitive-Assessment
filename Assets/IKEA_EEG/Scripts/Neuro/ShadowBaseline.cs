using System.Collections.Generic;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// Per-session baseline for the two ROI band powers, built from ACCEPTED windows only.
    ///
    /// WHAT THIS DELIBERATELY DOES NOT DO: it applies no outlier rejection of its own. The
    /// numerical rejection thresholds are pending scientific review, and inventing them here —
    /// even "reasonable" ones — would silently bake an unreviewed decision into every later
    /// figure. Windows arrive already screened by the EEG pipeline's own quality rules; this
    /// class only refuses to produce a baseline when there is not enough accepted material.
    ///
    /// The median is used rather than the mean because the 8 September session's delayed phase
    /// showed exactly the failure mode a mean cannot survive: a frontocentral theta mean of
    /// 112 267 against a median of 29.0. A single artefact window moves a mean by four orders
    /// of magnitude and leaves a median untouched.
    /// </summary>
    public sealed class ShadowBaseline
    {
        /// <summary>
        /// Minimum accepted windows before a baseline is offered at all. Below this the
        /// answer is "insufficient", never an estimate from two samples.
        /// </summary>
        public const int MinimumWindows = 8;

        readonly List<double> m_Theta = new List<double>();
        readonly List<double> m_Alpha = new List<double>();

        public int acceptedWindowCount => m_Theta.Count;

        public bool isValid => m_Theta.Count >= MinimumWindows &&
                               IsUsable(Median(m_Theta)) &&
                               IsUsable(Median(m_Alpha));

        public double thetaBaseline => m_Theta.Count == 0 ? double.NaN : Median(m_Theta);
        public double alphaBaseline => m_Alpha.Count == 0 ? double.NaN : Median(m_Alpha);

        /// <summary>
        /// Adds one window's ROI values. The caller decides what "accepted" means; this class
        /// only stores finite, positive values, because a band power of zero or below cannot
        /// serve as a denominator.
        /// </summary>
        public void Accumulate(double frontocentralTheta, double posteriorAlpha)
        {
            if (!IsUsable(frontocentralTheta) || !IsUsable(posteriorAlpha))
                return;

            m_Theta.Add(frontocentralTheta);
            m_Alpha.Add(posteriorAlpha);
        }

        /// <summary>
        /// Theta/alpha ratio expressed against the session baseline, or NaN when the baseline
        /// is not usable. NaN is a deliberate, checkable outcome — never a substituted 0 or 1.
        /// </summary>
        public double Normalise(double frontocentralTheta, double posteriorAlpha)
        {
            if (!isValid || !IsUsable(frontocentralTheta) || !IsUsable(posteriorAlpha))
                return double.NaN;

            var thetaRatio = frontocentralTheta / thetaBaseline;
            var alphaRatio = posteriorAlpha / alphaBaseline;

            if (!IsUsable(alphaRatio))
                return double.NaN;

            var index = thetaRatio / alphaRatio;
            return IsUsable(index) ? index : double.NaN;
        }

        public void Reset()
        {
            m_Theta.Clear();
            m_Alpha.Clear();
        }

        static bool IsUsable(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0d;

        static double Median(List<double> values)
        {
            if (values.Count == 0)
                return double.NaN;

            var copy = new List<double>(values);
            copy.Sort();

            var mid = copy.Count / 2;
            return copy.Count % 2 == 1
                ? copy[mid]
                : (copy[mid - 1] + copy[mid]) * 0.5d;
        }
    }
}
