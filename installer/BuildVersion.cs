using System.Reflection;

namespace D2RInstaller;

internal static class BuildVersion
{
    private const string FallbackVersion = "0.0.0-local";

    private static readonly Lazy<string> ReleaseTagValue = new(ResolveReleaseTag);
    private static readonly Lazy<string> UserAgentVersionValue = new(() => SanitizeForToken(ReleaseTag));

    public static string ReleaseTag => ReleaseTagValue.Value;

    public static string UserAgentVersion => UserAgentVersionValue.Value;

    private static string ResolveReleaseTag()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion;

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is null)
            return FallbackVersion;

        return $"{assemblyVersion.Major}.{Math.Max(assemblyVersion.Minor, 0)}.{Math.Max(assemblyVersion.Build, 0)}";
    }

    private static string SanitizeForToken(string version)
    {
        var sanitized = new string(version.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? FallbackVersion : sanitized;
    }
}