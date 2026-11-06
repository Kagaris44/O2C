"""Список подсетей автономной системы для набора сетей компании (например, @blizzard — AS57976).

Источник — RIPEstat (announced-prefixes): подсети, которые автономная система анонсирует в интернете
по данным коллекторов маршрутов RIPE RIS. Скрипт сливает соседние подсети и пишет по одной
«адрес/длина» на строку.

Использование: python build/as-networks.py <AS> <выходной файл> [минимум подсетей IPv4]
"""
import datetime
import ipaddress
import json
import sys
import urllib.request

SOURCE = 'https://stat.ripe.net/data/announced-prefixes/data.json?resource=AS{asn}&sourceapp=OpenToConnect-build'


def main():
    asn = sys.argv[1].upper().removeprefix('AS')
    out = sys.argv[2]
    min_v4 = int(sys.argv[3]) if len(sys.argv) > 3 else 20
    if not asn.isdigit():
        sys.exit(f'Неверный номер AS: {sys.argv[1]}')

    url = SOURCE.format(asn=asn)
    request = urllib.request.Request(url, headers={'User-Agent': 'OpenToConnect-build'})
    with urllib.request.urlopen(request, timeout=120) as response:
        data = json.load(response)
    if data.get('status') != 'ok':
        sys.exit(f'RIPEstat ответил: {data.get("status")} {data.get("messages")}')

    v4, v6 = [], []
    for item in data['data']['prefixes']:
        network = ipaddress.ip_network(item['prefix'], strict=False)
        # Слишком широкие анонсы (меньше /8 и /19 для IPv6) — ошибка данных, а не сети компании.
        if network.version == 4 and network.prefixlen >= 8:
            v4.append(network)
        elif network.version == 6 and network.prefixlen >= 19:
            v6.append(network)

    v4 = list(ipaddress.collapse_addresses(v4))
    v6 = list(ipaddress.collapse_addresses(v6))
    if len(v4) < min_v4:
        sys.exit(f'Подозрительно мало подсетей IPv4: {len(v4)}')

    date = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d')
    with open(out, 'w', encoding='ascii', newline='\n') as f:
        f.write(f'# OpenToConnect: networks announced by AS{asn}\n')
        f.write('# source: RIPEstat announced-prefixes (RIPE RIS), https://stat.ripe.net/\n')
        f.write(f'# date: {date}\n')
        f.write(f'# ipv4: {len(v4)}, ipv6: {len(v6)}\n')
        for n in v4 + v6:
            f.write(f'{n}\n')
    print(f'{out}: AS{asn}, date {date}, IPv4 {len(v4)}, IPv6 {len(v6)}')


if __name__ == '__main__':
    main()
