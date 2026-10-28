using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Wayfarer.Models;
using Wayfarer.Models.Enums;

namespace Wayfarer.Services.LocationImports;

/// <summary>Immutable client metadata; syntax is checked before current upload policy or body reads.</summary>
public sealed record ManagedImportFingerprint(string Format, long Bytes, string Sha256, bool EnrichmentRequested)
{
    internal LocationImportFileType FileType => Format == "csv" ? LocationImportFileType.Csv : LocationImportFileType.WayfarerGeoJson;
    internal bool Matches(MobileLocationImportUpload row) => row.Format == Format && row.ByteLength == Bytes
        && row.Sha256 == Sha256 && row.EnrichmentRequested == EnrichmentRequested;
}

/// <summary>Authority-neutral observation of crash windows at the production storage/commit seam.</summary>
internal interface IManagedImportObserver
{
    Task BeforeFirstOpenAsync(ApplicationDbContext db, CancellationToken token) => Task.CompletedTask;
    Task AfterCloseAsync(CancellationToken token) => Task.CompletedTask;
    Task AfterAcceptanceAsync(CancellationToken token) => Task.CompletedTask;
    Task AfterUnlinkAsync(CancellationToken token) => Task.CompletedTask;
}

/// <summary>Owns bounded raw transfer and joint stopped-job acceptance; exceptions never authorize unlink.</summary>
public sealed class ManagedImportUpload(
    IDbContextFactory<ApplicationDbContext> contexts, LocationImportStagedFiles files, ManagedImportStorageGate gate)
{
    private static readonly ApiWorkAdmission Transfers = new(4, 4);
    internal IManagedImportObserver? Observer { get; set; }

    /// <summary>Allocates durable custody, writes one handle, then commits the job and request together.</summary>
    public async Task<(Guid Id, bool Created)> AcceptAsync(string userId, string credential, Guid requestId,
        ManagedImportFingerprint fingerprint, long effectiveLimit, Stream body, CancellationToken token)
    {
        using var permit = Transfers.TryAcquire(userId, out var denied);
        if (permit is null) throw new ManagedImportException(denied, "upload_capacity");
        var (attempt, allocated) = await ReserveAsync(userId, credential, requestId, fingerprint, effectiveLimit, token);
        if (!allocated)
            return (attempt.Id, false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(600));
        try
        {
            await using (var stream = await FirstOpenAsync(attempt, deadline.Token))
            {
                await CopyAsync(attempt, stream, body, fingerprint, credential, deadline.Token);
                await stream.FlushAsync(deadline.Token);
                stream.Flush(flushToDisk: true);
            }
            if (Observer is not null) await Observer.AfterCloseAsync(deadline.Token);
            await PublishAsync(attempt, credential, deadline.Token);
            if (Observer is not null) await Observer.AfterAcceptanceAsync(deadline.Token);
            return (attempt.Id, true);
        }
        catch
        {
            // No exception-side File.Delete. Claim rechecks committed acceptance under the publication fence.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await AbortAsync(attempt, recovery.Token); }
            catch { /* Unknown DB outcome retains custody and reservation for recurring recovery. */ }
            throw;
        }
    }

    private async Task<(MobileLocationImportUpload Row, bool Allocated)> ReserveAsync(string userId, string credential, Guid requestId,
        ManagedImportFingerprint fingerprint, long limit, CancellationToken token)
    {
        using var budget = ManagedImportAuthority.Budget(token);
        token = budget.Token;
        using var storage = gate.TryAcquire() ?? throw new ManagedImportException(503, "managed_store_unavailable");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, userId, token);
        await RequireStoreAsync(db, storage.StoreIdentity, token);
        await ManagedImportAuthority.RequireCredentialAsync(db, userId, credential, token);
        var row = await db.Set<MobileLocationImportUpload>().SingleOrDefaultAsync(
            item => item.UserId == userId && item.RequestId == requestId, token);
        if (row is not null)
        {
            if (!fingerprint.Matches(row)) throw new ManagedImportException(409, "upload_request_conflict");
            if (row.AcceptedAtUtc.HasValue || row.FileState != ManagedImportFileState.Removed) return (row, false);
        }
        if (fingerprint.EnrichmentRequested) throw new ManagedImportException(503, "enrichment_integration_pending");
        if (limit == 0) throw new ManagedImportException(403, "uploads_disabled");
        if (fingerprint.Bytes > limit) throw new ManagedImportException(413, "upload_too_large");
        var now = await ManagedImportAuthority.NowAsync(db, token);
        if (row is null)
        {
            if (await db.Set<MobileLocationImportUpload>().CountAsync(item => item.UserId == userId
                && item.CreatedAtUtc > now.AddDays(-1), token) >= 100)
                throw new ManagedImportException(429, "upload_identity_limit");
            row = new() { UserId = userId, RequestId = requestId, Format = fingerprint.Format,
                ByteLength = fingerprint.Bytes, Sha256 = fingerprint.Sha256,
                EnrichmentRequested = fingerprint.EnrichmentRequested, CreatedAtUtc = now };
            db.Add(row);
        }
        var owned = db.Set<MobileLocationImportUpload>().Where(item => item.UserId == userId
            && item.FileState != ManagedImportFileState.Removed);
        if (await owned.AnyAsync(item => item.FileState == ManagedImportFileState.Uploading, token))
            throw new ManagedImportException(409, "upload_active");
        if (await owned.CountAsync(token) >= 3) throw new ManagedImportException(409, "managed_file_limit");
        if ((await owned.SumAsync(item => item.ReservedBytes, token)) + fingerprint.Bytes > 3 * limit
            || (await db.Set<MobileLocationImportUpload>().SumAsync(item => item.ReservedBytes, token)) + fingerprint.Bytes > 10L * 1024 * 1024 * 1024)
            throw new ManagedImportException(409, "managed_byte_limit");
        row.Generation = checked(row.Generation + 1);
        row.FileReference = LocationImportStagedFiles.CreateReference(fingerprint.FileType);
        row.FileState = ManagedImportFileState.Uploading;
        row.ReservedBytes = fingerprint.Bytes;
        row.OwnerByteLimit = 3 * limit;
        row.LeaseId = Guid.NewGuid();
        row.LeaseExpiresAtUtc = now.AddSeconds(60);
        row.UploadDeadlineUtc = now.AddSeconds(600);
        row.CleanupReason = row.FailureCode = null;
        row.RetryAtUtc = row.LeaseExpiresAtUtc;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token); // Uncertainty here means no first-open by this invocation.
        return (row, true);
    }

    /// <summary>The single synchronous CreateNew remains inside the OS gate, including the DB-loss instruction window.</summary>
    internal async Task<FileStream> FirstOpenAsync(MobileLocationImportUpload attempt, CancellationToken token)
    {
        using var budget = ManagedImportAuthority.Budget(token);
        token = budget.Token;
        if (attempt.LeaseId is null || attempt.UserId is null) throw new ManagedImportException(409, "upload_authority_lost");
        using var storage = gate.TryAcquire() ?? throw new ManagedImportException(503, "managed_store_unavailable");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, attempt.UserId, token);
        await RequireStoreAsync(db, storage.StoreIdentity, token);
        var current = await ManagedImportAuthority.FindAsync(db, attempt.Id, token);
        var now = await ManagedImportAuthority.NowAsync(db, token);
        if (!ManagedImportAuthority.Live(current, attempt.Generation, attempt.LeaseId!.Value, now)
            || current!.FileReference != attempt.FileReference
            || !await db.Users.AnyAsync(user => user.Id == attempt.UserId && user.IsActive, token))
            throw new ManagedImportException(409, "upload_authority_lost");
        if (!files.TryResolve(current.FileReference, out var path)) throw new ManagedImportException(503, "managed_store_unavailable");
        Directory.CreateDirectory(files.DirectoryPath);
        if (Observer is not null) await Observer.BeforeFirstOpenAsync(db, token);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { await transaction.CommitAsync(token); return stream; }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async Task CopyAsync(MobileLocationImportUpload attempt, FileStream output, Stream input,
        ManagedImportFingerprint fingerprint, string credential, CancellationToken token)
    {
        var buffer = new byte[65536];
        using var prefix = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        var renewed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var size = await input.ReadAsync(buffer, token);
            if (size == 0) break;
            count += size;
            if (count > fingerprint.Bytes) throw new ManagedImportException(413, "upload_too_large");
            if (prefix.Length < 65536) prefix.Write(buffer, 0, Math.Min(size, 65536 - (int)prefix.Length));
            hash.AppendData(buffer, 0, size);
            await output.WriteAsync(buffer.AsMemory(0, size), token);
            if (renewed.Elapsed < TimeSpan.FromSeconds(20)) continue;
            await RenewAsync(attempt, credential, token);
            renewed.Restart();
        }
        if (count != fingerprint.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != fingerprint.Sha256)
            throw new ManagedImportException(400, "upload_integrity_mismatch");
        ValidateEnvelope(prefix.ToArray(), fingerprint.Format);
    }

    private async Task RenewAsync(MobileLocationImportUpload attempt, string credential, CancellationToken token)
    {
        using var budget = ManagedImportAuthority.Budget(token);
        token = budget.Token;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, attempt.UserId, token);
        await ManagedImportAuthority.RequireCredentialAsync(db, attempt.UserId!, credential, token);
        var row = await ManagedImportAuthority.FindAsync(db, attempt.Id, token);
        var now = await ManagedImportAuthority.NowAsync(db, token);
        if (!ManagedImportAuthority.Live(row, attempt.Generation, attempt.LeaseId!.Value, now))
            throw new ManagedImportException(409, "upload_authority_lost");
        row!.LeaseExpiresAtUtc = new[] { now.AddSeconds(60), row.UploadDeadlineUtc!.Value }.Min();
        row.RetryAtUtc = row.LeaseExpiresAtUtc;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task PublishAsync(MobileLocationImportUpload attempt, string credential, CancellationToken token)
    {
        using var budget = ManagedImportAuthority.Budget(token);
        token = budget.Token;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, attempt.UserId, token);
        await ManagedImportAuthority.RequireCredentialAsync(db, attempt.UserId!, credential, token);
        var row = await ManagedImportAuthority.FindAsync(db, attempt.Id, token);
        var now = await ManagedImportAuthority.NowAsync(db, token);
        if (!ManagedImportAuthority.Live(row, attempt.Generation, attempt.LeaseId!.Value, now))
            throw new ManagedImportException(409, "upload_authority_lost");
        var import = new LocationImport
        {
            UserId = row!.UserId!, FilePath = row.FileReference!, FileType = row.Format == "csv" ? LocationImportFileType.Csv : LocationImportFileType.WayfarerGeoJson,
            ManagedUpload = true, TotalRecords = 0, LastProcessedIndex = 0, Status = ImportStatus.Stopped,
            EnrichmentRequested = row.EnrichmentRequested, EnrichmentRequestedAtUtc = row.EnrichmentRequested ? now : null
        };
        row.Import = import;
        row.AcceptedAtUtc = now;
        row.FileState = ManagedImportFileState.Retained;
        row.VerifiedBytes = row.ByteLength!.Value;
        row.RetryAtUtc = now.AddMinutes(5);
        row.LeaseId = null;
        row.LeaseExpiresAtUtc = null;
        ManagedImportAuthority.Remember(row, import);
        db.Add(import);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    /// <summary>Abort is a conditional claim, so a lost successful acceptance reply cannot erase its payload.</summary>
    internal async Task AbortAsync(MobileLocationImportUpload attempt, CancellationToken token)
    {
        using var budget = ManagedImportAuthority.Budget(token);
        token = budget.Token;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await ManagedImportAuthority.BeginAsync(db, attempt.UserId, token);
        var row = await ManagedImportAuthority.FindAsync(db, attempt.Id, token);
        if (row is { AcceptedAtUtc: null, FileState: ManagedImportFileState.Uploading }
            && row.Generation == attempt.Generation && row.LeaseId == attempt.LeaseId)
        {
            row.FileState = ManagedImportFileState.CleanupClaimed;
            row.CleanupReason = "upload_aborted";
            row.LeaseId = null;
            row.LeaseExpiresAtUtc = null;
            row.RetryAtUtc = await ManagedImportAuthority.NowAsync(db, token);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    /// <summary>Store identity is installation-wide, independent of payload/account lifetimes.</summary>
    internal static async Task RequireStoreAsync(ApplicationDbContext db, string identity, CancellationToken token)
    {
        var store = await db.Set<ManagedImportStore>().SingleOrDefaultAsync(token);
        if (store is null) { db.Add(new ManagedImportStore { Identity = identity }); await db.SaveChangesAsync(token); }
        else if (store.Identity != identity) throw new ManagedImportException(503, "managed_store_unavailable");
    }

    /// <summary>Only a bounded native header/envelope is inspected; record validation stays in existing parsers.</summary>
    private static void ValidateEnvelope(byte[] bytes, string format)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            if (format == "csv")
            {
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { BadDataFound = null });
                if (!csv.Read() || !csv.ReadHeader()) throw new FormatException();
                var header = csv.HeaderRecord!;
                if (new[] { "Latitude", "Longitude", "TimestampUtc" }.All(name => header.Contains(name))) return;
            }
            else
            {
                using var json = new JsonTextReader(reader) { MaxDepth = 32, DateParseHandling = DateParseHandling.None };
                if (!json.Read() || json.TokenType != JsonToken.StartObject) throw new FormatException();
                var collection = false;
                while (json.Read())
                {
                    if (json.TokenType != JsonToken.PropertyName || json.Depth != 1) continue;
                    var property = (string?)json.Value;
                    if (!json.Read()) break;
                    if (property == "type") collection = (string?)json.Value == "FeatureCollection";
                    if (property == "features" && collection && json.TokenType == JsonToken.StartArray) return;
                    json.Skip();
                }
            }
        }
        catch (Exception error) when (error is FormatException or JsonException or CsvHelperException or DecoderFallbackException) { }
        throw new ManagedImportException(400, "invalid_file_envelope");
    }
}
