using System;
using System.Collections.Generic;
using UnityEngine;

namespace IkeaEeg.Memory
{
    /// <summary>What a transcription attempt produced.</summary>
    [Serializable]
    public class TranscriptionResult
    {
        /// <summary>True only if a real transcript was produced.</summary>
        public bool isAvailable;

        /// <summary>Raw transcript text. Empty when <see cref="isAvailable"/> is false.</summary>
        public string transcript = string.Empty;

        /// <summary>Word tokens in spoken order.</summary>
        public List<string> words = new List<string>();

        /// <summary>Name of the provider that produced (or declined to produce) this result.</summary>
        public string providerName = string.Empty;

        /// <summary>Why transcription was unavailable, for the CSV notes column.</summary>
        public string unavailableReason = string.Empty;

        public static TranscriptionResult Unavailable(string providerName, string reason)
        {
            return new TranscriptionResult
            {
                isAvailable = false,
                providerName = providerName,
                unavailableReason = reason,
            };
        }
    }

    /// <summary>
    /// Abstraction over speech-to-text.
    ///
    /// The prototype ships with <see cref="NullTranscriptionProvider"/> only: audio is
    /// recorded and timestamped, but nothing is transcribed. Scoring
    /// (<see cref="RecallScorer"/>) is fully implemented and simply has nothing to score yet.
    ///
    /// TO ADD REAL TRANSCRIPTION LATER: implement this interface (e.g. a local Whisper
    /// wrapper) and assign the component to VoiceRecallManager's provider field. Nothing
    /// else changes, and the experiment flow never blocks on transcription being present.
    /// </summary>
    public interface ISpeechTranscriptionProvider
    {
        /// <summary>Display name written to the log.</summary>
        string providerName { get; }

        /// <summary>False when the provider cannot run in the current environment.</summary>
        bool isAvailable { get; }

        /// <summary>
        /// Transcribes a finished recording. Implementations must be non-blocking or fast;
        /// the result is delivered through <paramref name="onComplete"/>.
        /// </summary>
        /// <param name="clip">The recorded audio (may be null if the mic was unavailable).</param>
        /// <param name="wavFilePath">Absolute path of the saved .wav, or empty.</param>
        /// <param name="onComplete">Always invoked exactly once, even on failure.</param>
        void Transcribe(AudioClip clip, string wavFilePath, Action<TranscriptionResult> onComplete);
    }
}
