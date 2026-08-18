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
        CancellationToken cancellationToken)
    {
        var verification = await _verifier.VerifyAsync(candidateId, expectedVersion, expectedProviderRowVersion, cancellationToken);
        if (!verification.Succeeded) return RoutingActivationResult.Failure(verification.ErrorCode!);

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
        try
        {
            var settings = await LockSettingsAsync(cancellationToken);
            var candidate = await LockProviderAsync(candidateId, cancellationToken);
            if (settings == null || candidate == null || settings.RowVersion != expectedSettingsRowVersion
                || !candidate.Enabled || candidate.ConfigurationVersion != expectedVersion
                || candidate.VerifiedConfigurationVersion != expectedVersion || candidate.RowVersion != verification.RowVersion
                || candidate.ProfileMappings.Count == 0)
                return RoutingActivationResult.Failure("provider-activation-stale");
            settings.ActiveRoutingProviderConfigurationId = candidateId;
            await _dbContext.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            return new RoutingActivationResult(true, null);
        }
        catch (DbUpdateConcurrencyException) { return RoutingActivationResult.Failure("provider-activation-stale"); }
        catch (Exception exception) when (IsSerializationFailure(exception))
        { return RoutingActivationResult.Failure("provider-activation-stale"); }
    }

    private Task<ApplicationSettings?> LockSettingsAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.IsNpgsql()
            ? _dbContext.Set<ApplicationSettings>().FromSqlRaw("SELECT *, xmin FROM \"ApplicationSettings\" WHERE \"Id\" = 1 FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
            : _dbContext.Set<ApplicationSettings>().SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);

    private Task<RoutingProviderConfiguration?> LockProviderAsync(Guid id, CancellationToken cancellationToken)
    {
        var query = _dbContext.Database.IsNpgsql()
            ? _dbContext.Set<RoutingProviderConfiguration>().FromSqlInterpolated($"SELECT *, xmin FROM \"RoutingProviderConfigurations\" WHERE \"Id\" = {id} FOR UPDATE")
            : _dbContext.Set<RoutingProviderConfiguration>().Where(item => item.Id == id);
        return query.Include(item => item.ProfileMappings).SingleOrDefaultAsync(cancellationToken);
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
