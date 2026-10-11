using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AlienCrush.Core;

namespace AlienCrush.App
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--headless") { Headless.Run(args); return; }
            if (args.Length >= 3 && args[0] == "--packall") { RomPackage.BuildAll(args[1], args[2], Array.IndexOf(args, "--aspect") >= 0 ? 1 : 0); return; }
            if (args.Length >= 3 && args[0] == "--pack") { int fl = Array.IndexOf(args, "--aspect") >= 0 ? 1 : 0; RomPackage.Build(Application.ExecutablePath, RomPackage.Normalize(File.ReadAllBytes(args[1])), args[2], fl); return; }
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int flags;
            byte[] rom = RomPackage.TryExtractFrom(Application.ExecutablePath, out flags);
            // a packed exe is named after its game; a ROM given on the command line uses the ROM's file name
            string title = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            if (rom == null && args.Length >= 1 && File.Exists(args[0]))
            {
                try { rom = File.ReadAllBytes(args[0]); title = Path.GetFileNameWithoutExtension(args[0]); } catch { rom = null; }
            }
            if (rom != null) Application.Run(new GameForm(rom, (flags & RomPackage.FlagAspect) != 0, title));
            else Application.Run(new ConverterForm());
        }
    }

    // Rapid fire: input bits 0x1000 / 0x2000 press I / II automatically while held
    static class Turbo
    {
        public const int I = 0x1000, II = 0x2000;
        public static readonly int[] Periods = { 2, 3, 4, 6 };   // frames per shot: 30 / 20 / 15 / 10 shots a second
        public const int DefaultRate = 2;
        // raw: pad bits plus the rapid-fire bits; returns the pad bits for this frame
        public static int Apply(int raw, long frame, int period)
        {
            int p = raw & 0xFFF;
            if (frame % period < (period + 1) / 2)
            {
                if ((raw & I) != 0) p |= 0x01;
                if ((raw & II) != 0) p |= 0x02;
            }
            return p;
        }
    }

    // Appends the ROM to a copy of this executable:  [exe][rom][flags 4][magic 8][len 4]  (v1 layout had no flags)
    static class RomPackage
    {
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("ACRUSHv2");
        static readonly byte[] MagicV1 = Encoding.ASCII.GetBytes("ACRUSHv1");
        public const int FlagAspect = 1;

        public static byte[] TryExtractFrom(string exePath) { int f; return TryExtractFrom(exePath, out f); }
        public static byte[] TryExtractFrom(string exePath, out int flags)
        {
            flags = 0;
            try
            {
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 12) return null;
                    fs.Seek(-12, SeekOrigin.End);
                    var tail = new byte[12]; fs.Read(tail, 0, 12);
                    bool v2 = true, v1 = true;
                    for (int i = 0; i < 8; i++) { if (tail[i] != Magic[i]) v2 = false; if (tail[i] != MagicV1[i]) v1 = false; }
                    if (!v2 && !v1) return null;
                    int len = BitConverter.ToInt32(tail, 8);
                    if (len <= 0 || len > fs.Length - 12) return null;
                    if (v2) { if (len > fs.Length - 16) return null; fs.Seek(-16, SeekOrigin.End); var fb = new byte[4]; fs.Read(fb, 0, 4); flags = BitConverter.ToInt32(fb, 0); fs.Seek(-16 - len, SeekOrigin.End); }
                    else fs.Seek(-12 - len, SeekOrigin.End);
                    var rom = new byte[len]; int got = 0;
                    while (got < len) { int n = fs.Read(rom, got, len - got); if (n <= 0) break; got += n; }
                    return got == len ? rom : null;
                }
            }
            catch { return null; }
        }

        public static void Build(string exePath, byte[] rom, string outPath, int flags)
        {
            byte[] exe = File.ReadAllBytes(exePath);
            // strip an already-embedded ROM if present
            int oldFlags; byte[] existing = TryExtractFrom(exePath, out oldFlags);
            int exeLen = exe.Length;
            if (existing != null) { bool oldV2 = true; for (int i = 0; i < 8; i++) if (exe[exe.Length - 12 + i] != Magic[i]) oldV2 = false; exeLen = exe.Length - existing.Length - (oldV2 ? 16 : 12); }
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                fs.Write(exe, 0, exeLen);
                fs.Write(rom, 0, rom.Length);
                fs.Write(BitConverter.GetBytes(flags), 0, 4);
                fs.Write(Magic, 0, 8);
                fs.Write(BitConverter.GetBytes(rom.Length), 0, 4);
            }
        }

        // Packs every .pce in romDir into outDir\<rom name>.exe; returns the number of exes written
        public static int BuildAll(string romDir, string outDir, int flags)
        {
            Directory.CreateDirectory(outDir);
            int n = 0;
            foreach (string path in Directory.GetFiles(romDir, "*.pce"))
            {
                byte[] rom = Normalize(File.ReadAllBytes(path));
                if (rom.Length < 0x2000) continue;
                Build(Application.ExecutablePath, rom, Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".exe"), flags);
                n++;
            }
            return n;
        }

        // Strip a 512-byte header if present (size not multiple of 8KB)
        public static byte[] Normalize(byte[] rom)
        {
            if (rom.Length % 0x2000 == 512) { var r = new byte[rom.Length - 512]; Array.Copy(rom, 512, r, 0, r.Length); rom = r; }
            // some US dumps keep the HuCard's reversed data-line order: the reset vector then points below $E000
            if (rom.Length >= 0x2000 && rom[0x1FFF] < 0xE0 && Rev(rom[0x1FFF]) >= 0xE0)
            {
                var r = new byte[rom.Length];
                for (int i = 0; i < r.Length; i++) r[i] = Rev(rom[i]);
                rom = r;
            }
            return rom;
        }
        static byte Rev(byte b)
        {
            int v = 0; for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) v |= 0x80 >> i;
            return (byte)v;
        }
    }

    // --headless rom.pce frames out.png [padmask@from-to ...]   (padmask hex: 1=I 2=II 4=SEL 8=RUN 10=UP 20=RIGHT 40=DOWN 80=LEFT 100-800=III-VI 1000/2000=rapid I/II)
    static class Headless
    {
        public static void Run(string[] args)
        {
            var log = new StringBuilder();
            try
            {
                string romPath = args[1]; int frames = int.Parse(args[2]); string outPng = args[3];
                var inputs = new System.Collections.Generic.List<int[]>();
                int traceFrame = -1, roundTrip = -1; string wavPath = null; var wav = new System.Collections.Generic.List<short>();
                for (int i = 4; i < args.Length; i++)
                {
                    if (args[i].StartsWith("--trace=")) { traceFrame = int.Parse(args[i].Substring(8)); continue; }
                    if (args[i].StartsWith("--wav=")) { wavPath = args[i].Substring(6); continue; }
                    if (args[i].StartsWith("--stateroundtrip=")) { roundTrip = int.Parse(args[i].Substring(17)); continue; }
                    // "p2:" prefix: input for player 2
                    bool second = args[i].StartsWith("p2:");
                    var p = (second ? args[i].Substring(3) : args[i]).Split('@'); var r = p[1].Split('-');
                    inputs.Add(new[] { Convert.ToInt32(p[0], 16), int.Parse(r[0]), int.Parse(r[1]), second ? 1 : 0 });
                }
                byte[] rom = RomPackage.Normalize(File.ReadAllBytes(romPath));
                var m = new Machine(rom);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < frames; f++)
                {
                    if (f == roundTrip)
                    {
                        // save, then continue on a fresh machine restored from that state: output must match an uninterrupted run
                        byte[] st = m.SaveState(); var m2 = new Machine(rom);
                        bool rejects = !m2.LoadState(new byte[st.Length]) && !m2.LoadState(new byte[10]);
                        if (!m2.LoadState(st)) throw new Exception("state round trip failed");
                        m = m2;
                        log.AppendFormat("state round trip at frame {0}: {1} bytes, rejects bad data={2}" + (char)13 + (char)10, f, st.Length, rejects);
                    }
                    int pad = 0;
                    int pad2 = 0;
                    foreach (var inp in inputs) if (f >= inp[1] && f <= inp[2]) { if (inp[3] != 0) pad2 |= inp[0]; else pad |= inp[0]; }
                    m.PadState2 = Turbo.Apply(pad2, f, Turbo.Periods[Turbo.DefaultRate]);
                    m.PadState = Turbo.Apply(pad, f, Turbo.Periods[Turbo.DefaultRate]);
                    m.Trace = (f == traceFrame);
                    m.RunFrame();
                    if (f % 60 == 59 || f == frames - 1)
                        log.AppendFormat("frame {0}: PC={1:X4} MPR={2} CR={3:X4} BXR={4} BYR={5} MWR={6:X} HDR={7:X4} VPR={8:X4} VDW={9:X4} SATB={10:X4} status/line={11} w={12} h={13} psgPending={14}\r\n",
                            f + 1, m.Cpu.PC, Hex(m.Mpr), m.Vdc.R[5], m.Vdc.R[7], m.Vdc.R[8], m.Vdc.R[9], m.Vdc.R[0xB], m.Vdc.R[0xC], m.Vdc.R[0xD], m.Vdc.R[0x13], m.Vdc.Line, m.Vdc.FrameWidth, m.Vdc.FrameHeight, m.Psg.Pending);
                    if (m.Psg.Pending > 0) { var tmp = new short[m.Psg.Pending]; int got = m.Psg.Drain(tmp); if (wavPath != null) for (int k = 0; k < got; k++) wav.Add(tmp[k]); }
                }
                sw.Stop();
                log.AppendFormat("elapsed {0} ms for {1} frames ({2:F1} fps)\r\n", sw.ElapsedMilliseconds, frames, frames * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds));
                int nzPal = 0; for (int i = 0; i < 512; i++) if (m.Vdc.Palette[i] != 0) nzPal++;
                int nzVram = 0; for (int i = 0; i < 0x8000; i++) if (m.Vdc.Vram[i] != 0) nzVram++;
                log.AppendFormat("palette nonzero={0} vram nonzero words={1}\r\n", nzPal, nzVram);
                var sb = new StringBuilder();
                for (int i = 0; i < 16; i++) sb.AppendFormat("{0:X3} ", m.Vdc.Palette[i]);
                log.AppendFormat("pal0: {0}\r\n", sb);
                sb.Length = 0;
                for (int i = 0; i < 8; i++) sb.AppendFormat("[{0:X4} {1:X4} {2:X4} {3:X4}] ", m.Vdc.Sat[i * 4], m.Vdc.Sat[i * 4 + 1], m.Vdc.Sat[i * 4 + 2], m.Vdc.Sat[i * 4 + 3]);
                log.AppendFormat("sat0-7: {0}\r\n", sb);
                sb.Length = 0;
                for (int i = 0; i < 64; i++) if (m.Vdc.Sat[i * 4] != 0 || m.Vdc.Sat[i * 4 + 1] != 0)
                    sb.AppendFormat("#{0}: y={1} x={2} pat={3:X4} attr={4:X4}\r\n", i, (m.Vdc.Sat[i * 4] & 0x3FF) - 64, (m.Vdc.Sat[i * 4 + 1] & 0x3FF) - 32, m.Vdc.Sat[i * 4 + 2], m.Vdc.Sat[i * 4 + 3]);
                log.Append(sb);
                File.WriteAllBytes("ram.bin", m.Ram);
                { var vb = new byte[0x10000]; Buffer.BlockCopy(m.Vdc.Vram, 0, vb, 0, vb.Length); File.WriteAllBytes("vram.bin", vb); }
                if (wavPath != null) WriteWav(wavPath, wav.ToArray());
                if (traceFrame >= 0)
                {
                    var tr = new StringBuilder();
                    for (int i = 0; i < 65536; i++) if (m.TracePcs[i] > 0) tr.AppendFormat("{0:X4} {1}", i, m.TracePcs[i]).Append((char)10);
                    File.WriteAllText("trace.txt", tr.ToString());
                }
                SavePng(m, outPng);
                log.AppendFormat("saved {0} ({1}x{2})\r\n", outPng, m.Vdc.FrameWidth, m.Vdc.FrameHeight);
            }
            catch (Exception ex) { log.Append("ERROR: " + ex); }
            File.WriteAllText("headless.log", log.ToString());
        }
        static void WriteWav(string path, short[] s)
        {
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                int dataLen = s.Length * 2;
                bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + dataLen); bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt ")); bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(Psg.SampleRate); bw.Write(Psg.SampleRate * 4); bw.Write((short)4); bw.Write((short)16);
                bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(dataLen);
                foreach (var v in s) bw.Write(v);
            }
        }
        static string Hex(byte[] b) { var s = new StringBuilder(); foreach (var x in b) s.AppendFormat("{0:X2} ", x); return s.ToString().Trim(); }

        public static void SavePng(Machine m, string path)
        {
            int w = m.Vdc.FrameWidth, h = m.Vdc.FrameHeight;
            var px = new int[w * h]; m.Vdc.Blit(px);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(px, 0, bd.Scan0, px.Length);
                bmp.UnlockBits(bd);
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
