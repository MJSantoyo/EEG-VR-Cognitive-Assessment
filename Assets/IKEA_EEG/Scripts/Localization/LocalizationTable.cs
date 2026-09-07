using System.Collections.Generic;

namespace IkeaEeg.Localization
{
    /// <summary>
    /// Every participant-facing string, in every supported language, in one place.
    ///
    /// WHY A CODE TABLE RATHER THAN PER-LANGUAGE ASSETS: the three columns sit side by side, so
    /// a missing or drifted translation is visible while editing rather than after a session,
    /// and the self test can assert completeness by walking one structure. Nothing else in the
    /// project holds a participant-facing literal.
    ///
    /// TRANSLATION NOTES:
    ///   * Spanish and Japanese are UI/instruction translations. They are NOT translations of
    ///     experimental stimuli — the verbal-memory word lists are deliberately kept separate
    ///     and are language-specific data, not translations (see WordListDefinition).
    ///   * The chair attribute terms describe VISIBLE GEOMETRY in each language, matching the
    ///     objective-category rule the English terms follow. They are not literal translations
    ///     of English words where a more concrete local term reads better.
    ///   * Placeholders ({COLOR}, {N}, {TOTAL}, ...) must survive translation unchanged.
    /// </summary>
    public static class LocalizationTable
    {
        /// <summary>One string in the three supported languages.</summary>
        public readonly struct Entry
        {
            public readonly string english;
            public readonly string spanish;
            public readonly string japanese;

            public Entry(string english, string spanish, string japanese)
            {
                this.english = english;
                this.spanish = spanish;
                this.japanese = japanese;
            }

            public string For(ExperimentLanguage language)
            {
                switch (language)
                {
                    case ExperimentLanguage.Spanish: return spanish;
                    case ExperimentLanguage.Japanese: return japanese;
                    default: return english;
                }
            }
        }

