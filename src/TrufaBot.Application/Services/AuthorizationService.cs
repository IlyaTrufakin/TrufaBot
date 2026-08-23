using TrufaBot.Domain.Entities;

namespace TrufaBot.Application.Services;

public interface IAuthorizationService
{
    bool HasAnyAccessToSource(User user, int storageSourceId);
    bool CanAccessPath(User user, int storageSourceId, string relativePath);
    bool CanViewFolder(User user, int storageSourceId, string folderPath);
}

public class AuthorizationService : IAuthorizationService
{
    public bool HasAnyAccessToSource(User user, int storageSourceId)
    {
        if (!user.IsActive) return false;
        if (user.IsAdmin) return true;

        return user.Permissions.Any(p => p.StorageSourceId == storageSourceId && !p.IsDenied);
    }

    public bool CanAccessPath(User user, int storageSourceId, string relativePath)
    {
        if (!user.IsActive) return false;
        if (user.IsAdmin) return true;

        var permissions = user.Permissions
            .Where(p => p.StorageSourceId == storageSourceId)
            .ToList();

        if (!permissions.Any()) return false;

        var normalizedPath = relativePath.Replace('\\', '/').Trim('/');

        // 1. Проверяем явный запрет
        bool isDenied = permissions
            .Where(p => p.IsDenied)
            .Any(p => IsPathMatch(normalizedPath, p.AllowedRelativePath, p.IsRecursive));

        if (isDenied) return false;

        // 2. Проверяем наличие разрешения
        bool isAllowed = permissions
            .Where(p => !p.IsDenied)
            .Any(p => IsPathMatch(normalizedPath, p.AllowedRelativePath, p.IsRecursive));

        return isAllowed;
    }

    public bool CanViewFolder(User user, int storageSourceId, string folderPath)
    {
        if (!user.IsActive) return false;
        if (user.IsAdmin) return true;

        var permissions = user.Permissions
            .Where(p => p.StorageSourceId == storageSourceId)
            .ToList();

        if (!permissions.Any()) return false;

        var normalizedFolder = folderPath.Replace('\\', '/').Trim('/');

        // Если у пользователя есть прямой доступ к этой папке или выше
        if (CanAccessPath(user, storageSourceId, normalizedFolder))
            return true;

        // Либо если одно из разрешений пользователя находится ВНУТРИ этой папки (чтобы пользователь мог дойти до нее по дереву)
        bool hasChildPermission = permissions
            .Where(p => !p.IsDenied)
            .Any(p =>
            {
                var rule = p.AllowedRelativePath.Replace('\\', '/').Trim('/');
                if (rule == "*") return true;
                if (string.IsNullOrEmpty(normalizedFolder)) return true;
                return rule.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase);
            });

        return hasChildPermission;
    }

    private static bool IsPathMatch(string targetPath, string rulePath, bool isRecursive)
    {
        rulePath = rulePath.Replace('\\', '/').Trim('/');
        if (rulePath == "*" || string.IsNullOrEmpty(rulePath)) return true;

        if (targetPath.Equals(rulePath, StringComparison.OrdinalIgnoreCase))
            return true;

        if (isRecursive && targetPath.StartsWith(rulePath + "/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
