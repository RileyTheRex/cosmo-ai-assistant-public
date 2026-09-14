using System.IO;
using System.Text;

namespace OliverAssistant
{
    // Wraps raw 16-bit PCM mono samples in a minimal canonical WAV container.
    internal static class WavWriter
    {
        public static byte[] Wrap(byte[] pcm16Mono, int sampleRate)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                var dataBytes = pcm16Mono.Length;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16); // fmt chunk size
                writer.Write((short)1); // PCM
                writer.Write((short)1); // mono
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2); // byte rate
                writer.Write((short)2); // block align
                writer.Write((short)16); // bits per sample
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);
                writer.Write(pcm16Mono);
                return ms.ToArray();
            }
        }
    }
}
