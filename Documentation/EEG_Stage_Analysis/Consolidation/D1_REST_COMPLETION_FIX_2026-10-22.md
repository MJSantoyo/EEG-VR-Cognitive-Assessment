# D1/R1: un descanso saltado o abortado ya no se acepta como baseline (2026-10-22)

Corrección de la decisión D1 / riesgo R1 de [CONSOLIDATION_OVERVIEW_2026-10-21.md](CONSOLIDATION_OVERVIEW_2026-10-21.md).
Entrada de la matriz: REQ-20 en [EVIDENCE_MATRIX.csv](EVIDENCE_MATRIX.csv).
El estado histórico (NO_CUMPLE del 2026-10-21, basado en inferencia del código) se conserva en esa fila.

## Causa

- `ExperimentManager.RunRestBlock` registra `PRE_TASK_REST_END` también cuando el descanso se salta o se aborta.
  El estado real de finalización va en `notes`: `completed=…; developer_skipped=…`.
- `NeuroPhaseEvent.From` no copiaba `notes`.
- `BaselineAggregator.End` marcaba `restEnded=true` para cualquier END, y `SessionBaselineSummary.restCompleted` era ese valor.
- Resultado: un descanso saltado o abortado con al menos una ventana válida quedaba `collected=TRUE`.
- Además, `Finalise` fijaba el resumen de sesión con solo `restCompleted`, aunque `collected` fuera FALSE.
  Así, un intento sin ventanas válidas bloqueaba un descanso completo posterior, que quedaba como `BASELINE_REPEAT_IGNORED`.

## Cambio

| Archivo | Cambio |
|---|---|
| `Scripts/Neuro/BaselineAggregator.cs` | Nuevo `RestCompletion`: `EndNotObserved`, `Completed`, `DeveloperSkipped`, `Aborted`, `Unconfirmed`. Nuevo `RestCompletionLabels.FromEndNotes`: lo que falte o se contradiga queda como `Unconfirmed`, nunca como `Completed`. El resumen guarda `restCompletion`; `restCompleted` pasa a ser derivado. Nuevo `rejectionReason`. `Finalise` acepta solo si `collected`: `hasCompletedSummary` pasa a llamarse `hasAcceptedSummary`. |
| `Scripts/Neuro/NeuroadaptiveController.cs` | `NeuroPhaseEvent` transporta `restCompletion`, leído de `notes`; si no se indica, vale `Unconfirmed`. `OnRestEnd` lo pasa al agregador. Las filas de baseline añaden `rest_completion=…` y `baseline_rejected=…` en `notes`. `Version` pasa a `neuroadaptive-0.1.2-shadow`. |
| `Scripts/Experiment/ExperimentManager.cs` | El texto de `notes` del END se extrae a `FormatRestEndNotes` (estático), con salida idéntica, para que la prueba use el texto real. Sin cambios de flujo, duración, UI ni eventos. |
| `Editor/ExperimentSelfTest.cs` | Los cuatro END existentes pasan a indicar `Completed` explícitamente. Nueva comprobación `CheckRestCompletionGatesBaseline` con los casos A–H, que recorre `FormatRestEndNotes`, `ExperimentEvent`, `EventBus`, `NeuroPhaseEvent.From` y `BaselineAggregator`. |

Sin cambios:
- columnas CSV, nombres de eventos y tipos de fila;
- duraciones y UI;
- filtros, QC y features;
- agregación (`PLACEHOLDER_MEDIAN`), asignación B1/B2/B3, estimador y política (todo sigue OFF).

`BASELINE` sigue significando que el END confirmó la finalización. La aceptación se ve en `session_baseline_collected`.

## Motivos en `shadow_trial_decisions.csv` (columna `notes`)

| Situación | record_type | session_baseline_collected | notes |
|---|---|---|---|
| Saltado por el desarrollador | BASELINE_INCOMPLETE | FALSE | `rest_completion=DEVELOPER_SKIPPED; baseline_rejected=REST_DEVELOPER_SKIPPED` |
| Abortado | BASELINE_INCOMPLETE | FALSE | `rest_completion=ABORTED; baseline_rejected=REST_ABORTED` |
| END sin finalización confirmada | BASELINE_INCOMPLETE | FALSE | `rest_completion=UNCONFIRMED; baseline_rejected=REST_UNCONFIRMED` |
| END no observado (cambio de sesión o ronda) | BASELINE_INCOMPLETE | FALSE | `rest_completion=END_NOT_OBSERVED; baseline_rejected=REST_END_NOT_OBSERVED` |
| Completo sin ventanas válidas | BASELINE | FALSE | `rest_completion=COMPLETED; baseline_rejected=NO_VALID_REST_WINDOWS` |
| Completo con ventanas válidas | BASELINE | TRUE | `rest_completion=COMPLETED; session baseline summary; validity NOT assessed` |
| Descanso tras un baseline ya aceptado | BASELINE_REPEAT_IGNORED | TRUE (el anterior) | `…this rest was not used; rest_completion=<estado>` |

Los eventos `DEVELOPER_REST_SKIPPED` y `PRE_TASK_REST_START`/`END` en `events.csv` no cambian, y siguen conteniendo `completed` y `developer_skipped`.

## Validación

