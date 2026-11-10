// NetWatch - the bookkeeping: which program sent / received how much, and to which servers.
//
// Events arrive from EtwSession on its own thread; the window reads summaries once a second.
// Everything here is protected by one lock (Sync).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace NetWatch
{
    internal enum AddrKind : byte { Internet, Lan, Multicast, Loopback, Unspecified }

    /// <summary>An IPv4 or IPv6 address as plain numbers (cheap to compare and to use as a dictionary key).</summary>
    internal struct IpKey : IEquatable<IpKey>
    {
        public ulong Hi, Lo;
        public bool V6;

        public static IpKey Make(bool v6, ulong hi, ulong lo)
        {
            IpKey k;
            if (v6 && hi == 0 && (lo & 0xFFFFFFFFUL) == 0xFFFF0000UL)
            {
                // IPv4 address written as IPv6 (::ffff:a.b.c.d) - treat it as the IPv4 address it is
                k.Hi = 0;
                k.Lo = lo >> 32;
                k.V6 = false;
            }
            else if (v6)
            {
                k.Hi = hi;
                k.Lo = lo;
                k.V6 = true;
            }
            else
            {
                k.Hi = 0;
                k.Lo = lo & 0xFFFFFFFFUL;
                k.V6 = false;
            }
            return k;
        }

        public static IpKey From(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            if (b.Length == 4) return Make(false, 0, BitConverter.ToUInt32(b, 0));
            return Make(true, BitConverter.ToUInt64(b, 0), BitConverter.ToUInt64(b, 8));
        }

        public static bool TryParse(string text, out IpKey key)
        {
            IPAddress address;
            if (!string.IsNullOrEmpty(text) && (text.IndexOf('.') >= 0 || text.IndexOf(':') >= 0) &&
                IPAddress.TryParse(text, out address))
            {
                key = From(address);
                return true;
            }
            key = new IpKey();
            return false;
        }

        public byte[] ToBytes()
        {
            if (!V6) return BitConverter.GetBytes((uint)Lo);
            byte[] b = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(Hi), 0, b, 0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(Lo), 0, b, 8, 8);
            return b;
        }

        public override string ToString()
        {
            return new IPAddress(ToBytes()).ToString();
        }

        /// <summary>Is this address on the internet, on the local network, or the PC itself?</summary>
        public AddrKind Kind
        {
            get
            {
                if (!V6)
                {
                    uint a = (uint)Lo;
                    byte b0 = (byte)a, b1 = (byte)(a >> 8);
                    if (a == 0) return AddrKind.Unspecified;
                    if (b0 == 127) return AddrKind.Loopback;
                    if (a == 0xFFFFFFFF) return AddrKind.Multicast;                 // broadcast
                    if (b0 >= 224 && b0 <= 239) return AddrKind.Multicast;
                    if (b0 == 10) return AddrKind.Lan;
                    if (b0 == 172 && b1 >= 16 && b1 <= 31) return AddrKind.Lan;
                    if (b0 == 192 && b1 == 168) return AddrKind.Lan;
                    if (b0 == 169 && b1 == 254) return AddrKind.Lan;                // self-assigned
                    if (b0 == 100 && b1 >= 64 && b1 <= 127) return AddrKind.Lan;    // provider / VPN internal range
                    return AddrKind.Internet;
                }
                if (Hi == 0 && Lo == 0) return AddrKind.Unspecified;
                if (Hi == 0 && Lo == 0x0100000000000000UL) return AddrKind.Loopback; // ::1
                byte c0 = (byte)Hi, c1 = (byte)(Hi >> 8);
                if (c0 == 0xFF) return AddrKind.Multicast;
                if (c0 == 0xFE && (c1 & 0xC0) == 0x80) return AddrKind.Lan;          // fe80::/10 link-local
                if ((c0 & 0xFE) == 0xFC) return AddrKind.Lan;                         // fc00::/7 private
                return AddrKind.Internet;
            }
        }

        public bool Equals(IpKey other)
        {
            return Lo == other.Lo && Hi == other.Hi && V6 == other.V6;
        }

        public override bool Equals(object obj)
        {
            return obj is IpKey && Equals((IpKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                ulong h = (Lo * 0x9E3779B97F4A7C15UL) ^ (Hi * 0xC2B2AE3D27D4EB4FUL);
                return (int)(h ^ (h >> 32)) ^ (V6 ? 0x5bd1e995 : 0);
            }
        }
    }

    /// <summary>Bytes per second for roughly the last two minutes, indexed by wall-clock second.</summary>
    internal sealed class SecondSeries
    {
        const int Size = 128;                       // power of two, comfortably more than the 60 s we show
        readonly long[] _values = new long[Size];
        long _newest = long.MinValue;

        public void Add(long second, long amount)
        {
            if (_newest == long.MinValue)
            {
                _newest = second;
            }
            else if (second > _newest)
            {
                if (second - _newest >= Size) Array.Clear(_values, 0, Size);
                else for (long s = _newest + 1; s <= second; s++) _values[s & (Size - 1)] = 0;
                _newest = second;
            }
            else if (second <= _newest - Size)
            {
                return;                             // far too old to matter
            }
            _values[second & (Size - 1)] += amount;
        }

        public long Get(long second)
        {
            if (_newest == long.MinValue || second > _newest || second <= _newest - Size) return 0;
            return _values[second & (Size - 1)];
        }
    }

    /// <summary>One server (remote address) a program talked to.</summary>
    internal sealed class RemoteEntry
    {
        public IpKey Ip;
        public bool IsLan;
        public bool IsOverflow;                     // "all other addresses" bucket of a very busy program
        public long Sent, Received;
        public long LastSent, LastRecv;             // UTC FILETIME of the newest event
        public string Host;                         // name, once known
        public string AddressText;
        public string PortText;
        int _portTextCount = -1;
        readonly ushort[] _ports = new ushort[3];
        readonly bool[] _portUdp = new bool[3];
        int _portCount;
        bool _morePorts;

        public void AddPort(ushort port, bool udp)
        {
            for (int i = 0; i < _portCount; i++)
                if (_ports[i] == port && _portUdp[i] == udp) return;
            if (_portCount < _ports.Length)
            {
                _ports[_portCount] = port;
                _portUdp[_portCount] = udp;
                _portCount++;
            }
            else
            {
                _morePorts = true;
            }
        }

        public string GetPortText()
        {
            int stamp = _portCount + (_morePorts ? 100 : 0);
            if (stamp == _portTextCount) return PortText;
            _portTextCount = stamp;
            if (_portCount == 0)
            {
                PortText = "";
            }
            else if (_portCount == 1 && !_morePorts)
            {
                PortText = PortNames.Describe(_ports[0], _portUdp[0]);
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < _portCount; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(_ports[i].ToString(CultureInfo.InvariantCulture));
                    if (_portUdp[i]) sb.Append(" UDP");
                }
                if (_morePorts) sb.Append(", \u2026");
                PortText = sb.ToString();
            }
            return PortText;
        }
    }

    /// <summary>Plain-language names for well-known port numbers.</summary>
    internal static class PortNames
    {
        public static string Describe(ushort port, bool udp)
        {
            string number = port.ToString(CultureInfo.InvariantCulture);
            string name = null;
            switch (port)
            {
                case 20: case 21: name = "FTP file transfer"; break;
                case 22: name = "SSH"; break;
                case 25: case 465: case 587: name = "sending e-mail"; break;
                case 53: name = "DNS name lookup"; break;
                case 67: case 68: name = "DHCP"; break;
                case 80: name = "web (not encrypted)"; break;
                case 110: case 995: name = "e-mail (POP)"; break;
                case 123: name = "clock sync"; break;
                case 137: case 138: case 139: name = "Windows network names"; break;
                case 143: case 993: name = "e-mail (IMAP)"; break;
                case 443: name = udp ? "web (encrypted, QUIC)" : "web (encrypted)"; break;
                case 445: name = "Windows file sharing"; break;
                case 853: name = "DNS (encrypted)"; break;
                case 1900: name = "device discovery"; break;
                case 3389: name = "Remote Desktop"; break;
                case 3478: case 3479: name = "calls / voice chat"; break;
                case 5222: case 5223: name = "messaging"; break;
                case 5228: name = "Google services"; break;
                case 5353: name = "local name lookup"; break;
                case 5355: name = "local name lookup"; break;
                case 8080: case 8443: name = "web (alternative)"; break;
            }
            if (name == null) return udp ? number + " UDP" : number;
            return number + "  \u00B7  " + name;
        }
    }

    /// <summary>One program (all its running copies together).</summary>
    internal sealed class AppEntry
    {
        public int Id;
        public string Key, Path, FileName, Name, Company, Sub;
        public bool IsSystem, IsSvcHost, IsSelf;
        public bool IsWindows;          // Windows itself or a program from the Windows folder
        public string Warning;          // why the program looks unusual (null = nothing unusual), see TrustCheck
        public Image Icon;
        public int LivePids;
        public bool AlwaysRunning;

        // index = (local network ? 2 : 0) + (received ? 1 : 0)
        public readonly long[] Total = new long[4];
        public readonly int[] Hosts = new int[4];
        public readonly long[] Last = new long[4];
        public readonly SecondSeries[] Series = new SecondSeries[] { new SecondSeries(), new SecondSeries(), new SecondSeries(), new SecondSeries() };

        public readonly Dictionary<IpKey, RemoteEntry> Remotes = new Dictionary<IpKey, RemoteEntry>();
        public RemoteEntry OverflowInternet, OverflowLan;
    }

    internal sealed class ProcInfo
    {
        public int Pid;
        public string Path;
        public bool Resolved;
        public bool Exited;
        public AppEntry App;
    }

    /// <summary>What the window shows for one program. Reused from second to second.</summary>
    internal sealed class AppSnapshot
    {
        public const int HistoryLength = 60;

        public int Id;
        public string Name = "", Sub = "", Path, FileName;
        public Image Icon;
        public bool IsSystem, IsSelf, IsWindows, WindowsPart, Running, CanBlock, Blocked;
        public string Warning;
        public long Sent, Received;
        public double SentRate, RecvRate;
        public int SentHosts, RecvHosts;
        public long LastSent, LastRecv;
        public readonly float[] SentHistory = new float[HistoryLength];
        public readonly float[] RecvHistory = new float[HistoryLength];
        public float SentPeak, RecvPeak;
    }

    internal sealed class TotalsSnapshot
    {
        public long Sent, Received;
        public double SentRate, RecvRate;
        public int SendingApps, ReceivingApps;
        public readonly float[] SentHistory = new float[AppSnapshot.HistoryLength];
        public readonly float[] RecvHistory = new float[AppSnapshot.HistoryLength];
        public float SentPeak, RecvPeak;
    }

    internal sealed class RemoteSnapshot
    {
        public IpKey Ip;
        public string Host, Address, PortText;
        public long Sent, Received, LastSent, LastRecv;
        public bool IsLan, IsOverflow;
    }

    internal sealed class TrafficModel : IEtwSink
    {
        public const int MaxRemotesPerApp = 3000;

        public readonly object Sync = new object();

        readonly Dictionary<int, ProcInfo> _procs = new Dictionary<int, ProcInfo>();
        readonly Dictionary<string, AppEntry> _appsByKey = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, AppEntry> _appsById = new Dictionary<int, AppEntry>();
        readonly List<AppEntry> _apps = new List<AppEntry>();
        readonly Dictionary<IpKey, string> _hostByIp = new Dictionary<IpKey, string>();
        volatile HashSet<IpKey> _local = new HashSet<IpKey>();
        volatile bool _localStale = true;
        long _localRefreshed;
        int _nextId = 1;
        long _networkEvents;
        readonly string _windowsDir;
        readonly string _selfPath;

        DateTime _startedUtc = DateTime.UtcNow;

        // optional trace of the first raw events (diagnostics only)
        List<string> _rawLog;
        int _rawLogLimit;
        readonly int[] _rawPerKind = new int[8];
        readonly long[] _kindCounts = new long[8];      // events per kind: bit0 = received, bit1 = UDP, bit2 = IPv6
        long _processStarts, _processStops, _dnsAnswers, _maxLagMs;
        readonly List<string> _recentStarts = new List<string>();

        static readonly string[] ProtectedWindowsFiles = new string[] {
            "svchost.exe", "lsass.exe", "services.exe", "csrss.exe", "smss.exe", "wininit.exe", "winlogon.exe", "ntoskrnl.exe"
        };

        public TrafficModel()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            _windowsDir = string.IsNullOrEmpty(windows) ? "C:\\Windows" : windows.TrimEnd('\\');
            try { _selfPath = System.Windows.Forms.Application.ExecutablePath; }
            catch { _selfPath = ""; }
            RefreshLocalAddresses();
        }

        public DateTime StartedUtc { get { lock (Sync) return _startedUtc; } }
        public long NetworkEvents { get { return Interlocked.Read(ref _networkEvents); } }

        public void EnableRawLog(int limit)
        {
            lock (Sync)
            {
                _rawLog = new List<string>();
                _rawLogLimit = limit;
            }
        }

        // ------------------------------------------------------------ addresses of this PC

        public void RefreshLocalAddresses()
        {
            HashSet<IpKey> set = new HashSet<IpKey>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                        set.Add(IpKey.From(address.Address));
                }
            }
            catch
            {
            }
            _local = set;
            _localStale = false;
            _localRefreshed = Stopwatch.GetTimestamp();
        }

        public List<IpKey> LocalAddresses()
        {
            return new List<IpKey>(_local);
        }

        internal void AddLocalAddressForTests(IpKey address)
        {
            HashSet<IpKey> set = new HashSet<IpKey>(_local);
            set.Add(address);
            _local = set;
        }

        // ------------------------------------------------------------------ incoming events

        public void OnNetwork(int pid, int size, bool isSend, bool isUdp, bool isV6, long timeStamp,
                              ulong sHi, ulong sLo, ushort sPort, ulong dHi, ulong dLo, ushort dPort)
        {
            Interlocked.Increment(ref _networkEvents);
            IpKey source = IpKey.Make(isV6, sHi, sLo);
            IpKey destination = IpKey.Make(isV6, dHi, dLo);

            int kind = (isSend ? 0 : 1) | (isUdp ? 2 : 0) | (isV6 ? 4 : 0);
            _kindCounts[kind]++;
            if (_rawLog != null) TraceRaw(kind, timeStamp, string.Format(CultureInfo.InvariantCulture,
                "{0} {1}{2} pid={3} size={4} src={5}:{6} dst={7}:{8}",
                isSend ? "SEND" : "RECV", isUdp ? "UDP" : "TCP", isV6 ? "6" : "4", pid, size, source, sPort, destination, dPort));

            if (size <= 0) return;
            AddrKind sourceKind = source.Kind, destinationKind = destination.Kind;
            if (sourceKind == AddrKind.Loopback || destinationKind == AddrKind.Loopback) return;   // the PC talking to itself

            HashSet<IpKey> local = _local;
            bool sourceLocal = local.Contains(source), destinationLocal = local.Contains(destination);
            if (sourceLocal && destinationLocal) return;                                            // also the PC talking to itself
            // the PC hearing its own announcement to a group address
            if (!isSend && isUdp && sourceLocal && destinationKind == AddrKind.Multicast) return;

            IpKey remote;
            ushort remotePort;
            AddrKind remoteKind;
            if (sourceLocal)
            {
                remote = destination; remotePort = dPort; remoteKind = destinationKind;
            }
            else if (destinationLocal)
            {
                remote = source; remotePort = sPort; remoteKind = sourceKind;
            }
            else
            {
                // Neither side is an address we know as ours (a group address, or our list is out of date).
                bool sourceIsOther = isSend ? false : destinationKind == AddrKind.Multicast || sourceKind != AddrKind.Multicast;
                if (isSend || !sourceIsOther)
                {
                    remote = destination; remotePort = dPort; remoteKind = destinationKind;
                }
                else
                {
                    remote = source; remotePort = sPort; remoteKind = sourceKind;
                }
                if (sourceKind != AddrKind.Multicast && destinationKind != AddrKind.Multicast &&
                    sourceKind != AddrKind.Unspecified && destinationKind != AddrKind.Unspecified)
                    _localStale = true;
            }
            if (remoteKind == AddrKind.Loopback) return;
            Account(pid, size, isSend, isUdp, timeStamp, remote, remotePort, remoteKind);
        }

        void TraceRaw(int kind, long timeStamp, string text)
        {
            long lagMs = (DateTime.UtcNow.ToFileTimeUtc() - timeStamp) / 10000L;
            lock (Sync)
            {
                if (_rawLog == null) return;
                if (lagMs > _maxLagMs) _maxLagMs = lagMs;
                if (_rawLog.Count < _rawLogLimit && _rawPerKind[kind] < 12)
                {
                    _rawPerKind[kind]++;
                    _rawLog.Add(text + " lag=" + lagMs.ToString(CultureInfo.InvariantCulture) + "ms");
                }
            }
        }

        // Adds one send or receive to the books of the program that owns the process.
        void Account(int pid, int size, bool isSend, bool isUdp, long timeStamp, IpKey remote, ushort remotePort, AddrKind remoteKind)
        {
            bool lan = remoteKind == AddrKind.Lan || remoteKind == AddrKind.Multicast;
            long second = timeStamp / 10000000L;
            int index = (lan ? 2 : 0) + (isSend ? 0 : 1);

            lock (Sync)
            {
                AppEntry app = AppForPid(pid);
                app.Total[index] += size;
                app.Last[index] = timeStamp;
                app.Series[index].Add(second, size);

                RemoteEntry entry;
                if (!app.Remotes.TryGetValue(remote, out entry))
                {
                    if (app.Remotes.Count >= MaxRemotesPerApp)
                    {
                        entry = lan ? app.OverflowLan : app.OverflowInternet;
                        if (entry == null)
                        {
                            entry = new RemoteEntry();
                            entry.IsLan = lan;
                            entry.IsOverflow = true;
                            if (lan) app.OverflowLan = entry; else app.OverflowInternet = entry;
                        }
                    }
                    else
                    {
                        entry = new RemoteEntry();
                        entry.Ip = remote;
                        entry.IsLan = lan;
                        _hostByIp.TryGetValue(remote, out entry.Host);
                        app.Remotes[remote] = entry;
                    }
                }
                if (isSend)
                {
                    if (entry.Sent == 0) app.Hosts[index]++;
                    entry.Sent += size;
                    entry.LastSent = timeStamp;
                }
                else
                {
                    if (entry.Received == 0) app.Hosts[index]++;
                    entry.Received += size;
                    entry.LastRecv = timeStamp;
                }
                if (!entry.IsOverflow) entry.AddPort(remotePort, isUdp);
            }
        }

        public void OnProcessStart(int pid, string ntImagePath)
        {
            string path = ProcessUtil.NtPathToDos(ntImagePath);
            if (path != null && path.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase))
                ProcessUtil.InvalidateServices();
            lock (Sync)
            {
                _processStarts++;
                if (_rawLog != null && _recentStarts.Count < 8)
                    _recentStarts.Add(pid.ToString(CultureInfo.InvariantCulture) + " -> " + (path ?? "(no path)") + "   [" + (ntImagePath ?? "null") + "]");
                ProcInfo old;
                if (_procs.TryGetValue(pid, out old) && old.App != null && !old.Exited)
                    old.App.LivePids--;
                ProcInfo info = new ProcInfo();
                info.Pid = pid;
                info.Path = path;
                info.Resolved = path != null;
                _procs[pid] = info;
            }
        }

        public void OnProcessStop(int pid)
        {
            lock (Sync)
            {
                _processStops++;
                ProcInfo info;
                if (!_procs.TryGetValue(pid, out info)) return;
                if (info.App == null)
                {
                    _procs.Remove(pid);         // never used the network: forget it
                    return;
                }
                if (!info.Exited)
                {
                    info.Exited = true;
                    info.App.LivePids--;
                }
            }
        }

        public void OnDns(string queryName, string queryResults)
        {
            if (string.IsNullOrEmpty(queryName) || string.IsNullOrEmpty(queryResults)) return;
            string name = queryName.Trim().TrimEnd('.').ToLowerInvariant();
            if (name.Length == 0) return;
            string[] parts = queryResults.Split(';');
            lock (Sync)
            {
                _dnsAnswers++;
                foreach (string raw in parts)
                {
                    string part = raw.Trim();
                    if (part.Length < 3 || part.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) continue;
                    IpKey ip;
                    if (!IpKey.TryParse(part, out ip)) continue;
                    if (_hostByIp.Count > 200000) _hostByIp.Clear();
                    _hostByIp[ip] = name;
                }
            }
        }

        /// <summary>Remembers a name for an address unless a better one (from a real lookup) is already known.</summary>
        public void SetHostIfMissing(IpKey ip, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (Sync)
            {
                if (!_hostByIp.ContainsKey(ip)) _hostByIp[ip] = name;
            }
        }

        // ------------------------------------------------------------------- programs

        AppEntry AppForPid(int pid)
        {
            ProcInfo info;
            if (!_procs.TryGetValue(pid, out info))
            {
                info = new ProcInfo();
                info.Pid = pid;
                _procs[pid] = info;
            }
            if (!info.Resolved)
            {
                info.Path = ProcessUtil.GetImagePath(pid);
                info.Resolved = true;
            }
            if (info.App == null)
            {
                info.App = GetOrCreateApp(pid, info.Path);
                if (!info.Exited) info.App.LivePids++;
            }
            return info.App;
        }

        AppEntry GetOrCreateApp(int pid, string path)
        {
            string key, name, sub, fileName = null;
            bool system = false, svcHost = false, always = false, self = false;

            if (pid <= 4)
            {
                key = "::kernel";
                name = "Windows system";
                sub = "Built-in Windows networking (file sharing, device discovery)";
                system = true;
                always = true;
                path = null;
            }
            else if (string.IsNullOrEmpty(path))
            {
                key = "::pid:" + pid.ToString(CultureInfo.InvariantCulture);
                name = "Unknown program";
                sub = "Process " + pid.ToString(CultureInfo.InvariantCulture) + " closed before it could be identified";
            }
            else
            {
                fileName = System.IO.Path.GetFileName(path);
                key = path;
                name = fileName;
                sub = fileName;
                if (IsWindowsFile(path, "svchost.exe"))
                {
                    svcHost = true;
                    system = true;
                    List<ServiceInfo> services = ProcessUtil.ServicesForPid(pid);
                    if (services.Count == 0)
                    {
                        key = path + "|pid:" + pid.ToString(CultureInfo.InvariantCulture);
                        name = "Windows service host";
                        sub = "svchost.exe";
                    }
                    else
                    {
                        StringBuilder names = new StringBuilder();
                        for (int i = 0; i < services.Count; i++)
                        {
                            if (i > 0) names.Append(", ");
                            names.Append(services[i].Name);
                        }
                        key = path + "|" + names;
                        name = services[0].DisplayName;
                        if (services.Count > 1)
                            name += " + " + (services.Count - 1).ToString(CultureInfo.InvariantCulture) + " more";
                        sub = (services.Count == 1 ? "Windows service  \u00B7  " : "Windows services  \u00B7  ") + names;
                    }
                }
                else if (string.Equals(path, _selfPath, StringComparison.OrdinalIgnoreCase))
                {
                    self = true;
                }
                else if (IsProtected(path))
                {
                    system = true;
                }
            }

            AppEntry app;
            if (_appsByKey.TryGetValue(key, out app)) return app;

            app = new AppEntry();
            app.Id = _nextId++;
            app.Key = key;
            app.Path = path;
            app.FileName = fileName;
            app.Name = name;
            app.Sub = sub;
            app.IsSystem = system;
            app.IsSelf = self;
            app.IsWindows = always || svcHost || (path != null &&
                path.StartsWith(_windowsDir + "\\", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(_windowsDir + "\\Temp\\", StringComparison.OrdinalIgnoreCase));
            if (path == null && !always)
                app.Warning = "It closed again before NetWatch could see which program file it was.";
            app.IsSvcHost = svcHost;
            app.AlwaysRunning = always;
            _appsByKey[key] = app;
            _appsById[app.Id] = app;
            _apps.Add(app);
            if (path != null) ThreadPool.QueueUserWorkItem(Enrich, app);
            return app;
        }

        bool IsWindowsFile(string path, string fileName)
        {
            return path.StartsWith(_windowsDir + "\\", StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase);
        }

        bool IsProtected(string path)
        {
            foreach (string file in ProtectedWindowsFiles)
                if (IsWindowsFile(path, file)) return true;
            return false;
        }

        // Runs on a pool thread: reads the program's proper name, maker and icon from its file.
        void Enrich(object state)
        {
            AppEntry app = (AppEntry)state;
            string name = null, company = null;
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(app.Path);
                name = Clean(version.FileDescription);
                if (name == null) name = Clean(version.ProductName);
                company = Clean(version.CompanyName);
            }
            catch
            {
            }
            Image icon = IconCache.Load(app.Path);
            string warning = null;
            if (!app.IsSelf)
            {
                try { warning = TrustCheck.Assess(app.Path, name, company, _windowsDir); }
                catch { }
            }
            lock (Sync)
            {
                app.Company = company;
                app.Icon = icon;
                app.Warning = warning;
                if (!app.IsSvcHost)
                {
                    if (name != null) app.Name = name;
                    app.Sub = company != null ? app.FileName + "  \u00B7  " + company : app.FileName;
                }
            }
        }

        /// <summary>
        /// Showcase hook (used only to make the example pictures for the project page): adds an invented
        /// program, so that no real program of the PC the pictures are made on appears in them.
        /// </summary>
        internal void RegisterForShowcase(int pid, string path, string name, string sub, bool windowsService, string warning)
        {
            lock (Sync)
            {
                AppEntry app = new AppEntry();
                app.Id = _nextId++;
                app.Key = path + "|" + name;
                app.Path = path;
                app.FileName = System.IO.Path.GetFileName(path);
                app.Name = name;
                app.Sub = sub;
                app.IsSvcHost = windowsService;
                app.IsSystem = windowsService;
                app.IsWindows = windowsService;
                app.Warning = warning;
                app.LivePids = 1;
                _appsByKey[app.Key] = app;
                _appsById[app.Id] = app;
                _apps.Add(app);

                ProcInfo info = new ProcInfo();
                info.Pid = pid;
                info.Path = path;
                info.Resolved = true;
                info.App = app;
                _procs[pid] = info;
                _startedUtc = DateTime.UtcNow.AddMinutes(-12);
            }
        }

        /// <summary>Self-test hook: marks a program as unusual so the red highlight can be looked at.</summary>
        internal void MarkUnusualForTests(int appId, string warning)
        {
            lock (Sync)
            {
                AppEntry app;
                if (_appsById.TryGetValue(appId, out app)) app.Warning = warning;
            }
        }

        static string Clean(string text)
        {
            if (text == null) return null;
            text = text.Trim();
            return text.Length == 0 ? null : text;
        }

        // -------------------------------------------------------------------- housekeeping

        /// <summary>Called every few seconds from a background timer.</summary>
        public void Maintain()
        {
            long now = Stopwatch.GetTimestamp();
            if (_localStale || now - _localRefreshed > Stopwatch.Frequency * 30)
                RefreshLocalAddresses();

            // Notice programs that closed without us being told (only matters if process events are unavailable).
            List<ProcInfo> live = new List<ProcInfo>();
            lock (Sync)
            {
                foreach (ProcInfo info in _procs.Values)
                    if (info.App != null && !info.Exited && info.Pid > 4 && info.Path != null) live.Add(info);
            }
            List<KeyValuePair<ProcInfo, string>> changed = null;
            foreach (ProcInfo info in live)
            {
                string current = ProcessUtil.GetImagePath(info.Pid);
                if (current == null || !string.Equals(current, info.Path, StringComparison.OrdinalIgnoreCase))
                {
                    if (changed == null) changed = new List<KeyValuePair<ProcInfo, string>>();
                    changed.Add(new KeyValuePair<ProcInfo, string>(info, current));
                }
            }
            if (changed == null) return;
            lock (Sync)
            {
                foreach (KeyValuePair<ProcInfo, string> item in changed)
                {
                    ProcInfo info = item.Key, current;
                    if (!_procs.TryGetValue(info.Pid, out current) || current != info || info.Exited) continue;
                    info.Exited = true;
                    if (info.App != null) info.App.LivePids--;
                    if (item.Value != null) _procs.Remove(info.Pid);    // the id now belongs to another program
                }
            }
        }

        /// <summary>Forgets all counted traffic and starts again from zero.</summary>
        public void Reset()
        {
            lock (Sync)
            {
                _apps.Clear();
                _appsByKey.Clear();
                _appsById.Clear();
                List<int> gone = new List<int>();
                foreach (ProcInfo info in _procs.Values)
                {
                    info.App = null;
                    if (info.Exited) gone.Add(info.Pid);
                }
                foreach (int pid in gone) _procs.Remove(pid);
                _startedUtc = DateTime.UtcNow;
            }
        }

        // ----------------------------------------------------------------------- reading

        /// <summary>
        /// Fills "output" with every program that has traffic. nowSecond is the current wall-clock second;
        /// the 60-second histories end four seconds before it, because Windows delivers events slightly late.
        /// </summary>
        public void Snapshot(bool includeLan, long nowSecond, Dictionary<int, AppSnapshot> cache, List<AppSnapshot> output, TotalsSnapshot totals)
        {
            output.Clear();
            const int n = AppSnapshot.HistoryLength;
            Array.Clear(totals.SentHistory, 0, n);
            Array.Clear(totals.RecvHistory, 0, n);
            totals.Sent = 0;
            totals.Received = 0;
            totals.SendingApps = 0;
            totals.ReceivingApps = 0;
            // The newest second shown is four seconds old. Measured on a real PC, Windows hands events over
            // up to 2.6 s after they happen, and the window reads them once a second, so anything newer
            // could still be incomplete and would make the charts flicker.
            long first = nowSecond - 3 - n;

            lock (Sync)
            {
                foreach (AppEntry app in _apps)
                {
                    long sent = app.Total[0], received = app.Total[1];
                    if (includeLan)
                    {
                        sent += app.Total[2];
                        received += app.Total[3];
                    }
                    if (sent == 0 && received == 0) continue;

                    AppSnapshot s;
                    if (!cache.TryGetValue(app.Id, out s))
                    {
                        s = new AppSnapshot();
                        s.Id = app.Id;
                        cache[app.Id] = s;
                    }
                    s.Name = app.Name;
                    s.Sub = app.Sub;
                    s.Path = app.Path;
                    s.FileName = app.FileName;
                    s.WindowsPart = app.IsSvcHost || app.AlwaysRunning;     // a Windows service or Windows itself
                    s.Icon = s.WindowsPart ? null : app.Icon;
                    s.IsSystem = app.IsSystem;
                    s.IsSelf = app.IsSelf;
                    s.IsWindows = app.IsWindows;
                    s.Warning = app.Warning;
                    s.Running = app.AlwaysRunning || app.LivePids > 0;
                    s.CanBlock = app.Path != null && !app.IsSystem && !app.IsSelf;
                    s.Sent = sent;
                    s.Received = received;
                    s.SentHosts = app.Hosts[0] + (includeLan ? app.Hosts[2] : 0);
                    s.RecvHosts = app.Hosts[1] + (includeLan ? app.Hosts[3] : 0);
                    s.LastSent = includeLan ? Math.Max(app.Last[0], app.Last[2]) : app.Last[0];
                    s.LastRecv = includeLan ? Math.Max(app.Last[1], app.Last[3]) : app.Last[1];

                    float sentPeak = 0, recvPeak = 0;
                    for (int i = 0; i < n; i++)
                    {
                        long second = first + i;
                        float up = app.Series[0].Get(second), down = app.Series[1].Get(second);
                        if (includeLan)
                        {
                            up += app.Series[2].Get(second);
                            down += app.Series[3].Get(second);
                        }
                        s.SentHistory[i] = up;
                        s.RecvHistory[i] = down;
                        if (up > sentPeak) sentPeak = up;
                        if (down > recvPeak) recvPeak = down;
                        totals.SentHistory[i] += up;
                        totals.RecvHistory[i] += down;
                    }
                    s.SentPeak = sentPeak;
                    s.RecvPeak = recvPeak;
                    s.SentRate = (s.SentHistory[n - 1] + s.SentHistory[n - 2] + s.SentHistory[n - 3]) / 3.0;
                    s.RecvRate = (s.RecvHistory[n - 1] + s.RecvHistory[n - 2] + s.RecvHistory[n - 3]) / 3.0;

                    totals.Sent += sent;
                    totals.Received += received;
                    if (sent > 0) totals.SendingApps++;
                    if (received > 0) totals.ReceivingApps++;
                    output.Add(s);
                }
            }

            float totalSentPeak = 0, totalRecvPeak = 0;
            for (int i = 0; i < n; i++)
            {
                if (totals.SentHistory[i] > totalSentPeak) totalSentPeak = totals.SentHistory[i];
                if (totals.RecvHistory[i] > totalRecvPeak) totalRecvPeak = totals.RecvHistory[i];
            }
            totals.SentPeak = totalSentPeak;
            totals.RecvPeak = totalRecvPeak;
            totals.SentRate = (totals.SentHistory[n - 1] + totals.SentHistory[n - 2] + totals.SentHistory[n - 3]) / 3.0;
            totals.RecvRate = (totals.RecvHistory[n - 1] + totals.RecvHistory[n - 2] + totals.RecvHistory[n - 3]) / 3.0;
        }

        /// <summary>Fills "output" with the servers one program talked to.</summary>
        public void SnapshotRemotes(int appId, bool includeLan, List<RemoteSnapshot> output)
        {
            output.Clear();
            lock (Sync)
            {
                AppEntry app;
                if (!_appsById.TryGetValue(appId, out app)) return;
                foreach (RemoteEntry entry in app.Remotes.Values)
                {
                    if (entry.IsLan && !includeLan) continue;
                    if (entry.Host == null) _hostByIp.TryGetValue(entry.Ip, out entry.Host);
                    if (entry.AddressText == null) entry.AddressText = entry.Ip.ToString();
                    output.Add(ToSnapshot(entry));
                }
                if (app.OverflowInternet != null) output.Add(ToSnapshot(app.OverflowInternet));
                if (app.OverflowLan != null && includeLan) output.Add(ToSnapshot(app.OverflowLan));
            }
        }

        static RemoteSnapshot ToSnapshot(RemoteEntry entry)
        {
            RemoteSnapshot r = new RemoteSnapshot();
            r.Ip = entry.Ip;
            r.IsLan = entry.IsLan;
            r.IsOverflow = entry.IsOverflow;
            r.Host = entry.IsOverflow ? "(many other addresses)" : entry.Host;
            r.Address = entry.IsOverflow ? "" : entry.AddressText;
            r.PortText = entry.IsOverflow ? "" : entry.GetPortText();
            r.Sent = entry.Sent;
            r.Received = entry.Received;
            r.LastSent = entry.LastSent;
            r.LastRecv = entry.LastRecv;
            return r;
        }

        /// <summary>Plain-text report of everything counted so far (diagnostics).</summary>
        public string DumpText()
        {
            StringBuilder sb = new StringBuilder();
            lock (Sync)
            {
                sb.AppendLine("local addresses: " + string.Join(", ", new List<IpKey>(_local).ConvertAll(delegate(IpKey k) { return k.ToString(); }).ToArray()));
                sb.AppendLine("network events: " + NetworkEvents.ToString(CultureInfo.InvariantCulture) +
                              "   known processes: " + _procs.Count.ToString(CultureInfo.InvariantCulture) +
                              "   known host names: " + _hostByIp.Count.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "events by kind: TCP4 sent {0} received {1} | UDP4 sent {2} received {3} | TCP6 sent {4} received {5} | UDP6 sent {6} received {7}",
                    _kindCounts[0], _kindCounts[1], _kindCounts[2], _kindCounts[3], _kindCounts[4], _kindCounts[5], _kindCounts[6], _kindCounts[7]));
                sb.AppendLine("process starts seen: " + _processStarts.ToString(CultureInfo.InvariantCulture) +
                              "   stops: " + _processStops.ToString(CultureInfo.InvariantCulture) +
                              "   name answers seen: " + _dnsAnswers.ToString(CultureInfo.InvariantCulture));
                foreach (string start in _recentStarts) sb.AppendLine("   started: " + start);
                sb.AppendLine("slowest event delivery: " + _maxLagMs.ToString(CultureInfo.InvariantCulture) + " ms");
                _maxLagMs = 0;
                long[] sum = new long[4];
                foreach (AppEntry app in _apps)
                    for (int i = 0; i < 4; i++) sum[i] += app.Total[i];
                sb.AppendLine("all programs together: internet sent " + Theme.Bytes(sum[0]) + ", received " + Theme.Bytes(sum[1]) +
                              " | home network sent " + Theme.Bytes(sum[2]) + ", received " + Theme.Bytes(sum[3]));
                sb.AppendLine();
                sb.AppendLine("PROGRAMS (internet sent / received, local network sent / received, servers, live processes)");
                List<AppEntry> sorted = new List<AppEntry>(_apps);
                sorted.Sort(delegate(AppEntry a, AppEntry b) { return (b.Total[0] + b.Total[2]).CompareTo(a.Total[0] + a.Total[2]); });
                foreach (AppEntry app in sorted)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0,-44} net {1,10} / {2,10}   lan {3,9} / {4,9}   servers {5,3}   pids {6}   {7}{8}",
                        Trim(app.Name, 44), Theme.Bytes(app.Total[0]), Theme.Bytes(app.Total[1]), Theme.Bytes(app.Total[2]), Theme.Bytes(app.Total[3]),
                        app.Remotes.Count, app.LivePids, app.Path ?? app.Key, app.IsSystem ? "  [system]" : ""));
                    List<RemoteEntry> remotes = new List<RemoteEntry>(app.Remotes.Values);
                    remotes.Sort(delegate(RemoteEntry a, RemoteEntry b) { return (b.Sent + b.Received).CompareTo(a.Sent + a.Received); });
                    for (int i = 0; i < remotes.Count && i < 6; i++)
                    {
                        RemoteEntry r = remotes[i];
                        string host = r.Host;
                        if (host == null) _hostByIp.TryGetValue(r.Ip, out host);
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "        {0,-40} {1,-46} sent {2,10}  recv {3,10}  [{4}]{5}",
                            Trim(host ?? "-", 40), r.Ip, Theme.Bytes(r.Sent), Theme.Bytes(r.Received), r.GetPortText(), r.IsLan ? "  (local network)" : ""));
                    }
                }
                if (_rawLog != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("FIRST RAW EVENTS");
                    foreach (string line in _rawLog) sb.AppendLine("  " + line);
                }
            }
            return sb.ToString();
        }

        static string Trim(string text, int max)
        {
            if (text == null) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
