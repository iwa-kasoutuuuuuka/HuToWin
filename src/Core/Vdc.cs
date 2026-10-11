// HuC6270 VDC + HuC6260 VCE
using System;

namespace AlienCrush.Core
{
    public sealed class Vdc
    {
        public const int MaxW = 512, MaxH = 263;
        readonly Machine m;
        public readonly ushort[] Vram = new ushort[0x8000];
        public readonly ushort[] Sat = new ushort[256];
        public readonly ushort[] R = new ushort[32];
        int addrReg;
        byte vwrLow, dataLowLatch;
        ushort readBuf;
        byte status;                 // bit0 CR,1 OR,2 RR,3 DS,4 DV,5 VD
        bool satbPending;
        int bgYCounter;              // background Y line counter
        bool byrWritten;
        public int Line;             // 0..261
        int dispStart, dispHeight;
        public int Width = 256, Height = 240;
        public int FrameWidth = 256, FrameHeight = 240;
        // frame buffer of palette indices (0..511), row stride MaxW
        public readonly ushort[] Frame = new ushort[MaxW * MaxH];
        readonly ushort[] bgLine = new ushort[MaxW + 16];
        readonly ushort[] spLine = new ushort[MaxW + 64];
        readonly byte[] spPrio = new byte[MaxW + 64];
        readonly byte[] sp0Mask = new byte[MaxW + 64];   // opaque pixels of sprite 0 (collision detection)
        public const int MaxSpritesPerLine = 16;

        // VCE
        public readonly ushort[] Palette = new ushort[512];
        int vceAddr; byte vceCtrl;

        public Vdc(Machine machine) { m = machine; Reset(); }

        public void Reset()
        {
            Array.Clear(R, 0, R.Length); Array.Clear(Vram, 0, Vram.Length);
            R[0x0B] = 0x1F; R[0x0D] = 0xEF; R[0x0C] = 0x0F02; R[0x0E] = 0x04;
            status = 0; satbPending = false; Line = 0; vceAddr = 0; vceCtrl = 0;
            dispStart = 14; dispHeight = 240;
        }

        public void SaveState(System.IO.BinaryWriter w)
        {
            foreach (var v in Vram) w.Write(v);
            foreach (var v in Sat) w.Write(v);
            foreach (var v in R) w.Write(v);
            foreach (var v in Palette) w.Write(v);
            w.Write(addrReg); w.Write(vwrLow); w.Write(dataLowLatch); w.Write(readBuf); w.Write(status);
            w.Write(satbPending); w.Write(bgYCounter); w.Write(byrWritten);
            w.Write(dispStart); w.Write(dispHeight); w.Write(Width); w.Write(Height);
            w.Write(FrameWidth); w.Write(FrameHeight); w.Write(vceAddr); w.Write(vceCtrl);
        }
        public void LoadState(System.IO.BinaryReader r)
        {
            for (int i = 0; i < Vram.Length; i++) Vram[i] = r.ReadUInt16();
            for (int i = 0; i < Sat.Length; i++) Sat[i] = r.ReadUInt16();
            for (int i = 0; i < R.Length; i++) R[i] = r.ReadUInt16();
            for (int i = 0; i < Palette.Length; i++) Palette[i] = (ushort)(r.ReadUInt16() & 0x1FF);
            addrReg = r.ReadInt32() & 0x1F; vwrLow = r.ReadByte(); dataLowLatch = r.ReadByte(); readBuf = r.ReadUInt16(); status = r.ReadByte();
            satbPending = r.ReadBoolean(); bgYCounter = r.ReadInt32() & 0x1FF; byrWritten = r.ReadBoolean();
            // clamp geometry so a damaged file cannot index outside the frame buffer
            dispStart = r.ReadInt32(); dispHeight = Clamp(r.ReadInt32(), 1, 242); Width = Clamp(r.ReadInt32(), 8, MaxW); Height = Clamp(r.ReadInt32(), 1, 242);
            FrameWidth = Clamp(r.ReadInt32(), 8, MaxW); FrameHeight = Clamp(r.ReadInt32(), 1, 242); vceAddr = r.ReadInt32() & 0x1FF; vceCtrl = r.ReadByte();
        }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        public bool IrqLine
        {
            get
            {
                ushort cr = R[5];
                if ((status & 0x20) != 0 && (cr & 0x08) != 0) return true;   // vblank
                if ((status & 0x04) != 0 && (cr & 0x04) != 0) return true;   // raster
                if ((status & 0x01) != 0 && (cr & 0x01) != 0) return true;   // collision
                if ((status & 0x02) != 0 && (cr & 0x02) != 0) return true;   // overflow
                ushort dcr = R[0x0F];
                if ((status & 0x08) != 0 && (dcr & 0x01) != 0) return true;  // satb dma
                if ((status & 0x10) != 0 && (dcr & 0x02) != 0) return true;  // vram dma
                return false;
            }
        }

