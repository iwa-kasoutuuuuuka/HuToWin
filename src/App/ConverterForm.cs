using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace AlienCrush.App
{
    // Converter window: pick a ROM, verify it, and write a self-contained Windows exe
    sealed class ConverterForm : Form
    {
        // ROMs that have been checked with this emulator: MD5 -> { title, controls }
        static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]>
        {
            { "0088d1d20861490677edb147d2de60bd", new[] { "Alien Crush (USA)", "方向キー=左フリッパー / X=右フリッパー / Z=プランジャー(長押し)" } },
            { "92fbe89d4c87bf283dbfbeac69a64e6e", new[] { "Devil's Crush (USA)", "方向キー=左フリッパー / X=右フリッパー / Z=プランジャー" } },
            { "fe5b3f899f0ee5994508f259bbb74cfe", new[] { "Final Soldier (Japan) (En)", "方向キー=移動 / Z=ショット / X=速度切替" } },
            { "838e1ae033f797bad4a122a593604a8f", new[] { "Final Soldier - Special Version (Japan) (En)", "方向キー=移動 / Z=ショット / X=速度切替" } },
            { "a09f962fc2940fc12c88322aa6bbed43", new[] { "Final Soldier (World) (Wii U Virtual Console)", "方向キー=移動 / Z=ショット / X=速度切替" } },
            { "b928568fd7258f90b5ac410c7f65c48c", new[] { "Energy (Japan)", "方向キー=移動 / X・Z=ボタン I・II" } },
            { "1fe13078c7aba4ef0777d5a470d56986", new[] { "Dragon Spirit (USA)", "方向キー=移動 / X・Z=ショット (対空 / 対地)" } },
            { "c42ad354e906d5a4741349002b94b9c2", new[] { "Detana!! TwinBee (Japan)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "f98df7740f72fbab06e9252a0160a807", new[] { "Darius Alpha (World)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "73b3ef4925207683f83598288a00b1d3", new[] { "Bonk 3 - Bonk's Big Adventure (USA, Europe)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "f60f3d46b7fab6060c3be9aad2103558", new[] { "Bravoman (USA)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "7822065caf6c6b5cd8f16189915b4e21", new[] { "Bikkuriman World (Japan)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "539eb9333d3e39f597786288872917dc", new[] { "Bari Bari Densetsu (Japan)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "ac7e0730b10c3cf1afdc675c808572b1", new[] { "R-Type I (Japan) (En)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "1b00aea45aa694e0ae58a7d35aede8ad", new[] { "R-Type II (Japan) (En)", "方向キー=十字キー / X=I ボタン / Z=II ボタン" } },
            { "0d9135be3267876bfec7f588abeda5bb", new[] { "Street Fighter II' - Champion Edition (Japan)", "方向キー=移動 / S D F=パンチ弱中強 (IV V VI) / C Z X=キック弱中強 (III II I)" } },
        };
        const string GenericKeys = "方向キー=十字キー / X=I ボタン / Z=II ボタン";

        readonly TextBox pathBox = new TextBox();
        readonly Label info = new Label();
        readonly Button browse = new Button(), convert = new Button(), play = new Button(), batch = new Button();
        readonly CheckBox aspectBox = new CheckBox();
        byte[] rom;
        string romName = "";         // ROM file name without extension
        string keys = GenericKeys;

        public ConverterForm()
        {
            Text = "PC エンジン ROM → Windows 変換ツール";
            ClientSize = new Size(560, 308);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Yu Gothic UI", 10f);
            AllowDrop = true;

            var l = new Label { Text = "PC エンジン / TurboGrafx-16 の HuCard ROM (.pce) を選択してください:", Left = 16, Top = 16, Width = 520 };
            pathBox.SetBounds(16, 44, 430, 28); pathBox.ReadOnly = true;
            browse.SetBounds(452, 42, 90, 30); browse.Text = "参照..."; browse.Click += (s, e) => Browse();
            info.SetBounds(16, 84, 526, 70); info.Text = "ROM 未選択";
            aspectBox.SetBounds(16, 156, 526, 26); aspectBox.Text = "アスペクト比補正 (4:3 表示) を初期状態で ON にする  ※ゲーム中は A キーで切替"; aspectBox.Checked = true;
            convert.SetBounds(16, 200, 250, 40); convert.Text = "変換 (Windows用 exe を生成)"; convert.Enabled = false; convert.Click += (s, e) => Convert();
            play.SetBounds(292, 200, 250, 40); play.Text = "テストプレイ"; play.Enabled = false; play.Click += (s, e) => { using (var f = new GameForm(rom, aspectBox.Checked, romName)) f.ShowDialog(this); };
            batch.SetBounds(16, 252, 526, 36); batch.Text = "フォルダ内の ROM をまとめて変換..."; batch.Click += (s, e) => Batch();
            Controls.AddRange(new Control[] { l, pathBox, browse, info, aspectBox, convert, play, batch });
            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => { var f = (string[])e.Data.GetData(DataFormats.FileDrop); if (f.Length > 0) LoadRom(f[0]); };
        }

        void Browse()
        {
            using (var d = new OpenFileDialog { Filter = "PC Engine ROM (*.pce)|*.pce|All files (*.*)|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) LoadRom(d.FileName);
        }

        void LoadRom(string path)
        {
            try
            {
                var raw = File.ReadAllBytes(path);
                rom = RomPackage.Normalize(raw);
                string md5;
                using (var h = MD5.Create()) md5 = BitConverter.ToString(h.ComputeHash(rom)).Replace("-", "").ToLowerInvariant();
                string[] k; bool known = Known.TryGetValue(md5, out k);
                romName = Path.GetFileNameWithoutExtension(path);
                keys = known ? k[1] : GenericKeys;
                pathBox.Text = path;
                info.Text = string.Format("サイズ: {0:N0} bytes ({1} バンク)   MD5: {2}\r\n{3}", rom.Length, rom.Length / 0x2000, md5,
                    known ? "✔ 動作確認済み: " + k[0] : "⚠ 動作未確認の ROM です（HuCard 用の標準的な ROM なら動く可能性があります）。");
                convert.Enabled = play.Enabled = rom.Length >= 0x2000;
            }
            catch (Exception ex) { info.Text = "読み込み失敗: " + ex.Message; rom = null; convert.Enabled = play.Enabled = false; }
        }

        void Convert()
        {
            using (var d = new SaveFileDialog { Filter = "Windows 実行ファイル (*.exe)|*.exe", FileName = romName + ".exe" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    RomPackage.Build(Application.ExecutablePath, rom, d.FileName, aspectBox.Checked ? RomPackage.FlagAspect : 0);
                    MessageBox.Show(this, "生成しました:\r\n" + d.FileName + "\r\n\r\nこの exe 単体で Windows 11 上でゲームが起動します。\r\n操作: " + keys + " / Enter=RUN / Space=SELECT / A=アスペクト比切替", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, "失敗: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        // Converts every .pce in a folder into <folder>\Windows\<rom name>.exe
        void Batch()
        {
            using (var d = new FolderBrowserDialog { Description = "ROM (.pce) が入っているフォルダを選択してください。変換結果はその中の Windows フォルダに出力されます。" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string outDir = Path.Combine(d.SelectedPath, "Windows");
                    int n = RomPackage.BuildAll(d.SelectedPath, outDir, aspectBox.Checked ? RomPackage.FlagAspect : 0);
                    MessageBox.Show(this, n == 0 ? "このフォルダに .pce ファイルはありませんでした。" : n + " 本の ROM を変換しました:\r\n" + outDir, "一括変換", MessageBoxButtons.OK, n == 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show(this, "失敗: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
