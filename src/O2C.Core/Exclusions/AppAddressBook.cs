using System.Net;
using System.Text.Json;

namespace O2C.Core.Exclusions;

/// <summary>
/// Адреса, к которым обращались программы из списка исключений: запись → адрес → когда видели в последний раз.
/// Адрес забывается, если программа не обращалась к нему <see cref="Ttl"/> (7 дней); пока программа запущена,
/// срок её адресов продлевается. Хранится между перезапусками службы, поэтому при подключении маршруты
/// ставятся сразу, до первого обращения программы. Потокобезопасен.
/// </summary>
public sealed class AppAddressBook
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    /// <summary>Больше адресов на одну запись не храним: при переполнении забываются самые старые.</summary>
    public const int MaxPerEntry = 1024;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<IPAddress, DateTime>> _book = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Есть изменения, не сохранённые на диск.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Запоминает адрес. Возвращает true, если адрес для записи новый.</summary>
    public bool Learn(string entry, IPAddress address, DateTime now)
    {
        lock (_lock)
        {
            if (!_book.TryGetValue(entry, out var addresses))
                _book[entry] = addresses = [];
            bool added = !addresses.ContainsKey(address);
            addresses[address] = now;
            if (added && addresses.Count > MaxPerEntry)
            {
                var oldest = addresses.MinBy(kv => kv.Value).Key;
                addresses.Remove(oldest);
            }
            Dirty = true;
            return added;
        }
    }

    /// <summary>Программа работает — продлеваем срок всех её адресов.</summary>
    public void Touch(string entry, DateTime now)
    {
        lock (_lock)
        {
            if (!_book.TryGetValue(entry, out var addresses))
                return;
            foreach (var address in addresses.Keys.ToList())
                addresses[address] = now;
            Dirty = true;
        }
    }

    public IReadOnlyList<IPAddress> Get(string entry)
    {
        lock (_lock)
            return _book.TryGetValue(entry, out var addresses) ? [.. addresses.Keys] : [];
    }

    /// <summary>Забывает устаревшие адреса и записи, которых больше нет в списке.</summary>
    public void Prune(DateTime now, IReadOnlySet<string> currentEntries)
    {
        lock (_lock)
        {
            foreach (var entry in _book.Keys.ToList())
            {
                if (!currentEntries.Contains(entry))
                {
                    _book.Remove(entry);
                    Dirty = true;
                    continue;
                }
                var addresses = _book[entry];
                foreach (var (address, seen) in addresses.ToList())
                {
                    if (now - seen > Ttl)
                    {
                        addresses.Remove(address);
                        Dirty = true;
                    }
                }
            }
        }
    }

    public string Serialize()
    {
        lock (_lock)
        {
            Dirty = false;
            var data = _book.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToDictionary(a => a.Key.ToString(), a => a.Value));
            return JsonSerializer.Serialize(data);
        }
    }

    public static AppAddressBook Deserialize(string json)
    {
        var book = new AppAddressBook();
        var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DateTime>>>(json) ?? [];
        foreach (var (entry, addresses) in data)
        {
            var map = new Dictionary<IPAddress, DateTime>();
            foreach (var (text, seen) in addresses)
            {
                if (IPAddress.TryParse(text, out var address))
                    map[address] = DateTime.SpecifyKind(seen, DateTimeKind.Utc);
            }
            book._book[entry] = map;
        }
        return book;
    }
}
