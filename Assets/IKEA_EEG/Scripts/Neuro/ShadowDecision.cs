using System.Globalization;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// Why a window did not produce a label. Recorded on EVERY window, including accepted
    /// ones (<see cref="None"/>), so the output is a complete census rather than a survivor
    /// list: a rejected window stays in the file with its reason attached.
    /// </summary>
    public enum ShadowRejection
    {
        None = 0,

        /// <summary>The pipeline itself marked the window untrustworthy.</summary>
        FeatureInvalid,

        /// <summary>The ROI averages a channel the quality rules flagged.</summary>
        RoiContaminated,

        /// <summary>Frontocentral theta or posterior alpha was not computable.</summary>
        RoiValueMissing,

        /// <summary>Not enough accepted windows yet to define a baseline.</summary>
        BaselineInsufficient,

        /// <summary>A baseline exists but is degenerate (zero or non-finite scale).</summary>
        BaselineDegenerate,

        /// <summary>
        /// No approved numerical threshold rule is configured. This is the Phase 1 state and
        /// it is NOT a failure: the thresholds are pending scientific review and must not be
        /// invented, so every window is honestly reported as undecided.
        /// </summary>
        NoApprovedRule,
    }

    /// <summary>
    /// One immutable observation: what the pipeline produced for a window, what the rule made
    /// of it, and everything needed to audit that later.
    ///
    /// This type carries no reference to the experiment. It is data, produced downstream of
    /// the EEG pipeline and written to its own file.
    /// </summary>
    public readonly struct ShadowDecision
    {
        public readonly long windowIndex;

        /// <summary>Window bounds on the SAME analysis clock the raw EEG file records.</summary>
        public readonly double windowStartLsl;
        public readonly double windowEndLsl;

        public readonly int channelCount;
        public readonly int validChannelCount;

        /// <summary>Raw ROI values as the pipeline computed them. NaN when unavailable.</summary>
        public readonly double frontocentralTheta;
        public readonly double posteriorAlpha;

        /// <summary>Baseline used, and the normalised index derived from it. NaN when absent.</summary>
        public readonly double baselineTheta;
        public readonly double baselineAlpha;
        public readonly double normalizedIndex;

        public readonly bool baselineValid;

        /// <summary>PROVISIONAL rule-based label. See <see cref="WorkloadLevel"/>.</summary>
        public readonly WorkloadLevel level;

        public readonly ShadowRejection rejectedReason;

        /// <summary>
        /// Pinned to UNVERIFIED until a physical electrode-placement check is performed and
        /// documented. It is deliberately NOT read from AuraMontageConfig, whose mappingSource
        /// field defaults to HumanVerifiedAcquisitionUi without any physical evidence having
        /// been collected.
        /// </summary>
        public readonly string montageStatus;

        public readonly string qualityRuleVersion;
        public readonly string controllerVersion;

        public ShadowDecision(long windowIndex, double windowStartLsl, double windowEndLsl,
            int channelCount, int validChannelCount,
            double frontocentralTheta, double posteriorAlpha,
            double baselineTheta, double baselineAlpha, double normalizedIndex,
            bool baselineValid, WorkloadLevel level, ShadowRejection rejectedReason,
            string montageStatus, string qualityRuleVersion, string controllerVersion)
        {
            this.windowIndex = windowIndex;
            this.windowStartLsl = windowStartLsl;
            this.windowEndLsl = windowEndLsl;
            this.channelCount = channelCount;
            this.validChannelCount = validChannelCount;
            this.frontocentralTheta = frontocentralTheta;
            this.posteriorAlpha = posteriorAlpha;
            this.baselineTheta = baselineTheta;
            this.baselineAlpha = baselineAlpha;
            this.normalizedIndex = normalizedIndex;
            this.baselineValid = baselineValid;
            this.level = level;
            this.rejectedReason = rejectedReason;
            this.montageStatus = montageStatus;
            this.qualityRuleVersion = qualityRuleVersion;
            this.controllerVersion = controllerVersion;
        }

        public const string CsvHeader =
            "window_index,window_start_lsl,window_end_lsl,channel_count,valid_channel_count," +
            "frontocentral_theta,posterior_alpha,baseline_theta,baseline_alpha," +
            "normalized_index,baseline_valid,level,rejected_reason,montage_status," +
            "quality_rule_version,controller_version";

        static string N(double v) =>
            double.IsNaN(v) || double.IsInfinity(v)
                ? string.Empty
                : v.ToString("G17", CultureInfo.InvariantCulture);

        public string ToCsvRow()
        {
            var c = CultureInfo.InvariantCulture;

            return string.Join(",",
                windowIndex.ToString(c),
                N(windowStartLsl), N(windowEndLsl),
                channelCount.ToString(c), validChannelCount.ToString(c),
                N(frontocentralTheta), N(posteriorAlpha),
                N(baselineTheta), N(baselineAlpha), N(normalizedIndex),
                baselineValid ? "TRUE" : "FALSE",
                level.ToString().ToUpperInvariant(),
                rejectedReason.ToString().ToUpperInvariant(),
                montageStatus,
                qualityRuleVersion,
                controllerVersion);
        }
    }
}
