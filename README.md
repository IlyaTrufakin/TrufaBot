# TrufaBot 📸🤖

мный домашний медиа-сервер для Windows с интерактивным Telegram-ботом, фоновым режимом (System Tray), многоисточниковой файловой системой (NAS / локальные диски), гранулярным разделением прав доступа и будущей поддержкой локальной -классификации по расписанию (ONNX Runtime).

## 🏛 рхитектура
- **Язык**: C# (.NET 8)
- **рхитектурный шаблон**: Clean Architecture + DDD + MVVM
- **аза данных**: SQLite (EF Core)
- **UI**: WPF + System Tray
- **Telegram**: Telegram.Bot (C#)
- **езопасность**: 100% изоляция пользовательских данных в %LOCALAPPDATA%\TrufaBot\

## 🚀 Сборка и запуск
1. ткройте TrufaBot.sln в **Visual Studio 2026 / 2022**.
2. Соберите и запустите проект TrufaBot.Presentation (F5).
