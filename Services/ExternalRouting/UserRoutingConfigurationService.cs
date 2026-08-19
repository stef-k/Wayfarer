using Microsoft.EntityFrameworkCore;
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
        var configuration = await _dbContext.Set<UserRoutingConfiguration>()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (configuration == null) return UserRoutingMutationResult.NotFound;
        if (configuration.RowVersion != expectedRowVersion) return UserRoutingMutationResult.Conflict;
        if (providerId == null)
        {
            configuration.UseServerDefault();
            Audit(userId, "UserRoutingDefault", null, "server-default selected");
        }
        else
        {
            var provider = await _dbContext.Set<RoutingProviderConfiguration>().Include(item => item.ProfileMappings)
                .ThenInclude(item => item.TransportProfile).SingleOrDefaultAsync(item => item.Id == providerId, cancellationToken);
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
            {
                configuration.InvalidateVerification();
            }
            else
            {
                _credentials.Replace(configuration, provider.Id, credential);
            }
            Audit(userId, "UserRoutingSelection", provider.Id, "approved template selected");
        }
        try { await _dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return UserRoutingMutationResult.Conflict; }
        return UserRoutingMutationResult.Success;
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
