# TrufaBot 📸🤖

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-blue?style=for-the-badge&logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Language-C%23%2012-brightgreen?style=for-the-badge&logo=csharp" alt="C#" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6?style=for-the-badge&logo=windows" alt="Windows" />
  <img src="https://img.shields.io/badge/Telegram-Bot%20API-2CA5E0?style=for-the-badge&logo=telegram" alt="Telegram" />
  <img src="https://img.shields.io/badge/Architecture-Clean%20Architecture-orange?style=for-the-badge" alt="Clean Architecture" />
  <img src="https://img.shields.io/badge/License-MIT-green?style=for-the-badge" alt="MIT License" />
</p>

---

<p align="center">
  <a href="#-english"><b>English</b></a> •
  <a href="#-українська"><b>Українська</b></a>
</p>

---

# 🇬🇧 English

**TrufaBot** is a private, lightweight, and extensible home media server designed to run 24/7 on a Windows PC. It bridges your family's personal photo/video archives (stored on NAS or local hard drives) with Telegram, providing an intuitive, secure, and interactive interface for your family members.

### 🌟 Key Features

* 📁 **Multi-Source Storage Management**: Seamlessly connect local drives (`D:\Photos`) and Network Attached Storage shares (`\\NAS\FamilyArchive`).
* 🤖 **Interactive Telegram Bot**:
  * **Always-Visible Controls**: Persistent menu buttons (`📁 Archive Explorer`, `🎲 Random Photo`) eliminate the need to type commands manually.
  * **Deep Directory Tree Traversal**: Dual pagination system for subfolders and media files.
  * **On-the-Fly Thumbnail Albums**: Generates lightweight compressed previews on the fly and sends media group collages (8–10 photos) instantly.
  * **Post-Media Quick Actions**: Every sent photo includes inline buttons to download the uncompressed original, pick another random photo, or jump straight into its parent folder.
  * **Smart Random Photo Selection**: Strictly filters image formats (`.jpg`, `.jpeg`, `.png`, `.webp`, `.heic`), completely excluding video files from random picks.
  * **Large Video Handling**: Automatically detects videos exceeding Telegram Bot API limits (50 MB) and warns the user without crashing or polluting error logs.
* 🔒 **Granular Family Access Control (RBAC)**:
  * Visual permission matrix in the WPF desktop interface.
  * Whitelist authentication: unauthorized Telegram users are immediately rejected.
  * Individual folder-level grants per family member (e.g., Mom can access `/Vacations/2023`, while children only see `/Family/Cartoons`).
* 🕒 **24/7 Background System Tray Mode**:
  * Closing the app window with **[✕]** minimizes it directly to the Windows Notification Area (System Tray).
  * Tray icon context menu allows fast window restoration, bot start/stop, and graceful exit.
* 📊 **Structured Audit & High-Contrast Log Analyzer**:
  * Real-time structured logging (Serilog + SQLite audit trail).
  * High-contrast dark theme log viewer with instant visual feedback.
