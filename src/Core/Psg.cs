// HuC6280 PSG (6 channel wavetable)
using System;

namespace AlienCrush.Core
{
    public sealed class Psg
    {
        public const int SampleRate = 44100;
        const double PsgClock = 3579545.0;
        const int Oversample = 4;                       // synthesis runs at 4x and is averaged down: less aliasing on high notes
        const double TickRate = SampleRate * (double)Oversample;
        sealed class Ch
        {
            public int Freq; public bool On, Dda; public int Vol; public int PanL = 0xF, PanR = 0xF;
            public readonly byte[] Wave = new byte[32]; public int WaveIdx; public int DdaOut;
            public double Phase; public bool Noise; public int NoiseFreq; public double NoisePhase; public int Lfsr = 1; public int NoiseOut;
        }
        readonly Ch[] ch = new Ch[6];
        int sel; int mainL = 0xF, mainR = 0xF;
        int lfoFreq, lfoCtrl;        // $0808 / $0809
        static readonly double[] VolTab = BuildVolTab();
        static double[] BuildVolTab()
        {
            var t = new double[92];
            for (int i = 0; i < t.Length; i++) t[i] = Math.Pow(10.0, -1.5 * i / 20.0);
            return t;
        }
        readonly short[] buf = new short[SampleRate / 10 * 2];
        int bufPos;
        double sampleAcc;
        double accL, accR; int accN;                    // oversampling accumulator
        double dcInL, dcInR, dcOutL, dcOutR;            // output coupling capacitor (removes DC, softens clicks)

        public Psg() { Reset(); }

        public void Reset()
        {
            for (int i = 0; i < 6; i++) ch[i] = new Ch();
            sel = 0; mainL = mainR = 0xF; lfoFreq = lfoCtrl = 0;
            accL = accR = 0; accN = 0; dcInL = dcInR = dcOutL = dcOutR = 0;
        }

        public void SaveState(System.IO.BinaryWriter w)
        {
            w.Write(sel); w.Write(mainL); w.Write(mainR); w.Write(lfoFreq); w.Write(lfoCtrl); w.Write(sampleAcc);
            w.Write(accL); w.Write(accR); w.Write(accN); w.Write(dcInL); w.Write(dcInR); w.Write(dcOutL); w.Write(dcOutR);
            foreach (var c in ch)
            {
                w.Write(c.Freq); w.Write(c.On); w.Write(c.Dda); w.Write(c.Vol); w.Write(c.PanL); w.Write(c.PanR);
                w.Write(c.Wave); w.Write(c.WaveIdx); w.Write(c.DdaOut); w.Write(c.Phase);
                w.Write(c.Noise); w.Write(c.NoiseFreq); w.Write(c.NoisePhase); w.Write(c.Lfsr); w.Write(c.NoiseOut);
            }
        }
        public void LoadState(System.IO.BinaryReader r)
        {
            sel = r.ReadInt32() & 7; mainL = r.ReadInt32() & 0xF; mainR = r.ReadInt32() & 0xF; lfoFreq = r.ReadInt32() & 0xFF; lfoCtrl = r.ReadInt32() & 0xFF;
            sampleAcc = Unit(r.ReadDouble());
            accL = Level(r.ReadDouble()); accR = Level(r.ReadDouble()); accN = r.ReadInt32() & (Oversample - 1);
            dcInL = Level(r.ReadDouble()); dcInR = Level(r.ReadDouble()); dcOutL = Level(r.ReadDouble()); dcOutR = Level(r.ReadDouble());
            foreach (var c in ch)
            {
                c.Freq = r.ReadInt32() & 0xFFF; c.On = r.ReadBoolean(); c.Dda = r.ReadBoolean(); c.Vol = r.ReadInt32() & 0x1F; c.PanL = r.ReadInt32() & 0xF; c.PanR = r.ReadInt32() & 0xF;
                r.Read(c.Wave, 0, 32); c.WaveIdx = r.ReadInt32() & 31; c.DdaOut = r.ReadInt32() & 0x1F; c.Phase = Unit(r.ReadDouble());
                c.Noise = r.ReadBoolean(); c.NoiseFreq = r.ReadInt32() & 0x1F; c.NoisePhase = Unit(r.ReadDouble()); c.Lfsr = r.ReadInt32(); c.NoiseOut = r.ReadInt32() & 0x1F;
            }
        }
        // phases live in [0,1); anything else (damaged file) would stall the mixer
        static double Unit(double v) { return v >= 0 && v < 1 ? v : 0; }
        static double Level(double v) { return v > -64 && v < 64 ? v : 0; }

