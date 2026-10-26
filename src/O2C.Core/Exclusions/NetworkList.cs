using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace O2C.Core.Exclusions;

/// <summary>
/// Неизменяемый набор подсетей (IPv4 и IPv6) с поиском подсети, содержащей адрес, за O(log n).
/// Формат текста: по одной подсети «адрес/длина» на строку; строки с «#» — комментарии,
/// «# date: ГГГГММДД» — дата данных (по ней выбирается более свежий из нескольких списков).
/// </summary>
public sealed class NetworkList
{
    private readonly uint[] _v4Start;
    private readonly uint[] _v4End;
    private readonly byte[] _v4Prefix;
    private readonly UInt128[] _v6Start;
    private readonly UInt128[] _v6End;
    private readonly byte[] _v6Prefix;

    private NetworkList(List<(uint Start, uint End, byte Prefix)> v4, List<(UInt128 Start, UInt128 End, byte Prefix)> v6, string date)
    {
        v4.Sort((a, b) => a.Start.CompareTo(b.Start));
        v6.Sort((a, b) => a.Start.CompareTo(b.Start));
        _v4Start = [.. v4.Select(r => r.Start)];
        _v4End = [.. v4.Select(r => r.End)];
        _v4Prefix = [.. v4.Select(r => r.Prefix)];
        _v6Start = [.. v6.Select(r => r.Start)];
        _v6End = [.. v6.Select(r => r.End)];
        _v6Prefix = [.. v6.Select(r => r.Prefix)];
        Date = date;
    }

    public static NetworkList Empty { get; } = new([], [], "");

    public int Count4 => _v4Start.Length;
    public int Count6 => _v6Start.Length;

    /// <summary>Дата данных из заголовка («ГГГГММДД») или пустая строка.</summary>
    public string Date { get; }

    public static NetworkList Parse(string text)
    {
        var v4 = new List<(uint, uint, byte)>();
        var v6 = new List<(UInt128, UInt128, byte)>();
        string date = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '#')
            {
                if (line.StartsWith("# date:", StringComparison.OrdinalIgnoreCase))
                    date = line[7..].Trim();
                continue;
            }
            var slash = line.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0 || !IPAddress.TryParse(line.AsSpan(0, slash), out var address) ||
                !int.TryParse(line.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            {
                continue;
            }
            if (address.AddressFamily == AddressFamily.InterNetwork && prefix is >= 1 and <= 32)
            {
                uint start = ToUInt32(address) & Mask32(prefix);
                v4.Add((start, start | ~Mask32(prefix), (byte)prefix));
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6 && prefix is >= 1 and <= 128)
            {
                UInt128 start = ToUInt128(address) & Mask128(prefix);
                v6.Add((start, start | ~Mask128(prefix), (byte)prefix));
            }
        }
        return new NetworkList(v4, v6, date);
    }

    /// <summary>Подсеть списка, которая содержит адрес, или null.</summary>
    public (IPAddress Network, int Prefix)? Find(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int i = Floor(_v4Start, ToUInt32(address));
            return i >= 0 && ToUInt32(address) <= _v4End[i] ? (FromUInt32(_v4Start[i]), _v4Prefix[i]) : null;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            int i = Floor(_v6Start, ToUInt128(address));
            return i >= 0 && ToUInt128(address) <= _v6End[i] ? (FromUInt128(_v6Start[i]), _v6Prefix[i]) : null;
        }
        return null;
    }

    public bool Contains(IPAddress address) => Find(address) is not null;

    /// <summary>Индекс последнего начала диапазона, не большего value; -1, если такого нет.</summary>
    private static int Floor<T>(T[] starts, T value) where T : IComparable<T>
    {
        int i = Array.BinarySearch(starts, value);
        return i >= 0 ? i : ~i - 1;
    }

    private static uint Mask32(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
    private static UInt128 Mask128(int prefix) => prefix == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - prefix);

    private static uint ToUInt32(IPAddress a)
    {
        Span<byte> b = stackalloc byte[4];
        a.TryWriteBytes(b, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static UInt128 ToUInt128(IPAddress a)
    {
        Span<byte> b = stackalloc byte[16];
        a.TryWriteBytes(b, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(b);
    }

    private static IPAddress FromUInt32(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return new IPAddress(b);
    }

    private static IPAddress FromUInt128(UInt128 v)
    {
        Span<byte> b = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(b, v);
        return new IPAddress(b);
    }
}
