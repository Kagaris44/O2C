using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace O2C.Network;

/// <summary>TCP-соединение IPv4 из системной таблицы.</summary>
public sealed record TcpConnection(IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort, int State);

/// <summary>Таблица TCP-соединений IPv4 и их принудительное закрытие.</summary>
public static unsafe partial class IpHelper
{
    // MIB_TCPROW (tcpmib.h): dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort — 20 байт.
    private const int TcpRowSize = 20;
    private const int TcpTableHeader = 4;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int MIB_TCP_STATE_ESTAB = 5;
    private const int MIB_TCP_STATE_DELETE_TCB = 12;

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);

    [LibraryImport("iphlpapi.dll")]
    private static partial int SetTcpEntry(byte* row);

    public static List<TcpConnection> GetTcpConnections()
    {
        uint size = 0;
        int error = GetTcpTable(null, ref size, false);
        while (error == ERROR_INSUFFICIENT_BUFFER)
        {
            var buffer = new byte[size + (16 * TcpRowSize)];
            size = (uint)buffer.Length;
            fixed (byte* p = buffer)
            {
                error = GetTcpTable(p, ref size, false);
                if (error != NO_ERROR)
                    continue;
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                var result = new List<TcpConnection>((int)count);
                for (int i = 0; i < count; i++)
                {
                    var row = buffer.AsSpan(TcpTableHeader + (i * TcpRowSize), TcpRowSize);
                    result.Add(new TcpConnection(
                        new IPAddress(row.Slice(4, 4)),
                        BinaryPrimitives.ReadUInt16BigEndian(row.Slice(8, 2)),
                        new IPAddress(row.Slice(12, 4)),
                        BinaryPrimitives.ReadUInt16BigEndian(row.Slice(16, 2)),
                        BinaryPrimitives.ReadInt32LittleEndian(row)));
                }
                return result;
            }
        }
        if (error != NO_ERROR)
            throw new IpHelperException(error, "GetTcpTable");
        return [];
    }

    /// <summary>
    /// Закрывает установленные TCP-соединения IPv4, у которых локальный адрес — один из <paramref name="localAddresses"/>,
    /// а удалённый попадает в <paramref name="isRemoteMatch"/>. Нужны права администратора. Возвращает число закрытых.
    /// </summary>
    public static int ResetConnections(IReadOnlySet<IPAddress> localAddresses, Func<IPAddress, bool> isRemoteMatch)
    {
        int reset = 0;
        var row = stackalloc byte[TcpRowSize];
        foreach (var c in GetTcpConnections())
        {
            if (c.State != MIB_TCP_STATE_ESTAB || !localAddresses.Contains(c.LocalAddress) || !isRemoteMatch(c.RemoteAddress))
                continue;

            var span = new Span<byte>(row, TcpRowSize);
            BinaryPrimitives.WriteInt32LittleEndian(span, MIB_TCP_STATE_DELETE_TCB);
            c.LocalAddress.TryWriteBytes(span.Slice(4, 4), out _);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), 0);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(8, 2), (ushort)c.LocalPort);
            c.RemoteAddress.TryWriteBytes(span.Slice(12, 4), out _);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), 0);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(16, 2), (ushort)c.RemotePort);
            if (SetTcpEntry(row) == NO_ERROR)
                reset++;
        }
        return reset;
    }
}
