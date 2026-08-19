using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Owns current-user selection and credential mutations.</summary>
public sealed class UserRoutingConfigurationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserRoutingCredentialService _credentials;

    /// <summary>Initializes current-user routing configuration mutations.</summary>
    public UserRoutingConfigurationService(ApplicationDbContext dbContext, UserRoutingCredentialService credentials) =>
        (_dbContext, _credentials) = (dbContext, credentials);

    /// <summary>Selects an eligible template or server-default mode with optimistic concurrency.</summary>
    public async Task<UserRoutingMutationResult> SaveAsync(
        string userId, Guid? providerId, string? credential, uint expectedRowVersion, CancellationToken cancellationToken)
    {
        if (credential?.Length > 2000)
            return UserRoutingMutationResult.Invalid("The personal credential is too long.");
        if (!await _dbContext.ApplicationSettings.AsNoTracking()
                .AnyAsync(item => item.Id == 1 && item.ExternalRouteGenerationEnabled, cancellationToken))
            return UserRoutingMutationResult.Invalid("Personal routing settings are unavailable.");
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var provider = providerId is { } selectedProviderId
                ? await LockProviderAsync(selectedProviderId, cancellationToken) : null;
            var configuration = await LockUserAsync(userId, cancellationToken);
            if (configuration == null) return UserRoutingMutationResult.NotFound;
            if (configuration.RowVersion != expectedRowVersion) return UserRoutingMutationResult.Conflict;
            if (providerId == null)
            {
                configuration.UseServerDefault();
                Audit(userId, "UserRoutingDefault", null, "server-default selected");
            }
            else
            {
                if (provider == null || !PersonalRoutingEligibility.Evaluate(provider).Eligible)
                    return UserRoutingMutationResult.NotFound;
                if (provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialFree
                    && !string.IsNullOrWhiteSpace(credential))
                    return UserRoutingMutationResult.Invalid("This template does not accept a personal credential.");
                if (provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialRequired
                    && string.IsNullOrWhiteSpace(credential)
                    && (configuration.SelectedProviderConfigurationId != provider.Id || !configuration.CredentialPresent))
                    return UserRoutingMutationResult.Invalid("Enter a personal credential for this template.");
                configuration.SelectPersonalProvider(provider.Id);
                if (provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialFree)
                    configuration.NormalizeCredentialFree();
                else
                    _credentials.Replace(configuration, provider.Id, credential);
                Audit(userId, "UserRoutingSelection", provider.Id, "approved template selected");
            }
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return UserRoutingMutationResult.Success;
        }
        catch (DbUpdateConcurrencyException)
        {
            _dbContext.ChangeTracker.Clear();
            return UserRoutingMutationResult.Conflict;
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            _dbContext.ChangeTracker.Clear();
            return UserRoutingMutationResult.Conflict;
        }
    }

    /// <summary>Clears only the current user's credential after confirmation.</summary>
    public async Task<UserRoutingMutationResult> ClearAsync(
        string userId, bool confirmed, uint expectedRowVersion, CancellationToken cancellationToken)
    {
        if (!confirmed) return UserRoutingMutationResult.Invalid("Confirm credential clearing.");
        var configuration = await _dbContext.Set<UserRoutingConfiguration>()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (configuration == null) return UserRoutingMutationResult.NotFound;
        if (configuration.RowVersion != expectedRowVersion) return UserRoutingMutationResult.Conflict;
        if (!_credentials.Clear(configuration, true))
            return UserRoutingMutationResult.Invalid("No personal credential is stored.");
        Audit(userId, "UserRoutingCredentialClear", configuration.SelectedProviderConfigurationId, "credential cleared");
        try { await _dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return UserRoutingMutationResult.Conflict; }
        return UserRoutingMutationResult.Success;
    }

    private void Audit(string userId, string action, Guid? providerId, string result) => _dbContext.AuditLogs.Add(new AuditLog
    {
        UserId = userId, Action = action, Timestamp = DateTime.UtcNow,
        Details = $"ProviderId={providerId?.ToString() ?? "none"}; {result}."
    });

    private Task<RoutingProviderConfiguration?> LockProviderAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var query = _dbContext.Database.IsNpgsql()
            ? _dbContext.Set<RoutingProviderConfiguration>().FromSqlInterpolated(
                $"SELECT *, xmin FROM \"RoutingProviderConfigurations\" WHERE \"Id\" = {providerId} FOR UPDATE")
            : _dbContext.Set<RoutingProviderConfiguration>().Where(item => item.Id == providerId);
        return query.Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private Task<UserRoutingConfiguration?> LockUserAsync(string userId, CancellationToken cancellationToken) =>
        _dbContext.Database.IsNpgsql()
            ? _dbContext.Set<UserRoutingConfiguration>().FromSqlInterpolated(
                $"SELECT *, xmin FROM \"UserRoutingConfigurations\" WHERE \"UserId\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : _dbContext.Set<UserRoutingConfiguration>().SingleOrDefaultAsync(
                item => item.UserId == userId, cancellationToken);

    private static bool IsSerializationFailure(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
        || exception.InnerException != null && IsSerializationFailure(exception.InnerException);
}

/// <summary>Contains a bounded user-routing mutation outcome.</summary>
public sealed record UserRoutingMutationResult(bool Succeeded, bool Missing, string? Error)
{
    /// <summary>Gets the successful result.</summary>
    public static UserRoutingMutationResult Success { get; } = new(true, false, null);
    /// <summary>Gets the non-disclosing missing result.</summary>
    public static UserRoutingMutationResult NotFound { get; } = new(false, true, null);
    /// <summary>Gets the bounded concurrency result.</summary>
    public static UserRoutingMutationResult Conflict { get; } = new(false, false, "Routing settings changed. Reload and try again.");
    /// <summary>Creates a bounded validation result.</summary>
    public static UserRoutingMutationResult Invalid(string error) => new(false, false, error);
}
