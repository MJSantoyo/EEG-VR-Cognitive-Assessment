namespace IkeaEeg.Localization
{
    /// <summary>
    /// The platform languages the experiment supports.
    ///
    /// The language is a property of the EXPERIMENT SESSION, not of a run or a trial: every run
    /// of one sitting is presented in the language the participant chose at the start, and NEW
    /// TRIAL never asks again. Only a RESTART — which means a new participant — returns to the
    /// language screen.
    ///
    /// Order is fixed and appended-only: the value is written to the data as a stable code
    /// (EN/ES/JA), never as an integer, so this enum can grow without invalidating old files.
    /// </summary>
    public enum ExperimentLanguage
    {
        /// <summary>No language chosen yet. The participant sees only the language screen.</summary>
        None = 0,

        English = 1,
        Spanish = 2,
        Japanese = 3,
    }

    /// <summary>Conversions between the enum and the codes written to the data.</summary>
    public static class ExperimentLanguages
    {
        /// <summary>The value written to the CSV's platform_language column.</summary>
        public static string ToCode(ExperimentLanguage language)
        {
            switch (language)
            {
                case ExperimentLanguage.English: return "EN";
                case ExperimentLanguage.Spanish: return "ES";
                case ExperimentLanguage.Japanese: return "JA";
                default: return string.Empty;
            }
        }

        public static ExperimentLanguage FromCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return ExperimentLanguage.None;

            switch (code.Trim().ToUpperInvariant())
            {
                case "EN": return ExperimentLanguage.English;
                case "ES": return ExperimentLanguage.Spanish;
                case "JA": return ExperimentLanguage.Japanese;
                default: return ExperimentLanguage.None;
            }
        }

        /// <summary>
        /// The language's own name, in that language — what the selection button shows.
        ///
        /// A participant who reads only Spanish must be able to find their language without
        /// reading English, so the button says "Español", never "Spanish".
        /// </summary>
        public static string NativeName(ExperimentLanguage language)
        {
            switch (language)
            {
                case ExperimentLanguage.English: return "English";
                case ExperimentLanguage.Spanish: return "Español";
                case ExperimentLanguage.Japanese: return "日本語";
                default: return string.Empty;
            }
        }

        /// <summary>Sub-folder name used for this language's narration assets.</summary>
        public static string AudioFolder(ExperimentLanguage language) => ToCode(language);

        /// <summary>The three selectable languages, in presentation order.</summary>
        public static readonly ExperimentLanguage[] Selectable =
        {
            ExperimentLanguage.English,
            ExperimentLanguage.Spanish,
            ExperimentLanguage.Japanese,
        };
    }
}
