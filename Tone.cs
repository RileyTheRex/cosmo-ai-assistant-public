using System;
using System.IO;
using System.Media;

namespace OliverAssistant
{
    // Synthesizes a short, soft chime in memory - no .wav asset to ship - so Cosmo can give
    // an audible cue for push-to-talk without relying on the (often jarring) Windows system
    // sound theme. A raised-cosine envelope has no hard attack or decay edge at all, which
    // reads as a gentle swell rather than the percussive click/beep/thud variants tried
    // before it.
    internal static class Tone
    {
        // Built once at class load rather than re-synthesized on every keypress - the
        // waveform is fixed, so there's nothing to recompute per call.
        private static readonly byte[] ChimeWav = BuildChime(440, 150, 0.18);

        public static void PlayClickAsync()
        {
            try
            {
                var player = new SoundPlayer(new MemoryStream(ChimeWav));
                player.Play(); // asynchronous - returns immediately
            }
            catch
            {
                // audio cue is best-effort; never let it break dictation
            }
        }

        // A bright, synthesized "ding" - built in memory the same way as the PTT click
        // above, rather than playing a Windows system sound asset (tried first, but
        // SoundPlayer loading an external .wav by path was unreliable in testing here).
        // Played as a confirmation whenever a voice *command* (open app, lock, search,
        // remind, etc.) finishes executing. Deliberately not used for dictation, which
        // fires far more often and would make the cue noise rather than signal.
        private static readonly byte[] DingWav = BuildDing(1200, 250, 0.22);

        public static void PlayDing()
        {
            try
            {
                var player = new SoundPlayer(new MemoryStream(DingWav));
                player.Play(); // asynchronous - returns immediately
            }
            catch
            {
                // audio cue is best-effort; never let it break command execution
            }
        }

        private static byte[] BuildDing(int frequencyHz, int durationMs, double amplitude)
        {
            const int sampleRate = 16000;
            var sampleCount = sampleRate * durationMs / 1000;

            var data = new short[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                // Fast attack, exponential decay - reads as a bright bell-like "ding"
                // rather than the PTT click's soft symmetric swell.
                var t = (double)i / sampleCount;
                var envelope = Math.Exp(-4.5 * t);
                var sample = Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * amplitude * envelope;
                data[i] = (short)(sample * short.MaxValue);
            }

            var pcm = new byte[data.Length * 2];
            Buffer.BlockCopy(data, 0, pcm, 0, pcm.Length);
            return WavWriter.Wrap(pcm, sampleRate);
        }

        private static byte[] BuildChime(int frequencyHz, int durationMs, double amplitude)
        {
            const int sampleRate = 16000;
            var sampleCount = sampleRate * durationMs / 1000;

            var data = new short[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                // Raised cosine: 0 -> 1 -> 0 with no hard edges, unlike a linear fade or a
                // power-curve decay - this is what makes it read as a smooth swell instead
                // of a click.
                var t = (double)i / sampleCount;
                var envelope = 0.5 * (1 - Math.Cos(2 * Math.PI * t));
                var sample = Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * amplitude * envelope;
                data[i] = (short)(sample * short.MaxValue);
            }

            var pcm = new byte[data.Length * 2];
            Buffer.BlockCopy(data, 0, pcm, 0, pcm.Length);
            return WavWriter.Wrap(pcm, sampleRate);
        }
    }
}
