using System.Data;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Npgsql;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Verifies then atomically selects one provider through singleton application settings.</summary>
public sealed class RoutingProviderActivationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IRoutingProviderVerifier _verifier;

    /// <summary>Initializes atomic activation.</summary>
    public RoutingProviderActivationService(ApplicationDbContext dbContext, IRoutingProviderVerifier verifier)
        => (_dbContext, _verifier) = (dbContext, verifier);

    /// <summary>Leaves the previous provider selected unless verification and every locked recheck succeeds.</summary>
    public async Task<RoutingActivationResult> VerifyAndActivateAsync(
        Guid candidateId, int expectedVersion, uint expectedProviderRowVersion, uint expectedSettingsRowVersion,
        string administratorId, CancellationToken cancellationToken)
    {
        var verification = await _verifier.VerifyAsync(candidateId, expectedVersion, expectedProviderRowVersion, administratorId, cancellationToken);
        if (!verification.Succeeded) return await FailureAsync(candidateId, administratorId, "verification-failed", verification.ErrorCode!, cancellationToken);

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var settings = await LockSettingsAsync(cancellationToken);
            var candidate = await LockProviderAsync(candidateId, cancellationToken);
            if (settings == null || candidate == null || settings.RowVersion != expectedSettingsRowVersion
                || !candidate.Enabled || candidate.ConfigurationVersion != expectedVersion
                || candidate.VerifiedConfigurationVersion != expectedVersion || candidate.RowVersion != verification.RowVersion
                || candidate.ProfileMappings.Count == 0
                || candidate.ProfileMappings.Any(mapping => mapping.TransportProfile is not { IsActive: true }))
            {
                AddAudit(administratorId, candidateId, "conflict", "activation-retained");
                await _dbContext.SaveChangesAsync(cancellationToken);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                return RoutingActivationResult.Failure("provider-activation-stale");
            }
            settings.ActiveRoutingProviderConfigurationId = candidateId;
            AddAudit(administratorId, candidateId, "success", "verified-to-active");
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new RoutingActivationResult(true, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            return await FailureAsync(candidateId, administratorId, "conflict", "provider-activation-stale", cancellationToken);
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            if (transaction != null) await transaction.RollbackAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            return await FailureAsync(candidateId, administratorId, "failure", "provider-activation-stale", cancellationToken);
        }
    }

    private async Task<RoutingActivationResult> FailureAsync(
        Guid providerId, string administratorId, string category, string code, CancellationToken cancellationToken)
    {
        AddAudit(administratorId, providerId, category, "activation-retained");
        await _dbContext.SaveChangesAsync(cancellationToken);
        return RoutingActivationResult.Failure(code);
    }

    private void AddAudit(string administratorId, Guid providerId, string category, string transition) =>
        _dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = administratorId, Action = "RoutingProviderActivation", Timestamp = DateTime.UtcNow,
            Details = $"ProviderId={providerId}; AdapterType=OsrmCompatible; Category={category}; Transition={transition}."
        });

    private Task<ApplicationSettings?> LockSettingsAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.IsNpgsql()
            ? _dbContext.Set<ApplicationSettings>().FromSqlRaw("SELECT *, xmin FROM \"ApplicationSettings\" WHERE \"Id\" = 1 FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
            : _dbContext.Set<ApplicationSettings>().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);

    private Task<RoutingProviderConfiguration?> LockProviderAsync(Guid id, CancellationToken cancellationToken)
    {
        var query = _dbContext.Database.IsNpgsql()
            ? _dbContext.Set<RoutingProviderConfiguration>().FromSqlInterpolated($"SELECT *, xmin FROM \"RoutingProviderConfigurations\" WHERE \"Id\" = {id} FOR UPDATE")
            : _dbContext.Set<RoutingProviderConfiguration>().Where(item => item.Id == id);
        return query.Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static bool IsSerializationFailure(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure }
        || exception.InnerException != null && IsSerializationFailure(exception.InnerException);
}

/// <summary>Contains the bounded atomic activation outcome.</summary>
public sealed record RoutingActivationResult(bool Succeeded, string? ErrorCode)
{
    /// <summary>Creates a safe activation failure.</summary>
    public static RoutingActivationResult Failure(string code) => new(false, code);
}
