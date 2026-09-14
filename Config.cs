using System.Collections.Generic;
using System.Runtime.Serialization;

namespace OliverAssistant
{
    [DataContract]
    public class CommandEntry
    {
        [DataMember(Name = "phrase")] public string Phrase;
        [DataMember(Name = "type")] public string Type;
        [DataMember(Name = "path")] public string Path;
        [DataMember(Name = "args")] public string Args;
        [DataMember(Name = "action")] public string Action;
        [DataMember(Name = "key")] public string Key;
        [DataMember(Name = "urlTemplate")] public string UrlTemplate;
    }

    [DataContract]
    public class Config
    {
        [DataMember(Name = "wakeWord")] public string WakeWord;
        [DataMember(Name = "confidenceThreshold")] public double ConfidenceThreshold;
        [DataMember(Name = "commands")] public List<CommandEntry> Commands;
    }

    [DataContract]
    public class Settings
    {
        [DataMember(Name = "notificationsEnabled")] public bool NotificationsEnabled;
        [DataMember(Name = "debugNotifications")] public bool DebugNotifications;
        // Defaults to false (omitted key deserializes as false too), so a fresh install -
        // or an existing settings.json from before this setting existed - starts in
        // dictation-only mode rather than silently opting into voice commands.
        [DataMember(Name = "commandsEnabled")] public bool CommandsEnabled;
        // Defaults to false: gaming mode (adds Pause/Break as a second PTT key, since
        // '~' is commonly bound to the console in games) is opt-in.
        [DataMember(Name = "gamingModeEnabled")] public bool GamingModeEnabled;
        // Stored inverted (disabled, not enabled) so the hold-Insert-to-remind hotkey
        // defaults ON for existing settings.json files that predate this field - it's the
        // only way to create a reminder now, so it shouldn't silently default off.
        [DataMember(Name = "remindHotkeyDisabled")] public bool RemindHotkeyDisabled;
    }
}
