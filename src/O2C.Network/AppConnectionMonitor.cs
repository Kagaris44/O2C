using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace O2C.Network;

/// <summary>Программа обратилась к адресу: путь exe, адрес и порт собеседника, протокол (6 — TCP, 17 — UDP).</summary>
public sealed record AppConnection(string AppPath, int ProcessId, IPAddress RemoteAddress, int RemotePort, int Protocol, DateTime Time);

/// <summary>
/// Наблюдение за соединениями программ через события ядра Windows (ETW, провайдер Microsoft-Windows-Kernel-Network):
/// TCP — событие на установку соединения, UDP — на отправку датаграммы. Лишние события отсекает сама Windows
/// (фильтр по номерам событий), повторы «программа + адрес» отсекаются здесь. Без драйвера и без перехвата трафика.
/// Нужны права администратора.
/// </summary>
public sealed unsafe partial class AppConnectionMonitor : IDisposable
{
    private const string SessionName = "O2C-AppConnections";
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    // Ключевые слова провайдера: IPv4 и IPv6.
    private const ulong KeywordIpv4 = 0x10;
    private const ulong KeywordIpv6 = 0x20;

    // Номера событий: TCP connect (IPv4/IPv6), UDP send (IPv4/IPv6).
    private const ushort TcpConnectV4 = 12;
    private const ushort TcpConnectV6 = 28;
    private const ushort UdpSendV4 = 42;
    private const ushort UdpSendV6 = 58;

    /// <summary>Повтор «процесс + адрес + протокол» не сообщается чаще, чем раз в это время.</summary>
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(60);

    private readonly Action<AppConnection> _onConnection;
    private readonly ConcurrentDictionary<(int Pid, IPAddress Address, int Protocol), long> _recent = new();
    private readonly ConcurrentDictionary<int, (string? Path, long Expires)> _paths = new();
    private ulong _session;
    private ulong _trace = InvalidTrace;
    private Thread? _thread;
    private GCHandle _self;
    private long _eventCount;

    private const ulong InvalidTrace = ulong.MaxValue;

    public AppConnectionMonitor(Action<AppConnection> onConnection) => _onConnection = onConnection;

    /// <summary>Сколько событий ядра обработано (для диагностики и замера нагрузки).</summary>
    public long EventCount => Interlocked.Read(ref _eventCount);

    public void Start()
    {
        StartSession();
        EnableProvider();
        OpenConsumer();
    }

    // ---------- Сеанс трассировки ----------

    private const int PropertiesSize = 120;
    private const int PropertiesTotal = PropertiesSize + 1024;

    private void StartSession()
    {
        var props = stackalloc byte[PropertiesTotal];
        FillProperties(props);
        int error = StartTraceW(out _session, SessionName, props);
        if (error == 183) // ERROR_ALREADY_EXISTS — остался от прошлого запуска
        {
            FillProperties(props);
            _ = ControlTraceW(0, SessionName, props, 1 /* STOP */);
            FillProperties(props);
            error = StartTraceW(out _session, SessionName, props);
        }
        if (error != 0)
            throw new IpHelperException(error, "StartTrace");
    }

