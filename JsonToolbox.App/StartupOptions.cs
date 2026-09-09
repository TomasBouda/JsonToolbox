namespace JsonToolbox.App;

/// <summary>
/// What the command line asked the application to do.
/// </summary>
/// <remarks>
/// <para>
/// The plain form — one or more paths — opens each of them, which is what makes the toolbox
/// usable as the "Open with" handler for <c>.json</c>. Adding <c>--compare</c> opens the two
/// files given and puts them straight into the comparison, so a script or a shell alias can go
/// from two paths to a side-by-side diff without a click.
/// </para>
/// <para>
/// Parsing is kept apart from acting on the result so that it can be reasoned about, and tested,
/// without an application to run it in.
/// </para>
/// </remarks>
/// <param name="Paths">The files to open, in the order they were given.</param>
/// <param name="Compare">Whether to open the comparison on the two files.</param>
/// <param name="Problem">
/// What was wrong with the command line, or <c>null</c> when it made sense. A problem does not
/// stop the application: the window opens and says so, because a mistyped argument is not a
/// reason to give somebody nothing at all.
/// </param>
public sealed record StartupOptions(IReadOnlyList<string> Paths, bool Compare, string? Problem)
{
    private const string Usage = "Usage: JsonToolbox [--compare] [file ...]";

    public static StartupOptions Parse(IReadOnlyList<string>? arguments)
    {
        List<string> paths = [];
        List<string> unknown = [];
        bool compare = false;

        foreach (string argument in arguments ?? [])
        {
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            // Anything that starts with a dash is meant as a switch. A path could in principle
            // start with one, but treating it as a file and silently opening nothing is the
            // worse of the two failures: a mistyped switch would go unmentioned.
            if (argument[0] == '-')
            {
                switch (argument.ToLowerInvariant())
                {
                    case "-c" or "--compare" or "--diff":
                        compare = true;
                        break;

                    default:
                        unknown.Add(argument);
                        break;
                }
            }
            else
            {
                paths.Add(argument);
            }
        }

        return new StartupOptions(paths, compare, Explain(paths, compare, unknown));
    }

    private static string? Explain(List<string> paths, bool compare, List<string> unknown)
    {
        if (unknown.Count > 0)
        {
            return $"Not a known option: {string.Join(", ", unknown)}. {Usage}";
        }

        if (!compare)
        {
            return null;
        }

        return paths.Count switch
        {
            2 => null,
            < 2 => $"--compare needs two files; {(paths.Count == 0 ? "none were" : "only one was")} given. {Usage}",

            // The extra files still open as tabs, and either can be put into the comparison
            // afterwards, so this is worth saying rather than refusing over.
            _ => $"--compare takes two files; the first two of the {paths.Count} given are being compared.",
        };
    }
}
