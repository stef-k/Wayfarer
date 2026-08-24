using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves direct PostgreSQL constraints for durable enrichment operation authority.</summary>
public sealed partial class GeoapifyBackfillConcurrencyPostgresTests
{
    /// <summary>Proves PostgreSQL rejects an active operation missing any durable ownership field.</summary>
    [PostgresFact]
    public async Task PostgreSqlRejectsIncompleteActiveOperation()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        await using var db = fixture.CreateContext();
        var location = await db.Locations.SingleAsync(item => item.UserId == user.Id);
        var profile = await db.PersonalLocationProviderProfiles.SingleAsync(item => item.UserId == user.Id);
        db.Add(LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow));
        db.Add(new LocationEnrichmentAttempt
        {
            UserId = user.Id, LocationId = location.Id, ProviderKey = "geoapify", ProviderProfileId = profile.Id,
            Capability = PersonalProviderCapability.Geocoding, CredentialGeneration = 1, ConfigurationGeneration = 1,
            SelectionGeneration = 1, Verification = PersonalProviderVerification.Verified,
            VerificationCredentialGeneration = 1, VerificationGeneration = 1,
            Outcome = LocationEnrichmentOutcome.RetryableFailure, AdmittedAttemptCount = 1,
            LastAttemptAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1),
            OperationId = Guid.NewGuid(), OperationLeaseId = null, OperationFencingGeneration = 1,
            OperationWorkflowEpoch = 1, OperationAttemptNumber = 1, OperationStartedAtUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Proves every nullable active-operation binding fails closed when null or malformed.</summary>
    [PostgresTheory]
    [InlineData("\"OperationWorkflowEpoch\" = NULL", false)]
    [InlineData("\"OperationWorkflowEpoch\" = -1", false)]
    [InlineData("\"OperationFencingGeneration\" = NULL", false)]
    [InlineData("\"OperationFencingGeneration\" = 0", false)]
    [InlineData("\"OperationAttemptNumber\" = NULL", false)]
    [InlineData("\"OperationAttemptNumber\" = 0", false)]
    [InlineData("\"Capability\" = NULL", false)]
    [InlineData("\"Capability\" = 2", false)]
    [InlineData("\"ProviderProfileId\" = NULL", false)]
    [InlineData("\"ProviderKey\" = NULL", false)]
    [InlineData("\"CredentialGeneration\" = NULL", false)]
    [InlineData("\"CredentialGeneration\" = 0", false)]
    [InlineData("\"ConfigurationGeneration\" = NULL", false)]
    [InlineData("\"ConfigurationGeneration\" = 0", false)]
    [InlineData("\"SelectionGeneration\" = NULL", false)]
    [InlineData("\"SelectionGeneration\" = 0", false)]
    [InlineData("\"Verification\" = NULL", false)]
    [InlineData("\"Verification\" = 0", false)]
    [InlineData("\"VerificationCredentialGeneration\" = NULL", false)]
    [InlineData("\"VerificationCredentialGeneration\" = 0", false)]
    [InlineData("\"VerificationGeneration\" = NULL", false)]
    [InlineData("\"VerificationGeneration\" = 0", false)]
    [InlineData("\"ConsentVersion\" = NULL", true)]
    [InlineData("\"ConsentVersion\" = 0", true)]
    [InlineData("\"ConsentTimestamp\" = NULL", true)]
    [InlineData("\"ConsentCredentialGeneration\" = NULL", true)]
    [InlineData("\"ConsentCredentialGeneration\" = 0", true)]
    public async Task PostgreSqlRejectsNullOrMalformedActiveOperationBinding(string mutation, bool mapbox)
    {
        var (db, attempt) = await CreateCompleteOperationAsync(mapbox);
        await using (db)
        {
            await db.SaveChangesAsync();
            var command = $"UPDATE \"LocationEnrichmentAttempts\" SET {mutation} WHERE \"Id\" = {attempt.Id}";
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(command));
        }
    }

    /// <summary>Proves complete provider branches are accepted and historical rows remain valid.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlAcceptsCompleteActiveProviderOperation(bool mapbox)
    {
        var (db, _) = await CreateCompleteOperationAsync(mapbox);
        await using (db) await db.SaveChangesAsync();
    }

    /// <summary>Proves inactive history may retain authority data but partial operations fail closed.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlAppliesExplicitInactiveAndPartialOperationRules(bool partial)
    {
        var (db, attempt) = await CreateCompleteOperationAsync(false);
        await using (db)
        {
            attempt.OperationId = null;
            attempt.OperationLeaseId = partial ? Guid.NewGuid() : null;
            attempt.OperationFencingGeneration = null;
            attempt.OperationStartedAtUtc = null;
            attempt.OperationWorkflowEpoch = null;
            attempt.OperationAttemptNumber = null;
            if (partial) await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            else await db.SaveChangesAsync();
        }
    }

    /// <summary>Creates a constraint-valid active operation without duplicating production authority checks.</summary>
    private async Task<(ApplicationDbContext Db, LocationEnrichmentAttempt Attempt)> CreateCompleteOperationAsync(
        bool mapbox)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var db = fixture.CreateContext();
        var location = await db.Locations.SingleAsync(item => item.UserId == user.Id);
        var profile = await db.PersonalLocationProviderProfiles.SingleAsync(item => item.UserId == user.Id);
        db.Add(LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow));
        var attempt = new LocationEnrichmentAttempt
        {
            UserId = user.Id, LocationId = location.Id, ProviderKey = mapbox ? "mapbox" : "geoapify",
            ProviderProfileId = profile.Id, Capability = PersonalProviderCapability.Geocoding,
            CredentialGeneration = 1, ConfigurationGeneration = 1, SelectionGeneration = 1,
            Verification = PersonalProviderVerification.Verified, VerificationCredentialGeneration = 1,
            VerificationGeneration = 1, ConsentVersion = mapbox ? 1 : null,
            ConsentTimestamp = mapbox ? DateTime.UtcNow : null, ConsentCredentialGeneration = mapbox ? 1 : null,
            Outcome = LocationEnrichmentOutcome.RetryableFailure, AdmittedAttemptCount = 1,
            LastAttemptAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1),
            OperationId = Guid.NewGuid(), OperationLeaseId = Guid.NewGuid(), OperationFencingGeneration = 1,
            OperationWorkflowEpoch = 1, OperationAttemptNumber = 1, OperationStartedAtUtc = DateTime.UtcNow
        };
        db.Add(attempt);
        return (db, attempt);
    }
}
