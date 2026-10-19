using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace O2C.Network;

/// <summary>Запись таблицы маршрутов Windows.</summary>
public sealed record RouteEntry(
    ulong InterfaceLuid,
    uint InterfaceIndex,
    IPAddress Destination,
    int PrefixLength,
    IPAddress NextHop,
    uint Metric)
{
    public bool IsDefault => PrefixLength == 0;
    public bool HasGateway => !NextHop.Equals(IPAddress.Any) && !NextHop.Equals(IPAddress.IPv6Any);
    public override string ToString() => $"{Destination}/{PrefixLength} via {NextHop} if {InterfaceIndex} metric {Metric}";
}

/// <summary>
/// Обёртка над IP Helper API (iphlpapi.dll): чтение таблицы маршрутов, добавление и удаление маршрутов.
/// Структуры читаются по смещениям из заголовков Windows SDK (x64); размеры проверяются тестами.
/// </summary>
public static unsafe partial class IpHelper
{
    private const ushort AF_UNSPEC = 0;
    private const ushort AF_INET = 2;
    private const ushort AF_INET6 = 23;

    private const int NO_ERROR = 0;
    private const int ERROR_NOT_FOUND = 1168;
    private const int ERROR_OBJECT_ALREADY_EXISTS = 5010;
    private const int MIB_IPPROTO_NETMGMT = 3;

    // MIB_IPFORWARD_ROW2 (netioapi.h), 104 байта.
    public const int ForwardRowSize = 104;
    private const int FwdLuid = 0;
    private const int FwdIfIndex = 8;
    private const int FwdDestPrefix = 12;      // SOCKADDR_INET (28) + UINT8 PrefixLength
    private const int FwdDestPrefixLength = 40;
    private const int FwdNextHop = 44;         // SOCKADDR_INET (28)
    private const int FwdMetric = 84;
    private const int FwdProtocol = 88;

    // MIB_IPFORWARD_TABLE2: ULONG NumEntries, затем массив строк с выравниванием 8.
    private const int ForwardTableHeader = 8;

