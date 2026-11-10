// NetWatch - live capture of per-process network traffic through Event Tracing for Windows (ETW).
//
// Windows itself reports every TCP/UDP send and receive together with the process that did it
// (the same source Resource Monitor uses). This file opens a real-time tracing session, enables
// the three built-in providers we need and decodes their events:
//
//   Microsoft-Windows-Kernel-Network  -> bytes sent / received, per process, per remote address
//   Microsoft-Windows-Kernel-Process  -> process start / stop (so short-lived programs are named)
//   Microsoft-Windows-DNS-Client      -> which host name a remote address belongs to
//
// Nothing is injected into other programs and no packet contents are read.
//
// Good to know: Windows hands the events over in batches, up to about 2.6 seconds after they
// happened (measured). Totals are therefore always a moment behind the network adapter's own
// counter; compared over minutes they agree to within the size of the packet headers.

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetWatch
{
    /// <summary>Receives decoded events. Called on the ETW thread, one event at a time.</summary>
    internal interface IEtwSink
    {
        /// <summary>
        /// One send or receive. "s" is the event's source endpoint and "d" its destination endpoint,
        /// exactly as Windows reports them. (Careful: for TCP, Windows always puts this PC in "s",
        /// even when receiving; for UDP it puts the real sender there. The receiver of these events
        /// must work out which side is remote by itself.) Addresses are raw network-order bytes: IPv4
        /// uses the low 4 bytes of the Lo value, IPv6 uses Hi (first 8 bytes) and Lo (last 8 bytes).
        /// timeStamp is the moment of the event in UTC FILETIME units (100 ns).
        /// </summary>
        void OnNetwork(int pid, int size, bool isSend, bool isUdp, bool isV6, long timeStamp,
                       ulong sHi, ulong sLo, ushort sPort,
                       ulong dHi, ulong dLo, ushort dPort);

        void OnProcessStart(int pid, string ntImagePath);
        void OnProcessStop(int pid);
        void OnDns(string queryName, string queryResults);
    }

    internal sealed class EtwSession : IDisposable
    {
        public const string SessionName = "NetWatchTrafficSession";

        static readonly Guid SessionGuid = new Guid("5b0f4c2e-7a51-4d0e-9a67-3f1c2b8e6d41");
        static readonly Guid KernelNetwork = new Guid("7dd42a49-5329-4832-8dfd-43d979153a88");
        static readonly Guid KernelProcess = new Guid("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
        static readonly Guid DnsClient = new Guid("1c95126e-7eea-49a9-a3fe-a378b03ddb4d");

        const int ERROR_SUCCESS = 0;
        const int ERROR_ALREADY_EXISTS = 183;

        const int PropsHeaderSize = 120;          // sizeof(EVENT_TRACE_PROPERTIES) on x64
        const int PropsTotalSize = PropsHeaderSize + 2048;

        const uint EVENT_TRACE_CONTROL_STOP = 1;
        const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        const byte TRACE_LEVEL_INFORMATION = 4;

        readonly IEtwSink _sink;
        readonly long _netG0, _netG1, _procG0, _procG1, _dnsG0, _dnsG1;

        ulong _session;
        ulong _trace;
        bool _sessionOpen;
        bool _traceOpen;
        Thread _thread;
        NativeMethods.EventRecordCallback _callback;   // must stay referenced while the trace is open
        IntPtr _loggerName = IntPtr.Zero;
        IntPtr _tdhBuffer = IntPtr.Zero;
        const int TdhBufferSize = 64 * 1024;
        IntPtr _nameImageName, _nameQueryName, _nameQueryResults;

        long _eventCount;
        long _lostEvents;

        /// <summary>Win32 error of the last Start() step that failed (0 when everything is fine).</summary>
        public int NetworkProviderError { get; private set; }
        public int ProcessProviderError { get; private set; }
        public int DnsProviderError { get; private set; }
        public bool IsRunning { get { return _traceOpen; } }
        public long EventCount { get { return Interlocked.Read(ref _eventCount); } }

        public EtwSession(IEtwSink sink)
        {
            _sink = sink;
            Split(KernelNetwork, out _netG0, out _netG1);
            Split(KernelProcess, out _procG0, out _procG1);
            Split(DnsClient, out _dnsG0, out _dnsG1);
        }

        static void Split(Guid g, out long a, out long b)
        {
            byte[] raw = g.ToByteArray();
            a = BitConverter.ToInt64(raw, 0);
            b = BitConverter.ToInt64(raw, 8);
        }

        /// <summary>
        /// Starts the session. Returns 0 on success, otherwise the Win32 error code
        /// (5 = access denied: the program needs administrator rights).
        /// </summary>
        public int Start()
        {
            if (IntPtr.Size != 8)
                return 50; // ERROR_NOT_SUPPORTED: built for 64-bit Windows only
            if (_traceOpen)
                return ERROR_SUCCESS;

            IntPtr props = BuildProperties();
            try
            {
                int err = NativeMethods.StartTraceW(out _session, SessionName, props);
                if (err == ERROR_ALREADY_EXISTS)
                {
                    // A previous run was killed before it could clean up. Stop the leftover and retry.
                    StopByName();
                    Marshal.FreeHGlobal(props);
                    props = BuildProperties();
                    err = NativeMethods.StartTraceW(out _session, SessionName, props);
                }
                if (err != ERROR_SUCCESS)
                    return err;
                _sessionOpen = true;
            }
            finally
            {
                Marshal.FreeHGlobal(props);
            }

            Guid g = KernelNetwork;
            NetworkProviderError = NativeMethods.EnableTraceEx2(_session, ref g, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                TRACE_LEVEL_INFORMATION, 0x30 /* IPv4 | IPv6 */, 0, 0, IntPtr.Zero);
            if (NetworkProviderError != ERROR_SUCCESS)
            {
                int e = NetworkProviderError;
                Stop();
                return e;
            }

            // The next two are helpful but optional: the program still works when they are refused.
            g = KernelProcess;
            ProcessProviderError = NativeMethods.EnableTraceEx2(_session, ref g, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                TRACE_LEVEL_INFORMATION, 0x10 /* process start/stop only */, 0, 0, IntPtr.Zero);
            g = DnsClient;
            DnsProviderError = NativeMethods.EnableTraceEx2(_session, ref g, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);

            _tdhBuffer = Marshal.AllocHGlobal(TdhBufferSize);
            _nameImageName = Marshal.StringToHGlobalUni("ImageName");
            _nameQueryName = Marshal.StringToHGlobalUni("QueryName");
            _nameQueryResults = Marshal.StringToHGlobalUni("QueryResults");

            _callback = new NativeMethods.EventRecordCallback(OnEvent);
            _loggerName = Marshal.StringToHGlobalUni(SessionName);

            NativeMethods.EVENT_TRACE_LOGFILE logfile = new NativeMethods.EVENT_TRACE_LOGFILE();
            logfile.LoggerName = _loggerName;
            logfile.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD;
            logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback);

            _trace = NativeMethods.OpenTraceW(ref logfile);
            if (_trace == ulong.MaxValue || _trace == 0x00000000FFFFFFFFUL)
            {
                int e = Marshal.GetLastWin32Error();
                Stop();
                return e == 0 ? 31 : e;
            }
            _traceOpen = true;

            _thread = new Thread(ProcessLoop);
            _thread.IsBackground = true;
            _thread.Name = "NetWatch ETW";
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
            return ERROR_SUCCESS;
        }

        void ProcessLoop()
        {
            try
            {
                ulong[] handles = new ulong[] { _trace };
                NativeMethods.ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);   // blocks until the session stops
            }
            catch
            {
            }
        }

        /// <summary>Stops the session and releases everything. Safe to call more than once.</summary>
        public void Stop()
        {
            if (_sessionOpen)
            {
                _sessionOpen = false;
                IntPtr props = BuildProperties();
                try { NativeMethods.ControlTraceW(_session, null, props, EVENT_TRACE_CONTROL_STOP); }
                catch { }
                finally { Marshal.FreeHGlobal(props); }
            }
            if (_traceOpen)
            {
                _traceOpen = false;
                try { NativeMethods.CloseTrace(_trace); } catch { }
            }
            Thread t = _thread;
            _thread = null;
            if (t != null && t != Thread.CurrentThread)
            {
                try { t.Join(3000); } catch { }
            }
            // The native buffers are only touched by the ETW thread, which has ended by now.
            FreePtr(ref _loggerName);
            FreePtr(ref _tdhBuffer);
            FreePtr(ref _nameImageName);
            FreePtr(ref _nameQueryName);
            FreePtr(ref _nameQueryResults);
        }

        static void FreePtr(ref IntPtr p)
        {
            if (p != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(p);
                p = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Stops a leftover session with our name (for example after a crash). Harmless if none exists.</summary>
        public static int StopByName()
        {
            IntPtr props = BuildProperties();
            try { return NativeMethods.ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP); }
            finally { Marshal.FreeHGlobal(props); }
        }

        /// <summary>Events Windows had to drop because we could not keep up (0 in normal use).</summary>
        public long QueryLostEvents()
        {
            if (!_sessionOpen) return Interlocked.Read(ref _lostEvents);
            IntPtr props = BuildProperties();
            try
            {
                if (NativeMethods.ControlTraceW(_session, null, props, 0 /* QUERY */) == ERROR_SUCCESS)
                {
                    long lost = (uint)Marshal.ReadInt32(props, 88) + (long)(uint)Marshal.ReadInt32(props, 100);
                    Interlocked.Exchange(ref _lostEvents, lost);
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(props); }
            return Interlocked.Read(ref _lostEvents);
        }

        // Builds an EVENT_TRACE_PROPERTIES block (x64 layout) followed by room for the session name.
        static IntPtr BuildProperties()
        {
            IntPtr p = Marshal.AllocHGlobal(PropsTotalSize);
            NativeMethods.RtlZeroMemory(p, (IntPtr)PropsTotalSize);
            Marshal.WriteInt32(p, 0, PropsTotalSize);                 // Wnode.BufferSize
            Marshal.Copy(SessionGuid.ToByteArray(), 0, p + 24, 16);   // Wnode.Guid
            Marshal.WriteInt32(p, 40, 1);                             // Wnode.ClientContext = QPC clock
            Marshal.WriteInt32(p, 44, unchecked((int)WNODE_FLAG_TRACED_GUID));
            Marshal.WriteInt32(p, 48, 64);                            // BufferSize in KB
            Marshal.WriteInt32(p, 52, 32);                            // MinimumBuffers
            Marshal.WriteInt32(p, 56, 256);                           // MaximumBuffers
            Marshal.WriteInt32(p, 64, unchecked((int)EVENT_TRACE_REAL_TIME_MODE));
            Marshal.WriteInt32(p, 68, 1);                             // FlushTimer: deliver at least once a second
            Marshal.WriteInt32(p, 112, 0);                            // LogFileNameOffset (no file)
            Marshal.WriteInt32(p, 116, PropsHeaderSize);              // LoggerNameOffset
            return p;
        }

        static ushort Swap(ushort v)
        {
            return (ushort)((v >> 8) | (v << 8));
        }

        // EVENT_RECORD layout (x64): EventHeader.ProcessId @12, TimeStamp @16, ProviderId @24,
        // Descriptor.Id @40, UserDataLength @86, UserData @96.
        unsafe void OnEvent(IntPtr record)
        {
            try
            {
                byte* p = (byte*)record;
                long g0 = *(long*)(p + 24);
                long g1 = *(long*)(p + 32);
                ushort id = *(ushort*)(p + 40);
                long timeStamp = *(long*)(p + 16);      // UTC, 100 ns units (FILETIME)
                int len = *(ushort*)(p + 86);
                byte* d = *(byte**)(p + 96);
                if (d == null) return;
                _eventCount++;

                if (g0 == _netG0 && g1 == _netG1)
                {
                    bool send, udp, v6;
                    switch (id)
                    {
                        case 10: send = true; udp = false; v6 = false; break;   // TCPv4 data sent
                        case 11: send = false; udp = false; v6 = false; break;  // TCPv4 data received
                        case 26: send = true; udp = false; v6 = true; break;    // TCPv6 data sent
                        case 27: send = false; udp = false; v6 = true; break;   // TCPv6 data received
                        case 42: send = true; udp = true; v6 = false; break;    // UDPv4 data sent
                        case 43: send = false; udp = true; v6 = false; break;   // UDPv4 data received
                        case 58: send = true; udp = true; v6 = true; break;     // UDPv6 data sent
                        case 59: send = false; udp = true; v6 = true; break;    // UDPv6 data received
                        default: return;
                    }
                    // Payload: PID, size, daddr, saddr, dport, sport (ports are in network byte order).
                    if (!v6)
                    {
                        if (len < 20) return;
                        int pid = *(int*)d;
                        int size = *(int*)(d + 4);
                        uint daddr = *(uint*)(d + 8);
                        uint saddr = *(uint*)(d + 12);
                        ushort dport = Swap(*(ushort*)(d + 16));
                        ushort sport = Swap(*(ushort*)(d + 18));
                        _sink.OnNetwork(pid, size, send, udp, false, timeStamp, 0, saddr, sport, 0, daddr, dport);
                    }
                    else
                    {
                        if (len < 44) return;
                        int pid = *(int*)d;
                        int size = *(int*)(d + 4);
                        ulong dHi = *(ulong*)(d + 8);
                        ulong dLo = *(ulong*)(d + 16);
                        ulong sHi = *(ulong*)(d + 24);
                        ulong sLo = *(ulong*)(d + 32);
                        ushort dport = Swap(*(ushort*)(d + 40));
                        ushort sport = Swap(*(ushort*)(d + 42));
                        _sink.OnNetwork(pid, size, send, udp, true, timeStamp, sHi, sLo, sport, dHi, dLo, dport);
                    }
                }
                else if (g0 == _procG0 && g1 == _procG1)
                {
                    if (len < 4) return;
                    if (id == 1)        // ProcessStart: first field is the new process id
                    {
                        int pid = *(int*)d;
                        string image = GetStringProperty(record, _nameImageName);
                        _sink.OnProcessStart(pid, image);
                    }
                    else if (id == 2)   // ProcessStop
                    {
                        _sink.OnProcessStop(*(int*)d);
                    }
                }
                else if (g0 == _dnsG0 && g1 == _dnsG1)
                {
                    if (id == 3008 || id == 3020)   // query completed / response received
                    {
                        string name = GetStringProperty(record, _nameQueryName);
                        if (string.IsNullOrEmpty(name)) return;
                        string results = GetStringProperty(record, _nameQueryResults);
                        if (string.IsNullOrEmpty(results)) return;
                        _sink.OnDns(name, results);
                    }
                }
            }
            catch
            {
                // Never let an exception travel back into Windows' tracing code.
            }
        }

        // Reads one named text field of an event through the Trace Data Helper, which knows the
        // field layout of every Windows version.
        unsafe string GetStringProperty(IntPtr record, IntPtr propertyName)
        {
            if (_tdhBuffer == IntPtr.Zero || propertyName == IntPtr.Zero) return null;
            NativeMethods.PROPERTY_DATA_DESCRIPTOR desc = new NativeMethods.PROPERTY_DATA_DESCRIPTOR();
            desc.PropertyName = (ulong)propertyName.ToInt64();
            desc.ArrayIndex = 0xFFFFFFFF;
            uint size;
            if (NativeMethods.TdhGetPropertySize(record, 0, IntPtr.Zero, 1, ref desc, out size) != ERROR_SUCCESS)
                return null;
            if (size < 2 || size > TdhBufferSize - 2)
                return null;
            if (NativeMethods.TdhGetProperty(record, 0, IntPtr.Zero, 1, ref desc, size, _tdhBuffer) != ERROR_SUCCESS)
                return null;
            char* chars = (char*)_tdhBuffer;
            int n = (int)(size / 2);
            int length = 0;
            while (length < n && chars[length] != '\0') length++;
            return new string(chars, 0, length);
        }
    }

    internal static partial class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void EventRecordCallback(IntPtr eventRecord);

        // EVENT_TRACE_LOGFILEW, x64 layout. Only the fields we set are declared.
        [StructLayout(LayoutKind.Explicit, Size = 448)]
        internal struct EVENT_TRACE_LOGFILE
        {
            [FieldOffset(0)] public IntPtr LogFileName;
            [FieldOffset(8)] public IntPtr LoggerName;
            [FieldOffset(28)] public uint ProcessTraceMode;
            [FieldOffset(400)] public IntPtr BufferCallback;
            [FieldOffset(424)] public IntPtr EventRecordCallback;
            [FieldOffset(440)] public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROPERTY_DATA_DESCRIPTOR
        {
            public ulong PropertyName;
            public uint ArrayIndex;
            public uint Reserved;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern int EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level,
            ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILE logfile);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern int ProcessTrace([In] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        internal static extern int CloseTrace(ulong traceHandle);

        [DllImport("tdh.dll", ExactSpelling = true)]
        internal static extern int TdhGetPropertySize(IntPtr eventRecord, uint tdhContextCount, IntPtr tdhContext,
            uint propertyDataCount, ref PROPERTY_DATA_DESCRIPTOR propertyData, out uint propertySize);

        [DllImport("tdh.dll", ExactSpelling = true)]
        internal static extern int TdhGetProperty(IntPtr eventRecord, uint tdhContextCount, IntPtr tdhContext,
            uint propertyDataCount, ref PROPERTY_DATA_DESCRIPTOR propertyData, uint bufferSize, IntPtr buffer);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern void RtlZeroMemory(IntPtr destination, IntPtr length);
    }
}
