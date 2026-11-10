// NetWatch - invented traffic for testing the window without administrator rights.
//
// Started with "NetWatch.exe --demo". It attributes made-up traffic to programs that are really
// running on this PC (so names and icons look right) and marks the whole window as demo data.
// Nothing is captured, nothing is blocked and no firewall setting is read or written in this mode.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace NetWatch
{
    internal sealed class DemoFeeder : IDisposable
    {
        sealed class Flow
        {
            public int Pid;
            public IpKey Remote;
            public ushort Port;
            public bool Udp;
            public double UpRate, DownRate;     // bytes per second while active
            public double Activity;             // how often it is active (0..1)
            public bool Active;
        }

        // name, address, port, protocol ("" name = a server without a known name)
        static readonly string[][] Servers = new string[][] {
            new[] { "www.google.com", "142.250.185.68", "443", "tcp" },
            new[] { "clients4.google.com", "142.250.185.110", "443", "udp" },
            new[] { "api.github.com", "140.82.121.6", "443", "tcp" },
            new[] { "settings-win.data.microsoft.com", "51.104.136.2", "443", "tcp" },
            new[] { "v10.events.data.microsoft.com", "20.189.173.10", "443", "tcp" },
            new[] { "api.example-cloud.net", "160.79.104.10", "443", "tcp" },
            new[] { "cdn.example-games.com", "23.50.131.119", "80", "tcp" },
            new[] { "gateway.example-chat.gg", "162.159.135.234", "443", "tcp" },
            new[] { "audio.example-music.com", "35.186.224.25", "443", "tcp" },
            new[] { "downloads.example-engine.com", "13.224.189.12", "443", "tcp" },
            new[] { "ocsp.example-cert.com", "192.229.221.95", "80", "tcp" },
            new[] { "time.windows.com", "20.101.57.9", "123", "udp" },
            new[] { "dns.google", "8.8.8.8", "53", "udp" },
            new[] { "update.googleapis.com", "142.250.74.195", "443", "tcp" },
            new[] { "edge.microsoft.com", "13.107.21.239", "443", "tcp" },
            new[] { "login.live.com", "20.190.159.2", "443", "tcp" },
            new[] { "ipv6.example-video.com", "2a00:1450:4001:82a::200e", "443", "udp" },
            new[] { "", "185.199.108.153", "443", "tcp" },
            new[] { "", "104.18.32.47", "8443", "tcp" },
            new[] { "", "192.168.1.1", "53", "udp" },
            new[] { "", "192.168.1.40", "445", "tcp" },
            new[] { "", "224.0.0.251", "5353", "udp" },
        };

        static readonly string[] Preferred = new string[] {
            "chrome.exe", "msedge.exe", "firefox.exe", "claude.exe", "steam.exe", "discord.exe", "spotify.exe", "code.exe",
            "unrealeditor.exe", "epicgameslauncher.exe", "onedrive.exe", "explorer.exe", "searchapp.exe", "msmpeng.exe",
            "nvcontainer.exe", "blender.exe", "steamwebhelper.exe", "teams.exe", "slack.exe"
        };

        readonly TrafficModel _model;
        readonly List<Flow> _flows = new List<Flow>();
        readonly List<string> _paths = new List<string>();
        readonly Random _random = new Random(20261110);
        IpKey _local4, _local6;
        bool _hasLocal6;
        Thread _thread;
        volatile bool _stop;

        public DemoFeeder(TrafficModel model)
        {
            _model = model;
        }

        public List<string> SamplePaths(int count)
        {
            List<string> result = new List<string>();
            for (int i = 0; i < _paths.Count && result.Count < count; i += 2) result.Add(_paths[i]);
            return result;
        }

        public void Start()
        {
            bool found4 = false;
            foreach (IpKey key in _model.LocalAddresses())
            {
                if (!key.V6 && key.Kind == AddrKind.Lan && !found4) { _local4 = key; found4 = true; }
                if (key.V6 && key.Kind == AddrKind.Internet && !_hasLocal6) { _local6 = key; _hasLocal6 = true; }
            }
            if (!found4)
            {
                IpKey.TryParse("192.168.1.20", out _local4);
                _model.AddLocalAddressForTests(_local4);
            }
            if (!_hasLocal6)
            {
                IpKey.TryParse("2a02:1234:5678::20", out _local6);
                _model.AddLocalAddressForTests(_local6);
            }

            foreach (string[] server in Servers)
                if (server[0].Length > 0) _model.OnDns(server[0], server[1] + ";");

            BuildFlowsAndRun(PickProcesses());
        }

        /// <summary>
        /// Like Start(), but with invented programs instead of the ones running on this PC. Used to make
        /// the example pictures for the project page.
        /// </summary>
        public void StartShowcase()
        {
            IpKey.TryParse("192.168.1.20", out _local4);
            _model.AddLocalAddressForTests(_local4);
            IpKey.TryParse("2a02:1234:5678::20", out _local6);
            _model.AddLocalAddressForTests(_local6);
            _hasLocal6 = true;
            foreach (string[] server in Servers)
                if (server[0].Length > 0) _model.OnDns(server[0], server[1] + ";");

            const string dot = "  \u00B7  ";
            string[][] programs = new string[][] {
                // file, name, second line, kind ("" = ordinary, "service" = Windows service, anything else = why it is unusual)
                new[] { @"C:\Program Files\Example Browser\browser.exe", "Web Browser", "browser.exe" + dot + "Example Software", "" },
                new[] { @"C:\Program Files\Cloud Backup\backup.exe", "Cloud Backup", "backup.exe" + dot + "Example Storage Inc.", "" },
                new[] { @"C:\Windows\System32\svchost.exe", "Windows Update", "Windows service" + dot + "wuauserv", "service" },
                new[] { @"C:\Users\you\AppData\Local\Temp\updater.exe", "updater.exe", "updater.exe",
                        "It is not digitally signed, so nobody vouches for who made it.\nIt runs from a temporary or download folder.\nThe file does not say what it is or who made it." },
                new[] { @"C:\Program Files\Video Calls\calls.exe", "Video Calls", "calls.exe" + dot + "Example Meetings Ltd.", "" },
                new[] { @"C:\Program Files\Game Launcher\launcher.exe", "Game Launcher", "launcher.exe" + dot + "Example Games", "" },
                new[] { @"C:\Program Files\Music Player\music.exe", "Music Player", "music.exe" + dot + "Example Audio", "" },
                new[] { @"C:\Program Files\Mail\mail.exe", "Mail", "mail.exe" + dot + "Example Software", "" },
                new[] { @"C:\Windows\System32\svchost.exe", "DNS Client", "Windows service" + dot + "Dnscache", "service" },
                new[] { @"C:\Program Files\Code Editor\editor.exe", "Code Editor", "editor.exe" + dot + "Example Tools", "" },
                new[] { @"C:\Program Files\Graphics Driver\helper.exe", "Graphics Driver Helper", "helper.exe" + dot + "Example Graphics Corp.", "" },
            };
            List<int> pids = new List<int>();
            for (int i = 0; i < programs.Length; i++)
            {
                string[] p = programs[i];
                int pid = 900000 + i;
                _model.RegisterForShowcase(pid, p[0], p[1], p[2], p[3] == "service", p[3].Length > 0 && p[3] != "service" ? p[3] : null);
                _paths.Add(p[0]);
                pids.Add(pid);
            }
            BuildFlowsAndRun(pids);
        }

        void BuildFlowsAndRun(List<int> pids)
        {
            int next = 0;
            for (int i = 0; i < pids.Count; i++)
            {
                int flowCount = 1 + _random.Next(6);
                for (int f = 0; f < flowCount; f++)
                {
                    string[] server = Servers[next++ % Servers.Length];
                    Flow flow = new Flow();
                    flow.Pid = pids[i];
                    IpKey.TryParse(server[1], out flow.Remote);
                    flow.Port = ushort.Parse(server[2], CultureInfo.InvariantCulture);
                    flow.Udp = server[3] == "udp";
                    double scale = Math.Pow(10, _random.NextDouble() * 2.6);
                    flow.UpRate = 250 * scale * (i == 1 ? 9 : 1);
                    flow.DownRate = flow.UpRate * (1.5 + _random.NextDouble() * 22);
                    flow.Activity = 0.1 + _random.NextDouble() * 0.9;
                    flow.Active = _random.NextDouble() < flow.Activity;
                    _flows.Add(flow);
                }
            }

            long now = DateTime.UtcNow.ToFileTimeUtc();
            for (int s = 80; s >= 1; s--) Step(now - s * 10000000L, 1.0);

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "NetWatch demo";
            _thread.Start();
        }

        List<int> PickProcesses()
        {
            Dictionary<string, int> byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<int> serviceHosts = new List<int>();
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    int pid = process.Id;
                    if (pid <= 4) continue;
                    string path = ProcessUtil.GetImagePath(pid);
                    if (path == null) continue;
                    if (path.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ProcessUtil.ServicesForPid(pid).Count > 0) serviceHosts.Add(pid);
                        continue;
                    }
                    if (!byPath.ContainsKey(path)) byPath[path] = pid;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            List<int> result = new List<int>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Preferred)
            {
                foreach (KeyValuePair<string, int> item in byPath)
                {
                    if (!item.Key.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase) || !used.Add(item.Key)) continue;
                    result.Add(item.Value);
                    _paths.Add(item.Key);
                    break;
                }
            }
            List<string> others = new List<string>(byPath.Keys);
            others.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string path in others)
            {
                if (result.Count >= 13) break;
                if (!used.Add(path)) continue;
                result.Add(byPath[path]);
                _paths.Add(path);
            }
            serviceHosts.Sort();
            for (int i = 0; i < serviceHosts.Count && i < 3; i++) result.Add(serviceHosts[i * Math.Max(1, serviceHosts.Count / 3)]);
            result.Add(4);      // the Windows kernel itself
            return result;
        }

        void Loop()
        {
            while (!_stop)
            {
                Thread.Sleep(250);
                if (_stop) break;
                try { Step(DateTime.UtcNow.ToFileTimeUtc(), 0.25); }
                catch { }
            }
        }

        void Step(long timeStamp, double seconds)
        {
            foreach (Flow flow in _flows)
            {
                if (_random.NextDouble() < 0.10 * seconds * 4) flow.Active = _random.NextDouble() < flow.Activity;
                if (!flow.Active) continue;
                int up = (int)(flow.UpRate * seconds * (0.4 + _random.NextDouble() * 1.2));
                int down = (int)(flow.DownRate * seconds * (0.4 + _random.NextDouble() * 1.2));
                IpKey local = flow.Remote.V6 ? _local6 : _local4;
                ushort localPort = (ushort)(50000 + (flow.Pid + flow.Port) % 9000);
                if (up > 0)
                    _model.OnNetwork(flow.Pid, up, true, flow.Udp, flow.Remote.V6, timeStamp,
                        local.Hi, local.Lo, localPort, flow.Remote.Hi, flow.Remote.Lo, flow.Port);
                if (down > 0)
                    _model.OnNetwork(flow.Pid, down, false, flow.Udp, flow.Remote.V6, timeStamp,
                        flow.Remote.Hi, flow.Remote.Lo, flow.Port, local.Hi, local.Lo, localPort);
            }
        }

        public void Dispose()
        {
            _stop = true;
        }
    }
}
