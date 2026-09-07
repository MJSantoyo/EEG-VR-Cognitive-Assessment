using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace IkeaEeg.Data
{
    /// <summary>
    /// Writes the raw EEG of one run to disk, next to that run's CSV and WAV files.
    ///
    /// FORMAT: CSV, one row per sample — the local-domain timestamp, the sender's raw timestamp,
    /// the correction between them, then one column per channel. Chosen for
    /// transparency during development: it can be opened, eyeballed and checked against the
    /// event log without any tooling. It is verbose, and moving to a binary format later is a
    /// change to this file alone.
    ///
    /// VALUES ARE WRITTEN EXACTLY AS RECEIVED. No filtering, no scaling, no unit conversion, no
    /// re-referencing, no interpolation of gaps. AURA's metadata does not document units or
    /// scaling, so applying any would be inventing a transformation nobody could reproduce.
    /// R17 round-tripping is used so a value read back is bit-identical to the double received.
    ///
    /// CHANNELS ARE NUMBERED, NOT NAMED. ch1..chN, in stream order. There is deliberately no
    /// Fp1/F3/Cz here: AURA's stream does not publish a verified electrode mapping, and a
    /// guessed montage would attach anatomy to a signal that may not have it — the single most
    /// damaging thing this file could get wrong.
    ///
    /// THREADING: samples arrive at a few hundred hertz on the LSL drain path. They are pushed
    /// onto a lock-free queue and a single background thread does the formatting and the disk
    /// I/O, so the VR frame never waits on a write and never touches the file system.
    /// </summary>
    public class RawEegRecorder : IDisposable
    {
        readonly ConcurrentQueue<(double analysis, double local, double remote, double correction,
            double[] channels)> m_Queue =
            new ConcurrentQueue<(double, double, double, double, double[])>();

        readonly ManualResetEventSlim m_Signal = new ManualResetEventSlim(false);

        Thread m_Worker;
        volatile bool m_Running;

        StreamWriter m_Writer;
        string m_FilePath = string.Empty;
        int m_ChannelCount;

        long m_Queued;
        long m_Written;
        long m_Dropped;

        /// <summary>Where this run's EEG is being written. Empty when not recording.</summary>
        public string filePath => m_FilePath;

        public bool isRecording => m_Running;

        /// <summary>Samples handed to the recorder.</summary>
        public long queuedSamples => Interlocked.Read(ref m_Queued);

        /// <summary>Samples actually written to disk.</summary>
        public long writtenSamples => Interlocked.Read(ref m_Written);

        /// <summary>
        /// Samples refused because the backlog exceeded the safety bound.
        ///
        /// Reported rather than hidden: dropping EEG silently would make a file look complete
        /// when it is not.
        /// </summary>
        public long droppedSamples => Interlocked.Read(ref m_Dropped);

        /// <summary>
        /// Hard bound on the backlog, in samples.
        ///
        /// If the disk cannot keep up the queue is bounded rather than allowed to grow until
        /// the headset runs out of memory mid-session. At a few hundred hertz this is many
        /// seconds of slack — reaching it means something is badly wrong, and the count says so.
        /// </summary>
        const int k_MaxQueuedSamples = 200000;

        /// <summary>
        /// Opens the file and writes the header plus a provenance block.
        ///
        /// The metadata comment lines are what tie this file to the run: without
        /// experiment_session_id / run_index / run_session_id, an EEG file found later is a
        /// column of numbers belonging to nobody in particular.
        /// </summary>
        public bool Start(string directory, string fileName, LslBinding.StreamMetadata stream,
            string experimentSessionId, string runSessionId, int runIndex, string platformLanguage,
            out string problem)
        {
            Stop();

            if (string.IsNullOrEmpty(directory))
            {
                problem = "no session directory";
                return false;
            }

            if (stream.channelCount <= 0)
            {
                problem = $"the stream reports {stream.channelCount} channels";
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);

                m_FilePath = Path.Combine(directory, fileName);
                m_ChannelCount = stream.channelCount;

                m_Writer = new StreamWriter(m_FilePath, false, new UTF8Encoding(false))
                {
                    AutoFlush = false,
                };

                // ---- Provenance -----------------------------------------------------------
                // Comment lines, so every CSV reader skips them by default while the file
                // remains self-describing.
                m_Writer.WriteLine("# IKEA_EEG raw EEG — values exactly as received from LSL.");
                m_Writer.WriteLine("# NO filtering, scaling, unit conversion or re-referencing has been applied.");
                m_Writer.WriteLine("# Channels are numbered in STREAM ORDER. No electrode mapping is claimed:");
                m_Writer.WriteLine("#   the source stream publishes no verified montage, so ch1..chN are positions,");
                m_Writer.WriteLine("#   not scalp locations.");
                m_Writer.WriteLine($"# experiment_session_id={experimentSessionId}");
                m_Writer.WriteLine($"# run_session_id={runSessionId}");
                m_Writer.WriteLine($"# run_index={runIndex.ToString(CultureInfo.InvariantCulture)}");
                m_Writer.WriteLine($"# platform_language={platformLanguage}");
                m_Writer.WriteLine($"# stream_name={stream.name}");
                m_Writer.WriteLine($"# stream_type={stream.type}");
                m_Writer.WriteLine($"# stream_source_id={stream.sourceId}");
                m_Writer.WriteLine($"# channel_count={stream.channelCount.ToString(CultureInfo.InvariantCulture)}");
                m_Writer.WriteLine($"# nominal_srate_hz={stream.nominalSrate.ToString("F6", CultureInfo.InvariantCulture)}");
                m_Writer.WriteLine($"# channel_format={stream.channelFormat}");
                m_Writer.WriteLine("# lsl_timestamp_analysis is the DE-JITTERED analysis time base. USE THIS to cut epochs.");
                m_Writer.WriteLine("#   Derived by fitting a line to (sample index, local raw timestamp); the sender");
                m_Writer.WriteLine("#   delivers 9-sample chunks whose anchor timestamps jitter, while the samples");
                m_Writer.WriteLine("#   themselves are uniform. NO EEG value is affected and no sample is reordered.");
                m_Writer.WriteLine("# lsl_timestamp_local_raw is THIS machine's LSL clock, UNSMOOTHED — the SAME clock as the");
                m_Writer.WriteLine("#   lsl_timestamp column of this run's event CSV. USE THIS COLUMN to align EEG with events.");
                m_Writer.WriteLine("# lsl_timestamp_remote_raw is the sender's own clock, unsmoothed, for provenance only.");
                m_Writer.WriteLine("#   local = remote + time_correction (liblsl time_correction(), added per liblsl docs).");

                var header = new StringBuilder(
                    "lsl_timestamp_analysis,lsl_timestamp_local_raw,lsl_timestamp_remote_raw,time_correction");
                for (var c = 1; c <= m_ChannelCount; c++)
                    header.Append(",ch").Append(c.ToString(CultureInfo.InvariantCulture));

                m_Writer.WriteLine(header.ToString());
                m_Writer.Flush();

                Interlocked.Exchange(ref m_Queued, 0);
                Interlocked.Exchange(ref m_Written, 0);
                Interlocked.Exchange(ref m_Dropped, 0);

                m_Running = true;

                m_Worker = new Thread(WriteLoop)
                {
                    IsBackground = true,   // never keeps the editor or the app alive
                    Name = "IKEA_EEG RawEegRecorder",
                    Priority = System.Threading.ThreadPriority.BelowNormal,
                };

                m_Worker.Start();

                problem = string.Empty;
                return true;
            }
            catch (Exception e)
            {
                problem = $"{e.GetType().Name}: {e.Message}";
                m_Writer = null;
                m_FilePath = string.Empty;
                m_Running = false;
                return false;
            }
        }

        /// <summary>
        /// Hands one sample to the writer. Returns immediately — this is called from the LSL
        /// drain path and must never touch the disk.
        /// </summary>
        public void Write(double analysisTimestamp, double lslTimestampLocal,
            double remoteTimestamp, double timeCorrection, double[] channels)
        {
            if (!m_Running || channels == null || channels.Length < m_ChannelCount)
                return;

            if (Interlocked.Read(ref m_Queued) - Interlocked.Read(ref m_Written) >
                k_MaxQueuedSamples)
            {
                Interlocked.Increment(ref m_Dropped);
                return;
            }

            // Copied: the caller reuses its buffer for the next sample.
            var copy = new double[m_ChannelCount];
            Array.Copy(channels, copy, m_ChannelCount);

            m_Queue.Enqueue((analysisTimestamp, lslTimestampLocal, remoteTimestamp, timeCorrection, copy));
            Interlocked.Increment(ref m_Queued);

            m_Signal.Set();
        }

        void WriteLoop()
        {
            var line = new StringBuilder(32 + m_ChannelCount * 12);

            while (m_Running || !m_Queue.IsEmpty)
            {
                if (!m_Queue.TryDequeue(out var sample))
                {
                    // Nothing waiting: sleep until a sample arrives rather than spinning.
                    m_Signal.Reset();
                    m_Signal.Wait(200);
                    continue;
                }

                try
                {
                    line.Clear();
                    line.Append(sample.analysis.ToString("R", CultureInfo.InvariantCulture));
                    line.Append(',').Append(sample.local.ToString("R", CultureInfo.InvariantCulture));
                    line.Append(',').Append(sample.remote.ToString("R", CultureInfo.InvariantCulture));
                    line.Append(',').Append(sample.correction.ToString("R", CultureInfo.InvariantCulture));

                    for (var c = 0; c < m_ChannelCount; c++)
                    {
                        line.Append(',');

                        // "R" round-trips: the value read back equals the value received,
                        // which is the whole promise of a raw file.
                        line.Append(sample.channels[c].ToString("R", CultureInfo.InvariantCulture));
                    }

                    m_Writer.WriteLine(line.ToString());
                    Interlocked.Increment(ref m_Written);

                    // Periodic flush: often enough that a crash costs little, rarely enough
                    // that the disk is not hit per sample.
                    if (Interlocked.Read(ref m_Written) % 500 == 0)
                        m_Writer.Flush();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[IKEA_EEG] Raw EEG write failed: {e.Message}. Recording " +
                                   "stopped; the file holds everything written up to this point.");
                    m_Running = false;
                    return;
                }
            }
        }

        /// <summary>Drains the backlog and closes the file. Safe to call more than once.</summary>
        public void Stop()
        {
            if (!m_Running && m_Writer == null)
                return;

            m_Running = false;
            m_Signal.Set();

            try
            {
                // Bounded: a stuck writer must not hang the run's finalisation.
                m_Worker?.Join(4000);
            }
            catch
            {
                // Joining is best-effort; the file is closed either way.
            }

            m_Worker = null;

            try
            {
                if (m_Writer != null)
                {
                    m_Writer.Flush();
                    m_Writer.Dispose();

                    Debug.Log($"[IKEA_EEG] Raw EEG file closed: {m_FilePath} " +
                              $"({Interlocked.Read(ref m_Written)} sample(s) written" +
                              (Interlocked.Read(ref m_Dropped) > 0
                                  ? $", {Interlocked.Read(ref m_Dropped)} DROPPED — the disk could " +
                                    "not keep up; this file is incomplete"
                                  : "") + ").");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[IKEA_EEG] Could not close the raw EEG file: {e.Message}");
            }
            finally
            {
                m_Writer = null;
            }
        }

        public void Dispose()
        {
            Stop();
            m_Signal.Dispose();
        }
    }
}
