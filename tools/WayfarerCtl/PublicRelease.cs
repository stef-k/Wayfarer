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
    public static PublicRelease Parse(ReadOnlyMemory<byte> json, string selector, string? platform = null)
    {
        Selector(selector);
        platform ??= WayfarerRecovery.NativePlatform.Current;
        if (!WayfarerRecovery.NativePlatform.Supported(platform)) throw new IOException("Unsupported public platform.");
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
        var asset = "wayfarer-" + tag + "-" + platform.Replace('/', '-') + ".tar.gz";
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

/// <summary>Safe acquisition explanation and remedy; neither field comes from transport or child output.</summary>
public sealed class AcquisitionException(string message, string nextAction) : IOException(message)
{
    /// <summary>One fixed remedy selected by the acquisition owner, never a provider-supplied URL or error.</summary>
    public string NextAction { get; } = nextAction;
}

/// <summary>One anonymous bounded preparation owner shared by setup, prefetch and the existing update planner.</summary>
public sealed class PublicReleaseAcquisition(IProcessRunner runner, Action<string>? progress = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Import precedes exact image pulls; failure never changes current release, receipts or database.</summary>
    public async Task<ReleaseBundle> AcquireAsync(string root, string selector, CancellationToken token)
    {
        string stage;
        try
        {
            ProtectedFiles.RequireRoot();
            ProtectedFiles.SafePath(root);
            ProtectedFiles.Check(root, 0, directory: true);
            // All preparation residue stays below releases, which fresh setup permits on a subsequent plain retry.
            var releases = Path.Combine(root, "releases");
            Directory.CreateDirectory(releases, ProtectedFiles.PrivateDirectory);
            ProtectedFiles.Check(releases, 0, directory: true);
            stage = Path.Combine(releases, ".acquire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage, ProtectedFiles.PrivateDirectory);
        }
        catch (Exception)
        {
            throw new AcquisitionException("Wayfarer could not prepare protected download files.",
                "Check available disk space and the installation folder's ownership and permissions.");
        }
        ReleaseBundle retained;
        Exception? primary = null;
        try
        {
            var bundle = await StageAsync(stage, selector, token, progress, delay);
            progress?.Invoke("Retaining the verified download...");
            try { retained = ReleaseStore.Import(root, bundle.Directory); }
            catch (Exception)
            {
                throw new AcquisitionException("Wayfarer could not safely retain the verified release. Existing release files were preserved.",
                    "Check disk space and protected folder permissions. Do not replace existing release files or bypass validation.");
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally { Cleanup(stage, primary); }
        await PullAsync(retained, token);
        return retained;
    }

    /// <summary>Read-only staging never imports, pulls or executes downloads; an injected HttpClient uses the same transport checks.</summary>
    internal static async Task<ReleaseBundle> StageAsync(string stage, string selector, CancellationToken token,
        Action<string>? progress = null, Func<TimeSpan, CancellationToken, Task>? delay = null, HttpClient? client = null)
    {
        using var ownedClient = client is null ? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            Credentials = null, ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = Timeout.InfiniteTimeSpan } : null;
        client ??= ownedClient!;
        if (ownedClient is not null) client.DefaultRequestHeaders.UserAgent.ParseAdd("wayfarerctl/1");
        progress?.Invoke("Finding the Wayfarer release...");
        var release = await ResolveAsync(client, selector, token, progress, delay);
        var archive = Path.Combine(stage, "download.gz");
        progress?.Invoke("Downloading Wayfarer...");
        await DownloadAsync(client, release, archive, token, progress, delay);
        progress?.Invoke("Unpacking the verified download...");
        try
        {
            var bundle = await ReleaseArchive.ExtractAsync(archive, stage, token);
            if (bundle.Manifest.Status != "stable" || bundle.Manifest.Version != release.Version || bundle.Manifest.Tag != release.Tag)
                throw new IOException("Downloaded bundle contradicts public release identity.");
            ReleaseContract.RequireUse(bundle.Manifest, ReleaseCommands.OperatorVersion);
            return bundle;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            throw new AcquisitionException("Wayfarer could not safely unpack or validate the downloaded release.",
                "Do not bypass this check. Check free disk space and try again later using an official Wayfarer release.");
        }
    }

    /// <summary>Only exact immutable pulls retry; image identity verification and local safety checks never do.</summary>
    internal async Task PullAsync(ReleaseBundle bundle, CancellationToken token)
    {
        string? config = null;
        Exception? primary = null;
        var failure = "Wayfarer could not validate the retained release before downloading containers.";
        try
        {
            bundle = ReleaseBundle.Validate(bundle.Directory, installed: true);
            config = Path.Combine(Path.GetDirectoryName(bundle.Directory)!, ".pull-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(config, ProtectedFiles.PrivateDirectory);
            failure = "Wayfarer could not download the required containers. The verified release is safely retained.";
            progress?.Invoke("Downloading required containers...");
            foreach (var reference in new[] { "ghcr.io/stef-k/wayfarer@" + bundle.Manifest.Images.PlatformDigest,
                "ghcr.io/stef-k/wayfarer-db@" + bundle.Manifest.Images.DatabaseDigest, "caddy@" + bundle.Manifest.Images.CaddyDigest })
                await PullImageAsync(config, bundle.Manifest.Platform, reference, token);
            failure = "Wayfarer could not verify the required containers. The verified release is safely retained.";
            progress?.Invoke("Verifying required containers...");
            if (!await new ReleaseImagesVerifier(runner).VerifyAsync(bundle, token)) throw new IOException("Images are not verified.");
        }
        catch (AcquisitionException error) { primary = error; throw; }
        catch (OperationCanceledException error) when (token.IsCancellationRequested) { primary = error; throw; }
        catch (Exception)
        {
            primary = new AcquisitionException(failure, "Check Docker and the protected release files. Do not bypass container verification.");
            throw primary;
        }
        finally { if (config is not null) Cleanup(config, primary); }
    }

    /// <summary>Retry one already-validated immutable image read without forwarding child output or changing release authority.</summary>
    internal async Task PullImageAsync(string config, string platform, string reference, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var pulled = false;
            try { pulled = (await runner.RunAsync(["--config", config, "pull", "--platform", platform, reference], null, token)).Code == 0; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* Only an exact pull can retry a child timeout. */ }
            if (pulled) return;
            if (attempt == 3) throw new AcquisitionException("Wayfarer could not download the required containers. The verified release is safely retained.",
                "Check internet access to ghcr.io and the Caddy container registry (docker.io).");
            await WaitAsync(attempt, "download required containers", null, token, progress, delay);
        }
    }

    /// <summary>Read the fixed public API with a finite deadline; invalid identity or metadata is never retried.</summary>
    internal static async Task<PublicRelease> ResolveAsync(HttpClient client, string selector, CancellationToken token,
        Action<string>? progress = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        PublicRelease.Selector(selector);
        byte[] bytes;
        TimeSpan? retryAfter = null;
        try
        {
            bytes = await RetryAsync(async () =>
            {
                retryAfter = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var endpoint = "https://api.github.com/repos/stef-k/Wayfarer/releases/" +
                    (selector == "latest" ? "latest" : "tags/v" + selector);
                using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                retryAfter = RetryAfter(response);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > PublicRelease.MetadataLimit) throw new InvalidDataException("Metadata exceeds bound.");
                using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var output = new MemoryStream();
                await CopyAsync(input, output, PublicRelease.MetadataLimit, timeout.Token, transport: true);
                return output.ToArray();
            }, "reach GitHub to check the release", false, () => retryAfter, token, progress, delay);
        }
        catch (HttpRequestException)
        {
            throw new AcquisitionException("Wayfarer could not check the official release on GitHub.",
                "Check your internet connection and access to github.com. If the release is unavailable, try again later.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new AcquisitionException("Wayfarer timed out while checking the official release on GitHub.", "Check your internet connection and access to github.com.");
        }
        catch (InvalidDataException) { throw InvalidMetadata(); }
        try { return PublicRelease.Parse(bytes, selector); }
        catch (Exception) { throw InvalidMetadata(); }
    }

    /// <summary>Malformed official metadata cannot authorize a fallback release or weaker integrity checks.</summary>
    private static AcquisitionException InvalidMetadata() => new("Wayfarer could not validate the official release information.",
        "Do not bypass validation. Try again later using an official Wayfarer release with a deployment download for this computer.");

    /// <summary>Redirect authority is restricted to GitHub's release-asset CDN, with HTTPS and no credentials.</summary>
    internal static bool AssetRedirect(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host == "release-assets.githubusercontent.com" && uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal);

    /// <summary>Retries restart only our own private partial file; published size/digest contradictions fail immediately.</summary>
    internal static async Task DownloadAsync(HttpClient client, PublicRelease release, string path, CancellationToken token,
        Action<string>? progress = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var created = false;
        TimeSpan? retryAfter = null;
        try
        {
            await RetryAsync(async () =>
            {
                if (created) { File.Delete(path); created = false; }
                retryAfter = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                var uri = new Uri(PublicRelease.Repository + "/releases/download/" + release.Tag + "/" + release.Asset);
                using var initial = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                HttpResponseMessage response = initial;
                if (initial.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
                {
                    var location = initial.Headers.Location;
                    if (location is null || !location.IsAbsoluteUri || !AssetRedirect(location)) throw new InvalidDataException("Untrusted asset redirect.");
                    response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                }
                using (response)
                {
                    retryAfter = RetryAfter(response);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength is { } size && size != release.Size) throw new InvalidDataException("Asset size changed.");
                    using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var output = new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, UnixCreateMode = ProtectedFiles.PrivateFile
                    });
                    created = true;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var count = await CopyAsync(input, output, release.Size, timeout.Token, hash, transport: true);
                    progress?.Invoke("Verifying the download...");
                    if (count != release.Size || "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Digest)
                        throw new InvalidDataException("Public asset size/SHA-256 mismatch.");
                }
                return true;
            }, "download Wayfarer from GitHub", true, () => retryAfter, token, progress, delay);
        }
        catch (HttpRequestException)
        {
            throw new AcquisitionException("Wayfarer could not download the release from GitHub.", "Check your internet connection and access to github.com.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new AcquisitionException("Wayfarer timed out while downloading the release from GitHub.", "Check your internet connection and access to github.com.");
        }
        catch (InvalidDataException)
        {
            throw new AcquisitionException("The downloaded Wayfarer release did not match its published integrity information, or its download location was unsafe.",
                "Do not bypass this check. Try again later using an official Wayfarer release.");
        }
        catch (IOException)
        {
            throw new AcquisitionException("Wayfarer could not write the private download files.", "Check available disk space and protected folder permissions.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new AcquisitionException("Wayfarer could not access the private download files.", "Check protected folder permissions.");
        }
    }

    /// <summary>Four read-only attempts; TLS/trust failures, ordinary 404s and all local validation/mutation failures stay terminal.</summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> operation, string operationName, bool asset,
        Func<TimeSpan?> retryAfter, CancellationToken token, Action<string>? progress, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await operation(); }
            catch (Exception error) when (Transient(error, asset, token))
            {
                if (attempt == 3) throw;
                await WaitAsync(attempt, operationName, retryAfter(), token, progress, delay);
            }
        }
    }

    /// <summary>Only acquisition transport/timeouts or selected HTTP statuses qualify; metadata 404 is never propagation.</summary>
    private static bool Transient(Exception error, bool asset, CancellationToken token) => !token.IsCancellationRequested &&
        (error is OperationCanceledException || error is HttpRequestException http &&
            (http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
             asset && http.StatusCode == HttpStatusCode.NotFound || http.StatusCode is null &&
                http.HttpRequestError is HttpRequestError.Unknown or HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded));

    /// <summary>Server delay suggestions cannot extend a retry beyond thirty seconds.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var value = header?.Delta ?? (header?.Date - DateTimeOffset.UtcNow);
        return value is { } duration && duration >= TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds, 30)) : null;
    }

    /// <summary>Installer-owned bounded backoff remains cancellable; tests replace only the wait.</summary>
    private static async Task WaitAsync(int attempt, string operationName, TimeSpan? retryAfter, CancellationToken token,
        Action<string>? progress, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        progress?.Invoke($"Could not {operationName}. Retrying ({attempt + 2}/4)...");
        var duration = retryAfter ?? TimeSpan.FromSeconds(new[] { 2, 5, 10 }[attempt]);
        await (delay ?? Task.Delay)(duration, token);
    }

    /// <summary>Cleanup owns only one private tree and never replaces the original failure or removes release-placement evidence.</summary>
    private static void Cleanup(string stage, Exception? primary)
    {
        try { Directory.Delete(stage, true); }
        catch when (primary is not null) { Console.Error.WriteLine("Private acquisition cleanup was incomplete; original failure and retained evidence preserved."); }
        catch (Exception)
        {
            throw new AcquisitionException("Wayfarer could not finish cleaning its private download files. Retained releases were preserved.",
                "Check disk space and protected folder permissions.");
        }
    }

    /// <summary>Bound streamed bytes and stalled reads; only response-read I/O is classified as retryable transport.</summary>
    internal static async Task<long> CopyAsync(Stream input, Stream output, long limit, CancellationToken token,
        IncrementalHash? hash = null, bool transport = false)
    {
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            int count;
            try { count = await input.ReadAsync(buffer, readTimeout.Token); }
            catch (IOException error) when (transport) { throw new HttpRequestException(HttpRequestError.ConnectionError, "Public response interrupted.", error); }
            if (count == 0) return total;
            total = checked(total + count);
            if (total > limit) throw new InvalidDataException("Public input exceeds byte bound.");
            hash?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
