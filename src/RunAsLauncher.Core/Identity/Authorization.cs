using RunAsLauncher.Policies;

namespace RunAsLauncher.Identity;

/// <summary>
/// Decisão pura de autorização: o usuário (ou algum de seus grupos) está em
/// <see cref="PolicyItem.AllowedSids"/>? Lista vazia = ninguém pode lançar o item.
/// </summary>
public static class Authorization
{
    public static bool IsAllowed(PolicyItem item, string userSid, IEnumerable<string> groupSids)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.AllowedSids is null || item.AllowedSids.Count == 0)
        {
            return false;
        }

        var allowed = new HashSet<string>(item.AllowedSids, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(userSid) && allowed.Contains(userSid))
        {
            return true;
        }

        foreach (var sid in groupSids ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrEmpty(sid) && allowed.Contains(sid))
            {
                return true;
            }
        }

        return false;
    }
}
