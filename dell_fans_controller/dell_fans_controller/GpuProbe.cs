using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace dell_fans_controller
{
    /// <summary>One graphics card as reported by nvidia-smi.</summary>
    public class GpuInfo
    {
        public int Index;
        public string Name = "";
        public int Temperature;
    }

    /// <summary>
    /// Non-UI helpers: read GPU temperatures through nvidia-smi and send the
    /// Dell iDRAC fan commands through ipmitool.
    /// </summary>
    public static class GpuProbe
    {
        public static string[] DefaultSmiCandidates = new string[]
        {
            "nvidia-smi",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation\\NVSMI\\nvidia-smi.exe")
        };

        /// <summary>Run a program and capture stdout. error is null when nothing went wrong.</summary>
        public static string RunProcess(string fileName, string arguments, int timeoutMs, out int exitCode, out string error)
        {
            exitCode = -1;
            error = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName);
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); }
                        catch { }
                        error = "timeout after " + timeoutMs + " ms";
                        return stdout;
                    }
                    exitCode = p.ExitCode;
                    if (!string.IsNullOrEmpty(stderr) && stderr.Trim().Length > 0)
                    {
                        error = stderr.Trim();
                    }
                    return stdout;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Read every GPU temperature. Tries the configured tool first, then the
        /// usual locations. Returns null and fills error when nothing worked.
        /// </summary>
        public static List<GpuInfo> ReadGpus(string configuredTool, out string error)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(configuredTool)) candidates.Add(configuredTool);
            foreach (string c in DefaultSmiCandidates)
            {
                if (!candidates.Contains(c)) candidates.Add(c);
            }

            string lastError = "nvidia-smi not found";
            foreach (string tool in candidates)
            {
                int exitCode;
                string err;
                string outp = RunProcess(tool,
                    "--query-gpu=index,name,temperature.gpu --format=csv,noheader,nounits",
                    20000, out exitCode, out err);

                List<GpuInfo> list = ParseGpuCsv(outp);
                if (list != null && list.Count > 0)
                {
                    error = null;
                    return list;
                }
                if (!string.IsNullOrEmpty(err)) lastError = err;
            }
            error = lastError;
            return null;
        }

        private static List<GpuInfo> ParseGpuCsv(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return null;
            List<GpuInfo> list = new List<GpuInfo>();
            foreach (string raw in csv.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(',');
                if (parts.Length < 3) continue;

                int index, temp;
                if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) continue;
                string tempText = parts[2].Trim();
                if (tempText.StartsWith("[")) continue;               // "[N/A]"
                if (!int.TryParse(tempText, NumberStyles.Integer, CultureInfo.InvariantCulture, out temp)) continue;

                GpuInfo g = new GpuInfo();
                g.Index = index;
                g.Name = parts[1].Trim();
                g.Temperature = temp;
                list.Add(g);
            }
            if (list.Count == 0) return null;
            list.Sort(delegate(GpuInfo a, GpuInfo b) { return a.Index.CompareTo(b.Index); });
            return list;
        }

        // ------------------------------------------------------------------
        // iDRAC fan commands (same raw commands used by the buttons of the UI)
        // ------------------------------------------------------------------
        public const int FanCount = 6;

        private static string Build(string ipmitool, string ip, string user, string password, string raw)
        {
            return string.Format("-I lanplus -H {0} -U {1} -P {2} {3}", ip, user, password, raw);
        }

        private static bool Send(string ipmitool, string ip, string user, string password, string raw, out string error)
        {
            error = null;
            int exitCode;
            string err;
            string outp = RunProcess(ipmitool, Build(ipmitool, ip, user, password, raw), 30000, out exitCode, out err);
            if (outp == null)
            {
                error = "ipmitool 无法启动: " + err;
                return false;
            }
            string all = (outp ?? "") + " " + (err ?? "");
            if (exitCode != 0 || all.Contains("Error") || all.Contains("Unable") || all.Contains("Invalid") || all.Contains("timeout"))
            {
                error = all.Trim();
                return false;
            }
            return true;
        }

        /// <summary>raw 0x30 0x30 0x01 0x00 - take the fans out of automatic mode.</summary>
        public static bool DisableAutoMode(string ipmitool, string ip, string user, string password, out string error)
        {
            return Send(ipmitool, ip, user, password, "raw 0x30 0x30 0x01 0x00", out error);
        }

        /// <summary>raw 0x30 0x30 0x01 0x01 - give the fans back to the BMC.</summary>
        public static bool EnableAutoMode(string ipmitool, string ip, string user, string password, out string error)
        {
            return Send(ipmitool, ip, user, password, "raw 0x30 0x30 0x01 0x01", out error);
        }

        /// <summary>Set one fan. fanIndex is 0 based: Fan_1 = 0 ... Fan_6 = 5.</summary>
        public static bool SetFan(string ipmitool, string ip, string user, string password, int fanIndex, int percent, out string error)
        {
            if (percent < 10) percent = 10;
            if (percent > 100) percent = 100;
            return Send(ipmitool, ip, user, password,
                string.Format("raw 0x30 0x30 0x02 0x{0:x2} 0x{1:x2}", fanIndex, percent), out error);
        }

        /// <summary>Set all fans at once (fan id 0xff).</summary>
        public static bool SetAllFans(string ipmitool, string ip, string user, string password, int percent, out string error)
        {
            if (percent < 10) percent = 10;
            if (percent > 100) percent = 100;
            return Send(ipmitool, ip, user, password,
                string.Format("raw 0x30 0x30 0x02 0xff 0x{0:x2}", percent), out error);
        }
    }

    /// <summary>
    /// Temperature -> fan speed ladder with hysteresis, so the fans do not
    /// flap up and down when the temperature sits on a threshold.
    /// </summary>
    public class SpeedLadder
    {
        // Trigger temperature and the speed used from that temperature up.
        public int[] Trigger = new int[] { 50, 65, 75 };
        public int[] Speed = new int[] { 40, 55, 70 };
        public int BaseSpeed = 20;      // used below the first trigger (quiet mode)
        public int Hysteresis = 3;      // speed drops back only after temp < trigger - Hysteresis
        public int EmergencyTemp = 90;  // always 100 % from here on

        private int[] level;            // current ladder position per GPU

        public SpeedLadder()
        {
            level = new int[0];
        }

        public void Reset()
        {
            level = new int[0];
        }

        private void EnsureSlots(int count)
        {
            if (level.Length >= count) return;
            int[] next = new int[count];
            Array.Copy(level, next, level.Length);
            level = next;
        }

        /// <summary>Ladder position 0 = quiet, 1..N = the configured steps.</summary>
        public int SpeedFor(int gpuIndex, int temperature)
        {
            EnsureSlots(gpuIndex + 1);

            if (temperature >= EmergencyTemp)
            {
                level[gpuIndex] = Trigger.Length;
                return 100;
            }

            int wanted = 0;
            for (int i = 0; i < Trigger.Length; i++)
            {
                if (temperature >= Trigger[i]) wanted = i + 1;
            }

            int current = level[gpuIndex];
            if (wanted > current)
            {
                current = wanted;                       // heat up: react at once
            }
            else if (wanted < current)
            {
                int dropBelow = (current >= 1 && current - 1 < Trigger.Length)
                    ? Trigger[current - 1] - Hysteresis
                    : 0;
                if (temperature < dropBelow) current = wanted;   // cool down: wait for the gap
            }

            level[gpuIndex] = current;
            if (current == 0) return BaseSpeed;
            return Speed[current - 1];
        }

        public int LevelFor(int gpuIndex)
        {
            if (gpuIndex < 0 || gpuIndex >= level.Length) return 0;
            return level[gpuIndex];
        }
    }
}
