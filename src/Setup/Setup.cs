// NetWatch Setup - installs NetWatch, and removes it again (the same file is copied into the program
// folder as Uninstall.exe).
//
// Installing does exactly this, and nothing else:
//   - copies NetWatch.exe and Uninstall.exe into C:\Program Files\NetWatch
//   - adds a Start menu shortcut (and a desktop shortcut if wanted)
//   - registers NetWatch under "Apps & features" so Windows can remove it
// Removing undoes all of that, and (if you agree) deletes the firewall blocks NetWatch created.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("NetWatch Setup")]
[assembly: AssemblyProduct("NetWatch")]
[assembly: AssemblyDescription("Installs or removes NetWatch")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace NetWatch
{
    /// <summary>Where things are installed. The self-test uses a throw-away sandbox instead of the real places.</summary>
    internal sealed class InstallTarget
    {
        public string Dir;
        public string StartMenuDir;
        public string DesktopDir;
        public RegistryKey RegistryRoot;
        public string RegistryPath;
        public bool IsSandbox;

        public string ExePath { get { return Path.Combine(Dir, "NetWatch.exe"); } }
        public string UninstallerPath { get { return Path.Combine(Dir, "Uninstall.exe"); } }
        public string StartMenuLink { get { return Path.Combine(StartMenuDir, "NetWatch.lnk"); } }
        public string DesktopLink { get { return Path.Combine(DesktopDir, "NetWatch.lnk"); } }

        public static InstallTarget Real(string dir)
        {
            InstallTarget t = new InstallTarget();
            t.RegistryRoot = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            t.RegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\NetWatch";
            t.StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            t.DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (dir == null)
            {
                try
                {
                    using (RegistryKey key = t.RegistryRoot.OpenSubKey(t.RegistryPath))
                        if (key != null) dir = key.GetValue("InstallLocation") as string;
                }
                catch
                {
                }
            }
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetWatch");
            t.Dir = dir.TrimEnd('\\');
            return t;
        }

        public static InstallTarget Sandbox(string folder)
        {
            InstallTarget t = new InstallTarget();
            t.IsSandbox = true;
            t.Dir = Path.Combine(folder, "Program Files", "NetWatch");
            t.StartMenuDir = Path.Combine(folder, "Start Menu");
            t.DesktopDir = Path.Combine(folder, "Desktop");
            t.RegistryRoot = Registry.CurrentUser;
            t.RegistryPath = "Software\\NetWatchSetupSelfTest\\Uninstall\\NetWatch";
            Directory.CreateDirectory(t.StartMenuDir);
            Directory.CreateDirectory(t.DesktopDir);
            return t;
        }

        public bool IsInstalled { get { return File.Exists(ExePath); } }
    }

    internal static class Installer
    {
        public const string Version = "1.0.0";
        const string TraceSession = "NetWatchTrafficSession";

        public static bool IsInside(string file, string dir)
        {
            try
            {
                string full = Path.GetFullPath(file), root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Process ids of NetWatch copies that were started from the program folder.</summary>
        public static List<int> RunningCopies(InstallTarget target)
        {
            List<int> result = new List<int>();
            foreach (Process process in Process.GetProcessesByName("NetWatch"))
            {
                try
                {
                    string path = null;
                    try { path = process.MainModule.FileName; }
                    catch { }
                    if (path != null && IsInside(path, target.Dir)) result.Add(process.Id);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            return result;
        }

        public static void CloseRunningCopies(InstallTarget target)
        {
            List<int> pids = RunningCopies(target);
            foreach (int pid in pids)
            {
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch
                {
                }
            }
            if (pids.Count > 0 && !target.IsSandbox) StopTraceSession();
        }

        // NetWatch stops its Windows tracing session when it closes normally. After a forced close the
        // session would linger until the next restart, so stop it here (harmless if it does not exist).
        static void StopTraceSession()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "logman.exe"), "stop " + TraceSession + " -ets");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process p = Process.Start(info))
                    p.WaitForExit(8000);
            }
            catch
            {
            }
        }

        public static void Install(InstallTarget target, bool desktopShortcut)
        {
            Directory.CreateDirectory(target.Dir);

            using (Stream payload = typeof(Installer).Assembly.GetManifestResourceStream("NetWatch.Payload.exe"))
            {
                if (payload == null) throw new InvalidOperationException("This setup file is incomplete: the program is missing from it.");
                WriteFile(target.ExePath, payload);
            }

            string self = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target.UninstallerPath), StringComparison.OrdinalIgnoreCase))
            {
                using (FileStream source = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
                    WriteFile(target.UninstallerPath, source);
            }

            Directory.CreateDirectory(target.StartMenuDir);
            CreateShortcut(target.StartMenuLink, target.ExePath);
            if (desktopShortcut) CreateShortcut(target.DesktopLink, target.ExePath);
            else TryDelete(target.DesktopLink);

            long size = new FileInfo(target.ExePath).Length + new FileInfo(target.UninstallerPath).Length;
            using (RegistryKey key = target.RegistryRoot.CreateSubKey(target.RegistryPath))
            {
                key.SetValue("DisplayName", "NetWatch");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "NetWatch");
                key.SetValue("DisplayIcon", target.ExePath);
                key.SetValue("InstallLocation", target.Dir);
                key.SetValue("UninstallString", "\"" + target.UninstallerPath + "\" /uninstall");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        /// <summary>Removes NetWatch. Returns a list of things that could not be removed (normally empty).</summary>
        public static List<string> Uninstall(InstallTarget target, bool removeBlocks)
        {
            List<string> problems = new List<string>();

            if (removeBlocks && !target.IsSandbox)
            {
                try { FirewallManager.UnblockAll(); }
                catch (Exception ex) { problems.Add("The firewall blocks could not be removed: " + ex.Message); }
            }

            TryDelete(target.StartMenuLink);
            TryDelete(target.DesktopLink);
            try { target.RegistryRoot.DeleteSubKeyTree(target.RegistryPath, false); }
            catch (Exception ex) { problems.Add("The entry in \"Apps & features\" could not be removed: " + ex.Message); }

            // Only ever delete the two files setup put there, then the folder if it is empty.
            string self = Path.GetFullPath(Application.ExecutablePath);
            foreach (string file in new string[] { target.ExePath, target.UninstallerPath })
            {
                if (string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase)) continue;
                if (!DeleteWithRetry(file)) problems.Add("This file could not be deleted: " + file);
            }
            try
            {
                if (Directory.Exists(target.Dir) && Directory.GetFileSystemEntries(target.Dir).Length == 0)
                    Directory.Delete(target.Dir, false);
            }
            catch
            {
            }

            if (!target.IsSandbox)
            {
                // NetWatch's own settings and problem log
                TryDeleteFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetWatch"), "settings.txt");
                TryDeleteFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetWatch"), "log.txt");
            }
            return problems;
        }

        static void CreateShortcut(string linkPath, string targetPath)
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell", true);
            dynamic shell = Activator.CreateInstance(type);
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = Path.GetDirectoryName(targetPath);
            link.Description = "See which programs send and receive data";
            link.IconLocation = targetPath + ",0";
            link.Save();
        }

        static void WriteFile(string destination, Stream content)
        {
            IOException last = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (content.CanSeek) content.Position = 0;
                        content.CopyTo(output);
                    }
                    return;
                }
                catch (IOException ex)
                {
                    last = ex;                  // usually: the old copy is still closing
                    Thread.Sleep(300);
                }
            }
            throw new IOException("Could not write " + destination + ". " + (last != null ? last.Message : ""));
        }

        static bool DeleteWithRetry(string file)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (!File.Exists(file)) return true;
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    return true;
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
            return !File.Exists(file);
        }

        static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { }
        }

        // Deletes one known file and then the folder, but only if nothing else is in it.
        static void TryDeleteFolder(string folder, string knownFile)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                TryDelete(Path.Combine(folder, knownFile));
                if (Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder, false);
            }
            catch
            {
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        const int Ready = 0, Done = 1, Failed = 2;

        readonly bool _uninstall;
        readonly InstallTarget _target;
        readonly FlatButton _primary = new FlatButton();
        readonly FlatButton _cancel = new FlatButton();
        readonly CheckRow _check1 = new CheckRow();
        readonly CheckRow _check2 = new CheckRow();
        Image _logo;
        string _title = "", _subtitle = "", _body = "";
        int _state = Ready;
        int _blockedCount;
        bool _installedDesktop;
        bool _show1, _show2;

        public bool SelfTest;

        public SetupForm(bool uninstall, InstallTarget target)
        {
            Theme.Init();
            _uninstall = uninstall;
            _target = target;

            Text = uninstall ? "Remove NetWatch" : "NetWatch Setup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Panel;
            DoubleBuffered = true;
            KeyPreview = true;
            ClientSize = new Size(Theme.S(580), Theme.S(380));
            try
            {
                using (Stream stream = typeof(SetupForm).Assembly.GetManifestResourceStream("NetWatch.ico"))
                {
                    if (stream != null)
                    {
                        Icon = new Icon(stream);
                        stream.Position = 0;
                        using (Icon large = new Icon(stream, 64, 64)) _logo = large.ToBitmap();
                    }
                }
            }
            catch
            {
            }

            _check1.BackColor = Theme.Panel;
            _check2.BackColor = Theme.Panel;
            _primary.BackColor = Theme.Panel;
            _cancel.BackColor = Theme.Panel;
            _cancel.Kind = ButtonKind.Normal;
            _cancel.Text = "Cancel";
            _cancel.Click += delegate { Close(); };
            _primary.Click += delegate { OnPrimary(); };
            Controls.Add(_check1);
            Controls.Add(_check2);
            Controls.Add(_cancel);
            Controls.Add(_primary);

            if (uninstall)
            {
                if (!target.IsSandbox)
                {
                    try { _blockedCount = FirewallManager.ListBlocked().Count; }
                    catch { _blockedCount = 0; }
                }
                _title = "Remove NetWatch";
                _subtitle = "from this PC";
                _body = "NetWatch, its shortcuts and its settings will be removed.";
                if (_blockedCount > 0)
                {
                    _body += "\n\nNetWatch is blocking " + (_blockedCount == 1 ? "1 program" : _blockedCount.ToString(CultureInfo.InvariantCulture) + " programs") +
                             " at the moment. If you keep the blocks, they stay in Windows Firewall (rule group \"NetWatch\") and can only be removed there.";
                    _check1.Text = _blockedCount == 1 ? "Unblock that program as well" : "Unblock those programs as well";
                    _check1.Checked = true;
                    _show1 = true;
                }
                _primary.Kind = ButtonKind.Danger;
                _primary.Text = "Remove";
            }
            else
            {
                bool update = target.IsInstalled;
                _title = update ? "Update NetWatch" : "Install NetWatch";
                _subtitle = "version " + Installer.Version;
                _body = "NetWatch shows which programs on this PC send and receive data over the internet, how much, and to which servers. " +
                        "You can also block a program from there.\n\n" +
                        (update ? "The copy that is already installed will be replaced:\n" : "It will be installed in:\n") + target.Dir;
                _check1.Text = "Put a shortcut on the desktop";
                _check1.Checked = update ? File.Exists(target.DesktopLink) : true;
                _check2.Text = "Open NetWatch when setup has finished";
                _check2.Checked = true;
                _show1 = true;
                _show2 = true;
                _primary.Kind = ButtonKind.Primary;
                _primary.Accent = Theme.Down;
                _primary.Text = update ? "Update" : "Install";
            }
            LayoutControls();
        }

        protected override bool ShowWithoutActivation { get { return SelfTest; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!SelfTest) _primary.Focus();
        }

        Rectangle BodyRect
        {
            get { return new Rectangle(Theme.S(30), Theme.S(112), ClientSize.Width - Theme.S(60), Theme.S(150)); }
        }

        void LayoutControls()
        {
            int pad = Theme.S(30);
            int bodyHeight = Theme.WrappedHeight(_body, Theme.Body, BodyRect.Width);
            int y = BodyRect.Top + bodyHeight + Theme.S(18);
            // (Control.Visible reads false for everything until the window itself is shown, so the
            //  wanted state is kept in _show1 / _show2.)
            _check1.Visible = _show1;
            _check2.Visible = _show2;
            if (_show1)
            {
                _check1.Bounds = new Rectangle(pad, y, ClientSize.Width - 2 * pad, Theme.S(28));
                y += Theme.S(32);
            }
            if (_show2)
                _check2.Bounds = new Rectangle(pad, y, ClientSize.Width - 2 * pad, Theme.S(28));
            int buttonHeight = Theme.S(36);
            int buttonTop = ClientSize.Height - pad + Theme.S(4) - buttonHeight;
            int primaryWidth = Math.Max(Theme.S(120), _primary.PreferredWidth());
            _primary.Bounds = new Rectangle(ClientSize.Width - pad - primaryWidth, buttonTop, primaryWidth, buttonHeight);
            int cancelWidth = Math.Max(Theme.S(100), _cancel.PreferredWidth());
            _cancel.Bounds = new Rectangle(_primary.Left - Theme.S(10) - cancelWidth, buttonTop, cancelWidth, buttonHeight);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int pad = Theme.S(30);
            int logoSize = Theme.S(52);
            if (_logo != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(_logo, new Rectangle(pad, Theme.S(30), logoSize, logoSize));
            }
            int textX = pad + logoSize + Theme.S(16);
            Color titleColor = _state == Failed ? Theme.Danger : Theme.Text;
            Theme.Left(g, _title, Theme.Big, new Rectangle(textX, Theme.S(26), ClientSize.Width - textX - pad, Theme.S(38)), titleColor);
            Theme.Left(g, _subtitle, Theme.Body, new Rectangle(textX + 1, Theme.S(62), ClientSize.Width - textX - pad, Theme.S(22)), Theme.TextFaint);
            Theme.Wrapped(g, _body, Theme.Body, BodyRect, Color.FromArgb(205, 210, 222));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _state == Ready)
            {
                e.Handled = true;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        void OnPrimary()
        {
            if (_state != Ready)
            {
                bool open = !_uninstall && _state == Done && _show2 && _check2.Checked;
                if (open && !SelfTest)
                {
                    try { Process.Start(_target.ExePath); }
                    catch (Exception ex) { MessageDialog.Info(this, "NetWatch Setup", "NetWatch could not be opened", ex.Message); }
                }
                Close();
                return;
            }
            RunNow();
        }

        /// <summary>Does the install / removal. Public so the self-test can drive it.</summary>
        public void RunNow()
        {
            if (!_target.IsSandbox && Installer.RunningCopies(_target).Count > 0)
            {
                bool close = MessageDialog.Confirm(this, Text, "NetWatch is open",
                    "Setup has to close NetWatch to continue. The traffic totals it has counted so far will be lost.",
                    "Close NetWatch", "Cancel", ButtonKind.Primary, Theme.Down);
                if (!close) return;
            }

            Cursor = Cursors.WaitCursor;
            _primary.Enabled = false;
            _cancel.Enabled = false;
            Update();
            try
            {
                Installer.CloseRunningCopies(_target);
                if (_uninstall)
                {
                    bool unblock = _show1 && _check1.Checked;
                    List<string> problems = Installer.Uninstall(_target, unblock);
                    _state = Done;
                    _title = "NetWatch was removed";
                    _subtitle = "";
                    _body = problems.Count == 0
                        ? "Everything NetWatch installed is gone."
                        : "NetWatch was removed, but not everything could be cleaned up:\n\n" + string.Join("\n", problems.ToArray());
                    if (_blockedCount > 0 && !unblock)
                        _body += "\n\nThe firewall blocks were kept. You find them in Windows Firewall under the rule group \"NetWatch\".";
                }
                else
                {
                    _installedDesktop = _check1.Checked;
                    Installer.Install(_target, _installedDesktop);
                    _state = Done;
                    _title = "NetWatch is installed";
                    _subtitle = "version " + Installer.Version;
                    _body = "You can open it from the Start menu" + (_installedDesktop ? " or from the shortcut on the desktop." : ".") +
                            "\n\nEach time NetWatch starts, Windows asks for your permission. That is expected: " +
                            "Windows only lets administrators see which program sends what.\n\n" +
                            "To remove NetWatch later, use \"Apps & features\" in Windows Settings.";
                }
            }
            catch (Exception ex)
            {
                _state = Failed;
                _title = _uninstall ? "NetWatch could not be removed" : "NetWatch could not be installed";
                _subtitle = "";
                _body = ex.Message;
            }
            Cursor = Cursors.Default;

            _show1 = false;
            _show2 = !_uninstall && _state == Done;
            if (_show2) _check2.Text = "Open NetWatch now";
            _cancel.Visible = false;
            _primary.Enabled = true;
            _primary.Kind = ButtonKind.Primary;
            _primary.Accent = Theme.Down;
            _primary.Text = _state == Failed ? "Close" : (_uninstall ? "Close" : "Finish");
            LayoutControls();
        }
    }

    internal static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            bool uninstall = false;
            string dir = null, selfTestDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                else if (a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(5).Trim('"');
                else if (a.Equals("/selftest", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) selfTestDir = args[++i];
            }
            string self = Application.ExecutablePath;
            if (Path.GetFileName(self).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();

            if (selfTestDir != null) return RunSelfTest(selfTestDir);

            try
            {
                InstallTarget target = InstallTarget.Real(dir);
                if (uninstall)
                {
                    // Run from a temporary copy, so that the program folder can be removed completely.
                    if (Installer.IsInside(self, target.Dir))
                    {
                        string copy = Path.Combine(Path.GetTempPath(), "NetWatch-remove-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                        File.Copy(self, copy, true);
                        ProcessStartInfo info = new ProcessStartInfo(copy, "/uninstall \"/dir=" + target.Dir + "\"");
                        info.UseShellExecute = false;
                        Process.Start(info);
                        return 0;
                    }
                    Application.Run(new SetupForm(true, target));
                    DeleteSelfLater(self);
                    return 0;
                }
                Application.Run(new SetupForm(false, target));
                return 0;
            }
            catch (Exception ex)
            {
                MessageDialog.Info(null, "NetWatch Setup", "Setup ran into a problem", ex.Message);
                return 1;
            }
        }

        // The temporary copy used for removing cannot delete itself while it runs: let Windows do it a moment later.
        static void DeleteSelfLater(string self)
        {
            try
            {
                if (!Installer.IsInside(self, Path.GetTempPath())) return;
                if (!Path.GetFileName(self).StartsWith("NetWatch-remove-", StringComparison.OrdinalIgnoreCase)) return;
                ProcessStartInfo info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + self + "\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                Process.Start(info);
            }
            catch
            {
            }
        }

        // Gives a freshly shown window time to finish drawing before a picture of it is taken.
        static void Settle(Form form)
        {
            for (int i = 0; i < 4; i++)
            {
                form.Refresh();
                Application.DoEvents();
                Thread.Sleep(120);
            }
        }

        // Installs into and removes from a throw-away sandbox folder (and a test registry key under the
        // current user), saving pictures of each step. Touches nothing real and needs no administrator rights.
        static int RunSelfTest(string folder)
        {
            List<string> report = new List<string>();
            try
            {
                Directory.CreateDirectory(folder);
                InstallTarget target = InstallTarget.Sandbox(Path.Combine(folder, "sandbox"));

                using (SetupForm form = new SetupForm(false, target))
                {
                    form.SelfTest = true;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(60, 60);
                    form.Show();
                    Settle(form);
                    Theme.SaveWindowPicture(form, Path.Combine(folder, "s1-install.png"));
                    form.RunNow();
                    Settle(form);
                    Theme.SaveWindowPicture(form, Path.Combine(folder, "s2-installed.png"));
                }
                report.Add("exe installed: " + File.Exists(target.ExePath) + " (" + (File.Exists(target.ExePath) ? new FileInfo(target.ExePath).Length : 0) + " bytes)");
                report.Add("uninstaller installed: " + File.Exists(target.UninstallerPath));
                report.Add("start menu shortcut: " + File.Exists(target.StartMenuLink));
                report.Add("desktop shortcut: " + File.Exists(target.DesktopLink));
                using (RegistryKey key = target.RegistryRoot.OpenSubKey(target.RegistryPath))
                {
                    report.Add("registry entry: " + (key != null));
                    if (key != null)
                        foreach (string name in key.GetValueNames()) report.Add("    " + name + " = " + key.GetValue(name));
                }
                try
                {
                    Type type = Type.GetTypeFromProgID("WScript.Shell", true);
                    dynamic shell = Activator.CreateInstance(type);
                    dynamic link = shell.CreateShortcut(target.StartMenuLink);
                    report.Add("shortcut points to: " + (string)link.TargetPath);
                }
                catch (Exception ex)
                {
                    report.Add("shortcut check failed: " + ex.Message);
                }

                using (SetupForm form = new SetupForm(true, target))
                {
                    form.SelfTest = true;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(60, 60);
                    form.Show();
                    Settle(form);
                    Theme.SaveWindowPicture(form, Path.Combine(folder, "s3-remove.png"));
                    form.RunNow();
                    Settle(form);
                    Theme.SaveWindowPicture(form, Path.Combine(folder, "s4-removed.png"));
                }
                report.Add("after removal - exe: " + File.Exists(target.ExePath) + ", uninstaller: " + File.Exists(target.UninstallerPath) +
                           ", folder: " + Directory.Exists(target.Dir) + ", start menu: " + File.Exists(target.StartMenuLink) +
                           ", desktop: " + File.Exists(target.DesktopLink));
                using (RegistryKey key = target.RegistryRoot.OpenSubKey(target.RegistryPath))
                    report.Add("after removal - registry entry: " + (key != null));
                try { Registry.CurrentUser.DeleteSubKeyTree("Software\\NetWatchSetupSelfTest", false); }
                catch { }
            }
            catch (Exception ex)
            {
                report.Add("FAILED: " + ex);
            }
            File.WriteAllLines(Path.Combine(folder, "setup-selftest.txt"), report.ToArray());
            return 0;
        }
    }
}
