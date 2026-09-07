using UnityEngine;

namespace IkeaEeg.Audio
{
    /// <summary>
    /// Generates simple audio cues procedurally at runtime.
    ///
    /// Deliberately no downloaded/licensed audio assets: every sound the experiment makes is
    /// synthesised here from a sine wave, so the project has no third-party audio content and
    /// the exact waveform of each cue is reproducible and documented.
    ///
    /// Each clip gets a short raised-cosine attack/release envelope. Without it a raw sine
    /// burst clicks at onset, and a click is an uncontrolled extra auditory event — exactly
    /// what must not happen right before an EEG-relevant marker.
    /// </summary>
    public static class ToneGenerator
    {
        public const int DefaultSampleRate = 44100;

        /// <summary>Single sine tone with a click-free envelope.</summary>
        /// <param name="name">Clip name (shows in the profiler / inspector).</param>
        /// <param name="frequencyHz">Tone frequency.</param>
        /// <param name="durationSeconds">Total duration.</param>
        /// <param name="amplitude">Peak amplitude, 0..1.</param>
        /// <param name="envelopeSeconds">Attack and release ramp length.</param>
        public static AudioClip CreateTone(string name, float frequencyHz, float durationSeconds,
            float amplitude = 0.5f, float envelopeSeconds = 0.01f)
        {
            var sampleRate = DefaultSampleRate;
            var sampleCount = Mathf.Max(1, Mathf.RoundToInt(durationSeconds * sampleRate));
            var data = new float[sampleCount];

            var envelopeSamples = Mathf.Clamp(
                Mathf.RoundToInt(envelopeSeconds * sampleRate), 1, sampleCount / 2);

            for (var i = 0; i < sampleCount; i++)
            {
                var t = (float)i / sampleRate;
                var value = Mathf.Sin(2f * Mathf.PI * frequencyHz * t);
                data[i] = value * amplitude * Envelope(i, sampleCount, envelopeSamples);
            }

            return FromSamples(name, data, sampleRate);
        }

        /// <summary>Two tones back to back — used for the "words finished" beep.</summary>
        public static AudioClip CreateTwoToneBeep(string name, float firstHz, float secondHz,
            float toneSeconds, float gapSeconds, float amplitude = 0.5f)
        {
            var sampleRate = DefaultSampleRate;
            var toneSamples = Mathf.Max(1, Mathf.RoundToInt(toneSeconds * sampleRate));
            var gapSamples = Mathf.Max(0, Mathf.RoundToInt(gapSeconds * sampleRate));
            var total = toneSamples * 2 + gapSamples;
            var data = new float[total];

            var envelopeSamples = Mathf.Clamp(
                Mathf.RoundToInt(0.008f * sampleRate), 1, toneSamples / 2);

            for (var i = 0; i < toneSamples; i++)
            {
                var t = (float)i / sampleRate;
                var env = amplitude * Envelope(i, toneSamples, envelopeSamples);
                data[i] = Mathf.Sin(2f * Mathf.PI * firstHz * t) * env;
                data[toneSamples + gapSamples + i] = Mathf.Sin(2f * Mathf.PI * secondHz * t) * env;
            }

            return FromSamples(name, data, sampleRate);
        }

        /// <summary>
        /// Short percussive blip used for chair-selection confirmation. A falling pitch reads
        /// as "registered" and is clearly distinct from the recall beeps.
        /// </summary>
        public static AudioClip CreateSelectionBlip(string name, float startHz, float endHz,
            float durationSeconds, float amplitude = 0.5f)
        {
            var sampleRate = DefaultSampleRate;
            var sampleCount = Mathf.Max(1, Mathf.RoundToInt(durationSeconds * sampleRate));
            var data = new float[sampleCount];
            var envelopeSamples = Mathf.Clamp(
                Mathf.RoundToInt(0.005f * sampleRate), 1, sampleCount / 2);

            var phase = 0f;
            for (var i = 0; i < sampleCount; i++)
            {
                var progress = (float)i / sampleCount;
                var frequency = Mathf.Lerp(startHz, endHz, progress);

                // Integrate the instantaneous frequency so the sweep has no phase discontinuity.
                phase += 2f * Mathf.PI * frequency / sampleRate;

                // Exponential decay on top of the envelope gives it a percussive feel.
                var decay = Mathf.Exp(-4f * progress);
                data[i] = Mathf.Sin(phase) * amplitude * decay *
                          Envelope(i, sampleCount, envelopeSamples);
            }

            return FromSamples(name, data, sampleRate);
        }

        static float Envelope(int index, int sampleCount, int envelopeSamples)
        {
            if (index < envelopeSamples)
                return 0.5f * (1f - Mathf.Cos(Mathf.PI * index / envelopeSamples));

            var fromEnd = sampleCount - 1 - index;
            if (fromEnd < envelopeSamples)
                return 0.5f * (1f - Mathf.Cos(Mathf.PI * fromEnd / envelopeSamples));

            return 1f;
        }

        static AudioClip FromSamples(string name, float[] data, int sampleRate)
        {
            var clip = AudioClip.Create(name, data.Length, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
