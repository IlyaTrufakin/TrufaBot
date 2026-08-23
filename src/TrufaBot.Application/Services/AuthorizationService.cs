using TrufaBot.Domain.Entities;

namespace TrufaBot.Application.Services;

public interface IAuthorizationService
{
    bool CanAccessPath(User user, int storageSourceId, string relativePath);
}

public class AuthorizationService : IAuthorizationService
{
    public bool CanAccessPath(User user, int storageSourceId, string relativePath)
    {
        if (!user.IsActive) return false;
        if (user.IsAdmin) return true;

        var permissions = user.Permissions
            .Where(p => p.StorageSourceId == storageSourceId)
            .ToList();

        if (!permissions.Any()) return false;

        var normalizedPath = relativePath.Replace('\\', '/').Trim('/');

        bool isDenied = permissions
            .Where(p => p.IsDenied)
            .Any(p => IsPathMatch(normalizedPath, p.AllowedRelativePath, p.IsRecursive));

        if (isDenied) return false;

        bool isAllowed = permissions
            .Where(p => !p.IsDenied)
            .Any(p => IsPathMatch(normalizedPath, p.AllowedRelativePath, p.IsRecursive));

        return isAllowed;
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
