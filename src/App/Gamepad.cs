using System;
using System.Runtime.InteropServices;

namespace AlienCrush.App
{
    // XInput controller mapped to the PC Engine pad bits used by Machine.PadState
    sealed class Gamepad
    {
        [StructLayout(LayoutKind.Sequential)]
        struct State { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int GetState(int index, out State st);
        const int Stick = 12000, Trigger = 60;
        readonly int index;          // XInput user index (0 = first controller)
        public Gamepad(int userIndex) { index = userIndex; }
        bool unavailable;            // xinput1_4.dll missing
        int retry;                   // frames to wait before probing a disconnected pad again (probing is slow)

        // six: Street Fighter layout (X Y RB = IV V VI, A B RT = III II I)
        public int Poll(bool six)
        {
            if (unavailable) return 0;
            if (retry > 0) { retry--; return 0; }
            State s;
            try { if (GetState(index, out s) != 0) { retry = 120; return 0; } }
            catch (Exception) { unavailable = true; return 0; }
            int b = s.Buttons, p = 0;
            if ((b & 0x0001) != 0 || s.LY > Stick) p |= 0x10;                    // up
            if ((b & 0x0008) != 0 || s.LX > Stick) p |= 0x20;                    // right
            if ((b & 0x0002) != 0 || s.LY < -Stick) p |= 0x40;                   // down
            if ((b & 0x0004) != 0 || s.LX < -Stick) p |= 0x80;                   // left
            if (six)
            {
                if (s.RT > Trigger) p |= 0x01;                                   // RT: I
                if ((b & 0x2000) != 0) p |= 0x02;                                // B: II
                if ((b & 0x1000) != 0) p |= 0x100;                               // A: III
                if ((b & 0x4000) != 0) p |= 0x200;                               // X: IV
                if ((b & 0x8000) != 0) p |= 0x400;                               // Y: V
                if ((b & 0x0200) != 0) p |= 0x800;                               // RB: VI
            }
            else
            {
                if ((b & 0x0100) != 0 || s.LT > Trigger) p |= 0x80;              // LB / LT: left flipper
                if ((b & 0x2200) != 0 || s.RT > Trigger) p |= 0x01;              // B / RB / RT: I (right flipper)
                if ((b & 0x1000) != 0) p |= 0x02;                                // A: II (plunger)
                if ((b & 0x8000) != 0) p |= Turbo.I;                             // Y: rapid I
                if ((b & 0x4000) != 0) p |= Turbo.II;                            // X: rapid II
            }
            if ((b & 0x0020) != 0) p |= 0x04;                                    // BACK: SELECT
            if ((b & 0x0010) != 0) p |= 0x08;                                    // START: RUN
            return p;
        }
    }
}
