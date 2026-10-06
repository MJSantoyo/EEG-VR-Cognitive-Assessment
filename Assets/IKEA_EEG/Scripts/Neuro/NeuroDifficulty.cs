namespace IkeaEeg.Neuro
{
    /// <summary>
    /// The chair-trial difficulty AS THE NEURO LAYER SEES IT: a label read off the event log.
    ///
    /// A separate type on purpose. The neuro layer must not be able to name the experiment's own
    /// difficulty type (ExperimentSelfTest forbids it), so it parses the label the experiment
    /// already writes into the difficulty column instead. Any future mapping back onto the chair
    /// task belongs at the experiment boundary, not here.
    ///
    /// <see cref="Unknown"/> is the zero value: a default-constructed difficulty is "not known",
    /// never accidentally "Low".
    /// </summary>
    public enum NeuroDifficulty
    {
        Unknown = 0,
        Low = 1,
        Medium = 2,
        High = 3,
    }

    public static class NeuroDifficultyLabels
    {
        /// <summary>
        /// Parses the difficulty column of the event log ("LOW", "MEDIUM", "HIGH"). Anything else,
        /// including the empty label the legacy fixed-target mode writes, is Unknown.
        /// </summary>
        public static NeuroDifficulty Parse(string label)
        {
            switch ((label ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "LOW": return NeuroDifficulty.Low;
                case "MEDIUM": return NeuroDifficulty.Medium;
                case "HIGH": return NeuroDifficulty.High;
                default: return NeuroDifficulty.Unknown;
            }
        }

        /// <summary>Upper-case label; empty for Unknown, so an absent value reads as absent.</summary>
        public static string ToLabel(NeuroDifficulty difficulty) =>
            difficulty == NeuroDifficulty.Unknown
                ? string.Empty
                : difficulty.ToString().ToUpperInvariant();
    }
}
