using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace OliverAssistant
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.Run(new TrayApplicationContext());
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private readonly string _appDir;
        private readonly string _configPath;
        private readonly string _settingsPath;
        private readonly string _logPath;
        private readonly NotifyIcon _trayIcon;
        private readonly ToolStripMenuItem _commandsItem;
        private readonly ToolStripMenuItem _micItem;
        private readonly ToolStripMenuItem _debugItem;
        private readonly ToolStripMenuItem _notificationsItem;
        private readonly ToolStripMenuItem _gamingModeItem;
        private readonly ToolStripMenuItem _remindHotkeyItem;
        private SpeechRecognitionEngine _recognizer;
        private WhisperEngine _whisperEngine;
        private Config _config;
        private Dictionary<string, CommandEntry> _commandLookup;
        private Dictionary<string, CommandEntry> _wildcardTriggerLookup;
        private bool _debugNotifications;
        private bool _notificationsEnabled;
        private bool _commandsEnabled;
        private bool _gamingModeEnabled;
        private bool _remindHotkeyEnabled;
        private PushToTalkHotkey _pushToTalk;
        private WaveInRecorder _pttRecorder;
        private readonly object _pttLock = new object();
        private bool _pttActive;
        private RemindHotkey _remindHotkey;
        private WaveInRecorder _remindRecorder;
        private readonly object _remindLock = new object();
        private bool _remindActive;

        public TrayApplicationContext()
        {
            _appDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(_appDir, "..", "commands.json");
            _settingsPath = Path.Combine(_appDir, "..", "settings.json");
            _logPath = Path.Combine(_appDir, "..", "cosmo.log");

            var settings = LoadSettings();
            _notificationsEnabled = settings.NotificationsEnabled;
            _debugNotifications = settings.DebugNotifications;
            _commandsEnabled = settings.CommandsEnabled;
            _gamingModeEnabled = settings.GamingModeEnabled;
            _remindHotkeyEnabled = !settings.RemindHotkeyDisabled;

            var menu = new ContextMenuStrip();
            _commandsItem = new ToolStripMenuItem("Enable voice commands (open apps, lock, search, etc.)", null, OnToggleCommands) { CheckOnClick = true, Checked = _commandsEnabled };
            _gamingModeItem = new ToolStripMenuItem("Gaming mode (also dictate with Tab)", null, OnToggleGamingMode) { CheckOnClick = true, Checked = _gamingModeEnabled };
            _remindHotkeyItem = new ToolStripMenuItem("Hold Insert to create a reminder", null, OnToggleRemindHotkey) { CheckOnClick = true, Checked = _remindHotkeyEnabled };
            _micItem = new ToolStripMenuItem("Mic: (detecting...)") { Enabled = false };
            _notificationsItem = new ToolStripMenuItem("Enable notifications", null, OnToggleNotifications) { CheckOnClick = true, Checked = _notificationsEnabled };
            _debugItem = new ToolStripMenuItem("Show debug notifications", null, OnToggleDebug) { CheckOnClick = true, Checked = _debugNotifications };
            menu.Items.Add(_commandsItem);
            menu.Items.Add(_gamingModeItem);
            menu.Items.Add(_remindHotkeyItem);
            menu.Items.Add(_micItem);
            menu.Items.Add("Change microphone (Windows Sound Settings)", null, OnChangeMicrophone);
            menu.Items.Add("Open commands.json", null, OnOpenConfig);
            menu.Items.Add("Open log file", null, OnOpenLog);
            menu.Items.Add(_notificationsItem);
            menu.Items.Add(_debugItem);
            menu.Items.Add("Enable at Startup", null, OnEnableStartup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, OnQuit);

            _trayIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Visible = true,
                Text = "Cosmo - starting...",
                ContextMenuStrip = menu
            };

            LoadConfigAndStartListening();

            _pushToTalk = new PushToTalkHotkey { GamingModeEnabled = _gamingModeEnabled };
            _pushToTalk.KeyDown += OnDictateKeyDown;
            _pushToTalk.KeyUp += OnDictateKeyUp;
            try
            {
                _pushToTalk.Install();
                Log("Push-to-talk hotkey installed (hold ~ to dictate" + (_gamingModeEnabled ? ", gaming mode on: Tab also works" : "") + ").");
            }
            catch (Exception ex)
            {
                Log("Push-to-talk hotkey failed to install: " + ex.Message);
                ShowImportantBalloon(3000, "Cosmo", "Hold-to-dictate (~) couldn't be enabled: " + ex.Message, ToolTipIcon.Error);
            }

            _remindHotkey = new RemindHotkey { Enabled = _remindHotkeyEnabled };
            _remindHotkey.KeyDown += OnRemindKeyDown;
            _remindHotkey.KeyUp += OnRemindKeyUp;
            try
            {
                _remindHotkey.Install();
                Log("Reminder hotkey installed (hold Insert to create a reminder" + (_remindHotkeyEnabled ? "" : ", currently disabled") + ").");
            }
            catch (Exception ex)
            {
                Log("Reminder hotkey failed to install: " + ex.Message);
                ShowImportantBalloon(3000, "Cosmo", "Hold-Insert-to-remind couldn't be enabled: " + ex.Message, ToolTipIcon.Error);
            }

            // Reminders fire on a background timer thread, not the UI thread - ShowBalloon/
            // NotifyIcon calls are safe to make directly from there (unlike WinForms
            // controls in general), so no Invoke/BeginInvoke marshaling is needed here.
            Actions.ReminderDue += OnReminderDue;
            Actions.LoadAndRescheduleReminders();
        }

        private void OnReminderDue(string message)
        {
            Log("Reminder due: " + message);
            Tone.PlayDing();
            ShowImportantBalloon(5000, "Cosmo - Reminder", message, ToolTipIcon.Info);
        }

        private Icon LoadAppIcon()
        {
            var iconPath = Path.Combine(_appDir, "..", "icon.ico");
            return File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;
        }

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
                // logging is best-effort
            }
        }

        private void ShowBalloon(int timeout, string title, string text, ToolTipIcon icon)
        {
            if (!_notificationsEnabled) return;
            _trayIcon.ShowBalloonTip(timeout, title, text, icon);
        }

        // Failures (and "nothing happened" results) always show, regardless of the notifications
        // toggle - that toggle is for quieting routine "here's what I did" success toasts, not for
        // hiding the only signal you'd get that something actually broke.
        private void ShowImportantBalloon(int timeout, string title, string text, ToolTipIcon icon)
        {
            _trayIcon.ShowBalloonTip(timeout, title, text, icon);
        }

        private void OnToggleNotifications(object sender, EventArgs e)
        {
            _notificationsEnabled = _notificationsItem.Checked;
            SaveSettings();
        }

        private Settings LoadSettings()
        {
            if (!File.Exists(_settingsPath))
            {
                return new Settings();
            }
            try
            {
                return DeserializeJson<Settings>(_settingsPath);
            }
            catch
            {
                return new Settings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new Settings { NotificationsEnabled = _notificationsEnabled, DebugNotifications = _debugNotifications, CommandsEnabled = _commandsEnabled, GamingModeEnabled = _gamingModeEnabled, RemindHotkeyDisabled = !_remindHotkeyEnabled };
                SerializeJson(_settingsPath, settings);
            }
            catch
            {
                // settings persistence is best-effort
            }
        }

        private static T DeserializeJson<T>(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }

        private static void SerializeJson<T>(string path, T value)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(stream, value);
            }
        }

        private void LoadConfigAndStartListening()
        {
            _config = LoadConfig(_configPath);
            _commandLookup = new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase);
            _wildcardTriggerLookup = new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase);

            var micName = AudioDevice.GetDefaultMicrophoneName();
            _micItem.Text = "Mic: " + micName;

            Log("=== Cosmo starting ===");
            Log("Microphone: " + micName);
            Log("Wake word: " + _config.WakeWord);
            Log("Commands loaded: " + _config.Commands.Count);

            var whisperServerPath = Path.Combine(_appDir, "..", "whisper", "whisper-server.exe");
            // base.en trades a bit of accuracy for roughly 3x lower latency than small.en -
            // for a push-to-talk tool used mid-conversation, response time matters as much
            // as transcript quality. ggml-small.en.bin is still on disk under whisper/models
            // if the accuracy/speed balance ever needs to swing back the other way.
            var whisperModelPath = Path.Combine(_appDir, "..", "whisper", "models", "ggml-base.en.bin");
            try
            {
                _whisperEngine = WhisperEngine.Start(whisperServerPath, whisperModelPath, 8765, Log);
                Log("Whisper server started (model: " + whisperModelPath + ")");
            }
            catch (Exception ex)
            {
                _whisperEngine = null;
                Log("Whisper server failed to start: " + ex.Message);
            }

            _recognizer = new SpeechRecognitionEngine();
            // Default end-silence timeout (~150ms) finalizes an utterance the instant you
            // pause mid-sentence, which cuts off "look up"/"dictate" payloads before you're
            // done talking. Require 2s of trailing silence instead so free-form speech has
            // room to breathe. This applies recognizer-wide, so fixed commands also wait
            // ~2s after you stop talking before firing.
            _recognizer.EndSilenceTimeout = TimeSpan.FromSeconds(2);
            _recognizer.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(2);

            var choices = new Choices();
            foreach (var cmd in _config.Commands)
            {
                if (cmd.Type == "search" || cmd.Type == "dictate")
                {
                    var wildcardGrammarName = cmd.Type + ":" + cmd.Phrase;
                    var gb = new GrammarBuilder();
                    gb.Append(_config.WakeWord + " " + cmd.Phrase);
                    gb.AppendWildcard();
                    var wildcardGrammar = new Grammar(gb) { Name = wildcardGrammarName };
                    _recognizer.LoadGrammar(wildcardGrammar);
                    _wildcardTriggerLookup[wildcardGrammarName] = cmd;
                }
                else
                {
                    var fullPhrase = _config.WakeWord + " " + cmd.Phrase;
                    choices.Add(fullPhrase);
                    _commandLookup[cmd.Phrase.Trim()] = cmd;
                }
            }

            var grammar = new Grammar(new GrammarBuilder(choices)) { Name = "fixed-commands" };
            _recognizer.LoadGrammar(grammar);
            _recognizer.SpeechRecognized += OnSpeechRecognized;
            _recognizer.SpeechRecognitionRejected += OnSpeechRejected;
            _recognizer.SpeechDetected += OnSpeechDetected;
            _recognizer.SpeechHypothesized += OnSpeechHypothesized;
            _recognizer.SetInputToDefaultAudioDevice();

            // Voice commands default to off - only hold-to-dictate (and hold-Insert-to-
            // remind, wired up separately in the constructor) run out of the box.
            if (_commandsEnabled)
            {
                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                _trayIcon.Text = "Cosmo - listening";
                Log("Voice commands enabled, listening started.");
            }
            else
            {
                _trayIcon.Text = "Cosmo - dictation only";
                Log("Voice commands disabled (dictation-only mode).");
            }
        }

        private static Config LoadConfig(string path)
        {
            return DeserializeJson<Config>(path);
        }

        private void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            Log("RECOGNIZED text=\"" + e.Result.Text + "\" confidence=" + e.Result.Confidence.ToString("0.00"));

            if (e.Result.Confidence < _config.ConfidenceThreshold)
            {
                Log("  -> below threshold (" + _config.ConfidenceThreshold.ToString("0.00") + "), ignored");
                if (_debugNotifications)
                {
                    ShowBalloon(2500, "Cosmo heard (below threshold)",
                        "\"" + e.Result.Text + "\" (confidence " + e.Result.Confidence.ToString("0.00") +
                        ", needs " + _config.ConfidenceThreshold.ToString("0.00") + ")", ToolTipIcon.Warning);
                }
                return;
            }

            var grammarName = e.Result.Grammar != null ? e.Result.Grammar.Name : null;
            CommandEntry wildcardEntry;
            if (grammarName != null && _wildcardTriggerLookup.TryGetValue(grammarName, out wildcardEntry))
            {
                Log("  -> " + wildcardEntry.Type + " trigger matched (whole utterance), transcribing with Whisper");
                var result = e.Result;
                System.Threading.Tasks.Task.Run(() => HandleWildcardTrigger(wildcardEntry, result));
                return;
            }

            var text = e.Result.Text.Trim();
            var prefix = _config.WakeWord + " ";
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Log("  -> did not start with wake word \"" + _config.WakeWord + "\"");
                return;
            }

            var phrase = text.Substring(prefix.Length).Trim();
            CommandEntry entry;
            if (!_commandLookup.TryGetValue(phrase, out entry))
            {
                Log("  -> no command matches phrase \"" + phrase + "\"");
                return;
            }

            RunCommand(entry, () => Actions.Execute(entry));
        }

        private void HandleWildcardTrigger(CommandEntry entry, RecognitionResult result)
        {
            if (_whisperEngine == null)
            {
                Log("  -> Whisper engine not available, cannot process " + entry.Type);
                ShowImportantBalloon(3000, "Cosmo", "Voice recognition engine isn't loaded (check log).", ToolTipIcon.Error);
                return;
            }
            var audio = result.Audio;
            if (audio == null)
            {
                Log("  -> no audio captured for " + entry.Type + " utterance");
                return;
            }

            try
            {
                var format = audio.Format;
                byte[] pcm;
                using (var ms = new MemoryStream())
                {
                    audio.WriteToAudioStream(ms);
                    pcm = ms.ToArray();
                }
                if (format.ChannelCount == 2)
                {
                    pcm = DownmixStereoToMono(pcm);
                }

                var transcript = _whisperEngine.Transcribe(pcm, (int)format.SamplesPerSecond).Trim();
                Log("  -> Whisper transcribed: \"" + transcript + "\"");

                if (IsNonSpeech(transcript))
                {
                    Log("  -> no real speech detected, doing nothing");
                    return;
                }

                var content = ExtractPayloadText(transcript, entry.Phrase, _config.WakeWord);

                if (string.IsNullOrWhiteSpace(content))
                {
                    ShowImportantBalloon(2500, "Cosmo", "Didn't catch anything to " + entry.Phrase + ".", ToolTipIcon.Warning);
                    return;
                }

                var message = entry.Type == "dictate"
                    ? Actions.TypeText(ApplyDictationPunctuation(content))
                    : Actions.ExecuteSearch(entry, content);
                ReportSuccess(message, 1500, entry.Type != "dictate");
            }
            catch (Exception ex)
            {
                ReportFailure(entry.Type + " FAILED", "Cosmo - " + entry.Type + " failed", ex);
            }
        }

        // Hold-to-dictate: while the tilde key is held (anywhere - the hook is global, not
        // scoped to a Cosmo window), record raw mic audio and transcribe it with Whisper on
        // release, bypassing the wake word and grammar matching entirely. Runs concurrently
        // with the always-on wake-word recognizer rather than pausing it first - Windows
        // shared-mode audio capture supports multiple simultaneous readers of the same
        // device, and this keeps the hook callback (which must return quickly) from doing
        // anything that could block waiting on the other engine to release the mic.
        private void OnDictateKeyDown()
        {
            lock (_pttLock)
            {
                if (_pttActive) return; // key-repeat while already recording
                _pttActive = true;
            }

            try
            {
                var recorder = new WaveInRecorder();
                recorder.Start();
                _pttRecorder = recorder;
                _trayIcon.Text = "Cosmo - dictating...";
                Log("PTT dictation started (~ held)");
                System.Threading.Tasks.Task.Run(() => Tone.PlayClickAsync());
            }
            catch (Exception ex)
            {
                _pttActive = false;
                Log("PTT start FAILED: " + ex.Message);
                ShowImportantBalloon(3000, "Cosmo - dictate failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void OnDictateKeyUp()
        {
            WaveInRecorder recorder;
            lock (_pttLock)
            {
                if (!_pttActive) return;
                _pttActive = false;
                recorder = _pttRecorder;
                _pttRecorder = null;
            }
            if (recorder == null) return;

            System.Threading.Tasks.Task.Run(() => FinishPushToTalk(recorder));
        }

        private void FinishPushToTalk(WaveInRecorder recorder)
        {
            try
            {
                byte[] pcm;
                try
                {
                    pcm = recorder.StopAndGetPcm();
                }
                finally
                {
                    recorder.Dispose();
                }
                Log("PTT dictation stopped, captured " + pcm.Length + " bytes");

                if (_whisperEngine == null)
                {
                    Log("  -> Whisper engine not available, cannot transcribe PTT audio");
                    ShowImportantBalloon(3000, "Cosmo", "Voice recognition engine isn't loaded (check log).", ToolTipIcon.Error);
                    return;
                }

                // Less than ~0.3s of audio is almost certainly just the key press itself.
                if (pcm.Length < WaveInRecorder.SampleRate * 2 * 3 / 10)
                {
                    Log("  -> PTT audio too short, skipping transcription");
                    return;
                }

                var transcript = _whisperEngine.Transcribe(pcm, WaveInRecorder.SampleRate).Trim();
                Log("  -> PTT Whisper transcribed: \"" + transcript + "\"");

                if (IsNonSpeech(transcript))
                {
                    Log("  -> no real speech detected, doing nothing");
                    return;
                }

                var message = Actions.TypeText(ApplyDictationPunctuation(transcript));
                ReportSuccess(message, 1200, false);
            }
            catch (Exception ex)
            {
                ReportFailure("PTT dictation FAILED", "Cosmo - dictate failed", ex);
            }
            finally
            {
                _trayIcon.Text = _commandsEnabled ? "Cosmo - listening" : "Cosmo - dictation only";
            }
        }

        // Hold-to-remind: while Insert is held (anywhere, same shape as hold-to-dictate
        // above but on a different key and a separate recorder/lock so the two can't
        // interfere with each other), record raw mic audio and transcribe it with Whisper
        // on release. Whatever comes out goes straight to Actions.ScheduleReminder - no
        // wake word or trigger phrase needed, since holding Insert is itself the trigger.
        private void OnRemindKeyDown()
        {
            lock (_remindLock)
            {
                if (_remindActive) return; // key-repeat while already recording
                _remindActive = true;
            }

            try
            {
                var recorder = new WaveInRecorder();
                recorder.Start();
                _remindRecorder = recorder;
                _trayIcon.Text = "Cosmo - recording reminder...";
                Log("Reminder hotkey started (Insert held)");
                System.Threading.Tasks.Task.Run(() => Tone.PlayClickAsync());
            }
            catch (Exception ex)
            {
                _remindActive = false;
                Log("Reminder hotkey start FAILED: " + ex.Message);
                ShowImportantBalloon(3000, "Cosmo - reminder failed", ex.Message, ToolTipIcon.Error);
            }
        }

        private void OnRemindKeyUp()
        {
            WaveInRecorder recorder;
            lock (_remindLock)
            {
                if (!_remindActive) return;
                _remindActive = false;
                recorder = _remindRecorder;
                _remindRecorder = null;
            }
            if (recorder == null) return;

            System.Threading.Tasks.Task.Run(() => FinishRemindHotkey(recorder));
        }

        private void FinishRemindHotkey(WaveInRecorder recorder)
        {
            try
            {
                byte[] pcm;
                try
                {
                    pcm = recorder.StopAndGetPcm();
                }
                finally
                {
                    recorder.Dispose();
                }
                Log("Reminder hotkey stopped, captured " + pcm.Length + " bytes");

                if (_whisperEngine == null)
                {
                    Log("  -> Whisper engine not available, cannot transcribe reminder audio");
                    ShowImportantBalloon(3000, "Cosmo", "Voice recognition engine isn't loaded (check log).", ToolTipIcon.Error);
                    return;
                }

                // Less than ~0.3s of audio is almost certainly just the key press itself.
                if (pcm.Length < WaveInRecorder.SampleRate * 2 * 3 / 10)
                {
                    Log("  -> reminder audio too short, skipping transcription");
                    return;
                }

                var transcript = _whisperEngine.Transcribe(pcm, WaveInRecorder.SampleRate).Trim();
                Log("  -> reminder Whisper transcribed: \"" + transcript + "\"");

                if (IsNonSpeech(transcript))
                {
                    Log("  -> no real speech detected, doing nothing");
                    return;
                }

                var message = Actions.ScheduleReminder(transcript);
                ReportSuccess(message, 2500, true);
            }
            catch (Exception ex)
            {
                ReportFailure("Reminder hotkey FAILED", "Cosmo - reminder failed", ex);
            }
            finally
            {
                _trayIcon.Text = _commandsEnabled ? "Cosmo - listening" : "Cosmo - dictation only";
            }
        }

        // Best case: the phrase ("look up", "dictate") shows up literally in Whisper's transcript -
        // anchor on that. Second best: Whisper got the phrase slightly wrong (e.g. "look up" heard as
        // "luck up") - find the closest-matching run of words instead of requiring an exact hit.
        // Worst case: the wake word "Cosmo" (not a real dictionary word) got mangled together with
        // the phrase into something unrecognizable (e.g. "cosmo dictate" -> "cosmetic") - fall back
        // to just dropping as many leading words as the wake word + phrase have, rather than
        // discarding the whole utterance.
        private string ExtractPayloadText(string transcript, string phrase, string wakeWord)
        {
            var anchorIndex = transcript.LastIndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (anchorIndex >= 0)
            {
                return transcript.Substring(anchorIndex + phrase.Length).Trim();
            }

            var transcriptWords = SplitWords(transcript);
            var phraseWords = SplitWords(phrase);
            var fuzzyEnd = FindFuzzyAnchorEnd(transcriptWords, phraseWords);
            if (fuzzyEnd >= 0)
            {
                Log("  -> transcript didn't contain \"" + phrase + "\" verbatim, fuzzy-matched trigger words instead");
                return transcriptWords.Length > fuzzyEnd
                    ? string.Join(" ", transcriptWords, fuzzyEnd, transcriptWords.Length - fuzzyEnd)
                    : "";
            }

            Log("  -> transcript didn't resemble \"" + phrase + "\" closely enough, falling back to word-count trim");
            var wordsToDrop = SplitWords(wakeWord + " " + phrase).Length;
            return transcriptWords.Length > wordsToDrop
                ? string.Join(" ", transcriptWords, wordsToDrop, transcriptWords.Length - wordsToDrop)
                : "";
        }

        private static string[] SplitWords(string text)
        {
            return text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // Slides a window the size of the trigger phrase across the transcript's words looking for
        // the closest fuzzy match (tolerating a couple of letters of ASR slip per word). Returns the
        // transcript word-index right after the best matching window, or -1 if nothing in the
        // transcript resembles the phrase closely enough to trust.
        private static int FindFuzzyAnchorEnd(string[] transcriptWords, string[] phraseWords)
        {
            if (phraseWords.Length == 0 || transcriptWords.Length < phraseWords.Length) return -1;

            // Lowercase once up front instead of on every Levenshtein call - phraseWords in
            // particular gets re-compared against every sliding window position.
            var lowerTranscript = ToLowerInvariant(transcriptWords);
            var lowerPhrase = ToLowerInvariant(phraseWords);

            var bestDistance = int.MaxValue;
            var bestEnd = -1;
            for (var start = 0; start <= lowerTranscript.Length - lowerPhrase.Length; start++)
            {
                var distance = 0;
                for (var i = 0; i < lowerPhrase.Length; i++)
                {
                    distance += LevenshteinDistance(lowerTranscript[start + i], lowerPhrase[i]);
                }
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestEnd = start + lowerPhrase.Length;
                }
            }

            var maxAcceptableDistance = lowerPhrase.Length * 2;
            return bestDistance <= maxAcceptableDistance ? bestEnd : -1;
        }

        private static string[] ToLowerInvariant(string[] words)
        {
            var result = new string[words.Length];
            for (var i = 0; i < words.Length; i++) result[i] = words[i].ToLowerInvariant();
            return result;
        }

        // Callers pass already-lowercased strings (see FindFuzzyAnchorEnd).
        private static int LevenshteinDistance(string a, string b)
        {
            var dp = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                }
            }
            return dp[a.Length, b.Length];
        }

        private static readonly Tuple<string, string>[] DictationPunctuation =
        {
            Tuple.Create("question mark", "?"),
            Tuple.Create("exclamation point", "!"),
            Tuple.Create("exclamation mark", "!"),
            Tuple.Create("full stop", "."),
            Tuple.Create("new line", "\n"),
            Tuple.Create("newline", "\n"),
            Tuple.Create("tab", "\t"),
            Tuple.Create("comma", ","),
            Tuple.Create("period", "."),
            Tuple.Create("colon", ":"),
            Tuple.Create("semicolon", ";"),
            Tuple.Create("open parenthesis", "("),
            Tuple.Create("open paren", "("),
            Tuple.Create("close parenthesis", ")"),
            Tuple.Create("close paren", ")"),
            Tuple.Create("open bracket", "["),
            Tuple.Create("close bracket", "]"),
            Tuple.Create("open curly brace", "{"),
            Tuple.Create("close curly brace", "}"),
            Tuple.Create("open brace", "{"),
            Tuple.Create("close brace", "}"),
            Tuple.Create("quotation mark", "\""),
            Tuple.Create("quote", "\""),
            Tuple.Create("apostrophe", "'"),
            Tuple.Create("at sign", "@"),
            Tuple.Create("hashtag", "#"),
            Tuple.Create("pound sign", "#"),
            Tuple.Create("dollar sign", "$"),
            Tuple.Create("percent sign", "%"),
            Tuple.Create("ampersand", "&"),
            Tuple.Create("asterisk", "*"),
            Tuple.Create("plus sign", "+"),
            Tuple.Create("equals sign", "="),
            Tuple.Create("underscore", "_"),
            Tuple.Create("forward slash", "/"),
            Tuple.Create("slash", "/"),
            Tuple.Create("backslash", "\\"),
            Tuple.Create("tilde", "~"),
            Tuple.Create("caret", "^"),
            Tuple.Create("vertical bar", "|"),
            Tuple.Create("pipe", "|"),
            Tuple.Create("less than", "<"),
            Tuple.Create("greater than", ">"),
            Tuple.Create("ellipsis", "..."),
            Tuple.Create("dash", "-"),
            Tuple.Create("hyphen", "-"),
        };

        // Whisper emits a bracketed/parenthesized annotation instead of real words when it
        // hears no speech (e.g. "[BLANK_AUDIO]") or only ambient noise (e.g. "(keyboard
        // clicking)"). None of that is something the user actually said - treat it exactly
        // like an empty transcript rather than typing the annotation itself.
        private static bool IsNonSpeech(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return true;
            return Regex.IsMatch(transcript, @"^[\[\(].*[\]\)]$");
        }

        // Precompiled once instead of re-parsing all 45 patterns (via Regex.Replace's string
        // overload) on every dictation - that path re-escapes and re-caches each pattern per
        // call, which thrashes .NET's default 15-entry regex cache.
        private static readonly Tuple<Regex, string>[] CompiledDictationPunctuation = BuildCompiledDictationPunctuation();

        private static Tuple<Regex, string>[] BuildCompiledDictationPunctuation()
        {
            var compiled = new Tuple<Regex, string>[DictationPunctuation.Length];
            for (var i = 0; i < DictationPunctuation.Length; i++)
            {
                var pair = DictationPunctuation[i];
                // Swallow the whitespace before the spoken word too, so "hello comma world"
                // becomes "hello, world" instead of "hello , world".
                var regex = new Regex(@"\s*\b" + Regex.Escape(pair.Item1) + @"\b", RegexOptions.IgnoreCase);
                compiled[i] = Tuple.Create(regex, pair.Item2);
            }
            return compiled;
        }

        private static string ApplyDictationPunctuation(string text)
        {
            foreach (var pair in CompiledDictationPunctuation)
            {
                text = pair.Item1.Replace(text, pair.Item2);
            }
            return text;
        }

        private static byte[] DownmixStereoToMono(byte[] stereoPcm16)
        {
            var mono = new byte[stereoPcm16.Length / 2];
            var monoIndex = 0;
            for (var i = 0; i + 3 < stereoPcm16.Length; i += 4)
            {
                var left = (short)(stereoPcm16[i] | (stereoPcm16[i + 1] << 8));
                var right = (short)(stereoPcm16[i + 2] | (stereoPcm16[i + 3] << 8));
                var avg = (short)((left + right) / 2);
                mono[monoIndex++] = (byte)(avg & 0xFF);
                mono[monoIndex++] = (byte)((avg >> 8) & 0xFF);
            }
            return mono;
        }

        private void RunCommand(CommandEntry entry, Func<string> action)
        {
            _trayIcon.Text = "Cosmo - listening";
            try
            {
                ReportSuccess(action(), 1500, true);
            }
            catch (Exception ex)
            {
                ReportFailure("FAILED", "Cosmo - command failed", ex);
            }
        }

        private void ReportSuccess(string message, int timeoutMs, bool playDing)
        {
            Log("  -> executed: " + message);
            if (playDing)
            {
                Tone.PlayDing();
            }
            ShowBalloon(timeoutMs, "Cosmo", message, ToolTipIcon.Info);
        }

        private void ReportFailure(string logPrefix, string title, Exception ex)
        {
            Log("  -> " + logPrefix + ": " + ex.Message);
            ShowImportantBalloon(3000, title, ex.Message, ToolTipIcon.Error);
        }

        private void OnSpeechHypothesized(object sender, SpeechHypothesizedEventArgs e)
        {
            Log("hypothesis: \"" + e.Result.Text + "\" confidence=" + e.Result.Confidence.ToString("0.00"));
        }

        private void OnSpeechDetected(object sender, SpeechDetectedEventArgs e)
        {
            Log("audio detected (mic is picking up sound)");
            _trayIcon.Text = "Cosmo - hearing audio...";
        }

        private void OnSpeechRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            _trayIcon.Text = "Cosmo - listening";
            if (e.Result == null)
            {
                Log("REJECTED (no result)");
                return;
            }
            var best = e.Result.Text;
            var confidence = e.Result.Confidence;
            Log("REJECTED best-guess=\"" + best + "\" confidence=" + confidence.ToString("0.00"));
            if (_debugNotifications)
            {
                ShowBalloon(2500, "Cosmo heard (no match)",
                    "\"" + best + "\" (confidence " + confidence.ToString("0.00") + ")", ToolTipIcon.Warning);
            }
        }

        private void OnToggleDebug(object sender, EventArgs e)
        {
            _debugNotifications = _debugItem.Checked;
            SaveSettings();
        }

        private void OnToggleCommands(object sender, EventArgs e)
        {
            _commandsEnabled = _commandsItem.Checked;
            SaveSettings();

            if (_commandsEnabled)
            {
                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                _trayIcon.Text = "Cosmo - listening";
                Log("Voice commands enabled by user.");
            }
            else
            {
                _recognizer.RecognizeAsyncStop();
                _trayIcon.Text = "Cosmo - dictation only";
                Log("Voice commands disabled by user.");
            }
        }

        private void OnToggleGamingMode(object sender, EventArgs e)
        {
            _gamingModeEnabled = _gamingModeItem.Checked;
            _pushToTalk.GamingModeEnabled = _gamingModeEnabled;
            SaveSettings();
            Log(_gamingModeEnabled ? "Gaming mode enabled (Tab also dictates)." : "Gaming mode disabled.");
        }

        private void OnToggleRemindHotkey(object sender, EventArgs e)
        {
            _remindHotkeyEnabled = _remindHotkeyItem.Checked;
            _remindHotkey.Enabled = _remindHotkeyEnabled;
            SaveSettings();
            Log(_remindHotkeyEnabled ? "Reminder hotkey enabled (hold Insert)." : "Reminder hotkey disabled.");
        }

        private void OnOpenConfig(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("notepad.exe", "\"" + _configPath + "\"");
        }

        private void OnOpenLog(object sender, EventArgs e)
        {
            if (!File.Exists(_logPath)) File.WriteAllText(_logPath, "");
            System.Diagnostics.Process.Start("notepad.exe", "\"" + _logPath + "\"");
        }

        private void OnChangeMicrophone(object sender, EventArgs e)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ms-settings:sound") { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }

        private void OnEnableStartup(object sender, EventArgs e)
        {
            var scriptPath = Path.Combine(_appDir, "..", "install-startup.ps1");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
            ShowBalloon(2000, "Cosmo", "Startup shortcut created.", ToolTipIcon.Info);
        }

        private void OnQuit(object sender, EventArgs e)
        {
            if (_pushToTalk != null)
            {
                _pushToTalk.Dispose();
            }
            if (_remindHotkey != null)
            {
                _remindHotkey.Dispose();
            }
            if (_recognizer != null)
            {
                _recognizer.RecognizeAsyncStop();
                _recognizer.Dispose();
            }
            if (_whisperEngine != null)
            {
                _whisperEngine.Dispose();
            }
            _trayIcon.Visible = false;
            Application.Exit();
        }
    }
}
