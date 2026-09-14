using System;
using System.Runtime.InteropServices;

namespace OliverAssistant
{
    // Minimal winmm (waveIn) microphone capture - no external libraries required, matching
    // how the rest of Cosmo talks to Windows directly. Records into a single pre-allocated
    // buffer for the duration of a push-to-talk hold: Start() on key-down, StopAndGetPcm()
    // on key-up. Using one buffer instead of a streaming/double-buffered callback setup
    // avoids calling winmm functions from inside the driver callback, which is unsafe.
    public class WaveInRecorder : IDisposable
    {
        public const int SampleRate = 16000;
        private const int MaxSeconds = 30;
        private const int WAVE_MAPPER = -1;
        private const short WAVE_FORMAT_PCM = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private class WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, WAVEHDR pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, WAVEHDR pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, WAVEHDR pwh, int cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        private IntPtr _handle = IntPtr.Zero;
        private WAVEHDR _header;
        private GCHandle _pinnedBuffer;
        private byte[] _buffer;
        private bool _prepared;

        public void Start()
        {
            var format = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = SampleRate * 2,
                cbSize = 0
            };

            var result = waveInOpen(out _handle, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0 /* CALLBACK_NULL */);
            if (result != 0)
                throw new InvalidOperationException("waveInOpen failed (mmresult " + result + ")");

            _buffer = new byte[SampleRate * 2 * MaxSeconds];
            _pinnedBuffer = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
            _header = new WAVEHDR
            {
                lpData = _pinnedBuffer.AddrOfPinnedObject(),
                dwBufferLength = _buffer.Length
            };

            var hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
            waveInPrepareHeader(_handle, _header, hdrSize);
            _prepared = true;
            waveInAddBuffer(_handle, _header, hdrSize);
            waveInStart(_handle);
        }

        public byte[] StopAndGetPcm()
        {
            if (_handle == IntPtr.Zero) return new byte[0];

            waveInStop(_handle); // synchronously flushes the in-flight buffer as "done"

            var recorded = _header.dwBytesRecorded;
            var result = new byte[recorded];
            Array.Copy(_buffer, result, recorded);

            if (_prepared)
            {
                waveInUnprepareHeader(_handle, _header, Marshal.SizeOf(typeof(WAVEHDR)));
                _prepared = false;
            }
            waveInClose(_handle);
            _handle = IntPtr.Zero;
            if (_pinnedBuffer.IsAllocated) _pinnedBuffer.Free();

            return result;
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                try
                {
                    waveInReset(_handle);
                    if (_prepared)
                    {
                        waveInUnprepareHeader(_handle, _header, Marshal.SizeOf(typeof(WAVEHDR)));
                        _prepared = false;
                    }
                    waveInClose(_handle);
                }
                catch
                {
                    // best-effort cleanup
                }
                _handle = IntPtr.Zero;
            }
            if (_pinnedBuffer.IsAllocated) _pinnedBuffer.Free();
        }
    }
}
