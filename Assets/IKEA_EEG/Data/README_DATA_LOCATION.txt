IKEA_EEG — where the experiment data is written
================================================

CSV (one per session):
  %USERPROFILE%\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\<session_id>\events_<session_id>.csv

Recall audio (one WAV per recall phase per trial):
  %USERPROFILE%\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\<session_id>\audio\<trial_id>_immediate_recall.wav
  %USERPROFILE%\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\<session_id>\audio\<trial_id>_delayed_recall.wav

The absolute path is also printed to the Unity Console at SESSION_START.
Application.persistentDataPath for this project resolves to:
  %USERPROFILE%\AppData\LocalLow\DefaultCompany\IKEA_EEG

Derived files written next to the event CSV at the end of a session:
  session_summary_<session_id>.csv        one row per chair trial
  researcher_summary_<session_id>.txt     human-readable debrief
  manual_recall_scoring_<session_id>.csv  blank form for offline recall scoring
These are DERIVED. The event CSV and the WAV files are the authoritative record
and are never modified by them.

CSV columns (in order) - 45 columns, verified against CsvEventSink.k_Header:

   1 timestamp_absolute              24 chair_trial_count
   2 timestamp_relative              25 difficulty
   3 session_id                      26 randomization_seed
   4 trial_id                        27 word_set_id
   5 experiment_state                28 clip_name
   6 room                            29 scheduled_audio_time
   7 event_type                      30 confirmed_audio_time
   8 object_id                       31 experiment_session_id
   9 target_color                    32 run_index
  10 target_size                     33 developer_interrupted
  11 target_shape                    34 platform_language
  12 selected_color                  35 lsl_timestamp
  13 selected_size                   36 protocol_mode
  14 selected_shape                  37 recognition_item_id
  15 correct                         38 recognition_item_class
  16 response_time_ms                39 recognition_phase
  17 word_index                      40 recognition_presentation_order
  18 expected_word                   41 recognition_response
  19 recall_phase                    42 recognition_outcome
  20 transcript                      43 recognition_reaction_time_ms
  21 elapsed_trial_time              44 stimulus_onset_time
  22 notes                           45 stimulus_offset_time
  23 chair_trial_index

The schema is APPEND-ONLY. Columns 1-22 keep their original names AND positions,
so an analysis script written against the earliest format still reads these files
unchanged. Later columns were appended in four groups:

  23-30  repeatable-protocol columns (chair trials, seed, word set, audio timing)
  31-34  multi-run columns. session_id above is THIS RUN's id; these place the
         run inside a sitting.
  35     lsl_timestamp - the EEG-alignment clock. This is the SAME unsmoothed
         local LSL clock recorded in raw_eeg.csv's lsl_timestamp_local_raw
         column, which is what makes the two files alignable.
  36-45  recognition-protocol columns. A FreeRecall run leaves these empty,
         exactly as it leaves the chair columns empty outside Area B.

EEG (written only when an AURA LSL stream was connected during the run):
  raw_eeg.csv    one row per sample: three timestamps, the liblsl time
                 correction, then one column per channel. Values are written
                 EXACTLY as received - no filtering, scaling, unit conversion or
                 re-referencing. A comment header records the stream's own
                 metadata (name, channel count, nominal rate, channel format).

CHAIR SHAPE LABELS CHANGED — READ BEFORE POOLING SESSIONS
------------------------------------------------------------
The target_shape / selected_shape values were renamed to remove two ambiguous,
aesthetic categories. The GEOMETRY did not change; only the words did:

    old value    new value    what the chair actually looks like
    Modern    -> Solid        one continuous flat back panel
    Classic   -> Slatted      separate horizontal back bars, with gaps
    Rounded   -> Curved       cylindrical parts, curved back

Sessions recorded BEFORE this change contain the old words and sessions after
it contain the new ones. A file's shape vocabulary therefore identifies which
build produced it. Pooling old and new sessions requires mapping one set onto
the other using the table above — do NOT assume a reader will do this, and do
not rewrite historical files in place.

timestamp_relative is seconds since SESSION_START from a monotonic Stopwatch;
elapsed_trial_time is seconds since the current TRIAL_START.
randomization_seed appears on EVERY row: it is all that is needed to regenerate
the session's chair trials.
Empty cells mean 'this column does not apply to this event type'.
