using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace O2C.Network;

/// <summary>Новое разрешённое соединение программы: путь exe, адрес и порт собеседника, протокол.</summary>
public sealed record AppConnection(string AppPath, IPAddress RemoteAddress, int RemotePort, int Protocol, DateTime Time);

/// <summary>
/// События сетевого фильтра Windows (WFP) о разрешённых соединениях: по одному на новое соединение
/// (TCP и UDP), с путём программы и адресом собеседника. Без драйвера и без перехвата трафика.
/// На время работы включает в движке WFP сбор событий «разрешено» и возвращает прежние настройки при остановке.
/// Нужны права администратора.
/// </summary>
public sealed unsafe partial class AppConnectionMonitor : IDisposable
{
    private const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;
    private const int FWPM_ENGINE_COLLECT_NET_EVENTS = 0;
    private const int FWPM_ENGINE_NET_EVENT_MATCH_ANY_KEYWORDS = 1;
    private const uint FWPM_NET_EVENT_KEYWORD_CLASSIFY_ALLOW = 0x10;
    private const int FWP_UINT32 = 3;
    private const int FWPM_NET_EVENT_TYPE_CLASSIFY_ALLOW = 6;

    // FWPM_NET_EVENT_HEADER2 (fwpmtypes.h), x64.
    private const int HdrFlags = 8;
    private const int HdrIpVersion = 12;
    private const int HdrProtocol = 16;
    private const int HdrRemoteAddr = 36;
    private const int HdrRemotePort = 54;
    private const int HdrAppId = 64;          // FWP_BYTE_BLOB { UINT32 size; UINT8* data }
    private const int HeaderSize = 104;       // затем FWPM_NET_EVENT_TYPE type
    private const uint FlagRemoteAddr = 0x4;
    private const uint FlagRemotePort = 0x10;
    private const uint FlagAppId = 0x20;
    private const int FwpIpVersionV4 = 0;

    private readonly Action<AppConnection> _onConnection;
    private readonly DevicePathMapper _paths = new();
    private IntPtr _engine;
    private IntPtr _subscription;
    private GCHandle _self;
    private uint? _previousCollect;
    private uint? _previousKeywords;

    public AppConnectionMonitor(Action<AppConnection> onConnection) => _onConnection = onConnection;

    /// <summary>Любое событие движка (тип FWPM_NET_EVENT_TYPE) — для диагностики.</summary>
    public event Action<int>? RawEvent;

    /// <summary>Текущие значения настроек сбора событий (для диагностики).</summary>
    /// <summary>Диагностика: 0 — без шаблона отбора, 1 — пустой шаблон, 2 — шаблон с интервалом времени.</summary>
    public int TemplateMode { get; init; }

    public (uint? Collect, uint? Keywords) Options => (GetOption(FWPM_ENGINE_COLLECT_NET_EVENTS), GetOption(FWPM_ENGINE_NET_EVENT_MATCH_ANY_KEYWORDS));

    public void Start()
    {
        int error = FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, IntPtr.Zero, IntPtr.Zero, out _engine);
        if (error != 0)
            throw new IpHelperException(error, "FwpmEngineOpen0");

        _previousCollect = GetOption(FWPM_ENGINE_COLLECT_NET_EVENTS);
        _previousKeywords = GetOption(FWPM_ENGINE_NET_EVENT_MATCH_ANY_KEYWORDS);
        SetOption(FWPM_ENGINE_COLLECT_NET_EVENTS, 1);
        SetOption(FWPM_ENGINE_NET_EVENT_MATCH_ANY_KEYWORDS, (_previousKeywords ?? 0) | FWPM_NET_EVENT_KEYWORD_CLASSIFY_ALLOW);

