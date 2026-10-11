// PC Engine machine: memory map, I/O, timing
using System;

namespace AlienCrush.Core
{
    public sealed class Machine
    {
        public const int CyclesPerLine = 455;
        public const int LinesPerFrame = 262;
        public readonly byte[] Rom;
        readonly int[] romMap = new int[0x80];   // bank -> ROM offset
        // Street Fighter II' (2.5MB): banks $40-$7F show one of four 512KB pages, chosen by a write to $1FF0-$1FF3
        readonly bool sf2; int sf2Page;
        // 6-button pad (enabled for Street Fighter II' only: it confuses games that do not know it)
        public readonly bool SixButton; bool sixPhase;
        public readonly byte[] Ram = new byte[0x2000];
        public readonly byte[] Mpr = new byte[8];
        public readonly Cpu Cpu;
        public readonly Vdc Vdc;
        public readonly Psg Psg;
        // joypad: bit0 I, bit1 II, bit2 SELECT, bit3 RUN, bit4 UP, bit5 RIGHT, bit6 DOWN, bit7 LEFT, bit8-11 III IV V VI  (1 = pressed)
        public volatile int PadState;
        public volatile int PadState2;   // player 2 (multitap port 2)
        bool padSel, padClr;
        int padIndex;                // multitap port being scanned (pads 1 and 2 are plugged in)
        byte irqMask, irqStatus;     // $1402 / $1403 (bit0 IRQ2, bit1 IRQ1, bit2 TIMER)
        byte timerReload; int timerCounter; bool timerOn; int timerDiv;
        byte ioBuffer;
        public long FrameCount;

        public Machine(byte[] rom)
        {
            Rom = rom;
            int banks = Math.Max(1, rom.Length / 0x2000);
            sf2 = SixButton = rom.Length == 0x280000;
            for (int i = 0; i < 0x80; i++)
            {
                int b;
                // 384KB cards are split 256KB + 128KB, 512KB cards mirror their upper half at bank $40
                if (banks == 0x30) b = i < 0x40 ? (i & 0x1F) : 0x20 + (i & 0x0F);
                else if (sf2) b = i & 0x3F;
                else if (banks == 0x40) b = i < 0x40 ? i : 0x20 + (i & 0x1F);
                else b = i % banks;
                romMap[i] = b << 13;
            }
            Cpu = new Cpu(this); Vdc = new Vdc(this); Psg = new Psg();
            Reset();
        }

        public void Reset()
        {
            Array.Clear(Ram, 0, Ram.Length);
            for (int i = 0; i < 8; i++) Mpr[i] = 0;
            Mpr[7] = 0;
            padSel = padClr = false; padIndex = 0; sf2Page = 0; sixPhase = false;
            irqMask = 0; irqStatus = 0; timerOn = false; timerReload = 0; timerCounter = 0; timerDiv = 0;
            Vdc.Reset();
            Psg.Reset();
            Cpu.Reset();
        }

        // ---- save states ----
        const int StateMagic = 0x34545341;   // "AST4"
        public byte[] SaveState()
        {
            var ms = new System.IO.MemoryStream();
            var w = new System.IO.BinaryWriter(ms);
            w.Write(StateMagic); w.Write(Rom.Length);
            w.Write(Ram); w.Write(Mpr);
            w.Write(padSel); w.Write(padClr); w.Write(irqMask); w.Write(irqStatus);
            w.Write(timerReload); w.Write(timerCounter); w.Write(timerOn); w.Write(timerDiv); w.Write(ioBuffer); w.Write(FrameCount); w.Write((byte)padIndex); w.Write((byte)sf2Page); w.Write(sixPhase);
            Cpu.SaveState(w); Vdc.SaveState(w); Psg.SaveState(w);
            w.Flush();
            return ms.ToArray();
        }
        // Returns false (machine untouched) if the data is not a state for this ROM
        public bool LoadState(byte[] data)
        {
            if (data == null || data.Length != SaveState().Length) return false;
            var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            if (r.ReadInt32() != StateMagic || r.ReadInt32() != Rom.Length) return false;
            r.Read(Ram, 0, Ram.Length); r.Read(Mpr, 0, Mpr.Length);
            padSel = r.ReadBoolean(); padClr = r.ReadBoolean(); irqMask = (byte)(r.ReadByte() & 7); irqStatus = (byte)(r.ReadByte() & 7);
            timerReload = (byte)(r.ReadByte() & 0x7F); timerCounter = r.ReadInt32() & 0x7F; timerOn = r.ReadBoolean(); timerDiv = r.ReadInt32() & 0x3FF; ioBuffer = r.ReadByte(); FrameCount = r.ReadInt64(); padIndex = Math.Min((int)r.ReadByte(), 5); sf2Page = r.ReadByte() & 3; sixPhase = r.ReadBoolean();
            Cpu.LoadState(r); Vdc.LoadState(r); Psg.LoadState(r);
            return true;
        }

