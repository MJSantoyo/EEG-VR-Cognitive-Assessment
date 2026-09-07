# Decision Record — Recognition-Memory Protocol (Version 8)

Written 2026-08-25. Records decisions and their reasons for the recognition-memory pass.
Only facts verified against the project are stated as implemented.

> **Status tags.** **[VERIFIED]** executed with evidence · **[IMPLEMENTED, UNTESTED ON HARDWARE]**
> code exists and passes editor validation, never run in a headset · **[PLACEHOLDER]** structure
> exists, content pending · **[NOT IMPLEMENTED]**.

---

## 1. We do not claim equivalence to CNS Vital Signs

The implemented task is **CNS-derived, not CNS-equivalent**, and nothing in the project may
describe it otherwise.

| | CNS Vital Signs (as described in the brief) | This implementation |
|---|---|---|
| Targets | 15 words | 15, **configured** (read from the asset) |
| Immediate | recognition among targets + distractors | recognition, targets + lures |
| Delayed | recognition later in the battery | recognition after Area B |
| Stimulus words | the official CNS list | **placeholders — not CNS stimuli** |
| Scoring | proprietary CNS scoring | **none — raw tallies only** |

**What is deliberately absent:** the official CNS word list, any CNS scoring formula, any
composite index, and any normative comparison. The outputs are four transparent
signal-detection counts: hits, misses, correct rejections, false alarms. No d′, no corrected
recognition index, no derived score of any kind. Those are analysis decisions to be made against
a defined methodology; inventing one now would place an unvalidated number in the data where it
would later be mistaken for a result.

## 2. Free recall was preserved, not replaced

The original five-word spoken free-recall task is hardware-verified. Rather than delete it, a
`VerbalProtocolMode` enum selects between `FreeRecall` and `Recognition`.

**Reason.** The two are genuinely different experiments — different stimulus modality, different
response, different meaning of the resulting numbers. Data already collected under FreeRecall
stays interpretable and reproducible, and every CSV row now states which protocol produced it
(`protocol_mode`).

`ExperimentConfig.protocolMode` defaults to **`Recognition`**, which is the stated objective of
this pass. Switching back to `FreeRecall` restores the original behaviour exactly; the FreeRecall
code path was left character-for-character unchanged, and the self-test asserts both paths still
exist. **[VERIFIED]**

## 3. Stimulus modality is inverted between the protocols — and enforced

| | FreeRecall | Recognition |
|---|---|---|
| Encoding words | **spoken**, never displayed | **displayed**, never spoken |
| Response | speech, recorded to WAV | button press |

`ExperimentUIController.SetWordDisplay` had to be **added** — it was previously and deliberately
absent to make displaying a word impossible under the auditory-only paradigm. The constraint
moved from "the method does not exist" to "only the Recognition path calls it", and the self-test
asserts that separation by scanning the visual-encoding routine for `AudioCue.SpokenWord` and
`GetWordClip` and requiring neither. **[VERIFIED]**

Spoken *instructions* and narration are unaffected in both protocols.

## 4. Word-list data architecture

`RecognitionWordList` (ScriptableObject) holds three independent lists:

- `targetWords` — presented at encoding, then re-presented as Targets
- `immediateRecognitionLures` — novel items, immediate phase only
- `delayedRecognitionLures` — novel items, delayed phase only

**Why three lists rather than one lure pool.** A lure reused across phases is no longer novel the
second time, which silently changes what a false alarm means. Separate lists make that impossible
by construction; `Validate` additionally rejects target/lure overlap, cross-phase lure reuse,
duplicates and blanks. **[VERIFIED]** by self-test.

### The shipped words are placeholders **[PLACEHOLDER]**

`Assets/IKEA_EEG/Data/RecognitionWordList_PLACEHOLDER.asset` contains 15 + 15 + 15 ordinary
English nouns, created only so the architecture can be exercised end to end.

> **DEVELOPMENT ONLY — NOT CNS STIMULI.** They were not derived from the CNS list, are
> uncontrolled for frequency, imageability, length and semantic category, carry no norms, and
> must not be used for data collection.

`validatedForResearch` is `false` and the asset's own provenance field says all of this, so a
data file produced from them is self-describing. The builder never overwrites an existing asset.

## 5. Delayed recognition is configured, not assumed

