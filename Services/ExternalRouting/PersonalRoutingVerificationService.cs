using System.Data;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Verifies required personal credentials without holding database locks during provider contact.</summary>
public sealed class PersonalRoutingVerificationService(
    ApplicationDbContext dbContext, RoutingBoundedExecutor executor, AuthoritativeRoutingProviderResolver resolver,
    RoutingAttemptCoordinator attempts)
{
    private const int MaximumProfiles = 8;

    /// <summary>Gets the deterministic short-commit lock order.</summary>
    public static IReadOnlyList<PersonalRoutingVerificationLock> CommitLockOrder { get; } =
        [PersonalRoutingVerificationLock.Provider, PersonalRoutingVerificationLock.UserRoutingConfiguration];

    /// <summary>Probes the selected template and persists success only for unchanged authority.</summary>
    public async Task<PersonalRoutingVerificationResult> VerifyAsync(
        string userId, uint expectedUserRowVersion, CancellationToken cancellationToken)
    {
        var snapshot = await LoadSnapshotAsync(userId, cancellationToken);
        if (snapshot == null || snapshot.UserRowVersion != expectedUserRowVersion)
            return PersonalRoutingVerificationResult.Failure("personal-routing-stale");
        var resolution = await resolver.ResolveForVerificationAsync(userId, snapshot.ProfileIds[0], cancellationToken);
        if (resolution is not { Outcome: RoutingProviderResolutionOutcome.ResolvedPersonal, Execution: { } target })
            return PersonalRoutingVerificationResult.Failure("personal-credential-unavailable");

        foreach (var profile in snapshot.Profiles)
        {
            var request = OsrmRoutingAdapter.BuildRelativeRequest(profile, [snapshot.From, snapshot.To]);
            var execution = await executor.GetJsonAsync(snapshot.Endpoint, request, snapshot.ResponseSizeLimitBytes,
                TimeSpan.FromSeconds(5), cancellationToken, target.Credential,
                prepareAttempt: token => attempts.PrepareAsync(snapshot.Provider,
                    inner => IsCurrentAsync(snapshot, inner), token));
            if (!execution.Succeeded) return PersonalRoutingVerificationResult.Failure(execution.ErrorCode!);
            using var response = JsonResponse(execution.Json!);
            var route = await OsrmRoutingAdapter.ParseAsync(response, cancellationToken);
            if (!route.Succeeded || route.Waypoints.Count != 2 || route.Geometry.Count > 1000)
                return PersonalRoutingVerificationResult.Failure("personal-verification-invalid");
        }
        return await CommitAsync(snapshot, cancellationToken);
    }

    private async Task<PersonalVerificationSnapshot?> LoadSnapshotAsync(
        string userId, CancellationToken cancellationToken)
    {
        var configuration = await dbContext.Set<UserRoutingConfiguration>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (configuration?.SelectedProviderConfigurationId is not { } providerId
            || !configuration.CredentialPresent || configuration.CredentialCiphertext == null) return null;
        var provider = await dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(item => item.Id == providerId, cancellationToken);
        if (provider == null || provider.PersonalRoutingAccess != PersonalRoutingAccess.CredentialRequired
            || !PersonalRoutingEligibility.Evaluate(provider).Eligible
            || !TryCoordinates(provider, out var from, out var to)) return null;
        var mappings = provider.ProfileMappings.Where(item => item.TransportProfile is { IsActive: true })
            .GroupBy(item => item.OsrmProfile, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(item => item.OsrmProfile).ToArray();
        var profiles = mappings.Select(item => item.OsrmProfile).ToArray();
        if (profiles.Length is 0 or > MaximumProfiles) return null;
        var operationalProvider = provider.WithCredentialRemoved();
        return new PersonalVerificationSnapshot(userId, configuration.ConfigurationVersion, configuration.RowVersion,
            configuration.CredentialCiphertext, provider.Id, provider.ConfigurationVersion, provider.RowVersion,
            new Uri(provider.BaseEndpoint!), provider.ResponseSizeLimitBytes, profiles,
            mappings.Select(item => item.TransportProfileId).ToArray(), from, to, operationalProvider);
    }

    private async Task<bool> IsCurrentAsync(PersonalVerificationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var current = await LoadSnapshotAsync(snapshot.UserId, cancellationToken);
        return current != null && SameAuthority(snapshot, current);
    }

    private async Task<PersonalRoutingVerificationResult> CommitAsync(
        PersonalVerificationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var relational = dbContext.Database.IsRelational();
        if (relational) dbContext.ChangeTracker.Clear();
        await using var transaction = relational
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var providerQuery = relational && dbContext.Database.IsNpgsql()
                ? dbContext.Set<RoutingProviderConfiguration>().FromSqlInterpolated(
                    $"SELECT *, xmin FROM \"RoutingProviderConfigurations\" WHERE \"Id\" = {snapshot.ProviderId} FOR UPDATE")
                : dbContext.Set<RoutingProviderConfiguration>().Where(item => item.Id == snapshot.ProviderId);
            var provider = await providerQuery.Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
                .SingleOrDefaultAsync(cancellationToken);
            var userQuery = relational && dbContext.Database.IsNpgsql()
                ? dbContext.Set<UserRoutingConfiguration>().FromSqlInterpolated(
                    $"SELECT *, xmin FROM \"UserRoutingConfigurations\" WHERE \"UserId\" = {snapshot.UserId} FOR UPDATE")
                : dbContext.Set<UserRoutingConfiguration>().Where(item => item.UserId == snapshot.UserId);
            var configuration = await userQuery.SingleOrDefaultAsync(cancellationToken);
            if (provider == null || configuration == null || !Matches(snapshot, provider, configuration))
                return PersonalRoutingVerificationResult.Failure("personal-routing-stale");
            configuration.VerifiedUserConfigurationVersion = configuration.ConfigurationVersion;
            configuration.VerifiedProviderConfigurationVersion = provider.ConfigurationVersion;
            configuration.VerificationStatus = "verified";
            configuration.UpdatedAt = DateTime.UtcNow;
            dbContext.AuditLogs.Add(new AuditLog
            {
                UserId = snapshot.UserId, Action = "PersonalRoutingVerification", Timestamp = DateTime.UtcNow,
                Details = $"ProviderId={snapshot.ProviderId}; Category=success; Transition=ready-to-verified."
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new PersonalRoutingVerificationResult(true, null, configuration.ConfigurationVersion,
                provider.ConfigurationVersion, configuration.RowVersion);
        }
        catch (DbUpdateConcurrencyException) { return Stale(); }
        catch (Exception exception) when (IsSerializationFailure(exception)) { return Stale(); }
    }

    private PersonalRoutingVerificationResult Stale()
    {
        dbContext.ChangeTracker.Clear();
        return PersonalRoutingVerificationResult.Failure("personal-routing-stale");
    }

    private static bool Matches(PersonalVerificationSnapshot snapshot, RoutingProviderConfiguration provider,
        UserRoutingConfiguration configuration)
    {
        var profiles = provider.ProfileMappings.Where(item => item.TransportProfile is { IsActive: true })
            .Select(item => item.OsrmProfile).Distinct(StringComparer.Ordinal).Order().ToArray();
        return provider.ConfigurationVersion == snapshot.ProviderVersion && provider.RowVersion == snapshot.ProviderRowVersion
            && provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialRequired
            && PersonalRoutingEligibility.Evaluate(provider).Eligible && profiles.SequenceEqual(snapshot.Profiles)
            && configuration.UserId == snapshot.UserId && configuration.ConfigurationVersion == snapshot.UserVersion
            && configuration.RowVersion == snapshot.UserRowVersion
            && configuration.SelectedProviderConfigurationId == snapshot.ProviderId && configuration.CredentialPresent
            && string.Equals(configuration.CredentialCiphertext, snapshot.Ciphertext, StringComparison.Ordinal);
    }

    private static bool SameAuthority(PersonalVerificationSnapshot first, PersonalVerificationSnapshot second) =>
        first.ProviderId == second.ProviderId && first.ProviderVersion == second.ProviderVersion
        && first.ProviderRowVersion == second.ProviderRowVersion && first.UserVersion == second.UserVersion
        && first.UserRowVersion == second.UserRowVersion && first.Ciphertext == second.Ciphertext
        && first.Profiles.SequenceEqual(second.Profiles);

    private static bool TryCoordinates(
        RoutingProviderConfiguration provider, out RouteCoordinate from, out RouteCoordinate to)
    {
        from = default; to = default;
        if (provider.VerificationFromLongitude is not { } fromLongitude
            || provider.VerificationFromLatitude is not { } fromLatitude
            || provider.VerificationToLongitude is not { } toLongitude
            || provider.VerificationToLatitude is not { } toLatitude) return false;
        from = new RouteCoordinate(fromLongitude, fromLatitude);
        to = new RouteCoordinate(toLongitude, toLatitude);
        return from.IsValid && to.IsValid;
    }

    private static HttpResponseMessage JsonResponse(byte[] json) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(json) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } } };

    private static bool IsSerializationFailure(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
        || exception.InnerException != null && IsSerializationFailure(exception.InnerException);

    private sealed record PersonalVerificationSnapshot(
        string UserId, int UserVersion, uint UserRowVersion, string Ciphertext,
        Guid ProviderId, int ProviderVersion, uint ProviderRowVersion, Uri Endpoint, int ResponseSizeLimitBytes,
        string[] Profiles, Guid[] ProfileIds, RouteCoordinate From, RouteCoordinate To,
        RoutingProviderConfiguration Provider);
}

