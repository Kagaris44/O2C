using System.Net;
using System.Net.Sockets;
using O2C.Core.NetConfig;

namespace O2C.Network;

/// <summary>Маршрут в обход туннеля, поставленный конфигуратором (нужен, чтобы снять его при отключении).</summary>
public sealed record BypassRoute(ulong InterfaceLuid, string Destination, int PrefixLength, string NextHop);

/// <summary>Что конфигуратор поставил вне адаптера туннеля. Хранится между вызовами (connect → disconnect).</summary>
public sealed record TunnelState
{
    public ulong TunnelLuid { get; init; }
    public IReadOnlyList<BypassRoute> BypassRoutes { get; init; } = [];
}

/// <summary>
/// Настройка сети туннеля через Windows API — замена vpnc-script-win.js (Windows Script Host + netsh/route).
/// Порядок: очистить адаптер, задать MTU и метрику, адреса, маршруты, DNS.
/// </summary>
public sealed class TunnelConfigurator(Action<string> log)
{
    /// <summary>Метрика интерфейса при полном туннеле: DNS-серверы туннеля опрашиваются первыми.</summary>
    private const uint FullTunnelInterfaceMetric = 1;

    public TunnelState Connect(TunnelPlan plan, TunnelState? previous)
    {
        if (plan.TunnelIndex is not { } index)
            throw new InvalidOperationException("openconnect не передал индекс адаптера (TUNIDX).");
        var luid = IpHelper.LuidFromIndex(index);

        RemoveBypassRoutes(previous);

        // Чистый лист: адаптер мог остаться настроенным с прошлого подключения.
        IpHelper.FlushRoutes(luid);
        IpHelper.FlushAddresses(luid);

        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            bool full = family == AddressFamily.InterNetwork ? plan.FullTunnel4 : plan.FullTunnel6;
            // IPv6 не допускает MTU меньше 1280.
            int? mtu = family == AddressFamily.InterNetworkV6 && plan.Mtu < 1280 ? null : plan.Mtu;
            if (IpHelper.SetInterfaceParameters(luid, family, mtu, full ? FullTunnelInterfaceMetric : null))
                log($"{Name(family)}: MTU {mtu?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "без изменений"}, метрика {(full ? FullTunnelInterfaceMetric.ToString(System.Globalization.CultureInfo.InvariantCulture) : "автоматическая")}");
        }

        foreach (var address in new[] { plan.Address4, plan.Address6 })
        {
            if (address is { } a)
            {
                IpHelper.AddAddress(luid, a.Address, a.PrefixLength);
                log($"Адрес туннеля: {a}");
            }
        }

        foreach (var route in plan.TunnelRoutes)
        {
            var onLink = route.Family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
            IpHelper.AddRoute(luid, route.Address, route.PrefixLength, onLink, 0);
        }
        log(plan.FullTunnel4
            ? "Весь трафик IPv4 — через туннель."
            : $"Маршрутов в туннель: {plan.TunnelRoutes.Count} ({string.Join(", ", plan.TunnelRoutes.Take(5))}{(plan.TunnelRoutes.Count > 5 ? ", ..." : "")}).");

        var bypass = AddBypassRoutes(plan, index);
        if (plan.UseServerDns)
            ApplyServerDns(plan, luid);
        else
            bypass.AddRange(UseLocalDns(luid, index));
        IpHelper.FlushDnsCache();

