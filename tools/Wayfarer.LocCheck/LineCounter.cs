namespace Wayfarer.LocCheck;

/// <summary>
/// Counts non-blank lines in source files.
/// </summary>
public static class LineCounter
{
    /// <summary>
    /// Counts non-blank lines. Comments count as LOC because they still affect file size.
    /// </summary>
    public static int CountNonBlankLines(string path)
    {
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return count;
    }
}
