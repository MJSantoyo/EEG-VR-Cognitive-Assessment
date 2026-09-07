using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace IkeaEeg.Memory
{
    /// <summary>Scoring of one recall attempt against the presented word list.</summary>
    [Serializable]
    public class RecallScore
    {
        /// <summary>False when there was no transcript to score.</summary>
        public bool scored;

        /// <summary>Expected words, in presentation order.</summary>
        public List<string> expectedWords = new List<string>();

        /// <summary>Recalled words, in the order the participant said them.</summary>
        public List<string> transcribedWords = new List<string>();

        /// <summary>How many of the expected words appeared anywhere in the response.</summary>
        public int numberCorrect;

        /// <summary>True when every recalled target word appeared in presentation order.</summary>
        public bool wordOrderCorrect;

        /// <summary>Words said that were not on the list. Duplicates counted once.</summary>
        public List<string> intrusions = new List<string>();

        /// <summary>Expected words that were NOT recalled at all.</summary>
        public List<string> omissions = new List<string>();

        /// <summary>Where the score came from: the transcription provider, or a human.</summary>
        public string scoringSource = string.Empty;

        public string ExpectedWordsCsv() => string.Join(" ", expectedWords);
        public string TranscribedWordsCsv() => string.Join(" ", transcribedWords);
        public string IntrusionsCsv() => string.Join(" ", intrusions);
        public string OmissionsCsv() => string.Join(" ", omissions);

        /// <summary>
        /// The CSV/notes form. When nothing has been transcribed this reports NA for every
        /// measure rather than 0: "no words recalled" and "we have not scored this yet" are
        /// completely different findings and must never look the same in the data.
        /// </summary>
        public string ToNotesString()
        {
            if (!scored)
                return "scored=FALSE; number_correct=NA; order_correct=NA; intrusions=NA; " +
                       "omissions=NA; reason=no_transcript";

            return string.Format(CultureInfo.InvariantCulture,
                "scored=TRUE; number_correct={0}; order_correct={1}; intrusions={2}; " +
                "omissions={3}; source={4}",
                numberCorrect, wordOrderCorrect ? "TRUE" : "FALSE", intrusions.Count,
                omissions.Count,
                string.IsNullOrEmpty(scoringSource) ? "unspecified" : scoringSource);
        }
    }

    /// <summary>
    /// Compares a recall transcript to the presented word list.
    ///
    /// Fully implemented now even though no transcript is produced yet: the moment a real
    /// <see cref="ISpeechTranscriptionProvider"/> is plugged in, scoring works with no
    /// further changes.
    ///
    /// Scoring rules used (deliberately simple and explicit — change them here, in one place,
    /// if the clinical protocol requires different rules):
    ///   * matching is case-insensitive and ignores punctuation;
    ///   * a target word counts once no matter how often it is repeated;
    ///   * order is correct when the recalled target words form a subsequence of the
    ///     presentation order;
    ///   * an intrusion is any spoken word that is not on the list (duplicates counted once);
    ///   * an omission is any expected word that does not appear in the response.
    ///
    /// The four reported measures, stated once so the analysis and the code agree:
    ///   number_correct — expected words recalled, REGARDLESS of position;
    ///   order_correct  — true when all recalled target words appear in the expected order;
    ///   intrusions     — spoken words not present in the expected five-word set;
    ///   omissions      — expected words not recalled.
    /// </summary>
    public static class RecallScorer
    {
        /// <summary>Value written wherever a measure exists but has not been produced yet.</summary>
        public const string NotScored = "NA";

        public static RecallScore Score(IReadOnlyList<string> expectedWords,
            IReadOnlyList<string> transcribedWords, string scoringSource = "")
        {
            var score = new RecallScore();

            if (expectedWords != null)
                score.expectedWords.AddRange(expectedWords);

            if (transcribedWords == null || transcribedWords.Count == 0)
            {
                // No transcript: every measure stays unset and 'scored' stays false. Nothing
                // here may invent a zero — see RecallScore.ToNotesString.
                score.scored = false;
                score.scoringSource = scoringSource;
                return score;
            }

            score.scored = true;
            score.scoringSource = scoringSource;
            score.transcribedWords.AddRange(transcribedWords);

            var normalizedExpected = new List<string>(score.expectedWords.Count);
            foreach (var w in score.expectedWords)
                normalizedExpected.Add(Normalize(w));

            var matchedExpectedIndices = new List<int>();
            var seenExpected = new HashSet<int>();
            var seenIntrusions = new HashSet<string>();

            foreach (var raw in transcribedWords)
            {
                var word = Normalize(raw);
                if (word.Length == 0)
                    continue;

                var index = normalizedExpected.IndexOf(word);
                if (index >= 0)
                {
                    if (seenExpected.Add(index))
                        matchedExpectedIndices.Add(index);
                }
                else if (seenIntrusions.Add(word))
                {
                    score.intrusions.Add(raw);
                }
            }

            score.numberCorrect = matchedExpectedIndices.Count;

            // Omissions are the complement of the matched set, reported with the ORIGINAL
            // spelling of the expected words so the summary reads like the word list.
            for (var i = 0; i < score.expectedWords.Count; i++)
            {
                if (!seenExpected.Contains(i))
                    score.omissions.Add(score.expectedWords[i]);
            }

            // Order is correct when the indices of the recalled target words are strictly
            // increasing, i.e. they form a subsequence of the presentation order.
            score.wordOrderCorrect = true;
            for (var i = 1; i < matchedExpectedIndices.Count; i++)
            {
                if (matchedExpectedIndices[i] < matchedExpectedIndices[i - 1])
                {
                    score.wordOrderCorrect = false;
                    break;
                }
            }

            return score;
        }

        /// <summary>Splits raw transcript text into comparable word tokens.</summary>
        public static List<string> Tokenize(string transcript)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(transcript))
                return result;

            var current = new StringBuilder();
            foreach (var c in transcript)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                }
                else if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Length = 0;
                }
            }

            if (current.Length > 0)
                result.Add(current.ToString());

            return result;
        }

        static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }
    }
}
