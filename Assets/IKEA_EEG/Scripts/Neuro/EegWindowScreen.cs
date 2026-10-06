using IkeaEeg.Data;

namespace IkeaEeg.Neuro
{
    /// <summary>
    /// Decides whether one published feature window is USABLE material for an aggregate.
    ///
    /// NOT A NEW CRITERION. These are exactly the first three gates of
    /// <see cref="ShadowModeController.Evaluate"/> — the pipeline's own validity, a clean ROI,
    /// and both ROI values present — evaluated through the same
    /// <see cref="ShadowModeController.FirstUnmetGate"/> so the precedence cannot drift. The
    /// scientific gates after them (normalization, baseline, rule) are passed as satisfied here
    /// because they do not describe the WINDOW; they describe what may be concluded from it, and
    /// that is the workload estimator's question, not this one.
    ///
    /// ExperimentSelfTest drives this and the shadow controller with the same synthetic windows
    /// and asserts they agree.
    /// </summary>
    public static class EegWindowScreen
    {
        /// <summary>
        /// <see cref="ShadowRejection.None"/> when the window is usable, otherwise the first of
        /// FeatureInvalid, RoiContaminated or RoiValueMissing that applies.
        /// </summary>
        public static ShadowRejection Screen(LatestEegFeatures features)
        {
            if (features == null)
                return ShadowRejection.FeatureInvalid;

            var theta = features.frontalTheta;
            var alpha = features.posteriorAlpha;

            var featureValid = features.featureValidity;
            var roiClean = features.roiValid && string.IsNullOrEmpty(features.roiContamination);
            var roiValuesPresent = features.frontalThetaValid && features.posteriorAlphaValid &&
                                   !double.IsNaN(theta) && !double.IsNaN(alpha);

            return ShadowModeController.FirstUnmetGate(featureValid, roiClean, roiValuesPresent,
                montageVerified: true, normalizationApproved: true, baselineValid: true,
                decisionRuleApproved: true);
        }

        public static bool IsUsable(LatestEegFeatures features) =>
            Screen(features) == ShadowRejection.None;
    }
}