        return new TunnelState { TunnelLuid = luid, BypassRoutes = bypass };
    }

    /// <summary>
    /// Перед повторной попыткой соединения: основной шлюз мог смениться (другая сеть),
    /// поэтому маршрут до сервера и исключения сервера ставим заново.
    /// </summary>
    public TunnelState RefreshBypass(TunnelPlan plan, TunnelState? previous)
    {
        RemoveBypassRoutes(previous);
        if (plan.TunnelIndex is not { } index)
            return previous ?? new TunnelState();
        var luid = previous?.TunnelLuid ?? IpHelper.LuidFromIndex(index);
        var bypass = AddBypassRoutes(plan, index);
        if (!plan.UseServerDns)
            bypass.AddRange(UseLocalDns(luid, index));
        return new TunnelState { TunnelLuid = luid, BypassRoutes = bypass };
    }

    public void Disconnect(TunnelPlan plan, TunnelState? previous)
    {
        RemoveBypassRoutes(previous);

        ulong? luid = previous?.TunnelLuid is > 0 and var l ? l : plan.TunnelIndex is { } i ? IpHelper.LuidFromIndex(i) : null;
        if (luid is not { } tunnel)
            return;

        // Адаптер исчезнет вместе с openconnect, но DNS-настройки и маршруты лучше снять сразу.
        TryStep("очистка DNS", () =>
        {
            IpHelper.SetDns(tunnel, AddressFamily.InterNetwork, [], []);
            IpHelper.SetDns(tunnel, AddressFamily.InterNetworkV6, [], []);
        });
        TryStep("удаление маршрутов туннеля", () => IpHelper.FlushRoutes(tunnel));
        TryStep("удаление адресов туннеля", () => IpHelper.FlushAddresses(tunnel));
        IpHelper.FlushDnsCache();
        log("Сеть туннеля очищена.");
    }

    private List<BypassRoute> AddBypassRoutes(TunnelPlan plan, uint tunnelIndex)
    {
        var routes = IpHelper.GetRoutes();
        var interfaces = GatewaySelector.SnapshotInterfaces();
        PhysicalGateway? Gateway(AddressFamily family) => GatewaySelector.Find(routes, family, tunnelIndex,
            i => interfaces.GetValueOrDefault(i), l => IpHelper.GetInterfaceMetric(l, family));
        var gw4 = Gateway(AddressFamily.InterNetwork);
        var gw6 = Gateway(AddressFamily.InterNetworkV6);

        var wanted = new List<IpNetwork>();
        if (plan.Gateway is { } server)
            wanted.Add(new IpNetwork(server, server.AddressFamily == AddressFamily.InterNetwork ? 32 : 128));
        wanted.AddRange(plan.BypassRoutes);

        var added = new List<BypassRoute>();
        foreach (var net in wanted)
        {
            var gw = net.Family == AddressFamily.InterNetwork ? gw4 : gw6;
            if (gw is null)
            {
                log($"Нет основного шлюза для {net} — маршрут в обход туннеля не поставлен.");
                continue;
            }
            // Уже существующий маршрут (например, исключение пользователя) не считаем своим.
            if (IpHelper.AddRoute(gw.InterfaceLuid, net.Address, net.PrefixLength, gw.NextHop, 1))
                added.Add(new BypassRoute(gw.InterfaceLuid, net.Address.ToString(), net.PrefixLength, gw.NextHop.ToString()));
        }

        if (plan.Gateway is { } g)
            log($"Сервер {g} — через {Describe(g.AddressFamily == AddressFamily.InterNetwork ? gw4 : gw6)}.");
        if (plan.BypassRoutes.Count > 0)
            log($"Исключений от сервера: {plan.BypassRoutes.Count}.");
        return added;
    }

    private void RemoveBypassRoutes(TunnelState? state)
    {
        foreach (var r in state?.BypassRoutes ?? [])
        {
            TryStep($"удаление маршрута {r.Destination}/{r.PrefixLength}", () =>
                IpHelper.DeleteRoute(r.InterfaceLuid, IPAddress.Parse(r.Destination), r.PrefixLength, IPAddress.Parse(r.NextHop)));
        }
    }

    /// <summary>
    /// DNS компьютера: туннелю DNS не назначаем, и Windows спрашивает серверы основного адаптера.
    /// Если такой сервер оказался бы за туннелем (например, публичный DNS при полном туннеле),
    /// ставим до него маршрут напрямую — DNS работает так же, как без подключения.
    /// </summary>
    private List<BypassRoute> UseLocalDns(ulong tunnelLuid, uint tunnelIndex)
    {
        TryStep("очистка DNS туннеля", () =>
        {
            IpHelper.SetDns(tunnelLuid, AddressFamily.InterNetwork, [], []);
            IpHelper.SetDns(tunnelLuid, AddressFamily.InterNetworkV6, [], []);
        });

        var routes = IpHelper.GetRoutes();
        var interfaces = GatewaySelector.SnapshotInterfaces();
        var added = new List<BypassRoute>();
        var described = new List<string>();
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            var gw = GatewaySelector.Find(routes, family, tunnelIndex,
                i => interfaces.GetValueOrDefault(i), l => IpHelper.GetInterfaceMetric(l, family));
            if (gw is null)
                continue;
            foreach (var dns in RouteLookup.DnsServersOf(gw.InterfaceIndex).Where(a => a.AddressFamily == family))
            {
                var best = RouteLookup.Best(routes, dns);
                if (best is not null && best.InterfaceLuid == tunnelLuid)
                {
                    int len = family == AddressFamily.InterNetwork ? 32 : 128;
                    if (IpHelper.AddRoute(gw.InterfaceLuid, dns, len, gw.NextHop, 1))
                        added.Add(new BypassRoute(gw.InterfaceLuid, dns.ToString(), len, gw.NextHop.ToString()));
                    described.Add($"{dns} (напрямую)");
                }
                else
                {
                    described.Add(dns.ToString());
                }
            }
            if (described.Count > 0 && family == AddressFamily.InterNetwork)
                log($"DNS: этого компьютера — {string.Join(", ", described)} через {gw.InterfaceName}.");
        }
        if (described.Count == 0)
            log("DNS: этого компьютера (серверы основного адаптера не найдены — Windows выберет сама).");
        return added;
    }

    /// <summary>DNS сервера: назначаем туннелю, запросы идут через туннель (метрика туннеля ниже).</summary>
    private void ApplyServerDns(TunnelPlan plan, ulong luid)
    {
        if (plan.Dns4.Count == 0 && plan.Dns6.Count == 0)
        {
            log("Сервер не прислал DNS-серверов.");
            return;
        }
        // Ошибка DNS не должна рвать подключение: адреса и маршруты уже работают.
        TryStep("настройка DNS IPv4", () => IpHelper.SetDns(luid, AddressFamily.InterNetwork, plan.Dns4, plan.SearchDomains));
        if (plan.Dns6.Count > 0 || plan.Address6 is not null)
            TryStep("настройка DNS IPv6", () => IpHelper.SetDns(luid, AddressFamily.InterNetworkV6, plan.Dns6, plan.SearchDomains));
        log($"DNS: {string.Join(", ", plan.Dns4.Concat(plan.Dns6))}" +
            (plan.SearchDomains.Count > 0 ? $"; домены поиска: {string.Join(", ", plan.SearchDomains)}" : ""));
    }

    private void TryStep(string what, Action action)
    {
        try
        {
            action();
        }
        catch (IpHelperException ex)
        {
            log($"Предупреждение ({what}): {ex.Message}");
        }
    }

    private static string Name(AddressFamily family) => family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";

    private static string Describe(PhysicalGateway? gw) => gw is null ? "—" : $"{gw.InterfaceName} ({gw.NextHop})";
}
