using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace O2C.Network;

/// <summary>Адрес, назначенный интерфейсу.</summary>
public sealed record UnicastAddress(ulong InterfaceLuid, IPAddress Address, int PrefixLength);

/// <summary>Настройка самого интерфейса туннеля: адреса, MTU, метрика, DNS.</summary>
public static unsafe partial class IpHelper
{
    // MIB_UNICASTIPADDRESS_ROW (netioapi.h), 80 байт.
    public const int UnicastRowSize = 80;
    private const int UniAddress = 0;            // SOCKADDR_INET (28)
    private const int UniLuid = 32;
    private const int UniOnLinkPrefixLength = 60;
    private const int UniDadState = 64;
    private const int IpDadStatePreferred = 4;

    // Поля MIB_IPINTERFACE_ROW, которые меняем.
    private const int IfUseAutomaticMetric = 44;
    private const int IfSitePrefixLength = 144;
    private const int IfNlMtu = 152;

    // DNS_INTERFACE_SETTINGS (netioapi.h, Windows 10 2004+), 64 байта.
    private const int DnsSettingsSize = 64;
    private const ulong DNS_SETTING_IPV6 = 0x1;
    private const ulong DNS_SETTING_NAMESERVER = 0x2;
    private const ulong DNS_SETTING_SEARCHLIST = 0x4;

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void InitializeUnicastIpAddressEntry(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int CreateUnicastIpAddressEntry(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int DeleteUnicastIpAddressEntry(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int SetIpInterfaceEntry(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int ConvertInterfaceIndexToLuid(uint index, out ulong luid);

    [LibraryImport("iphlpapi.dll")]
    private static partial int ConvertInterfaceLuidToGuid(in ulong luid, out Guid guid);

    [LibraryImport("iphlpapi.dll")]
    private static partial int SetInterfaceDnsSettings(Guid interfaceGuid, byte* settings);

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();

    public static ulong LuidFromIndex(uint index)
    {
        int error = ConvertInterfaceIndexToLuid(index, out var luid);
        if (error != NO_ERROR)
            throw new IpHelperException(error, $"ConvertInterfaceIndexToLuid {index}");
        return luid;
    }

    public static List<UnicastAddress> GetAddresses()
    {
        int error = GetUnicastIpAddressTable(AF_UNSPEC, out var table);
        if (error != NO_ERROR)
            throw new IpHelperException(error, "GetUnicastIpAddressTable");
        try
        {
            uint count = (uint)Marshal.ReadInt32(table);
            var result = new List<UnicastAddress>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var row = new ReadOnlySpan<byte>((byte*)table + ForwardTableHeader + (i * UnicastRowSize), UnicastRowSize);
                if (ReadSockAddr(row[UniAddress..]) is { } address)
                    result.Add(new UnicastAddress(BinaryPrimitives.ReadUInt64LittleEndian(row[UniLuid..]), address, row[UniOnLinkPrefixLength]));
            }
            return result;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>Назначает адрес интерфейсу (без проверки дубликатов в сети — для туннеля она не нужна).</summary>
    public static void AddAddress(ulong luid, IPAddress address, int prefixLength)
    {
        var row = stackalloc byte[UnicastRowSize];
        new Span<byte>(row, UnicastRowSize).Clear();
        InitializeUnicastIpAddressEntry(row);
        var span = new Span<byte>(row, UnicastRowSize);
        WriteSockAddr(span[UniAddress..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(span[UniLuid..], luid);
        span[UniOnLinkPrefixLength] = (byte)prefixLength;
        BinaryPrimitives.WriteInt32LittleEndian(span[UniDadState..], IpDadStatePreferred);

        int error = CreateUnicastIpAddressEntry(row);
        if (error != NO_ERROR && error != ERROR_OBJECT_ALREADY_EXISTS)
            throw new IpHelperException(error, $"CreateUnicastIpAddressEntry {address}/{prefixLength}");
    }

    /// <summary>Удаляет все адреса интерфейса указанного семейства (или обоих).</summary>
    public static void FlushAddresses(ulong luid, AddressFamily? family = null)
    {
        var row = stackalloc byte[UnicastRowSize];
        foreach (var a in GetAddresses().Where(a => a.InterfaceLuid == luid && (family is null || a.Address.AddressFamily == family)))
        {
            new Span<byte>(row, UnicastRowSize).Clear();
            InitializeUnicastIpAddressEntry(row);
            var span = new Span<byte>(row, UnicastRowSize);
            WriteSockAddr(span[UniAddress..], a.Address);
            BinaryPrimitives.WriteUInt64LittleEndian(span[UniLuid..], luid);
            int error = DeleteUnicastIpAddressEntry(row);
            if (error != NO_ERROR && error != ERROR_NOT_FOUND)
                throw new IpHelperException(error, $"DeleteUnicastIpAddressEntry {a.Address}");
        }
    }

    /// <summary>Удаляет все маршруты интерфейса указанного семейства (или обоих).</summary>
    public static void FlushRoutes(ulong luid, AddressFamily? family = null)
    {
        foreach (var r in GetRoutes().Where(r => r.InterfaceLuid == luid && (family is null || r.Destination.AddressFamily == family)))
            DeleteRoute(luid, r.Destination, r.PrefixLength, r.NextHop);
    }

    /// <summary>
    /// Меняет MTU и/или метрику интерфейса. metric = null — оставить автоматическую.
    /// Возвращает false, если интерфейс не настроен для этого семейства.
    /// </summary>
    public static bool SetInterfaceParameters(ulong luid, AddressFamily family, int? mtu, uint? metric)
    {
        var row = stackalloc byte[InterfaceRowSize];
        new Span<byte>(row, InterfaceRowSize).Clear();
        InitializeIpInterfaceEntry(row);
        var span = new Span<byte>(row, InterfaceRowSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[IfFamily..], family == AddressFamily.InterNetwork ? AF_INET : AF_INET6);
        BinaryPrimitives.WriteUInt64LittleEndian(span[IfLuid..], luid);
        if (GetIpInterfaceEntry(row) != NO_ERROR)
            return false;

        if (mtu is { } m)
            BinaryPrimitives.WriteUInt32LittleEndian(span[IfNlMtu..], (uint)m);
        if (metric is { } value)
        {
            span[IfUseAutomaticMetric] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(span[IfMetric..], value);
        }
        // Требование SetIpInterfaceEntry: для IPv4 SitePrefixLength должен быть 0.
        if (family == AddressFamily.InterNetwork)
            BinaryPrimitives.WriteUInt32LittleEndian(span[IfSitePrefixLength..], 0);

        int error = SetIpInterfaceEntry(row);
        if (error != NO_ERROR)
            throw new IpHelperException(error, $"SetIpInterfaceEntry mtu={mtu} metric={metric}");
        return true;
    }

    /// <summary>
    /// DNS-серверы и суффиксы поиска интерфейса (SetInterfaceDnsSettings, Windows 10 2004+).
    /// Пустой список серверов очищает настройку.
    /// </summary>
    public static void SetDns(ulong luid, AddressFamily family, IReadOnlyList<IPAddress> servers, IReadOnlyList<string> searchDomains)
    {
        int error = ConvertInterfaceLuidToGuid(luid, out var guid);
        if (error != NO_ERROR)
            throw new IpHelperException(error, "ConvertInterfaceLuidToGuid");

        var nameServer = Marshal.StringToHGlobalUni(string.Join(',', servers));
        var searchList = Marshal.StringToHGlobalUni(string.Join(',', searchDomains));
        try
        {
            var settings = stackalloc byte[DnsSettingsSize];
            var span = new Span<byte>(settings, DnsSettingsSize);
            span.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(span, 1); // DNS_INTERFACE_SETTINGS_VERSION1
            ulong flags = DNS_SETTING_NAMESERVER | DNS_SETTING_SEARCHLIST |
                          (family == AddressFamily.InterNetworkV6 ? DNS_SETTING_IPV6 : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], flags);
            BinaryPrimitives.WriteInt64LittleEndian(span[24..], nameServer);
            BinaryPrimitives.WriteInt64LittleEndian(span[32..], searchList);

            error = SetInterfaceDnsSettings(guid, settings);
            if (error != NO_ERROR)
                throw new IpHelperException(error, "SetInterfaceDnsSettings");
        }
        finally
        {
            Marshal.FreeHGlobal(nameServer);
            Marshal.FreeHGlobal(searchList);
        }
    }

    public static void FlushDnsCache() => DnsFlushResolverCache();
}
