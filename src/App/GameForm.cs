using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AlienCrush.Core;

namespace AlienCrush.App
{
    // Game window: runs the emulation on a worker thread, draws frames, handles input/audio
    sealed class GameForm : Form
    {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        const int Slots = 5, FastFactor = 4;
        const string HelpText =
@"方向キー: 十字キー    X / L: I ボタン
Z / K: II ボタン    Enter: RUN    Space: SELECT
D: I 連射    S: II 連射    T: 連射速度切替
F1-F4: 表示倍率    A: 4:3 補正    F11: フルスクリーン
F5: ステートセーブ    F7: ロード    F6: スロット切替
F9: リセット    F12: スクリーンショット
Tab (押している間): 早送り    M: ミュート
P: 一時停止    H: この一覧    Esc: 終了
2P: テンキー 8 4 6 2=十字  .=I  0=II  +=RUN  -=SELECT
2P パッド: 2 台目のコントローラ (F8: 1 台目を 2P にする)
パッド: 十字/スティック  B/RB/RT=I  A=II  Y=I 連射  X=II 連射  LB/LT=左";
        const string SixHelp = @"
6 ボタン: C=III  S=IV  D=V  F=VI
パッド(6B): X Y RB=IV V VI  A B RT=III II I";
        readonly Machine m;
        readonly string title;       // game name: window caption, save state and screenshot file names
        readonly object emuLock = new object();   // guards the Machine: frame step vs. save/load/reset from the UI thread
        Thread worker; volatile bool running;
        readonly object frameLock = new object();
        int[] pixels = new int[Vdc.MaxW * Vdc.MaxH];
        int frameW = 256, frameH = 240;
        int scale = 3;
        bool aspect;                 // true: stretch to 4:3 like a real CRT display
        bool fullscreen;
        int pad, pad2;               // keyboard state of player 1 / player 2
        volatile bool padSwap;       // true: the first controller plays as player 2 (keyboard vs. one controller)
        readonly HashSet<Keys> held = new HashSet<Keys>();   // suppresses key auto-repeat for the hotkeys
        readonly Gamepad gamepad = new Gamepad(0), gamepad2 = new Gamepad(1);
        WaveOut audio;
        readonly short[] audioBuf = new short[Psg.SampleRate / 5 * 2];
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        volatile bool paused, fast, muted;
        bool autoPaused;             // paused because the window lost focus
        bool showHelp;
        int slot = 1;
        volatile int turboRate = Turbo.DefaultRate;
        string osd; DateTime osdUntil;
        int fpsCount; DateTime fpsTime = DateTime.Now; string fpsText = "";

        public GameForm(byte[] rom, bool aspectCorrection, string gameTitle)
        {
            m = new Machine(RomPackage.Normalize(rom));
            aspect = aspectCorrection;
            title = string.IsNullOrEmpty(gameTitle) ? "PC Engine" : gameTitle;
            Text = title;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            ApplyScale();
            timer.Interval = 15; timer.Tick += (s, e) => Invalidate();
        }

        Size TargetSize()
        {
            int h = frameH * scale;
            int w = aspect ? (int)Math.Round(h * 4.0 / 3.0) : frameW * scale;
            return new Size(w, h);
        }
        void ApplyScale() { if (!fullscreen) ClientSize = TargetSize(); }

        // Largest centred rectangle for the picture in fullscreen (whole-number zoom unless 4:3 correction is on)
        Rectangle FitRect(Size cs, int w, int h)
        {
            int dw, dh;
            if (aspect)
            {
                dh = cs.Height; dw = (int)Math.Round(dh * 4.0 / 3.0);
                if (dw > cs.Width) { dw = cs.Width; dh = (int)Math.Round(dw * 3.0 / 4.0); }
            }
            else
            {
                int k = Math.Max(1, Math.Min(cs.Width / w, cs.Height / h));
                dw = w * k; dh = h * k;
            }
            return new Rectangle((cs.Width - dw) / 2, (cs.Height - dh) / 2, dw, dh);
        }