        int Increment
        {
            get { switch ((R[5] >> 11) & 3) { case 0: return 1; case 1: return 0x20; case 2: return 0x40; default: return 0x80; } }
        }

        // ---- CPU port access ($0000-$0003) ----
        public byte Read(int port)
        {
            switch (port & 3)
            {
                case 0: { byte s = status; status = 0; return s; }
                case 2: return (byte)readBuf;
                case 3:
                    {
                        byte hi = (byte)(readBuf >> 8);
                        if (addrReg == 2) { R[1] = (ushort)(R[1] + Increment); readBuf = Vram[R[1] & 0x7FFF]; }
                        return hi;
                    }
                default: return 0;
            }
        }
        public void Write(int port, byte v)
        {
            switch (port & 3)
            {
                case 0: addrReg = v & 0x1F; break;
                case 2: WriteLow(v); break;
                case 3: WriteHigh(v); break;
            }
        }
        public void WriteSt0(byte v) { Write(0, v); }
        public void WriteSt1(byte v) { Write(2, v); }
        public void WriteSt2(byte v) { Write(3, v); }

        void WriteLow(byte v)
        {
            if (addrReg == 2) { vwrLow = v; return; }
            R[addrReg] = (ushort)((R[addrReg] & 0xFF00) | v);
            if (addrReg == 8) { bgYCounter = R[8] & 0x1FF; byrWritten = true; }
        }
        void WriteHigh(byte v)
        {
            switch (addrReg)
            {
                case 2:
                    // only 32K words are fitted: writes above $7FFF are dropped
                    if (R[0] < 0x8000) Vram[R[0]] = (ushort)(vwrLow | (v << 8));
                    R[0] = (ushort)(R[0] + Increment);
                    return;
                case 1:
                    R[1] = (ushort)((R[1] & 0x00FF) | (v << 8));
                    readBuf = Vram[R[1] & 0x7FFF];
                    return;
            }
            R[addrReg] = (ushort)((R[addrReg] & 0x00FF) | (v << 8));
            switch (addrReg)
            {
                case 8: bgYCounter = R[8] & 0x1FF; byrWritten = true; break;
                case 0x12: DoVramDma(); break;
                case 0x13: satbPending = true; break;
                case 0x0C: case 0x0D: UpdateWindow(); break;
                case 0x0B: Width = Math.Min(MaxW, ((R[0x0B] & 0x7F) + 1) * 8); break;
            }
        }
        void UpdateWindow()
        {
            dispStart = ((R[0x0C] >> 8) & 0xFF) + (R[0x0C] & 0x1F) + 2;
            dispHeight = (R[0x0D] & 0x1FF) + 1;
            if (dispHeight > 242) dispHeight = 242;
            Height = dispHeight;
        }
        void DoVramDma()
        {
            int src = R[0x10], dst = R[0x11], len = R[0x12] + 1;
            bool sdec = (R[0x0F] & 4) != 0, ddec = (R[0x0F] & 8) != 0;
            for (int i = 0; i < len; i++)
            {
                if ((dst & 0x8000) == 0) Vram[dst & 0x7FFF] = Vram[src & 0x7FFF];
                src += sdec ? -1 : 1; dst += ddec ? -1 : 1;
            }
            R[0x10] = (ushort)src; R[0x11] = (ushort)dst;
            status |= 0x10;
        }
        void DoSatbDma()
        {
            int src = R[0x13] & 0x7FFF;
            for (int i = 0; i < 256; i++) Sat[i] = Vram[(src + i) & 0x7FFF];
            status |= 0x08;
            satbPending = (R[0x0F] & 0x10) != 0;
        }

