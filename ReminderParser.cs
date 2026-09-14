using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OliverAssistant
{
    // Converts raw spoken/transcribed English (e.g. "me in three hours and 25 minutes to
    // take out the trash") into a delay in seconds plus the leftover reminder text.
    // Deliberately tolerant of loose phrasing since the input comes straight out of
    // Whisper's free-form transcript, rather than requiring a rigid fixed format.
    public static class ReminderParser
    {
        private static readonly Dictionary<string, int> NumberWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "zero", 0 }, { "a", 1 }, { "an", 1 }, { "one", 1 }, { "two", 2 }, { "three", 3 },
            { "four", 4 }, { "five", 5 }, { "six", 6 }, { "seven", 7 }, { "eight", 8 }, { "nine", 9 },
            { "ten", 10 }, { "eleven", 11 }, { "twelve", 12 }, { "thirteen", 13 }, { "fourteen", 14 },
            { "fifteen", 15 }, { "sixteen", 16 }, { "seventeen", 17 }, { "eighteen", 18 }, { "nineteen", 19 },
            { "twenty", 20 }, { "thirty", 30 }, { "forty", 40 }, { "fifty", 50 },
            { "sixty", 60 }, { "seventy", 70 }, { "eighty", 80 }, { "ninety", 90 }
        };

        private static readonly Dictionary<string, double> UnitSeconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "second", 1 }, { "seconds", 1 }, { "sec", 1 }, { "secs", 1 },
            { "minute", 60 }, { "minutes", 60 }, { "min", 60 }, { "mins", 60 },
            { "hour", 3600 }, { "hours", 3600 }, { "hr", 3600 }, { "hrs", 3600 },
            { "day", 86400 }, { "days", 86400 }
        };

        // "remind" included because the hold-Insert-to-remind hotkey has no wake word to
        // strip - people naturally still say "remind me in..." out of habit.
        private static readonly string[] LeadingFillers = { "remind", "me", "to", "that", "and" };

        // Scans for the first spot in the payload that looks like a duration (optionally led
        // by "in"/"for"), pulls it out, and returns whatever's left (with connector words
        // trimmed off the edges) as the reminder message. The duration can appear anywhere -
        // "remind me to walk the dog in 20 minutes" works the same as "remind me in 20
        // minutes to walk the dog".
        public static bool TryParse(string rawPayload, out double seconds, out string message)
        {
            seconds = 0;
            message = null;
            if (string.IsNullOrWhiteSpace(rawPayload)) return false;

            var matchCollection = Regex.Matches(rawPayload, "[A-Za-z']+|\\d+");
            if (matchCollection.Count == 0) return false;

            var tokens = new Match[matchCollection.Count];
            var words = new string[matchCollection.Count];
            for (var i = 0; i < matchCollection.Count; i++)
            {
                tokens[i] = matchCollection[i];
                words[i] = matchCollection[i].Value;
            }

            for (var i = 0; i < words.Length; i++)
            {
                var hasLeadIn = Eq(words[i], "in") || Eq(words[i], "for");
                var durStart = hasLeadIn ? i + 1 : i;
                if (durStart >= words.Length) continue;

                double durSeconds;
                int durEnd;
                if (TryParseDurationChunk(words, durStart, out durSeconds, out durEnd) && durSeconds > 0)
                {
                    var startChar = tokens[i].Index;
                    var endToken = tokens[durEnd - 1];
                    var endChar = endToken.Index + endToken.Length;
                    var remainder = rawPayload.Remove(startChar, endChar - startChar);

                    seconds = durSeconds;
                    message = CleanMessage(remainder);
                    return true;
                }
            }
            return false;
        }

        private static bool TryParseDurationChunk(string[] words, int startIdx, out double totalSeconds, out int endIdx)
        {
            totalSeconds = 0;
            var i = startIdx;
            var foundAny = false;

            while (i < words.Length)
            {
                var tryIdx = i;
                if (Eq(words[tryIdx], "and")) tryIdx++;
                if (tryIdx >= words.Length) break;

                double specialSeconds;
                int specialConsumed;
                if (TrySpecialPhrase(words, tryIdx, out specialSeconds, out specialConsumed))
                {
                    totalSeconds += specialSeconds;
                    i = tryIdx + specialConsumed;
                    foundAny = true;
                    continue;
                }

                int numVal, numConsumed;
                if (TryParseNumberAt(words, tryIdx, out numVal, out numConsumed))
                {
                    var unitIdx = tryIdx + numConsumed;
                    double unitSeconds;
                    if (unitIdx < words.Length && UnitSeconds.TryGetValue(words[unitIdx], out unitSeconds))
                    {
                        totalSeconds += numVal * unitSeconds;
                        i = unitIdx + 1;
                        foundAny = true;
                        continue;
                    }
                }
                break;
            }

            endIdx = i;
            return foundAny;
        }

        private static bool TrySpecialPhrase(string[] words, int i, out double seconds, out int consumed)
        {
            seconds = 0;
            consumed = 0;
            if (i >= words.Length) return false;

            if (Eq(words[i], "half"))
            {
                var j = i + 1;
                if (j < words.Length && (Eq(words[j], "an") || Eq(words[j], "a"))) j++;
                if (j < words.Length && Eq(words[j], "hour"))
                {
                    seconds = 1800;
                    consumed = j - i + 1;
                    return true;
                }
                return false;
            }

            var quarterStart = -1;
            if (Eq(words[i], "quarter"))
            {
                quarterStart = i + 1;
            }
            else if (Eq(words[i], "a") && i + 1 < words.Length && Eq(words[i + 1], "quarter"))
            {
                quarterStart = i + 2;
            }
            if (quarterStart >= 0)
            {
                var j = quarterStart;
                if (j < words.Length && Eq(words[j], "of")) j++;
                if (j < words.Length && (Eq(words[j], "an") || Eq(words[j], "a"))) j++;
                if (j < words.Length && Eq(words[j], "hour"))
                {
                    seconds = 900;
                    consumed = j - i + 1;
                    return true;
                }
            }
            return false;
        }

        private static bool TryParseNumberAt(string[] words, int i, out int value, out int consumed)
        {
            value = 0;
            consumed = 0;
            if (i >= words.Length) return false;

            var word = words[i];
            int digitValue;
            if (int.TryParse(word, out digitValue))
            {
                value = digitValue;
                consumed = 1;
                return true;
            }

            int wordValue;
            if (!NumberWords.TryGetValue(word, out wordValue)) return false;

            value = wordValue;
            consumed = 1;

            // Combine tens + ones, e.g. "twenty" + "five" -> 25.
            if (wordValue >= 20 && wordValue % 10 == 0 && i + 1 < words.Length)
            {
                int onesValue;
                if (NumberWords.TryGetValue(words[i + 1], out onesValue) && onesValue >= 1 && onesValue <= 9)
                {
                    value = wordValue + onesValue;
                    consumed = 2;
                }
            }

            return true;
        }

        private static string CleanMessage(string text)
        {
            text = text.Trim();
            var changed = true;
            while (changed)
            {
                changed = false;
                text = text.Trim(' ', ',', '.', ';', ':');
                foreach (var filler in LeadingFillers)
                {
                    if (Regex.IsMatch(text, "^" + filler + "\\b", RegexOptions.IgnoreCase))
                    {
                        text = text.Substring(filler.Length).TrimStart();
                        changed = true;
                        break;
                    }
                }
            }
            text = text.Trim(' ', ',', '.', ';', ':');
            return string.IsNullOrWhiteSpace(text) ? "Reminder!" : text;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static string FormatDuration(double totalSeconds)
        {
            var seconds = (long)Math.Round(totalSeconds);
            var parts = new List<string>();
            var days = seconds / 86400; seconds %= 86400;
            var hours = seconds / 3600; seconds %= 3600;
            var minutes = seconds / 60; seconds %= 60;

            if (days > 0) parts.Add(days + (days == 1 ? " day" : " days"));
            if (hours > 0) parts.Add(hours + (hours == 1 ? " hour" : " hours"));
            if (minutes > 0) parts.Add(minutes + (minutes == 1 ? " minute" : " minutes"));
            if (seconds > 0 || parts.Count == 0) parts.Add(seconds + (seconds == 1 ? " second" : " seconds"));

            return string.Join(", ", parts.ToArray());
        }
    }
}
