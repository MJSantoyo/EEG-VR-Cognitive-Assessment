using System.Globalization;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// Why a window did not produce a label. Recorded on EVERY window, including accepted
    /// ones (<see cref="None"/>), so the output is a complete census rather than a survivor
    /// list: a rejected window stays in the file with its reason attached.
    ///
    /// The order of the members is the order the gates are evaluated in — see
    /// <see cref="ShadowModeController.FirstUnmetGate"/>. The first unmet gate is the reason
    /// recorded, so the reason always names the earliest obstacle rather than an arbitrary one.
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

        /// <summary>
        /// The physical channel-to-electrode correspondence has not been verified, so the ROI
        /// values cannot be treated as electrode-level measurements. The values are still
        /// logged; what is withheld is the interpretation, and therefore the label.
        /// </summary>
        MontageUnverified,

        /// <summary>
        /// No normalization method has been scientifically frozen. Phase 1 computes no
        /// normalized index rather than choosing a formula on the code's own authority.
        /// </summary>
        NoApprovedNormalization,

        /// <summary>
        /// No approved baseline protocol has supplied a baseline. Distinct from "not enough
        /// windows yet": there is no automatic accumulation to be insufficient.
        /// </summary>
        BaselineUnavailable,

        /// <summary>
        /// No approved numerical threshold rule is configured. Reaching this gate would mean
        /// every earlier one had been cleared.
        /// </summary>
        NoApprovedRule,
    }

    /// <summary>
    /// One immutable observation: what the pipeline produced for a window, what the gates made
    /// of it, and everything needed to audit that later.
    ///
    /// This type carries no reference to the experiment. It is data, produced downstream of
    /// the EEG pipeline and written to its own file.
    /// </summary>
    public readonly struct ShadowDecision
    {
        public readonly long windowIndex;

        /// <summary>Window bounds on the SAME analysis clock the raw EEG file records.</summary>
        public readonly double lslStart;
        public readonly double lslEnd;

        public readonly int validChannelCount;

        /// <summary>
        /// ROI band powers exactly as the existing pipeline computed them. Logged as pipeline
        /// outputs; NOT confirmed electrode-level measurements while the montage is unverified.
        /// </summary>
        public readonly double thetaFc;
        public readonly double alphaPost;

        /// <summary>Supplied baseline, or NaN while no approved provider has supplied one.</summary>
        public readonly double baselineTheta;
        public readonly double baselineAlpha;

        /// <summary>
        /// NaN in Phase 1, always. No normalization method is frozen, so none is computed.
        /// </summary>
        public readonly double normalizedIndex;

        /// <summary>PROVISIONAL rule-based label. See <see cref="WorkloadLevel"/>.</summary>
        public readonly WorkloadLevel level;

        /// <summary>
        /// Pinned to UNVERIFIED until a physical electrode-placement check is performed and
        /// documented. Deliberately NOT read from AuraMontageConfig, whose mappingSource
        /// defaults to HumanVerifiedAcquisitionUi without any physical evidence behind it.
        /// </summary>
        public readonly string montageStatus;

        public readonly bool baselineValid;
        public readonly string qualityRuleVersion;
        public readonly string controllerVersion;
        public readonly ShadowRejection rejectedReason;

        public ShadowDecision(long windowIndex, double lslStart, double lslEnd,
            int validChannelCount, double thetaFc, double alphaPost,
            double baselineTheta, double baselineAlpha, double normalizedIndex,
            WorkloadLevel level, string montageStatus, bool baselineValid,
            string qualityRuleVersion, string controllerVersion,
            ShadowRejection rejectedReason)
        {
            this.windowIndex = windowIndex;
            this.lslStart = lslStart;
            this.lslEnd = lslEnd;
            this.validChannelCount = validChannelCount;
            this.thetaFc = thetaFc;
            this.alphaPost = alphaPost;
            this.baselineTheta = baselineTheta;
            this.baselineAlpha = baselineAlpha;
            this.normalizedIndex = normalizedIndex;
            this.level = level;
            this.montageStatus = montageStatus;
            this.baselineValid = baselineValid;
            this.qualityRuleVersion = qualityRuleVersion;
            this.controllerVersion = controllerVersion;
            this.rejectedReason = rejectedReason;
        }

        /// <summary>
        /// FROZEN SCHEMA. Fifteen columns, this order. Anything consuming the output may rely
        /// on it; changing it is a breaking change to every downstream analysis.
        /// </summary>
        public const string CsvHeader =
            "window_index,lsl_start,lsl_end,n_valid_channels,theta_fc,alpha_post," +
            "baseline_theta,baseline_alpha,normalized_index,level,montage_status," +
            "baseline_valid,quality_rule_version,controller_version,rejected_reason";

        /// <summary>Empty, never a substituted zero: an absent value must read as absent.</summary>
        static string N(double v) =>
            double.IsNaN(v) || double.IsInfinity(v)
                ? string.Empty
                : v.ToString("G17", CultureInfo.InvariantCulture);

        public string ToCsvRow()
        {
            var c = CultureInfo.InvariantCulture;

            return string.Join(",",
                windowIndex.ToString(c),
                N(lslStart), N(lslEnd),
                validChannelCount.ToString(c),
                N(thetaFc), N(alphaPost),
                N(baselineTheta), N(baselineAlpha), N(normalizedIndex),
                level.ToString().ToUpperInvariant(),
                montageStatus,
                baselineValid ? "TRUE" : "FALSE",
                qualityRuleVersion,
                controllerVersion,
                rejectedReason.ToString().ToUpperInvariant());
        }
    }
}
