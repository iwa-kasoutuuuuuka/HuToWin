// HuC6280 CPU core (65C02 + Hudson extensions)
using System;

namespace AlienCrush.Core
{
    public sealed class Cpu
    {
        public const byte FC = 0x01, FZ = 0x02, FI = 0x04, FD = 0x08, FB = 0x10, FT = 0x20, FV = 0x40, FN = 0x80;

        readonly Machine m;
        public byte A, X, Y, S, P;
        public ushort PC;
        public bool HighSpeed = true;
        public long Cycles;          // total cycles (7.16 MHz units)
        bool tFlag;                  // T flag active for current instruction
        bool setPending;

        public Cpu(Machine machine) { m = machine; }

        public void Reset()
        {
            A = X = Y = 0; S = 0xFF; P = FI | FB;
            tFlag = false; setPending = false; HighSpeed = false; IrqInhibit = true;
            PC = (ushort)(m.Read(0xFFFE) | (m.Read(0xFFFF) << 8));
        }

        public void SaveState(System.IO.BinaryWriter w)
        {
            w.Write(A); w.Write(X); w.Write(Y); w.Write(S); w.Write(P); w.Write(PC);
            w.Write(HighSpeed); w.Write(Cycles); w.Write(tFlag); w.Write(setPending);
        }
        public void LoadState(System.IO.BinaryReader r)
        {
            A = r.ReadByte(); X = r.ReadByte(); Y = r.ReadByte(); S = r.ReadByte(); P = r.ReadByte(); PC = r.ReadUInt16();
            HighSpeed = r.ReadBoolean(); Cycles = r.ReadInt64(); tFlag = r.ReadBoolean(); setPending = r.ReadBoolean();
            IrqInhibit = (P & FI) != 0;
        }

        // ---- helpers ----
        byte Rd(int a) { return m.Read((ushort)a); }
        void Wr(int a, byte v) { m.Write((ushort)a, v); }
        byte Fetch() { return m.Read(PC++); }
        ushort Fetch16() { byte lo = Fetch(); byte hi = Fetch(); return (ushort)(lo | (hi << 8)); }
        void Push(byte v) { m.Write((ushort)(0x2100 | S), v); S--; }
        byte Pop() { S++; return m.Read((ushort)(0x2100 | S)); }
        static int Zp(byte a) { return 0x2000 | a; }
        void SetNZ(byte v) { P = (byte)((P & ~(FN | FZ)) | (v & FN) | (v == 0 ? FZ : 0)); }
        ushort Rd16(int a) { return (ushort)(Rd(a) | (Rd(a + 1) << 8)); }
        ushort Rd16Zp(byte a) { return (ushort)(Rd(Zp(a)) | (Rd(Zp((byte)(a + 1))) << 8)); }

        int AddrZp() { return Zp(Fetch()); }
        int AddrZpX() { return Zp((byte)(Fetch() + X)); }
        int AddrZpY() { return Zp((byte)(Fetch() + Y)); }
        int AddrAbs() { return Fetch16(); }
        int AddrAbsX() { return (ushort)(Fetch16() + X); }
        int AddrAbsY() { return (ushort)(Fetch16() + Y); }
        int AddrIndX() { return Rd16Zp((byte)(Fetch() + X)); }
        int AddrIndY() { return (ushort)(Rd16Zp(Fetch()) + Y); }
        int AddrInd() { return Rd16Zp(Fetch()); }

