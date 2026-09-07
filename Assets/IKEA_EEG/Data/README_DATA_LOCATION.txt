IKEA_EEG — where the experiment data is written
================================================

CSV (one per session):
  C:\Users\Mariana\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\<session_id>\events_<session_id>.csv

Recall audio (one WAV per recall phase per trial):
  C:\Users\Mariana\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\<session_id>\audio\<trial_id>_immediate_recall.wav
  C:\Users\Mariana\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\<session_id>\audio\<trial_id>_delayed_recall.wav

The absolute path is also printed to the Unity Console at SESSION_START.
Application.persistentDataPath for this project resolves to:
  C:\Users\Mariana\AppData\LocalLow\DefaultCompany\IKEA_EEG

Derived files written next to the event CSV at the end of a session:
  session_summary_<session_id>.csv        one row per chair trial
  researcher_summary_<session_id>.txt     human-readable debrief
  manual_recall_scoring_<session_id>.csv  blank form for offline recall scoring
These are DERIVED. The event CSV and the WAV files are the authoritative record
and are never modified by them.

CSV columns (in order):
  timestamp_absolute, timestamp_relative, session_id, trial_id, experiment_state,
  room, event_type, object_id, target_color, target_size, target_shape,
  selected_color, selected_size, selected_shape, correct, response_time_ms,
  word_index, expected_word, recall_phase, transcript, elapsed_trial_time, notes,
  chair_trial_index, chair_trial_count, difficulty, randomization_seed,
  word_set_id, clip_name, scheduled_audio_time, confirmed_audio_time

The last eight columns were APPENDED, so the first 22 keep their original
names and positions and older analysis scripts still work.

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