* 🛡 **100% Privacy & Open Source Ready**:
  * All sensitive configurations, tokens, databases, logs, and caches are strictly stored in `%LOCALAPPDATA%\TrufaBot\`.
  * Zero risk of leaking private data, credentials, or NAS file paths to public Git repositories.
* 🧠 **Upcoming Phase 2: Scheduled Local AI Classification**:
  * Powered by **ONNX Runtime** (DirectML / CUDA / CPU) running 100% offline on your PC.
  * Background batch classification of untagged images based on a customizable category taxonomy.

---

### 🏛 Solution Architecture (Clean Architecture)

```
TrufaBot/
├── src/
│   ├── TrufaBot.Domain/               # Core entities (StorageSource, User, UserFolderPermission, MediaItem, AuditLog)
│   ├── TrufaBot.Application/          # Business logic, AuthorizationService, interfaces
│   ├── TrufaBot.Infrastructure/       # SQLite EF Core, StorageSyncService, ThumbnailService, TelegramBotService
│   └── TrufaBot.Presentation/         # Modern WPF GUI (MVVM via CommunityToolkit.Mvvm) + H.NotifyIcon Tray
└── TrufaBot.slnx                      # Visual Studio 2026 / 2022 Solution
```

---

### 🚀 Getting Started

1. **Prerequisites**: Windows 10 / 11 and [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. **Clone the repository**:
   ```bash
   git clone https://github.com/IlyaTrufakin/TrufaBot.git
   ```
3. **Open in Visual Studio**:
   * Open `TrufaBot.slnx` (or `TrufaBot.sln`).
   * Set `TrufaBot.Presentation` as the Startup Project.
   * Press **F5** to build and run.
4. **Configuration**:
   * Enter your Telegram Bot Token in the top bar.
   * Add your NAS or local folders in the **Sources** tab.
   * Add family members' Telegram IDs and configure their folder permissions in the **Users & Access** tab.
   * Click **▶ Start Bot**.

---

# 🇺🇦 Українська

**TrufaBot** — це приватний, надійний та розширюваний домашній медіа-сервер для Windows, створений для цілодобової фонової роботи на вашому ПК. Він надає зручний та безпечний доступ родині до сімейних фото- та відеоархівів (на NAS або локальних дисках) через інтерактивного Telegram-бота.

### 🌟 Основні можливості

* 📁 **Гнучке підключення сховищ**: Одночасна підтримка локальних дисків (`D:\Photos`) та мережевих папок NAS (`\\NAS\FamilyArchive`).
* 🤖 **Інтерактивний Telegram-бот**:
  * **Постійне нижнє меню**: Кнопки швидкого доступу (`📁 Провідник архіву`, `🎲 Випадкове фото`) завжди закріплені на екрані — писати команду `/start` вручну більше не потрібно.
  * **Повноцінна навігація по дереву папок**: Роздільна пагінація для підпапок та файлів на будь-яку глибину каталогу.
  * **Альбоми попереднього перегляду на льоту**: Бот автоматично стискає фотографії та надсилає їх зручним колажем (MediaGroup до 8–10 фото) без витрат трафіку на завантаження важких оригіналів.
  * **Швидкі дії під кожним надісланим медіа**: Кнопки під фото дозволяють завантажити нестиснений оригінал, отримати наступне випадкове фото або в один клік відкрити вихідну папку на диску.
  * **Вибір тільки фотографій**: Випадковий вибір суворо фільтрує зображення (`.jpg`, `.jpeg`, `.png`, `.webp`, `.heic`), повністю виключаючи відеофайли з випадкової видачі.
  * **Захист від перевантаження великими відео**: Бот автоматично перевіряє розмір відео та інформує користувача, якщо файл перевищує ліміт Telegram Bot API (50 МБ).
* 🔒 **Гранулярне керування правами доступу (RBAC)**:
  * Візуальна панель налаштування прав для кожного члена родини в інтерфейсі WPF.
  * Авторизація за білим списком (Whitelist) — сторонні користувачі миттєво відсікаються.
  * Призначення доступу до конкретних підпапок (наприклад, доступ тільки до `/Відпустка/2023`, тоді як інші папки залишаються прихованими).
* 🕒 **Фоновий режим у системному треї (24/7)**:
  * При натисканні на хрестик **[✕]** програма не закривається, а згортається в область сповіщень Windows (біля годинника).
  * Контекстне меню іконки в треї дозволяє розгорнути вікно, запустити/зупинити бота або здійснити повний вихід.
* 📊 **Журнал подій та контрастний аудит-аналізатор**:
  * Структуроване логування подій у реальному часі (Serilog + SQLite).
  * Зручна темна контрастна тема для комфортного читання журналу.
* 🛡 **100% Приватність для Open Source**:
  * Усі токени, налаштування, бази даних, логи та кеш мініатюр зберігаються виключно в `%LOCALAPPDATA%\TrufaBot\`.
  * Ваші конфіденційні дані гарантовано не потраплять у відкритий репозиторій Git.
* 🧠 **Етап 2 (у розробці): Локальна ШІ-класифікація за розкладом**:
  * Локальний запуск моделей **ONNX Runtime** (DirectML / CUDA / CPU) без передачі даних у хмару.
  * Фонова пакетна класифікація нових фотографій за категоріями.

---

### 🏛 Архітектура проєкту (Clean Architecture)

```
TrufaBot/
├── src/
│   ├── TrufaBot.Domain/               # Сутності бази даних (StorageSource, User, UserFolderPermission, MediaItem, AuditLog)
│   ├── TrufaBot.Application/          # Бізнес-логіка, сервіс авторизації AuthorizationService, інтерфейси
│   ├── TrufaBot.Infrastructure/       # SQLite EF Core, StorageSyncService, ThumbnailService, TelegramBotService
│   └── TrufaBot.Presentation/         # Графічний інтерфейс WPF (.NET 8, MVVM) + системний трей
└── TrufaBot.slnx                      # Файл рішення Visual Studio 2026 / 2022
```

---

### 🚀 Встановлення та запуск

1. **Вимоги**: Windows 10 / 11 та [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. **Клонування репозиторію**:
   ```bash
   git clone https://github.com/IlyaTrufakin/TrufaBot.git
   ```
3. **Запуск у Visual Studio**:
   * Відкрийте `TrufaBot.slnx` (або `TrufaBot.sln`).
   * Встановіть `TrufaBot.Presentation` як запускаємий проєкт (*Set as Startup Project*).
   * Натисніть **F5** для збірки та запуску.
4. **Перше налаштування**:
   * Введіть токен вашого Telegram-бота у верхній панелі.
   * Додайте локальні папки або мережевий NAS на вкладці **«Джерела»**.
   * Додайте Telegram ID членів родини та налаштуйте для них дозволені папки на вкладці **«Користувачі та Доступ»**.
   * Натисніть **▶ Запустити бота**.
