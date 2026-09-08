namespace JsonExplorer.Core.Model;

/// <summary>
/// Formats byte counts for people rather than for machines.
/// </summary>
public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} kB",
        _ => $"{bytes} B",
    };
}
