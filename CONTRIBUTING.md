# Участие и сборка

Предложения и исправления — через Issues и Pull Requests.

- Ветка `main` защищена: изменения попадают в неё только через PR, и только когда CI (сборка и все тесты) прошёл.
- Сборки CI для PR от новых участников запускаются после одобрения владельцем репозитория.
- Dependabot раз в неделю предлагает обновления зависимостей в виде PR и сообщает об уязвимостях в них. Ничего не сливается автоматически. WiX остаётся на версии 5.x: начиная с WiX 6 действует другая лицензия.

## Состав репозитория

| Путь | Назначение |
|---|---|
| `src/O2C.Core` | Модели, протокол IPC, разбор вывода openconnect и списка исключений |
| `src/O2C.Service` | Служба Windows |
| `src/O2C.Network` | Работа с сетью Windows: маршруты, адреса, DNS, выбор основного шлюза, настройка туннеля |
| `src/O2C.NetConfig` | Помощник настройки сети, его запускает openconnect |
| `src/O2C.App` | Окно (WPF) |
| `tests/` | Модульные тесты и тесты на настоящей сети Windows: маршруты, временный адаптер Wintun, журнал DNS (с правами администратора — в CI) |
| `installer/` | Установщик MSI (WiX 5) |
| `build/build-openconnect.sh` | Сборка `openconnect.exe` 9.21 из исходников (MSYS2) |
| `build/patches/` | Патчи к OpenConnect (LGPL-2.1, как и сам OpenConnect) |
| `build/fetch-runtime.ps1` | Загрузка `wintun.dll` с проверкой SHA-256 |
| `build/publish.ps1` | Публикация и сборка MSI в `artifacts/` |
| `build/ru-networks.py` | Список российских подсетей из статистики RIPE NCC (запись `@ru`) |
| `.github/workflows/ru-networks.yml` | Еженедельное обновление `ru-networks.txt` в последнем релизе |
| `docs/add/index.html` | Страница GitHub Pages для ссылок на списки исключений (формат кода — `ShareCode.cs`) |

## Сборка

Нужен .NET SDK 10. Для `publish.ps1` ещё нужен Python 3: он собирает список российских подсетей (`build/ru-networks.py`, скачивает около 18 МБ статистики RIPE NCC и сверяет MD5).

```powershell
dotnet build
dotnet test
./build/publish.ps1 -NoInstaller    # только .NET-часть в artifacts/publish
```

Полный MSI собирает CI (GitHub Actions, workflow `build`), результат — в артефакте `OpenToConnect-msi`. Для локальной сборки MSI нужна папка с результатом `build-openconnect.sh`: `./build/publish.ps1 -OpenConnectDir <папка>`.

**openconnect.exe.** Готовых консольных сборок OpenConnect под Windows не публикуется, поэтому CI собирает 9.21 из исходного архива (SHA-256 проверяется) в MSYS2 UCRT64. Скрипт сборки:

- заменяет заголовок `sec_api/stdlib_s.h`, которого нет в новом MinGW, на `stdlib.h`;
- применяет патчи из `build/patches`: запуск помощника `.exe` напрямую, без `cscript.exe`, и создание нового адаптера Wintun, если в реестре осталась запись удалённого;
- записывает версии пакетов MSYS2 и копирует их лицензии (`PACKAGES.txt`, `licenses/`).

**Релиз** создаётся по тегу: поднять `<Version>` в `Directory.Build.props`, добавить раздел версии в [CHANGELOG.md](CHANGELOG.md) (он станет текстом релиза), слить в `main` и отправить тег `vX.Y.Z`. CI проверит совпадение версии, соберёт MSI и опубликует релиз с SHA-256 и файлом `ru-networks.txt`.
