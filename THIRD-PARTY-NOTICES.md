# Сторонние компоненты

OpenToConnect распространяется под лицензией Apache-2.0 (файл `LICENSE`). Установщик также содержит компоненты других авторов. Они распространяются под своими лицензиями, перечисленными ниже. Полные тексты лицензий установлены вместе с программой:

- `C:\Program Files\OpenToConnect\licenses\` — .NET, WiX, этот файл;
- `C:\Program Files\OpenToConnect\openconnect\` — OpenConnect (`openconnect-COPYING.LGPL`) и Wintun (`wintun-LICENSE.txt`);
- `C:\Program Files\OpenToConnect\openconnect\licenses\<пакет>\` — лицензии библиотек, взятые из пакетов MSYS2. Если пакет MSYS2 не содержит файла лицензии (так бывает у библиотек GNU), приложен канонический текст его лицензии с gnu.org (папка [`licenses/gnu`](licenses/gnu) в репозитории), а для остальных — файл лицензии из исходников проекта той же версии ([`licenses/upstream`](licenses/upstream)).

Точные версии библиотек перечислены в `openconnect\PACKAGES.txt`.

## OpenConnect и его библиотеки

`openconnect.exe` — консольный клиент [OpenConnect](https://www.infradead.org/openconnect/) 9.21. Он собран из исходного архива проекта с патчами из [`build/patches`](build/patches) скриптом [`build/build-openconnect.sh`](build/build-openconnect.sh). OpenToConnect запускает его как отдельный процесс и не компонуется с ним.

| Компонент | Лицензия |
|---|---|
| OpenConnect 9.21 и патчи `build/patches` | LGPL-2.1-or-later |
| GnuTLS | LGPL-2.1-or-later |
| Nettle (libnettle, libhogweed) | LGPL-3.0-or-later или GPL-2.0-or-later |
| GMP | LGPL-3.0-or-later или GPL-2.0-or-later |
| libtasn1 | LGPL-2.1-or-later |
| p11-kit | BSD-3-Clause (© Red Hat, Inc. и участники) |
| libidn2 | LGPL-3.0-or-later или GPL-2.0-or-later |
| libunistring | LGPL-3.0-or-later или GPL-2.0-or-later |
| libiconv | LGPL-2.1-or-later |
| gettext runtime (libintl) | LGPL-2.1-or-later |
| libxml2 | MIT |
| zlib | Zlib |
| LZ4 | BSD-2-Clause (© Yann Collet) |
| Zstandard | BSD-3-Clause |
| Brotli | MIT |
| libffi | MIT |
| mingw-w64 winpthreads | MIT |

**Исходные тексты.** Исходники OpenConnect — архив `openconnect-9.21.tar.gz` с сайта проекта (его SHA-256 указан в скрипте сборки) и патчи в этом репозитории. Библиотеки взяты из пакетов [MSYS2](https://www.msys2.org/) UCRT64 в версиях из `openconnect\PACKAGES.txt`. Исходники этих версий доступны в [repo.msys2.org/mingw/sources](https://repo.msys2.org/mingw/sources/), рецепты сборки — в [github.com/msys2/MINGW-packages](https://github.com/msys2/MINGW-packages).

Библиотеки подключаются как отдельные DLL в папке `openconnect`, и их можно заменить своими сборками совместимых версий.

## Wintun

`wintun.dll` 0.14.1 — драйвер виртуального сетевого адаптера с [wintun.net](https://www.wintun.net/). Распространяется без изменений по лицензии «Prebuilt Binaries License» (текст — `openconnect\wintun-LICENSE.txt`). Используется только через свой программный интерфейс (им пользуется `openconnect.exe`).

## .NET

Среда выполнения .NET 10, WPF, Windows Forms, `Microsoft.Extensions.*`, `System.Security.Cryptography.ProtectedData` — © .NET Foundation и участники, лицензия MIT. Тексты: `licenses\dotnet-LICENSE.txt` и уведомления о сторонних компонентах .NET `licenses\dotnet-ThirdPartyNotices.txt`.

## WiX Toolset

Установщик собран WiX Toolset 5. Вспомогательные модули WiX внутри MSI (настройка службы, закрытие окна перед обновлением) — © .NET Foundation и участники, лицензия Microsoft Reciprocal License (MS-RL), текст — `licenses\WiX-MS-RL.txt`.

## Только для разработки

Не входят в установщик: xUnit (Apache-2.0) и Microsoft.NET.Test.Sdk (MIT) — для тестов; WiX Toolset — для сборки MSI; MSYS2 — для сборки OpenConnect.
