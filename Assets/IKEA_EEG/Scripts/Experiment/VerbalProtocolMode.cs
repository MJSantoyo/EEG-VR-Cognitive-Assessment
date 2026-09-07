namespace IkeaEeg.Experiment
{
    /// <summary>
    /// Which verbal-memory protocol a run uses.
    ///
    /// The two modes are genuinely different experiments, not settings of one experiment: they
    /// differ in stimulus modality, in what the participant is asked to produce, and in what the
    /// resulting numbers mean. They are kept as an explicit choice — rather than one silently
    /// replacing the other — so that data already collected under FreeRecall stays interpretable
    /// and reproducible, and so a run's own record states which protocol produced it.
    /// </summary>
    public enum VerbalProtocolMode
    {
        /// <summary>
        /// The original task. Words are SPOKEN (auditory-only, never displayed); the participant
        /// speaks them back in immediate and delayed free recall, scored by
        /// <see cref="Memory.RecallScorer"/>.
        ///
        /// Fully implemented, hardware-tested, and unchanged by the recognition work.
        /// </summary>
        FreeRecall,

        /// <summary>
        /// CNS Vital Signs-DERIVED recognition memory. Words are VISUAL (displayed one at a
        /// time, never spoken); the participant answers SEEN BEFORE / NOT SEEN BEFORE to targets
        /// mixed with novel lures, immediately and again after the intervening Area B activity.
        ///
        /// "DERIVED" IS LOAD-BEARING. This is NOT CNS Vital Signs and is not equivalent to it.
        /// The official CNS stimulus words have not been established from authorised
        /// methodological documentation, no CNS scoring formula is implemented, and the delayed
        /// item structure is configured rather than assumed. Outputs are transparent
        /// signal-detection tallies — hits, misses, correct rejections, false alarms — and
        /// nothing more.
        /// </summary>
        Recognition,
    }
}
