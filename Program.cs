using System;
using System.IO;
using System.Timers;
using CommandSystem;
using Exiled.API.Enums;
using Exiled.API.Features;
using Exiled.API.Interfaces;

namespace FacilityTools
{
    /// <summary>
    /// 合并配置：探测(ReportPath) + 恢复(自动/手动开关等)。
    /// EXILED 会按属性名生成 yml，键名为下划线形式（如 report_path）。
    /// </summary>
    public class Config : IConfig
    {
        public bool IsEnabled { get; set; } = true;

        // EXILED 8.x IConfig 必需成员（缺失会报 CS0535）
        public bool Debug { get; set; } = false;

        // ---------- 探测 ----------

        /// <summary>探测报告写到哪里（相对服务器目录，留空则只输出到 RA 和日志）</summary>
        public string ReportPath { get; set; } = "FacilityTools_report.txt";

        // ---------- 恢复 ----------

        /// <summary>核弹爆炸后自动恢复（false 则只能靠命令手动恢复）</summary>
        public bool AutoRestore { get; set; } = true;

        /// <summary>检测到爆炸后，延迟多少秒再恢复（给爆炸特效/死亡结算留时间）</summary>
        public float RestoreDelaySeconds { get; set; } = 3f;

        /// <summary>核弹状态轮询间隔（秒）。仅在自动恢复开启时生效</summary>
        public float PollIntervalSeconds { get; set; } = 1f;

        /// <summary>解锁所有电梯（同时会解锁电梯门 / bulkhead 舱壁）</summary>
        public bool UnlockLifts { get; set; } = true;

        /// <summary>清除门上由核弹施加的 Warhead 锁定标记</summary>
        public bool RemoveWarheadLocks { get; set; } = true;

        /// <summary>恢复后是否全服广播</summary>
        public bool BroadcastOnRestore { get; set; } = true;

        /// <summary>广播持续秒数</summary>
        public ushort BroadcastDuration { get; set; } = 5;

        /// <summary>广播内容</summary>
        public string BroadcastMessage { get; set; } =
            "设施已恢复：电梯重新启用，通往地下区域的通道已开放。";
    }

    // ============================================================
    //  共用：报告落盘 + 关键行提取
    // ============================================================

    internal static class Report
    {
        /// <summary>把长文本写到「报告路径 + 后缀」，RA 只回显关键行</summary>
        public static string Write(string reportPath, string suffix, string full, string keyLines)
        {
            if (string.IsNullOrWhiteSpace(reportPath)) return full;
            try
            {
                string path = Path.ChangeExtension(reportPath, null) + suffix + ".txt";
                File.WriteAllText(path, full);
                return "(full result written to file)\n" + Path.GetFullPath(path)
                     + "\n\n--- key lines ---\n" + keyLines;
            }
            catch (Exception ex)
            {
                return full + "\n\n[WARN] cannot write file: " + ex.Message;
            }
        }

