using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

// Exercise the actual worker runtime before adding capture policy or installation mutations.
if (args is not ["runtime-check"])
{
    Console.Error.WriteLine("Recovery worker is not yet configured.");
    return 2;
}
using var content = new MemoryStream();
using (var gzip = new GZipStream(content, CompressionLevel.SmallestSize, leaveOpen: true))
using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
{
    var entry = new UstarTarEntry(TarEntryType.RegularFile, "probe")
    {
        DataStream = new MemoryStream("Wayfarer recovery runtime"u8.ToArray())
    };
    tar.WriteEntry(entry);
}
content.Position = 0;
var digest = Convert.ToHexStringLower(SHA256.HashData(content));
var start = new ProcessStartInfo("pg_dump") { RedirectStandardOutput = true, UseShellExecute = false };
start.ArgumentList.Add("--version");
using var child = Process.Start(start) ?? throw new IOException("Cannot start PostgreSQL tool.");
var version = await child.StandardOutput.ReadToEndAsync();
await child.WaitForExitAsync();
if (child.ExitCode != 0 || !version.StartsWith("pg_dump (PostgreSQL) 17.")) return 1;
Console.WriteLine(JsonSerializer.Serialize(new { Schema = 1, Runtime = "ready", Digest = digest, Tool = version.Trim() }));
return 0;
