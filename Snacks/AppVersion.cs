using System.Reflection;

namespace Snacks;

/// <summary>
///     Runtime view of the MSBuild <c>&lt;Version&gt;</c> value in Snacks.csproj.
///     Build metadata (for example a source-revision suffix) is deliberately omitted
///     from user-facing and cluster-protocol output.
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<string> Resolved = new(Resolve);

    /// <summary>Semantic application version, such as <c>2.15.1</c>.</summary>
    public static string Current => Resolved.Value;

    private static string Resolve()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        var version = assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
