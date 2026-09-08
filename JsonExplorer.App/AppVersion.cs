using System.Reflection;

namespace JsonExplorer.App;

/// <summary>
/// The version of the running build, taken from the assembly rather than from a constant.
/// </summary>
/// <remarks>
/// A hand-maintained version string drifts from the build it claims to describe the first
/// time somebody forgets to update it, and then the number in the window is worse than no
/// number at all. Reading it back from the assembly makes that impossible.
/// </remarks>
public static class AppVersion
{
    private static readonly Lazy<string> Value = new(Read);

    public static string Current => Value.Value;

    private static string Read()
    {
        Assembly assembly = typeof(AppVersion).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // The SDK appends the source revision after a '+', which is noise in a title bar.
        int plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
