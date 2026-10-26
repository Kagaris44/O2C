using System.Globalization;
using System.Net;
using System.Text.Json;

namespace O2C.Core.Exclusions;

/// <summary>
/// Подсети, к которым обращался компьютер, для записи «Российские сети»: подсеть → когда видели в последний раз.
/// Подсеть забывается, если к ней не обращались <see cref="Ttl"/>. Хранится между перезапусками службы,
/// поэтому при подключении маршруты на привычные подсети ставятся сразу. Потокобезопасен.
/// </summary>
public sealed class NetworkBook
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>Больше подсетей не храним: при переполнении забываются самые старые.</summary>
    public const int MaxNetworks = 2048;

    private readonly Lock _lock = new();
    private readonly Dictionary<(IPAddress Network, int Prefix), DateTime> _seen = [];

    /// <summary>Есть изменения, не сохранённые на диск.</summary>
    public bool Dirty { get; private set; }

    public int Count
    {
        get
        {
            lock (_lock)
                return _seen.Count;
        }
    }

    /// <summary>Отмечает обращение к подсети. Возвращает true, если подсеть новая.</summary>
    public bool Learn(IPAddress network, int prefix, DateTime now)
    {
        lock (_lock)
        {
            var key = (ExclusionParser.Mask(network, prefix), prefix);
            bool added = !_seen.ContainsKey(key);
            // Продлеваем не чаще раза в минуту — меньше записей на диск при частых соединениях.
            if (added || now - _seen[key] > TimeSpan.FromMinutes(1))
            {
                _seen[key] = now;
                Dirty = true;
            }
            if (added && _seen.Count > MaxNetworks)
                _seen.Remove(_seen.MinBy(kv => kv.Value).Key);
            return added;
        }
    }

    public IReadOnlyList<(IPAddress Network, int Prefix)> Get()
    {
        lock (_lock)
            return [.. _seen.Keys];
    }

    /// <summary>Забывает подсети, к которым не обращались дольше срока.</summary>
    public void Prune(DateTime now)
    {
        lock (_lock)
        {
            foreach (var (key, seen) in _seen.ToList())
            {
                if (now - seen > Ttl)
                {
                    _seen.Remove(key);
                    Dirty = true;
                }
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_seen.Count == 0)
                return;
            _seen.Clear();
            Dirty = true;
        }
    }

    public string Serialize()
    {
        lock (_lock)
        {
            Dirty = false;
            return JsonSerializer.Serialize(_seen.ToDictionary(kv => $"{kv.Key.Network}/{kv.Key.Prefix}", kv => kv.Value));
        }
    }

    public static NetworkBook Deserialize(string json)
    {
        var book = new NetworkBook();
        var data = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? [];
        foreach (var (text, seen) in data)
        {
            var slash = text.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && IPAddress.TryParse(text.AsSpan(0, slash), out var network) &&
                int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            {
                book._seen[(network, prefix)] = DateTime.SpecifyKind(seen, DateTimeKind.Utc);
            }
        }
        return book;
    }
}
