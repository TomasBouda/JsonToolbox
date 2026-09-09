using System.Runtime.InteropServices;

namespace JsonToolbox.App;

/// <summary>
/// Writes to the console the application was started from, when there is one.
/// </summary>
/// <remarks>
/// <para>
/// The toolbox is a windowed program, which on Windows means it starts with no console at all:
/// anything written to standard output goes nowhere, so <c>--help</c> would print into the void.
/// Attaching to the console of the process that launched it puts the text where the person who
/// typed the command is looking, and costs nothing when there is no such console — started from
/// Explorer or a shortcut, the attach simply fails and the text is dropped.
/// </para>
/// <para>
/// One thing to expect: the shell has already printed its next prompt by the time this writes,
/// so the output lands underneath it. That is how every windowed program that borrows its
/// parent's console behaves, and it is better than printing nothing.
/// </para>
/// </remarks>
internal static class ConsoleOutput
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    private static bool _attached;

    /// <summary>Writes a line to the console, if one can be reached.</summary>
    /// <returns>True when the text was written somewhere a person can see it.</returns>
    public static bool WriteLine(string text)
    {
        if (!Attach())
        {
            return false;
        }

        Console.Out.WriteLine(text);
        Console.Out.Flush();
        return true;
    }

    private static bool Attach()
    {
        if (_attached)
        {
            return true;
        }

        // Somewhere to write already: a pipe or a file that the shell redirected the output to,
        // or — everywhere but Windows — the terminal the process inherited. Attaching to the
        // parent's console in that case would throw the redirection away.
        if (!OperatingSystem.IsWindows() || HasOutput())
        {
            _attached = true;
            return true;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            return false;
        }

        // The standard output handle was bound to nothing when the process started without a
        // console, so it has to be opened again now that there is one to write to.
        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(writer);

        _attached = true;
        return true;
    }

    /// <summary>Whether standard output already leads somewhere.</summary>
    private static bool HasOutput()
    {
        try
        {
            // A process started without a console and without redirection has an invalid
            // standard output handle, which the runtime hands back as the null stream.
            return Console.OpenStandardOutput() != Stream.Null;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
