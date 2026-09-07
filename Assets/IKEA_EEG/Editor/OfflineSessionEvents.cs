using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// Reads one recorded run's event CSV into memory and derives the experimental phases and
    /// recognition items from it.
    ///
    /// WHAT THIS IS NOT. It is not a second definition of the protocol. Every phase boundary and
    /// every recognition attribute below is read from a column or a marker the running experiment
    /// already wrote; nothing is inferred from word counts, durations or expected ordering. If a
    /// marker is absent, the phase is reported ABSENT rather than reconstructed — a phase window
    /// guessed from timing would look identical to a measured one in every plot that used it.
    ///
    /// TIME BASE. Every timestamp exposed here is the run's <c>lsl_timestamp</c> column, which is
    /// this machine's unsmoothed LSL clock. The raw EEG file's own header names that same clock
    /// as the one to align events against, so no conversion happens anywhere in this file.
    ///
    /// The schema is append-only, so columns are resolved BY NAME. A file written before a column
    /// existed reads back with that field empty instead of throwing.
    /// </summary>
    public class OfflineSessionEvents
    {
        // ---------------------------------------------------------------------------------
        // Rows
        // ---------------------------------------------------------------------------------

        public class EventRow
        {
            public int lineNumber;
            public double lslTimestamp = double.NaN;
            public double relativeSeconds = double.NaN;
            public string eventType = string.Empty;
            public string experimentState = string.Empty;
            public string room = string.Empty;
            public string notes = string.Empty;
            public string protocolMode = string.Empty;

            public string recognitionItemId = string.Empty;
            public string recognitionItemClass = string.Empty;
            public string recognitionPhase = string.Empty;
            public string recognitionResponse = string.Empty;
            public string recognitionOutcome = string.Empty;
            public int recognitionOrder = -1;
            public double recognitionReactionMs = double.NaN;

            public bool hasLslTimestamp => !double.IsNaN(lslTimestamp);
        }

        readonly List<EventRow> m_Rows = new List<EventRow>();

        public IReadOnlyList<EventRow> rows => m_Rows;

        public string path { get; private set; } = string.Empty;
        public string sessionId { get; private set; } = string.Empty;
        public string protocolMode { get; private set; } = string.Empty;
        public int rowsWithoutTimestamp { get; private set; }

        public double firstTimestamp { get; private set; } = double.NaN;
        public double lastTimestamp { get; private set; } = double.NaN;

        public double spanSeconds =>
            double.IsNaN(firstTimestamp) || double.IsNaN(lastTimestamp)
                ? double.NaN
                : lastTimestamp - firstTimestamp;

        // ---------------------------------------------------------------------------------
        // Phases
        // ---------------------------------------------------------------------------------

        /// <summary>One annotated interval of the run, in the event clock.</summary>
        public class Phase
        {
            public string name = string.Empty;
            public double start = double.NaN;
            public double end = double.NaN;

            /// <summary>Which marker pair, or which state run, produced this interval.</summary>
            public string derivation = string.Empty;

            public bool present => !double.IsNaN(start) && !double.IsNaN(end) && end > start;
            public double durationSeconds => present ? end - start : double.NaN;
        }

        readonly List<Phase> m_Phases = new List<Phase>();

        public IReadOnlyList<Phase> phases => m_Phases;

        // ---------------------------------------------------------------------------------
        // Recognition items
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// One presented recognition item, with its onset, its response (if any), and the
        /// classification the experiment itself recorded.
        /// </summary>
        public class RecognitionItem
        {
            public string itemId = string.Empty;

            /// <summary>IMMEDIATE or DELAYED, exactly as logged.</summary>
            public string phase = string.Empty;

            /// <summary>TARGET or LURE, exactly as logged.</summary>
            public string itemClass = string.Empty;

            public int order = -1;

            public double onsetTimestamp = double.NaN;
            public double responseTimestamp = double.NaN;

            public string response = string.Empty;
            public string outcome = string.Empty;
            public double reactionMs = double.NaN;

            public bool responded => !double.IsNaN(responseTimestamp);

            public bool isTarget =>
                string.Equals(itemClass, "TARGET", StringComparison.OrdinalIgnoreCase);

            public bool isLure =>
                string.Equals(itemClass, "LURE", StringComparison.OrdinalIgnoreCase);

            public bool isImmediate =>
                string.Equals(phase, "IMMEDIATE", StringComparison.OrdinalIgnoreCase);

            public bool isDelayed =>
                string.Equals(phase, "DELAYED", StringComparison.OrdinalIgnoreCase);
        }

        readonly List<RecognitionItem> m_Items = new List<RecognitionItem>();

        public IReadOnlyList<RecognitionItem> recognitionItems => m_Items;

        /// <summary>Onsets logged with no matching response row — a timeout, or a truncated file.</summary>
        public int recognitionItemsWithoutResponse { get; private set; }

        // ---------------------------------------------------------------------------------
        // Loading
        // ---------------------------------------------------------------------------------

        public static OfflineSessionEvents Load(string filePath, out string problem)
        {
            problem = string.Empty;

            var events = new OfflineSessionEvents { path = filePath };

            if (!File.Exists(filePath))
            {
                problem = "no event CSV at " + filePath;
                return null;
            }

            try
            {
                // Parsed as RECORDS, not lines. The notes column carries the on-screen chair
                // instruction, which contains real newlines inside its quoted cell — reading the
                // file line by line splits those rows into three or four fragments, inflating the
                // row count and creating phantom rows with no event type. Every one of those
                // fragments would then be scanned for phase markers and recognition attributes.
                var records = ParseCsv(File.ReadAllText(filePath));

                if (records.Count < 2)
                {
                    problem = "the event CSV has no data rows";
                    return null;
                }

                var header = records[0];
                var column = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < header.Count; i++)
                {
                    var name = header[i].Trim().TrimStart('﻿');

                    if (!column.ContainsKey(name))
                        column[name] = i;
                }

                for (var line = 1; line < records.Count; line++)
                {
                    var cells = records[line];

                    if (cells.Count <= 1)
                        continue;

                    var row = new EventRow
                    {
                        lineNumber = line + 1,
                        lslTimestamp = ReadDouble(cells, column, "lsl_timestamp"),
                        relativeSeconds = ReadDouble(cells, column, "timestamp_relative"),
                        eventType = ReadString(cells, column, "event_type"),
                        experimentState = ReadString(cells, column, "experiment_state"),
                        room = ReadString(cells, column, "room"),
                        notes = ReadString(cells, column, "notes"),
                        protocolMode = ReadString(cells, column, "protocol_mode"),
                        recognitionItemId = ReadString(cells, column, "recognition_item_id"),
                        recognitionItemClass = ReadString(cells, column, "recognition_item_class"),
                        recognitionPhase = ReadString(cells, column, "recognition_phase"),
                        recognitionResponse = ReadString(cells, column, "recognition_response"),
                        recognitionOutcome = ReadString(cells, column, "recognition_outcome"),
                        recognitionReactionMs =
                            ReadDouble(cells, column, "recognition_reaction_time_ms"),
                    };

                    var order = ReadDouble(cells, column, "recognition_presentation_order");
                    row.recognitionOrder = double.IsNaN(order) ? -1 : (int)Math.Round(order);

                    if (string.IsNullOrEmpty(events.sessionId))
                        events.sessionId = ReadString(cells, column, "session_id");

                    if (string.IsNullOrEmpty(events.protocolMode) &&
                        !string.IsNullOrEmpty(row.protocolMode))
                    {
                        events.protocolMode = row.protocolMode;
                    }

                    if (!row.hasLslTimestamp)
                        events.rowsWithoutTimestamp++;

                    events.m_Rows.Add(row);
                }
            }
            catch (Exception e)
            {
                problem = e.GetType().Name + ": " + e.Message;
                return null;
            }

            events.MeasureSpan();
            events.DerivePhases();
            events.DeriveRecognitionItems();

            return events;
        }

        void MeasureSpan()
        {
            foreach (var row in m_Rows)
            {
                if (!row.hasLslTimestamp)
                    continue;

                if (double.IsNaN(firstTimestamp) || row.lslTimestamp < firstTimestamp)
                    firstTimestamp = row.lslTimestamp;

                if (double.IsNaN(lastTimestamp) || row.lslTimestamp > lastTimestamp)
                    lastTimestamp = row.lslTimestamp;
            }
        }

        // ---------------------------------------------------------------------------------
        // Phase derivation
        // ---------------------------------------------------------------------------------

        /// <summary>The four phases this analysis annotates, and the markers that bound each.</summary>
        static readonly (string name, string startMarker, string endMarker, string[] states)[]
            k_PhaseDefinitions =
            {
                ("Encoding", "WORD_ENCODING_START", "WORD_ENCODING_END",
                    new[] { "WordEncoding" }),

                ("Immediate Recognition", "IMMEDIATE_RECOGNITION_START",
                    "IMMEDIATE_RECOGNITION_END", new[] { "ImmediateRecognition" }),

                ("Area B", "AREA_B_ENTER", "CHAIR_BLOCK_COMPLETE",
                    new[]
                    {
                        "ReadyForAreaB", "AreaBInstructions", "ChairInstruction",
                        "ChairSelection", "ChairTrialFeedback", "ChairInterTrialInterval",
                    }),

                ("Delayed Recognition", "DELAYED_RECOGNITION_START", "DELAYED_RECOGNITION_END",
                    new[] { "DelayedRecognition" }),
            };

        void DerivePhases()
        {
            foreach (var definition in k_PhaseDefinitions)
            {
                var phase = new Phase { name = definition.name };

                var start = FirstTimestampOf(definition.startMarker);
                var end = LastTimestampOf(definition.endMarker);

                if (!double.IsNaN(start) && !double.IsNaN(end) && end > start)
                {
                    phase.start = start;
                    phase.end = end;
                    phase.derivation = definition.startMarker + " .. " + definition.endMarker;
                }
                else
                {
                    // Fall back to the span of rows logged while the run was in one of the
                    // phase's own states. Reported as a fallback so it is never mistaken for a
                    // marker-bounded interval.
                    double stateStart = double.NaN, stateEnd = double.NaN;

                    foreach (var row in m_Rows)
                    {
                        if (!row.hasLslTimestamp)
                            continue;

                        var match = false;

                        foreach (var state in definition.states)
                        {
                            if (string.Equals(row.experimentState, state,
                                    StringComparison.Ordinal))
                            {
                                match = true;
                                break;
                            }
                        }

                        if (!match)
                            continue;

                        if (double.IsNaN(stateStart) || row.lslTimestamp < stateStart)
                            stateStart = row.lslTimestamp;

                        if (double.IsNaN(stateEnd) || row.lslTimestamp > stateEnd)
                            stateEnd = row.lslTimestamp;
                    }

                    if (!double.IsNaN(stateStart) && !double.IsNaN(stateEnd) &&
                        stateEnd > stateStart)
                    {
                        phase.start = stateStart;
                        phase.end = stateEnd;
                        phase.derivation = "FALLBACK: span of rows in state(s) " +
                                           string.Join("/", definition.states) +
                                           " (markers " + definition.startMarker + " / " +
                                           definition.endMarker + " not both present)";
                    }
                    else
                    {
                        phase.derivation = "ABSENT: neither " + definition.startMarker + " / " +
                                           definition.endMarker + " nor any row in state(s) " +
                                           string.Join("/", definition.states);
                    }
                }

                m_Phases.Add(phase);
            }
        }

        public double FirstTimestampOf(string eventType)
        {
            foreach (var row in m_Rows)
            {
                if (row.hasLslTimestamp &&
                    string.Equals(row.eventType, eventType, StringComparison.Ordinal))
                {
                    return row.lslTimestamp;
                }
            }

            return double.NaN;
        }

        public double LastTimestampOf(string eventType)
        {
            var found = double.NaN;

            foreach (var row in m_Rows)
            {
                if (row.hasLslTimestamp &&
                    string.Equals(row.eventType, eventType, StringComparison.Ordinal))
                {
                    found = row.lslTimestamp;
                }
            }

            return found;
        }

        public Phase FindPhase(string name)
        {
            foreach (var phase in m_Phases)
            {
                if (string.Equals(phase.name, name, StringComparison.OrdinalIgnoreCase))
                    return phase;
            }

            return null;
        }

        /// <summary>The phase containing a timestamp, or empty when it falls outside all of them.</summary>
        public string PhaseAt(double timestamp)
        {
            foreach (var phase in m_Phases)
            {
                if (phase.present && timestamp >= phase.start && timestamp <= phase.end)
                    return phase.name;
            }

            return string.Empty;
        }

        // ---------------------------------------------------------------------------------
        // Recognition items
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Pairs each RECOGNITION_ITEM_ONSET with the next RECOGNITION_RESPONSE carrying the same
        /// phase and presentation order.
        ///
        /// Matched on the logged identity rather than on "the next response row" so that a
        /// timed-out item cannot silently absorb the following item's response — which would
        /// mislabel two epochs at once and stay invisible in every summary.
        /// </summary>
        void DeriveRecognitionItems()
        {
            var responses = new List<EventRow>();

            foreach (var row in m_Rows)
            {
                if (string.Equals(row.eventType, "RECOGNITION_RESPONSE", StringComparison.Ordinal))
                    responses.Add(row);
            }

            foreach (var row in m_Rows)
            {
                if (!string.Equals(row.eventType, "RECOGNITION_ITEM_ONSET",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var item = new RecognitionItem
                {
                    itemId = row.recognitionItemId,
                    phase = row.recognitionPhase,
                    itemClass = row.recognitionItemClass,
                    order = row.recognitionOrder,
                    onsetTimestamp = row.lslTimestamp,
                };

                foreach (var response in responses)
                {
                    if (response.recognitionOrder != item.order)
                        continue;

                    if (!string.Equals(response.recognitionPhase, item.phase,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!double.IsNaN(item.onsetTimestamp) && response.hasLslTimestamp &&
                        response.lslTimestamp < item.onsetTimestamp)
                    {
                        continue;
                    }

                    item.responseTimestamp = response.lslTimestamp;
                    item.response = response.recognitionResponse;
                    item.outcome = response.recognitionOutcome;
                    item.reactionMs = response.recognitionReactionMs;

                    // The item class is carried on both rows; prefer the onset's, but accept the
                    // response's when the onset did not carry one.
                    if (string.IsNullOrEmpty(item.itemClass))
                        item.itemClass = response.recognitionItemClass;

                    if (string.IsNullOrEmpty(item.itemId))
                        item.itemId = response.recognitionItemId;

                    break;
                }

                if (!item.responded)
                    recognitionItemsWithoutResponse++;

                m_Items.Add(item);
            }
        }

        /// <summary>Counts of the four signal-detection outcomes, exactly as the run logged them.</summary>
        public Dictionary<string, int> OutcomeCounts(string phaseFilter)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in m_Items)
            {
                if (!string.IsNullOrEmpty(phaseFilter) &&
                    !string.Equals(item.phase, phaseFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key = string.IsNullOrEmpty(item.outcome) ? "NO_RESPONSE" : item.outcome;

                counts.TryGetValue(key, out var n);
                counts[key] = n + 1;
            }

            return counts;
        }

        // ---------------------------------------------------------------------------------
        // CSV helpers
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Parses a whole CSV document into records, honouring double-quoted cells that contain
        /// commas AND newlines.
        ///
        /// Both matter in this schema. The notes column routinely contains commas, so a naive
        /// split on ',' shifts every column after it and moves recognition attributes into the
        /// wrong fields. It also contains real newlines — the chair instruction is logged with
        /// the line breaks it was displayed with — so a naive split on '\n' turns one row into
        /// several, each of which then looks like an event with no type.
        ///
        /// A quote only opens a quoted section at the START of a cell; a bare quote in the middle
        /// of unquoted text is data. That matters here because the notes column contains
        /// attribute values written as target="..." inside an otherwise unquoted cell.
        /// </summary>
        public static List<List<string>> ParseCsv(string text)
        {
            var records = new List<List<string>>();

            if (string.IsNullOrEmpty(text))
                return records;

            var row = new List<string>();
            var cell = new System.Text.StringBuilder();
            var inQuotes = false;
            var atCellStart = true;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }

                    continue;
                }

                if (c == '"' && atCellStart)
                {
                    inQuotes = true;
                    atCellStart = false;
                    continue;
                }

                if (c == ',')
                {
                    row.Add(cell.ToString());
                    cell.Length = 0;
                    atCellStart = true;
                    continue;
                }

                if (c == '\r')
                    continue;

                if (c == '\n')
                {
                    row.Add(cell.ToString());
                    cell.Length = 0;
                    atCellStart = true;

                    records.Add(row);
                    row = new List<string>();
                    continue;
                }

                cell.Append(c);
                atCellStart = false;
            }

            // Whatever is left after the final newline. A file ending in a newline leaves nothing,
            // and an empty trailing record is not added.
            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                records.Add(row);
            }

            return records;
        }

        static string ReadString(List<string> cells, Dictionary<string, int> column, string name)
        {
            if (!column.TryGetValue(name, out var index) || index >= cells.Count)
                return string.Empty;

            return cells[index].Trim();
        }

        static double ReadDouble(List<string> cells, Dictionary<string, int> column, string name)
        {
            var text = ReadString(cells, column, name);

            if (string.IsNullOrEmpty(text))
                return double.NaN;

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value)
                ? value
                : double.NaN;
        }
    }
}