    // MIB_IPINTERFACE_ROW (netioapi.h), 168 байт.
    public const int InterfaceRowSize = 168;
    private const int IfFamily = 0;
    private const int IfLuid = 8;
    private const int IfMetric = 148;

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIpForwardTable2(ushort family, out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(IntPtr memory);

    [LibraryImport("iphlpapi.dll")]
    private static partial void InitializeIpForwardEntry(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int CreateIpForwardEntry2(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int DeleteIpForwardEntry2(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial void InitializeIpInterfaceEntry(byte* row);

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIpInterfaceEntry(byte* row);

    [LibraryImport("iphlpapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ConvertInterfaceAliasToLuid(string alias, out ulong luid);

    [LibraryImport("iphlpapi.dll")]
    private static partial int ConvertInterfaceLuidToIndex(in ulong luid, out uint index);

    public static List<RouteEntry> GetRoutes()
    {
        int error = GetIpForwardTable2(AF_UNSPEC, out var table);
        if (error != NO_ERROR)
            throw new IpHelperException(error, "GetIpForwardTable2");
        try
        {
            uint count = (uint)Marshal.ReadInt32(table);
            var result = new List<RouteEntry>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var row = new ReadOnlySpan<byte>((byte*)table + ForwardTableHeader + (i * ForwardRowSize), ForwardRowSize);
                var destination = ReadSockAddr(row[FwdDestPrefix..]);
                var nextHop = ReadSockAddr(row[FwdNextHop..]);
                if (destination is null || nextHop is null)
                    continue;
                result.Add(new RouteEntry(
                    BinaryPrimitives.ReadUInt64LittleEndian(row[FwdLuid..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(row[FwdIfIndex..]),
                    destination,
                    row[FwdDestPrefixLength],
                    nextHop,
                    BinaryPrimitives.ReadUInt32LittleEndian(row[FwdMetric..])));
            }
            return result;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>Добавляет маршрут. Возвращает false, если точно такой маршрут уже существовал.</summary>
    public static bool AddRoute(ulong interfaceLuid, IPAddress destination, int prefixLength, IPAddress nextHop, uint metric)
    {
        var row = stackalloc byte[ForwardRowSize];
        new Span<byte>(row, ForwardRowSize).Clear();
        InitializeIpForwardEntry(row);
        FillRoute(new Span<byte>(row, ForwardRowSize), interfaceLuid, destination, prefixLength, nextHop);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(row + FwdMetric, 4), metric);
        BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(row + FwdProtocol, 4), MIB_IPPROTO_NETMGMT);

        int error = CreateIpForwardEntry2(row);
        if (error == ERROR_OBJECT_ALREADY_EXISTS)
            return false;
        if (error != NO_ERROR)
            throw new IpHelperException(error, $"CreateIpForwardEntry2 {destination}/{prefixLength}");
        return true;
    }

    /// <summary>Удаляет маршрут. Отсутствующий маршрут — не ошибка.</summary>
    public static void DeleteRoute(ulong interfaceLuid, IPAddress destination, int prefixLength, IPAddress nextHop)
    {
        var row = stackalloc byte[ForwardRowSize];
        new Span<byte>(row, ForwardRowSize).Clear();
        InitializeIpForwardEntry(row);
        FillRoute(new Span<byte>(row, ForwardRowSize), interfaceLuid, destination, prefixLength, nextHop);

        int error = DeleteIpForwardEntry2(row);
        if (error != NO_ERROR && error != ERROR_NOT_FOUND)
            throw new IpHelperException(error, $"DeleteIpForwardEntry2 {destination}/{prefixLength}");
    }

    /// <summary>Метрика интерфейса для семейства адресов; null, если интерфейс не настроен для него.</summary>
    public static uint? GetInterfaceMetric(ulong interfaceLuid, AddressFamily family)
    {
        var row = stackalloc byte[InterfaceRowSize];
        new Span<byte>(row, InterfaceRowSize).Clear();
        InitializeIpInterfaceEntry(row);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(row + IfFamily, 2), ToNative(family));
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(row + IfLuid, 8), interfaceLuid);
        if (GetIpInterfaceEntry(row) != NO_ERROR)
            return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(row + IfMetric, 4));
    }

    public static ulong? TryGetLuidByAlias(string alias) =>
        ConvertInterfaceAliasToLuid(alias, out var luid) == NO_ERROR ? luid : null;

    public static uint? TryGetIndexByLuid(ulong luid) =>
        ConvertInterfaceLuidToIndex(luid, out var index) == NO_ERROR ? index : null;

    private static void FillRoute(Span<byte> row, ulong interfaceLuid, IPAddress destination, int prefixLength, IPAddress nextHop)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(row[FwdLuid..], interfaceLuid);
        WriteSockAddr(row[FwdDestPrefix..], destination);
        row[FwdDestPrefixLength] = (byte)prefixLength;
        WriteSockAddr(row[FwdNextHop..], nextHop);
    }

    // SOCKADDR_INET: sockaddr_in { family, port, addr[4], zero[8] } | sockaddr_in6 { family, port, flowinfo, addr[16], scope_id }.
    public static IPAddress? ReadSockAddr(ReadOnlySpan<byte> sa)
    {
        var family = BinaryPrimitives.ReadUInt16LittleEndian(sa);
        return family switch
        {
            AF_INET => new IPAddress(sa.Slice(4, 4)),
            AF_INET6 => new IPAddress(sa.Slice(8, 16), BinaryPrimitives.ReadUInt32LittleEndian(sa[24..])),
            _ => null,
        };
    }

    public static void WriteSockAddr(Span<byte> sa, IPAddress address)
    {
        sa[..28].Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(sa, ToNative(address.AddressFamily));
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            address.TryWriteBytes(sa.Slice(4, 4), out _);
        }
        else
        {
            address.TryWriteBytes(sa.Slice(8, 16), out _);
            BinaryPrimitives.WriteUInt32LittleEndian(sa[24..], (uint)address.ScopeId);
        }
    }

    private static ushort ToNative(AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => AF_INET,
        AddressFamily.InterNetworkV6 => AF_INET6,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}

public sealed class IpHelperException(int error, string operation)
    : System.ComponentModel.Win32Exception(error, $"{operation}: {new System.ComponentModel.Win32Exception(error).Message} ({error})");
