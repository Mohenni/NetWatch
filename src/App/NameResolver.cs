// NetWatch - finding a readable name for a server address.
//
// Most names come for free: Windows tells us which name each program looked up (see EtwSession).
// Two extra sources fill the gaps:
//   1. the names Windows still remembers from before NetWatch was started (its DNS cache), and
//   2. a "reverse lookup" - asking the DNS server which name belongs to an address.
// The reverse lookup is the only thing here that produces network traffic of its own, and it can be
// switched off in the Options menu.

using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NetWatch
{
    internal sealed class NameResolver
    {
        readonly TrafficModel _model;
        readonly object _lock = new object();
        readonly Queue<IpKey> _queue = new Queue<IpKey>();
        readonly HashSet<IpKey> _asked = new HashSet<IpKey>();
        int _workers;

        public volatile bool Enabled = true;

        public NameResolver(TrafficModel model)
        {
            _model = model;
        }

        /// <summary>Queues a reverse lookup for an internet address (each address is only ever asked once).</summary>
        public void Request(IpKey ip)
        {
            if (!Enabled || ip.Kind != AddrKind.Internet) return;
            lock (_lock)
            {
                if (!_asked.Add(ip)) return;
                _queue.Enqueue(ip);
                if (_workers >= 2) return;
                _workers++;
            }
            ThreadPool.QueueUserWorkItem(Work);
        }

        void Work(object unused)
        {
            while (true)
            {
                IpKey ip;
                lock (_lock)
                {
                    if (_queue.Count == 0 || !Enabled)
                    {
                        _workers--;
                        return;
                    }
                    ip = _queue.Dequeue();
                }
                try
                {
                    string name = ReverseLookup(ip);
                    if (name != null) _model.SetHostIfMissing(ip, name);
                }
                catch
                {
                }
            }
        }

        static string ReverseLookup(IpKey ip)
        {
            IntPtr results;
            int status = NativeMethods.DnsQuery_W(ReverseName(ip), 12 /* PTR */, 0, IntPtr.Zero, out results, IntPtr.Zero);
            if (status != 0 || results == IntPtr.Zero) return null;
            try
            {
                // DNS_RECORDW (x64): pNext @0, wType @16, Data @32 (for PTR: pointer to the host name)
                for (IntPtr record = results; record != IntPtr.Zero; record = Marshal.ReadIntPtr(record, 0))
                {
                    if ((ushort)Marshal.ReadInt16(record, 16) != 12) continue;
                    string host = Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, 32));
                    if (!string.IsNullOrEmpty(host)) return host.TrimEnd('.').ToLowerInvariant();
                }
            }
            finally
            {
                NativeMethods.DnsRecordListFree(results, 1);
            }
            return null;
        }

        // 1.2.3.4 -> "4.3.2.1.in-addr.arpa"; IPv6 -> 32 reversed hex digits + ".ip6.arpa"
        internal static string ReverseName(IpKey ip)
        {
            byte[] b = ip.ToBytes();
            StringBuilder sb = new StringBuilder();
            if (b.Length == 4)
            {
                for (int i = 3; i >= 0; i--) sb.Append(b[i]).Append('.');
                sb.Append("in-addr.arpa");
            }
            else
            {
                const string hex = "0123456789abcdef";
                for (int i = 15; i >= 0; i--)
                {
                    sb.Append(hex[b[i] & 0x0F]).Append('.');
                    sb.Append(hex[b[i] >> 4]).Append('.');
                }
                sb.Append("ip6.arpa");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Copies the names Windows already has in its DNS cache, so connections that were open
        /// before NetWatch started get names too. Returns how many addresses were learned.
        /// </summary>
        public static int SeedFromWindowsDnsCache(TrafficModel model)
        {
            int count = 0;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\StandardCimv2",
                           "SELECT Entry, Data, Type FROM MSFT_DNSClientCache"))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementBaseObject item in items)
                    {
                        using (item)
                        {
                            int type = Convert.ToInt32(item["Type"]);
                            if (type != 1 && type != 28) continue;      // A and AAAA records only
                            string entry = item["Entry"] as string;
                            string data = item["Data"] as string;
                            IpKey ip;
                            if (string.IsNullOrEmpty(entry) || !IpKey.TryParse(data, out ip)) continue;
                            model.SetHostIfMissing(ip, entry.TrimEnd('.').ToLowerInvariant());
                            count++;
                        }
                    }
                }
            }
            catch
            {
            }
            return count;
        }
    }
}
