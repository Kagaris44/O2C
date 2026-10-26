"""Список подсетей, зарегистрированных в России, для записи «Российские сети» (@ru).

Источник — публичная статистика распределения адресов RIPE NCC (delegated-ripencc-extended-latest).
Скрипт скачивает её, сверяет MD5 с опубликованным RIPE, берёт записи страны RU (allocated/assigned),
сливает соседние подсети и пишет по одной «адрес/длина» на строку.

Использование: python build/ru-networks.py <выходной файл> [локальная копия статистики]
"""
import hashlib
import ipaddress
import sys
import urllib.request

SOURCE = 'https://ftp.ripe.net/pub/stats/ripencc/delegated-ripencc-extended-latest'
MIN_V4 = 3000      # защита от обрезанного или пустого ответа
MIN_V6 = 500


def fetch(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'OpenToConnect-build'})
    with urllib.request.urlopen(request, timeout=300) as response:
        return response.read()


def main():
    out = sys.argv[1]
    if len(sys.argv) > 2:
        data = open(sys.argv[2], 'rb').read()
    else:
        data = fetch(SOURCE)
        expected = fetch(SOURCE + '.md5').decode('ascii').split()[-1].strip().lower()
        actual = hashlib.md5(data).hexdigest()
        if actual != expected:
            sys.exit(f'MD5 не совпадает: {actual} != {expected}')

    serial = ''
    v4, v6 = [], []
    for line in data.decode('ascii', errors='replace').splitlines():
        if line.startswith('#'):
            continue
        p = line.split('|')
        if len(p) >= 3 and p[1] == 'ripencc' and p[0].isdigit() and not serial:
            # Заголовок: версия|ripencc|серия|записей|начало|конец (ГГГГММДД)|часовой пояс.
            serial = p[5] if len(p) > 5 and p[5].isdigit() else p[2]
            continue
        if len(p) < 7 or p[1] != 'RU' or p[6] not in ('allocated', 'assigned'):
            continue
        if p[2] == 'ipv4':
            start = ipaddress.IPv4Address(p[3])
            v4.extend(ipaddress.summarize_address_range(start, start + int(p[4]) - 1))
        elif p[2] == 'ipv6':
            v6.append(ipaddress.IPv6Network(f'{p[3]}/{p[4]}'))

    v4 = list(ipaddress.collapse_addresses(v4))
    v6 = list(ipaddress.collapse_addresses(v6))
    if len(v4) < MIN_V4 or len(v6) < MIN_V6:
        sys.exit(f'Подозрительно мало подсетей: IPv4 {len(v4)}, IPv6 {len(v6)}')

    with open(out, 'w', encoding='ascii', newline='\n') as f:
        f.write('# OpenToConnect: networks registered in Russia (RU)\n')
        f.write('# source: RIPE NCC delegated statistics, ' + SOURCE + '\n')
        f.write(f'# date: {serial}\n')
        f.write(f'# ipv4: {len(v4)}, ipv6: {len(v6)}\n')
        for n in v4 + v6:
            f.write(f'{n}\n')
    print(f'{out}: date {serial}, IPv4 {len(v4)}, IPv6 {len(v6)}')


if __name__ == '__main__':
    main()