        /// <summary>从开门诊断结果里抽取关键行，避免 RA 窗口刷屏</summary>
        public static string Summarize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("Instances:")
                 || t.StartsWith("Type:")
                 || t.StartsWith("_isOpen")
                 || t.StartsWith(">>")
                 || t.StartsWith("->")
                 || t.StartsWith("[Verify]")
                 || t.StartsWith("(False"))
                {
                    sb.Append(line).Append("\n");
                }
            }
            return sb.Length == 0 ? "(no key lines)" : sb.ToString();
        }

        /// <summary>从电梯扫描结果里抽取关键行（锁定相关）</summary>
        public static string SummarizeLift(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("Lift type:")
                 || t.StartsWith("Instances:")
                 || t.StartsWith("via '")
                 || t.StartsWith("(no Base")
                 || t.StartsWith("[") && t.Contains("IsLocked=")
                 || (t.Contains("set=Y") && (t.Contains("IsLocked") || t.Contains("locked")))
                 || (t.Contains("(") && (t.Contains("Unlock") || t.Contains("SetLock")
                     || t.Contains("ServerSet"))))
                {
                    sb.Append(line).Append("\n");
                }
            }
            return sb.Length == 0 ? "(no key lines)" : sb.ToString();
        }
    }

    // ============================================================
    //  恢复动作（命令与自动检测共用）
    // ============================================================

    internal static class RestoreAction
    {
        /// <summary>执行一次完整恢复，返回给 RA 的结果文本</summary>
        public static string Run(Config cfg, out bool anySuccess)
        {
            anySuccess = false;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            // 0. 开启 BlastDoor —— 核爆降下的防爆挡板，就是电梯前那道穿不过的"墙"
            bool wallOk = Restore.OpenBlastDoors(out int wallCount, out string wallDetail);
            anySuccess |= wallOk;
            sb.Append(wallOk ? "[OK] " : "[FAIL] ")
              .Append("BlastDoor(wall): ").Append(wallDetail).Append("\n");

            // 1. 解锁所有电梯
            if (cfg.UnlockLifts)
            {
                bool ok = Restore.UnlockAllLifts(out int liftCount, out string liftDetail);
                anySuccess |= ok;
                sb.Append(ok ? "[OK] " : "[FAIL] ").Append("Lifts: ").Append(liftDetail).Append("\n");
            }
            else
            {
                sb.Append("[SKIP] Lifts unlocking is disabled\n");
            }

            // 2. 清除门上的 Warhead 锁定
            if (cfg.RemoveWarheadLocks)
            {
                bool ok = Restore.RemoveWarheadLocks(out int doorCount, out string doorDetail);
                anySuccess |= ok;
                sb.Append(ok ? "[OK] " : "[FAIL] ").Append("Doors: ").Append(doorDetail).Append("\n");
            }
            else
            {
                sb.Append("[SKIP] Warhead lock removal is disabled\n");
            }

            // 3. 广播（失败不影响结果）
            if (cfg.BroadcastOnRestore)
            {
                bool bcOk = Restore.TryBroadcast(cfg.BroadcastMessage,
                                                 cfg.BroadcastDuration, out string bcDetail);
                sb.Append(bcOk ? "[OK] " : "[WARN] ").Append("Broadcast: ").Append(bcDetail).Append("\n");
            }

            return sb.ToString().TrimEnd('\n', '\r');
        }
    }

    /// <summary>轮询检测核弹爆炸，触发自动恢复</summary>
    internal sealed class RestoreMonitor
    {
        private readonly Plugin _plugin;
        private Timer _pollTimer;
        private bool _lastDetonated;

        public RestoreMonitor(Plugin plugin)
        {
            _plugin = plugin;
        }

        public void Start()
        {
            Stop();
            if (!_plugin.Config.AutoRestore) return;

            double intervalMs = Math.Max(200d, _plugin.Config.PollIntervalSeconds * 1000d);
            _pollTimer = new Timer(intervalMs);
            _pollTimer.AutoReset = true;
            _pollTimer.Elapsed += OnPoll;
            _pollTimer.Start();
        }

        public void Stop()
        {
            if (_pollTimer == null) return;
            try
            {
                _pollTimer.Stop();
                _pollTimer.Dispose();
            }
            catch { }
            _pollTimer = null;
        }

        private void OnPoll(object sender, ElapsedEventArgs e)
        {
            try
            {
                bool detonated = Restore.IsWarheadDetonated(out string detail);
                if (_plugin.Config.Debug)
                    Log.Debug("[FacilityTools] poll: " + detail);

                // 只在「未爆炸 -> 已爆炸」的跳变时触发一次
                if (!detonated)
                {
                    _lastDetonated = false;
                    return;
                }
                if (_lastDetonated) return;

                _lastDetonated = true;
                Log.Info("[FacilityTools] Warhead detonation detected, scheduling restore...");
                ScheduleRestore();
            }
            catch (Exception ex)
            {
                Log.Error("[FacilityTools] poll error: " + ex.Message);
            }
        }

        /// <summary>延迟一段时间后执行恢复（一次性定时器）</summary>
        private void ScheduleRestore()
        {
            double delayMs = Math.Max(0d, _plugin.Config.RestoreDelaySeconds * 1000d);

            if (delayMs <= 0)
            {
                DoRestore();
                return;
            }

            Timer once = new Timer(delayMs);
            once.AutoReset = false;
            once.Elapsed += (s, e) =>
            {
                try { DoRestore(); } catch { }
                try { once.Dispose(); } catch { }
            };
            once.Start();
        }

        private void DoRestore()
        {
            string result = RestoreAction.Run(_plugin.Config, out bool ok);
            Log.Info("[FacilityTools] auto-restore " + (ok ? "succeeded" : "reported issues") + "\n" + result);
        }
    }

    // ============================================================
    //  RA 命令 1：probe
    //    probe          -> 完整探测，报告写入文件
    //    probe open     -> 尝试打开 BlastDoor（核爆那道墙）
    //    probe lift     -> 电梯深度扫描，找真正的解锁通道
    // ============================================================

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public class ProbeCommand : ICommand
    {
        public string Command { get; } = "probe";
        public string[] Aliases { get; } = { "fp", "probefacility" };
        public string Description { get; } =
            "Dump BlastDoor / BulkheadDoor / Lift / Door runtime info. Subcommands: open, lift";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            response = "";
            Plugin plugin = Plugin.Instance;
            if (plugin == null || !plugin.Config.IsEnabled)
            {
                response = "FacilityTools is disabled.";
                return false;
            }

            string sub = "";
            if (arguments.Count > 0) sub = arguments.Array[arguments.Offset];

            // 子命令：lift（电梯深度扫描）
            if (sub.Equals("lift", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string r = Probe.ProbeLifts();
                    Log.Info("[FacilityTools] lift scan:\n" + r);
                    response = Report.Write(plugin.Config.ReportPath, "_lift", r, Report.SummarizeLift(r));
                    return true;
                }
                catch (Exception ex)
                {
                    response = "lift scan threw: " + ex.Message;
                    Log.Error("[FacilityTools] " + response);
                    return false;
                }
            }

            // 子命令：open（多策略尝试开门 + 完整诊断）
            if (sub.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string r = Probe.TryOpenBlastDoors();
                    Log.Info("[FacilityTools] manual open attempt:\n" + r);
                    response = Report.Write(plugin.Config.ReportPath, "_open", r, Report.Summarize(r));
                    return true;
                }
                catch (Exception ex)
                {
                    response = "open threw: " + ex.Message;
                    Log.Error("[FacilityTools] " + response);
                    return false;
                }
            }

            // 默认：完整探测
            try
            {
                string full = Probe.RunProbe(out string summary);
                response = summary;

                string path = plugin.Config.ReportPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        File.WriteAllText(path, full);
                        response += "\n\nFull report written to: " + Path.GetFullPath(path);
                    }
                    catch (Exception ex)
                    {
                        response += "\n\n[WARN] cannot write report file: " + ex.Message;
                    }
                }

                Log.Info("[FacilityTools] probe run complete.\n" + full);
                return true;
            }
            catch (Exception ex)
            {
                response = "Probe threw: " + ex.Message + "\n" + ex.StackTrace;
                Log.Error("[FacilityTools] " + response);
                return false;
            }
        }
    }

    // ============================================================
    //  RA 命令 2：restorefacility
    // ============================================================

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public class RestoreCommand : ICommand
    {
        public string Command { get; } = "restorefacility";
        public string[] Aliases { get; } = { "rf", "restoreunderground" };
        public string Description { get; } =
            "Re-enable all elevators and remove the warhead bulkheads blocking the underground.";

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            response = "";
            Plugin plugin = Plugin.Instance;
            if (plugin == null || !plugin.Config.IsEnabled)
            {
                response = "FacilityTools is disabled.";
                return false;
            }

            string result = RestoreAction.Run(plugin.Config, out bool ok);
            response = result;
            Log.Info("[FacilityTools] manual restore by RA\n" + result);
            return ok;
        }
    }

    // ============================================================
    //  插件主体
    // ============================================================

    public class Plugin : Exiled.API.Features.Plugin<Config>
    {
        public static Plugin Instance { get; private set; }

        private RestoreMonitor _monitor;

        public override string Name => "FacilityTools";
        public override string Author => "Yuanbao";
        public override Version Version => new Version(1, 0, 0);
        // 枚举名经 ILSpy 反编译确认为 PluginPriority（非 PluginPriorityLevel）
        public override PluginPriority Priority => PluginPriority.Default;

        public override void OnEnabled()
        {
            Instance = this;
            _monitor = new RestoreMonitor(this);
            _monitor.Start();
            base.OnEnabled();

            Log.Info("[FacilityTools] Enabled. Commands: probe (probe open / probe lift),"
                     + " restorefacility"
                     + (Config.AutoRestore
                         ? " Auto-restore ON (poll " + Config.PollIntervalSeconds
                           + "s, delay " + Config.RestoreDelaySeconds + "s)."
                         : " Auto-restore OFF, use 'restorefacility'."));
        }

        public override void OnDisabled()
        {
            if (_monitor != null)
            {
                _monitor.Stop();
                _monitor = null;
            }
            Instance = null;
            base.OnDisabled();
        }
    }
}
