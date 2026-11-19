using System.Reflection;

namespace ZeroDep;

/// <summary>Version and identity information for the ZeroDep engine.</summary>
public static class ZeroDepInfo
{
    /// <summary>
    /// The semantic version of the ZeroDep engine, read from the assembly's informational version
    /// (any <c>+commit</c> build metadata is removed).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        Assembly assembly = typeof(ZeroDepInfo).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational!.IndexOf('+');
            return plus >= 0 ? informational.Substring(0, plus) : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