        // ---- bus ----
        public byte Read(ushort addr)
        {
            int bank = Mpr[addr >> 13];
            int off = addr & 0x1FFF;
            if (bank < 0x80)
            {
                int idx = romMap[bank] | off;
                if (sf2 && bank >= 0x40) idx += (sf2Page + 1) << 19;
                return idx < Rom.Length ? Rom[idx] : (byte)0xFF;
            }
            if (bank >= 0xF8 && bank <= 0xFB) return Ram[off];
            if (bank == 0xFF) return ReadIo(off);
            return 0xFF;
        }
        public void Write(ushort addr, byte v)
        {
            int bank = Mpr[addr >> 13];
            int off = addr & 0x1FFF;
            if (bank >= 0xF8 && bank <= 0xFB) { Ram[off] = v; return; }
            if (bank == 0xFF) { WriteIo(off, v); return; }
            if (sf2 && bank < 0x80 && (off & 0x1FFC) == 0x1FF0) sf2Page = off & 3;
        }

        byte ReadIo(int off)
        {
            switch (off >> 10)
            {
                case 0: return Vdc.Read(off & 3);
                case 1: return Vdc.VceRead(off & 7);
                case 2: return ioBuffer;
                case 3: return (byte)((ioBuffer & 0x80) | (timerCounter & 0x7F));
                case 4: return ReadPad();
                case 5:
                    switch (off & 3)
                    {
                        case 2: return (byte)((ioBuffer & 0xF8) | (irqMask & 7));
                        case 3: return (byte)((ioBuffer & 0xF8) | (irqStatus & 7));
                        default: return ioBuffer;
                    }
                default: return 0xFF;
            }
        }
        void WriteIo(int off, byte v)
        {
            switch (off >> 10)
            {
                case 0: Vdc.Write(off & 3, v); break;
                case 1: Vdc.VceWrite(off & 7, v); break;
                case 2: ioBuffer = v; Psg.Write(off & 0xF, v); break;
                case 3:
                    ioBuffer = v;
                    if ((off & 1) == 0) timerReload = (byte)(v & 0x7F);
                    else { bool on = (v & 1) != 0; if (on && !timerOn) { timerCounter = timerReload; timerDiv = 0; } timerOn = on; }
                    break;
                case 4: ioBuffer = v; WritePad(v); break;
                case 5:
                    ioBuffer = v;
                    if ((off & 3) == 2) irqMask = (byte)(v & 7);
                    else if ((off & 3) == 3) irqStatus &= unchecked((byte)~0x04);  // ack timer
                    break;
            }
        }

        // ---- joypad ($1000) ----
        byte ReadPad()
        {
            int nib;
            if (padClr) nib = 0;
            else
            {
                int p = padIndex == 0 ? PadState : padIndex == 1 ? PadState2 : 0;
                if (SixButton && sixPhase) nib = padSel ? (padIndex <= 1 ? 0xF : 0) : (p >> 8) & 0xF;   // signature / III IV V VI
                else if (padSel) nib = ((p >> 4) & 0xF);      // UP RIGHT DOWN LEFT
                else nib = p & 0xF;                       // I II SEL RUN
            }
            // active low; bit6 = 0 (TurboGrafx-16 region), bit7 = 1 (no CD-ROM)
            return (byte)(0xB0 | (~nib & 0xF));
        }
        void WritePad(byte v)
        {
            bool sel = (v & 1) != 0, clr = (v & 2) != 0;
            if (sel && !padSel) sixPhase = !sixPhase;
            if (clr) padIndex = 0;
            else if (sel && !padSel && padIndex < 5) padIndex++;
            padSel = sel; padClr = clr;
        }

        // ---- timing ----
        void StepTimer(int cycles)
        {
            if (!timerOn) return;
            timerDiv += cycles;
            while (timerDiv >= 1024)
            {
                timerDiv -= 1024;
                if (timerCounter == 0) { timerCounter = timerReload; irqStatus |= 0x04; }
                else timerCounter--;
            }
        }

        void CheckIrq()
        {
            if (Cpu.IrqInhibit) return;
            if ((irqStatus & 0x04) != 0 && (irqMask & 0x04) == 0) { Cpu.Interrupt(0xFFFA); return; }
            if (Vdc.IrqLine && (irqMask & 0x02) == 0) { Cpu.Interrupt(0xFFF8); return; }
        }

        public bool Trace; public readonly int[] TracePcs = new int[65536];
        // Run one full frame (262 lines)
        public void RunFrame()
        {
            for (int line = 0; line < LinesPerFrame; line++)
            {
                Vdc.Line = line;
                Vdc.StartLine();
                int target = CyclesPerLine;
                int used = 0;
                while (used < target)
                {
                    CheckIrq();
                    if (Trace) TracePcs[Cpu.PC]++;
                    int c = Cpu.Step();
                    used += c;
                    StepTimer(c);
                    Psg.Run(c);
                }
            }
            FrameCount++;
        }
    }
}
