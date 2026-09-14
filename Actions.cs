using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace OliverAssistant
{
    [DataContract]
    public class ReminderEntry
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "fireAtUtcTicks")] public long FireAtUtcTicks;
        [DataMember(Name = "message")] public string Message;
    }

    public static class Actions
    {
        private static readonly string RemindersPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "reminders.json");
        private static readonly Dictionary<string, Timer> ActiveTimers = new Dictionary<string, Timer>();
        private static readonly object RemindersLock = new object();

        // Raised on a background timer thread when a reminder's time is up. Program.cs
        // subscribes to this to show a tray balloon and play the confirmation ding.
        public static event Action<string> ReminderDue;

        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // INPUT is a union in the real Win32 API (keyboard/mouse/hardware share the same slot).
        // The struct's size must match exactly what SendInput expects (40 bytes on x64, 28 on
        // x86) or the whole call is silently rejected - so this mirrors the union, not just
        // the keyboard fields, even though we only ever populate the keyboard variant.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP_FLAG = 0x0002;

        public static string Execute(CommandEntry entry)
        {
            switch (entry.Type)
            {
                case "process":
                    var path = Environment.ExpandEnvironmentVariables(entry.Path);
                    var args = Environment.ExpandEnvironmentVariables(entry.Args ?? "");
                    Process.Start(path, args);
                    return "Opening " + entry.Phrase.Replace("open ", "");

                case "system":
                    if (entry.Action == "lock")
                    {
                        LockWorkStation();
                        return "Locking computer";
                    }
                    throw new InvalidOperationException("Unknown system action: " + entry.Action);

                case "claude-terminal":
                    var workingDir = Environment.ExpandEnvironmentVariables(entry.Path);
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/k claude",
                        WorkingDirectory = workingDir,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    return "Launching Claude in " + workingDir;

                default:
                    throw new InvalidOperationException("Unknown command type: " + entry.Type);
            }
        }

        public static string ExecuteSearch(CommandEntry entry, string query)
        {
            var url = string.Format(entry.UrlTemplate, Uri.EscapeDataString(query));
            var psi = new ProcessStartInfo(url) { UseShellExecute = true };
            Process.Start(psi);
            return "Searching for \"" + query + "\"";
        }

        // Reminders are entirely local - no external service or second machine required.
        // Delivery is a Windows tray balloon (Program.cs subscribes to ReminderDue), fired
        // by an in-memory Timer. The pending reminder is persisted to reminders.json first
        // so a Cosmo restart before it fires doesn't silently lose it -
        // LoadAndRescheduleReminders (called once at startup) picks it back up.
        public static string ScheduleReminder(string content)
        {
            double seconds;
            string message;
            if (!ReminderParser.TryParse(content, out seconds, out message))
            {
                throw new InvalidOperationException("Didn't catch a time to remind you in \"" + content + "\".");
            }

            const double maxSeconds = 30 * 86400;
            if (seconds > maxSeconds)
            {
                throw new InvalidOperationException("That's too far out - reminders are capped at 30 days.");
            }

            var id = Guid.NewGuid().ToString();
            var fireAtUtc = DateTime.UtcNow.AddSeconds(seconds);
            SaveReminder(new ReminderEntry { Id = id, FireAtUtcTicks = fireAtUtc.Ticks, Message = message });
            ArmTimer(id, seconds, message);

            return "Reminder set for " + ReminderParser.FormatDuration(seconds) + " from now: " + message;
        }

        // Called once at startup so reminders survive a Cosmo restart (e.g. the PC rebooting
        // with a reminder still pending). Anything whose time already passed while nothing
        // was running fires immediately rather than being silently dropped.
        public static void LoadAndRescheduleReminders()
        {
            List<ReminderEntry> reminders;
            lock (RemindersLock)
            {
                reminders = LoadReminders();
            }
            foreach (var reminder in reminders)
            {
                var delay = new DateTime(reminder.FireAtUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow;
                var remainingSeconds = Math.Max(0, delay.TotalSeconds);
                ArmTimer(reminder.Id, remainingSeconds, reminder.Message);
            }
        }

        private static void ArmTimer(string id, double delaySeconds, string message)
        {
            Timer timer = null;
            timer = new Timer(_ =>
            {
                RemoveReminder(id);
                lock (RemindersLock)
                {
                    ActiveTimers.Remove(id);
                }
                var handler = ReminderDue;
                if (handler != null) handler(message);
            }, null, TimeSpan.FromSeconds(delaySeconds), Timeout.InfiniteTimeSpan);

            lock (RemindersLock)
            {
                // A Timer with no other reference gets garbage-collected before it fires -
                // this dictionary is what keeps it rooted until then.
                ActiveTimers[id] = timer;
            }
        }

        private static List<ReminderEntry> LoadReminders()
        {
            if (!File.Exists(RemindersPath)) return new List<ReminderEntry>();
            try
            {
                using (var stream = new FileStream(RemindersPath, FileMode.Open, FileAccess.Read))
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<ReminderEntry>));
                    return (List<ReminderEntry>)serializer.ReadObject(stream) ?? new List<ReminderEntry>();
                }
            }
            catch
            {
                // A corrupt/partially-written reminders.json shouldn't take down startup -
                // treat it as "no pending reminders" rather than crashing.
                return new List<ReminderEntry>();
            }
        }

        private static void SaveReminder(ReminderEntry entry)
        {
            lock (RemindersLock)
            {
                var reminders = LoadReminders();
                reminders.Add(entry);
                WriteReminders(reminders);
            }
        }

        private static void RemoveReminder(string id)
        {
            lock (RemindersLock)
            {
                var reminders = LoadReminders();
                reminders.RemoveAll(r => r.Id == id);
                WriteReminders(reminders);
            }
        }

        private static void WriteReminders(List<ReminderEntry> reminders)
        {
            using (var stream = new FileStream(RemindersPath, FileMode.Create, FileAccess.Write))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ReminderEntry>));
                serializer.WriteObject(stream, reminders);
            }
        }

        public static string TypeText(string text)
        {
            var inputs = new INPUT[text.Length * 2];
            for (var i = 0; i < text.Length; i++)
            {
                var scan = (ushort)text[i];
                inputs[i * 2] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero } }
                };
                inputs[i * 2 + 1] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = scan, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP_FLAG, time = 0, dwExtraInfo = IntPtr.Zero } }
                };
            }
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length)
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("SendInput only accepted " + sent + "/" + inputs.Length + " events (Win32 error " + error + ")");
            }
            return "Typed: \"" + text + "\"";
        }
    }
}