        // ---- VCE ($0400-$0407) ----
        public byte VceRead(int port)
        {
            switch (port & 7)
            {
                case 4: return (byte)Palette[vceAddr];
                case 5: { byte hi = (byte)((Palette[vceAddr] >> 8) | 0xFE); vceAddr = (vceAddr + 1) & 0x1FF; return hi; }
                default: return 0xFF;
            }
        }
        public void VceWrite(int port, byte v)
        {
            switch (port & 7)
            {
                case 0: vceCtrl = v; break;
                case 2: vceAddr = (vceAddr & 0x100) | v; break;
                case 3: vceAddr = (vceAddr & 0xFF) | ((v & 1) << 8); break;
                case 4: Palette[vceAddr] = (ushort)((Palette[vceAddr] & 0x100) | v); break;
                case 5: Palette[vceAddr] = (ushort)((Palette[vceAddr] & 0xFF) | ((v & 1) << 8)); vceAddr = (vceAddr + 1) & 0x1FF; break;
            }
        }
        public static int ToArgb(int c9)
        {
            int g = (c9 >> 6) & 7, r = (c9 >> 3) & 7, b = c9 & 7;
            return unchecked((int)0xFF000000) | ((r * 255 / 7) << 16) | ((g * 255 / 7) << 8) | (b * 255 / 7);
        }

        // ---- per scanline processing; called by Machine at the start of each line ----
        public bool FrameDone;
        public void StartLine()
        {
            int dl = Line - dispStart;           // display-relative line
            if (Line == 0) { FrameDone = false; }
            if (dl == 0) { bgYCounter = R[8] & 0x1FF; byrWritten = false; FrameWidth = Width; FrameHeight = dispHeight; }
            // raster compare: RCR = 64 + display line
            if ((R[5] & 0x04) != 0 && dl + 64 == (R[6] & 0x3FF) && dl >= -1 && dl < dispHeight + 1)
            {
                status |= 0x04;
            }
            if (dl >= 0 && dl < dispHeight)
            {
                RenderLine(dl);
                if (!byrWritten) bgYCounter++; else byrWritten = false;
                bgYCounter &= 0x1FF;
            }
            if (dl == dispHeight)
            {
                status |= 0x20;                  // vblank
                if (satbPending) DoSatbDma();
                FrameDone = true;
            }
        }

        void RenderLine(int dl)
        {
            int w = Width;
            ushort cr = R[5];
            bool bgOn = (cr & 0x80) != 0, spOn = (cr & 0x40) != 0;
            int mwr = R[9];
            int bw = ((mwr >> 4) & 3) == 0 ? 32 : ((mwr >> 4) & 3) == 1 ? 64 : 128;
            int bh = ((mwr >> 6) & 1) == 0 ? 32 : 64;
            int y = bgYCounter & (bh * 8 - 1);
            int x0 = R[7] & (bw * 8 - 1);
            int ty = y >> 3, fy = y & 7;
            if (bgOn)
            {
                int x = 0;
                int px = x0;
                while (x < w)
                {
                    int tx = (px >> 3) & (bw - 1);
                    ushort bat = Vram[(ty * bw + tx) & 0x7FFF];
                    int tile = bat & 0xFFF; int pal = (bat >> 12) << 4;
                    int pa = (tile << 4) + fy;
                    ushort w01 = Vram[pa & 0x7FFF], w23 = Vram[(pa + 8) & 0x7FFF];
                    int p0 = w01 & 0xFF, p1 = w01 >> 8, p2 = w23 & 0xFF, p3 = w23 >> 8;
                    int sx = px & 7;
                    for (int i = sx; i < 8 && x < w; i++, x++)
                    {
                        int sh = 7 - i;
                        int c = ((p0 >> sh) & 1) | (((p1 >> sh) & 1) << 1) | (((p2 >> sh) & 1) << 2) | (((p3 >> sh) & 1) << 3);
                        bgLine[x] = (ushort)(c == 0 ? 0 : pal | c);
                    }
                    px += 8 - sx;
                }
            }
            else
            {
                for (int x = 0; x < w; x++) bgLine[x] = 0;
            }
            if (spOn) RenderSprites(dl, w); else { for (int x = 0; x < w; x++) spLine[x] = 0; }
            int o = dl * MaxW;
            for (int x = 0; x < w; x++)
            {
                ushort s = spLine[x];
                ushort b = bgLine[x];
                if (s != 0 && (spPrio[x] != 0 || b == 0)) Frame[o + x] = s; else Frame[o + x] = b;
            }
        }

