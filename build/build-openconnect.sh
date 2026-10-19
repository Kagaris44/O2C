#!/usr/bin/env bash
# Сборка openconnect.exe из исходников в среде MSYS2 UCRT64.
# Готовых сборок CLI под Windows больше не публикуют, поэтому собираем сами из подписанного релиза.
#
# Использование (в оболочке MSYS2 UCRT64):  build/build-openconnect.sh <выходная папка>
# Зависимости: pacboy -S gcc:p pkgconf:p gnutls:p libxml2:p zlib:p lz4:p ; pacman -S make
set -euo pipefail

OC_VERSION=9.21
OC_SHA256=5b32369467db6e5f317aa1ed12cfcbb81ed00bdbc765450b6bfcbdc300944a58

OUT="$(realpath -m "${1:?usage: build-openconnect.sh <output dir>}")"
# Путь к патчам — до смены каталога: BASH_SOURCE может быть относительным.
PATCH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/patches"
# Канонические тексты GNU-лицензий — для пакетов MSYS2, в которых нет своего файла лицензии.
GNU_LICENSES="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/licenses/gnu"
# Файлы лицензий из исходников проекта (тех же версий) — для не-GNU пакетов без своего файла.
UPSTREAM_LICENSES="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/licenses/upstream"

# Лицензии GNU-библиотек (по их исходникам), если пакет MSYS2 не содержит файла лицензии.
declare -A GNU_FALLBACK=(
    [gmp]="LGPL-3.0 GPL-2.0 GPL-3.0"
    [nettle]="LGPL-3.0 GPL-2.0 GPL-3.0"
    [libidn2]="LGPL-3.0 GPL-2.0 GPL-3.0"
    [libunistring]="LGPL-3.0 GPL-2.0 GPL-3.0"
    [gnutls]="LGPL-2.1"
    [libtasn1]="LGPL-2.1"
    [libiconv]="LGPL-2.1"
    [gettext-runtime]="LGPL-2.1"
)
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cd "$WORK"
curl -fsSL -o oc.tar.gz "https://www.infradead.org/openconnect/download/openconnect-${OC_VERSION}.tar.gz"
echo "${OC_SHA256}  oc.tar.gz" | sha256sum -c -
tar xzf oc.tar.gz
cd "openconnect-${OC_VERSION}"

# В mingw-w64 v12+ заголовка sec_api/stdlib_s.h больше нет: getenv_s/_putenv_s объявлены в stdlib.h.
# openconnect 9.21 подключает его в compat.c и в проверках configure — меняем на stdlib.h.
sed -i 's|<sec_api/stdlib_s.h>|<stdlib.h>|g' compat.c configure
if grep -q 'sec_api' compat.c configure; then
    echo "patch sec_api failed" >&2
    exit 1
fi

# Патчи OpenToConnect (см. build/patches): помощник настройки сети (.exe) запускается напрямую,
# без Windows Script Host.
for p in "$PATCH_DIR"/*.patch; do
    echo "applying $(basename "$p")"
    patch -p1 --forward < "$p"
done

# Makefile под Windows сам скачивает vpnc-script-win.js с ветки master (нужен jq, версия не зафиксирована).
# Кладём заранее зафиксированную и проверенную версию — и make её не трогает.
VPNC_COMMIT=6cdb2fe2d75be324564fe27ff85a5e1bb17a66b0
VPNC_SHA256=e196fdf0cc8b325180154535f843034dc0ae9eb43ead9980adfcfc57c58069b2
curl -fsSL -o vpnc-script-win.js "https://gitlab.com/openconnect/vpnc-scripts/-/raw/${VPNC_COMMIT}/vpnc-script-win.js"
echo "${VPNC_SHA256}  vpnc-script-win.js" | sha256sum -c -

# libopenconnect собираем статически внутрь exe: меньше файлов и нет конфликта с чужими libopenconnect-5.dll.
# vpnc-script-win.js нужен только самому Makefile (цель sbin_SCRIPTS), в продукт он не попадает.
# Скрипт по умолчанию — наш помощник на уровень выше папки openconnect. Служба всё равно передаёт путь явно.
./configure \
    --disable-shared --enable-static \
    --with-vpnc-script=../OpenToConnect.NetConfig.exe \
    --with-gnutls \
    --disable-nls --disable-docs \
    --without-libproxy --without-stoken --without-libpcsclite --without-libpskc --without-gssapi

make -j"$(nproc)"

mkdir -p "$OUT"
cp openconnect.exe "$OUT/"

# Все DLL из UCRT64, от которых зависит exe (системные DLL Windows не копируем).
ldd openconnect.exe | awk '$3 ~ /^\/ucrt64\// { print $3 }' | sort -u | while read -r dll; do
    cp "$dll" "$OUT/"
done

# Лицензии. openconnect — LGPL-2.1 (с патчами из build/patches под той же лицензией).
cp COPYING.LGPL "$OUT/openconnect-COPYING.LGPL"
echo "openconnect ${OC_VERSION} + build/patches" > "$OUT/VERSION.txt"

# Для каждой DLL — пакет MSYS2 и его версия (по ним находятся точные исходники:
# https://repo.msys2.org/mingw/sources/) и файлы лицензии из самого пакета.
mkdir -p "$OUT/licenses"
: > "$OUT/PACKAGES.txt"
for dll in "$OUT"/*.dll; do
    pkg="$(pacman -Qqo "/ucrt64/bin/$(basename "$dll")")"
    echo "$(basename "$dll") $(pacman -Q "$pkg")" >> "$OUT/PACKAGES.txt"
    # Файлы лицензии пакета: всё из share/licenses и файлы с «лицензионными» именами из share/doc.
    lics="$( { pacman -Qlq "$pkg" | grep -E '/share/licenses/.+[^/]$' || true;
               pacman -Qlq "$pkg" | grep -E '/share/doc/.+[^/]$' | grep -iE '/(licen[cs]e|copying|copyright|notice)[^/]*$' || true; } )"
    mkdir -p "$OUT/licenses/$pkg"
    if [ -n "$lics" ]; then
        echo "$lics" | while read -r lic; do
            cp "$lic" "$OUT/licenses/$pkg/"
        done
        continue
    fi
    short="${pkg#mingw-w64-ucrt-x86_64-}"
    if [ -d "$UPSTREAM_LICENSES/$short" ]; then
        cp "$UPSTREAM_LICENSES/$short"/* "$OUT/licenses/$pkg/"
        echo "$pkg: файла лицензии в пакете нет — приложен файл из исходников проекта"
    elif [ -n "${GNU_FALLBACK[$short]:-}" ]; then
        for id in ${GNU_FALLBACK[$short]}; do
            cp "$GNU_LICENSES/$id.txt" "$OUT/licenses/$pkg/"
        done
        echo "$pkg: файла лицензии в пакете нет — приложены тексты ${GNU_FALLBACK[$short]}"
    else
        echo "нет файлов лицензии в пакете $pkg" >&2
        MISSING="${MISSING:-} $pkg"
    fi
done
if [ -n "${MISSING:-}" ]; then
    echo "Пакеты без лицензий:${MISSING}" >&2
    exit 1
fi
sort -o "$OUT/PACKAGES.txt" "$OUT/PACKAGES.txt"
cat "$OUT/PACKAGES.txt"


"$OUT/openconnect.exe" --version
ls -la "$OUT"
