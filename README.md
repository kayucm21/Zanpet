<div align="center">

<img src="Assets/app-256.png" width="96" alt="Zapret UI"/>

# Zapret UI

**Графическая оболочка для обхода DPI на Windows**  
Движок [zapret2](https://github.com/bol-van/zapret2) (winws2) + готовые пресеты, hosts и автообновление.

[![Последняя версия](https://img.shields.io/github/v/release/kayucm21/Zanpet?label=версия)](https://github.com/kayucm21/Zanpet/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-10%2F11%20x64-blue)](https://github.com/kayucm21/Zanpet/releases/latest)

**Скачать:** [ZapretUI-v2.9.11.zip](https://github.com/kayucm21/Zanpet/releases/download/v2.9.11/ZapretUI-v2.9.11.zip)

</div>

---

## Что это

**Zapret UI** — программа для обхода блокировок DPI одной кнопкой. Не нужно вручную править `.cmd`, hosts или стратегии: всё уже настроено в пресетах.

Движок **winws2** перехватывает трафик и искажает первые пакеты так, что DPI их не распознаёт, а сервер принимает соединение нормально.

Дополнительно: встроенный **VPN** (VLESS/REALITY через xray-core) и **автообновление** с FTP и GitHub.

---

## Быстрый старт

1. Скачайте [последний релиз](https://github.com/kayucm21/Zanpet/releases/latest) и распакуйте.
2. Запустите **ZapretUI.exe от администратора**.
3. Выберите пресет (например, *YouTube + Discord + Telegram*) и нажмите **«Включить обход»**.
4. Откройте нужный сайт или приложение.

**Обновление:** *Настройки → Проверить обновления* (FTP + GitHub).

---

## Что обходит

| Сервис | Как |
|--------|-----|
| **YouTube** | winws + ipset для QUIC |
| **Discord** | hosts + TLS-стратегии + UDP для войса |
| **Telegram Web** | hosts + hostfakesplit |
| **Telegram Desktop** | tg-ws-proxy (мост MTProto → WebSocket), автозапуск с обходом |
| **TikTok / Instagram / WhatsApp** | hosts + пресеты для CDN и веб-версий |

Discord и Telegram Desktop **не запускаются автоматически** — открываете сами, обход уже работает.

---

## Возможности

- **Обход DPI** — старт/стоп одной кнопкой, готовые пресеты, автоподбор стратегии
- **Автообновление** — приложение и движок с **FTP** (основной) и **GitHub** (зеркало)
- **Надёжный апдейтер** — полная замена файлов с любой версии 2.7.x+, лог в `%LOCALAPPDATA%\ZapretUI\logs\update.log`
- **VPN** — VLESS/REALITY, подписка, пинг, автообновление серверов
- **Тёмная и светлая тема**
- **Трей** — сворачивание, иконка меняется при активном обходе

---

## Требования

- Windows 10/11 **x64**
- Права **администратора** (WinDivert загружает драйвер в ядро)
- Интернет при первом запуске (загрузка движка zapret2)

---

## Сборка

```powershell
# Сборка + zip с проверкой версии в exe
powershell -File Scripts\Build-ReleaseZip.ps1

# Публикация на FTP (параметры — свои)
powershell -File Scripts\Publish-FtpUpdate.ps1 -FtpHost HOST -FtpUser USER -FtpPassword PASS -FtpPath /updates
```

Результат: `bin\Release\net9.0-windows\win-x64\publish\ZapretUI-vX.Y.Z.zip`

---

## Структура проекта

| Папка / файл | Назначение |
|--------------|------------|
| `Services/` | EngineService, UpdaterService, PresetService, TgWsProxyService и др. |
| `ViewModels/MainViewModel.cs` | MVVM-координатор |
| `Themes/` | Тёмная и светлая тема |
| `Scripts/` | Сборка релиза и загрузка на FTP |
| `ClassicData/` | Пресеты, списки, бинарники движка |

---

## Лицензия

MIT. Движок winws2 — по лицензии [zapret2](https://github.com/bol-van/zapret2).
