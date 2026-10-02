using System.Security.Cryptography;
using System.Text;

namespace Codev;

/// <summary>Resolves Codev's per-user data root and keeps explicitly redirected profiles separate.</summary>
public static class CodevDataPaths
{
    public const string RootEnvironmentVariable = "CODEV_DATA_ROOT";

    public static string LocalDataRoot => ResolveLocalDataRoot(
        Environment.GetEnvironmentVariable(RootEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string ApplicationDataRoot => Path.Combine(LocalDataRoot, "Codev");

    public static string ResolveLocalDataRoot(string? configuredRoot, string? defaultRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            if (string.IsNullOrWhiteSpace(defaultRoot))
                throw new InvalidOperationException("Codev could not determine the local application-data folder.");
            return Path.GetFullPath(defaultRoot);
        }

        if (!Path.IsPathRooted(configuredRoot))
            throw new InvalidOperationException($"{RootEnvironmentVariable} must be an absolute path.");
        return Path.GetFullPath(configuredRoot);
    }

    /// <summary>Leaves default credential targets unchanged and namespaces redirected profiles by root.</summary>
    public static string ScopeCredentialTarget(string target, string? configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (string.IsNullOrWhiteSpace(configuredRoot)) return target;
        if (!Path.IsPathRooted(configuredRoot))
            throw new InvalidOperationException($"{RootEnvironmentVariable} must be an absolute path.");

        var root = Path.GetFullPath(configuredRoot);
        var identity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return target.TrimEnd('/') + "/" + digest;
    }
}
