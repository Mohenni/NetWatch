// NetWatch - finding out which program a process id belongs to.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace NetWatch
{
    internal struct ServiceInfo
    {
        public string Name;
        public string DisplayName;
    }

    internal static class ProcessUtil
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const int SystemProcessIdInformation = 88;
        const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Full path of the program file behind a process id, or null when it cannot be found.</summary>
        public static string GetImagePath(int pid)
        {
            if (pid <= 4) return null;
            string path = QueryWin32Path(pid);
            if (path != null) return path;
            string nt = QueryNtPath(pid);
            return nt == null ? null : NtPathToDos(nt);
        }

        static string QueryWin32Path(int pid)
        {
            IntPtr handle = NativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (NativeMethods.QueryFullProcessImageNameW(handle, 0, sb, ref size) && size > 0)
                    return sb.ToString(0, size);
                return null;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        // Works for processes we are not allowed to open (system services): asks the kernel directly.
        static string QueryNtPath(int pid)
        {
            int capacity = 2048;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(capacity);
                try
                {
                    NativeMethods.SYSTEM_PROCESS_ID_INFORMATION info = new NativeMethods.SYSTEM_PROCESS_ID_INFORMATION();
                    info.ProcessId = (IntPtr)pid;
                    info.Length = 0;
                    info.MaximumLength = (ushort)capacity;
                    info.Buffer = buffer;
                    int returned;
                    int status = NativeMethods.NtQuerySystemInformation(SystemProcessIdInformation, ref info,
                        Marshal.SizeOf(typeof(NativeMethods.SYSTEM_PROCESS_ID_INFORMATION)), out returned);
                    if (status == 0)
                        return info.Length == 0 ? null : Marshal.PtrToStringUni(buffer, info.Length / 2);
                    if (status == STATUS_INFO_LENGTH_MISMATCH && info.MaximumLength > capacity)
                    {
                        capacity = info.MaximumLength;
                        continue;
                    }
                    return null;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }

        // ---- "\Device\HarddiskVolume3\Windows\..."  ->  "C:\Windows\..."

        static readonly object DeviceLock = new object();
        static List<KeyValuePair<string, string>> _devices;

        public static string NtPathToDos(string ntPath)
        {
            if (string.IsNullOrEmpty(ntPath)) return null;
            if (ntPath.Length > 2 && ntPath[1] == ':') return ntPath;       // already a normal path
            for (int attempt = 0; attempt < 2; attempt++)
            {
                List<KeyValuePair<string, string>> devices = GetDevices(attempt == 1);
                foreach (KeyValuePair<string, string> device in devices)
                {
                    if (ntPath.Length > device.Key.Length && ntPath[device.Key.Length] == '\\' &&
                        ntPath.StartsWith(device.Key, StringComparison.OrdinalIgnoreCase))
                        return device.Value + ntPath.Substring(device.Key.Length);
                }
            }
            const string mup = "\\Device\\Mup\\";
            if (ntPath.StartsWith(mup, StringComparison.OrdinalIgnoreCase))
                return "\\\\" + ntPath.Substring(mup.Length);               // network share
            return ntPath;
        }

        static List<KeyValuePair<string, string>> GetDevices(bool refresh)
        {
            lock (DeviceLock)
            {
                if (_devices != null && !refresh) return _devices;
                List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
                try
                {
                    foreach (string drive in Environment.GetLogicalDrives())
                    {
                        if (drive.Length < 2) continue;
                        string letter = drive.Substring(0, 2);
                        StringBuilder sb = new StringBuilder(600);
                        if (NativeMethods.QueryDosDeviceW(letter, sb, sb.Capacity) != 0 && sb.Length > 0)
                            list.Add(new KeyValuePair<string, string>(sb.ToString(), letter));
                    }
                }
                catch
                {
                }
                _devices = list;
                return list;
            }
        }

        // ---- which Windows services live inside a "svchost.exe" process

        static readonly object ServiceLock = new object();
        static Dictionary<int, List<ServiceInfo>> _services;
        static long _servicesStamp;
        static readonly List<ServiceInfo> NoServices = new List<ServiceInfo>();

        public static List<ServiceInfo> ServicesForPid(int pid)
        {
            lock (ServiceLock)
            {
                List<ServiceInfo> list;
                if (_services != null && _services.TryGetValue(pid, out list)) return list;
                long now = Stopwatch.GetTimestamp();
                if (_services == null || now - _servicesStamp > Stopwatch.Frequency * 2)
                {
                    _services = QueryServices();
                    _servicesStamp = now;
                    if (_services.TryGetValue(pid, out list)) return list;
                }
                return NoServices;
            }
        }

        /// <summary>Forget the cached list (a service process was started, so ids may have changed).</summary>
        public static void InvalidateServices()
        {
            lock (ServiceLock)
            {
                _services = null;
            }
        }

        static Dictionary<int, List<ServiceInfo>> QueryServices()
        {
            Dictionary<int, List<ServiceInfo>> map = new Dictionary<int, List<ServiceInfo>>();
            IntPtr scm = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                scm = NativeMethods.OpenSCManagerW(null, null, 0x0004 /* SC_MANAGER_ENUMERATE_SERVICE */);
                if (scm == IntPtr.Zero) return map;
                const int size = 256 * 1024;
                const int itemSize = 56;        // sizeof(ENUM_SERVICE_STATUS_PROCESSW) on x64
                buffer = Marshal.AllocHGlobal(size);
                uint resume = 0;
                for (int guard = 0; guard < 64; guard++)
                {
                    uint needed, returned;
                    bool ok = NativeMethods.EnumServicesStatusExW(scm, 0, 0x30 /* SERVICE_WIN32 */, 1 /* SERVICE_ACTIVE */,
                        buffer, size, out needed, out returned, ref resume, null);
                    int error = Marshal.GetLastWin32Error();
                    if (!ok && error != 234 /* ERROR_MORE_DATA */) break;
                    for (int i = 0; i < returned; i++)
                    {
                        IntPtr item = buffer + i * itemSize;
                        ServiceInfo info;
                        info.Name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
                        info.DisplayName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 8)) ?? info.Name;
                        int pid = Marshal.ReadInt32(item, 44);      // ServiceStatusProcess.dwProcessId
                        if (pid == 0) continue;
                        List<ServiceInfo> list;
                        if (!map.TryGetValue(pid, out list))
                        {
                            list = new List<ServiceInfo>();
                            map[pid] = list;
                        }
                        list.Add(info);
                    }
                    if (ok) break;
                }
            }
            catch
            {
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (scm != IntPtr.Zero) NativeMethods.CloseServiceHandle(scm);
            }
            foreach (List<ServiceInfo> list in map.Values)
                list.Sort(delegate(ServiceInfo a, ServiceInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return map;
        }
    }

    /// <summary>Program icons, loaded once per file.</summary>
    internal static class IconCache
    {
        static readonly Dictionary<string, Image> Icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Loads the icon now (call from a background thread). Returns null when the file has none.</summary>
        public static Image Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (Icons)
            {
                Image cached;
                if (Icons.TryGetValue(path, out cached)) return cached;
            }
            Image result = null;
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(path))
                {
                    if (icon != null) result = icon.ToBitmap();
                }
            }
            catch
            {
            }
            lock (Icons)
            {
                Icons[path] = result;
            }
            return result;
        }

        /// <summary>Returns the icon if it is already known; otherwise starts loading it and returns null.</summary>
        public static Image TryGet(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (Icons)
            {
                Image cached;
                if (Icons.TryGetValue(path, out cached)) return cached;
                if (!Pending.Add(path)) return null;
            }
            ThreadPool.QueueUserWorkItem(delegate { Load(path); });
            return null;
        }
    }

    internal static partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_PROCESS_ID_INFORMATION
        {
            public IntPtr ProcessId;
            public ushort Length;           // UNICODE_STRING ImageName
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, int max);

        [DllImport("ntdll.dll")]
        internal static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_PROCESS_ID_INFORMATION info, int size, out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumServicesStatusExW(IntPtr scManager, int infoLevel, uint serviceType, uint serviceState,
            IntPtr buffer, int bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string groupName);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int DnsQuery_W(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

        [DllImport("dnsapi.dll")]
        internal static extern void DnsRecordListFree(IntPtr list, int freeType);
    }
}
