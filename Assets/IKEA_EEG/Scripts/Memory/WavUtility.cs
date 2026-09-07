using System;
using System.IO;
using UnityEngine;

namespace IkeaEeg.Memory
{
    /// <summary>
    /// Minimal 16-bit PCM WAV writer.
    ///
    /// Written by hand rather than pulled from a package so the prototype has zero extra
    /// dependencies and the recall audio is stored in a format every analysis tool
    /// (Python/scipy, MATLAB, Audacity, Praat) opens without conversion.
    /// </summary>
    public static class WavUtility
    {
        const int k_HeaderSize = 44;

        /// <summary>
        /// Writes <paramref name="clip"/> to <paramref name="filePath"/> as 16-bit PCM WAV.
        /// </summary>
        /// <param name="sampleCountOverride">
        /// Number of samples per channel actually recorded. Microphone clips are allocated at
        /// full length, so writing the whole clip would append silence and misrepresent the
        /// recording duration. Pass -1 to write the entire clip.
        /// </param>
        /// <returns>True on success.</returns>
        public static bool Save(string filePath, AudioClip clip, int sampleCountOverride = -1)
        {
            if (clip == null)
            {
                Debug.LogWarning("[IKEA_EEG] WavUtility.Save called with a null AudioClip.");
                return false;
            }

            try
            {
                var channels = Mathf.Max(1, clip.channels);
                var totalSamplesPerChannel = clip.samples;

                var samplesPerChannel = sampleCountOverride >= 0
                    ? Mathf.Clamp(sampleCountOverride, 0, totalSamplesPerChannel)
                    : totalSamplesPerChannel;

                if (samplesPerChannel == 0)
                {
                    Debug.LogWarning($"[IKEA_EEG] Refusing to write an empty WAV to '{filePath}'.");
                    return false;
                }

                var floatData = new float[totalSamplesPerChannel * channels];
                clip.GetData(floatData, 0);

                var valueCount = samplesPerChannel * channels;

                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    WriteHeader(writer, valueCount, channels, clip.frequency);

                    for (var i = 0; i < valueCount; i++)
                    {
                        var sample = Mathf.Clamp(floatData[i], -1f, 1f);
                        writer.Write((short)Mathf.RoundToInt(sample * short.MaxValue));
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Failed to write WAV '{filePath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads a 16-bit PCM WAV into interleaved floats. Chunk-walks the file rather than
        /// assuming a 44-byte header, because encoders (including Windows SAPI) insert
        /// LIST/fact chunks before the data.
        /// </summary>
        public static bool Load(string filePath, out float[] samples, out int channels,
            out int sampleRate)
        {
            samples = null;
            channels = 0;
            sampleRate = 0;

            try
            {
                var bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 12)
                    return false;

                var bitsPerSample = 0;
                var dataOffset = -1;
                var dataLength = 0;

                var cursor = 12;   // skip "RIFF" + size + "WAVE"
                while (cursor + 8 <= bytes.Length)
                {
                    var chunkId = System.Text.Encoding.ASCII.GetString(bytes, cursor, 4);
                    var chunkSize = BitConverter.ToInt32(bytes, cursor + 4);

                    if (chunkId == "fmt ")
                    {
                        channels = BitConverter.ToInt16(bytes, cursor + 10);
                        sampleRate = BitConverter.ToInt32(bytes, cursor + 12);
                        bitsPerSample = BitConverter.ToInt16(bytes, cursor + 22);
                    }
                    else if (chunkId == "data")
                    {
                        dataOffset = cursor + 8;
                        dataLength = chunkSize;
                        break;
                    }

                    // Chunks are word-aligned; an odd size is followed by a pad byte.
                    cursor += 8 + chunkSize + (chunkSize % 2);
                }

                if (dataOffset < 0 || bitsPerSample != 16 || channels <= 0)
                {
                    Debug.LogError($"[IKEA_EEG] '{filePath}' is not 16-bit PCM WAV " +
                                   $"(bits={bitsPerSample}, channels={channels}).");
                    return false;
                }

                dataLength = Mathf.Min(dataLength, bytes.Length - dataOffset);
                var count = dataLength / 2;
                samples = new float[count];

                for (var i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(bytes, dataOffset + i * 2) / (float)short.MaxValue;

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Failed to read WAV '{filePath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes raw float samples as 16-bit PCM WAV. Used by the word-clip trimmer.
        /// </summary>
        public static bool SaveSamples(string filePath, float[] samples, int channels, int sampleRate)
        {
            if (samples == null || samples.Length == 0)
                return false;

            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(stream))
                {
                    WriteHeader(writer, samples.Length, channels, sampleRate);

                    foreach (var sample in samples)
                    {
                        var clamped = Mathf.Clamp(sample, -1f, 1f);
                        writer.Write((short)Mathf.RoundToInt(clamped * short.MaxValue));
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Failed to write WAV '{filePath}': {e.Message}");
                return false;
            }
        }

        /// <summary>Signal-level statistics for a recording.</summary>
        public struct SignalStats
        {
            public float peak;              // 0..1
            public float rms;               // 0..1
            public float peakDbfs;          // <= 0, -inf represented as -120
            public float rmsDbfs;
            public float nearZeroPercent;   // proportion of samples below the near-zero floor
            public int sampleCount;

            /// <summary>
            /// A recording is treated as silent when its PEAK never rises meaningfully above
            /// the noise floor. Peak is used rather than RMS because a mostly-quiet recording
            /// with a few spoken words is normal and must not be flagged.
            /// </summary>
            public bool IsSilent(float peakThreshold) => peak < peakThreshold;

            public string ToNotes(float peakThreshold)
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "peak={0:F5}; rms={1:F5}; peak_dbfs={2:F1}; rms_dbfs={3:F1}; " +
                    "near_zero_pct={4:F1}; samples={5}; silent_threshold={6:F5}; silent={7}",
                    peak, rms, peakDbfs, rmsDbfs, nearZeroPercent, sampleCount, peakThreshold,
                    IsSilent(peakThreshold) ? "TRUE" : "FALSE");
            }
        }

        /// <summary>
        /// Measures peak / RMS / dBFS / near-zero proportion.
        ///
        /// Needed because "the sample array is non-empty" proves only that the capture ran,
        /// not that a microphone actually delivered signal. A device that is muted at the OS
        /// level, or an endpoint that exists but is not receiving audio, returns a full buffer
        /// of zeros and would otherwise be reported as a successful recording.
        /// </summary>
        public static SignalStats AnalyzeSignal(float[] samples, int count = -1,
            float nearZeroFloor = 0.0008f)
        {
            var stats = new SignalStats { peakDbfs = -120f, rmsDbfs = -120f };

            if (samples == null || samples.Length == 0)
                return stats;

            var n = count >= 0 ? Mathf.Min(count, samples.Length) : samples.Length;
            if (n == 0)
                return stats;

            var peak = 0f;
            var sumSquares = 0.0;
            var nearZero = 0;

            for (var i = 0; i < n; i++)
            {
                var value = Mathf.Abs(samples[i]);
                if (value > peak)
                    peak = value;

                sumSquares += (double)samples[i] * samples[i];

                if (value < nearZeroFloor)
                    nearZero++;
            }

            stats.sampleCount = n;
            stats.peak = peak;
            stats.rms = Mathf.Sqrt((float)(sumSquares / n));
            stats.nearZeroPercent = nearZero * 100f / n;
            stats.peakDbfs = ToDbfs(stats.peak);
            stats.rmsDbfs = ToDbfs(stats.rms);

            return stats;
        }

        static float ToDbfs(float amplitude)
        {
            return amplitude <= 0.0000001f ? -120f : 20f * Mathf.Log10(amplitude);
        }

        /// <summary>Result of measuring where the audible part of a clip actually starts.</summary>
        public struct SilenceBounds
        {
            public int firstAudibleSample;
            public int lastAudibleSample;
            public bool hasAudio;

            public float LeadMilliseconds(int sampleRate, int channels)
            {
                return firstAudibleSample / (float)(sampleRate * Mathf.Max(1, channels)) * 1000f;
            }
        }

        /// <summary>
        /// Finds the first and last samples above <paramref name="threshold"/>.
        ///
        /// This matters for the verbal memory task: TTS output carries a variable amount of
        /// leading silence (measured at 118-235 ms across the prototype word set). Logging
        /// WORD_PRESENTED at clip onset without trimming would put the marker a different
        /// distance before the actual voice for every word — over 100 ms of jitter, which is
        /// larger than most auditory ERP components of interest.
        /// </summary>
        public static SilenceBounds MeasureSilence(float[] samples, float threshold = 0.027f)
        {
            var bounds = new SilenceBounds { firstAudibleSample = -1, lastAudibleSample = -1 };

            for (var i = 0; i < samples.Length; i++)
            {
                if (Mathf.Abs(samples[i]) <= threshold)
                    continue;

                if (bounds.firstAudibleSample < 0)
                    bounds.firstAudibleSample = i;

                bounds.lastAudibleSample = i;
            }

            bounds.hasAudio = bounds.firstAudibleSample >= 0;
            return bounds;
        }

        /// <summary>
        /// Trims leading/trailing silence, keeping a short pre-roll so the word's attack is
        /// not clipped, and applies a tiny fade at both ends so trimming cannot introduce a
        /// click (a click would itself be an uncontrolled auditory onset).
        /// </summary>
        public static float[] TrimSilence(float[] samples, int sampleRate, int channels,
            float preRollMs = 10f, float tailMs = 90f, float threshold = 0.027f)
        {
            var bounds = MeasureSilence(samples, threshold);
            if (!bounds.hasAudio)
                return samples;

            var frameSize = Mathf.Max(1, channels);
            var preRoll = Mathf.RoundToInt(preRollMs * 0.001f * sampleRate) * frameSize;
            var tail = Mathf.RoundToInt(tailMs * 0.001f * sampleRate) * frameSize;

            var start = Mathf.Max(0, bounds.firstAudibleSample - preRoll);
            var end = Mathf.Min(samples.Length - 1, bounds.lastAudibleSample + tail);

            // Keep the cut on a frame boundary so stereo channels stay paired.
            start -= start % frameSize;

            var length = end - start + 1;
            if (length <= 0)
                return samples;

            var result = new float[length];
            Array.Copy(samples, start, result, 0, length);

            var fade = Mathf.Min(Mathf.RoundToInt(0.004f * sampleRate) * frameSize, length / 2);
            for (var i = 0; i < fade; i++)
            {
                var gain = i / (float)fade;
                result[i] *= gain;
                result[length - 1 - i] *= gain;
            }

            return result;
        }

        static void WriteHeader(BinaryWriter writer, int valueCount, int channels, int sampleRate)
        {
            const short bitsPerSample = 16;
            var byteRate = sampleRate * channels * (bitsPerSample / 8);
            var dataSize = valueCount * (bitsPerSample / 8);

            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(k_HeaderSize - 8 + dataSize);        // ChunkSize
            writer.Write(new[] { 'W', 'A', 'V', 'E' });

            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);                                  // Subchunk1Size (PCM)
            writer.Write((short)1);                            // AudioFormat = PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)(channels * (bitsPerSample / 8)));  // BlockAlign
            writer.Write(bitsPerSample);

            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);
        }
    }
}