| Tipo | Qué se ejecutó | Resultado |
|---|---|---|
| Reproducción (autotest sintético) | Harness .NET 10 en el scratchpad (`d1harness/`). Compila los archivos Neuro reales y `Core/ExperimentEvent.cs`/`EventTypes.cs`, y añade stubs solo para `UnityEngine.Debug`, `LatestEegFeatures` y la puerta estática de `ShadowModeController`. Los eventos pasan por `EventBus.Publish` y `NeuroPhaseEvent.From`, con las `notes` reales del END. | **HEAD:** 5 FAIL. A, B y C quedan `collected=TRUE` con una ventana válida. En F, el intento saltado deja el completo como `BASELINE_REPEAT_IGNORED`. H2 falla. **Copia de trabajo:** ALL PASS (A–H). |
| Compilación | Unity 6000.3.21f1 `-batchmode` (Editor no abierto) | `Tundra build success`, sin `error CS` |
| Autotest | `-executeMethod IkeaEeg.EditorTools.ExperimentSelfTest.RunSplitFromCommandLine` | `SELF TEST PASSED — 0 failures`; D1-A…D1-H `ok`; los casos de baseline existentes (F, run 2, RESTART) siguen `ok`; cleanup OK |
| Integridad tras el batch | SHA-256 de `.unity`/`.mat`/`.prefab` en `Scenes`, `Materials` y los 4 packs de terceros, más `git status`, antes y después | Idénticos |
| Play Mode / sesión real | — | **No ejecutado** |

En la primera llamada batch puse un namespace erróneo (`IkeaEeg.Editor`). Unity compiló y salió con «class not found» sin ejecutar nada. Después se repitió con `IkeaEeg.EditorTools`.

## Comprobación manual mínima en Unity (sin sesión completa)

1. Abrir `IKEA_EEG_Bootstrap.unity`, con AURA transmitiendo o con un stream sintético LSL.
2. Play, iniciar la ronda y, durante `PRE_TASK_REST`, esperar al menos una ventana válida (≥ 8 s). Después, saltar el descanso con el atajo de desarrollador.
3. Detener Play Mode cuando aparezca la instrucción de Área A.
4. En la carpeta de la sesión, `shadow_trial_decisions.csv` debe tener `BASELINE_INCOMPLETE`, `session_baseline_collected=FALSE` y `baseline_rejected=REST_DEVELOPER_SKIPPED`. En `events.csv`, el END debe seguir con `completed=FALSE; developer_skipped=TRUE`.
5. Opcional: repetir sin saltar el descanso (180 s). Debe aparecer `BASELINE` con `TRUE`.

## Pendiente

- Sesión real o Play Mode con EEG válido y un descanso saltado: no ejecutada.
- La aceptación de un descanso completo con ≥1 ventana válida no implica suficiencia científica. No se añadieron mínimos.

## Verificación en sesión real (añadido 2026-10-22, más tarde)

La usuaria ejecutó en Unity, con AURA real, las dos grabaciones de la comprobación manual. Después las revisó externamente (en ChatGPT, a partir de `SaltandoBaseline.zip` y `ConBaseline.zip`). Claude no tuvo acceso a esos ZIP. Sí encontró y leyó directamente las carpetas originales, en
`%USERPROFILE%\AppData\LocalLow\DefaultCompany\IKEA_EEG\IKEA_EEG_Data\`. Los valores coinciden con los que comunicó la usuaria.

| | Descanso saltado | Descanso completo |
|---|---|---|
| carpeta | `S_20261022_173034_r01_72217e` | `S_20261022_174233_r01_6b5a31` |
| experiment_session_id | `E_20261022_173034_73a99d` | `E_20261022_174233_f38098` |
| filas de datos en `raw_eeg.csv` | 34659 | 74736 |
| `interval_seconds` | 39.217411 | 180.001822 |
| ventanas ofrecidas / válidas | 9 / 3 | 44 / 13 |
| `record_type` | BASELINE_INCOMPLETE | BASELINE |
| `session_baseline_collected` | FALSE | TRUE |
| `notes` | `rest_completion=DEVELOPER_SKIPPED; baseline_rejected=REST_DEVELOPER_SKIPPED; …` | `rest_completion=COMPLETED; session baseline summary; validity NOT assessed …` |
| END en `events_*.csv` | `achieved_duration_s=39.217; completed=FALSE; developer_skipped=TRUE` | `achieved_duration_s=180.002; completed=TRUE; developer_skipped=FALSE` |
| adaptación | SHADOW_ONLY, `proposal_applied=FALSE` | SHADOW_ONLY, `proposal_applied=FALSE` |
| `controller_version` | neuroadaptive-0.1.2-shadow | neuroadaptive-0.1.2-shadow |

Los ficheros leídos de cada carpeta son `shadow_trial_decisions.csv`, `events_<run>.csv`, `raw_eeg.csv` y `eeg_session_metadata.txt`. Las grabaciones no se suben a Git.

Estado por caso:
- **Verificado en sesión real:** A (saltado con ventanas válidas → rechazado) y E (completo con ventanas válidas → aceptado).
- **Solo autotest sintético:** B (abortado), C (END no confirmado), D (completo sin ventanas), F (intento inválido seguido de uno completo en la misma sesión), G (baseline aceptado que no se reemplaza) y H (el reinicio limpia). No se han observado en Play Mode.
- 13 de 44 ventanas válidas **no** valida el baseline científicamente. No hay criterio aprobado de mínimos, agregación ni umbrales.
- B1/B2/B3 por ronda en Unity, la agregación por etapas y los umbrales siguen pendientes (REQ-19, REQ-25 a REQ-27).
