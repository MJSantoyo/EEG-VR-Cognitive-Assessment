using System;
using UnityEngine;

namespace IkeaEeg.Memory
{
    /// <summary>
    /// The default, safe provider: records nothing, sends nothing, transcribes nothing.
    ///
    /// It exists so the experimental flow is identical whether or not speech-to-text is
    /// installed. Audio is still captured and timestamped by VoiceRecallManager; only the
    /// automatic scoring is deferred. The CSV records the expected words and the path of the
    /// saved .wav, so recall can be scored manually offline in the meantime.
    ///
    /// This provider deliberately makes NO network calls and requires NO credentials.
    /// </summary>
    [DisallowMultipleComponent]
    public class NullTranscriptionProvider : MonoBehaviour, ISpeechTranscriptionProvider
    {
        public string providerName => "NullTranscriptionProvider";

        public bool isAvailable => false;

        public void Transcribe(AudioClip clip, string wavFilePath, Action<TranscriptionResult> onComplete)
        {
            onComplete?.Invoke(TranscriptionResult.Unavailable(
                providerName,
                "No speech-to-text provider installed. Audio saved for offline scoring."));
        }
    }
}
