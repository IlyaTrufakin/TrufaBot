namespace TrufaBot.Infrastructure.Common;

public static class AppPaths
{
    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrufaBot");

    public static string DatabasePath => Path.Combine(AppDataFolder, "trufabot.db");
    public static string ConfigFilePath => Path.Combine(AppDataFolder, "config.json");
    public static string LogsFolder => Path.Combine(AppDataFolder, "logs");
    public static string CacheFolder => Path.Combine(AppDataFolder, "cache", "thumbnails");

    public static void EnsureDirectoriesCreated()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(LogsFolder);
        Directory.CreateDirectory(CacheFolder);
    }
}