        _self = GCHandle.Alloc(this);
        // FWPM_NET_EVENT_SUBSCRIPTION0: enumTemplate, flags = 0, sessionKey = 0.
        var subscription = stackalloc byte[32];
        new Span<byte>(subscription, 32).Clear();
        var template = stackalloc byte[32];
        FillTemplate(template, TimeSpan.FromMinutes(1));
        if (TemplateMode == 1)
            new Span<byte>(template, 32).Clear();
        if (TemplateMode != 0)
            *(byte**)subscription = template;
        error = FwpmNetEventSubscribe1(_engine, subscription, &OnEvent, GCHandle.ToIntPtr(_self), out _subscription);
        if (error != 0)
            throw new IpHelperException(error, "FwpmNetEventSubscribe1");
    }

    // FWPM_NET_EVENT_ENUM_TEMPLATE0 { FILETIME startTime; FILETIME endTime; UINT32 numFilterConditions; FWPM_FILTER_CONDITION0* filterCondition; }
    private static void FillTemplate(byte* template, TimeSpan back)
    {
        var span = new Span<byte>(template, 32);
        span.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(span, DateTime.UtcNow.Subtract(back).ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], DateTime.UtcNow.AddDays(1).ToFileTimeUtc());
    }

    /// <summary>Диагностика: типы событий, накопленных движком за последнее время (без подписки).</summary>
    public static Dictionary<int, int> EnumRecentTypes(TimeSpan back, List<string> samples)
    {
        var result = new Dictionary<int, int>();
        int rc = FwpmEngineOpen0(null, RPC_C_AUTHN_DEFAULT, IntPtr.Zero, IntPtr.Zero, out var engine);
        samples.Add($"open=0x{rc:X}");
        if (rc != 0)
            return result;
        try
        {
            var template = stackalloc byte[32];
            FillTemplate(template, back);
            rc = FwpmNetEventCreateEnumHandle0(engine, template, out var handle);
            samples.Add($"createEnum=0x{rc:X}");
            if (rc != 0)
                return result;
            try
            {
                rc = FwpmNetEventEnum1(engine, handle, 5000, out var entries, out var count);
                samples.Add($"enum=0x{rc:X} count={count}");
                if (rc != 0 || entries == IntPtr.Zero)
                    return result;
                try
                {
                    var mapper = new DevicePathMapper();
                    for (int i = 0; i < count; i++)
                    {
                        var e = *(byte**)((byte*)entries + (i * IntPtr.Size));
                        int type = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(e + HeaderSize, 4));
                        result[type] = result.GetValueOrDefault(type) + 1;
                        if (type == FWPM_NET_EVENT_TYPE_CLASSIFY_ALLOW && samples.Count < 10)
                        {
                            uint size = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(e + HdrAppId, 4));
                            var data = *(byte**)(e + HdrAppId + 8);
                            var app = size > 1 && data is not null ? Encoding.Unicode.GetString(data, (int)size).TrimEnd((char)0) : "?";
                            samples.Add(mapper.ToDosPath(app));
                        }
                    }
                }
                finally
                {
                    FwpmFreeMemory0(ref entries);
                }
            }
            finally
            {
                _ = FwpmNetEventDestroyEnumHandle0(engine, handle);
            }
        }
        finally
        {
            _ = FwpmEngineClose0(engine);
        }
        return result;
    }

    /// <summary>Диагностика: сколько раз Windows вызвала обработчик и первая ошибка в нём.</summary>
    public static int CallbackCount => _callbackCount;
    public static string? LastCallbackError { get; private set; }
    private static int _callbackCount;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(IntPtr context, byte* netEvent)
    {
        Interlocked.Increment(ref _callbackCount);
        try
        {
            if (GCHandle.FromIntPtr(context).Target is AppConnectionMonitor monitor)
                monitor.Handle(netEvent);
        }
        catch (Exception ex)
        {
            // Исключение не должно уйти в RPC-поток Windows.
            LastCallbackError ??= ex.ToString();
        }
    }

    private void Handle(byte* e)
    {
        int type = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(e + HeaderSize, 4));
        RawEvent?.Invoke(type);
        if (type != FWPM_NET_EVENT_TYPE_CLASSIFY_ALLOW)
            return;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(e + HdrFlags, 4));
        const uint needed = FlagRemoteAddr | FlagAppId;
        if ((flags & needed) != needed)
            return;

        IPAddress remote;
        if (BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(e + HdrIpVersion, 4)) == FwpIpVersionV4)
        {
            // IPv4 в заголовке события — в порядке байт хоста.
            uint v4 = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(e + HdrRemoteAddr, 4));
            remote = new IPAddress(BinaryPrimitives.ReverseEndianness(v4));
        }
        else
        {
            remote = new IPAddress(new ReadOnlySpan<byte>(e + HdrRemoteAddr, 16));
        }

        int port = (flags & FlagRemotePort) != 0 ? BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(e + HdrRemotePort, 2)) : 0;
        uint appSize = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(e + HdrAppId, 4));
        var appData = *(byte**)(e + HdrAppId + 8);
        if (appSize < 2 || appData is null)
            return;
        // appId — путь устройства в UTF-16 с завершающим нулём: \device\harddiskvolume3\...\app.exe
        var devicePath = Encoding.Unicode.GetString(appData, (int)appSize).TrimEnd('\0');

        _onConnection(new AppConnection(_paths.ToDosPath(devicePath), remote, port, e[HdrProtocol], DateTime.UtcNow));
    }

    private uint? GetOption(int option)
    {
        if (FwpmEngineGetOption0(_engine, option, out var value) != 0 || value == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.ReadInt32(value) == FWP_UINT32 ? (uint)Marshal.ReadInt32(value, 8) : null;
        }
        finally
        {
            FwpmFreeMemory0(ref value);
        }
    }

    private void SetOption(int option, uint newValue)
    {
        // FWP_VALUE0 { FWP_DATA_TYPE type; union { UINT32 uint32; ... } } — 16 байт.
        var value = stackalloc byte[16];
        new Span<byte>(value, 16).Clear();
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(value, 4), FWP_UINT32);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(value + 8, 4), newValue);
        int error = FwpmEngineSetOption0(_engine, option, value);
        if (error != 0)
            throw new IpHelperException(error, $"FwpmEngineSetOption0({option})");
    }

    public void Dispose()
    {
        if (_subscription != IntPtr.Zero)
        {
            _ = FwpmNetEventUnsubscribe0(_engine, _subscription);
            _subscription = IntPtr.Zero;
        }
        if (_engine != IntPtr.Zero)
        {
            // Возвращаем движку прежние настройки сбора событий.
            try
            {
                if (_previousKeywords is { } keywords)
                    SetOption(FWPM_ENGINE_NET_EVENT_MATCH_ANY_KEYWORDS, keywords);
                if (_previousCollect is { } collect)
                    SetOption(FWPM_ENGINE_COLLECT_NET_EVENTS, collect);
            }
            catch (IpHelperException)
            {
            }
            _ = FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
        }
        if (_self.IsAllocated)
            _self.Free();
    }

    [LibraryImport("fwpuclnt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int FwpmEngineOpen0(string? serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmEngineClose0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmEngineGetOption0(IntPtr engineHandle, int option, out IntPtr value);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmEngineSetOption0(IntPtr engineHandle, int option, byte* newValue);

    [LibraryImport("fwpuclnt.dll")]
    private static partial void FwpmFreeMemory0(ref IntPtr p);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmNetEventSubscribe1(IntPtr engineHandle, byte* subscription,
        delegate* unmanaged[Stdcall]<IntPtr, byte*, void> callback, IntPtr context, out IntPtr eventsHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmNetEventUnsubscribe0(IntPtr engineHandle, IntPtr eventsHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmNetEventCreateEnumHandle0(IntPtr engineHandle, byte* enumTemplate, out IntPtr enumHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmNetEventEnum1(IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested, out IntPtr entries, out uint numEntriesReturned);

    [LibraryImport("fwpuclnt.dll")]
    private static partial int FwpmNetEventDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);
}

/// <summary>Перевод пути устройства (\device\harddiskvolume3\...) в обычный путь (C:\...).</summary>
public sealed partial class DevicePathMapper
{
    private readonly Dictionary<string, string> _devices = new(StringComparer.OrdinalIgnoreCase);

    public DevicePathMapper() => Refresh();

    public void Refresh()
    {
        _devices.Clear();
        var buffer = new char[1024];
        foreach (var drive in DriveInfo.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\');   // "C:"
            if (QueryDosDevice(letter, buffer, buffer.Length) > 0)
                _devices[new string(buffer).Split('\0')[0]] = letter;
        }
    }

    public string ToDosPath(string devicePath)
    {
        foreach (var (device, letter) in _devices)
        {
            if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return letter + devicePath[device.Length..];
        }
        return devicePath;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int QueryDosDevice(string deviceName, [Out] char[] targetPath, int max);
}
