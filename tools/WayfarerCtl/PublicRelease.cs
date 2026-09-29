using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WayfarerCtl;

/// <summary>Fixed public GitHub discovery and transport facts; retained release.json remains lifecycle authority.</summary>
public sealed record PublicRelease(string Version, string Tag, string Asset, long Size, string Digest)
{
    public const long ArchiveLimit = 512L * 1024 * 1024;
    public const int MetadataLimit = 1024 * 1024;
    public const string Repository = "https://github.com/stef-k/Wayfarer";

    /// <summary>Only latest or exact stable core versions can select network input.</summary>
    public static void Selector(string value)
    {
        if (value != "latest" && !ReleaseContract.VersionSyntax(value))
            throw new UsageException("Public acquisition requires X.Y.Z or latest.");
    }

    /// <summary>Require exact project/release/upload identity and the REST asset digest, never metadata URLs.</summary>
    public static PublicRelease Parse(ReadOnlyMemory<byte> json, string selector)
    {
        Selector(selector);
        if (json.Length > MetadataLimit) throw new IOException("Release metadata exceeds bound.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        Unique(document.RootElement);
        var value = document.RootElement;
        var tag = value.GetProperty("tag_name").GetString() ?? "";
        if (!tag.StartsWith('v') || !ReleaseContract.VersionSyntax(tag[1..]) ||
            selector != "latest" && tag != "v" + selector || value.GetProperty("name").GetString() != tag ||
            value.GetProperty("draft").GetBoolean() || value.GetProperty("prerelease").GetBoolean() ||
            value.GetProperty("html_url").GetString() != Repository + "/releases/tag/" + tag ||
            !Regex.IsMatch(value.GetProperty("url").GetString() ?? "", "\\Ahttps://api.github.com/repos/stef-k/Wayfarer/releases/[1-9][0-9]*\\z"))
            throw new UsageException("Public release identity is not an exact stable Wayfarer release.");
        var asset = "wayfarer-" + tag + "-linux-amd64.tar.gz";
        var matches = value.GetProperty("assets").EnumerateArray().Where(item => item.GetProperty("name").GetString() == asset).ToArray();
        if (matches.Length != 1) throw new UsageException("Stable release must contain exactly one deployment asset; source-only releases are unsupported.");
        var uploaded = matches[0];
        var size = uploaded.GetProperty("size").GetInt64();
        var digest = uploaded.GetProperty("digest").GetString() ?? "";
        if (uploaded.GetProperty("state").GetString() != "uploaded" || size <= 0 || size > ArchiveLimit ||
            !Regex.IsMatch(digest, "\\Asha256:[a-f0-9]{64}\\z")) throw new IOException("Invalid public asset size/state/SHA-256 digest.");
        return new(tag[1..], tag, asset, size, digest);
    }

    /// <summary>Ambiguous duplicate keys cannot alter discovery identity.</summary>
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate public metadata property.");
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Unique(child);
    }
}

