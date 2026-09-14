using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace OliverAssistant
{
    [DataContract]
    internal class WhisperResultJson
    {
        [DataMember(Name = "text")] public string Text;
    }

    // Wraps the official whisper.cpp server binary rather than P/Invoking whisper.dll
    // directly: whisper_full_params is a large, frequently-changing native struct, and
    // hand-marshaling it blind is far more likely to silently misalign than talking the
    // server's small, stable "upload a wav, get JSON back" HTTP contract. The model loads
    // once at startup and stays resident for the life of the process, so each dictation
    // request is just an HTTP round trip.
    public class WhisperEngine : IDisposable
    {
        private const int TargetSampleRate = 16000;

        private readonly Process _serverProcess;
        private readonly string _inferenceUrl;
        private readonly int _port;

        private WhisperEngine(Process serverProcess, string inferenceUrl, int port)
        {
            _serverProcess = serverProcess;
            _inferenceUrl = inferenceUrl;
            _port = port;
        }

        public static WhisperEngine Start(string serverExePath, string modelPath, int port, Action<string> log)
        {
            if (!File.Exists(serverExePath))
                throw new InvalidOperationException("whisper-server.exe not found at " + serverExePath);
            if (!File.Exists(modelPath))
                throw new InvalidOperationException("Whisper model not found at " + modelPath);

            var psi = new ProcessStartInfo
            {
                FileName = serverExePath,
                // -t 8: use all physical cores for decoding instead of the server's
                // default of 4 - this is the main lever for cutting the delay between
                // releasing the PTT key and text appearing.
                Arguments = "-m \"" + modelPath + "\" --host 127.0.0.1 --port " + port + " -nt -l en -t 8",
                WorkingDirectory = Path.GetDirectoryName(serverExePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to launch whisper-server.exe");

            // Drain both pipes so the child never blocks trying to write to a full buffer.
            process.OutputDataReceived += (s, e) => { if (e.Data != null) log("whisper-server: " + e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) log("whisper-server: " + e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var engine = new WhisperEngine(process, "http://127.0.0.1:" + port + "/inference", port);

            if (!engine.WaitUntilPortOpen(TimeSpan.FromSeconds(30)))
            {
                engine.Dispose();
                throw new InvalidOperationException("whisper-server.exe did not start listening in time (check log for its output)");
            }

            return engine;
        }

        // Loading a ~500MB model can take a couple of seconds, so poll for the port to
        // accept connections rather than assuming a fixed startup delay.
        private bool WaitUntilPortOpen(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_serverProcess.HasExited) return false;
                try
                {
                    using (var client = new TcpClient())
                    {
                        var result = client.BeginConnect("127.0.0.1", _port, null, null);
                        if (result.AsyncWaitHandle.WaitOne(500) && client.Connected) return true;
                    }
                }
                catch
                {
                    // not up yet
                }
                Thread.Sleep(300);
            }
            return false;
        }

        public string Transcribe(byte[] pcm16Mono, int sampleRate)
        {
            var resampled = sampleRate == TargetSampleRate ? pcm16Mono : Resample(pcm16Mono, sampleRate, TargetSampleRate);
            var wav = WavWriter.Wrap(resampled, TargetSampleRate);

            var boundary = "----CosmoWhisper" + Guid.NewGuid().ToString("N");
            var request = (HttpWebRequest)WebRequest.Create(_inferenceUrl);
            request.Method = "POST";
            request.ContentType = "multipart/form-data; boundary=" + boundary;
            request.Timeout = 60000;

            using (var stream = request.GetRequestStream())
            {
                WriteMultipartFile(stream, boundary, "file", "audio.wav", "audio/wav", wav);
                WriteMultipartField(stream, boundary, "response_format", "json");
                var closing = Encoding.ASCII.GetBytes("--" + boundary + "--\r\n");
                stream.Write(closing, 0, closing.Length);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(WhisperResultJson));
                var result = (WhisperResultJson)serializer.ReadObject(responseStream);
                return (result.Text ?? "").Trim();
            }
        }

        private static void WriteMultipartField(Stream stream, string boundary, string name, string value)
        {
            var header = "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"\r\n\r\n" + value + "\r\n";
            var bytes = Encoding.UTF8.GetBytes(header);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteMultipartFile(Stream stream, string boundary, string name, string fileName, string contentType, byte[] data)
        {
            var header = "--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + name + "\"; filename=\"" + fileName +
                         "\"\r\nContent-Type: " + contentType + "\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(data, 0, data.Length);
            var footer = Encoding.ASCII.GetBytes("\r\n");
            stream.Write(footer, 0, footer.Length);
        }

        // Simple linear-interpolation resample. System.Speech doesn't guarantee 16kHz
        // audio (it negotiates whatever format the mic driver offers), but whisper.cpp's
        // server reads the WAV's declared rate as-is rather than resampling for you.
        private static byte[] Resample(byte[] pcm16Mono, int sourceRate, int targetRate)
        {
            var sourceSamples = pcm16Mono.Length / 2;
            var destSamples = (int)((long)sourceSamples * targetRate / sourceRate);
            var dest = new byte[destSamples * 2];
            for (var i = 0; i < destSamples; i++)
            {
                var srcPos = (double)i * sourceRate / targetRate;
                var srcIndex = (int)srcPos;
                var frac = srcPos - srcIndex;
                var s0 = ReadSample(pcm16Mono, srcIndex, sourceSamples);
                var s1 = ReadSample(pcm16Mono, srcIndex + 1, sourceSamples);
                var interpolated = (short)(s0 + (s1 - s0) * frac);
                dest[i * 2] = (byte)(interpolated & 0xFF);
                dest[i * 2 + 1] = (byte)((interpolated >> 8) & 0xFF);
            }
            return dest;
        }

        private static short ReadSample(byte[] pcm, int index, int sampleCount)
        {
            if (index < 0 || index >= sampleCount) index = sampleCount - 1;
            var offset = index * 2;
            return (short)(pcm[offset] | (pcm[offset + 1] << 8));
        }

        public void Dispose()
        {
            try
            {
                if (!_serverProcess.HasExited)
                {
                    _serverProcess.Kill();
                    _serverProcess.WaitForExit(3000);
                }
            }
            catch
            {
                // best-effort shutdown
            }
            _serverProcess.Dispose();
        }
    }
}