        // ---- ALU ----
        void Adc(byte v)
        {
            if (tFlag) { int za = Zp(X); byte r = AdcCore(Rd(za), v); Wr(za, r); Cycles += 3; }
            else A = AdcCore(A, v);
        }
        byte AdcCore(byte a, byte v)
        {
            int c = P & FC;
            if ((P & FD) != 0)
            {
                int lo = (a & 0x0F) + (v & 0x0F) + c;
                int hi = (a >> 4) + (v >> 4);
                if (lo > 9) { lo -= 10; hi++; }
                bool carry = false;
                if (hi > 9) { hi -= 10; carry = true; }
                byte r = (byte)((hi << 4) | lo);
                P = (byte)((P & ~(FC | FN | FZ | FV)) | (carry ? FC : 0) | (r & FN) | (r == 0 ? FZ : 0));
                Cycles += 1;
                return r;
            }
            else
            {
                int sum = a + v + c;
                byte r = (byte)sum;
                P = (byte)((P & ~(FC | FN | FZ | FV)) | (sum > 0xFF ? FC : 0) | (r & FN) | (r == 0 ? FZ : 0)
                    | ((((a ^ r) & (v ^ r)) & 0x80) != 0 ? FV : 0));
                return r;
            }
        }
        void Sbc(byte v)
        {
            int c = (P & FC) != 0 ? 0 : 1;
            if ((P & FD) != 0)
            {
                int lo = (A & 0x0F) - (v & 0x0F) - c;
                int hi = (A >> 4) - (v >> 4);
                if (lo < 0) { lo += 10; hi--; }
                bool carry = true;
                if (hi < 0) { hi += 10; carry = false; }
                byte r = (byte)((hi << 4) | lo);
                P = (byte)((P & ~(FC | FN | FZ | FV)) | (carry ? FC : 0) | (r & FN) | (r == 0 ? FZ : 0));
                A = r; Cycles += 1;
            }
            else
            {
                int diff = A - v - c;
                byte r = (byte)diff;
                P = (byte)((P & ~(FC | FN | FZ | FV)) | (diff >= 0 ? FC : 0) | (r & FN) | (r == 0 ? FZ : 0)
                    | ((((A ^ v) & (A ^ r)) & 0x80) != 0 ? FV : 0));
                A = r;
            }
        }
        void And(byte v) { if (tFlag) { int za = Zp(X); byte r = (byte)(Rd(za) & v); Wr(za, r); SetNZ(r); Cycles += 3; } else { A &= v; SetNZ(A); } }
        void Ora(byte v) { if (tFlag) { int za = Zp(X); byte r = (byte)(Rd(za) | v); Wr(za, r); SetNZ(r); Cycles += 3; } else { A |= v; SetNZ(A); } }
        void Eor(byte v) { if (tFlag) { int za = Zp(X); byte r = (byte)(Rd(za) ^ v); Wr(za, r); SetNZ(r); Cycles += 3; } else { A ^= v; SetNZ(A); } }
        void Cmp(byte r, byte v) { int d = r - v; P = (byte)((P & ~(FC | FN | FZ)) | (d >= 0 ? FC : 0) | (d & FN) | ((d & 0xFF) == 0 ? FZ : 0)); }
        void Bit(byte v) { P = (byte)((P & ~(FN | FV | FZ)) | (v & (FN | FV)) | ((A & v) == 0 ? FZ : 0)); }
        byte Asl(byte v) { P = (byte)((P & ~FC) | (v >> 7)); v <<= 1; SetNZ(v); return v; }
        byte Lsr(byte v) { P = (byte)((P & ~FC) | (v & 1)); v >>= 1; SetNZ(v); return v; }
        byte Rol(byte v) { int c = P & FC; P = (byte)((P & ~FC) | (v >> 7)); v = (byte)((v << 1) | c); SetNZ(v); return v; }
        byte Ror(byte v) { int c = P & FC; P = (byte)((P & ~FC) | (v & 1)); v = (byte)((v >> 1) | (c << 7)); SetNZ(v); return v; }
        void Branch(bool cond) { sbyte off = (sbyte)Fetch(); if (cond) { PC = (ushort)(PC + off); Cycles += 2; } }
        void Tsb(int a) { byte v = Rd(a); P = (byte)((P & ~FZ) | ((A & v) == 0 ? FZ : 0)); Wr(a, (byte)(v | A)); }
        void Trb(int a) { byte v = Rd(a); P = (byte)((P & ~FZ) | ((A & v) == 0 ? FZ : 0)); Wr(a, (byte)(v & ~A)); }
        void Tst(byte imm, int a) { byte v = Rd(a); P = (byte)((P & ~(FN | FV | FZ)) | (v & (FN | FV)) | ((imm & v) == 0 ? FZ : 0)); }
        void BbrBbs(int bit, bool set)
        {
            byte v = Rd(AddrZp()); sbyte off = (sbyte)Fetch();
            bool b = ((v >> bit) & 1) != 0;
            if (b == set) { PC = (ushort)(PC + off); Cycles += 2; }
        }
        void Rmb(int bit) { int a = AddrZp(); Wr(a, (byte)(Rd(a) & ~(1 << bit))); }
        void Smb(int bit) { int a = AddrZp(); Wr(a, (byte)(Rd(a) | (1 << bit))); }

