using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CodexPushover
{
    // All modes use this installed location, regardless of shell or working directory.
    public static class Host
    {
        public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPushover");
        public static readonly string Exe = Path.Combine(Root, "CodexPushover.exe");
        static readonly List<DateTime> trayLaunches = new List<DateTime>();
        static readonly List<DateTime> guardLaunches = new List<DateTime>();

        static string Role(string role)
        {
            if (role != "tray" && role != "guard" && role != "sender" && role != "launch" && role != "startup")
                throw new ArgumentException("Unknown local process role.");
            return role;
        }
        public static string MutexName(string role)
        {
            using (SHA256 sha = SHA256.Create())
            {
                string digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToLowerInvariant()))).Replace("-", "").Substring(0, 24);
                return "Local\\CodexPushoverExe-" + digest + "-" + Role(role);
            }
        }
        static bool Flag(string name) { return File.Exists(Path.Combine(Root, name)); }
        static bool Stopped() { return Flag("APP_STOPPED") || Flag("APP_MIGRATING"); }
        static bool NoHookLaunch() { return Stopped() || Flag("DISABLED") || Flag("MAINTENANCE"); }
        static string HeartbeatPath(string role) { return Path.Combine(Root, "app-" + Role(role) + "-heartbeat.json"); }
        static object Value(Dictionary<string, object> d, string key)
        { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
        static string Text(object value) { return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture); }
        static long Number(object value) { try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); } catch { return 0; } }
        static Dictionary<string, object> ReadHeartbeat(string role)
        { try { return Core.ReadObject(HeartbeatPath(role)); } catch { return new Dictionary<string, object>(); } }
        static bool SamePath(string first, string second)
        {
            try { return String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        static bool IsThisProcess(Process process, Dictionary<string, object> record, string role)
        {
            try
            {
                return !process.HasExited && Text(Value(record, "role")) == role &&
                    SamePath(Text(Value(record, "path")), Exe) && SamePath(process.MainModule.FileName, Exe) &&
                    process.StartTime.ToUniversalTime().Ticks == Number(Value(record, "process_start_ticks"));
            }
            catch { return false; }
        }
        // The caller owns its role mutex. PID and process creation time jointly prevent PID reuse.
        public static void Pulse(string role, string phase)
        {
            Role(role);
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    if (!SamePath(process.MainModule.FileName, Exe)) { Core.Log("heartbeat_wrong_executable"); return; }
                    string stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    var value = new Dictionary<string, object>();
                    value["role"] = role; value["pid"] = process.Id;
                    value["process_start_ticks"] = process.StartTime.ToUniversalTime().Ticks;
                    value["time"] = stamp; value["utc"] = stamp;
                    value["path"] = Exe; value["phase"] = phase;
                    if (role == "tray")
                    {
                        long progress = Interlocked.Read(ref Program.SenderProgressTicks);
                        value["sender_progress_utc"] = new DateTime(progress, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
                    }
                    Core.WriteObject(HeartbeatPath(role), value);
                }
            }
            catch { Core.Log("heartbeat_write_failed"); }
        }
        public static bool IsHealthy(string role, int maxAgeSeconds)
        {
            if (maxAgeSeconds < 1) return false;
            try
            {
                Dictionary<string, object> record = ReadHeartbeat(role);
                DateTime stamp;
                if (!DateTime.TryParse(Text(Value(record, "time")), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out stamp)) return false;
                double age = (DateTime.UtcNow - stamp.ToUniversalTime()).TotalSeconds;
                if (age < -5 || age > maxAgeSeconds) return false;
                if (role == "tray")
                {
                    DateTime progress;
                    if (!DateTime.TryParse(Text(Value(record, "sender_progress_utc")), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out progress)) return false;
                    double progressAge = (DateTime.UtcNow - progress.ToUniversalTime()).TotalSeconds;
                    if (progressAge < -5 || progressAge > 120) return false;
                }
                int pid = checked((int)Number(Value(record, "pid")));
                if (pid <= 0) return false;
                using (Process process = Process.GetProcessById(pid)) return IsThisProcess(process, record, role);
            }
            catch { return false; }
        }
        static bool TryOwn(Mutex mutex)
        {
            try { return mutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }
        static bool ThrottleAllows(string role)
        {
            // The launch mutex serializes the small timestamp file, including concurrent hooks.
            string file = Path.Combine(Root, "app-" + Role(role) + "-last-launch.json");
            try
            {
                Dictionary<string, object> previous;
                try { previous = Core.ReadObject(file); }
                catch { previous = new Dictionary<string, object>(); Core.Log("launch_throttle_invalid"); }
                DateTime stamp;
                if (DateTime.TryParse(Text(Value(previous, "time")), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out stamp))
                {
                    double age = (DateTime.UtcNow - stamp.ToUniversalTime()).TotalSeconds;
                    if (age >= -5 && age < 30) return false;
                }
                var record = new Dictionary<string, object>();
                record["time"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                Core.WriteObject(file, record);
                return true;
            }
            catch { Core.Log("launch_throttle_failed"); return false; }
        }
        static bool StartMode(string role, bool useShell)
        {
            if (Stopped() || !File.Exists(Exe)) return false;
            try
            {
                using (Process child = Process.Start(new ProcessStartInfo {
                    FileName = Exe, Arguments = "--" + Role(role) + (role == "tray" ? " --background" : ""), WorkingDirectory = Root,
                    UseShellExecute = useShell, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                })) { }
                Core.Log(role == "tray" ? "tray_launch_requested" : "guard_launch_requested");
                return true;
            }
            catch { Core.Log("local_process_launch_failed"); return false; }
        }
        // No IPC waits, network, or child waits. Only bounded local checks and launch requests.
        public static void EnsureRunning()
        {
            if (NoHookLaunch()) return;
            using (var mutex = new Mutex(false, MutexName("launch")))
            {
                bool owned = false;
                try
                {
                    owned = TryOwn(mutex);
                    if (!owned || NoHookLaunch()) return;
                    if (!IsHealthy("tray", 120) && ThrottleAllows("tray")) StartMode("tray", true);
                    if (!IsHealthy("guard", 120) && ThrottleAllows("guard")) StartMode("guard", true);
                }
                catch { Core.Log("hook_launch_check_failed"); }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
        // UI startup and its timer may call this. A paused tray may retain its guard.
        public static void StartGuard()
        {
            if (Stopped() || Flag("MAINTENANCE")) return;
            using (var mutex = new Mutex(false, MutexName("launch")))
            {
                bool owned = false;
                try
                {
                    owned = TryOwn(mutex);
                    if (!owned || Stopped() || IsHealthy("guard", 120)) return;
                    if (ThrottleAllows("guard") && UnderRestartLimit(guardLaunches))
                    {
                        StopStaleRole("guard");
                        StartMode("guard", true);
                    }
                }
                catch { Core.Log("guard_launch_check_failed"); }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
        static bool StopStaleRole(string role)
        {
            try
            {
                var record = ReadHeartbeat(role);
                int pid = checked((int)Number(Value(record, "pid")));
                if (pid <= 0) return false;
                using (Process process = Process.GetProcessById(pid))
                {
                    if (!IsThisProcess(process, record, role) || IsHealthy(role, 120)) return false;
                    // Recheck after opening the process, immediately before Kill.
                    var latest = ReadHeartbeat(role);
                    if (Number(Value(latest, "pid")) != pid || !IsThisProcess(process, latest, role) ||
                        IsHealthy(role, 120) || Stopped() || Flag("MAINTENANCE")) return false;
                    DateTime uiTime, progressTime;
                    bool heartbeatStale = DateTime.TryParse(Text(Value(latest, "time")), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out uiTime) && (DateTime.UtcNow - uiTime.ToUniversalTime()).TotalSeconds > 120;
                    bool senderStale = role == "tray" && DateTime.TryParse(Text(Value(latest, "sender_progress_utc")), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out progressTime) && (DateTime.UtcNow - progressTime.ToUniversalTime()).TotalSeconds > 120;
                    // Bad or future timestamps are not proof of a hung process. Never kill on a clock rollback.
                    if (!heartbeatStale && !senderStale) return false;
                    process.Kill();
                    Core.Log(role == "tray" ? "stale_tray_stopped" : "stale_guard_stopped");
                    return true;
                }
            }
            catch { return false; }
        }
        static bool UnderRestartLimit(List<DateTime> launches)
        {
            DateTime now = DateTime.UtcNow;
            launches.RemoveAll(delegate(DateTime time) { return (now - time).TotalSeconds >= 300; });
            if (launches.Count >= 6) return false;
            launches.Add(now); return true;
        }
        public static void RunGuard()
        {
            Core.Initialize(Root);
            using (var mutex = new Mutex(false, MutexName("guard")))
            {
                bool owned = false;
                try
                {
                    owned = TryOwn(mutex);
                    if (!owned) return;
                    Core.Log("guard_started");
                    while (!Stopped())
                    {
                        string phase = "watching";
                        try
                        {
                            if (Flag("MAINTENANCE")) phase = "maintenance";
                            else if (!IsHealthy("tray", 120))
                            {
                                if (UnderRestartLimit(trayLaunches))
                                {
                                    StopStaleRole("tray");
                                    if (StartMode("tray", false)) phase = "tray_start_requested";
                                    else phase = "tray_start_failed";
                                }
                                else phase = "restart_backoff";
                            }
                            Pulse("guard", phase);
                        }
                        catch { Core.Log("guard_local_check_failed"); Pulse("guard", "local_check_failed"); }
                        for (int part = 0; part < 20 && !Stopped(); part++) Thread.Sleep(500);
                    }
                    Pulse("guard", "stopped");
                    Core.Log("guard_stopped");
                }
                catch { Core.Log("guard_failed"); }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
    }

    public static class Startup
    {
        const string Name = "Codex 手机通知.lnk";
        const string Arguments = "--tray";
        const string Description = "Codex 手机通知：登录后启动托盘和持久化发送程序";
        public static string StartupFolder { get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); } }
        static string StartupLink { get { return Path.Combine(StartupFolder, Name); } }
        static bool SameTarget(string target)
        {
            try { return String.Equals(Path.GetFullPath(target), Path.GetFullPath(Host.Exe), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        static void Release(object value)
        { try { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { } }
        static bool Owned(string file)
        {
            if (!File.Exists(file)) return false;
            object shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                shortcut = ((dynamic)shell).CreateShortcut(file);
                return SameTarget(Convert.ToString(((dynamic)shortcut).TargetPath, CultureInfo.InvariantCulture)) &&
                    String.Equals(Convert.ToString(((dynamic)shortcut).Arguments, CultureInfo.InvariantCulture), Arguments, StringComparison.Ordinal);
            }
            catch { return false; }
            finally { Release(shortcut); Release(shell); }
        }
        static void Create(string file)
        {
            if (File.Exists(file) && !Owned(file)) throw new IOException("An unrelated shortcut already uses this name; it was preserved.");
            string directory = Path.GetDirectoryName(file);
            Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory, ".CodexPushover-" + Guid.NewGuid().ToString("N") + ".lnk");
            object shell = null, shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                shortcut = ((dynamic)shell).CreateShortcut(temp);
                ((dynamic)shortcut).TargetPath = Host.Exe;
                ((dynamic)shortcut).Arguments = Arguments;
                ((dynamic)shortcut).WorkingDirectory = Host.Root;
                ((dynamic)shortcut).Description = Description;
                ((dynamic)shortcut).IconLocation = Host.Exe + ",0";
                ((dynamic)shortcut).WindowStyle = 7;
                ((dynamic)shortcut).Save();
                Release(shortcut); shortcut = null; Release(shell); shell = null;
                if (File.Exists(file))
                {
                    if (!Owned(file)) throw new IOException("The shortcut changed during registration; it was preserved.");
                    File.Replace(temp, file, null);
                }
                else File.Move(temp, file);
            }
            finally
            {
                Release(shortcut); Release(shell);
                try { File.Delete(temp); } catch { }
            }
        }
        static void Remove(string file)
        { if (Owned(file)) File.Delete(file); }
        public static void Set(bool enable)
        {
            using (var mutex = new Mutex(false, Host.MutexName("startup")))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(3000); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("Startup settings are being updated; try again shortly.");
                    if (enable) Create(StartupLink); else Remove(StartupLink);
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
        public static bool IsConfigured() { return Owned(StartupLink); }
        public static void CreateLauncherShortcuts()
        {
            Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name));
            Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name));
        }
        public static void RemoveOwnedShortcuts()
        {
            Set(false);
            Remove(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name));
            Remove(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name));
        }
    }
}