    // EVENT_TRACE_PROPERTIES (evntrace.h, x64), 120 байт + имя сеанса.
    private static void FillProperties(byte* props)
    {
        var span = new Span<byte>(props, PropertiesTotal);
        span.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(span, PropertiesTotal);          // Wnode.BufferSize
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 1);                  // Wnode.ClientContext = QPC
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 0x20000);            // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], 64);                 // BufferSize, КБ
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], 4);                  // MinimumBuffers
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], 32);                 // MaximumBuffers
        BinaryPrimitives.WriteUInt32LittleEndian(span[64..], 0x100);              // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
        BinaryPrimitives.WriteUInt32LittleEndian(span[68..], 1);                  // FlushTimer, с
        BinaryPrimitives.WriteUInt32LittleEndian(span[116..], PropertiesSize);    // LoggerNameOffset
    }

    private void EnableProvider()
    {
        // EVENT_FILTER_EVENT_ID { BOOLEAN FilterIn; UCHAR Reserved; USHORT Count; USHORT Events[]; }
        ushort[] ids = [TcpConnectV4, TcpConnectV6, UdpSendV4, UdpSendV6];
        int filterSize = 4 + (2 * ids.Length);
        var filter = stackalloc byte[filterSize];
        filter[0] = 1;
        filter[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(filter + 2, 2), (ushort)ids.Length);
        for (int i = 0; i < ids.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(filter + 4 + (2 * i), 2), ids[i]);

        // EVENT_FILTER_DESCRIPTOR { ULONGLONG Ptr; ULONG Size; ULONG Type; }
        var descriptor = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(descriptor, 8), (ulong)filter);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(descriptor + 8, 4), (uint)filterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(descriptor + 12, 4), 0x80000200); // EVENT_FILTER_TYPE_EVENT_ID

        // ENABLE_TRACE_PARAMETERS { ULONG Version; ULONG EnableProperty; ULONG ControlFlags; GUID SourceId; PEVENT_FILTER_DESCRIPTOR EnableFilterDesc; ULONG FilterDescCount; }
        var parameters = stackalloc byte[48];
        new Span<byte>(parameters, 48).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(parameters, 4), 2); // ENABLE_TRACE_PARAMETERS_VERSION_2
        *(byte**)(parameters + 32) = descriptor;
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(parameters + 40, 4), 1);

        var provider = KernelNetwork;
        int error = EnableTraceEx2(_session, &provider, 1 /* ENABLE */, 5 /* VERBOSE */,
            KeywordIpv4 | KeywordIpv6, 0, 0, parameters);
        if (error != 0)
            throw new IpHelperException(error, "EnableTraceEx2");
    }

    // ---------- Чтение событий ----------

    // EVENT_TRACE_LOGFILEW (evntrace.h, x64), 448 байт.
    private const int LogfileSize = 448;

    private void OpenConsumer()
    {
        _self = GCHandle.Alloc(this);
        var logfile = stackalloc byte[LogfileSize];
        var span = new Span<byte>(logfile, LogfileSize);
        span.Clear();
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            *(IntPtr*)(logfile + 8) = name;                                                   // LoggerName
            BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 0x100 | 0x10000000);         // REAL_TIME | EVENT_RECORD
            *(delegate* unmanaged[Stdcall]<byte*, void>*)(logfile + 424) = &OnEvent;          // EventRecordCallback
            *(IntPtr*)(logfile + 440) = GCHandle.ToIntPtr(_self);                             // Context
            _trace = OpenTraceW(logfile);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
        if (_trace == InvalidTrace)
            throw new IpHelperException(Marshal.GetLastPInvokeError(), "OpenTrace");

        _thread = new Thread(() =>
        {
            var handle = _trace;
            _ = ProcessTrace(&handle, 1, IntPtr.Zero, IntPtr.Zero);
        })
        { IsBackground = true, Name = "O2C ETW" };
        _thread.Start();
    }

    // EVENT_RECORD (evntcons.h, x64): EventHeader.EventDescriptor.Id — смещение 40, UserDataLength — 86,
    // UserData — 96, UserContext — 104.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(byte* record)
    {
        try
        {
            var context = *(IntPtr*)(record + 104);
            if (context != IntPtr.Zero && GCHandle.FromIntPtr(context).Target is AppConnectionMonitor monitor)
                monitor.Handle(record);
        }
        catch (Exception)
        {
            // Исключение не должно уйти в поток ETW.
        }
    }

    private void Handle(byte* record)
    {
        Interlocked.Increment(ref _eventCount);
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(record + 40, 2));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(record + 86, 2));
        var data = new ReadOnlySpan<byte>(*(byte**)(record + 96), length);

        // Данные событий: PID, size, daddr, saddr, dport, sport, ... (адреса и порты — в сетевом порядке байт).
        bool v6 = id is TcpConnectV6 or UdpSendV6;
        int addrSize = v6 ? 16 : 4;
        if (data.Length < 8 + (2 * addrSize) + 2)
            return;
        int pid = BinaryPrimitives.ReadInt32LittleEndian(data);
        var remote = new IPAddress(data.Slice(8, addrSize));
        int port = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8 + (2 * addrSize), 2));
        int protocol = id is TcpConnectV4 or TcpConnectV6 ? 6 : 17;

        long now = Environment.TickCount64;
        var key = (pid, remote, protocol);
        if (_recent.TryGetValue(key, out var last) && now - last < (long)RepeatInterval.TotalMilliseconds)
            return;
        _recent[key] = now;
        if (_recent.Count > 100_000)
            _recent.Clear();

        if (ProcessPath(pid, now) is { } path)
            _onConnection(new AppConnection(path, pid, remote, port, protocol, DateTime.UtcNow));
    }

    /// <summary>Путь exe по PID (с кэшем на минуту: PID может быть переиспользован).</summary>
    private string? ProcessPath(int pid, long now)
    {
        if (_paths.TryGetValue(pid, out var cached) && cached.Expires > now)
            return cached.Path;
        string? path = null;
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var buffer = new char[1024];
                int size = buffer.Length;
                if (QueryFullProcessImageNameW(process, 0, buffer, ref size))
                    path = new string(buffer, 0, size);
            }
            finally
            {
                CloseHandle(process);
            }
        }
        _paths[pid] = (path, now + 60_000);
        return path;
    }

    public void Dispose()
    {
        if (_session != 0)
        {
            var props = stackalloc byte[PropertiesTotal];
            FillProperties(props);
            _ = ControlTraceW(_session, null, props, 1 /* STOP */);
            _session = 0;
        }
        if (_trace != InvalidTrace)
        {
            _ = CloseTrace(_trace);
            _trace = InvalidTrace;
        }
        _thread?.Join(TimeSpan.FromSeconds(5));
        if (_self.IsAllocated)
            _self.Free();
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StartTraceW(out ulong sessionHandle, string sessionName, byte* properties);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ControlTraceW(ulong sessionHandle, string? sessionName, byte* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    private static partial int EnableTraceEx2(ulong sessionHandle, Guid* providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, byte* enableParameters);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial ulong OpenTraceW(byte* logfile);

    [LibraryImport("advapi32.dll")]
    private static partial int ProcessTrace(ulong* handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [LibraryImport("advapi32.dll")]
    private static partial int CloseTrace(ulong traceHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] exeName, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
