using IkeaEeg.Localization;

namespace IkeaEeg.Interaction
{
    /// <summary>
    /// The single definition of what an Area 0 practice colour IS.
    ///
    /// WHY THIS EXISTS: the practice task previously carried the colour twice — once as a
    /// ChairColor enum used for the on-screen label and once as a free-text string used to look
    /// up the spoken prompt. Two representations of one fact is two chances to disagree, and
    /// they did: the voice asked for one colour while the panel named another.
    ///
    /// Everything now derives from ONE ChairColor value:
    ///   * the canonical name written to the log            -> <see cref="CanonicalName"/>
    ///   * the participant-facing name on screen            -> ExperimentLocalization.PracticeColorName
    ///   * the narration clip key                           -> <see cref="NarrationKey"/>
    ///   * the correctness comparison                       -> ChairColor equality
    ///
    /// There is deliberately no way to construct a practice colour from a string at run time.
    /// </summary>
    public static class PracticeColors
    {
        /// <summary>
        /// The four colours Area 0 practises with, in presentation order (left to right).
        ///
        /// A SUBSET of ChairColor, reusing the palette's materials only. The practice objects are
        /// spheres with no size or shape variation, so nothing here shares a stimulus dimension
        /// with the Area B chair search.
        /// </summary>
        public static readonly ChairColor[] All =
        {
            ChairColor.Red,
            ChairColor.Blue,
            ChairColor.Yellow,
            ChairColor.Green,
        };

        /// <summary>
        /// The CANONICAL, language-independent name: RED / BLUE / YELLOW / GREEN.
        ///
        /// This is what reaches the CSV and the narration key. It is the enum's own name, so it
        /// cannot drift from the value it describes, and renaming what the participant reads
        /// never touches it.
        /// </summary>
        public static string CanonicalName(ChairColor color) =>
            color.ToString().ToUpperInvariant();

        /// <summary>
        /// The key of the spoken "Select the X object." clip for a language and a colour:
        /// "&lt;LANG&gt;_&lt;COLOR&gt;", e.g. ES_BLUE.
        ///
        /// Used by BOTH the authoring-time generator that writes the clips and the run-time
        /// lookup that plays them, so the two cannot key the same clip differently. The colour is
        /// passed as an enum, never as text: the requested colour is never inferred from a clip
        /// name.
        /// </summary>
        public static string NarrationKey(ExperimentLanguage language, ChairColor color) =>
            $"{ExperimentLanguages.ToCode(language)}_{CanonicalName(color)}";

        /// <summary>True for a colour Area 0 is allowed to ask for.</summary>
        public static bool IsPracticeColor(ChairColor color)
        {
            for (var i = 0; i < All.Length; i++)
            {
                if (All[i] == color)
                    return true;
            }

            return false;
        }
    }
}