        public void Interrupt(ushort vector)
        {
            Push((byte)(PC >> 8)); Push((byte)PC); Push((byte)(P & ~FB));
            P = (byte)((P | FI) & ~(FD | FT)); IrqInhibit = true;
            PC = Rd16(vector); Cycles += 8;
        }
        // I flag as seen by the interrupt logic: CLI/SEI/PLP take effect one instruction late
        public bool IrqInhibit = true;

        static readonly byte[] CycleTable = BuildCycleTable();
        static byte[] BuildCycleTable()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++) t[i] = 2;
            byte[] abs = { 0x0C,0x0D,0x0E,0x1C,0x1E,0x2C,0x2D,0x2E,0x3C,0x3E,0x4D,0x4E,0x5D,0x5E,0x6D,0x6E,0x7D,0x7E,
                0x8C,0x8D,0x8E,0x99,0x9C,0x9D,0x9E,0xAC,0xAD,0xAE,0xBC,0xBD,0xBE,0xCC,0xCD,0xCE,0xDD,0xDE,0xEC,0xED,0xEE,
                0xFD,0xFE,0x19,0x39,0x59,0x79,0xB9,0xD9,0xF9 };
            foreach (var o in abs) t[o] = 5;
            byte[] zp = { 0x04,0x05,0x06,0x14,0x15,0x16,0x24,0x25,0x26,0x34,0x35,0x36,0x45,0x46,0x55,0x56,0x64,0x65,0x66,
                0x74,0x75,0x76,0x84,0x85,0x86,0x94,0x95,0x96,0xA4,0xA5,0xA6,0xB4,0xB5,0xB6,0xC4,0xC5,0xC6,0xD5,0xD6,0xE4,
                0xE5,0xE6,0xF5,0xF6 };
            foreach (var o in zp) t[o] = 4;
            byte[] rmw = { 0x06,0x16,0x26,0x36,0x46,0x56,0x66,0x76,0xC6,0xD6,0xE6,0xF6,0x04,0x14 };
            foreach (var o in rmw) t[o] = 6;
            byte[] rmwa = { 0x0E,0x1E,0x2E,0x3E,0x4E,0x5E,0x6E,0x7E,0xCE,0xDE,0xEE,0xFE,0x0C,0x1C };
            foreach (var o in rmwa) t[o] = 7;
            byte[] ind = { 0x01,0x11,0x12,0x21,0x31,0x32,0x41,0x51,0x52,0x61,0x71,0x72,0x81,0x91,0x92,0xA1,0xB1,0xB2,
                0xC1,0xD1,0xD2,0xE1,0xF1,0xF2 };
            foreach (var o in ind) t[o] = 7;
            byte[] c7 = { 0x00,0x20,0x6C,0x7C,0x40,0x60,0x44 }; foreach (var o in c7) t[o] = 7;
            t[0x4C] = 4; t[0x08] = 3; t[0x28] = 4; t[0x48] = 3; t[0x68] = 4; t[0x5A] = 3; t[0x7A] = 4; t[0xDA] = 3; t[0xFA] = 4;
            byte[] c4 = { 0x03,0x13,0x23,0x43,0x53,0x54,0xD4,0x02,0x22,0x42,0x62,0x82,0xC2 }; foreach (var o in c4) t[o] = 3;
            byte[] bb = { 0x0F,0x1F,0x2F,0x3F,0x4F,0x5F,0x6F,0x7F,0x8F,0x9F,0xAF,0xBF,0xCF,0xDF,0xEF,0xFF,
                0x07,0x17,0x27,0x37,0x47,0x57,0x67,0x77,0x87,0x97,0xA7,0xB7,0xC7,0xD7,0xE7,0xF7 };
            foreach (var o in bb) t[o] = 6;
            byte[] tst = { 0x83,0x93,0xA3,0xB3 }; foreach (var o in tst) t[o] = 7;
            return t;
        }

        // Execute one instruction; returns elapsed cycles at the 7.16 MHz clock
        public int Step()
        {
            long start = Cycles;
            byte oldP = P;
            byte op = Fetch();
            Cycles += CycleTable[op];
            tFlag = setPending; setPending = false;
            Exec(op);
            tFlag = false;
            if (op == 0xF4) setPending = true; // SET
            IrqInhibit = (((op == 0x58 || op == 0x78 || op == 0x28) ? oldP : P) & FI) != 0;
            int c = (int)(Cycles - start);
            if (!HighSpeed) { Cycles += c * 3; c *= 4; }
            return c;
        }

        void Exec(byte op)
        {
            int a; byte v;
            switch (op)
            {
                case 0x00: PC++; Push((byte)(PC >> 8)); Push((byte)PC); Push((byte)(P | FB)); P = (byte)((P | FI) & ~(FD | FT)); PC = Rd16(0xFFF6); break; // BRK
                case 0x01: Ora(Rd(AddrIndX())); break;
                case 0x02: { byte tmp = X; X = Y; Y = tmp; } break; // SXY
                case 0x03: m.Vdc.WriteSt0(Fetch()); break;
                case 0x04: Tsb(AddrZp()); break;
                case 0x05: Ora(Rd(AddrZp())); break;
                case 0x06: a = AddrZp(); Wr(a, Asl(Rd(a))); break;
                case 0x07: Rmb(0); break;
                case 0x08: Push((byte)(P | FB)); break;
                case 0x09: Ora(Fetch()); break;
                case 0x0A: A = Asl(A); break;
                case 0x0C: Tsb(AddrAbs()); break;
                case 0x0D: Ora(Rd(AddrAbs())); break;
                case 0x0E: a = AddrAbs(); Wr(a, Asl(Rd(a))); break;
                case 0x0F: BbrBbs(0, false); break;
                case 0x10: Branch((P & FN) == 0); break;
                case 0x11: Ora(Rd(AddrIndY())); break;
                case 0x12: Ora(Rd(AddrInd())); break;
                case 0x13: m.Vdc.WriteSt1(Fetch()); break;
                case 0x14: Trb(AddrZp()); break;
                case 0x15: Ora(Rd(AddrZpX())); break;
                case 0x16: a = AddrZpX(); Wr(a, Asl(Rd(a))); break;
                case 0x17: Rmb(1); break;
                case 0x18: P &= unchecked((byte)~FC); break;
                case 0x19: Ora(Rd(AddrAbsY())); break;
                case 0x1A: A++; SetNZ(A); break;
                case 0x1C: Trb(AddrAbs()); break;
                case 0x1D: Ora(Rd(AddrAbsX())); break;
                case 0x1E: a = AddrAbsX(); Wr(a, Asl(Rd(a))); break;
                case 0x1F: BbrBbs(1, false); break;
                case 0x20: { ushort t = Fetch16(); PC--; Push((byte)(PC >> 8)); Push((byte)PC); PC = t; } break;
                case 0x21: And(Rd(AddrIndX())); break;
                case 0x22: { byte tmp = A; A = X; X = tmp; } break; // SAX
                case 0x23: m.Vdc.WriteSt2(Fetch()); break;
                case 0x24: Bit(Rd(AddrZp())); break;
                case 0x25: And(Rd(AddrZp())); break;
                case 0x26: a = AddrZp(); Wr(a, Rol(Rd(a))); break;
                case 0x27: Rmb(2); break;
                case 0x28: P = (byte)(Pop() | FB); break;
                case 0x29: And(Fetch()); break;
                case 0x2A: A = Rol(A); break;
                case 0x2C: Bit(Rd(AddrAbs())); break;
                case 0x2D: And(Rd(AddrAbs())); break;
                case 0x2E: a = AddrAbs(); Wr(a, Rol(Rd(a))); break;
                case 0x2F: BbrBbs(2, false); break;
                case 0x30: Branch((P & FN) != 0); break;
                case 0x31: And(Rd(AddrIndY())); break;
                case 0x32: And(Rd(AddrInd())); break;
                case 0x34: Bit(Rd(AddrZpX())); break;
                case 0x35: And(Rd(AddrZpX())); break;
                case 0x36: a = AddrZpX(); Wr(a, Rol(Rd(a))); break;
                case 0x37: Rmb(3); break;
                case 0x38: P |= FC; break;
                case 0x39: And(Rd(AddrAbsY())); break;
                case 0x3A: A--; SetNZ(A); break;
                case 0x3C: Bit(Rd(AddrAbsX())); break;
                case 0x3D: And(Rd(AddrAbsX())); break;
                case 0x3E: a = AddrAbsX(); Wr(a, Rol(Rd(a))); break;
                case 0x3F: BbrBbs(3, false); break;
                case 0x40: P = (byte)(Pop() | FB); PC = (ushort)(Pop() | (Pop() << 8)); break; // RTI
                case 0x41: Eor(Rd(AddrIndX())); break;
                case 0x42: { byte tmp = A; A = Y; Y = tmp; } break; // SAY
                case 0x43: { byte msk = Fetch(); for (int i = 0; i < 8; i++) if ((msk & (1 << i)) != 0) { A = m.Mpr[i]; break; } } break; // TMA
                case 0x44: { sbyte off = (sbyte)Fetch(); PC--; Push((byte)(PC >> 8)); Push((byte)PC); PC = (ushort)(PC + 1 + off); } break; // BSR
                case 0x45: Eor(Rd(AddrZp())); break;
                case 0x46: a = AddrZp(); Wr(a, Lsr(Rd(a))); break;
                case 0x47: Rmb(4); break;
                case 0x48: Push(A); break;
                case 0x49: Eor(Fetch()); break;
                case 0x4A: A = Lsr(A); break;
                case 0x4C: PC = Fetch16(); break;
                case 0x4D: Eor(Rd(AddrAbs())); break;
                case 0x4E: a = AddrAbs(); Wr(a, Lsr(Rd(a))); break;
                case 0x4F: BbrBbs(4, false); break;
                case 0x50: Branch((P & FV) == 0); break;
                case 0x51: Eor(Rd(AddrIndY())); break;
                case 0x52: Eor(Rd(AddrInd())); break;
                case 0x53: { byte msk = Fetch(); for (int i = 0; i < 8; i++) if ((msk & (1 << i)) != 0) m.Mpr[i] = A; } break; // TAM
                case 0x54: HighSpeed = false; break; // CSL
                case 0x55: Eor(Rd(AddrZpX())); break;
                case 0x56: a = AddrZpX(); Wr(a, Lsr(Rd(a))); break;
                case 0x57: Rmb(5); break;
                case 0x58: P &= unchecked((byte)~FI); break;
                case 0x59: Eor(Rd(AddrAbsY())); break;
                case 0x5A: Push(Y); break;
                case 0x5D: Eor(Rd(AddrAbsX())); break;
                case 0x5E: a = AddrAbsX(); Wr(a, Lsr(Rd(a))); break;
                case 0x5F: BbrBbs(5, false); break;
                case 0x60: PC = (ushort)(Pop() | (Pop() << 8)); PC++; break; // RTS
                case 0x61: Adc(Rd(AddrIndX())); break;
                case 0x62: A = 0; break; // CLA
                case 0x64: Wr(AddrZp(), 0); break;
                case 0x65: Adc(Rd(AddrZp())); break;
                case 0x66: a = AddrZp(); Wr(a, Ror(Rd(a))); break;
                case 0x67: Rmb(6); break;
                case 0x68: A = Pop(); SetNZ(A); break;
                case 0x69: Adc(Fetch()); break;
                case 0x6A: A = Ror(A); break;
                case 0x6C: PC = Rd16(Fetch16()); break;
                case 0x6D: Adc(Rd(AddrAbs())); break;
                case 0x6E: a = AddrAbs(); Wr(a, Ror(Rd(a))); break;
                case 0x6F: BbrBbs(6, false); break;
                case 0x70: Branch((P & FV) != 0); break;
                case 0x71: Adc(Rd(AddrIndY())); break;
                case 0x72: Adc(Rd(AddrInd())); break;
                case 0x73: BlockTransfer(0); break; // TII
                case 0x74: Wr(AddrZpX(), 0); break;
                case 0x75: Adc(Rd(AddrZpX())); break;
                case 0x76: a = AddrZpX(); Wr(a, Ror(Rd(a))); break;
                case 0x77: Rmb(7); break;
                case 0x78: P |= FI; break;
                case 0x79: Adc(Rd(AddrAbsY())); break;
                case 0x7A: Y = Pop(); SetNZ(Y); break;
                case 0x7C: PC = Rd16((ushort)(Fetch16() + X)); break;
                case 0x7D: Adc(Rd(AddrAbsX())); break;
                case 0x7E: a = AddrAbsX(); Wr(a, Ror(Rd(a))); break;
                case 0x7F: BbrBbs(7, false); break;
                case 0x80: Branch(true); break;
                case 0x81: Wr(AddrIndX(), A); break;
                case 0x82: X = 0; break; // CLX
                case 0x83: v = Fetch(); Tst(v, AddrZp()); break;
                case 0x84: Wr(AddrZp(), Y); break;
                case 0x85: Wr(AddrZp(), A); break;
                case 0x86: Wr(AddrZp(), X); break;
                case 0x87: Smb(0); break;
                case 0x88: Y--; SetNZ(Y); break;
                case 0x89: v = Fetch(); P = (byte)((P & ~FZ) | ((A & v) == 0 ? FZ : 0)); break;
                case 0x8A: A = X; SetNZ(A); break;
                case 0x8C: Wr(AddrAbs(), Y); break;
                case 0x8D: Wr(AddrAbs(), A); break;
                case 0x8E: Wr(AddrAbs(), X); break;
                case 0x8F: BbrBbs(0, true); break;
                case 0x90: Branch((P & FC) == 0); break;
                case 0x91: Wr(AddrIndY(), A); break;
                case 0x92: Wr(AddrInd(), A); break;
                case 0x93: v = Fetch(); Tst(v, AddrAbs()); break;
                case 0x94: Wr(AddrZpX(), Y); break;
                case 0x95: Wr(AddrZpX(), A); break;
                case 0x96: Wr(AddrZpY(), X); break;
                case 0x97: Smb(1); break;
                case 0x98: A = Y; SetNZ(A); break;
                case 0x99: Wr(AddrAbsY(), A); break;
                case 0x9A: S = X; break;
                case 0x9C: Wr(AddrAbs(), 0); break;
                case 0x9D: Wr(AddrAbsX(), A); break;
                case 0x9E: Wr(AddrAbsX(), 0); break;
                case 0x9F: BbrBbs(1, true); break;
                case 0xA0: Y = Fetch(); SetNZ(Y); break;
                case 0xA1: A = Rd(AddrIndX()); SetNZ(A); break;
                case 0xA2: X = Fetch(); SetNZ(X); break;
                case 0xA3: v = Fetch(); Tst(v, AddrZpX()); break;
                case 0xA4: Y = Rd(AddrZp()); SetNZ(Y); break;
                case 0xA5: A = Rd(AddrZp()); SetNZ(A); break;
                case 0xA6: X = Rd(AddrZp()); SetNZ(X); break;
                case 0xA7: Smb(2); break;
                case 0xA8: Y = A; SetNZ(Y); break;
                case 0xA9: A = Fetch(); SetNZ(A); break;
                case 0xAA: X = A; SetNZ(X); break;
                case 0xAC: Y = Rd(AddrAbs()); SetNZ(Y); break;
                case 0xAD: A = Rd(AddrAbs()); SetNZ(A); break;
                case 0xAE: X = Rd(AddrAbs()); SetNZ(X); break;
                case 0xAF: BbrBbs(2, true); break;
                case 0xB0: Branch((P & FC) != 0); break;
                case 0xB1: A = Rd(AddrIndY()); SetNZ(A); break;
                case 0xB2: A = Rd(AddrInd()); SetNZ(A); break;
                case 0xB3: v = Fetch(); Tst(v, AddrAbsX()); break;
                case 0xB4: Y = Rd(AddrZpX()); SetNZ(Y); break;
                case 0xB5: A = Rd(AddrZpX()); SetNZ(A); break;
                case 0xB6: X = Rd(AddrZpY()); SetNZ(X); break;
                case 0xB7: Smb(3); break;
                case 0xB8: P &= unchecked((byte)~FV); break;
                case 0xB9: A = Rd(AddrAbsY()); SetNZ(A); break;
                case 0xBA: X = S; SetNZ(X); break;
                case 0xBC: Y = Rd(AddrAbsX()); SetNZ(Y); break;
                case 0xBD: A = Rd(AddrAbsX()); SetNZ(A); break;
                case 0xBE: X = Rd(AddrAbsY()); SetNZ(X); break;
                case 0xBF: BbrBbs(3, true); break;
                case 0xC0: Cmp(Y, Fetch()); break;
                case 0xC1: Cmp(A, Rd(AddrIndX())); break;
                case 0xC2: Y = 0; break; // CLY
                case 0xC3: BlockTransfer(1); break; // TDD
                case 0xC4: Cmp(Y, Rd(AddrZp())); break;
                case 0xC5: Cmp(A, Rd(AddrZp())); break;
                case 0xC6: a = AddrZp(); v = (byte)(Rd(a) - 1); Wr(a, v); SetNZ(v); break;
                case 0xC7: Smb(4); break;
                case 0xC8: Y++; SetNZ(Y); break;
                case 0xC9: Cmp(A, Fetch()); break;
                case 0xCA: X--; SetNZ(X); break;
                case 0xCC: Cmp(Y, Rd(AddrAbs())); break;
                case 0xCD: Cmp(A, Rd(AddrAbs())); break;
                case 0xCE: a = AddrAbs(); v = (byte)(Rd(a) - 1); Wr(a, v); SetNZ(v); break;
                case 0xCF: BbrBbs(4, true); break;
                case 0xD0: Branch((P & FZ) == 0); break;
                case 0xD1: Cmp(A, Rd(AddrIndY())); break;
                case 0xD2: Cmp(A, Rd(AddrInd())); break;
                case 0xD3: BlockTransfer(2); break; // TIN
                case 0xD4: HighSpeed = true; break; // CSH
                case 0xD5: Cmp(A, Rd(AddrZpX())); break;
                case 0xD6: a = AddrZpX(); v = (byte)(Rd(a) - 1); Wr(a, v); SetNZ(v); break;
                case 0xD7: Smb(5); break;
                case 0xD8: P &= unchecked((byte)~FD); break;
                case 0xD9: Cmp(A, Rd(AddrAbsY())); break;
                case 0xDA: Push(X); break;
                case 0xDD: Cmp(A, Rd(AddrAbsX())); break;
                case 0xDE: a = AddrAbsX(); v = (byte)(Rd(a) - 1); Wr(a, v); SetNZ(v); break;
                case 0xDF: BbrBbs(5, true); break;
                case 0xE0: Cmp(X, Fetch()); break;
                case 0xE1: Sbc(Rd(AddrIndX())); break;
                case 0xE3: BlockTransfer(3); break; // TIA
                case 0xE4: Cmp(X, Rd(AddrZp())); break;
                case 0xE5: Sbc(Rd(AddrZp())); break;
                case 0xE6: a = AddrZp(); v = (byte)(Rd(a) + 1); Wr(a, v); SetNZ(v); break;
                case 0xE7: Smb(6); break;
                case 0xE8: X++; SetNZ(X); break;
                case 0xE9: Sbc(Fetch()); break;
                case 0xEA: break;
                case 0xEC: Cmp(X, Rd(AddrAbs())); break;
                case 0xED: Sbc(Rd(AddrAbs())); break;
                case 0xEE: a = AddrAbs(); v = (byte)(Rd(a) + 1); Wr(a, v); SetNZ(v); break;
                case 0xEF: BbrBbs(6, true); break;
                case 0xF0: Branch((P & FZ) != 0); break;
                case 0xF1: Sbc(Rd(AddrIndY())); break;
                case 0xF2: Sbc(Rd(AddrInd())); break;
                case 0xF3: BlockTransfer(4); break; // TAI
                case 0xF4: break; // SET (handled in Step)
                case 0xF5: Sbc(Rd(AddrZpX())); break;
                case 0xF6: a = AddrZpX(); v = (byte)(Rd(a) + 1); Wr(a, v); SetNZ(v); break;
                case 0xF7: Smb(7); break;
                case 0xF8: P |= FD; break;
                case 0xF9: Sbc(Rd(AddrAbsY())); break;
                case 0xFA: X = Pop(); SetNZ(X); break;
                case 0xFD: Sbc(Rd(AddrAbsX())); break;
                case 0xFE: a = AddrAbsX(); v = (byte)(Rd(a) + 1); Wr(a, v); SetNZ(v); break;
                case 0xFF: BbrBbs(7, true); break;
                default: break; // undefined opcodes act as NOP
            }
        }

        // 0=TII 1=TDD 2=TIN 3=TIA 4=TAI
        void BlockTransfer(int kind)
        {
            ushort src = Fetch16(), dst = Fetch16(), len = Fetch16();
            int n = len == 0 ? 0x10000 : len;
            Push(Y); Push(A); Push(X);
            bool alt = false;
            for (int i = 0; i < n; i++)
            {
                byte b = m.Read(src); m.Write(dst, b);
                switch (kind)
                {
                    case 0: src++; dst++; break;
                    case 1: src--; dst--; break;
                    case 2: src++; break;
                    case 3: src++; dst = (ushort)(alt ? dst - 1 : dst + 1); alt = !alt; break;
                    case 4: dst++; src = (ushort)(alt ? src - 1 : src + 1); alt = !alt; break;
                }
            }
            X = Pop(); A = Pop(); Y = Pop();
            Cycles += 17 + 6 * n;
        }
    }
}
