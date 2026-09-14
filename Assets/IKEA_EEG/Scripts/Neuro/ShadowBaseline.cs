namespace IkeaEeg.Neuro
{
    /// <summary>
    /// A HOLDER for a baseline that an approved protocol supplies from outside. It computes
    /// nothing and infers nothing.
    ///
    /// WHY THIS CLASS NO LONGER ACCUMULATES. An earlier revision built a baseline by pooling
    /// the median of the first N accepted task windows, with N = 8. Every part of that was an
    /// unreviewed scientific decision wearing the clothes of an implementation detail: which
    /// windows count as baseline material, how many are enough, how they are pooled, and
    /// whether task windows may serve as their own reference at all. A baseline derived from
    /// the very activity being measured is not a baseline, and none of those choices had been
    /// approved.
    ///
    /// So the rule is now explicit: a baseline exists only when an approved provider hands one
    /// over. In Phase 1 no such provider exists, nothing calls
    /// <see cref="SupplyApprovedBaseline"/>, and <see cref="isValid"/> is therefore always
    /// false. That is the correct state, not a gap to be filled in with a default.
    /// </summary>
    public sealed class ShadowBaseline
    {
        /// <summary>Identifier of whatever supplied the values. Empty while none has.</summary>
        public string providerId { get; private set; } = string.Empty;

        /// <summary>
        /// False until an approved provider supplies a baseline. There is deliberately no
        /// window count, duration or sufficiency criterion behind this: inventing one is
        /// exactly what the correction removed.
        /// </summary>
        public bool isValid { get; private set; }

        public double thetaBaseline { get; private set; } = double.NaN;
        public double alphaBaseline { get; private set; } = double.NaN;

        /// <summary>
        /// The ONLY way a baseline can come to exist. Intentionally unused in Phase 1.
        ///
        /// The checks here are arithmetic, not scientific: a non-finite or non-positive value
        /// cannot serve as a reference whatever protocol produced it. No threshold, duration,
        /// window count, outlier rule or pooling rule is applied or implied — those belong to
        /// the approved protocol, which does not exist yet.
        /// </summary>
        public bool SupplyApprovedBaseline(double theta, double alpha, string providerId)
        {
            if (!IsUsable(theta) || !IsUsable(alpha) || string.IsNullOrWhiteSpace(providerId))
                return false;

            thetaBaseline = theta;
            alphaBaseline = alpha;
            this.providerId = providerId;
            isValid = true;
            return true;
        }

        public void Reset()
        {
            thetaBaseline = double.NaN;
            alphaBaseline = double.NaN;
            providerId = string.Empty;
            isValid = false;
        }

        static bool IsUsable(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0d;
    }
}
