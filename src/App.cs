using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex 手机通知")]
[assembly: AssemblyDescription("Pushover 托盘通知、持久队列与用户登录启动")]
[assembly: AssemblyProduct("Codex 手机通知")]
[assembly: AssemblyVersion("1.2.1.0")]
[assembly: AssemblyFileVersion("1.2.1.0")]

namespace CodexPushover
{
    public static class Installation
    {
        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static string Text(Dictionary<string, object> obj, string key) { object v; return obj.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : ""; }
        public static List<object> Items(object value) { var a = value as IEnumerable; var r = new List<object>(); if (a != null && !(a is string)) foreach (object v in a) r.Add(v); return r; }
        static string Hash(string text) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant(); }
        public static string ConfigHome { get { var i = Core.ReadObject(Path.Combine(Host.Root, "installation.json")); var p = Text(i, "config_home"); return p.Length == 0 ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : p; } }
        public static void PrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            var sid = WindowsIdentity.GetCurrent().User;
            var acl = new DirectorySecurity(); acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, acl);
        }
        public static void Install()
        {
            PrivateDirectory(Host.Root);
            Core.Initialize(Host.Root);
            string self = Assembly.GetExecutingAssembly().Location;
            if (!String.Equals(self, Host.Exe, StringComparison.OrdinalIgnoreCase)) File.Copy(self, Host.Exe, true);
            var info = Core.ReadObject(Path.Combine(Host.Root, "installation.json"));
            string config = ConfigHome;
            info["config_home"] = config; info["executable"] = Host.Exe; info["executable_version"] = Program.Version;
            if (!info.ContainsKey("previous_background_mode")) info["previous_background_mode"] = Text(info, "background_mode");
            info["exe_migration_phase"] = "staged";
            info["startup_mode"] = "Current-user Startup folder shortcut";
            info["startup_shortcut"] = Path.Combine(Startup.StartupFolder, "Codex 手机通知.lnk");
            Core.WriteObject(Path.Combine(Host.Root, "installation.json"), info);
            if (!File.Exists(Path.Combine(Host.Root, "credentials.json"))) Core.WriteObject(Path.Combine(Host.Root, "credentials.json"), new Dictionary<string, object> { {"PUSHOVER_APP_TOKEN", ""}, {"PUSHOVER_USER_KEY", ""} });
            if (!File.Exists(Path.Combine(Host.Root, "notification-options.json"))) Core.WriteObject(Path.Combine(Host.Root, "notification-options.json"), new Dictionary<string, object> { {"content_mode", "user_input"}, {"user_input_max_chars", 200}, {"conversation_title_max_chars", 200} });
            Startup.Set(true); Startup.CreateLauncherShortcuts();
        }
        public static void InstallHooks()
        {
            string file = Path.Combine(ConfigHome, "hooks.json");
            string original = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "{}";
            string backup = Path.Combine(Host.Root, "backups", "exe-hooks-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            PrivateDirectory(backup); File.WriteAllText(Path.Combine(backup, "hooks.json"), original, new UTF8Encoding(false));
            string toml = Path.Combine(ConfigHome, "config.toml"); if (File.Exists(toml)) File.Copy(toml, Path.Combine(backup, "config.toml"));
            var data = Core.ReadObject(file); object hookObj; data.TryGetValue("hooks", out hookObj); var hooks = Obj(hookObj);
            var info = Core.ReadObject(Path.Combine(Host.Root, "installation.json"));
            var oldCommands = new HashSet<string> { Text(info, "hook_command"), Text(info, "prompt_hook_command") };
            // PowerShell treats a quoted path as a string; an explicit cmd call
            // also works when the Windows session shell itself is cmd.
            string win = "cmd.exe /d /c call \"" + Host.Exe + "\" hook";
            string wsl = Host.Exe.Replace('\\', '/'); if (wsl.Length > 2 && wsl[1] == ':') wsl = "/mnt/" + Char.ToLowerInvariant(wsl[0]) + wsl.Substring(2);
            string unix = "\"" + wsl + "\" hook";
            foreach (string name in new [] { "Stop", "UserPromptSubmit", "PreToolUse" })
            {
                string statusMessage = name == "Stop" ? "保存 Pushover 待发通知（EXE）" : name == "UserPromptSubmit" ? "保存 Pushover 本轮用户输入（EXE）" : "Pushover 提问与选择提醒（EXE）";
                object groupsObj; hooks.TryGetValue(name, out groupsObj); var groups = Items(groupsObj); bool installed = false;
                foreach (object g in groups)
                {
                    var group = Obj(g); object handlersObj; group.TryGetValue("hooks", out handlersObj); var handlers = Items(handlersObj);
                    foreach (object h in handlers)
                    {
                        var handler = Obj(h); string command = Text(handler, "command");
                        if ((command.Length > 0 && oldCommands.Contains(command)) || command == unix || Text(handler, "commandWindows") == win)
                        {
                            if (installed) throw new InvalidOperationException("Duplicate own hook; installation stopped.");
                            handler["command"] = unix; handler["commandWindows"] = win; handler["type"] = "command";
                            handler["timeout"] = 3; handler["statusMessage"] = statusMessage;
                            if (name == "PreToolUse") group["matcher"] = @"^(functions\.)?request_user_input(_async)?$";
                            installed = true;
                        }
                    }
                }
                if (!installed)
                {
                    var handler = new Dictionary<string, object> { {"type", "command"}, {"command", unix}, {"commandWindows", win}, {"timeout", 3}, {"statusMessage", statusMessage} };
                    var added = new Dictionary<string, object> { {"hooks", new object[] { handler }} };
                    if (name == "PreToolUse") added["matcher"] = @"^(functions\.)?request_user_input(_async)?$";
                    groups.Add(added);
                }
                hooks[name] = groups.ToArray();
            }
            data["hooks"] = hooks; Directory.CreateDirectory(ConfigHome); Core.WriteObject(file, data);
            info["exe_hook_command"] = unix; info["exe_hook_command_windows"] = win; info["exe_hooks_backup"] = backup;
            info["exe_installed_hooks_sha256"] = Hash(File.ReadAllText(file, Encoding.UTF8));
            info["exe_hooks_review"] = "required"; Core.WriteObject(Path.Combine(Host.Root, "installation.json"), info);
            Core.Log("exe_hooks_installed_review_required");
        }
        public static void Uninstall()
        {
            string file = Path.Combine(ConfigHome, "hooks.json");
            var info = Core.ReadObject(Path.Combine(Host.Root, "installation.json"));
            var own = new HashSet<string> { Text(info, "exe_hook_command"), Text(info, "exe_hook_command_windows") };
            own.Remove("");
            if (File.Exists(file))
            {
                var data = Core.ReadObject(file); object o; data.TryGetValue("hooks", out o); var hooks = Obj(o);
                foreach (string name in new List<string>(hooks.Keys))
                {
                    var groups = Items(hooks[name]); var kept = new List<object>();
                    foreach (object g in groups)
                    {
                        var group = Obj(g); object hs; group.TryGetValue("hooks", out hs); var list = Items(hs); var remaining = new List<object>();
                        foreach (object h in list)
                        {
                            var handler = Obj(h);
                            if (!own.Contains(Text(handler, "command")) && !own.Contains(Text(handler, "commandWindows"))) remaining.Add(h);
                        }
                        if (remaining.Count > 0) { group["hooks"] = remaining.ToArray(); kept.Add(group); }
                    }
                    if (kept.Count == 0) hooks.Remove(name); else hooks[name] = kept.ToArray();
                }
                data["hooks"] = hooks; Core.WriteObject(file, data);
            }
            Startup.Set(false); Startup.RemoveOwnedShortcuts(); Core.SetEnabled(false);
            File.WriteAllText(Path.Combine(Host.Root, "APP_STOPPED"), "Intentional uninstall.");
            Program.SendCommand("exit"); Core.Log("exe_uninstalled_data_retained");
        }
    }

    public static class Program
    {
        public const string Version = "1.2.1";
        public static string LastPhase = "starting";
        public static long SenderProgressTicks = DateTime.UtcNow.Ticks;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static volatile bool stopping;
        public static void SendCommand(string action)
        {
            string id = Guid.NewGuid().ToString("N");
            var command = new Dictionary<string, object> { {"id", id}, {"action", action}, {"created_utc", DateTime.UtcNow.ToString("o")} };
            if (action == "exit")
            {
                var heartbeat = Core.ReadObject(Path.Combine(Host.Root, "app-tray-heartbeat.json")); object value;
                if (heartbeat.TryGetValue("pid", out value)) command["target_pid"] = value;
                if (heartbeat.TryGetValue("process_start_ticks", out value)) command["target_start_ticks"] = value;
            }
            Directory.CreateDirectory(Path.Combine(Host.Root, "commands"));
            Core.WriteObject(Path.Combine(Host.Root, "commands", id + ".json"), command);
        }
        static string Input()
        {
            using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
            {
                var result = new StringBuilder(); var buffer = new char[8192]; int n;
                while ((n = reader.Read(buffer, 0, buffer.Length)) > 0) { if (result.Length + n > 2097152) throw new InvalidDataException(); result.Append(buffer, 0, n); }
                return result.ToString();
            }
        }
        static void Output(object value) { var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value) + "\n"); using (var s = Console.OpenStandardOutput()) { s.Write(bytes, 0, bytes.Length); s.Flush(); } }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Core.Initialize(Host.Root);
                string mode = args.Length > 0 ? args[0] : "--settings";
                if (mode == "hook" || mode == "stop" || mode == "capture-prompt")
                {
                    try { Core.HandleHook(Input()); Host.EnsureRunning(); } catch { Core.Log("hook_local_error"); }
                    Output(new Dictionary<string, object>()); return 0;
                }
                if (mode == "--guard") { Host.RunGuard(); return 0; }
                if (mode == "--suggestions" || mode == "--input-reminders")
                {
                    if (args.Length != 2 || (args[1] != "on" && args[1] != "off")) throw new ArgumentException();
                    string option = mode == "--suggestions" ? Core.SuggestionOption : Core.InputOption;
                    Core.SetOption(option, args[1] == "on"); Output(new { option = option, enabled = args[1] == "on" }); return 0;
                }
                if (mode == "--install") { Installation.Install(); Output(new { installed = true, executable = Host.Exe, startup_shortcut = Startup.IsConfigured() }); return 0; }
                if (mode == "--install-hooks") { Installation.InstallHooks(); Output(new { configured = true, review_required = true }); return 0; }
                if (mode == "--uninstall") { Installation.Uninstall(); Output(new { uninstalled = true, private_data_retained = true }); return 0; }
                if (mode == "--exit") { File.WriteAllText(Path.Combine(Host.Root, "APP_STOPPED"), "Intentional exit."); SendCommand("exit"); return 0; }
                if (mode == "--disable") { Core.SetEnabled(false); return 0; }
                if (mode == "--enable") { Core.SetEnabled(true); File.Delete(Path.Combine(Host.Root, "APP_STOPPED")); Host.EnsureRunning(); return 0; }
                if (mode == "--resume") { SendCommand("resume"); Host.EnsureRunning(); return 0; }
                if (mode == "--test") { Output(new { id = Core.EnqueueTest("EXE托盘通知测试", "验证单文件 EXE 通知。", "这条通知由 CodexPushover.exe 排队发送，请确认手机显示。"), outcome = "queued" }); Host.EnsureRunning(); return 0; }
                if (mode == "--status")
                {
                    var s = Core.Status(); s["tray_running"] = Host.IsHealthy("tray", 30); s["guard_running"] = Host.IsHealthy("guard", 30); s["startup_configured"] = Startup.IsConfigured(); s["executable"] = Host.Exe;
                    s["busy"] = Convert.ToBoolean(s["tray_running"]) && Installation.Text(Core.ReadObject(Path.Combine(Host.Root, "app-tray-heartbeat.json")), "phase") == "sending";
                    Output(s); return 0;
                }
                if (mode != "--tray" && mode != "--settings") throw new ArgumentException();
                if (Array.IndexOf(args, "--background") >= 0 && (File.Exists(Path.Combine(Host.Root, "APP_STOPPED")) || File.Exists(Path.Combine(Host.Root, "APP_MIGRATING")))) return 0;
                bool owns = false;
                using (var mutex = new Mutex(false, Host.MutexName("tray")))
                {
                    try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) { if (mode == "--settings") SendCommand("show"); return 0; }
                    try
                    {
                        File.Delete(Path.Combine(Host.Root, "APP_STOPPED")); Host.StartGuard();
                        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                        var context = new TrayContext(mode == "--settings");
                        var thread = new Thread(Sender); thread.IsBackground = true; thread.Start();
                        Application.Run(context);
                        stopping = true; wake.Set(); thread.Join(1000);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch { try { Core.Log("exe_main_local_error"); } catch { } return 1; }
        }
        static void Sender()
        {
            bool owns = false;
            using (var mutex = new Mutex(false, Host.MutexName("sender")))
            {
                try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) { LastPhase = "sender_already_running"; return; }
                try
                {
                    using (var processLock = new FileStream(Path.Combine(Host.Root, "app-sender.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                    Core.Log("exe_sender_started");
                    while (!stopping)
                    {
                        Interlocked.Exchange(ref SenderProgressTicks, DateTime.UtcNow.Ticks);
                        Core.Pause = stopping || File.Exists(Path.Combine(Host.Root, "APP_MIGRATING"));
                        try { LastPhase = Core.Cycle(); } catch { LastPhase = "local_error"; Core.Log("exe_sender_local_error"); }
                        Interlocked.Exchange(ref SenderProgressTicks, DateTime.UtcNow.Ticks);
                        wake.WaitOne(2000);
                    }
                    }
                }
                catch (IOException) { LastPhase = "sender_lock_unavailable"; Core.Log("sender_lock_unavailable"); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        public static void Exit(TrayContext context)
        {
            File.WriteAllText(Path.Combine(Host.Root, "APP_STOPPED"), "Intentional exit; next manual/startup launch may resume.");
            stopping = true; Core.Pause = true; wake.Set();
            ThreadPool.QueueUserWorkItem(delegate
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (Core.Busy && DateTime.UtcNow < deadline) Thread.Sleep(100);
                if (!Core.Busy) context.OnUi(delegate { context.FinishExit(); });
                else Core.Log("exit_waiting_for_active_request");
            });
        }
        public static void Wake() { wake.Set(); }
    }

    public sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon icon;
        readonly SettingsForm form;
        readonly System.Windows.Forms.Timer timer;
        bool exiting;
        int reading;
        int actionRunning;
        DashboardSnapshot snapshot;
        ToolStripMenuItem pauseItem;
        public TrayContext(bool show)
        {
            form = new SettingsForm(this); form.CreateControl(); var unusedHandle = form.Handle;
            var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 10f), ShowImageMargin = false, Renderer = new BlueMenuRenderer() };
            menu.Items.Add("状态与设置", null, delegate { ShowSettings(); });
            menu.Items.Add("发送测试通知", null, delegate { Test(); });
            pauseItem = (ToolStripMenuItem)menu.Items.Add("暂停手机通知", null, delegate { Toggle(); });
            menu.Items.Add("解除发送错误暂停", null, delegate { Resume(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开数据目录", null, delegate { Open(Host.Root); });
            menu.Items.Add("打开登录启动文件夹", null, delegate { Open(Startup.StartupFolder); });
            menu.Items.Add("使用说明", null, delegate { Open(Path.Combine(Host.Root, "使用说明.md")); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("卸载本工具…", null, delegate
            {
                if (MessageBox.Show("移除本工具的 hooks 和登录启动，停止通知。凭据、待发记录及备份保留。继续？", "卸载 Codex 手机通知", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { try { Installation.Uninstall(); RequestExit(); } catch { MessageBox.Show("卸载未完成，请查看私有目录中的脱敏日志。", "Codex 手机通知"); } }
            });
            menu.Items.Add("退出程序（保留待发）", null, delegate { RequestExit(); });
            icon = new NotifyIcon { Icon = MakeIcon(), Text = "Codex 手机通知", ContextMenuStrip = menu, Visible = true };
            icon.DoubleClick += delegate { ShowSettings(); };
            timer = new System.Windows.Forms.Timer { Interval = 5000 }; timer.Tick += delegate { Tick(); }; timer.Start(); Tick();
            if (show) ShowSettings();
        }
        public static Icon MakeIcon() { return UiAssets.SmallIcon(); }
        void Tick()
        {
            // Queue only from the live UI loop, so a hung UI still loses its heartbeat.
            RequestStatus();
        }
        public void RequestStatus()
        {
            if (exiting || Interlocked.CompareExchange(ref reading, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Host.Pulse("tray", Core.Busy ? "sending" : Program.LastPhase);
                    Host.StartGuard();
                    var commands = ReadCommands();
                    OnUi(delegate
                    {
                        if (exiting) return;
                        foreach (string action in commands)
                        {
                            if (action == "show") ShowSettings();
                            else if (action == "resume") Resume();
                            else if (action == "exit") RequestExit();
                        }
                    });
                    var next = DashboardSnapshot.Read();
                    OnUi(delegate
                    {
                        if (exiting) return;
                        snapshot = next;
                        icon.Text = !next.Enabled ? "Codex 手机通知 · 已暂停" : next.Blocked ? "Codex 手机通知 · 等待处理" : "Codex 手机通知 · 运行中";
                        pauseItem.Text = next.Enabled ? "暂停手机通知" : "恢复手机通知";
                        form.ApplySnapshot(next);
                    });
                }
                catch { Core.Log("tray_status_local_error"); OnUi(delegate { form.ShowFeedback("状态读取暂时失败，待发记录仍保留。", true); }); }
                finally { Interlocked.Exchange(ref reading, 0); }
            });
        }
        List<string> ReadCommands()
        {
            var actions = new List<string>();
            string directory = Path.Combine(Host.Root, "commands"); if (!Directory.Exists(directory)) return actions;
            int count = 0;
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            {
                if (++count > 20) break;
                try
                {
                    var command = Core.ReadObject(path); DateTime created;
                    if (!DateTime.TryParse(Installation.Text(command, "created_utc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out created)) continue;
                    double age = (DateTime.UtcNow - created.ToUniversalTime()).TotalSeconds; if (age < -5 || age > 60) continue;
                    string action = Installation.Text(command, "action");
                    if (action == "show" || action == "resume") actions.Add(action);
                    else if (action == "exit")
                    {
                        using (var current = Process.GetCurrentProcess())
                            if (Installation.Text(command, "target_pid") == current.Id.ToString() &&
                                Installation.Text(command, "target_start_ticks") == current.StartTime.ToUniversalTime().Ticks.ToString()) actions.Add("exit");
                    }
                }
                catch { Core.Log("local_command_invalid"); }
                finally { try { File.Delete(path); } catch { } }
            }
            return actions;
        }
        public void Resume()
        {
            RunAction(delegate { Core.Resume(); Program.Wake(); }, "已解除发送错误暂停。", null);
        }
        public void ShowSettings() { if (exiting) return; form.Show(); if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; form.Activate(); if (snapshot != null) form.ApplySnapshot(snapshot); RequestStatus(); }
        public void Toggle() { RunAction(delegate { var current = Core.Status(); Core.SetEnabled(!Convert.ToBoolean(current["enabled"])); Program.Wake(); }, "通知开关已更新。", null); }
        public void SetNotificationEnabled(bool enabled) { RunAction(delegate { Core.SetEnabled(enabled); Program.Wake(); }, enabled ? "手机通知已开启。" : "已暂停，新回合不会加入通知。", null); }
        public void SetStartup(bool enabled) { RunAction(delegate { Startup.Set(enabled); }, enabled ? "已启用登录自动运行。" : "已关闭登录自动运行。", null); }
        public void SetInteractionOption(string option, bool enabled)
        {
            string label = option == Core.SuggestionOption ? "输入框建议提醒" : "需要回答时提醒";
            RunAction(delegate { Core.SetOption(option, enabled); Program.Wake(); }, label + (enabled ? "已开启并保存。" : "已关闭并保存。"), null);
        }
        public void Test() { RunAction(delegate { Core.EnqueueTest("EXE托盘通知测试", "验证托盘通知发送。", "这条测试由单文件 EXE 排队，接口接受后仍请确认手机实际收到。"); Program.Wake(); }, "测试通知已排队，请查看手机。", null); }
        public void RunAction(Action work, string done, Action success)
        {
            if (exiting || Interlocked.CompareExchange(ref actionRunning, 1, 0) != 0) return;
            form.SetWorking(true); form.ShowFeedback("正在处理…", false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { work(); }
                catch (UiInputError e) { error = e.Message; }
                catch { Core.Log("settings_local_error"); error = "操作未完成，请打开数据目录查看脱敏日志。"; }
                finally
                {
                    Interlocked.Exchange(ref actionRunning, 0);
                    OnUi(delegate { form.SetWorking(false); if (error == null && success != null) success(); form.ShowFeedback(error ?? done, error != null); });
                    RequestStatus();
                }
            });
        }
        public static void Open(string file) { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { MessageBox.Show("暂时无法打开，请在文件资源管理器中查看该位置。", "Codex 手机通知"); } }
        public void RequestExit() { if (exiting) return; exiting = true; Program.Exit(this); }
        public void OnUi(Action action) { try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke(action); } catch (InvalidOperationException) { } }
        public void FinishExit() { timer.Stop(); icon.Visible = false; var ownedIcon = icon.Icon; icon.Dispose(); ownedIcon.Dispose(); form.CloseForExit = true; form.Close(); timer.Dispose(); ExitThread(); }
    }
    public static class Native { [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle); }

}