        void RenderSprites(int dl, int w)
        {
            for (int x = 0; x < w; x++) { spLine[x] = 0; spPrio[x] = 0; sp0Mask[x] = 0; }
            int cells = 0;                       // 16-pixel-wide sprite cells used on this line
            for (int i = 0; i < 64; i++)
            {
                int b = i * 4;
                int sy = (Sat[b] & 0x3FF) - 64;
                int sx = (Sat[b + 1] & 0x3FF) - 32;
                ushort a3 = Sat[b + 3];
                int cgy = (a3 >> 12) & 3; if (cgy == 2) cgy = 3;
                int rows = cgy + 1;                 // 1,2,4 cells tall
                int h = rows * 16;
                if (dl < sy || dl >= sy + h) continue;
                int cgx = (a3 >> 8) & 1;
                int cols = cgx + 1;
                int wd = cols * 16;
                // hardware limit: 16 sprite cells per scanline; the rest are dropped and the overflow flag is set
                if (cells + cols > MaxSpritesPerLine) { status |= 0x02; break; }
                cells += cols;
                if (sx >= w || sx + wd <= 0) continue;
                bool yflip = (a3 & 0x8000) != 0, xflip = (a3 & 0x0800) != 0;
                int prio = (a3 >> 7) & 1;
                int pal = 256 + ((a3 & 0xF) << 4);
                int code = (Sat[b + 2] >> 1) & 0x3FF;
                if (cgx != 0) code &= ~1;
                if (cgy == 1) code &= ~2; else if (cgy == 3) code &= ~6;
                int baseAddr = code << 6;
                int ly = dl - sy; if (yflip) ly = h - 1 - ly;
                int row = ly >> 4, fy = ly & 15;
                for (int col = 0; col < cols; col++)
                {
                    int cc = xflip ? cols - 1 - col : col;
                    int ca = baseAddr + row * 0x80 + cc * 0x40 + fy;
                    ushort p0 = Vram[ca & 0x7FFF], p1 = Vram[(ca + 16) & 0x7FFF], p2 = Vram[(ca + 32) & 0x7FFF], p3 = Vram[(ca + 48) & 0x7FFF];
                    int xb = sx + col * 16;
                    for (int px = 0; px < 16; px++)
                    {
                        int x = xb + px;
                        if (x < 0 || x >= w) continue;
                        int sh = xflip ? px : 15 - px;
                        int c = ((p0 >> sh) & 1) | (((p1 >> sh) & 1) << 1) | (((p2 >> sh) & 1) << 2) | (((p3 >> sh) & 1) << 3);
                        if (c == 0) continue;
                        if (i == 0) sp0Mask[x] = 1;
                        else if (sp0Mask[x] != 0) status |= 0x01;   // sprite 0 collision
                        if (spLine[x] != 0) continue;
                        spLine[x] = (ushort)(pal | c); spPrio[x] = (byte)prio;
                    }
                }
            }
        }

        // Convert frame (palette indices) to ARGB pixels; dst length >= FrameWidth*FrameHeight
        public void Blit(int[] dst)
        {
            int w = FrameWidth, h = FrameHeight;
            var lut = new int[512];
            for (int i = 0; i < 512; i++) lut[i] = ToArgb(Palette[i]);
            int bg = lut[0];
            for (int y = 0; y < h; y++)
            {
                int so = y * MaxW, d = y * w;
                for (int x = 0; x < w; x++)
                {
                    int c = Frame[so + x];
                    // BG palettes: colour 0 of any palette shows palette 0 colour 0
                    dst[d + x] = (c < 256 && (c & 15) == 0) ? bg : lut[c];
                }
            }
        }
    }
}
