using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using O2C.Core;
using O2C.Core.Exclusions;
using O2C.Core.Ipc;
using O2C.Core.OpenConnect;
using O2C.Network;

namespace O2C.Service;

/// <summary>
/// Исключения из туннеля: для каждой записи списка ставит маршрут через основной (физический) шлюз.
/// Более узкий маршрут побеждает маршрут туннеля, поэтому этот трафик идёт мимо туннеля.
/// Работает только при активном подключении; пересчитывается при изменении списка, смене сети
/// и раз в 5 минут (обновление адресов доменов). Адреса доменов и масок *.example.com дополнительно
/// берутся из журнала DNS-клиента Windows — сразу, как только приложение разрешило имя.
/// </summary>
internal sealed class ExclusionManager : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NetworkChangeDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);
    private const uint RouteMetric = 1;

    /// <summary>Сколько помним адрес, увиденный в DNS, если имя больше не запрашивали.</summary>
    private static readonly TimeSpan LearnedTtl = TimeSpan.FromHours(24);
    private const int MaxLearnedPerEntry = 256;

    private readonly ConnectionManager _connection;
    private readonly SettingsStore _settings;
    private readonly LogBuffer _log;
    private readonly ILogger<ExclusionManager> _logger;

    /// <summary>Сигналы «пора пересчитать»; несколько подряд схлопываются в один пересчёт.</summary>
    private readonly Channel<bool> _triggers = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Маршруты, которые поставили мы (и только их мы удаляем).</summary>
    private readonly HashSet<RouteKey> _applied = [];

    /// <summary>Последние успешно полученные адреса доменов — на случай временного сбоя DNS.</summary>
    private readonly Dictionary<string, IPAddress[]> _resolved = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _reportLock = new();
    private ExclusionReport _report = new() { Message = "Исключения действуют только при подключении." };
    private volatile bool _active;

    /// <summary>Нужно ли при пересчёте заново разрешать домены (иначе — пересчёт по уже известным адресам).</summary>
    private int _needResolve = 1;

    /// <summary>Текущие записи списка — для сопоставления событий DNS (читаются из потока журнала).</summary>
    private volatile IReadOnlyList<ExclusionEntry> _entries = [];

    /// <summary>Адреса, увиденные в журнале DNS для записей-доменов и масок: запись → адрес → когда видели.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IPAddress, DateTime>> _learned = new(StringComparer.OrdinalIgnoreCase);

    private DnsClientWatcher? _dnsWatcher;
    private CancellationTokenSource? _networkDebounce;

    public ExclusionManager(ConnectionManager connection, SettingsStore settings, LogBuffer log, ILogger<ExclusionManager> logger)
    {
        _connection = connection;
        _settings = settings;
        _log = log;
        _logger = logger;
    }

    public event Action<ExclusionReport>? ReportChanged;

    public ExclusionReport Report
    {
        get
        {
            lock (_reportLock)
                return _report;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CleanupLeftovers();

        _connection.StatusChanged += OnStatusChanged;
        _settings.ExclusionsChanged += Trigger;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        OnStatusChanged(_connection.Status);

        using var timer = new PeriodicTimer(RefreshInterval);
        var tick = TickAsync(timer, stoppingToken);
        try
        {
            await foreach (var _ in _triggers.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Ошибка применения исключений");
                    SetReport(Report with { Message = $"Ошибка применения исключений: {ex.Message}" });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _connection.StatusChanged -= OnStatusChanged;
            _settings.ExclusionsChanged -= Trigger;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            await tick;
            _dnsWatcher?.Dispose();
            RemoveAll();
        }
    }

    private async Task TickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_active)
                    Trigger();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnStatusChanged(ConnectionStatus status)
    {
        switch (status.State)
        {
            case ConnectionState.Connected:
                _active = true;
                Trigger();
                break;
            case ConnectionState.Disconnected or ConnectionState.Error:
                _active = false;
                Trigger();
                break;
            // Подключение, переподключение, отключение: оставляем как есть до итогового состояния.
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (!_active)
            return;
        // Смена сети порождает пачку событий — ждём, пока всё уляжется.
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _networkDebounce, cts)?.Cancel();
        _ = Task.Delay(NetworkChangeDelay, cts.Token).ContinueWith(
            t => Trigger(), cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Полный пересчёт: с повторным разрешением доменов.</summary>
    public void Trigger()
    {
        Interlocked.Exchange(ref _needResolve, 1);
        _triggers.Writer.TryWrite(true);
    }

    /// <summary>Быстрый пересчёт по уже известным адресам (после события DNS).</summary>
    private void TriggerFast() => _triggers.Writer.TryWrite(true);

    /// <summary>Событие журнала DNS: имя разрешено в адреса. Если имя подходит к записи — запоминаем адреса.</summary>
    private void OnDnsResolved(string name, IReadOnlyList<IPAddress> addresses)
    {
        if (!_active)
            return;
        bool anyNew = false;
        foreach (var entry in _entries)
        {
            if (!DnsNames.Matches(entry, name))
                continue;
            var known = _learned.GetOrAdd(entry.Text, _ => new ConcurrentDictionary<IPAddress, DateTime>());
            bool added = false;
            foreach (var address in addresses)
            {
                if (known.Count >= MaxLearnedPerEntry && !known.ContainsKey(address))
                    break;
                if (known.TryAdd(address, DateTime.UtcNow))
                    added = true;
                else
                    known[address] = DateTime.UtcNow;
            }
            if (added)
            {
                anyNew = true;
                _log.Add($"DNS: {name} → {string.Join(", ", addresses)} (исключение {entry.Text})");
            }
        }
        if (anyNew)
            TriggerFast();
    }

    /// <summary>Журнал DNS нужен, только пока есть подключение и в списке есть домены или маски.</summary>
    private void UpdateDnsWatcher(bool needed)
    {
        try
        {
            if (needed)
            {
                _dnsWatcher ??= new DnsClientWatcher(_logger, OnDnsResolved);
                _dnsWatcher.Start();
            }
            else
            {
                _dnsWatcher?.Stop();
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Журнал DNS-клиента недоступен — маски работать не будут");
            _log.Add($"Журнал DNS-клиента недоступен: {ex.Message}. Маски *.домен не будут применяться.");
        }
    }

    private void PruneLearned()
    {
        var cutoff = DateTime.UtcNow - LearnedTtl;
        var current = _entries.Select(e => e.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, addresses) in _learned)
        {
            if (!current.Contains(entry))
            {
                _learned.TryRemove(entry, out _);
                continue;
            }
            foreach (var (address, seen) in addresses)
            {
                if (seen < cutoff)
                    addresses.TryRemove(address, out _);
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var parsed = ExclusionParser.Parse(_settings.GetExclusionsText());
        _entries = parsed.Entries;
        bool resolve = Interlocked.Exchange(ref _needResolve, 0) == 1;
        UpdateDnsWatcher(_active && parsed.Entries.Any(e => e.Kind is ExclusionKind.Host or ExclusionKind.Wildcard));
        PruneLearned();

        if (!_active)
        {
            RemoveAll();
            SetReport(new ExclusionReport
            {
                Active = false,
                Message = "Исключения действуют только при подключении.",
                Items = [.. parsed.Entries.Select(e => new ExclusionItemStatus { Entry = e.Text })],
                ParseErrors = parsed.Errors,
            });
            return;
        }

        var routes = IpHelper.GetRoutes();
        var interfaces = GatewaySelector.SnapshotInterfaces();
        var tunnelLuid = IpHelper.TryGetLuidByAlias(OpenConnectCommand.InterfaceName);
        uint? tunnelIndex = tunnelLuid is { } luid ? IpHelper.TryGetIndexByLuid(luid) : null;

        var gw4 = GatewaySelector.Find(routes, AddressFamily.InterNetwork, tunnelIndex,
            i => interfaces.GetValueOrDefault(i), l => IpHelper.GetInterfaceMetric(l, AddressFamily.InterNetwork));
        var gw6 = GatewaySelector.Find(routes, AddressFamily.InterNetworkV6, tunnelIndex,
            i => interfaces.GetValueOrDefault(i), l => IpHelper.GetInterfaceMetric(l, AddressFamily.InterNetworkV6));

        var desired = new HashSet<RouteKey>();
        var items = new List<ExclusionItemStatus>();
        foreach (var entry in parsed.Entries)
        {
            var (networks, resolveError) = await GetNetworksAsync(entry, resolve, ct);
            var itemRoutes = new List<string>();
            string? error = resolveError;
            foreach (var (address, length) in networks)
            {
                var gw = address.AddressFamily == AddressFamily.InterNetwork ? gw4 : gw6;
                if (gw is null)
                {
                    error ??= address.AddressFamily == AddressFamily.InterNetwork
                        ? "Не найден основной шлюз IPv4."
                        : "Нет IPv6-подключения в обход туннеля.";
                    continue;
                }
                desired.Add(new RouteKey(gw.InterfaceLuid, address, length, gw.NextHop));
                itemRoutes.Add(ExclusionParser.Mask(address, length) + "/" + length);
            }
            items.Add(new ExclusionItemStatus
            {
                Entry = entry.Text,
                Routes = itemRoutes.Count <= 10 ? itemRoutes : [.. itemRoutes.Take(10), $"…ещё {itemRoutes.Count - 10}"],
                Error = itemRoutes.Count > 0 || entry.Kind == ExclusionKind.Wildcard ? null : error,
                Note = itemRoutes.Count == 0 && entry.Kind == ExclusionKind.Wildcard
                    ? "Ждёт обращений: адреса появятся, когда приложение откроет такой домен."
                    : null,
            });
        }

        var added = ApplyDiff(desired);
        if (added.Count > 0 && tunnelLuid is { } tunnel)
            ResetTunnelConnections(tunnel, added);

        string message = gw4 is null && gw6 is null
            ? "Не найден основной шлюз: исключения не применены."
            : $"Применено маршрутов: {desired.Count}. Основной шлюз: {Describe(gw4) ?? Describe(gw6)}.";
        SetReport(new ExclusionReport { Active = true, Message = message, Items = items, ParseErrors = parsed.Errors });
    }

    private async Task<(List<(IPAddress Address, int Length)> Networks, string? Error)> GetNetworksAsync(ExclusionEntry entry, bool resolve, CancellationToken ct)
    {
        if (entry.Kind == ExclusionKind.Network)
            return ([(entry.Address!, entry.PrefixLength)], null);

        // Адреса из журнала DNS: для масок — единственный источник, для доменов — дополнение.
        List<IPAddress> learned = _learned.TryGetValue(entry.Text, out var seen) ? [.. seen.Keys] : [];
        if (entry.Kind == ExclusionKind.Wildcard)
            return ([.. learned.Select(a => (a, a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))], null);

        string? error = null;
        IPAddress[] addresses;
        if (!resolve && _resolved.TryGetValue(entry.Host!, out var cached))
        {
            addresses = cached;
        }
        else
        {
            (addresses, error) = await ResolveAsync(entry.Host!, ct);
        }

        var all = addresses.Union(learned).ToList();
        if (all.Count == 0)
            error ??= "Домен не найден.";
        return ([.. all.Select(a => (a, a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))], error);
    }

    private async Task<(IPAddress[] Addresses, string? Error)> ResolveAsync(string host, CancellationToken ct)
    {
        string? error = null;
        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DnsTimeout);
            addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            addresses = [.. addresses.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(a))];
            _resolved[host] = addresses;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Временный сбой DNS — оставляем прошлые адреса, чтобы не дёргать маршруты.
            addresses = _resolved.GetValueOrDefault(host) ?? [];
            error = addresses.Length == 0 ? "Не удалось определить адрес (DNS)." : null;
        }
        return (addresses, error);
    }

    /// <summary>Применяет разницу с текущим состоянием. Возвращает маршруты, добавленные сейчас.</summary>
    private List<RouteKey> ApplyDiff(HashSet<RouteKey> desired)
    {
        bool changed = false;
        var added = new List<RouteKey>();
        foreach (var stale in _applied.Where(r => !desired.Contains(r)).ToList())
        {
            TryDelete(stale);
            _applied.Remove(stale);
            changed = true;
        }

        foreach (var route in desired.Where(r => !_applied.Contains(r)))
        {
            try
            {
                if (IpHelper.AddRoute(route.InterfaceLuid, route.Destination, route.PrefixLength, route.NextHop, RouteMetric))
                {
                    _applied.Add(route);
                    added.Add(route);
                    changed = true;
                    _log.Add($"Исключение: {route.Destination}/{route.PrefixLength} → {route.NextHop}");
                }
            }
            catch (IpHelperException ex)
            {
                _logger.LogWarning(ex, "Не удалось добавить маршрут {Route}", route);
                _log.Add($"Не удалось добавить исключение {route.Destination}/{route.PrefixLength}: {ex.Message}");
            }
        }

        if (changed)
            SaveState();
        return added;
    }

    /// <summary>
    /// Соединения, открытые до появления исключения, привязаны к адресу туннеля и продолжают идти через него
    /// (браузер переиспользует их при обновлении страницы). Закрываем их — приложение откроет новые, уже напрямую.
    /// Работает для TCP IPv4; UDP/QUIC закрыть нельзя, но браузеры при сбое переходят на TCP.
    /// </summary>
    private void ResetTunnelConnections(ulong tunnelLuid, List<RouteKey> added)
    {
        try
        {
            var tunnelAddresses = IpHelper.GetAddresses()
                .Where(a => a.InterfaceLuid == tunnelLuid && a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address)
                .ToHashSet();
            var networks = added.Where(r => r.Destination.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (tunnelAddresses.Count == 0 || networks.Count == 0)
                return;

            int reset = IpHelper.ResetConnections(tunnelAddresses,
                remote => networks.Any(n => ExclusionParser.Mask(remote, n.PrefixLength).Equals(n.Destination)));
            if (reset > 0)
                _log.Add($"Закрыто соединений через туннель к исключённым адресам: {reset} (откроются заново напрямую).");
        }
        catch (IpHelperException ex)
        {
            _logger.LogWarning(ex, "Не удалось закрыть соединения через туннель");
        }
    }

    private void RemoveAll()
    {
        if (_applied.Count == 0)
            return;
        foreach (var route in _applied)
            TryDelete(route);
        _log.Add($"Исключения сняты ({_applied.Count} маршрутов).");
        _applied.Clear();
        SaveState();
    }

    private void TryDelete(RouteKey route)
    {
        try
        {
            IpHelper.DeleteRoute(route.InterfaceLuid, route.Destination, route.PrefixLength, route.NextHop);
        }
        catch (IpHelperException ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить маршрут {Route}", route);
        }
    }

    /// <summary>Если служба упала с поставленными маршрутами — снимаем их при следующем запуске.</summary>
    private void CleanupLeftovers()
    {
        try
        {
            if (!File.Exists(ServicePaths.RoutesStateFile))
                return;
            var stored = JsonSerializer.Deserialize<StoredRoute[]>(File.ReadAllText(ServicePaths.RoutesStateFile), IpcProtocol.JsonOptions) ?? [];
            foreach (var s in stored)
            {
                if (IPAddress.TryParse(s.Destination, out var dest) && IPAddress.TryParse(s.NextHop, out var hop))
                    TryDelete(new RouteKey(s.InterfaceLuid, dest, s.PrefixLength, hop));
            }
            File.Delete(ServicePaths.RoutesStateFile);
            if (stored.Length > 0)
                _logger.LogInformation("Сняты оставшиеся после сбоя маршруты: {Count}", stored.Length);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать файл состояния маршрутов");
        }
    }

    private void SaveState()
    {
        try
        {
            var stored = _applied.Select(r => new StoredRoute(r.InterfaceLuid, r.Destination.ToString(), r.PrefixLength, r.NextHop.ToString())).ToArray();
            File.WriteAllText(ServicePaths.RoutesStateFile, JsonSerializer.Serialize(stored, IpcProtocol.JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Не удалось сохранить состояние маршрутов");
        }
    }

    private void SetReport(ExclusionReport report)
    {
        lock (_reportLock)
            _report = report;
        ReportChanged?.Invoke(report);
    }

    private static string? Describe(PhysicalGateway? gw) =>
        gw is null ? null : $"{gw.InterfaceName} ({gw.NextHop})";

    public override void Dispose()
    {
        _networkDebounce?.Cancel();
        base.Dispose();
    }

    private sealed record RouteKey(ulong InterfaceLuid, IPAddress Destination, int PrefixLength, IPAddress NextHop);

    private sealed record StoredRoute(ulong InterfaceLuid, string Destination, int PrefixLength, string NextHop);
}
