using System.Collections.Generic;
using IkeaEeg.Localization;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// The research-sourced lexical pools the language-specific word sets are drawn from.
    ///
    /// ============================ METHODOLOGICAL STATUS ============================
    /// These WORDS come from published, language-specific RAVLT adaptations. The IKEA_EEG
    /// TASK does not.
    ///
    /// A standardized RAVLT administration presents 15 words, over repeated learning trials,
    /// with a delayed recall and — in some forms — an interference list. The IKEA_EEG verbal
    /// memory task presents a FIVE-word list once, with an immediate and a delayed recall,
    /// inside a VR session that also contains an unrelated executive task. It is a different
    /// procedure that happens to borrow validated stimuli.
    ///
    /// Therefore:
    ///   * "research-sourced lexical stimuli" is a claim this data supports;
    ///   * "a validated RAVLT administration" is NOT, and must never be written anywhere in
    ///     this project's UI, metadata, filenames or reports;
    ///   * no psychometric equivalence is claimed between any five-word subset used here and
    ///     the fifteen-word form it was drawn from. A subset of a validated instrument is not
    ///     itself a validated instrument.
    ///
    /// The subset actually used is recorded per run — source, form, start index and the exact
    /// words — so any analysis can reconstruct precisely what a participant heard.
    /// ===============================================================================
    ///
    /// Forms are kept SEPARATE and are never merged: mixing items across parallel forms would
    /// destroy the only property that makes them parallel.
    ///
    /// Authoring-time data only. Nothing here ships in a build; the runtime reads the word sets
    /// this produces, not these arrays.
    /// </summary>
    public static class WordSourcePools
    {
        /// <summary>The provenance of a pool, written into every set built from it.</summary>
        public const string SpanishSource = "RAVLT-derived Spanish adaptation";
        public const string JapaneseSource = "RAVLT-derived Japanese adaptation " +
                                             "(Wada 2016; list attributed to Wakamatsu, Anamizu & Kato 2003)";

        /// <summary>
        /// The status line that goes on every set built from these pools, and into the data.
        ///
        /// Deliberately says BOTH things: where the words came from, and what the task is not.
        /// </summary>
        public static string ValidationNote(ExperimentLanguage language)
        {
            switch (language)
            {
                case ExperimentLanguage.Spanish:
                    return "Research-sourced Spanish RAVLT lexical stimuli; IKEA_EEG " +
                           "administration is adapted and not a standardized RAVLT administration.";

                case ExperimentLanguage.Japanese:
                    return "Research-sourced Japanese RAVLT lexical stimuli; IKEA_EEG " +
                           "administration is adapted and not a standardized RAVLT administration.";

                default:
                    return string.Empty;
            }
        }

        /// <summary>One published list, kept whole and identified.</summary>
        public class SourceForm
        {
            /// <summary>Stable id used in the data, e.g. ES_F1_MAIN.</summary>
            public string formId;

            /// <summary>Human-readable description of which published list this is.</summary>
            public string formLabel;

            public ExperimentLanguage language;
            public string source;

            /// <summary>The words IN THEIR PUBLISHED ORDER. Never reordered.</summary>
            public List<string> words;
        }

        /// <summary>
        /// Spanish parallel forms.
        ///
        /// Form 1's main list is the primary pool; its interference list and Forms 2 and 3 are
        /// preserved as distinct, separately identified forms so a study can use them
        /// deliberately rather than by accident.
        /// </summary>
        public static readonly SourceForm[] Spanish =
        {
            new SourceForm
            {
                formId = "ES_F1_MAIN",
                formLabel = "Spanish RAVLT Wordlist 1 — main list",
                language = ExperimentLanguage.Spanish,
                source = SpanishSource,
                words = new List<string>
                {
                    "Tambor", "Cortina", "Campana", "Café", "Escuela",
                    "Padre", "Luna", "Jardín", "Sombrero", "Granjero",
                    "Nariz", "Pavo", "Color", "Casa", "Río",
                },
            },
            new SourceForm
            {
                formId = "ES_F1_INTERFERENCE",
                formLabel = "Spanish RAVLT Wordlist 1 — interference list",
                language = ExperimentLanguage.Spanish,
                source = SpanishSource,
                words = new List<string>
                {
                    "Escritorio", "Guardabosque", "Pájaro", "Zapato", "Horno",
                    "Montaña", "Gafas", "Toalla", "Nube", "Barca",
                    "Cordero", "Rifle", "Lápiz", "Iglesia", "Pez",
                },
            },
            new SourceForm
            {
                formId = "ES_F2_MAIN",
                formLabel = "Spanish RAVLT parallel Form 2 — main list",
                language = ExperimentLanguage.Spanish,
                source = SpanishSource,
                words = new List<string>
                {
                    "Violín", "Árbol", "Bufanda", "Jamón", "Maleta",
                    "Primo", "Tierra", "Escaleras", "Perro", "Plátano",
                    "Pueblo", "Radio", "Cazador", "Cubo", "Campo",
                },
            },
            new SourceForm
            {
                formId = "ES_F3_MAIN",
                formLabel = "Spanish RAVLT parallel Form 3 — main list",
                language = ExperimentLanguage.Spanish,
                source = SpanishSource,
                words = new List<string>
                {
                    "Muñeca", "Espejo", "Uña", "Marinero", "Corazón",
                    "Desierto", "Cara", "Carta", "Cama", "Máquina",
                    "Leche", "Casco", "Música", "Caballo", "Calle",
                },
            },
        };

        /// <summary>
        /// The Japanese list.
        ///
        /// NOT a translation. The Spanish and English lists were not translated to build this —
        /// a translated list carries none of the frequency, imageability or phonological
        /// properties that made the original a usable memory instrument, and the resulting
        /// scores would not be comparable to anything.
        /// </summary>
        public static readonly SourceForm[] Japanese =
        {
            new SourceForm
            {
                formId = "JA_W1_MAIN",
                formLabel = "Japanese RAVLT adaptation — main list",
                language = ExperimentLanguage.Japanese,
                source = JapaneseSource,
                words = new List<string>
                {
                    "大根", "はさみ", "ピアノ", "膝", "とんぼ",
                    "森", "野菜", "まぐろ", "さくら", "緑",
                    "靴下", "雀", "りんご", "鉄", "馬",
                },
            },
        };

        /// <summary>Every form, in build order.</summary>
        public static IEnumerable<SourceForm> AllForms()
        {
            foreach (var form in Spanish)
                yield return form;

            foreach (var form in Japanese)
                yield return form;
        }
    }
}