        public void Write(int port, byte v)
        {
            switch (port & 0xF)
            {
                case 0: sel = v & 7; break;
                case 1: mainL = v >> 4; mainR = v & 0xF; break;
                case 2: if (sel < 6) ch[sel].Freq = (ch[sel].Freq & 0xF00) | v; break;
                case 3: if (sel < 6) ch[sel].Freq = (ch[sel].Freq & 0x0FF) | ((v & 0xF) << 8); break;
                case 4:
                    if (sel < 6)
                    {
                        var c = ch[sel];
                        bool on = (v & 0x80) != 0, dda = (v & 0x40) != 0;
                        if (!on && !dda) c.WaveIdx = 0;
                        if (dda && !c.Dda) c.WaveIdx = 0;
                        c.On = on; c.Dda = dda; c.Vol = v & 0x1F;
                    }
                    break;
                case 5: if (sel < 6) { ch[sel].PanL = v >> 4; ch[sel].PanR = v & 0xF; } break;
                case 6:
                    if (sel < 6)
                    {
                        var c = ch[sel];
                        if (c.Dda) c.DdaOut = v & 0x1F;
                        else if (!c.On) { c.Wave[c.WaveIdx] = (byte)(v & 0x1F); c.WaveIdx = (c.WaveIdx + 1) & 31; }
                    }
                    break;
                case 7: if (sel >= 4 && sel < 6) { ch[sel].Noise = (v & 0x80) != 0; ch[sel].NoiseFreq = v & 0x1F; } break;
                case 8: lfoFreq = v; break;
                case 9: lfoCtrl = v; if ((v & 0x80) != 0) ch[1].Phase = 0; break;
            }
        }

        // advance by CPU cycles (7.16MHz) and synthesize samples
        public void Run(int cpuCycles)
        {
            sampleAcc += cpuCycles * (TickRate / 7159090.0);
            int n = (int)sampleAcc; sampleAcc -= n;
            for (int i = 0; i < n; i++) Mix();
        }

        void Mix()
        {
            double l = 0, r = 0;
            // LFO: channel 1 waveform modulates channel 0 frequency; channel 1 itself is muted
            bool lfoOn = (lfoCtrl & 3) != 0;
            int lfoAdj = 0;
            if (lfoOn)
            {
                var c1 = ch[1];
                int f1 = c1.Freq == 0 ? 4096 : c1.Freq;
                int lf = lfoFreq == 0 ? 256 : lfoFreq;
                if ((lfoCtrl & 0x80) == 0)
                {
                    double lstep = PsgClock / ((double)f1 * lf) / 32.0 / TickRate;
                    c1.Phase += lstep; if (c1.Phase >= 1.0) c1.Phase -= Math.Floor(c1.Phase);
                }
                int lv = c1.Wave[(int)(c1.Phase * 32) & 31] - 16;
                int shift = ((lfoCtrl & 3) - 1) * 4;
                lfoAdj = lv << shift;
            }
            for (int i = 0; i < 6; i++)
            {
                var c = ch[i];
                if (!c.On) continue;
                if (lfoOn && i == 1) continue;
                int s;
                if (c.Dda) s = c.DdaOut;
                else if (c.Noise)
                {
                    double rate = PsgClock / (64.0 * (32 - c.NoiseFreq));
                    c.NoisePhase += rate / TickRate;
                    while (c.NoisePhase >= 1.0)
                    {
                        c.NoisePhase -= 1.0;
                        int bit = ((c.Lfsr >> 0) ^ (c.Lfsr >> 1) ^ (c.Lfsr >> 11) ^ (c.Lfsr >> 12)) & 1;
                        c.Lfsr = (c.Lfsr >> 1) | (bit << 17);
                        c.NoiseOut = (c.Lfsr & 1) != 0 ? 0x1F : 0;
                    }
                    s = c.NoiseOut;
                }
                else
                {
                    int f = c.Freq;
                    if (lfoOn && i == 0) f = (f + lfoAdj) & 0xFFF;
                    if (f == 0) f = 4096;
                    double step = PsgClock / (32.0 * f) / TickRate;   // waveform cycles per sample
                    c.Phase += step; if (c.Phase >= 1.0) c.Phase -= Math.Floor(c.Phase);
                    s = c.Wave[(int)(c.Phase * 32) & 31];
                }
                int al = (0x1F - c.Vol) + (0xF - c.PanL) * 2 + (0xF - mainL) * 2;
                int ar = (0x1F - c.Vol) + (0xF - c.PanR) * 2 + (0xF - mainR) * 2;
                double v = (s - 16) / 16.0;
                if (al < VolTab.Length) l += v * VolTab[al];
                if (ar < VolTab.Length) r += v * VolTab[ar];
            }
            accL += l; accR += r;
            if (++accN < Oversample) return;
            l = accL / Oversample; r = accR / Oversample; accL = accR = 0; accN = 0;
            dcOutL = l - dcInL + 0.9985 * dcOutL; dcInL = l;
            dcOutR = r - dcInR + 0.9985 * dcOutR; dcInR = r;
            if (bufPos + 2 <= buf.Length)
            {
                buf[bufPos++] = Clamp(dcOutL * 6000); buf[bufPos++] = Clamp(dcOutR * 6000);
            }
        }
        static short Clamp(double v) { if (v > 32767) return 32767; if (v < -32768) return -32768; return (short)v; }

        // fetch and clear accumulated samples (interleaved stereo)
        public int Drain(short[] dst)
        {
            int n = Math.Min(bufPos, dst.Length);
            Array.Copy(buf, dst, n); bufPos = 0; return n;
        }
        public int Pending { get { return bufPos; } }
    }
}
