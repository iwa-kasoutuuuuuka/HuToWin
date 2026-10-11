using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AlienCrush.App
{
    // Minimal streaming audio output via winmm waveOut (no external dependencies)
    sealed class WaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        const int WHDR_DONE = 1;
        static readonly int FlagsOff = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");

        IntPtr handle;
        readonly int bufCount, bufSamples;   // stereo samples per buffer
        readonly IntPtr[] hdrs; readonly IntPtr[] data; readonly bool[] queued;
        int cur;
        readonly short[] pending; int pendingLen;
        public bool Ok { get; private set; }

        public WaveOut(int sampleRate, int bufferSamples, int buffers)
        {
            bufSamples = bufferSamples; bufCount = buffers;
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = sampleRate, wBitsPerSample = 16, cbSize = 0 };
            fmt.nBlockAlign = 4; fmt.nAvgBytesPerSec = sampleRate * 4;
            int r = waveOutOpen(out handle, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
            Ok = r == 0;
            hdrs = new IntPtr[bufCount]; data = new IntPtr[bufCount]; queued = new bool[bufCount];
            pending = new short[bufSamples * 2];
            if (!Ok) return;
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < bufCount; i++)
            {
                data[i] = Marshal.AllocHGlobal(bufSamples * 4);
                hdrs[i] = Marshal.AllocHGlobal(hs);
                var h = new WAVEHDR { lpData = data[i], dwBufferLength = bufSamples * 4 };
                Marshal.StructureToPtr(h, hdrs[i], false);
                waveOutPrepareHeader(handle, hdrs[i], hs);
            }
        }

        // Write interleaved stereo samples; blocks while the ring is full (paces the caller)
        public void Write(short[] samples, int count)
        {
            if (!Ok) return;
            int pos = 0;
            while (pos < count)
            {
                int n = Math.Min(count - pos, pending.Length - pendingLen);
                Array.Copy(samples, pos, pending, pendingLen, n); pendingLen += n; pos += n;
                if (pendingLen == pending.Length) { Submit(); pendingLen = 0; }
            }
        }

        void Submit()
        {
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            if (queued[cur])
            {
                int waited = 0;
                while ((Marshal.ReadInt32(hdrs[cur], FlagsOff) & WHDR_DONE) == 0 && waited < 200) { Thread.Sleep(1); waited++; }
                if ((Marshal.ReadInt32(hdrs[cur], FlagsOff) & WHDR_DONE) == 0) return; // device stalled: drop
            }
            Marshal.Copy(pending, 0, data[cur], pending.Length);
            Marshal.WriteInt32(hdrs[cur], FlagsOff, Marshal.ReadInt32(hdrs[cur], FlagsOff) & ~WHDR_DONE);
            waveOutWrite(handle, hdrs[cur], hs);
            queued[cur] = true;
            cur = (cur + 1) % bufCount;
        }

        public void Dispose()
        {
            if (!Ok) return;
            Ok = false;
            waveOutReset(handle);
            int hs = Marshal.SizeOf(typeof(WAVEHDR));
            for (int i = 0; i < bufCount; i++) { waveOutUnprepareHeader(handle, hdrs[i], hs); Marshal.FreeHGlobal(hdrs[i]); Marshal.FreeHGlobal(data[i]); }
            waveOutClose(handle);
        }
    }
}
