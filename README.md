# FireVPN для Windows и macOS

Версия для Windows — `v2rayN/v2rayN` (WPF), для macOS — `v2rayN/v2rayN.Desktop` (Avalonia; `.dmg` собирает
GitHub Actions «FireVPN macOS», `scripts/firevpn-macos-package.sh`). Общий код FireVPN — `v2rayN/ServiceLib/Common/FireVpn.cs`.

Клиент VPN-сервиса [FireVPN](https://fire-vpn.online): минималистичный главный экран — сервер, большая кнопка
с Фаером, таймер, быстрый выбор серверов; в настройках — подписка, режим «Прокси» или «Туннель» (весь компьютер).
Полный интерфейс v2rayN остаётся в «Расширенных настройках».

Скачать: https://fire-vpn.online/download/#windows · Подписка: Telegram-бот [@VpnFire57_bot](https://t.me/VpnFire57_bot)

## Основа и лицензия

Программа основана на [v2rayN](https://github.com/2dust/v2rayN) 7.25.6 (автор — 2dust) и, как и он,
распространяется по лицензии **GNU GPL v3** (см. `LICENSE`). Ядра Xray и sing-box — из официального релиза v2rayN.

## Что изменено

- `v2rayN/v2rayN/Views/FireHomeView.xaml(.cs)` — главный экран FireVPN поверх окна v2rayN: Фаер за решёткой,
  пока VPN выключен, и на свободе, когда включён; выбор сервера с задержкой; настройки
- `v2rayN/v2rayN/Common/FireFlags.cs` — круглые флаги стран (Windows не рисует флаги-эмодзи)
- обновление самого v2rayN с GitHub отключено — FireVPN обновляется со своего сайта
- `v2rayN/v2rayN/Common/FireVpn.cs` — сведения о подписке (`subscription-userinfo`), обновления с
  `fire-vpn.online/download/windows/latest.json`, ссылки `firevpn://install-sub?url=…`
- `MainWindow`, `App` — простой/расширенный режим, приём ссылок `firevpn://`
- имя `FireVPN.exe`, значки, по умолчанию русский язык и тёмная тема; `AmazTool` перезапускает `FireVPN.exe`

## Сборка

Нужен .NET SDK 10. Ядра (`bin/`) берутся из официального архива `v2rayN-windows-64.zip` того же выпуска
(пути в начале скрипта).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\firevpn-build.ps1
```

Результат — `FireVPN-windows-64.zip` (папка `FireVPN/` внутри: так его распаковывает программа обновления).