The item-level structure of delayed recognition is **not established**. Nothing hard-codes 15 or
30 for the delayed phase: its size is read from `delayedRecognitionLures`, and the self-test
proves a delayed set of a different size works. **[VERIFIED]**

## 6. Interaction: reuse, not a new package

The response buttons use `XRSimpleInteractable` + `selectEntered` — the same far-ray selection the
chairs and practice objects already use and that is verified on hardware. **No new interaction
package, no new input action, no change to the XR rig or OpenXR configuration.**

A poke/direct-touch interactor was rejected: it requires reaching a precise depth, which is slower
and less comfortable for a task answered thirty times in a row.

**Target size is a correctness property, not styling.** The buttons are 0.62 m × 0.26 m so
answering costs a coarse point rather than careful aiming — reaction time is being measured, and
time spent aiming would be recorded as time spent deciding. **[VERIFIED]** 0.62 m by self-test.

**Duplicate input** is prevented at three levels: each button latches on its first accepted
selection; the panel disarms *both* buttons the instant either fires; and the manager ignores any
response after the first for the current item.

## 7. Audio cues confirm registration, never correctness

Two cues were added:

- `StimulusTransition` — 60 ms, 990 Hz, marks each new visual word. Short because it fires once
  per word; anything longer would compete with the stimulus for attention.
- `ResponseConfirm` — identical for **every** answer.

**The confirmation sound must not leak correctness.** A correctness cue would turn a memory test
into a learning trial and contaminate the delayed phase. No reward or error sounds were added.

## 8. Logging

Ten columns were **appended** to the CSV (35 → **45**). The existing 35 keep their names *and*
positions, so a script written against the previous format reads the new files unchanged. A
FreeRecall run leaves the new columns empty, exactly as chair columns are empty outside Area B.

`protocol_mode`, `recognition_item_id`, `recognition_item_class`, `recognition_phase`,
`recognition_presentation_order`, `recognition_response`, `recognition_outcome`,
`recognition_reaction_time_ms`, `stimulus_onset_time`, `stimulus_offset_time`

Encoding logs onset **and** offset as separate events: a visual stimulus has a real duration, and
an epoch cut around "the word appeared" differs from one cut around "the word disappeared".

## 9. LSL markers

The marker system was **not redesigned**. Verified: `push_sample` passes timestamp `0.0`, so
**liblsl stamps each marker at push time** — no manual timestamp reconstruction exists anywhere.
**[VERIFIED]** by inspection of `LslBinding.PushSample`.

Added: `WORD_OFFSET`, `IMMEDIATE_RECOGNITION_START/END`, `DELAYED_RECOGNITION_START/END`,
`RECOGNITION_ITEM_ONSET`, `RECOGNITION_RESPONSE`, `RECOGNITION_ITEM_TIMEOUT`.

**Existing event types were reused, not renamed.** `WORD_ENCODING_START` / `WORD_PRESENTED` /
`WORD_ENCODING_END` already mean "encoding began", "one stimulus was delivered", "encoding ended"
— which is still exactly what happens. Renaming them would break every existing analysis script
for no gain in meaning; the change of modality is carried in the event's fields, not its name.
Likewise `CHAIR_SELECTED` was not renamed to `OBJECT_SELECTED`, and `RUN_ABORTED` not to `ABORT`.

`BASELINE_START` / `BASELINE_END` **[NOT IMPLEMENTED]** — no baseline phase exists in the flow yet.

## 10. Language

English-only this iteration, by instruction. **The localization architecture was not removed or
bypassed** — three new keys were added to the existing table. Their Spanish and Japanese slots
deliberately hold the English string rather than a machine translation: a participant-facing
clinical instruction must be written by someone who speaks the language, and an invented
translation would look finished while quietly changing what was asked.

---

## Pending decisions — none of these are resolved

1. **The official CNS word list.** Blocks all data collection under this protocol.
2. **Validated ES/JA lists** and translated recognition instructions.
3. **Delayed-phase item structure** — how many items, and which.
4. **Whether recognition replaces or supplements free recall** in the final protocol.
5. **Response timeout** (currently 15 s, configurable) — no methodological basis yet.
6. **Encoding duration** — 2 s per word as instructed; inter-word gap 0.25 s is an engineering
   default with no methodological basis.
7. **Whether a baseline phase is needed**, and where.