        void ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            if (fullscreen) { FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; Cursor.Hide(); }
            else { WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.FixedSingle; Cursor.Show(); ApplyScale(); CenterToScreen(); }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            timeBeginPeriod(1);
            audio = new WaveOut(Psg.SampleRate, 512, 6);
            running = true;
            worker = new Thread(Loop) { IsBackground = true, Name = "emu" };
            worker.Start();
            timer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            running = false;
            if (worker != null) worker.Join(1000);
            if (audio != null) audio.Dispose();
            timeEndPeriod(1);
            if (fullscreen) Cursor.Show();
            base.OnFormClosing(e);
        }

        // Losing focus drops all held keys and pauses until the window is active again
        protected override void OnDeactivate(EventArgs e)
        {
            pad = 0; pad2 = 0; fast = false; held.Clear();
            if (!paused) { paused = true; autoPaused = true; }
            base.OnDeactivate(e);
        }
        protected override void OnActivated(EventArgs e)
        {
            if (autoPaused) { autoPaused = false; paused = false; }
            base.OnActivated(e);
        }

        void Loop()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double next = 0;
            while (running)
            {
                if (paused) { Thread.Sleep(20); next = sw.Elapsed.TotalMilliseconds; continue; }
                int gp = gamepad.Poll(m.SixButton), gp2 = gamepad2.Poll(m.SixButton);
                bool swap = padSwap;
                lock (emuLock)
                {
                    m.PadState = Turbo.Apply(pad | (swap ? 0 : gp), m.FrameCount, Turbo.Periods[turboRate]);
                    m.PadState2 = Turbo.Apply(pad2 | (swap ? gp : gp2), m.FrameCount, Turbo.Periods[turboRate]);
                    m.RunFrame();
                }
                lock (frameLock)
                {
                    frameW = m.Vdc.FrameWidth; frameH = m.Vdc.FrameHeight;
                    m.Vdc.Blit(pixels);
                }
                int n = m.Psg.Drain(audioBuf);
                bool ff = fast;
                if (!ff && audio != null && audio.Ok)
                {
                    if (muted) Array.Clear(audioBuf, 0, n);
                    audio.Write(audioBuf, n);   // blocks: audio clock paces emulation
                    next = sw.Elapsed.TotalMilliseconds;
                }
                else
                {
                    // no audio device, or fast-forward (sound is dropped): pace with the stopwatch
                    next += 1000.0 / (ff ? 60.0 * FastFactor : 60.0);
                    double wait = next - sw.Elapsed.TotalMilliseconds;
                    if (wait > 0) Thread.Sleep((int)wait); else if (wait < -200) next = sw.Elapsed.TotalMilliseconds;
                }
                fpsCount++;
            }
        }

        // ---- picture scaling: done here instead of GDI+ (its smooth stretch costs most of a CPU core at 60 fps) ----
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [DllImport("gdi32.dll")] static extern int SetDIBitsToDevice(IntPtr hdc, int x, int y, int w, int h, int sx, int sy, int startScan, int scanLines, int[] bits, ref BITMAPINFOHEADER bmi, int usage);
        int[] scaled = new int[0];
        int[] colX = new int[0], colF = new int[0];   // per output column: left source pixel and blend weight (0..256) of its right neighbour
        int colSrcW, colDstW; bool colSmooth;

        // Stretch the frame to dw x dh: rows are repeated (sharp), columns are blended when smooth is set
        void Scale(int w, int h, int dw, int dh, bool smooth)
        {
            if (scaled.Length != dw * dh) scaled = new int[dw * dh];
            if (colSrcW != w || colDstW != dw || colSmooth != smooth || colX.Length != dw)
            {
                colX = new int[dw]; colF = new int[dw]; colSrcW = w; colDstW = dw; colSmooth = smooth;
                for (int x = 0; x < dw; x++)
                {
                    if (!smooth) { colX[x] = Math.Min(w - 1, x * w / dw); continue; }
                    double sx = (x + 0.5) * w / dw - 0.5;
                    if (sx < 0) sx = 0;
                    int x0 = Math.Min(w - 1, (int)sx);
                    colX[x] = x0; colF[x] = x0 >= w - 1 ? 0 : (int)((sx - x0) * 256);
                }
            }
            int prev = -1;
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min(h - 1, y * h / dh), o = y * dw;
                if (sy == prev) { Buffer.BlockCopy(scaled, (o - dw) * 4, scaled, o * 4, dw * 4); continue; }
                prev = sy;
                int so = sy * w;
                for (int x = 0; x < dw; x++)
                {
                    int a = pixels[so + colX[x]], f = colF[x];
                    if (f != 0)
                    {
                        int b = pixels[so + colX[x] + 1];
                        int rb = (((a & 0xFF00FF) * (256 - f) + (b & 0xFF00FF) * f) >> 8) & 0xFF00FF;
                        int gg = (((a & 0x00FF00) * (256 - f) + (b & 0x00FF00) * f) >> 8) & 0x00FF00;
                        a = rb | gg;
                    }
                    scaled[o + x] = a;
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Rectangle dst;
            lock (frameLock)
            {
                int w = frameW, h = frameH;
                if (fullscreen) { g.Clear(Color.Black); dst = FitRect(ClientSize, w, h); }
                else
                {
                    var ts = TargetSize();
                    if (ClientSize != ts) ClientSize = ts;
                    dst = new Rectangle(0, 0, ts.Width, ts.Height);
                }
                Scale(w, h, dst.Width, dst.Height, aspect);
            }
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = dst.Width, biHeight = -dst.Height, biPlanes = 1, biBitCount = 32 };
            IntPtr hdc = g.GetHdc();
            try { SetDIBitsToDevice(hdc, dst.X, dst.Y, dst.Width, dst.Height, 0, 0, 0, dst.Height, scaled, ref bi, 0); }
            finally { g.ReleaseHdc(hdc); }
            DrawOverlay(g, dst);
            var now = DateTime.Now;
            if ((now - fpsTime).TotalSeconds >= 1)
            {
                fpsText = string.Format("{5}  -  {0} fps  [{1}x{2}]{3}  スロット {4}  (H: 操作一覧)", fpsCount, scale, aspect ? " 4:3" : "", muted ? "  消音" : "", slot, title);
                fpsCount = 0; fpsTime = now; Text = fpsText;
            }
        }

        void Osd(string text) { osd = text; osdUntil = DateTime.Now.AddSeconds(2); }

        void DrawOverlay(Graphics g, Rectangle dst)
        {
            string text = showHelp ? (m.SixButton ? HelpText + SixHelp : HelpText)
                : DateTime.Now < osdUntil ? osd
                : paused ? (autoPaused ? "一時停止中" : "一時停止中 (P で再開)")
                : fast ? "早送り x" + FastFactor : null;
            if (text == null) return;
            using (var font = new Font("Meiryo UI", Math.Max(10f, dst.Height / (showHelp ? 36f : 30f)), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var back = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
            {
                var size = g.MeasureString(text, font);
                var box = new RectangleF(dst.X + 8, dst.Y + 8, size.Width + 12, size.Height + 8);
                g.FillRectangle(back, box);
                g.DrawString(text, font, Brushes.White, box.X + 6, box.Y + 4);
            }
        }

        // ---- save states / screenshots (files live next to the exe) ----
        static string BaseDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        string StatePath(int n) { return Path.Combine(BaseDir, SafeName(title) + ".st" + n); }
        static string SafeName(string s) { foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

        void SaveState()
        {
            try
            {
                byte[] data;
                lock (emuLock) data = m.SaveState();
                File.WriteAllBytes(StatePath(slot), data);
                Osd("スロット " + slot + " にセーブしました");
            }
            catch (Exception ex) { Osd("セーブ失敗: " + ex.Message); }
        }

        void LoadState()
        {
            try
            {
                string path = StatePath(slot);
                if (!File.Exists(path)) { Osd("スロット " + slot + " は空です"); return; }
                byte[] data = File.ReadAllBytes(path);
                bool ok;
                lock (emuLock) ok = m.LoadState(data);
                if (ok) { autoPaused = false; paused = false; Osd("スロット " + slot + " をロードしました"); }
                else Osd("スロット " + slot + " のデータを読み込めません");
            }
            catch (Exception ex) { Osd("ロード失敗: " + ex.Message); }
        }

        void Screenshot()
        {
            try
            {
                string dir = Path.Combine(BaseDir, "screenshots");
                Directory.CreateDirectory(dir);
                string name = SafeName(title) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
                lock (frameLock)
                {
                    using (var shot = new Bitmap(frameW, frameH, PixelFormat.Format32bppRgb))
                    {
                        var bd = shot.LockBits(new Rectangle(0, 0, frameW, frameH), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        for (int y = 0; y < frameH; y++) Marshal.Copy(pixels, y * frameW, bd.Scan0 + y * bd.Stride, frameW);
                        shot.UnlockBits(bd);
                        shot.Save(Path.Combine(dir, name), ImageFormat.Png);
                    }
                }
                Osd("保存: screenshots/" + name);
            }
            catch (Exception ex) { Osd("スクリーンショット失敗: " + ex.Message); }
        }

        // player 2 on the numeric keypad
        static int KeyBit2(Keys k)
        {
            switch (k)
            {
                case Keys.NumPad8: return 0x10;
                case Keys.NumPad6: return 0x20;
                case Keys.NumPad2: case Keys.NumPad5: return 0x40;
                case Keys.NumPad4: return 0x80;
                case Keys.Decimal: return 0x01;               // I
                case Keys.NumPad0: return 0x02;               // II
                case Keys.Subtract: return 0x04;              // SELECT
                case Keys.Add: return 0x08;                   // RUN
                default: return 0;
            }
        }

        int KeyBit(Keys k)
        {
            switch (k)
            {
                case Keys.Up: return 0x10;
                case Keys.Right: return 0x20;
                case Keys.Down: return 0x40;
                case Keys.Left: return 0x80;
                case Keys.X: case Keys.L: return 0x01;        // I
                case Keys.Z: case Keys.K: return 0x02;        // II
                case Keys.Space: case Keys.ShiftKey: return 0x04; // SELECT
                case Keys.Enter: return 0x08;                 // RUN
                case Keys.C: return 0x100;                    // III-VI: 6-button pad only
                case Keys.S: return m.SixButton ? 0x200 : Turbo.II;   // rapid fire on the 2-button pad
                case Keys.D: return m.SixButton ? 0x400 : Turbo.I;
                case Keys.F: return 0x800;
                default: return 0;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right: case Keys.Enter: case Keys.Tab: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            bool repeat = !held.Add(e.KeyCode);
            if (e.Alt && e.KeyCode == Keys.Enter) { if (!repeat) ToggleFullscreen(); e.Handled = e.SuppressKeyPress = true; return; }
            int b = KeyBit(e.KeyCode);
            if (b != 0) { pad |= b; e.Handled = true; return; }
            b = KeyBit2(e.KeyCode);
            if (b != 0) { pad2 |= b; e.Handled = true; return; }
            if (repeat) { base.OnKeyDown(e); return; }
            switch (e.KeyCode)
            {
                case Keys.Escape: if (fullscreen) ToggleFullscreen(); else Close(); break;
                case Keys.P: paused = !paused; autoPaused = false; break;
                case Keys.F1: scale = 1; ApplyScale(); break;
                case Keys.F2: scale = 2; ApplyScale(); break;
                case Keys.F3: scale = 3; ApplyScale(); break;
                case Keys.F4: scale = 4; ApplyScale(); break;
                case Keys.A: aspect = !aspect; ApplyScale(); break;
                case Keys.F5: SaveState(); break;
                case Keys.F6: slot = slot % Slots + 1; Osd("スロット " + slot + (File.Exists(StatePath(slot)) ? "" : " (空)")); break;
                case Keys.F7: LoadState(); break;
                case Keys.F9: lock (emuLock) m.Reset(); Osd("リセット"); break;
                case Keys.F11: ToggleFullscreen(); break;
                case Keys.F12: Screenshot(); break;
                case Keys.Tab: fast = true; break;
                case Keys.M: muted = !muted; Osd(muted ? "ミュート ON" : "ミュート OFF"); break;
                case Keys.H: showHelp = !showHelp; break;
                case Keys.F8: padSwap = !padSwap; Osd(padSwap ? "コントローラ 1 台目 → 2P (1P はキーボード)" : "コントローラ 1 台目 → 1P、2 台目 → 2P"); break;
                case Keys.T: turboRate = (turboRate + 1) % Turbo.Periods.Length; Osd("連射速度: 秒間 " + 60 / Turbo.Periods[turboRate] + " 連射"); break;
            }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            held.Remove(e.KeyCode);
            if (e.KeyCode == Keys.Tab) fast = false;
            int b = KeyBit(e.KeyCode);
            if (b != 0) { pad &= ~b; e.Handled = true; }
            b = KeyBit2(e.KeyCode);
            if (b != 0) { pad2 &= ~b; e.Handled = true; }
            base.OnKeyUp(e);
        }
    }
}