internal static class PersonalRoutingProviderSnapshotExtensions
{
    /// <summary>Copies operational limits while deliberately omitting both credential fields.</summary>
    internal static RoutingProviderConfiguration WithCredentialRemoved(this RoutingProviderConfiguration provider) => new()
    {
        Id = provider.Id, DisplayName = provider.DisplayName, BaseEndpoint = provider.BaseEndpoint,
        Enabled = provider.Enabled, ConfigurationVersion = provider.ConfigurationVersion,
        VerifiedConfigurationVersion = provider.VerifiedConfigurationVersion,
        ResponseSizeLimitBytes = provider.ResponseSizeLimitBytes, GenerationTimeoutSeconds = provider.GenerationTimeoutSeconds,
        RequestsPerMinute = provider.RequestsPerMinute, MinimumIntervalMilliseconds = provider.MinimumIntervalMilliseconds,
        MaxConcurrency = provider.MaxConcurrency
    };
}

/// <summary>Identifies rows locked during the short personal-verification commit phase.</summary>
public enum PersonalRoutingVerificationLock { Provider, UserRoutingConfiguration }

/// <summary>Contains only bounded personal-verification state.</summary>
public sealed record PersonalRoutingVerificationResult(
    bool Succeeded, string? ErrorCode, int? UserVersion = null, int? ProviderVersion = null, uint? RowVersion = null)
{
    /// <summary>Creates a bounded failure.</summary>
    public static PersonalRoutingVerificationResult Failure(string code) => new(false, code);
}
