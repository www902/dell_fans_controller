using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace dell_fans_controller
{
    /// <summary>What the main form offers to the GPU control panel.</summary>
    public interface IFanHost
    {
        string IpmiToolPath { get; }
        string[] IpmiCredential();                       // ip, user, password
        void SetFanUiValue(int fanOneBased, int percent); // only moves sliders/labels
        void OnUi(Action action);                         // marshal to the UI thread
        void GpuLog(string message);
    }

    /// <summary>
    /// Adds the "graphics card temperature -> fan speed" feature to the form:
    /// reads the GPU temperatures with nvidia-smi and drives the six fans with
    /// the same ipmitool raw commands the buttons of this program use.
    /// </summary>
    public class GpuFanControl
    {
        private const string Section = "gpu";

        // fan mapping combo items -> meaning
        private static readonly string[] MapItems = new string[]
        {
            "全部(取最高)", "显卡1", "显卡2", "显卡3", "显卡4", "不参与"
        };
        // selected index -> gpu index, -2 = all gpu, -1 = this fan is not controlled
        private static readonly int[] MapValue = new int[] { -2, 0, 1, 2, 3, -1 };

        private readonly IFanHost host;
        private readonly SpeedLadder ladder = new SpeedLadder();

        private GroupBox box;
        private CheckBox chkEnable;
        private NumericUpDown nudInterval, nudBase, nudHyst;
        private NumericUpDown nudT1, nudS1, nudT2, nudS2, nudT3, nudS3;
        private ComboBox[] cmbFan = new ComboBox[GpuProbe.FanCount];
        private TextBox txtSmi;
        private Button btnNow;
        private Label lblGpu, lblFans, lblStatus;
        private ToolTip tips;

        private class Snapshot
        {
            public bool Enabled;
            public int IntervalSeconds;
            public int Base, Hysteresis;
            public int[] Trigger = new int[3];
            public int[] Speed = new int[3];
            public int[] FanMap = new int[GpuProbe.FanCount];
            public string Smi = "";
        }

        private volatile Snapshot snap = new Snapshot();
        private Thread worker;
        private volatile bool stop;
        private volatile bool runNow;

        private readonly int[] applied = new int[GpuProbe.FanCount];
        private readonly object logLock = new object();
        private string lastError = "";

        public GpuFanControl(IFanHost owner)
        {
            host = owner;
            for (int i = 0; i < applied.Length; i++) applied[i] = -1;
            BuildUi();
            LoadConfig();
            TakeSnapshot();
        }

        public GroupBox Group { get { return box; } }

        // ------------------------------------------------------------------
        // UI
        // ------------------------------------------------------------------
        private static Label MakeLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.Text = text;
            l.Location = new Point(x, y);
            return l;
        }

        private static NumericUpDown MakeNum(int min, int max, int val, int x, int y, int w)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = val;
            n.Location = new Point(x, y);
            n.Size = new Size(w, 21);
            n.BorderStyle = BorderStyle.FixedSingle;
            return n;
        }

        private void BuildUi()
        {
            box = new GroupBox();
            box.Text = "显卡温度自动控速 (GPU Auto Control)  —  温度超过触发点自动加速，回落到(触发-回差)后自动降回安静转速";
            box.Location = new Point(12, 433);
            box.Size = new Size(817, 148);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            chkEnable = new CheckBox();
            chkEnable.Text = "启用显卡温控";
            chkEnable.AutoSize = true;
            chkEnable.Location = new Point(12, 18);
            chkEnable.CheckedChanged += delegate(object s, EventArgs e) { OnSettingChanged(); };

            nudInterval = MakeNum(5, 600, 15, 190, 17, 50);
            nudBase = MakeNum(10, 100, 20, 334, 17, 50);
            nudHyst = MakeNum(0, 15, 3, 450, 17, 45);
            nudT1 = MakeNum(25, 100, 50, 75, 43, 50);
            nudS1 = MakeNum(10, 100, 40, 158, 43, 50);
            nudT2 = MakeNum(25, 100, 65, 299, 43, 50);
            nudS2 = MakeNum(10, 100, 55, 382, 43, 50);
            nudT3 = MakeNum(25, 100, 75, 523, 43, 50);
            nudS3 = MakeNum(10, 100, 70, 606, 43, 50);
            NumericUpDown[] nums = new NumericUpDown[] { nudInterval, nudBase, nudHyst, nudT1, nudS1, nudT2, nudS2, nudT3, nudS3 };
            foreach (NumericUpDown n in nums)
            {
                n.ValueChanged += delegate(object s, EventArgs e) { OnSettingChanged(); };
            }

            btnNow = new Button();
            btnNow.Text = "立即检查";
            btnNow.Location = new Point(700, 16);
            btnNow.Size = new Size(104, 24);
            btnNow.Click += delegate(object s, EventArgs e)
            {
                OnSettingChanged();
                runNow = true;
                SetStatus("已请求立即检查...");
            };

            txtSmi = new TextBox();
            txtSmi.Location = new Point(100, 95);
            txtSmi.Size = new Size(170, 21);
            txtSmi.BorderStyle = BorderStyle.FixedSingle;
            txtSmi.Text = "nvidia-smi";
            txtSmi.Leave += delegate(object s, EventArgs e) { OnSettingChanged(); };

            tips = new ToolTip();
            tips.AutoPopDelay = 15000;
            tips.InitialDelay = 300;
            tips.ReshowDelay = 100;
            tips.ShowAlways = true;

            for (int i = 0; i < GpuProbe.FanCount; i++)
            {
                ComboBox c = new ComboBox();
                c.DropDownStyle = ComboBoxStyle.DropDownList;
                c.Items.AddRange(MapItems);
                c.SelectedIndex = 0;
                c.Location = new Point(118 + i * 120, 69);
                c.Size = new Size(98, 21);
                c.SelectedIndexChanged += delegate(object s, EventArgs e) { OnSettingChanged(); };
                cmbFan[i] = c;
                box.Controls.Add(MakeLabel("F" + (i + 1).ToString(CultureInfo.InvariantCulture), 100 + i * 120, 72));
                box.Controls.Add(c);
                tips.SetToolTip(c, "F" + (i + 1) + " 就是 Fan_" + (i + 1) + "：选择这把风扇按哪块显卡的温度调速。\n全部(取最高) = 任一显卡温度高就加速；不参与 = 这把风扇交给手动按钮。");
            }

            lblGpu = MakeLabel("显卡温度: 未读取", 300, 98);
            lblFans = MakeLabel("输出转速: -", 12, 121);
            lblFans.AutoSize = false;
            lblFans.Size = new Size(470, 20);
            lblStatus = MakeLabel("状态: 未启用", 490, 121);
            lblStatus.AutoSize = false;
            lblStatus.Size = new Size(315, 20);

            box.Controls.Add(chkEnable);
            box.Controls.Add(MakeLabel("检查间隔", 128, 21));
            box.Controls.Add(nudInterval);
            box.Controls.Add(MakeLabel("秒", 245, 21));
            box.Controls.Add(MakeLabel("安静转速", 272, 21));
            box.Controls.Add(nudBase);
            box.Controls.Add(MakeLabel("%", 389, 21));
            box.Controls.Add(MakeLabel("回差", 415, 21));
            box.Controls.Add(nudHyst);
            box.Controls.Add(MakeLabel("℃", 500, 21));
            box.Controls.Add(btnNow);

            box.Controls.Add(MakeLabel("一级", 34, 46));
            box.Controls.Add(nudT1);
            box.Controls.Add(MakeLabel("℃→", 130, 46));
            box.Controls.Add(nudS1);
            box.Controls.Add(MakeLabel("%", 213, 46));
            box.Controls.Add(MakeLabel("二级", 258, 46));
            box.Controls.Add(nudT2);
            box.Controls.Add(MakeLabel("℃→", 354, 46));
            box.Controls.Add(nudS2);
            box.Controls.Add(MakeLabel("%", 437, 46));
            box.Controls.Add(MakeLabel("三级", 482, 46));
            box.Controls.Add(nudT3);
            box.Controls.Add(MakeLabel("℃→", 578, 46));
            box.Controls.Add(nudS3);
            box.Controls.Add(MakeLabel("%  (≥90℃→100%)", 661, 46));

            box.Controls.Add(MakeLabel("nvidia-smi:", 12, 98));
            box.Controls.Add(txtSmi);
            box.Controls.Add(MakeLabel("每把风扇:", 12, 72));

            box.Controls.Add(lblGpu);
            box.Controls.Add(lblFans);
            box.Controls.Add(lblStatus);
        }

        private void SetStatus(string text)
        {
            host.OnUi(delegate { lblStatus.Text = "状态: " + text; });
        }

        // ------------------------------------------------------------------
        // settings
        // ------------------------------------------------------------------
        private void OnSettingChanged()
        {
            TakeSnapshot();
            SaveConfig();
            if (chkEnable.Checked)
            {
                ladder.Reset();
                for (int i = 0; i < applied.Length; i++) applied[i] = -1;
                runNow = true;
            }
            else
            {
                SetStatus("未启用 — 风扇保持手动/自动模式，由上面的按钮控制");
            }
        }

        private void TakeSnapshot()
        {
            Snapshot s = new Snapshot();
            s.Enabled = chkEnable.Checked;
            s.IntervalSeconds = (int)nudInterval.Value;
            s.Base = (int)nudBase.Value;
            s.Hysteresis = (int)nudHyst.Value;
            s.Trigger[0] = (int)nudT1.Value;
            s.Trigger[1] = (int)nudT2.Value;
            s.Trigger[2] = (int)nudT3.Value;
            s.Speed[0] = (int)nudS1.Value;
            s.Speed[1] = (int)nudS2.Value;
            s.Speed[2] = (int)nudS3.Value;
            s.Smi = txtSmi.Text.Trim();
            for (int i = 0; i < GpuProbe.FanCount; i++)
            {
                int idx = cmbFan[i].SelectedIndex;
                if (idx < 0 || idx >= MapValue.Length) idx = 0;
                s.FanMap[i] = MapValue[idx];
            }
            snap = s;   // single reference assignment: the worker thread reads one consistent set

            ladder.Trigger = s.Trigger;
            ladder.Speed = s.Speed;
            ladder.BaseSpeed = s.Base;
            ladder.Hysteresis = s.Hysteresis;
        }

        private int ReadInt(string key, int fallback)
        {
            string v = IniHelper.Read(Section, key, "", GpuConfig.FilePath);
            int parsed;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }

        private void LoadConfig()
        {
            chkEnable.Checked = ReadInt("enabled", 0) != 0;
            nudInterval.Value = Clamp(ReadInt("interval", 15), (int)nudInterval.Minimum, (int)nudInterval.Maximum);
            nudBase.Value = Clamp(ReadInt("base", 20), (int)nudBase.Minimum, (int)nudBase.Maximum);
            nudHyst.Value = Clamp(ReadInt("hyst", 3), (int)nudHyst.Minimum, (int)nudHyst.Maximum);
            nudT1.Value = Clamp(ReadInt("t1", 50), (int)nudT1.Minimum, (int)nudT1.Maximum);
            nudS1.Value = Clamp(ReadInt("s1", 40), (int)nudS1.Minimum, (int)nudS1.Maximum);
            nudT2.Value = Clamp(ReadInt("t2", 65), (int)nudT2.Minimum, (int)nudT2.Maximum);
            nudS2.Value = Clamp(ReadInt("s2", 55), (int)nudS2.Minimum, (int)nudS2.Maximum);
            nudT3.Value = Clamp(ReadInt("t3", 75), (int)nudT3.Minimum, (int)nudT3.Maximum);
            nudS3.Value = Clamp(ReadInt("s3", 70), (int)nudS3.Minimum, (int)nudS3.Maximum);

            string smi = IniHelper.Read(Section, "smi", "", GpuConfig.FilePath);
            txtSmi.Text = string.IsNullOrEmpty(smi) ? "nvidia-smi" : smi;

            for (int i = 0; i < GpuProbe.FanCount; i++)
            {
                int idx = ReadInt("fan" + (i + 1).ToString(CultureInfo.InvariantCulture), 0);
                if (idx < 0 || idx >= MapItems.Length) idx = 0;
                cmbFan[i].SelectedIndex = idx;
            }
        }

        private void SaveConfig()
        {
            IniHelper.Write(Section, "enabled", chkEnable.Checked ? "1" : "0", GpuConfig.FilePath);
            IniHelper.Write(Section, "interval", ((int)nudInterval.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "base", ((int)nudBase.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "hyst", ((int)nudHyst.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "t1", ((int)nudT1.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "s1", ((int)nudS1.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "t2", ((int)nudT2.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "s2", ((int)nudS2.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "t3", ((int)nudT3.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "s3", ((int)nudS3.Value).ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            IniHelper.Write(Section, "smi", txtSmi.Text.Trim(), GpuConfig.FilePath);
            for (int i = 0; i < GpuProbe.FanCount; i++)
            {
                IniHelper.Write(Section, "fan" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    cmbFan[i].SelectedIndex.ToString(CultureInfo.InvariantCulture), GpuConfig.FilePath);
            }
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // ------------------------------------------------------------------
        // worker
        // ------------------------------------------------------------------
        public void Start()
        {
            if (worker != null) return;
            worker = new Thread(new ThreadStart(Loop));
            worker.IsBackground = true;
            worker.Name = "gpu-fan-control";
            worker.Start();
        }

        public void Stop()
        {
            stop = true;
        }

        /// <summary>The user pressed "Reset to Auto Mode": stop the automatic control.</summary>
        public void UserResetToAuto()
        {
            if (chkEnable.InvokeRequired)
            {
                host.OnUi(delegate { UserResetToAuto(); });
                return;
            }
            if (chkEnable.Checked)
            {
                chkEnable.Checked = false;   // triggers OnSettingChanged -> snapshot + save
                SetStatus("已交还给 iDRAC 自动模式，显卡温控已关闭");
                Log("用户点击 Reset to Auto Mode：显卡温控已关闭");
            }
            for (int i = 0; i < applied.Length; i++) applied[i] = -1;
        }

        public void NoteManualApply(string what)
        {
            if (!snap.Enabled) return;
            SetStatus("手动设置了 " + what + "，显卡温控仍在使用（下一轮检查会按显卡温度重新设定）");
        }

        private void Loop()
        {
            while (!stop)
            {
                Snapshot s = snap;
                if (s.Enabled)
                {
                    try { Poll(s); }
                    catch (Exception ex) { ReportError("检查异常: " + ex.Message); }
                    int waitMs = Math.Max(5, s.IntervalSeconds) * 1000;
                    // sleep in small steps so a changed interval / enable takes effect quickly
                    int slept = 0;
                    while (slept < waitMs && !stop && !runNow)
                    {
                        Thread.Sleep(200);
                        slept += 200;
                    }
                    runNow = false;
                }
                else
                {
                    Thread.Sleep(1000);
                }
            }
        }

        private void Poll(Snapshot s)
        {
            string readError;
            List<GpuInfo> gpus = GpuProbe.ReadGpus(s.Smi, out readError);
            if (gpus == null)
            {
                ReportError("读取显卡温度失败: " + readError);
                return;
            }

            int[] gpuSpeed = new int[gpus.Count];
            StringBuilder gpuText = new StringBuilder();
            for (int i = 0; i < gpus.Count; i++)
            {
                gpuSpeed[i] = ladder.SpeedFor(i, gpus[i].Temperature);
                if (i > 0) gpuText.Append("   ");
                gpuText.Append("显卡" + gpus[i].Index + " " + gpus[i].Temperature + "℃ → " + gpuSpeed[i] + "%");
            }

            int[] fanTarget = new int[GpuProbe.FanCount];
            bool[] fanUsed = new bool[GpuProbe.FanCount];
            for (int f = 0; f < GpuProbe.FanCount; f++)
            {
                int map = s.FanMap[f];
                if (map == -1) { fanUsed[f] = false; continue; }
                int speed = 0;
                for (int g = 0; g < gpus.Count; g++)
                {
                    if (map >= 0 && map != gpus[g].Index) continue;
                    if (gpuSpeed[g] > speed) speed = gpuSpeed[g];
                }
                if (speed <= 0) continue;                 // mapped gpu gone: leave the fan alone
                speed = Clamp(speed, 10, 100);
                fanTarget[f] = speed;
                fanUsed[f] = true;
            }

            string[] cred = host.IpmiCredential();
            string ipmitool = host.IpmiToolPath;
            string ip = cred[0], user = cred[1], password = cred[2];

            List<int> changed = new List<int>();
            for (int f = 0; f < GpuProbe.FanCount; f++)
            {
                if (!fanUsed[f]) continue;
                if (applied[f] == fanTarget[f]) continue;
                changed.Add(f);
            }

            string problem = null;
            if (changed.Count > 0)
            {
                string err;
                if (!GpuProbe.DisableAutoMode(ipmitool, ip, user, password, out err))
                {
                    problem = "关闭 iDRAC 自动风扇模式失败: " + err;
                }
                else
                {
                    foreach (int f in changed)
                    {
                        if (!GpuProbe.SetFan(ipmitool, ip, user, password, f, fanTarget[f], out err))
                        {
                            problem = "Fan_" + (f + 1) + " 设置为 " + fanTarget[f] + "% 失败: " + err;
                            // per-fan command not accepted by this firmware: fall back to "all fans"
                            string err2;
                            int max = 0;
                            for (int f2 = 0; f2 < GpuProbe.FanCount; f2++)
                            {
                                if (fanUsed[f2] && fanTarget[f2] > max) max = fanTarget[f2];
                            }
                            if (max > 0 && GpuProbe.SetAllFans(ipmitool, ip, user, password, max, out err2))
                            {
                                problem = null;
                                for (int f2 = 0; f2 < GpuProbe.FanCount; f2++)
                                {
                                    if (fanUsed[f2]) applied[f2] = max;
                                }
                                Log("按单把风扇设置不被支持，已改用全部风扇 " + max + "%");
                            }
                            break;
                        }
                        applied[f] = fanTarget[f];
                        host.SetFanUiValue(f + 1, fanTarget[f]);
                    }
                }
            }

            int commonSpeed = -1;
            bool allSame = true;
            int usedCount = 0;
            for (int f = 0; f < GpuProbe.FanCount; f++)
            {
                if (!fanUsed[f]) continue;
                usedCount++;
                if (commonSpeed < 0) commonSpeed = fanTarget[f];
                else if (commonSpeed != fanTarget[f]) allSame = false;
            }

            StringBuilder fanText = new StringBuilder("输出转速: ");
            if (usedCount > 0 && allSame)
            {
                fanText.Append(usedCount.ToString(CultureInfo.InvariantCulture) + " 把风扇全部 " + commonSpeed.ToString(CultureInfo.InvariantCulture) + "%");
            }
            else
            {
                for (int f = 0; f < GpuProbe.FanCount; f++)
                {
                    if (f > 0) fanText.Append(" / ");
                    fanText.Append("F" + (f + 1) + " " + (fanUsed[f] ? fanTarget[f].ToString(CultureInfo.InvariantCulture) + "%" : "-"));
                }
            }

            string gpu = gpuText.ToString();
            string fans = fanText.ToString();
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            string extra = problem ?? (changed.Count > 0
                ? "已按显卡温度调整 " + changed.Count + " 把风扇"
                : (string.IsNullOrEmpty(lastError) ? "正常，转速无需调整" : "上次错误: " + lastError));

            host.OnUi(delegate
            {
                lblGpu.Text = "显卡温度: " + gpu;
                lblFans.Text = fans;
                lblStatus.Text = "状态: [" + stamp + "] " + extra;
            });

            if (problem != null) Log(problem);
            else if (changed.Count > 0) Log("显卡温度 " + gpu + "  =>  " + fans);
            lastError = problem ?? "";
        }

        private void ReportError(string message)
        {
            if (message == lastError) return;   // do not repeat the same error every round
            lastError = message;
            SetStatus(message);
            Log(message);
        }

        private void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            try
            {
                lock (logLock)
                {
                    File.AppendAllText(GpuConfig.LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
            host.GpuLog(line);
        }
    }

    /// <summary>Paths used by the GPU control feature.</summary>
    public static class GpuConfig
    {
        public static string FilePath = System.IO.Path.Combine(Application.StartupPath, "config.ini");
        public static string LogPath = System.IO.Path.Combine(Application.StartupPath, "gpu_control.log");
    }
}
