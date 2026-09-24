namespace Wayfarer.Tests.Infrastructure;

/// <summary>Owns one unique filesystem fixture; never adopts a caller-selected directory.</summary>
internal sealed class TestDirectory : IDisposable
{
    /// <summary>Exact generated root, also recognized by stale-residue maintenance.</summary>
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "wayfarer-fixture-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the fixture directory when its lifetime starts.</summary>
    internal TestDirectory() => Directory.CreateDirectory(Path);

    /// <summary>Removes only this instance's ordinary tree; leaves linked residue for diagnosis.</summary>
    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        RejectLinks(new DirectoryInfo(Path));
        Directory.Delete(Path, recursive: true);
    }

    /// <summary>Checks each level before traversal so a fixture link cannot redirect cleanup.</summary>
    private static void RejectLinks(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing linked test fixture cleanup: " + item.FullName);
        if (item is DirectoryInfo directory)
            foreach (var child in directory.EnumerateFileSystemInfos()) RejectLinks(child);
    }
}
