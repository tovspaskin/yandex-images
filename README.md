# Яндекс Фото

Приложение на C# и Avalonia для скачивания оригиналов изображений из публичных папок Яндекс Диска. Сборки автономные: устанавливать .NET пользователю не нужно.

[Скачать](https://github.com/tovspaskin/yandex-images/releases/latest) · [Автосборки](https://github.com/tovspaskin/yandex-images/actions) · [Исходники](https://github.com/tovspaskin/yandex-images)

## Запуск

| Система | Архив | Запуск после распаковки |
| --- | --- | --- |
| Windows x64 | `YandexImages-win-x64.zip` | `YandexImages.exe` |
| Linux x64 | `YandexImages-linux-x64.tar.gz` | `./YandexImages` |
| macOS Intel | `YandexImages-osx-x64.zip` | `YandexImages.app` |
| macOS Apple Silicon | `YandexImages-osx-arm64.zip` | `YandexImages.app` |

Готовые архивы доступны в [Releases](https://github.com/tovspaskin/yandex-images/releases). Они также появляются в **Actions → Build desktop apps → Artifacts** после успешного запуска CI. При отправке тега `v*` workflow создаёт GitHub Release с архивами и SHA-256. CI собирает приложение, запускает проверки загрузчика и проверяет старт упакованного приложения на Windows, Linux и обеих архитектурах macOS.

macOS-пакет из CI имеет локальную подпись **ad-hoc**, без Developer ID и нотариализации Apple. Gatekeeper может потребовать разрешить запуск через настройки «Конфиденциальность и безопасность». При упаковке macOS на другой системе подпись не создаётся.

Linux-сборке нужен рабочий стол X11 или XWayland и системные библиотеки. Для Ubuntu/Debian:

```bash
sudo apt install libx11-6 libice6 libsm6 libfontconfig1
tar -xzf YandexImages-linux-x64.tar.gz
cd YandexImages
./YandexImages
```

В минимальных системах также нужны штатные зависимости .NET: glibc, ICU, OpenSSL и сертификаты CA. Сборка `linux-x64` предназначена для glibc, а не Alpine/musl. [Требования Avalonia](https://docs.avaloniaui.net/docs/supported-platforms), [зависимости .NET для Linux](https://learn.microsoft.com/dotnet/core/install/linux).

## Использование

1. Вставьте публичную ссылку на папку.
2. Выберите папку сохранения.
3. При желании нажмите «Проверить папку», чтобы увидеть список и размер.
4. Нажмите «Скачать изображения».

Поддерживаются ссылки `disk.yandex.ru/d/…`, `disk.360.yandex.ru/d/…`, `disk.yandex.com/d/…`, `yadi.sk/d/…` и публичные ссылки с вложенным путём вида `/client/aa/d_…/подпапка`. Ссылки на отдельные публичные изображения также поддерживаются. Формат `/client/aa/` преобразуется в публичную ссылку; доступность проверяется через API.

«Включая вложенные папки» сохраняет структуру каталогов. Можно выбрать от 2 до 8 одновременных загрузок. «Остановить» сохраняет готовые файлы; незавершённый файл при следующем запуске загружается заново. Совпадающие файлы проверяются и пропускаются. Файлы с другим содержимым не перезаписываются: новая копия получает свободное имя. Недопустимые для Windows символы заменяются и на других ОС, чтобы структура оставалась переносимой. Формат и качество оригинала сохраняются.

Нужна публичная ссылка без пароля с разрешённым скачиванием. Личные ссылки `/client/disk/…` сначала замените ссылкой из меню «Поделиться». Приложение использует [официальный REST API Яндекс Диска](https://yandex.ru/dev/disk-api/doc/ru/reference/public), без входа в аккаунт.

## Сборка из исходников

Нужен .NET SDK **9.0.310** либо более новый патч той же ветки `9.0.3xx` — настройки находятся в `global.json`. Решение: `YandexImages.sln`; приложение — `App`, загрузчик — `Core`, автономные проверки — `Tests`, необязательная проверка публичной ссылки — `Verification`.

```powershell
git clone https://github.com/tovspaskin/yandex-images.git
cd yandex-images
dotnet build YandexImages.sln -c Release
dotnet run --project Tests/YandexImages.Tests.csproj -c Release
```

Для Windows доступен обычный PowerShell, без Python:

```powershell
.\build.ps1
```

Готовый EXE будет в `..\artifacts\YandexImages\YandexImages.exe` — этот путь сохранён для совместимости с предыдущей Windows-сборкой.

Общая публикация и упаковка требует PowerShell 7.2+ и Python 3.11+ (только стандартная библиотека):

```powershell
pwsh -File scripts/publish.ps1 -Runtime win-x64
pwsh -File scripts/publish.ps1 -Runtime linux-x64
pwsh -File scripts/publish.ps1 -Runtime osx-x64
pwsh -File scripts/publish.ps1 -Runtime osx-arm64
```

Результат: `artifacts/publish/<RID>`, пакеты: `artifacts/packages`. `-NoArchive` пропускает упаковку и не требует Python. Для нативной проверки запуска выполните `pwsh -File scripts/smoke.ps1 -Runtime <RID>` на целевой ОС после упаковки; Linux дополнительно требует `xvfb`. macOS следует упаковывать на Mac, чтобы создать и проверить подпись ad-hoc.

Workflow `.github/workflows/build.yml` собирает четыре RID, запускает тесты и проверку старта упакованного приложения на Windows, Linux и обеих архитектурах macOS. Используемые официальные Actions закреплены полными SHA коммитов.

## Git и GitHub

Исходники находятся в [tovspaskin/yandex-images](https://github.com/tovspaskin/yandex-images). Результаты сборок и временные файлы исключены через `.gitignore`. Для нового выпуска после проверки и обновления версии приложения:

```bash
git tag v1.1.0
git push origin v1.1.0
```
