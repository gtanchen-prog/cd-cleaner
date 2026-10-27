using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CdCleaner
{
    // ============================================================
    //  程序入口
    // ============================================================
    static class Program
    {
        // 版本号唯一来源：所有标题/托盘/日志都引用这里，避免多处硬编码不一致
        public const string AppVersion = "1.5";

        public static bool IsAdmin()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                SelfTest.Run();
                return;
            }
            if (args != null && args.Length > 0 && string.Equals(args[0], "--smoketest", StringComparison.OrdinalIgnoreCase))
            {
                SmokeTest.Run();
                return;
            }
            if (args != null && args.Length > 0 && string.Equals(args[0], "--auto", StringComparison.OrdinalIgnoreCase))
            {
                AutoRunner.Run();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                try { MessageBox.Show(e.Exception.Message, "C盘清理助手", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            };
            if (!IsAdmin())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                    return;
                }
                catch { }
            }
            Application.Run(new MainForm());
        }
    }

    // ============================================================
    //  原生 API
    // ============================================================
    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        [DllImport("shell32.dll")]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        public const uint SHERB_NOCONFIRMATION = 0x1;
        public const uint SHERB_NOPROGRESSUI = 0x2;
        public const uint SHERB_NOSOUND = 0x4;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        public const uint SWP_NOSIZE = 0x1;
        public const uint SWP_NOMOVE = 0x2;
        public const uint SWP_NOZORDER = 0x4;
        public const uint SWP_NOACTIVATE = 0x10;
        public const uint SWP_FRAMECHANGED = 0x20;

        public const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void SetDarkTitle(IntPtr hwnd, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);
            }
            catch { }
        }

        public static void SetDarkTheme(IntPtr hwnd, bool dark)
        {
            try { SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        public static bool IsDarkTitle(IntPtr hwnd)
        {
            try
            {
                int v;
                if (DwmGetWindowAttribute(hwnd, 20, out v, 4) == 0) return v != 0;
                if (DwmGetWindowAttribute(hwnd, 19, out v, 4) == 0) return v != 0;
            }
            catch { }
            return false;
        }

        // 窗口隐藏/恢复后 DWM 会重置标题栏颜色，需要重新设置并强制重绘边框
        public static void ReapplyFrame(IntPtr hwnd, bool dark)
        {
            try
            {
                SetDarkTitle(hwnd, dark);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { }
        }
    }

    // ============================================================
    //  主题
    // ============================================================
    enum ThemeMode { System = 0, Light = 1, Dark = 2 }

    class Palette
    {
        public Color FormBack, CardBack, CardBorder, Text, TextDim, LogBack, LogText, InputBack, InputText, ButtonBack, ButtonText, AccentBack, AccentText, DangerBack, DangerText, OkText, WarnText, ErrText;

        public static Palette Light()
        {
            Palette p = new Palette();
            p.FormBack = Color.FromArgb(244, 246, 249);
            p.CardBack = Color.White;
            p.CardBorder = Color.FromArgb(214, 219, 226);
            p.Text = Color.FromArgb(32, 32, 32);
            p.TextDim = Color.FromArgb(112, 112, 112);
            p.LogBack = Color.FromArgb(250, 250, 250);
            p.LogText = Color.FromArgb(42, 42, 42);
            p.InputBack = Color.White;
            p.InputText = Color.FromArgb(32, 32, 32);
            p.ButtonBack = Color.FromArgb(240, 242, 245);
            p.ButtonText = Color.FromArgb(40, 40, 40);
            p.AccentBack = Color.FromArgb(46, 125, 80);
            p.AccentText = Color.White;
            p.DangerBack = Color.FromArgb(190, 55, 55);
            p.DangerText = Color.White;
            p.OkText = Color.FromArgb(24, 120, 64);
            p.WarnText = Color.FromArgb(170, 105, 0);
            p.ErrText = Color.FromArgb(180, 30, 30);
            return p;
        }

        public static Palette Dark()
        {
            Palette p = new Palette();
            p.FormBack = Color.FromArgb(28, 30, 34);
            p.CardBack = Color.FromArgb(38, 41, 47);
            p.CardBorder = Color.FromArgb(64, 69, 78);
            p.Text = Color.FromArgb(236, 236, 236);
            p.TextDim = Color.FromArgb(152, 158, 165);
            p.LogBack = Color.FromArgb(20, 22, 25);
            p.LogText = Color.FromArgb(205, 210, 215);
            p.InputBack = Color.FromArgb(48, 52, 59);
            p.InputText = Color.FromArgb(236, 236, 236);
            p.ButtonBack = Color.FromArgb(54, 59, 67);
            p.ButtonText = Color.FromArgb(236, 236, 236);
            p.AccentBack = Color.FromArgb(47, 163, 107);
            p.AccentText = Color.White;
            p.DangerBack = Color.FromArgb(201, 74, 74);
            p.DangerText = Color.White;
            p.OkText = Color.FromArgb(90, 200, 140);
            p.WarnText = Color.FromArgb(230, 170, 70);
            p.ErrText = Color.FromArgb(235, 110, 110);
            return p;
        }
    }

    static class Settings
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CdCleaner"); }
        }
        static string IniFile { get { return Path.Combine(Dir, "settings.ini"); } }

        static Dictionary<string, string> ReadAll()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(IniFile)) return map;
                foreach (string line in File.ReadAllLines(IniFile))
                {
                    int idx = line.IndexOf('=');
                    if (idx > 0) map[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }
            }
            catch { }
            return map;
        }

        static void WriteAll(Dictionary<string, string> map)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in map) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(IniFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static void Set(string key, string value)
        {
            Dictionary<string, string> map = ReadAll();
            map[key] = value;
            WriteAll(map);
        }

        static int GetInt(string key, int def)
        {
            Dictionary<string, string> map = ReadAll();
            if (map.ContainsKey(key))
            {
                int v;
                if (int.TryParse(map[key], out v)) return v;
            }
            return def;
        }

        static string GetString(string key, string def)
        {
            Dictionary<string, string> map = ReadAll();
            return map.ContainsKey(key) ? map[key] : def;
        }

        public static ThemeMode LoadTheme()
        {
            int v = GetInt("theme", 0);
            if (v < 0 || v > 2) v = 0;
            return (ThemeMode)v;
        }

        public static void SaveTheme(ThemeMode m)
        {
            Set("theme", ((int)m).ToString());
        }

        public static int LoadPlanDay()
        {
            int d = GetInt("planDay", 0);
            if (d < 0 || d > 6) d = 0;
            return d;
        }

        public static string LoadPlanTime()
        {
            string t = GetString("planTime", "12:00");
            DateTime dt;
            if (!DateTime.TryParse(t, out dt)) t = "12:00";
            return dt.ToString("HH:mm");
        }

        public static void SavePlan(int day, string time)
        {
            Dictionary<string, string> map = ReadAll();
            map["planDay"] = day.ToString();
            map["planTime"] = time;
            WriteAll(map);
        }
    }

    static class Log
    {
        static string _path;
        public static string Path
        {
            get
            {
                if (_path != null) return _path;
                try
                {
                    string p = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.ExecutablePath), "清理日志.txt");
                    File.AppendAllText(p, "");
                    _path = p;
                }
                catch
                {
                    try
                    {
                        string d = Settings.Dir;
                        Directory.CreateDirectory(d);
                        _path = System.IO.Path.Combine(d, "清理日志.txt");
                    }
                    catch { _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CdCleaner_log.txt"); }
                }
                return _path;
            }
        }

        public static void Write(string line)
        {
            try { File.AppendAllText(Path, line + "\r\n", Encoding.UTF8); } catch { }
        }
    }

    // ============================================================
    //  引擎
    // ============================================================
    static class Engine
    {
        static readonly string[] BlockedFragments = new string[]
        {
            "\\windows\\system32", "\\windows\\syswow64", "\\windows\\winsxs",
            "\\windows\\installer", "\\windows\\servicing", "\\system volume information"
        };
        static readonly string[] BlockedDirs = new string[]
        {
            "\\documents", "\\desktop", "\\downloads", "\\pictures", "\\videos", "\\music", "\\onedrive"
        };

        public static bool IsSafePath(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string f = full.TrimEnd('\\');
            if (f.Length == 2 && f[1] == ':') return false;
            string lower = f.ToLowerInvariant();
            foreach (string b in BlockedDirs)
            {
                if (lower.EndsWith(b) || lower.Contains(b + "\\")) return false;
            }
            string sysRoot = (Environment.GetEnvironmentVariable("SystemRoot") ?? "").ToLowerInvariant().TrimEnd('\\');
            string userProf = (Environment.GetEnvironmentVariable("USERPROFILE") ?? "").ToLowerInvariant().TrimEnd('\\');
            string localApp = (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? "").ToLowerInvariant().TrimEnd('\\');
            string programData = (Environment.GetEnvironmentVariable("ProgramData") ?? "").ToLowerInvariant().TrimEnd('\\');
            if (sysRoot.Length > 0 && lower == sysRoot) return false;
            if (userProf.Length > 0 && lower == userProf) return false;
            if (localApp.Length > 0 && lower == localApp) return false;
            if (programData.Length > 0 && lower == programData) return false;
            string exeDir = (System.IO.Path.GetDirectoryName(Application.ExecutablePath) ?? "").ToLowerInvariant().TrimEnd('\\');
            if (exeDir.Length > 0 && lower.StartsWith(exeDir + "\\")) return false;
            foreach (string frag in BlockedFragments)
            {
                if (lower.Contains(frag)) return false;
            }
            return true;
        }

        public static long SizeOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            try
            {
                if (File.Exists(path))
                {
                    FileInfo fi = new FileInfo(path);
                    return fi.Length;
                }
                if (!Directory.Exists(path)) return 0;
                long total = 0;
                Stack<string> stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0)
                {
                    string d = stack.Pop();
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(d))
                        {
                            try { total += new FileInfo(f).Length; } catch { }
                        }
                        foreach (string sd in Directory.EnumerateDirectories(d)) stack.Push(sd);
                    }
                    catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        static void DeleteTree(string dir)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                }
            }
            catch { }
            try
            {
                foreach (string sd in Directory.GetDirectories(dir))
                {
                    DeleteTree(sd);
                    try { Directory.Delete(sd, false); } catch { }
                }
            }
            catch { }
        }

        public static void Clear(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return; }
            if (!IsSafePath(full)) return;
            if (File.Exists(full))
            {
                try { File.SetAttributes(full, FileAttributes.Normal); File.Delete(full); } catch { }
                return;
            }
            if (!Directory.Exists(full)) return;
            DeleteTree(full);
            try { Directory.Delete(full, false); } catch { }
            try { Directory.CreateDirectory(full); } catch { }
        }
    }

    // ============================================================
    //  清理目标
    // ============================================================
    class CleanContext
    {
        public List<string> Drives = new List<string>();
        public List<string> LocalRoots = new List<string>();
    }

    class Target
    {
        public string Id;
        public string Name;
        public string Category;
        public bool Sys;
        public Func<CleanContext, List<string>> Resolve;

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1073741824L) return ((double)bytes / 1073741824.0).ToString("N2") + " GB";
            if (bytes >= 1048576L) return ((double)bytes / 1048576.0).ToString("N1") + " MB";
            if (bytes >= 1024L) return ((double)bytes / 1024.0).ToString("N0") + " KB";
            return bytes.ToString() + " B";
        }
    }

    static class Targets
    {
        public static List<Target> Build()
        {
            List<Target> list = new List<Target>();

            list.Add(Make("sys_temp", "系统临时文件", "系统缓存", true, delegate(CleanContext c)
            {
                return new List<string> { Path.Combine(Environment.GetEnvironmentVariable("SystemRoot"), "Temp") };
            }));
            list.Add(Make("win_update", "Windows 更新下载缓存", "系统缓存", true, delegate(CleanContext c)
            {
                string sr = Environment.GetEnvironmentVariable("SystemRoot");
                return new List<string>
                {
                    Path.Combine(sr, "SoftwareDistribution", "Download"),
                    Path.Combine(sr, "SoftwareDistribution", "DeliveryOptimization")
                };
            }));
            list.Add(Make("prefetch", "预读取缓存 (Prefetch)", "系统缓存", true, delegate(CleanContext c)
            {
                return new List<string> { Path.Combine(Environment.GetEnvironmentVariable("SystemRoot"), "Prefetch") };
            }));
            list.Add(Make("wer_sys", "系统错误报告 (WER)", "系统缓存", true, delegate(CleanContext c)
            {
                return new List<string> { Path.Combine(Environment.GetEnvironmentVariable("ProgramData"), "Microsoft", "Windows", "WER") };
            }));
            list.Add(Make("dumps", "内存转储 (dmp/Minidump)", "系统缓存", true, delegate(CleanContext c)
            {
                string sr = Environment.GetEnvironmentVariable("SystemRoot");
                return new List<string>
                {
                    Path.Combine(sr, "memory.dmp"),
                    Path.Combine(sr, "Minidump"),
                    Path.Combine(sr, "LiveKernelReports")
                };
            }));
            list.Add(Make("user_temp", "用户临时文件", "用户缓存", false, delegate(CleanContext c)
            {
                List<string> r = new List<string>();
                foreach (string ld in c.LocalRoots) r.Add(Path.Combine(ld, "Temp"));
                return r;
            }));
            list.Add(Make("browser", "浏览器缓存", "用户缓存", false, delegate(CleanContext c)
            {
                return BrowserCachePaths(c.LocalRoots);
            }));
            list.Add(Make("thumb", "缩略图/图标缓存", "用户缓存", false, delegate(CleanContext c)
            {
                return ThumbnailFiles(c.LocalRoots);
            }));
            list.Add(Make("d3d", "DirectX 着色器缓存", "用户缓存", false, delegate(CleanContext c)
            {
                List<string> r = new List<string>();
                foreach (string ld in c.LocalRoots) r.Add(Path.Combine(ld, "D3DSCache"));
                return r;
            }));
            list.Add(Make("crashdump", "应用崩溃转储 (CrashDumps)", "用户缓存", false, delegate(CleanContext c)
            {
                List<string> r = new List<string>();
                foreach (string ld in c.LocalRoots) r.Add(Path.Combine(ld, "CrashDumps"));
                return r;
            }));
            list.Add(Make("wer_user", "用户错误报告 (WER)", "用户缓存", false, delegate(CleanContext c)
            {
                List<string> r = new List<string>();
                foreach (string ld in c.LocalRoots) r.Add(Path.Combine(ld, "Microsoft", "Windows", "WER"));
                return r;
            }));
            list.Add(Make("appcache", "应用缓存 (LocalCache)", "用户缓存", false, delegate(CleanContext c)
            {
                List<string> r = new List<string>();
                foreach (string ld in c.LocalRoots)
                {
                    string pk = Path.Combine(ld, "Packages");
                    if (!Directory.Exists(pk)) continue;
                    try
                    {
                        foreach (string d in Directory.GetDirectories(pk))
                        {
                            string lc = Path.Combine(d, "LocalCache");
                            if (Directory.Exists(lc)) r.Add(lc);
                        }
                    }
                    catch { }
                }
                return r;
            }));
            list.Add(Make("drive_temp", "各盘根目录 Temp", "驱动器", false, delegate(CleanContext c)
            {
                List<string> r = new List<string>();
                foreach (string d in c.Drives) r.Add(d + ":\\Temp");
                return r;
            }));
            return list;
        }

        static Target Make(string id, string name, string cat, bool sys, Func<CleanContext, List<string>> resolver)
        {
            Target t = new Target();
            t.Id = id;
            t.Name = name;
            t.Category = cat;
            t.Sys = sys;
            t.Resolve = resolver;
            return t;
        }

        static List<string> BrowserCachePaths(List<string> localRoots)
        {
            List<string> result = new List<string>();
            string[] chromiumRoots = new string[]
            {
                "Google\\Chrome\\User Data",
                "Microsoft\\Edge\\User Data",
                "BraveSoftware\\Brave-Browser\\User Data",
                "Chromium\\User Data",
                "Vivaldi\\User Data"
            };
            string[] operaRoots = new string[] { "Opera Software\\Opera Stable", "Opera Software\\Opera GX Stable" };
            string[] cacheNames = new string[] { "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "DawnGraphiteCache", "DawnWebGPUCache" };
            foreach (string ld in localRoots)
            {
                foreach (string rel in chromiumRoots)
                {
                    string ud = Path.Combine(ld, rel);
                    if (!Directory.Exists(ud)) continue;
                    foreach (string n in cacheNames)
                    {
                        string p = Path.Combine(ud, n);
                        if (Directory.Exists(p)) result.Add(p);
                    }
                    try
                    {
                        foreach (string prof in Directory.GetDirectories(ud))
                        {
                            foreach (string n in new string[] { "Cache", "Code Cache", "GPUCache" })
                            {
                                string p = Path.Combine(prof, n);
                                if (Directory.Exists(p)) result.Add(p);
                            }
                            string sw = Path.Combine(prof, "Service Worker", "CacheStorage");
                            if (Directory.Exists(sw)) result.Add(sw);
                        }
                    }
                    catch { }
                }
                foreach (string rel in operaRoots)
                {
                    string ud = Path.Combine(ld, rel);
                    if (!Directory.Exists(ud)) continue;
                    foreach (string n in new string[] { "Cache", "Code Cache", "GPUCache" })
                    {
                        string p = Path.Combine(ud, n);
                        if (Directory.Exists(p)) result.Add(p);
                    }
                }
                string ffProfiles = Path.Combine(ld, "Mozilla", "Firefox", "Profiles");
                if (Directory.Exists(ffProfiles))
                {
                    try
                    {
                        foreach (string prof in Directory.GetDirectories(ffProfiles))
                        {
                            foreach (string n in new string[] { "cache2", "startupCache" })
                            {
                                string p = Path.Combine(prof, n);
                                if (Directory.Exists(p)) result.Add(p);
                            }
                        }
                    }
                    catch { }
                }
            }
            return result;
        }

        static List<string> ThumbnailFiles(List<string> localRoots)
        {
            List<string> result = new List<string>();
            foreach (string ld in localRoots)
            {
                string exp = Path.Combine(ld, "Microsoft", "Windows", "Explorer");
                if (!Directory.Exists(exp)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(exp))
                    {
                        string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
                        if (n.StartsWith("thumbcache_") || n.StartsWith("iconcache_")) result.Add(f);
                    }
                }
                catch { }
            }
            return result;
        }
    }

    // ============================================================
    //  后台工作
    // ============================================================
    class Msg
    {
        public string Kind;
        public string Text;
        public string Id;
        public long Size;
        public int Percent;
    }

    static class Workers
    {
        public static void Scan(CleanContext ctx, List<Target> targets, IProgress<Msg> prog, CancellationToken ct)
        {
            int n = 0;
            foreach (Target t in targets)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                Report(prog, "status", "扫描中 " + n + "/" + targets.Count + "：" + t.Name, null, 0, 0);
                List<string> paths = SafeResolve(t, ctx);
                long bytes = 0;
                foreach (string p in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    bytes += Engine.SizeOf(p);
                }
                Report(prog, "size", Target.FormatSize(bytes), t.Id, bytes, 0);
                Report(prog, "log", "扫描 " + t.Name + "：" + Target.FormatSize(bytes) + "（" + paths.Count + " 个位置）", null, 0, 0);
                Report(prog, "percent", "", null, 0, 100 * n / Math.Max(1, targets.Count));
            }
        }

        public static long Clean(CleanContext ctx, List<Target> targets, IProgress<Msg> prog, CancellationToken ct, bool isAdmin)
        {
            long total = 0;
            int done = 0;
            foreach (Target t in targets)
            {
                if (ct.IsCancellationRequested) break;
                if (t.Sys && !isAdmin)
                {
                    Report(prog, "log", "跳过 " + t.Name + "：需要管理员权限", null, 0, 0);
                    done++;
                    Report(prog, "percent", "", null, 0, 100 * done / Math.Max(1, targets.Count));
                    continue;
                }
                List<string> paths = SafeResolve(t, ctx);
                if (paths.Count == 0)
                {
                    Report(prog, "log", t.Name + "：无内容，跳过", null, 0, 0);
                    done++;
                    Report(prog, "percent", "", null, 0, 100 * done / Math.Max(1, targets.Count));
                    continue;
                }
                long before = 0;
                foreach (string p in paths) before += Engine.SizeOf(p);
                Report(prog, "log", "开始清理 " + t.Name + "（" + Target.FormatSize(before) + "，" + paths.Count + " 个位置）", null, 0, 0);
                Report(prog, "status", "正在清理：" + t.Name, null, 0, 0);
                foreach (string p in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    Engine.Clear(p);
                }
                long after = 0;
                foreach (string p in paths) after += Engine.SizeOf(p);
                long freed = before - after;
                if (freed < 0) freed = 0;
                total += freed;
                Report(prog, "size", "已清 " + Target.FormatSize(freed), t.Id, freed, 0);
                Report(prog, "log", "完成 " + t.Name + "：释放 " + Target.FormatSize(freed), null, 0, 0);
                Report(prog, "result", t.Name, t.Id, freed, 0);
                done++;
                Report(prog, "percent", "", null, 0, 100 * done / Math.Max(1, targets.Count));
            }
            return total;
        }

        static List<string> SafeResolve(Target t, CleanContext ctx)
        {
            List<string> result = new List<string>();
            try
            {
                List<string> raw = t.Resolve(ctx);
                if (raw == null) return result;
                foreach (string p in raw)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    if (!Directory.Exists(p) && !File.Exists(p)) continue;
                    string full;
                    try { full = Path.GetFullPath(p).TrimEnd('\\'); } catch { continue; }
                    if (!Engine.IsSafePath(full)) continue;
                    if (!result.Contains(full)) result.Add(full);
                }
            }
            catch { }
            return result;
        }

        static void Report(IProgress<Msg> prog, string kind, string text, string id, long size, int percent)
        {
            if (prog == null) return;
            Msg m = new Msg();
            m.Kind = kind;
            m.Text = text;
            m.Id = id;
            m.Size = size;
            m.Percent = percent;
            try { prog.Report(m); } catch { }
        }
    }

    // ============================================================
    //  回收站
    // ============================================================
    class BinStat
    {
        public string Drive;
        public long Size;
        public long Count;
    }

    static class RecycleBin
    {
        public static bool Query(string drive, out long size, out long count)
        {
            size = 0;
            count = 0;
            try
            {
                Native.SHQUERYRBINFO info = new Native.SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
                int hr = Native.SHQueryRecycleBin(drive + ":\\", ref info);
                size = info.i64Size;
                count = info.i64NumItems;
                return hr == 0;
            }
            catch { return false; }
        }

        public static void Empty(IntPtr owner, string drive)
        {
            try
            {
                Native.SHEmptyRecycleBin(owner, drive + ":\\", Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            }
            catch { }
        }
    }

    // ============================================================
    //  计划任务（每周自动清理）
    // ============================================================
    static class PlanTask
    {
        public const string TaskName = "CdCleanerWeeklyClean";
        static readonly string[] Days = new string[] { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };

        public static bool Exists()
        {
            return RunSchTasks("/Query /TN \"" + TaskName + "\"", 15000);
        }

        public static bool Create(int dayIndex, string time)
        {
            if (dayIndex < 0 || dayIndex > 6) dayIndex = 0;
            string exe = Application.ExecutablePath;
            string tr = "\"" + exe + "\" --auto";
            string args = "/Create /TN \"" + TaskName + "\" /TR \"" + tr.Replace("\"", "\\\"") + "\" /SC WEEKLY /D " + Days[dayIndex] + " /ST " + time + " /RL HIGHEST /F";
            return RunSchTasks(args, 30000);
        }

        public static bool Delete()
        {
            return RunSchTasks("/Delete /TN \"" + TaskName + "\" /F", 15000);
        }

        static bool RunSchTasks(string args, int waitMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("schtasks.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(waitMs);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }

    // ============================================================
    //  无人值守自动清理
    // ============================================================
    static class AutoRunner
    {
        public static void Run()
        {
            try
            {
                List<Target> targets = Targets.Build();
                CleanContext ctx = new CleanContext();
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady) continue;
                        if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                        ctx.Drives.Add(d.Name.Substring(0, 1));
                    }
                    catch { }
                }
                ctx.LocalRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                if (Program.IsAdmin())
                {
                    try
                    {
                        string usersRoot = Path.GetDirectoryName(Environment.GetEnvironmentVariable("USERPROFILE"));
                        foreach (string u in Directory.GetDirectories(usersRoot))
                        {
                            string p = Path.Combine(u, "AppData", "Local");
                            if (Directory.Exists(p) && !ctx.LocalRoots.Contains(p)) ctx.LocalRoots.Add(p);
                        }
                    }
                    catch { }
                }
                Log.Write("=== 自动清理开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                long total = Workers.Clean(ctx, targets, null, CancellationToken.None, Program.IsAdmin());
                Log.Write("=== 自动清理结束，共释放 " + Target.FormatSize(total) + " ===");
            }
            catch (Exception ex)
            {
                try { Log.Write("自动清理失败：" + ex.Message); } catch { }
            }
        }
    }

    // ============================================================
    //  卡片容器（可主题化边框）
    // ============================================================
    class Card : Panel
    {
        public string TitleText = "";
        public Color BorderColor = Color.Transparent;
        public Color TitleColor = Color.Black;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (BorderColor.A != 0)
            {
                using (Pen pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawRectangle(pen, 0, 6, Width - 1, Height - 7);
                }
                if (!string.IsNullOrEmpty(TitleText))
                {
                    Size size = TextRenderer.MeasureText(TitleText, Font);
                    using (SolidBrush brush = new SolidBrush(BackColor))
                    {
                        e.Graphics.FillRectangle(brush, 12, 0, size.Width + 4, 14);
                    }
                    TextRenderer.DrawText(e.Graphics, TitleText, Font, new Point(16, 0), TitleColor);
                }
            }
        }
    }

    // ============================================================
    //  回收站确认窗口
    // ============================================================
    class ConfirmBinForm : Form
    {
        CheckBox chkAck;
        Button btnOk;

        public ConfirmBinForm(List<BinStat> stats, bool isAdmin, Palette p)
        {
            Text = "确认清空回收站";
            ClientSize = new Size(500, 350);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = p.CardBack;

            Label lblIntro = new Label();
            lblIntro.Text = "以下驱动器的回收站将被清空：";
            lblIntro.ForeColor = p.Text;
            lblIntro.SetBounds(18, 14, 460, 20);

            TextBox txt = new TextBox();
            txt.Multiline = true;
            txt.ReadOnly = true;
            txt.ScrollBars = ScrollBars.Vertical;
            txt.BorderStyle = BorderStyle.FixedSingle;
            txt.BackColor = p.InputBack;
            txt.ForeColor = p.InputText;
            txt.SetBounds(18, 40, 464, 136);
            StringBuilder sb = new StringBuilder();
            foreach (BinStat s in stats)
            {
                sb.AppendLine(s.Drive + ":    约 " + s.Count.ToString("N0") + " 个文件，" + Target.FormatSize(s.Size));
            }
            txt.Text = sb.ToString().TrimEnd();

            Label lblWarn = new Label();
            lblWarn.Text = isAdmin
                ? "注意：删除后无法恢复。管理员模式下会同时清除所选驱动器上所有用户的回收站内容。"
                : "注意：删除后无法恢复。部分其他用户的回收站可能需要管理员权限才能清空。";
            lblWarn.ForeColor = p.DangerBack;
            lblWarn.SetBounds(18, 188, 464, 44);

            chkAck = new CheckBox();
            chkAck.Text = "我明白此操作不可恢复";
            chkAck.ForeColor = p.Text;
            chkAck.SetBounds(18, 244, 300, 24);
            chkAck.CheckedChanged += delegate { btnOk.Enabled = chkAck.Checked; };

            btnOk = new Button();
            btnOk.Text = "清空回收站";
            btnOk.SetBounds(250, 292, 120, 34);
            btnOk.Enabled = false;
            btnOk.DialogResult = DialogResult.OK;
            btnOk.FlatStyle = FlatStyle.Flat;
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.BackColor = p.DangerBack;
            btnOk.ForeColor = p.DangerText;

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(380, 292, 100, 34);
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderColor = p.CardBorder;
            btnCancel.BackColor = p.ButtonBack;
            btnCancel.ForeColor = p.ButtonText;

            AcceptButton = btnCancel;
            CancelButton = btnCancel;

            Controls.Add(lblIntro);
            Controls.Add(txt);
            Controls.Add(lblWarn);
            Controls.Add(chkAck);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
        }
    }

    // ============================================================
    //  定时自动清理设置窗口
    // ============================================================
    class PlanForm : Form
    {
        CheckBox chkEnable;
        ComboBox cmbDay;
        DateTimePicker dtTime;

        public PlanForm(Palette p)
        {
            Text = "定时自动清理";
            ClientSize = new Size(430, 258);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = p.CardBack;

            Label lblInfo = new Label();
            lblInfo.Text = "在指定时间自动清理缓存，不需要打开界面，静默执行并写入日志。\r\n不会清空回收站，也不会修改任何系统设置。";
            lblInfo.ForeColor = p.TextDim;
            lblInfo.SetBounds(18, 14, 394, 42);

            chkEnable = new CheckBox();
            chkEnable.Text = "启用每周自动清理";
            chkEnable.ForeColor = p.Text;
            chkEnable.SetBounds(18, 64, 240, 24);
            chkEnable.Checked = PlanTask.Exists();
            chkEnable.CheckedChanged += delegate { UpdateEnabled(); };

            Label lblDay = new Label();
            lblDay.Text = "星期：";
            lblDay.ForeColor = p.Text;
            lblDay.SetBounds(18, 102, 60, 22);

            cmbDay = new ComboBox();
            cmbDay.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDay.FlatStyle = FlatStyle.Flat;
            cmbDay.BackColor = p.InputBack;
            cmbDay.ForeColor = p.InputText;
            cmbDay.SetBounds(80, 100, 120, 24);
            cmbDay.Items.AddRange(new object[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" });
            cmbDay.SelectedIndex = Settings.LoadPlanDay();

            Label lblTime = new Label();
            lblTime.Text = "时间：";
            lblTime.ForeColor = p.Text;
            lblTime.SetBounds(222, 102, 52, 22);

            dtTime = new DateTimePicker();
            dtTime.Format = DateTimePickerFormat.Time;
            dtTime.ShowUpDown = true;
            dtTime.SetBounds(276, 100, 118, 24);
            DateTime parsed;
            if (DateTime.TryParse("2000-01-01 " + Settings.LoadPlanTime(), out parsed)) dtTime.Value = parsed;
            else dtTime.Value = new DateTime(2000, 1, 1, 12, 0, 0);

            Button btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.SetBounds(218, 200, 90, 32);
            btnSave.FlatStyle = FlatStyle.Flat;
            btnSave.BackColor = p.AccentBack;
            btnSave.ForeColor = p.AccentText;
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += OnSave;

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(318, 200, 90, 32);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.BackColor = p.ButtonBack;
            btnCancel.ForeColor = p.ButtonText;
            btnCancel.FlatAppearance.BorderColor = p.CardBorder;
            btnCancel.DialogResult = DialogResult.Cancel;

            AcceptButton = btnSave;
            CancelButton = btnCancel;

            Controls.Add(lblInfo);
            Controls.Add(chkEnable);
            Controls.Add(lblDay);
            Controls.Add(cmbDay);
            Controls.Add(lblTime);
            Controls.Add(dtTime);
            Controls.Add(btnSave);
            Controls.Add(btnCancel);

            UpdateEnabled();
        }

        void UpdateEnabled()
        {
            cmbDay.Enabled = chkEnable.Checked;
            dtTime.Enabled = chkEnable.Checked;
        }

        void OnSave(object sender, EventArgs e)
        {
            try
            {
                int day = cmbDay.SelectedIndex;
                string time = dtTime.Value.ToString("HH:mm");
                bool ok;
                if (chkEnable.Checked) ok = PlanTask.Create(day, time);
                else ok = !PlanTask.Exists() || PlanTask.Delete();
                Settings.SavePlan(day, time);
                if (!ok)
                {
                    MessageBox.Show("计划任务操作失败（可能需要管理员权限，或系统计划任务服务被禁用）。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ============================================================
    //  清理结果汇总窗口
    // ============================================================
    class CleanSummaryForm : Form
    {
        public CleanSummaryForm(Palette p, List<KeyValuePair<string, long>> results, long total, TimeSpan elapsed, string logPath)
        {
            Text = "清理完成";
            ClientSize = new Size(440, 430);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = p.CardBack;

            Label lblTitle = new Label();
            lblTitle.Text = "清理完成";
            lblTitle.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
            lblTitle.ForeColor = p.Text;
            lblTitle.SetBounds(20, 16, 200, 30);

            Label lblTotal = new Label();
            lblTotal.Text = "共释放 " + Target.FormatSize(total);
            lblTotal.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            lblTotal.ForeColor = p.OkText;
            lblTotal.SetBounds(20, 50, 400, 26);

            Label lblElapsed = new Label();
            lblElapsed.Text = "用时 " + elapsed.TotalSeconds.ToString("N1") + " 秒";
            lblElapsed.ForeColor = p.TextDim;
            lblElapsed.SetBounds(20, 78, 200, 20);

            ListView lv = new ListView();
            lv.SetBounds(20, 104, 400, 240);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.GridLines = false;
            lv.BorderStyle = BorderStyle.FixedSingle;
            lv.BackColor = p.InputBack;
            lv.ForeColor = p.InputText;
            lv.Columns.Add("清理项目", 260);
            lv.Columns.Add("释放", 120);
            foreach (KeyValuePair<string, long> kv in results)
            {
                ListViewItem it = new ListViewItem(kv.Key);
                it.SubItems.Add(kv.Value > 0 ? Target.FormatSize(kv.Value) : "-");
                lv.Items.Add(it);
            }

            Button btnOpen = new Button();
            btnOpen.Text = "打开日志";
            btnOpen.SetBounds(20, 358, 120, 32);
            btnOpen.FlatStyle = FlatStyle.Flat;
            btnOpen.BackColor = p.ButtonBack;
            btnOpen.ForeColor = p.ButtonText;
            btnOpen.FlatAppearance.BorderColor = p.CardBorder;
            btnOpen.Click += delegate
            {
                try { Process.Start("notepad.exe", "\"" + logPath + "\""); } catch { }
            };

            Button btnOk = new Button();
            btnOk.Text = "确定";
            btnOk.SetBounds(300, 358, 120, 32);
            btnOk.FlatStyle = FlatStyle.Flat;
            btnOk.BackColor = p.AccentBack;
            btnOk.ForeColor = p.AccentText;
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.DialogResult = DialogResult.OK;

            AcceptButton = btnOk;
            CancelButton = btnOk;

            Controls.Add(lblTitle);
            Controls.Add(lblTotal);
            Controls.Add(lblElapsed);
            Controls.Add(lv);
            Controls.Add(btnOpen);
            Controls.Add(btnOk);
        }
    }

    // ============================================================
    //  主窗口
    // ============================================================
    class MainForm : Form
    {
        CheckedListBox lstDrives;
        ListView lvTargets;
        RichTextBox rtbLog;
        ProgressBar prg;
        Label lblTitle, lblSub, lblAdmin, lblStatus, lblTips, lblTheme;
        Button btnScan, btnStart, btnStop, btnLog, btnBin, btnExit, btnPlan;
        Button btnInvDrives, btnInvTargets;
        CheckBox chkAllDrives, chkAllTargets;
        ComboBox cmbTheme;
        Card cardDrives, cardTargets, cardOps, cardDanger, cardStatus, cardLog;
        NotifyIcon tray;

        List<DriveInfo> driveInfos = new List<DriveInfo>();
        List<Target> targets = Targets.Build();
        Dictionary<string, ListViewItem> targetItems = new Dictionary<string, ListViewItem>();
        CancellationTokenSource cts;
        Palette palette = Palette.Light();
        bool darkActive = false;
        bool applyingTheme = false;
        bool exitRequested = false;
        DateTime lastCleanStart;
        List<KeyValuePair<string, long>> lastResults = new List<KeyValuePair<string, long>>();

        public MainForm()
        {
            Text = "C盘清理助手 v" + Program.AppVersion;
            ClientSize = new Size(960, 700);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            BuildUi();
            LoadDrives();
            LoadTargets();
            UpdateDriveMaster();
            UpdateTargetMaster();
            SetBusy(false);
            SetupTray();

            int mode = (int)Settings.LoadTheme();
            if (mode < 0 || mode > 2) mode = 0;
            applyingTheme = true;
            cmbTheme.SelectedIndex = mode;
            applyingTheme = false;

            Shown += delegate
            {
                ApplyTheme((ThemeMode)cmbTheme.SelectedIndex, false);
                lblAdmin.Text = Program.IsAdmin() ? "✔ 管理员模式" : "受限模式（部分项目需管理员）";
                lblAdmin.ForeColor = Program.IsAdmin() ? Color.FromArgb(20, 115, 60) : Color.FromArgb(170, 105, 0);
                AppendLog("=== C盘清理助手 v" + Program.AppVersion + " ===", 0);
                AppendLog("运行环境：.NET " + Environment.Version.ToString(), 0);
                AppendLog("日志文件：" + Log.Path, 0);
                if (Program.IsAdmin()) AppendLog("已获得管理员权限，系统缓存与回收站可完整清理。", 1);
                else AppendLog("未获得管理员权限：系统临时文件、更新缓存、错误报告等项目会被跳过。", 2);
                AppendLog("点击“扫描大小”预览体积；点击“开始清理”执行；最小化后驻留托盘。", 0);
            };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) MinimizeToTray();
            };
            Activated += delegate
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(delegate { ReapplyTitleTheme(); }));
                }
            };
            FormClosing += OnFormClosing;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            FormClosed += delegate
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.Dispose();
                }
            };
        }

        void SetupTray()
        {
            tray = new NotifyIcon();
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                tray.Icon = ico != null ? ico : SystemIcons.Application;
            }
            catch { tray.Icon = SystemIcons.Application; }
            tray.Text = "C盘清理助手 v" + Program.AppVersion;
            tray.Visible = true;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开界面", null, delegate { RestoreFromTray(); });
            menu.Items.Add("立即清理缓存", null, delegate { RestoreFromTray(); StartClick(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { exitRequested = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreFromTray(); };
        }

        void MinimizeToTray()
        {
            Hide();
            ShowInTaskbar = false;
            try { tray.ShowBalloonTip(2500, "C盘清理助手", "已最小化到系统托盘，双击图标可恢复。", ToolTipIcon.Info); } catch { }
        }

        void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(delegate { ReapplyTitleTheme(); }));
            }
        }

        void ReapplyTitleTheme()
        {
            if (!IsHandleCreated) return;
            Native.ReapplyFrame(Handle, darkActive);
            if (lvTargets != null && lvTargets.IsHandleCreated) Native.SetDarkTheme(lvTargets.Handle, darkActive);
            if (lstDrives != null && lstDrives.IsHandleCreated) Native.SetDarkTheme(lstDrives.Handle, darkActive);
        }

        public bool IsDarkTitleActive()
        {
            return IsHandleCreated && Native.IsDarkTitle(Handle);
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General) return;
            if (cmbTheme.SelectedIndex != 0) return;
            try
            {
                if (IsHandleCreated)
                    BeginInvoke(new Action(delegate { ApplyTheme(ThemeMode.System, false); }));
            }
            catch { }
        }

        bool SystemIsDark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return ((int)v) == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        // ---------------- UI 构建 ----------------
        void BuildUi()
        {
            lblTitle = new Label();
            lblTitle.Text = "C盘清理助手 v" + Program.AppVersion;
            lblTitle.SetBounds(16, 8, 460, 30);
            lblTitle.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);

            lblSub = new Label();
            lblSub.Text = "只清理缓存与临时文件，不触碰系统文件和个人资料";
            lblSub.SetBounds(18, 38, 460, 18);

            lblAdmin = new Label();
            lblAdmin.SetBounds(500, 12, 210, 20);
            lblAdmin.TextAlign = ContentAlignment.MiddleRight;
            lblAdmin.Text = "检测权限中…";

            lblTheme = new Label();
            lblTheme.Text = "主题：";
            lblTheme.SetBounds(716, 14, 44, 18);

            cmbTheme = new ComboBox();
            cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbTheme.FlatStyle = FlatStyle.Flat;
            cmbTheme.SetBounds(760, 10, 110, 24);
            cmbTheme.Items.AddRange(new object[] { "跟随系统", "浅色", "深色" });
            cmbTheme.SelectedIndexChanged += delegate
            {
                if (applyingTheme) return;
                ThemeMode mode = (ThemeMode)cmbTheme.SelectedIndex;
                ApplyTheme(mode, true);
            };

            btnExit = new Button();
            btnExit.Text = "退出";
            btnExit.SetBounds(884, 9, 60, 26);
            btnExit.FlatStyle = FlatStyle.Flat;
            btnExit.FlatAppearance.BorderSize = 0;
            btnExit.Click += delegate { Close(); };

            // 驱动器
            cardDrives = new Card();
            cardDrives.TitleText = "驱动器（影响回收站与各盘 Temp 的清理范围）";
            cardDrives.SetBounds(12, 66, 600, 136);
            chkAllDrives = new CheckBox();
            chkAllDrives.Text = "全选驱动器";
            chkAllDrives.SetBounds(14, 16, 300, 20);
            chkAllDrives.AutoCheck = false;
            chkAllDrives.ThreeState = true;
            chkAllDrives.Click += delegate { SetAllDrives(GetDriveCheckedCount() < lstDrives.Items.Count); };
            btnInvDrives = MakeButton("反选", 322, 13, 60, 24);
            btnInvDrives.Click += delegate { InvertDrives(); };
            lstDrives = new CheckedListBox();
            lstDrives.SetBounds(14, 40, 572, 82);
            lstDrives.CheckOnClick = true;
            lstDrives.IntegralHeight = false;
            lstDrives.BorderStyle = BorderStyle.FixedSingle;
            lstDrives.ItemCheck += delegate
            {
                if (IsHandleCreated) BeginInvoke(new Action(delegate { UpdateDriveMaster(); }));
            };
            cardDrives.Controls.Add(chkAllDrives);
            cardDrives.Controls.Add(btnInvDrives);
            cardDrives.Controls.Add(lstDrives);

            // 清理项目
            cardTargets = new Card();
            cardTargets.TitleText = "清理项目（勾选后点击右侧“开始清理”）";
            cardTargets.SetBounds(12, 210, 600, 282);
            chkAllTargets = new CheckBox();
            chkAllTargets.Text = "全选清理项目";
            chkAllTargets.SetBounds(14, 16, 300, 20);
            chkAllTargets.AutoCheck = false;
            chkAllTargets.ThreeState = true;
            chkAllTargets.Click += delegate { SetAllTargets(GetTargetCheckedCount() < lvTargets.Items.Count); };
            btnInvTargets = MakeButton("反选", 322, 13, 60, 24);
            btnInvTargets.Click += delegate { InvertTargets(); };
            lvTargets = new ListView();
            lvTargets.SetBounds(14, 40, 572, 182);
            lvTargets.View = View.Details;
            lvTargets.CheckBoxes = true;
            lvTargets.FullRowSelect = true;
            lvTargets.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvTargets.GridLines = false;
            lvTargets.BorderStyle = BorderStyle.FixedSingle;
            lvTargets.Columns.Add("清理项目", 310);
            lvTargets.Columns.Add("类别", 80);
            lvTargets.Columns.Add("状态", 150);
            lvTargets.ItemChecked += delegate { if (IsHandleCreated) UpdateTargetMaster(); };
            btnScan = MakeButton("扫描大小", 14, 230, 110, 30);
            btnScan.Click += delegate { ScanClick(); };
            cardTargets.Controls.Add(chkAllTargets);
            cardTargets.Controls.Add(btnInvTargets);
            cardTargets.Controls.Add(lvTargets);
            cardTargets.Controls.Add(btnScan);

            // 操作
            cardOps = new Card();
            cardOps.TitleText = "操作";
            cardOps.SetBounds(624, 66, 324, 158);
            btnStart = MakeButton("开始清理", 14, 24, 296, 40);
            btnStart.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            btnStart.Click += delegate { StartClick(); };
            btnStop = MakeButton("停止", 14, 74, 140, 28);
            btnStop.Click += delegate { StopClick(); };
            btnLog = MakeButton("打开日志", 162, 74, 148, 28);
            btnLog.Click += delegate
            {
                try { Process.Start("notepad.exe", "\"" + Log.Path + "\""); } catch { }
            };
            btnPlan = MakeButton("定时自动清理…", 14, 114, 296, 30);
            btnPlan.Click += delegate { PlanClick(); };
            cardOps.Controls.Add(btnStart);
            cardOps.Controls.Add(btnStop);
            cardOps.Controls.Add(btnLog);
            cardOps.Controls.Add(btnPlan);

            // 危险
            cardDanger = new Card();
            cardDanger.TitleText = "危险操作（单独确认）";
            cardDanger.SetBounds(624, 232, 324, 110);
            btnBin = MakeButton("清空回收站（不可恢复）", 14, 26, 296, 36);
            btnBin.Click += delegate { BinClick(); };
            Label lblBinHint = new Label();
            lblBinHint.Text = "仅清空所选驱动器的回收站内容，删除后无法恢复。";
            lblBinHint.SetBounds(14, 68, 296, 34);
            cardDanger.Controls.Add(btnBin);
            cardDanger.Controls.Add(lblBinHint);
            lblBinHint.Name = "lblBinHint";

            // 状态
            cardStatus = new Card();
            cardStatus.TitleText = "状态";
            cardStatus.SetBounds(624, 350, 324, 56);
            prg = new ProgressBar();
            prg.SetBounds(14, 22, 296, 14);
            prg.Minimum = 0;
            prg.Maximum = 100;
            lblStatus = new Label();
            lblStatus.Text = "就绪";
            lblStatus.SetBounds(14, 40, 296, 18);
            cardStatus.Controls.Add(prg);
            cardStatus.Controls.Add(lblStatus);

            lblTips = new Label();
            lblTips.SetBounds(624, 414, 324, 70);
            lblTips.Text = "提示：\r\n· 清理前请关闭浏览器\r\n· 回收站需勾选确认\r\n· 最小化后驻留托盘 · 日志在程序目录";

            // 日志
            cardLog = new Card();
            cardLog.TitleText = "运行日志（同步写入 清理日志.txt）";
            cardLog.SetBounds(12, 500, 936, 188);
            rtbLog = new RichTextBox();
            rtbLog.SetBounds(14, 26, 908, 150);
            rtbLog.ReadOnly = true;
            rtbLog.BorderStyle = BorderStyle.FixedSingle;
            rtbLog.WordWrap = false;
            rtbLog.ScrollBars = RichTextBoxScrollBars.Both;
            rtbLog.Font = new Font("Consolas", 9f);
            cardLog.Controls.Add(rtbLog);

            Controls.Add(lblTitle);
            Controls.Add(lblSub);
            Controls.Add(lblAdmin);
            Controls.Add(lblTheme);
            Controls.Add(cmbTheme);
            Controls.Add(btnExit);
            Controls.Add(cardDrives);
            Controls.Add(cardTargets);
            Controls.Add(cardOps);
            Controls.Add(cardDanger);
            Controls.Add(cardStatus);
            Controls.Add(lblTips);
            Controls.Add(cardLog);
        }

        Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            return b;
        }

        void LoadDrives()
        {
            lstDrives.Items.Clear();
            driveInfos.Clear();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                }
                catch { continue; }
                driveInfos.Add(d);
                string tag = "";
                try
                {
                    if (string.Equals(d.Name.Substring(0, 2), Environment.GetEnvironmentVariable("SystemDrive") + "\\", StringComparison.OrdinalIgnoreCase)) tag = "  [系统盘]";
                }
                catch { }
                string text = string.Format("{0}  剩余 {1:N1} GB / 共 {2:N1} GB{3}",
                    d.Name,
                    (double)d.AvailableFreeSpace / 1073741824.0,
                    (double)d.TotalSize / 1073741824.0,
                    tag);
                lstDrives.Items.Add(text, d.DriveType == DriveType.Fixed);
            }
        }

        void RefreshDriveTexts()
        {
            for (int i = 0; i < lstDrives.Items.Count; i++)
            {
                try { driveInfos[i] = new DriveInfo(driveInfos[i].Name); } catch { }
                DriveInfo d = driveInfos[i];
                bool chk = lstDrives.GetItemChecked(i);
                string tag = "";
                try
                {
                    if (string.Equals(d.Name.Substring(0, 2), Environment.GetEnvironmentVariable("SystemDrive") + "\\", StringComparison.OrdinalIgnoreCase)) tag = "  [系统盘]";
                }
                catch { }
                lstDrives.Items[i] = string.Format("{0}  剩余 {1:N1} GB / 共 {2:N1} GB{3}",
                    d.Name, (double)d.AvailableFreeSpace / 1073741824.0, (double)d.TotalSize / 1073741824.0, tag);
                lstDrives.SetItemChecked(i, chk);
            }
        }

        void LoadTargets()
        {
            ListViewGroup gSys = new ListViewGroup("系统缓存");
            ListViewGroup gUser = new ListViewGroup("用户缓存");
            ListViewGroup gDrive = new ListViewGroup("驱动器");
            lvTargets.Groups.Add(gSys);
            lvTargets.Groups.Add(gUser);
            lvTargets.Groups.Add(gDrive);
            targetItems.Clear();
            foreach (Target t in targets)
            {
                ListViewItem it = new ListViewItem(t.Name);
                it.SubItems.Add(t.Category);
                it.SubItems.Add("未扫描");
                it.Tag = t.Id;
                if (t.Category == "系统缓存") it.Group = gSys;
                else if (t.Category == "用户缓存") it.Group = gUser;
                else it.Group = gDrive;
                lvTargets.Items.Add(it);
                it.Checked = true;
                targetItems[t.Id] = it;
            }
        }

        // ---------------- 主题 ----------------
        void ApplyTheme(ThemeMode mode, bool save)
        {
            palette = (mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemIsDark())) ? Palette.Dark() : Palette.Light();
            darkActive = (mode == ThemeMode.Dark) || (mode == ThemeMode.System && SystemIsDark());

            bool redraw = IsHandleCreated;
            if (redraw) Native.SendMessage(Handle, Native.WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            SuspendLayout();
            try
            {
                BackColor = palette.FormBack;
                ForeColor = palette.Text;

                lblTitle.ForeColor = palette.Text;
                lblSub.ForeColor = palette.TextDim;
                lblAdmin.ForeColor = palette.TextDim;
                lblTheme.ForeColor = palette.TextDim;
                lblStatus.ForeColor = palette.TextDim;
                lblTips.ForeColor = palette.TextDim;

                foreach (Card c in new Card[] { cardDrives, cardTargets, cardOps, cardDanger, cardStatus, cardLog })
                {
                    c.BackColor = palette.CardBack;
                    c.BorderColor = palette.CardBorder;
                    c.TitleColor = palette.Text;
                }

                lvTargets.BeginUpdate();
                lvTargets.BackColor = palette.CardBack;
                lvTargets.ForeColor = palette.Text;
                lvTargets.EndUpdate();

                lstDrives.BackColor = palette.InputBack;
                lstDrives.ForeColor = palette.InputText;
                rtbLog.BackColor = palette.LogBack;
                rtbLog.ForeColor = palette.LogText;
                cmbTheme.BackColor = palette.ButtonBack;
                cmbTheme.ForeColor = palette.Text;

                StyleBtn(btnScan, palette.ButtonBack, palette.ButtonText, palette.CardBorder);
                StyleBtn(btnInvDrives, palette.ButtonBack, palette.ButtonText, palette.CardBorder);
                StyleBtn(btnInvTargets, palette.ButtonBack, palette.ButtonText, palette.CardBorder);
                chkAllDrives.ForeColor = palette.Text;
                chkAllTargets.ForeColor = palette.Text;
                StyleBtn(btnStop, palette.ButtonBack, palette.ButtonText, palette.CardBorder);
                StyleBtn(btnLog, palette.ButtonBack, palette.ButtonText, palette.CardBorder);
                StyleBtn(btnExit, palette.ButtonBack, palette.ButtonText, palette.CardBorder);
                StyleBtn(btnPlan, palette.ButtonBack, palette.ButtonText, palette.CardBorder);
                StyleBtn(btnStart, palette.AccentBack, palette.AccentText, palette.AccentBack);
                StyleBtn(btnBin, palette.DangerBack, palette.DangerText, palette.DangerBack);

                Control[] all = cardDanger.Controls.Find("lblBinHint", false);
                if (all.Length > 0) all[0].ForeColor = palette.TextDim;

                if (IsHandleCreated)
                {
                    Native.SetDarkTheme(lvTargets.Handle, darkActive);
                    Native.SetDarkTheme(lstDrives.Handle, darkActive);
                    Native.SetDarkTitle(Handle, darkActive);
                }
            }
            finally
            {
                ResumeLayout(false);
                if (redraw)
                {
                    Native.SendMessage(Handle, Native.WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                    Native.ReapplyFrame(Handle, darkActive);
                    Invalidate(true);
                    Update();
                }
            }
            if (save) Settings.SaveTheme(mode);
        }

        public void SetThemeIndex(int index)
        {
            if (index < 0 || index > 2) return;
            applyingTheme = true;
            cmbTheme.SelectedIndex = index;
            applyingTheme = false;
            ApplyTheme((ThemeMode)index, false);
        }

        void StyleBtn(Button b, Color back, Color fore, Color border)
        {
            b.BackColor = back;
            b.ForeColor = fore;
            b.FlatAppearance.BorderColor = border;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back);
        }

        // ---------------- 日志 ----------------
        void AppendLog(string text, int level)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
            Color c = palette.LogText;
            if (level == 1) c = palette.OkText;
            if (level == 2) c = palette.WarnText;
            if (level == 3) c = palette.ErrText;
            if (rtbLog.IsDisposed) return;
            rtbLog.SelectionStart = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor = c;
            rtbLog.AppendText(line + "\r\n");
            rtbLog.ScrollToCaret();
            Log.Write(line);
        }

        // ---------------- 状态 ----------------
        void SetStatus(string s)
        {
            lblStatus.Text = s;
        }

        void SetBusy(bool busy)
        {
            btnStart.Enabled = !busy;
            btnScan.Enabled = !busy;
            btnBin.Enabled = !busy;
            btnStop.Enabled = busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        List<string> CheckedDrives()
        {
            List<string> result = new List<string>();
            for (int i = 0; i < lstDrives.Items.Count; i++)
            {
                if (lstDrives.GetItemChecked(i))
                    result.Add(driveInfos[i].Name.Substring(0, 1));
            }
            return result;
        }

        List<Target> CheckedTargets()
        {
            List<Target> result = new List<Target>();
            foreach (Target t in targets)
            {
                ListViewItem it;
                if (targetItems.TryGetValue(t.Id, out it) && it.Checked) result.Add(t);
            }
            return result;
        }

        int GetDriveCheckedCount()
        {
            int c = 0;
            for (int i = 0; i < lstDrives.Items.Count; i++)
            {
                if (lstDrives.GetItemChecked(i)) c++;
            }
            return c;
        }

        void SetAllDrives(bool value)
        {
            for (int i = 0; i < lstDrives.Items.Count; i++) lstDrives.SetItemChecked(i, value);
            UpdateDriveMaster();
        }

        void InvertDrives()
        {
            for (int i = 0; i < lstDrives.Items.Count; i++) lstDrives.SetItemChecked(i, !lstDrives.GetItemChecked(i));
            UpdateDriveMaster();
        }

        void UpdateDriveMaster()
        {
            if (chkAllDrives == null) return;
            int n = lstDrives.Items.Count;
            int c = GetDriveCheckedCount();
            if (c == 0) chkAllDrives.CheckState = CheckState.Unchecked;
            else if (c == n) chkAllDrives.CheckState = CheckState.Checked;
            else chkAllDrives.CheckState = CheckState.Indeterminate;
        }

        int GetTargetCheckedCount()
        {
            int c = 0;
            foreach (ListViewItem it in lvTargets.Items)
            {
                if (it.Checked) c++;
            }
            return c;
        }

        void SetAllTargets(bool value)
        {
            foreach (ListViewItem it in lvTargets.Items) it.Checked = value;
            UpdateTargetMaster();
        }

        void InvertTargets()
        {
            foreach (ListViewItem it in lvTargets.Items) it.Checked = !it.Checked;
            UpdateTargetMaster();
        }

        void UpdateTargetMaster()
        {
            if (chkAllTargets == null) return;
            int n = lvTargets.Items.Count;
            int c = GetTargetCheckedCount();
            if (c == 0) chkAllTargets.CheckState = CheckState.Unchecked;
            else if (c == n) chkAllTargets.CheckState = CheckState.Checked;
            else chkAllTargets.CheckState = CheckState.Indeterminate;
        }

        CleanContext NewContext()
        {
            CleanContext ctx = new CleanContext();
            ctx.Drives = CheckedDrives();
            ctx.LocalRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (Program.IsAdmin())
            {
                try
                {
                    string usersRoot = Path.GetDirectoryName(Environment.GetEnvironmentVariable("USERPROFILE"));
                    foreach (string u in Directory.GetDirectories(usersRoot))
                    {
                        string p = Path.Combine(u, "AppData", "Local");
                        if (Directory.Exists(p) && !ctx.LocalRoots.Contains(p)) ctx.LocalRoots.Add(p);
                    }
                }
                catch { }
            }
            return ctx;
        }

        // ---------------- 扫描 ----------------
        async void ScanClick()
        {
            if (cts != null) return;
            List<Target> list = CheckedTargets();
            if (list.Count == 0) { AppendLog("未勾选任何清理项目", 2); return; }
            CleanContext ctx = NewContext();
            CancellationTokenSource local = new CancellationTokenSource();
            cts = local;
            SetBusy(true);
            Progress<Msg> prog = new Progress<Msg>(OnProgress);
            try
            {
                await Task.Run(delegate { Workers.Scan(ctx, list, prog, local.Token); });
                SetStatus("扫描完成");
            }
            catch (OperationCanceledException)
            {
                AppendLog("扫描已停止", 2);
            }
            catch (Exception ex)
            {
                AppendLog("扫描出错：" + ex.Message, 3);
            }
            finally
            {
                if (cts == local) cts = null;
                SetBusy(false);
                local.Dispose();
                if (exitRequested)
                {
                    exitRequested = false;
                    Close();
                }
            }
        }

        // ---------------- 清理 ----------------
        async void StartClick()
        {
            if (cts != null) return;
            List<Target> list = CheckedTargets();
            if (list.Count == 0) { AppendLog("请先勾选要清理的项目", 2); return; }
            if (!Program.IsAdmin()) AppendLog("当前为受限模式：系统缓存等项目将被跳过。", 2);
            CleanContext ctx = NewContext();
            if (ctx.Drives.Count == 0) AppendLog("提示：未勾选驱动器，各盘 Temp 项目将跳过", 2);
            CancellationTokenSource local = new CancellationTokenSource();
            cts = local;
            SetBusy(true);
            prg.Value = 0;
            lastResults.Clear();
            lastCleanStart = DateTime.Now;
            Progress<Msg> prog = new Progress<Msg>(OnProgress);
            AppendLog("开始清理，共 " + list.Count + " 个项目", 0);
            try
            {
                long total = await Task.Run(delegate
                {
                    return Workers.Clean(ctx, list, prog, local.Token, Program.IsAdmin());
                });
                AppendLog("清理结束，本次共释放 " + Target.FormatSize(total), 1);
                SetStatus("完成，共释放 " + Target.FormatSize(total));
                RefreshDriveTexts();
                ShowSummary(total);
            }
            catch (OperationCanceledException)
            {
                AppendLog("已停止清理", 2);
            }
            catch (Exception ex)
            {
                AppendLog("清理出错：" + ex.Message, 3);
            }
            finally
            {
                if (cts == local) cts = null;
                SetBusy(false);
                local.Dispose();
                if (exitRequested)
                {
                    exitRequested = false;
                    Close();
                }
            }
        }

        void StopClick()
        {
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            AppendLog("收到停止请求，正在结束当前小步骤…", 2);
        }

        void OnProgress(Msg m)
        {
            if (m == null) return;
            switch (m.Kind)
            {
                case "status":
                    SetStatus(m.Text);
                    break;
                case "size":
                    ListViewItem it;
                    if (m.Id != null && targetItems.TryGetValue(m.Id, out it))
                    {
                        it.SubItems[2].Text = m.Text;
                    }
                    break;
                case "log":
                    AppendLog(m.Text, 0);
                    break;
                case "percent":
                    if (m.Percent >= 0 && m.Percent <= 100) prg.Value = m.Percent;
                    break;
                case "result":
                    lastResults.Add(new KeyValuePair<string, long>(m.Text, m.Size));
                    break;
            }
        }

        void ShowSummary(long total)
        {
            if (exitRequested) return;
            List<KeyValuePair<string, long>> results = new List<KeyValuePair<string, long>>(lastResults);
            TimeSpan elapsed = DateTime.Now - lastCleanStart;
            using (CleanSummaryForm f = new CleanSummaryForm(palette, results, total, elapsed, Log.Path))
            {
                f.ShowDialog(this);
            }
        }

        void PlanClick()
        {
            if (cts != null) return;
            using (PlanForm f = new PlanForm(palette))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    bool exists = PlanTask.Exists();
                    AppendLog(exists ? ("已启用每周自动清理：" + PlanText()) : "已关闭每周自动清理", exists ? 1 : 0);
                }
            }
        }

        static string PlanText()
        {
            string[] days = new string[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
            int d = Settings.LoadPlanDay();
            if (d < 0 || d > 6) d = 0;
            return "每周" + days[d] + " " + Settings.LoadPlanTime() + " 自动清理";
        }

        public void TestBulkChecks()
        {
            int dn = lstDrives.Items.Count;
            int tn = lvTargets.Items.Count;
            SetAllDrives(false);
            if (chkAllDrives.CheckState != CheckState.Unchecked) throw new Exception("drives master unchecked");
            if (dn > 0)
            {
                lstDrives.SetItemChecked(0, true);
                UpdateDriveMaster();
                if (dn > 1 && chkAllDrives.CheckState != CheckState.Indeterminate) throw new Exception("drives tri-state");
            }
            InvertDrives();
            if (GetDriveCheckedCount() != (dn - 1)) throw new Exception("drives invert count");
            SetAllDrives(true);
            if (chkAllDrives.CheckState != CheckState.Checked) throw new Exception("drives master checked");

            SetAllTargets(false);
            if (chkAllTargets.CheckState != CheckState.Unchecked) throw new Exception("targets master unchecked");
            lvTargets.Items[0].Checked = true;
            if (tn > 1 && chkAllTargets.CheckState != CheckState.Indeterminate) throw new Exception("targets tri-state");
            InvertTargets();
            if (GetTargetCheckedCount() != (tn - 1)) throw new Exception("targets invert count");
            SetAllTargets(true);
            if (chkAllTargets.CheckState != CheckState.Checked) throw new Exception("targets master checked");
        }

        public string HeaderTitleText()
        {
            return lblTitle.Text;
        }

        public string TrayTooltipText()
        {
            return tray == null ? "" : tray.Text;
        }

        public void TestTrayRoundtrip()
        {
            MinimizeToTray();
            Application.DoEvents();
            RestoreFromTray();
            Application.DoEvents();
        }

        public void TestDialogs()
        {
            List<KeyValuePair<string, long>> r = new List<KeyValuePair<string, long>>();
            r.Add(new KeyValuePair<string, long>("测试项目", 12345678L));
            using (CleanSummaryForm f = new CleanSummaryForm(palette, r, 12345678L, TimeSpan.FromSeconds(3.2), Log.Path)) { }
            using (PlanForm pf = new PlanForm(palette)) { }
        }

        // ---------------- 回收站 ----------------
        void BinClick()
        {
            if (cts != null) return;
            List<string> drives = CheckedDrives();
            if (drives.Count == 0) { AppendLog("请先在驱动器列表中勾选要清空的回收站", 2); return; }
            List<BinStat> stats = new List<BinStat>();
            foreach (string d in drives)
            {
                long size, count;
                RecycleBin.Query(d, out size, out count);
                BinStat s = new BinStat();
                s.Drive = d;
                s.Size = size;
                s.Count = count;
                stats.Add(s);
            }
            long totalCount = 0;
            foreach (BinStat s in stats) totalCount += s.Count;
            if (totalCount == 0)
            {
                AppendLog("所选驱动器回收站为空：" + string.Join(", ", drives.ToArray()), 0);
                return;
            }
            bool ok;
            using (ConfirmBinForm dlg = new ConfirmBinForm(stats, Program.IsAdmin(), palette))
            {
                ok = dlg.ShowDialog(this) == DialogResult.OK;
            }
            if (!ok)
            {
                AppendLog("已取消清空回收站", 0);
                return;
            }
            AppendLog("开始清空回收站：" + string.Join(", ", drives.ToArray()), 0);
            foreach (BinStat s in stats)
            {
                RecycleBin.Empty(Handle, s.Drive);
                AppendLog("已清空 " + s.Drive + ": 回收站（约释放 " + Target.FormatSize(s.Size) + "）", 1);
                Application.DoEvents();
            }
            SetStatus("回收站已清空");
            RefreshDriveTexts();
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (cts != null)
            {
                if (e.CloseReason == CloseReason.UserClosing || exitRequested)
                {
                    e.Cancel = true;
                    exitRequested = true;
                    try { cts.Cancel(); } catch { }
                    AppendLog("正在停止，完成后自动退出…", 2);
                }
            }
        }
    }

    // ============================================================
    //  冒烟测试（真实弹窗 + 主题切换，自动关闭）
    // ============================================================
    static class SmokeTest
    {
        public static void Run()
        {
            List<string> lines = new List<string>();
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                for (int i = 0; i < 3; i++)
                {
                    f.SetThemeIndex(i);
                    Application.DoEvents();
                    Thread.Sleep(150);
                }
                f.SetThemeIndex(2);
                Application.DoEvents();
                Thread.Sleep(250);
                Application.DoEvents();
                bool darkSupported = f.IsDarkTitleActive();
                lines.Add("[INFO] dark-title-supported=" + darkSupported);
                lines.Add("[PASS] show+theme-switch(3 modes)");
                string expected = "C盘清理助手 v" + Program.AppVersion;
                if (!string.Equals(f.HeaderTitleText(), expected, StringComparison.Ordinal)) throw new Exception("header label mismatch: " + f.HeaderTitleText());
                if (!string.Equals(f.Text, expected, StringComparison.Ordinal)) throw new Exception("form title mismatch: " + f.Text);
                if (!string.Equals(f.TrayTooltipText(), expected, StringComparison.Ordinal)) throw new Exception("tray tooltip mismatch: " + f.TrayTooltipText());
                lines.Add("[PASS] version-consistency");

                // 深色主题下：最小化到托盘 -> 恢复，标题栏必须仍为深色
                f.TestTrayRoundtrip();
                Application.DoEvents();
                Thread.Sleep(250);
                Application.DoEvents();
                if (darkSupported)
                {
                    if (!f.IsDarkTitleActive()) throw new Exception("dark title lost after tray restore");
                    lines.Add("[PASS] dark-title-survives-tray");
                }
                else
                {
                    lines.Add("[SKIP] dark-title-survives-tray (system not supported)");
                }

                f.TestBulkChecks();
                lines.Add("[PASS] tray-minimize + bulk-check");
                f.SetThemeIndex(0);
                Application.DoEvents();
                f.TestDialogs();
                lines.Add("[PASS] dialogs");
                f.Close();
                f.Dispose();
            }
            catch (Exception ex)
            {
                lines.Add("[FAIL] " + ex.Message);
            }
            lines.Add("SMOKETEST done");
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "cdc_smoke.txt"), string.Join("\r\n", lines.ToArray()), Encoding.UTF8);
            }
            catch { }
            Application.Exit();
        }
    }

    // ============================================================
    //  自检
    // ============================================================
    static class SelfTest
    {
        public static void Run()
        {
            List<string> lines = new List<string>();
            int pass = 0;
            int fail = 0;
            Action<string, bool, string> check = delegate(string name, bool ok, string detail)
            {
                if (ok) pass++; else fail++;
                lines.Add((ok ? "[PASS] " : "[FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  " + detail));
            };
            try
            {
                lines.Add("admin=" + Program.IsAdmin());
                List<Target> targets = Targets.Build();
                check("targets=13", targets.Count == 13, "actual=" + targets.Count);

                CleanContext ctx = new CleanContext();
                ctx.Drives.Add("C");
                ctx.LocalRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                bool allResolve = true;
                foreach (Target t in targets)
                {
                    try { t.Resolve(ctx); } catch { allResolve = false; }
                }
                check("resolvers", allResolve, "");

                bool uiOk = false;
                try
                {
                    MainForm f = new MainForm();
                    uiOk = f.Controls.Count > 5;
                    f.Dispose();
                }
                catch (Exception ex)
                {
                    lines.Add("  form error: " + ex.Message);
                }
                check("ui-build", uiOk, "");

                string sb = Path.Combine(Path.GetTempPath(), "cdc_selftest");
                try
                {
                    if (Directory.Exists(sb)) Directory.Delete(sb, true);
                    Directory.CreateDirectory(Path.Combine(sb, "a", "cache"));
                    Directory.CreateDirectory(Path.Combine(sb, "b"));
                    Directory.CreateDirectory(Path.Combine(sb, "Documents"));
                }
                catch { }
                string cache = Path.Combine(sb, "a", "cache");
                string f1 = Path.Combine(cache, "f1.bin");
                string f2 = Path.Combine(sb, "b", "f2.bin");
                byte[] buf = new byte[1048576];
                try
                {
                    File.WriteAllBytes(f1, buf);
                    File.WriteAllBytes(f2, new byte[524288]);
                }
                catch { }

                check("size-1MB", Engine.SizeOf(cache) == 1048576, "actual=" + Engine.SizeOf(cache));
                Engine.Clear(cache);
                check("clear-dir", Engine.SizeOf(cache) == 0, "");
                check("dir-recreated", Directory.Exists(cache), "");
                Engine.Clear(f2);
                check("clear-file", !File.Exists(f2), "");
                string docPath = Path.Combine(sb, "Documents");
                Engine.Clear(docPath);
                check("guard-documents", Directory.Exists(docPath), "");
                check("guard-root", !Engine.IsSafePath("E:\\"), "");
                long rbs, rbc;
                check("rb-query", RecycleBin.Query("C", out rbs, out rbc), "count=" + rbc);

                try { Directory.Delete(sb, true); } catch { }
            }
            catch (Exception ex)
            {
                lines.Add("FATAL " + ex.Message);
            }
            lines.Add("");
            lines.Add("SELFTEST_RESULT: " + pass + " passed, " + fail + " failed");
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "cdc_selftest_result.txt"), string.Join("\r\n", lines.ToArray()), Encoding.UTF8);
            }
            catch { }
            foreach (string l in lines) Console.WriteLine(l);
        }
    }
}
