// NetWatch - program start: administrator permission, one copy at a time, settings, error log.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("NetWatch")]
[assembly: AssemblyProduct("NetWatch")]
[assembly: AssemblyDescription("Shows which programs send and receive data over the network")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace NetWatch
{
    internal sealed class AppOptions
    {
        public bool Demo;               // --demo: invented traffic, no administrator rights needed
        public string SelfTestDir;      // --selftest <folder>: demo + save pictures of the window, then quit
        public string DiagFile;         // --diag <file>: normal run that also writes a text report
        public string ShowcaseDir;      // --showcase <folder>: invented programs, saves example pictures, then quits

        /// <summary>True for the two modes that run by themselves and must not touch real settings.</summary>
        public bool Scripted { get { return SelfTestDir != null || ShowcaseDir != null; } }
    }

    internal sealed class Settings
    {
        public bool IncludeLan;
        public bool HideWindows;
        public bool LookupNames = true;
        public int Width = 1240;
        public int Height = 820;
        public float DetailFraction = 0.36f;

        /// <summary>When set, nothing is written to disk (used by the self-test so it cannot change real settings).</summary>
        public bool ReadOnly;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetWatch", "settings.txt"); }
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    int number;
                    float fraction;
                    switch (key)
                    {
                        case "IncludeLan": s.IncludeLan = value == "1"; break;
                        case "HideWindows": s.HideWindows = value == "1"; break;
                        case "LookupNames": s.LookupNames = value != "0"; break;
                        case "Width": if (int.TryParse(value, out number) && number >= 600 && number <= 10000) s.Width = number; break;
                        case "Height": if (int.TryParse(value, out number) && number >= 400 && number <= 10000) s.Height = number; break;
                        case "DetailFraction":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction) && fraction >= 0.1f && fraction <= 0.8f)
                                s.DetailFraction = fraction;
                            break;
                    }
                }
            }
            catch
            {
            }
            return s;
        }

        public void Save()
        {
            if (ReadOnly) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("IncludeLan=" + (IncludeLan ? "1" : "0"));
                sb.AppendLine("HideWindows=" + (HideWindows ? "1" : "0"));
                sb.AppendLine("LookupNames=" + (LookupNames ? "1" : "0"));
                sb.AppendLine("Width=" + Width.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Height=" + Height.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("DetailFraction=" + DetailFraction.ToString("0.000", CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch
            {
            }
        }
    }

    internal static class Program
    {
        const string ActivateEventName = "Local\\NetWatch.Activate.Event";

        static EventWaitHandle _activate;
        static MainForm _form;
        static readonly object LogLock = new object();

        [STAThread]
        static int Main(string[] args)
        {
            AppOptions options = new AppOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--demo") options.Demo = true;
                else if (a == "--selftest" && i + 1 < args.Length) { options.Demo = true; options.SelfTestDir = args[++i]; }
                else if (a == "--showcase" && i + 1 < args.Length) { options.Demo = true; options.ShowcaseDir = args[++i]; }
                else if (a == "--diag" && i + 1 < args.Length) options.DiagFile = args[++i];
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Log("error: " + e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log("fatal: " + e.ExceptionObject);
                try { EtwSession.StopByName(); } catch { }
            };
            Theme.Init();

            if (!options.Demo)
            {
                // Is NetWatch already open? Then just bring that window to the front.
                if (SignalRunningCopy()) return 0;
                // Windows only tells administrators which program sends what.
                if (!ProcessUtil.IsElevated()) return RelaunchAsAdministrator(args);
                if (!BecomeTheOnlyCopy()) return 0;
            }

            Settings settings = options.Scripted ? new Settings() : Settings.Load();
            settings.ReadOnly = options.Scripted;
            _form = new MainForm(options, settings);
            if (_activate != null)
                ThreadPool.RegisterWaitForSingleObject(_activate, OnActivateSignal, null, Timeout.Infinite, false);
            Application.Run(_form);
            return 0;
        }

        static bool SignalRunningCopy()
        {
            try
            {
                EventWaitHandle existing;
                if (!EventWaitHandle.TryOpenExisting(ActivateEventName, EventWaitHandleRights.Modify, out existing)) return false;
                using (existing)
                {
                    NativeMethods.AllowSetForegroundWindow(-1);
                    existing.Set();
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;        // it is running, we are just not allowed to poke it
            }
            catch
            {
                return false;
            }
        }

        static bool BecomeTheOnlyCopy()
        {
            try
            {
                EventWaitHandleSecurity security = new EventWaitHandleSecurity();
                security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
                security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    EventWaitHandleRights.FullControl, AccessControlType.Allow));
                bool created;
                _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName, out created, security);
                if (created) return true;
                _activate.Set();
                _activate.Dispose();
                _activate = null;
                return false;
            }
            catch (Exception ex)
            {
                Log("single-copy check failed: " + ex.Message);
                return true;
            }
        }

        static void OnActivateSignal(object state, bool timedOut)
        {
            MainForm form = _form;
            try
            {
                if (form != null && form.IsHandleCreated)
                    form.BeginInvoke((MethodInvoker)form.BringToFront2);
            }
            catch
            {
            }
        }

        static int RelaunchAsAdministrator(string[] args)
        {
            while (true)
            {
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath);
                    info.UseShellExecute = true;
                    info.Verb = "runas";            // shows the Windows permission prompt
                    info.Arguments = JoinArguments(args);
                    info.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                    Process.Start(info);
                    return 0;
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode != 1223)     // 1223 = the user answered No
                    {
                        MessageDialog.Info(null, "NetWatch", "NetWatch could not start", ex.Message);
                        return 1;
                    }
                    bool again = MessageDialog.Confirm(null, "NetWatch", "NetWatch needs your permission",
                        "Windows only tells administrators which program sends and receives data, so NetWatch asks for that permission each time it starts.\n\n" +
                        "It uses the permission for two things: reading the traffic counts Windows keeps, and adding a firewall rule when you press Block.",
                        "Ask again", "Close", ButtonKind.Primary, Theme.Down);
                    if (!again) return 1;
                }
                catch (Exception ex)
                {
                    MessageDialog.Info(null, "NetWatch", "NetWatch could not start", ex.Message);
                    return 1;
                }
            }
        }

        static string JoinArguments(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.IndexOf(' ') >= 0 || a.Length == 0) sb.Append('"').Append(a.TrimEnd('\\')).Append('"');
                else sb.Append(a);
            }
            return sb.ToString();
        }

        /// <summary>Writes a line to %LocalAppData%\NetWatch\log.txt (problems only; no traffic data).</summary>
        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetWatch");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, "log.txt");
                    FileInfo info = new FileInfo(file);
                    if (info.Exists && info.Length > 512 * 1024) info.Delete();
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