        static readonly Dictionary<string, Entry> k_Entries = new Dictionary<string, Entry>
        {
            // ---- Language selection ------------------------------------------------------
            {
                LocKeys.LanguageSelectTitle,
                new Entry("Select Language", "Seleccione el idioma", "言語を選んでください")
            },

            // ---- Area 0 -------------------------------------------------------------------
            {
                LocKeys.FamiliarizationInstructions,
                new Entry(
                    "<b>VR Familiarization</b>\n\n" +
                    "Before the cognitive test begins, take a moment to learn the controls.\n\n" +
                    "Point at the practice objects and select them using either trigger that " +
                    "feels comfortable.\n\n" +
                    "When you are ready, press START EXPERIMENT.",

                    "<b>Familiarización con la RV</b>\n\n" +
                    "Antes de comenzar la prueba cognitiva, tómese un momento para aprender los " +
                    "controles.\n\n" +
                    "Apunte a los objetos de práctica y selecciónelos con el gatillo que le " +
                    "resulte más cómodo.\n\n" +
                    "Cuando esté listo, pulse COMENZAR EXPERIMENTO.",

                    "<b>VRの練習</b>\n\n" +
                    "認知課題を始める前に、操作に慣れてください。\n\n" +
                    "コントローラーで練習用の物体を指して、使いやすい方のトリガーで選んでください。\n\n" +
                    "準備ができたら「実験を開始」を押してください。")
            },
            {
                LocKeys.PracticeSelection,
                new Entry("Practice Selection", "Selección de práctica", "選択の練習")
            },
            {
                LocKeys.SelectColor,
                new Entry("Select the {COLOR} object.", "Seleccione el objeto {COLOR}.",
                    "{COLOR}の物体を選んでください。")
            },
            {
                LocKeys.PracticeSuccess,
                new Entry("Great! Selection successful.", "¡Muy bien! Selección correcta.",
                    "できました。選択に成功しました。")
            },
            {
                LocKeys.PracticeReady,
                new Entry("You are ready to begin.", "Ya puede comenzar.", "準備ができました。")
            },
            {
                LocKeys.PracticeTryTarget,
                new Entry("You selected {SELECTED}. Now select the {TARGET} one.",
                    "Seleccionó {SELECTED}. Ahora seleccione el {TARGET}.",
                    "{SELECTED}を選びました。次は{TARGET}を選んでください。")
            },
            {
                LocKeys.PracticeMoreOne,
                new Entry("Good. Try one more selection.",
                    "Bien. Haga una selección más.", "いいですね。もう一回選んでみましょう。")
            },
            {
                LocKeys.PracticeMoreMany,
                new Entry("Good. Try {N} more selections.",
                    "Bien. Haga {N} selecciones más.", "いいですね。あと{N}回選んでみましょう。")
            },
            {
                LocKeys.StartExperiment,
                new Entry("START EXPERIMENT", "COMENZAR EXPERIMENTO", "実験を開始")
            },
            { LocKeys.SkipIntro, new Entry("SKIP INTRO", "OMITIR INTRO", "説明をとばす") },
            {
                LocKeys.ReplayInstructions,
                new Entry("REPLAY INSTRUCTIONS", "REPETIR INSTRUCCIONES", "説明をもう一度")
            },
            { LocKeys.Recenter, new Entry("RECENTER", "RECENTRAR", "位置をリセット") },

            // ---- Controller help ------------------------------------------------------------
            {
                LocKeys.ControllerTitle,
                new Entry("YOUR CONTROLLER", "SU MANDO", "コントローラー")
            },
            {
                LocKeys.ControllerIndexTrigger,
                new Entry("<b>INDEX TRIGGER</b>\n<size=75%>front — selects</size>",
                    "<b>GATILLO DELANTERO</b>\n<size=75%>índice — selecciona</size>",
                    "<b>人さし指のトリガー</b>\n<size=75%>前面 — 選択</size>")
            },
            {
                LocKeys.ControllerGripTrigger,
                new Entry("<b>SIDE / GRIP TRIGGER</b>\n<size=75%>also selects</size>",
                    "<b>GATILLO LATERAL</b>\n<size=75%>también selecciona</size>",
                    "<b>横のトリガー</b>\n<size=75%>これでも選べます</size>")
            },
            {
                LocKeys.ControllerFooter,
                new Entry("Either trigger works.\nUse whichever is comfortable.",
                    "Cualquiera de los dos sirve.\nUse el que le resulte cómodo.",
                    "どちらのトリガーでも使えます。\n使いやすい方をお使いください。")
            },
            { LocKeys.HandLeft, new Entry("LEFT", "IZQUIERDA", "左") },
            { LocKeys.HandRight, new Entry("RIGHT", "DERECHA", "右") },

            // ---- Area A ------------------------------------------------------------------------
            {
                LocKeys.AreaATitle,
                new Entry("COGNITIVE ASSESSMENT", "EVALUACIÓN COGNITIVA", "認知機能の検査")
            },
            {
                LocKeys.AreaAWelcome,
                new Entry("Welcome.\n\nWhen you are ready, point at START with your controller " +
                          "and pull the trigger.",
                    "Bienvenido.\n\nCuando esté listo, apunte a COMENZAR con el mando y apriete " +
                    "el gatillo.",
                    "ようこそ。\n\n準備ができたら、コントローラーで「開始」を指して、トリガーを引いてください。")
            },
            {
                LocKeys.AreaAInstructions,
                new Entry("You will hear five words. Remember them.\n\n" +
                          "After the beep, repeat the five words in the same order.",
                    "Escuchará cinco palabras. Memorícelas.\n\n" +
                    "Después del pitido, repita las cinco palabras en el mismo orden.",
                    "これから5つの言葉が流れます。覚えてください。\n\n" +
                    "音が鳴ったら、同じ順番で5つの言葉を言ってください。")
            },
            {
                LocKeys.EncodingListen,
                new Entry("Listen carefully.", "Escuche con atención.", "よく聞いてください。")
            },
            {
                // RECOGNITION MODE. Separate from AreaAInstructions above, which stays exactly
                // as it is because it is still correct for FreeRecall.
                //
                // ENGLISH-ONLY THIS ITERATION: the ES and JA slots hold the English text on
                // purpose. This is participant-facing methodological instruction for a protocol
                // that is not yet finalised for those languages, and a machine translation would
                // look finished while quietly changing what the participant was asked to do.
                // The gap is visible; a wrong translation would not be.
                LocKeys.RecognitionEncodingInstructions,
                new Entry(
                    "You will see a total of 15 words, one at a time." +
                    "\n\n" +
                    "Try to remember them, because you will be asked about them later.",
                    "You will see a total of 15 words, one at a time." +
                    "\n\n" +
                    "Try to remember them, because you will be asked about them later.",
                    "You will see a total of 15 words, one at a time." +
                    "\n\n" +
                    "Try to remember them, because you will be asked about them later.")
            },
            {
                LocKeys.RecognitionEncodingWatch,
                new Entry("Watch carefully.", "Watch carefully.", "Watch carefully.")
            },
            {
                // RECOGNITION delayed-phase instructions. AreaCInstructions above is UNTOUCHED
                // and still describes spoken free recall, which is correct for FreeRecall.
                //
                // Deliberately contains none of: repeat, recall, say, listen, heard, five words.
                // ENGLISH-ONLY this iteration — ES/JA carry the English text on purpose, as with
                // every other Recognition string.
                LocKeys.RecognitionDelayedInstructions,
                new Entry(
                    "You will now be shown words again.\n\n" +
                    "For each word, select whether you saw it earlier.",
                    "You will now be shown words again.\n\n" +
                    "For each word, select whether you saw it earlier.",
                    "You will now be shown words again.\n\n" +
                    "For each word, select whether you saw it earlier.")
            },
            {
                // FAMILIARIZATION FEEDBACK. Localized in all three languages: this is tutorial
                // interaction feedback, not methodological instruction, and the practice status
                // strings beside it (PracticeSuccess, PracticeTryTarget) are already localized.
                LocKeys.PracticeFeedbackCorrect,
                new Entry("Good job.", "Muy bien.", "よくできました。")
            },
            {
                LocKeys.PracticeFeedbackAnother,
                new Entry("Please select another one.",
                    "Por favor, seleccione otro.",
                    "もう一つ選んでください。")
            },
            {
                LocKeys.PracticeFeedbackIncorrect,
                new Entry("That is not correct. Please try again.",
                    "Eso no es correcto. Inténtelo de nuevo.",
                    "それは正しくありません。もう一度お試しください。")
            },
            {
                LocKeys.PracticeFeedbackComplete,
                new Entry("Great. You are ready to begin the experiment.",
                    "Excelente. Está listo para comenzar el experimento.",
                    "素晴らしいです。実験を開始する準備ができました。")
            },
            {
                LocKeys.ImmediateRecallPrompt,
                new Entry("Please repeat the five words now.",
                    "Por favor, repita ahora las cinco palabras.",
                    "今、5つの言葉を言ってください。")
            },
            {
                // ENGLISH-ONLY THIS ITERATION. The Spanish and Japanese slots intentionally hold
                // the English text: see LocalizationKeys.RecognitionPrompt. Do not machine-
                // translate these - they are participant-facing task instructions.
                LocKeys.RecognitionPrompt,
                new Entry("Was this word shown before?",
                    "Was this word shown before?",
                    "Was this word shown before?")
            },
            {
                LocKeys.RecognitionSeenBefore,
                new Entry("SEEN BEFORE", "SEEN BEFORE", "SEEN BEFORE")
            },
            {
                LocKeys.RecognitionNotSeenBefore,
                new Entry("NOT SEEN BEFORE", "NOT SEEN BEFORE", "NOT SEEN BEFORE")
            },
            {
                // ENGLISH-ONLY THIS ITERATION, as with every Recognition string. ES/JA PENDING.
                // "Item" is short enough that a wrong translation would be cheap to make and
                // easy to miss, and it sits directly above the stimulus.
                LocKeys.RecognitionItemProgress,
                new Entry("Item {N} / {TOTAL}", "Item {N} / {TOTAL}", "Item {N} / {TOTAL}")
            },
            {
                LocKeys.ReadyForAreaB,
                new Entry("Well done.\n\nPress ENTER SHOWROOM to continue.",
                    "Muy bien.\n\nPulse ENTRAR A LA SALA para continuar.",
                    "お疲れさまでした。\n\n「ショールームへ」を押して次に進んでください。")
            },
            { LocKeys.Start, new Entry("START", "COMENZAR", "開始") },
            {
                LocKeys.EnterShowroom,
                new Entry("ENTER SHOWROOM", "ENTRAR A LA SALA", "ショールームへ")
            },
            { LocKeys.Recording, new Entry("Recording…", "Grabando…", "録音中…") },
            {
                LocKeys.RecordingComplete,
                new Entry("Recording complete.", "Grabación completada.", "録音が終わりました。")
            },

            // ---- Area B --------------------------------------------------------------------------
            {
                LocKeys.ExecutiveTaskInstructions,
                new Entry(
                    "<b>Executive Task</b>\n\n" +
                    // ENGLISH UPDATED (this pass): the three properties are now NAMED. The
                    // previous wording said only "a set of characteristics", leaving the
                    // participant to infer what they were being asked to match. ES/JA below
                    // are UNCHANGED and still say "three characteristics" without naming
                    // them: naming them there is a participant-facing wording change that
                    // has not been approved, and an invented translation would look
                    // finished while quietly differing from the English.
                    "Each chair has a SHAPE, a COLOR and a SIZE.\n\n" +
                    "You will be shown one of each.\n" +
                    "Select the ONE chair that matches\nALL THREE.\n\n" +
                    "Use the controller trigger to select.\n\n" +
                    "Press READY when you understand the task.",

                    "<b>Tarea ejecutiva</b>\n\n" +
                    "Se le mostrarán tres características.\n\n" +
                    "Seleccione la ÚNICA silla que cumpla\nLAS TRES características.\n\n" +
                    "Use el gatillo del mando para seleccionar.\n\n" +
                    "Pulse LISTO cuando entienda la tarea.",

                    "<b>実行機能の課題</b>\n\n" +
                    "3つの特徴が示されます。\n\n" +
                    "その3つすべてに合う\nイスを1つだけ選んでください。\n\n" +
                    "コントローラーのトリガーで選びます。\n\n" +
                    "分かったら「準備完了」を押してください。")
            },
            {
                LocKeys.ShapeLegendHint,
                new Entry("The three shapes are shown below.",
                    "Las tres formas se muestran abajo.", "3つの形は下に示しています。")
            },
            {
                LocKeys.ShapeLegendTitle,
                new Entry("The three shapes:", "Las tres formas:", "3つの形：")
            },
            { LocKeys.Ready, new Entry("READY", "LISTO", "準備完了") },
            {
                LocKeys.ChairTargetHeader,
                new Entry("Find the chair that is:", "Busque la silla que sea:",
                    "次のイスを探してください：")
            },
            {
                LocKeys.TaskProgress,
                new Entry("Task {N} of {TOTAL}", "Tarea {N} de {TOTAL}", "課題 {N} / {TOTAL}")
            },
            {
                LocKeys.PointAndSelect,
                new Entry("Point at a chair and pull the trigger to select it.",
                    "Apunte a una silla y apriete el gatillo para seleccionarla.",
                    "イスを指して、トリガーを引いて選んでください。")
            },
            { LocKeys.NextTask, new Entry("Next task…", "Siguiente tarea…", "次の課題…") },
            {
                LocKeys.ChairSelected,
                new Entry("Selection recorded.", "Selección registrada.", "選択を記録しました。")
            },
            { LocKeys.AnswerCorrect, new Entry("Correct. ", "Correcto. ", "正解です。") },
            {
                LocKeys.AnswerIncorrect,
                new Entry("Not the requested chair. ", "No es la silla pedida. ",
                    "指定されたイスではありません。")
            },
            {
                LocKeys.ExecutiveTaskComplete,
                new Entry("Executive task complete", "Tarea ejecutiva completada",
                    "実行機能の課題が終わりました")
            },
            {
                LocKeys.NCorrect,
                new Entry("{N} / {TOTAL} correct", "{N} / {TOTAL} correctas", "{TOTAL}問中 {N}問 正解")
            },
            {
                LocKeys.TasksNotCompleted,
                new Entry("({N} not completed and not counted.)",
                    "({N} sin completar; no se cuentan.)", "（{N}問は未完了のため数えていません。）")
            },
            {
                LocKeys.NoResponsesRecorded,
                new Entry("No chair selections were recorded.",
                    "No se registró ninguna selección de silla.", "イスの選択は記録されませんでした。")
            },
            {
                LocKeys.BlockFooter,
                new Entry("Press EXIT SHOWROOM to continue.",
                    "Pulse SALIR DE LA SALA para continuar.", "「ショールームを出る」を押してください。")
            },
            {
                LocKeys.ExitShowroom,
                new Entry("EXIT SHOWROOM", "SALIR DE LA SALA", "ショールームを出る")
            },

            // ---- Area C ---------------------------------------------------------------------------
            {
                LocKeys.AreaCInstructions,
                new Entry("Once you hear the beep, repeat the five words that were presented at " +
                          "the beginning of the session.",
                    "Cuando escuche el pitido, repita las cinco palabras que se presentaron al " +
                    "principio de la sesión.",
                    "音が鳴ったら、最初に聞いた5つの言葉を言ってください。")
            },
            {
                LocKeys.DelayedRecallPrompt,
                new Entry("Please repeat the five words now.",
                    "Por favor, repita ahora las cinco palabras.",
                    "今、5つの言葉を言ってください。")
            },
            {
                LocKeys.RunComplete,
                new Entry("The session is complete. Thank you.",
                    "La sesión ha terminado. Gracias.", "セッションは終了しました。ありがとうございました。")
            },

            // ---- Results ----------------------------------------------------------------------------
            { LocKeys.ResultsRun, new Entry("Run {N}", "Ronda {N}", "第{N}回") },
            {
                LocKeys.SessionSummaryHeading,
                new Entry("Session Summary", "Resumen de la sesión", "セッションのまとめ")
            },
            {
                LocKeys.StatExecutiveTask,
                new Entry("Executive task", "Tarea ejecutiva", "実行機能の課題")
            },
            {
                LocKeys.StatMeanResponseTime,
                new Entry("Mean response time", "Tiempo medio de respuesta", "平均の反応時間")
            },
            {
                LocKeys.StatMedianResponseTime,
                new Entry("Median response time", "Tiempo mediano de respuesta", "中央値の反応時間")
            },
            {
                LocKeys.StatTotalDuration,
                new Entry("Total duration", "Duración total", "全体の所要時間")
            },
            {
                LocKeys.StatImmediateRecall,
                new Entry("Immediate verbal recall", "Recuerdo verbal inmediato", "直後の言葉の再生")
            },
            {
                LocKeys.StatDelayedRecall,
                new Entry("Delayed verbal recall", "Recuerdo verbal diferido", "時間をおいた言葉の再生")
            },
            {
                LocKeys.RecordingSaved,
                new Entry("Recording saved", "Grabación guardada", "録音を保存しました")
            },
            { LocKeys.NotAvailable, new Entry("Not available", "No disponible", "ありません") },

            // ---- Results: Recognition protocol ---------------------------------------------------------
            // ENGLISH-ONLY THIS ITERATION. The ES and JA slots hold the English text on purpose,
            // exactly as every other Recognition string in this table does. These are
            // participant-facing results for a protocol whose wording is not yet finalised in
            // those languages, and a machine translation would look finished while quietly
            // renaming a signal-detection category. The gap is visible; a wrong translation is not.
            //
            // NOTE FOR THE NEXT PASS: ES/JA for these eleven keys are PENDING, not done.
            {
                LocKeys.RecognitionResultsImmediate,
                new Entry("Immediate Recognition", "Immediate Recognition", "Immediate Recognition")
            },
            {
                LocKeys.RecognitionResultsDelayed,
                new Entry("Delayed Recognition", "Delayed Recognition", "Delayed Recognition")
            },
            {
                LocKeys.RecognitionCorrectResponses,
                new Entry("Correct responses", "Correct responses", "Correct responses")
            },
            {
                // The denominator is named in the string itself. "8 / 10" alone would not say
                // which ten, and unanswered items are deliberately not among them.
                LocKeys.RecognitionAnsweredOf,
                new Entry("{N} of {TOTAL} answered", "{N} of {TOTAL} answered",
                    "{N} of {TOTAL} answered")
            },
            { LocKeys.RecognitionHits, new Entry("Hits", "Hits", "Hits") },
            { LocKeys.RecognitionMisses, new Entry("Misses", "Misses", "Misses") },
            {
                LocKeys.RecognitionCorrectRejections,
                new Entry("Correct rejections", "Correct rejections", "Correct rejections")
            },
            {
                LocKeys.RecognitionFalseAlarms,
                new Entry("False alarms", "False alarms", "False alarms")
            },
            {
                LocKeys.RecognitionNoResponse,
                new Entry("No response", "No response", "No response")
            },
            {
                LocKeys.RecognitionTotalItems,
                new Entry("Total items", "Total items", "Total items")
            },
            {
                LocKeys.RecognitionNoAnswersRecorded,
                new Entry("No answers were recorded.", "No answers were recorded.",
                    "No answers were recorded.")
            },

            // ---- Run management -----------------------------------------------------------------------
            { LocKeys.NewTrial, new Entry("NEW TRIAL", "NUEVA RONDA", "次のラウンド") },
            {
                LocKeys.NewTrialSubtitle,
                new Entry("Save this run and begin another trial",
                    "Guardar esta ronda y comenzar otra", "この回を保存して、次を始めます")
            },
            { LocKeys.Restart, new Entry("RESTART", "REINICIAR", "最初から") },
            {
                LocKeys.RestartSubtitle,
                new Entry("Discard this run and start over from the tutorial",
                    "Descartar esta ronda y empezar desde el tutorial",
                    "この回を破棄して、練習からやり直します")
            },
            { LocKeys.End, new Entry("END", "FINALIZAR", "終了") },
            {
                LocKeys.EndSubtitle,
                new Entry("Save and finish", "Guardar y terminar", "保存して終わります")
            },
            {
                LocKeys.ExperimentAborted,
                new Entry("Experiment aborted", "Experimento interrumpido", "実験を中止しました")
            },
            {
                LocKeys.ExperimentAbortedDetail,
                new Entry("The current run has been stopped.\nPlease wait for the researcher.",
                    "La ronda actual se ha detenido.\nEspere al investigador, por favor.",
                    "この回は停止しました。\n担当者をお待ちください。")
            },
            {
                LocKeys.SessionComplete,
                new Entry("Session complete", "Sesión finalizada", "セッション終了")
            },
            {
                LocKeys.SessionCompleteThanks,
                new Entry("Thank you.\nThe experiment has ended.",
                    "Gracias.\nEl experimento ha terminado.",
                    "ありがとうございました。\n実験は終了しました。")
            },

            // ---- Blocking states -----------------------------------------------------------------------
            {
                LocKeys.AudioUnavailable,
                new Entry("<b>AUDIO OUTPUT UNAVAILABLE</b>\nThe session cannot start until audio " +
                          "works.",
                    "<b>SIN SALIDA DE AUDIO</b>\nLa sesión no puede comenzar hasta que el audio " +
                    "funcione.",
                    "<b>音声が出ません</b>\n音声が使えるようになるまで開始できません。")
            },
            {
                LocKeys.RecheckAudio,
                new Entry("RE-CHECK AUDIO", "COMPROBAR AUDIO", "音声を再確認")
            },
            {
                LocKeys.NoWordSetForLanguage,
                new Entry("<b>This language has no verbal-memory word set.</b>\n" +
                          "The session cannot start. Please tell the researcher.",
                    "<b>Este idioma no tiene lista de palabras.</b>\n" +
                    "La sesión no puede comenzar. Avise al investigador.",
                    "<b>この言語の単語リストがありません。</b>\n" +
                    "セッションを開始できません。担当者にお知らせください。")
            },

            // ---- Chair attributes ------------------------------------------------------------------------
            // The INTERNAL enum names never change; only these displayed strings do. The Spanish
            // and Japanese terms describe the same visible geometry rather than translating the
            // English word literally.
            { LocKeys.ColorPrefix + "Red", new Entry("RED", "ROJA", "赤") },
            { LocKeys.ColorPrefix + "Blue", new Entry("BLUE", "AZUL", "青") },
            { LocKeys.ColorPrefix + "Green", new Entry("GREEN", "VERDE", "緑") },
            { LocKeys.ColorPrefix + "Yellow", new Entry("YELLOW", "AMARILLA", "黄色") },
            { LocKeys.ColorPrefix + "White", new Entry("WHITE", "BLANCA", "白") },
            { LocKeys.ColorPrefix + "Black", new Entry("BLACK", "NEGRA", "黒") },

            // ---- Area 0 practice colours ------------------------------------------------------------------
            // The four practice objects. Deliberately NOT the chair colour terms: those agree
            // with "silla" (feminine) and use the chair-length Japanese forms. A practice object
            // is a sphere, and the practice sentence is "Seleccione el objeto {COLOR}." — so the
            // Spanish is masculine and the Japanese is the short colour word.
            { LocKeys.PracticeColorPrefix + "Red", new Entry("RED", "ROJO", "赤") },
            { LocKeys.PracticeColorPrefix + "Blue", new Entry("BLUE", "AZUL", "青") },
            { LocKeys.PracticeColorPrefix + "Yellow", new Entry("YELLOW", "AMARILLO", "黄") },
            { LocKeys.PracticeColorPrefix + "Green", new Entry("GREEN", "VERDE", "緑") },

            { LocKeys.SizePrefix + "Small", new Entry("SMALL", "PEQUEÑA", "小さい") },
            { LocKeys.SizePrefix + "Medium", new Entry("MEDIUM", "MEDIANA", "中くらい") },
            { LocKeys.SizePrefix + "Large", new Entry("LARGE", "GRANDE", "大きい") },

            // SOLID   = one continuous flat back panel  -> LISA / 平ら
            // SLATTED = separate horizontal bars, gaps  -> CON LISTONES / 横板
            // CURVED  = round, cylindrical, curved back -> CURVA / 曲線
            { LocKeys.ShapePrefix + "Solid", new Entry("SOLID", "LISA", "平ら") },
            { LocKeys.ShapePrefix + "Slatted", new Entry("SLATTED", "CON LISTONES", "横板") },
            { LocKeys.ShapePrefix + "Curved", new Entry("CURVED", "CURVA", "曲線") },
        };

        public static IReadOnlyDictionary<string, Entry> entries => k_Entries;

        public static bool TryGet(string key, out Entry entry) => k_Entries.TryGetValue(key, out entry);
    }
}
