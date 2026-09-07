namespace IkeaEeg.Experiment
{
    /// <summary>
    /// The two steps of the Area 0 familiarization task, plus its completed state.
    ///
    /// WHY THIS IS EXPLICIT. The two steps have DIFFERENT pass conditions, and treating them as
    /// one repeated condition is exactly what broke on hardware: after correctly selecting the
    /// requested colour, the participant was asked to "select another one" and then told that
    /// selecting another colour was wrong — because the second selection was still being judged
    /// against the first step's requested colour.
    ///
    /// Naming the phases makes the pass condition a property of the phase rather than something
    /// re-derived at each selection, so the two can no longer be confused.
    /// </summary>
    public enum PracticePhase
    {
        /// <summary>
        /// Step 1. The participant must select the object whose colour was REQUESTED. Any other
        /// colour is incorrect: it neither advances the task nor changes the request, and the
        /// participant retries until they get it right.
        /// </summary>
        RequestedColorSelection,

        /// <summary>
        /// Step 2. The participant must select any valid practice object of a DIFFERENT colour
        /// from the one they just correctly selected.
        ///
        /// There is no "correct" colour here — the point is repeating the interaction by their
        /// own choice, not following a second instruction. Re-selecting the requested colour is
        /// not an ERROR, it simply is not "another one", so it prompts again rather than
        /// producing negative feedback.
        /// </summary>
        DifferentColorSelection,

        /// <summary>
        /// Both steps are done. START EXPERIMENT is emphasised, but the participant still
        /// presses it themselves — familiarization never auto-starts the experiment.
        /// </summary>
        Complete,
    }
}