/// <summary>One anonymous bounded preparation owner shared by prefetch and the existing update planner.</summary>
public sealed class PublicReleaseAcquisition(IProcessRunner runner)
{
    /// <summary>Import precedes exact image pulls; failure never changes current release, receipts or database.</summary>
    public async Task<ReleaseBundle> AcquireAsync(string root, string selector, CancellationToken token)
    {
        ProtectedFiles.RequireRoot();
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            Credentials = null, ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("wayfarerctl/1");
        var release = await ResolveAsync(client, selector, token);
        var stage = Path.Combine(root, ".acquire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage, ProtectedFiles.PrivateDirectory);
        ReleaseBundle retained;
        Exception? primary = null;
        try
        {
            var archive = Path.Combine(stage, "download.gz");
            await DownloadAsync(client, release, archive, token);
            var bundle = await ReleaseArchive.ExtractAsync(archive, stage, token);
            if (bundle.Manifest.Status != "stable" || bundle.Manifest.Version != release.Version || bundle.Manifest.Tag != release.Tag)
                throw new IOException("Downloaded bundle contradicts public release identity.");
            ReleaseContract.RequireUse(bundle.Manifest, ReleaseCommands.OperatorVersion);
            retained = ReleaseStore.Import(root, bundle.Directory);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // Only this private staging tree is owned; interrupted ReleaseStore placement is deliberately retained.
            try { Directory.Delete(stage, true); }
            catch when (primary is not null) { Console.Error.WriteLine("Acquisition staging cleanup failed; original failure retained."); }
        }
        await PullAsync(retained, token);
        return retained;
    }

    /// <summary>Pull only validated immutable references with an empty Docker client credential configuration.</summary>
    internal async Task PullAsync(ReleaseBundle bundle, CancellationToken token)
    {
        var config = Path.Combine(Path.GetDirectoryName(bundle.Directory)!, ".pull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config, ProtectedFiles.PrivateDirectory);
        try
        {
            foreach (var reference in new[] { "ghcr.io/stef-k/wayfarer@" + bundle.Manifest.Images.ApplicationDigest,
                "ghcr.io/stef-k/wayfarer-db@" + bundle.Manifest.Images.DatabaseDigest, "caddy@" + bundle.Manifest.Images.CaddyDigest })
                if ((await runner.RunAsync(["--config", config, "pull", "--platform", "linux/amd64", reference], null, token)).Code != 0)
                    throw new UsageException("Exact anonymous image pull failed; validated bundle retained, images not execution-ready.");
            if (!await new ReleaseImagesVerifier(runner).VerifyAsync(bundle, token))
                throw new UsageException("Image verification failed; validated bundle retained, images not execution-ready.");
        }
        finally { Directory.Delete(config, true); }
    }

    /// <summary>Read only the fixed public API with a finite request/body deadline.</summary>
    internal static async Task<PublicRelease> ResolveAsync(HttpClient client, string selector, CancellationToken token)
    {
        PublicRelease.Selector(selector);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var endpoint = "https://api.github.com/repos/stef-k/Wayfarer/releases/" +
            (selector == "latest" ? "latest" : "tags/v" + selector);
        using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > PublicRelease.MetadataLimit) throw new IOException("Metadata exceeds bound.");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        await CopyAsync(input, output, PublicRelease.MetadataLimit, timeout.Token);
        return PublicRelease.Parse(output.ToArray(), selector);
    }

    /// <summary>Redirect authority is restricted to GitHub's release-asset CDN, with HTTPS and no credentials.</summary>
    internal static bool AssetRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host == "release-assets.githubusercontent.com" && uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal);

    /// <summary>Verify advertised size and digest before any archive parsing; never follow a metadata-provided URL.</summary>
    internal static async Task DownloadAsync(HttpClient client, PublicRelease release, string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var uri = new Uri(PublicRelease.Repository + "/releases/download/" + release.Tag + "/" + release.Asset);
        using var initial = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        HttpResponseMessage response = initial;
        if (initial.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
        {
            var location = initial.Headers.Location;
            if (location is null || !location.IsAbsoluteUri || !AssetRedirect(location)) throw new IOException("Untrusted asset redirect.");
            response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } size && size != release.Size) throw new IOException("Asset size changed.");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, UnixCreateMode = ProtectedFiles.PrivateFile
            });
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var count = await CopyAsync(input, output, release.Size, timeout.Token, hash);
            if (count != release.Size || "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Digest)
                throw new IOException("Public asset size/SHA-256 mismatch.");
        }
    }

    /// <summary>Bound streamed bytes and each stalled read, including unknown-length and compressed responses.</summary>
    internal static async Task<long> CopyAsync(Stream input, Stream output, long limit, CancellationToken token, IncrementalHash? hash = null)
    {
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await input.ReadAsync(buffer, readTimeout.Token);
            if (count == 0) return total;
            total = checked(total + count);
            if (total > limit) throw new IOException("Public input exceeds byte bound.");
            hash?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
