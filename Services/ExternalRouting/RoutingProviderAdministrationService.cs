using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns allowlisted provider mutation, credential clearing, and global feature state.</summary>
public sealed class RoutingProviderAdministrationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly RoutingProviderCredentialService _credentials;

    /// <summary>Initializes focused provider administration.</summary>
    public RoutingProviderAdministrationService(ApplicationDbContext dbContext, RoutingProviderCredentialService credentials)
        => (_dbContext, _credentials) = (dbContext, credentials);

    /// <summary>Creates or updates one OSRM configuration while invalidating changed versions.</summary>
    public async Task<RoutingAdministrationResult> SaveAsync(
        RoutingProviderEditViewModel model, string administratorId, CancellationToken cancellationToken)
    {
        if (!TryNormalizeEndpoint(model.BaseEndpoint, out var endpoint))
            return RoutingAdministrationResult.Failure("The endpoint is malformed or contains unsupported URL parts.");
        var selectedMappings = model.Mappings.Where(item => !string.IsNullOrWhiteSpace(item.OsrmProfile)).ToArray();
        if (selectedMappings.Select(item => item.TransportProfileId).Distinct().Count() != selectedMappings.Length)
            return RoutingAdministrationResult.Failure("Each transport profile may be mapped only once.");
        var activeProfileIds = await _dbContext.Set<TransportProfile>().AsNoTracking()
            .Where(item => item.IsActive && selectedMappings.Select(mapping => mapping.TransportProfileId).Contains(item.Id))
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        if (activeProfileIds.Length != selectedMappings.Length)
            return RoutingAdministrationResult.Failure("Every provider mapping must reference an active transport profile.");
        var provider = model.Id == Guid.Empty ? null : await _dbContext.Set<RoutingProviderConfiguration>()
            .Include(item => item.ProfileMappings).SingleOrDefaultAsync(item => item.Id == model.Id, cancellationToken);
        var creating = provider == null && model.Id == Guid.Empty;
        if (!creating && provider == null) return RoutingAdministrationResult.Failure("The provider configuration was not found.");
        if (creating)
        {
            provider = new RoutingProviderConfiguration { Id = Guid.NewGuid(), ConfigurationVersion = 1 };
            _dbContext.Set<RoutingProviderConfiguration>().Add(provider);
        }
        else if (provider!.RowVersion != model.RowVersion)
            return RoutingAdministrationResult.Failure("The provider configuration changed. Reload and try again.");

        var normalizedMappings = selectedMappings.OrderBy(item => item.TransportProfileId)
            .Select(item => (item.TransportProfileId, Profile: item.OsrmProfile!.Trim())).ToArray();
        var existingMappings = provider!.ProfileMappings.OrderBy(item => item.TransportProfileId)
            .Select(item => (item.TransportProfileId, Profile: item.OsrmProfile)).ToArray();
        var changed = !creating && (provider.BaseEndpoint != endpoint
            || provider.CredentialRequired != model.CredentialRequired
            || provider.VerificationFromLongitude != model.VerificationFromLongitude
            || provider.VerificationFromLatitude != model.VerificationFromLatitude
            || provider.VerificationToLongitude != model.VerificationToLongitude
            || provider.VerificationToLatitude != model.VerificationToLatitude
            || provider.GenerationTimeoutSeconds != model.GenerationTimeoutSeconds
            || provider.ResponseSizeLimitBytes != model.ResponseSizeLimitBytes
            || provider.RequestsPerMinute != model.RequestsPerMinute || provider.MaxConcurrency != model.MaxConcurrency
            || !existingMappings.SequenceEqual(normalizedMappings));

        provider.DisplayName = model.DisplayName.Trim();
        provider.AdapterType = RoutingAdapterType.OsrmCompatible;
        provider.BaseEndpoint = endpoint;
        provider.CredentialRequired = model.CredentialRequired;
        provider.Enabled = model.Enabled;
        provider.Attribution = Normalize(model.Attribution);
        provider.ExternalCoordinateDisclosure = model.ExternalCoordinateDisclosure.Trim();
        provider.VerificationFromLongitude = model.VerificationFromLongitude;
        provider.VerificationFromLatitude = model.VerificationFromLatitude;
        provider.VerificationToLongitude = model.VerificationToLongitude;
        provider.VerificationToLatitude = model.VerificationToLatitude;
        provider.GenerationTimeoutSeconds = model.GenerationTimeoutSeconds;
        provider.ResponseSizeLimitBytes = model.ResponseSizeLimitBytes;
        provider.RequestsPerMinute = model.RequestsPerMinute;
        provider.MaxConcurrency = model.MaxConcurrency;
        if (creating || !existingMappings.SequenceEqual(normalizedMappings))
        {
            provider.ProfileMappings.Clear();
            foreach (var mapping in normalizedMappings)
                provider.ProfileMappings.Add(new RoutingProviderProfileMapping
                {
                    RoutingProviderConfigurationId = provider.Id, TransportProfileId = mapping.TransportProfileId,
                    OsrmProfile = mapping.Profile
                });
        }
        if (changed) provider.MarkConfigurationChanged();
        _credentials.ApplyEdit(provider, model.Credential);
        AddAudit(administratorId, creating ? "RoutingProviderCreate" : "RoutingProviderUpdate", provider.Id,
            changed || !string.IsNullOrWhiteSpace(model.Credential) ? "configuration changed; verification invalidated" : "metadata preserved");
        try { await _dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return RoutingAdministrationResult.Failure("The provider configuration changed. Reload and try again."); }
        return new RoutingAdministrationResult(true, null, provider.Id);
    }

    /// <summary>Clears a credential only through an explicit confirmed action.</summary>
    public async Task<RoutingAdministrationResult> ClearCredentialAsync(
        Guid providerId, bool confirmed, bool disableRouting, string administratorId, CancellationToken cancellationToken)
    {
        if (!confirmed) return RoutingAdministrationResult.Failure("Confirm credential clearing.");
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var settings = await LockSettingsAsync(cancellationToken);
            var provider = await LockProviderAsync(providerId, cancellationToken);
            if (settings == null || provider == null)
                return RoutingAdministrationResult.Failure("The provider configuration was not found.");
            if (settings.ExternalRouteGenerationEnabled && settings.ActiveRoutingProviderConfigurationId == providerId
                && provider.CredentialRequired && !disableRouting)
                return RoutingAdministrationResult.Failure("Disable external routing atomically before clearing this required active credential.");
            if (disableRouting && settings.ExternalRouteGenerationEnabled)
            {
                settings.ExternalRouteGenerationEnabled = false;
                settings.ExternalRouteGenerationVersion = checked(settings.ExternalRouteGenerationVersion + 1);
            }
            provider.CredentialCiphertext = null;
            provider.CredentialPresent = false;
            provider.MarkConfigurationChanged();
            AddAudit(administratorId, "RoutingProviderCredentialClear", providerId, "credential cleared; no secret value recorded");
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new RoutingAdministrationResult(true, null, providerId);
        }
        catch (DbUpdateConcurrencyException) { _dbContext.ChangeTracker.Clear(); return RoutingAdministrationResult.Failure("The provider configuration changed. Reload and try again."); }
        catch (Exception exception) when (IsSerializationFailure(exception))
        { _dbContext.ChangeTracker.Clear(); return RoutingAdministrationResult.Failure("The provider configuration changed. Reload and try again."); }
    }

    /// <summary>Enables only a verified selected provider or explicitly disables while retaining selection.</summary>
    public async Task<RoutingAdministrationResult> SetFeatureEnabledAsync(
        bool enabled, uint expectedSettingsRowVersion, string administratorId, CancellationToken cancellationToken)
    {
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var settings = await LockSettingsAsync(cancellationToken);
            if (settings == null || settings.RowVersion != expectedSettingsRowVersion)
                return RoutingAdministrationResult.Failure("Application settings changed. Reload and try again.");
            var provider = settings.ActiveRoutingProviderConfigurationId is { } providerId
                ? await LockProviderAsync(providerId, cancellationToken) : null;
            if (enabled && (provider is not { Enabled: true }
                || provider.VerifiedConfigurationVersion != provider.ConfigurationVersion
                || provider.CredentialRequired && (!provider.CredentialPresent || provider.CredentialCiphertext == null)
                || provider.ProfileMappings.Count == 0
                || provider.ProfileMappings.Any(mapping => mapping.TransportProfile is not { IsActive: true })))
                return RoutingAdministrationResult.Failure("Select and verify an enabled provider before enabling external routing.");
            if (settings.ExternalRouteGenerationEnabled != enabled)
            {
                settings.ExternalRouteGenerationEnabled = enabled;
                settings.ExternalRouteGenerationVersion = checked(settings.ExternalRouteGenerationVersion + 1);
            }
            AddAudit(administratorId, "ExternalRouteGenerationFeature", settings.ActiveRoutingProviderConfigurationId,
                enabled ? "enabled" : "disabled");
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new RoutingAdministrationResult(true, null);
        }
        catch (DbUpdateConcurrencyException) { _dbContext.ChangeTracker.Clear(); return RoutingAdministrationResult.Failure("Application settings changed. Reload and try again."); }
        catch (Exception exception) when (IsSerializationFailure(exception))
        { _dbContext.ChangeTracker.Clear(); return RoutingAdministrationResult.Failure("Application settings changed. Reload and try again."); }
    }

    private Task<ApplicationSettings?> LockSettingsAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.IsNpgsql()
            ? _dbContext.ApplicationSettings.FromSqlRaw("SELECT *, xmin FROM \"ApplicationSettings\" WHERE \"Id\" = 1 FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
            : _dbContext.ApplicationSettings.SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);

    private Task<RoutingProviderConfiguration?> LockProviderAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var query = _dbContext.Database.IsNpgsql()
            ? _dbContext.Set<RoutingProviderConfiguration>().FromSqlInterpolated($"SELECT *, xmin FROM \"RoutingProviderConfigurations\" WHERE \"Id\" = {providerId} FOR UPDATE")
            : _dbContext.Set<RoutingProviderConfiguration>().Where(item => item.Id == providerId);
        return query.Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static bool IsSerializationFailure(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
        || exception.InnerException != null && IsSerializationFailure(exception.InnerException);

    private void AddAudit(string userId, string action, Guid? providerId, string result) => _dbContext.AuditLogs.Add(new AuditLog
    {
        UserId = userId, Action = action, Timestamp = DateTime.UtcNow,
        Details = $"ProviderId={providerId?.ToString() ?? "none"}; {result}."
    });

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryNormalizeEndpoint(string value, out string endpoint)
    {
        endpoint = string.Empty;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Contains('*')) return false;
        endpoint = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}

/// <summary>Contains a bounded administration outcome.</summary>
public sealed record RoutingAdministrationResult(bool Succeeded, string? Error, Guid? ProviderId = null)
{
    /// <summary>Creates a safe administrative failure.</summary>
    public static RoutingAdministrationResult Failure(string error) => new(false, error);
}
