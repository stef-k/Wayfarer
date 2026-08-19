using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies credential, version, and feature-state administration boundaries.</summary>
public sealed class RoutingProviderAdministrationServiceTests : TestBase
{
    [Fact]
    public async Task Save_BlankCredentialPreservesCiphertextAndVerificationForMetadataOnlyEdit()
    {
        var fixture = CreateFixture(requiredCredential: true, featureEnabled: false);
        var ciphertext = fixture.Provider.CredentialCiphertext;
        var version = fixture.Provider.ConfigurationVersion;
        var model = Model(fixture);
        model.DisplayName = "Renamed OSRM";
        model.Credential = " ";

        var result = await fixture.Service.SaveAsync(model, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(ciphertext, fixture.Provider.CredentialCiphertext);
        Assert.Equal(version, fixture.Provider.ConfigurationVersion);
        Assert.Equal(version, fixture.Provider.VerifiedConfigurationVersion);
    }

    [Fact]
    public async Task Save_OperationalChangeIncrementsVersionAndInvalidatesVerification()
    {
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        var model = Model(fixture);
        model.GenerationTimeoutSeconds = 20;

        var result = await fixture.Service.SaveAsync(model, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, fixture.Provider.ConfigurationVersion);
        Assert.Null(fixture.Provider.VerifiedConfigurationVersion);
    }

    [Fact]
    public async Task Save_PersonalAccessChangeIncrementsVersionInvalidatesVerificationAndAuditsSafely()
    {
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        var model = Model(fixture);
        model.PersonalRoutingAccess = PersonalRoutingAccess.CredentialFree;

        var result = await fixture.Service.SaveAsync(model, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(PersonalRoutingAccess.CredentialFree, fixture.Provider.PersonalRoutingAccess);
        Assert.Null(fixture.Provider.VerifiedConfigurationVersion);
        Assert.DoesNotContain(fixture.Db.AuditLogs, item => item.Details.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Save_RequiredToCredentialFreeClearsSelectingUsersWithoutChangingSelection()
    {
        const string userId = "personal-owner";
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        fixture.Provider.PersonalRoutingAccess = PersonalRoutingAccess.CredentialRequired;
        var configuration = UserRoutingConfiguration.CreateServerDefault(userId);
        configuration.SelectPersonalProvider(fixture.Provider.Id);
        new UserRoutingCredentialService(new EphemeralDataProtectionProvider())
            .Replace(configuration, fixture.Provider.Id, "personal-secret");
        configuration.VerifiedUserConfigurationVersion = configuration.ConfigurationVersion;
        configuration.VerifiedProviderConfigurationVersion = fixture.Provider.ConfigurationVersion;
        configuration.VerificationStatus = "verified";
        fixture.Db.Set<UserRoutingConfiguration>().Add(configuration);
        fixture.Db.SaveChanges();
        var originalUserVersion = configuration.ConfigurationVersion;
        var model = Model(fixture);
        model.PersonalRoutingAccess = PersonalRoutingAccess.CredentialFree;

        var result = await fixture.Service.SaveAsync(model, "admin", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(fixture.Provider.Id, configuration.SelectedProviderConfigurationId);
        Assert.Null(configuration.CredentialCiphertext);
        Assert.False(configuration.CredentialPresent);
        Assert.Null(configuration.VerifiedUserConfigurationVersion);
        Assert.Null(configuration.VerifiedProviderConfigurationVersion);
        Assert.Null(configuration.VerificationStatus);
        Assert.Equal(originalUserVersion + 1, configuration.ConfigurationVersion);
        Assert.True(configuration.UpdatedAt > configuration.CreatedAt);
    }

    [Fact]
    public async Task Save_MinimumIntervalChangeIsExactAndInvalidatesOnlyWhenChanged()
    {
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        var originalVersion = fixture.Provider.ConfigurationVersion;
        var changed = Model(fixture);
        changed.MinimumIntervalSeconds = " 1.1 ";

        Assert.True((await fixture.Service.SaveAsync(changed, "admin", CancellationToken.None)).Succeeded);
        Assert.Equal(1100, fixture.Provider.MinimumIntervalMilliseconds);
        Assert.Equal(originalVersion + 1, fixture.Provider.ConfigurationVersion);
        Assert.Null(fixture.Provider.VerifiedConfigurationVersion);

        var unchangedVersion = fixture.Provider.ConfigurationVersion;
        var unchanged = Model(fixture);
        Assert.True((await fixture.Service.SaveAsync(unchanged, "admin", CancellationToken.None)).Succeeded);
        Assert.Equal(unchangedVersion, fixture.Provider.ConfigurationVersion);
    }

    [Fact]
    public async Task Save_CommittedIntervalDecreasePublishesToAlreadyQueuedPacingState()
    {
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        fixture.Provider.MinimumIntervalMilliseconds = 2000;
        fixture.Db.SaveChanges();
        fixture.Pacer.ApplyConfiguration(fixture.Provider.Id, fixture.Provider.ConfigurationVersion, 2000);
        var prior = await fixture.Pacer.WaitAsync(
            fixture.Provider.Id, fixture.Provider.ConfigurationVersion, CancellationToken.None);
        prior.Turn!.RecordAttemptStart(); prior.Turn.Dispose();
        var waiting = fixture.Pacer.WaitAsync(
            fixture.Provider.Id, fixture.Provider.ConfigurationVersion, CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var model = Model(fixture);
        model.MinimumIntervalSeconds = "1.0";

        Assert.True((await fixture.Service.SaveAsync(model, "admin", CancellationToken.None)).Succeeded);
        Assert.True((await waiting).Succeeded);
    }

    [Fact]
    public async Task Save_CommittedIntervalIncreasePreventsQueuedWorkUsingOlderInterval()
    {
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        fixture.Provider.MinimumIntervalMilliseconds = 1000;
        fixture.Db.SaveChanges();
        fixture.Pacer.ApplyConfiguration(fixture.Provider.Id, fixture.Provider.ConfigurationVersion, 1000);
        var prior = await fixture.Pacer.WaitAsync(
            fixture.Provider.Id, fixture.Provider.ConfigurationVersion, CancellationToken.None);
        prior.Turn!.RecordAttemptStart(); prior.Turn.Dispose();
        var waiting = fixture.Pacer.WaitAsync(
            fixture.Provider.Id, fixture.Provider.ConfigurationVersion, CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
        var model = Model(fixture);
        model.MinimumIntervalSeconds = "2.0";

        Assert.True((await fixture.Service.SaveAsync(model, "admin", CancellationToken.None)).Succeeded);
        fixture.Time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(waiting.IsCompleted);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await waiting).Succeeded);
    }

    [Fact]
    public async Task ClearCredential_RejectsRequiredActiveCredentialUnlessAtomicallyDisabled()
    {
        var fixture = CreateFixture(requiredCredential: true, featureEnabled: true);

        var rejected = await fixture.Service.ClearCredentialAsync(
            fixture.Provider.Id, true, false, fixture.Provider.RowVersion, fixture.Settings.RowVersion, "admin", CancellationToken.None);
        var cleared = await fixture.Service.ClearCredentialAsync(
            fixture.Provider.Id, true, true, fixture.Provider.RowVersion, fixture.Settings.RowVersion, "admin", CancellationToken.None);

        Assert.False(rejected.Succeeded);
        Assert.True(cleared.Succeeded);
        Assert.False(fixture.Settings.ExternalRouteGenerationEnabled);
        Assert.False(fixture.Provider.CredentialPresent);
        Assert.Null(fixture.Provider.CredentialCiphertext);
        Assert.Equal(2, fixture.Settings.ExternalRouteGenerationVersion);
    }

    [Fact]
    public async Task FeatureEnable_RejectsUnverifiedSelectedProvider()
    {
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        fixture.Provider.VerifiedConfigurationVersion = null;
        fixture.Db.SaveChanges();

        var result = await fixture.Service.SetFeatureEnabledAsync(true, fixture.Settings.RowVersion, "admin", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(fixture.Settings.ExternalRouteGenerationEnabled);
    }

    [Fact]
    public async Task Save_RejectsMappingToInactiveProfile()
    {
        var fixture = CreateFixture(requiredCredential: false, featureEnabled: false);
        fixture.Profile.IsActive = false;
        fixture.Db.SaveChanges();

        var result = await fixture.Service.SaveAsync(Model(fixture), "admin", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task FeatureEnable_RejectsRequiredCredentialClearedAfterVerification()
    {
        var fixture = CreateFixture(requiredCredential: true, featureEnabled: false);
        fixture.Provider.CredentialPresent = false;
        fixture.Provider.CredentialCiphertext = null;
        fixture.Db.SaveChanges();

        var result = await fixture.Service.SetFeatureEnabledAsync(
            true, fixture.Settings.RowVersion, "admin", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(fixture.Settings.ExternalRouteGenerationEnabled);
    }

    private Fixture CreateFixture(bool requiredCredential, bool featureEnabled)
    {
        var db = CreateDbContext();
        var credentialService = new RoutingProviderCredentialService(new EphemeralDataProtectionProvider());
        var profile = db.Set<TransportProfile>().First();
        var provider = new RoutingProviderConfiguration
        {
            Id = Guid.NewGuid(), DisplayName = "OSRM", BaseEndpoint = "https://routing.example", Enabled = true,
            CredentialRequired = requiredCredential, ExternalCoordinateDisclosure = "Coordinates are sent externally.",
            VerificationFromLongitude = 1, VerificationFromLatitude = 2, VerificationToLongitude = 3,
            VerificationToLatitude = 4
        };
        provider.ProfileMappings.Add(new RoutingProviderProfileMapping
        {
            RoutingProviderConfigurationId = provider.Id, TransportProfileId = profile.Id, OsrmProfile = "driving"
        });
        if (requiredCredential) credentialService.Replace(provider, "secret");
        else provider.MarkConfigurationChanged();
        provider.VerifiedConfigurationVersion = provider.ConfigurationVersion;
        var settings = new ApplicationSettings
        {
            Id = 1, ExternalRouteGenerationEnabled = featureEnabled, ExternalRouteGenerationVersion = 1,
            ActiveRoutingProviderConfigurationId = provider.Id
        };
        db.Set<RoutingProviderConfiguration>().Add(provider);
        db.ApplicationSettings.Add(settings);
        db.SaveChanges();
        var time = new ControlledTimeProvider();
        var pacer = new RoutingProviderPacer(time);
        return new Fixture(db, new RoutingProviderAdministrationService(db, credentialService, pacer), provider, settings, profile, pacer, time);
    }

    private static RoutingProviderEditViewModel Model(Fixture fixture) => new()
    {
        Id = fixture.Provider.Id, DisplayName = fixture.Provider.DisplayName, BaseEndpoint = fixture.Provider.BaseEndpoint!,
        CredentialRequired = fixture.Provider.CredentialRequired, CredentialPresent = fixture.Provider.CredentialPresent,
        PersonalRoutingAccess = fixture.Provider.PersonalRoutingAccess,
        Enabled = fixture.Provider.Enabled, ExternalCoordinateDisclosure = fixture.Provider.ExternalCoordinateDisclosure!,
        VerificationFromLongitude = fixture.Provider.VerificationFromLongitude,
        VerificationFromLatitude = fixture.Provider.VerificationFromLatitude,
        VerificationToLongitude = fixture.Provider.VerificationToLongitude,
        VerificationToLatitude = fixture.Provider.VerificationToLatitude,
        GenerationTimeoutSeconds = fixture.Provider.GenerationTimeoutSeconds,
        ResponseSizeLimitBytes = fixture.Provider.ResponseSizeLimitBytes,
        RequestsPerMinute = fixture.Provider.RequestsPerMinute, MaxConcurrency = fixture.Provider.MaxConcurrency,
        MinimumIntervalSeconds = RoutingMinimumIntervalConverter.Format(fixture.Provider.MinimumIntervalMilliseconds),
        RowVersion = fixture.Provider.RowVersion, ConfigurationVersion = fixture.Provider.ConfigurationVersion,
        Mappings = [new RoutingProviderMappingViewModel
            { TransportProfileId = fixture.Profile.Id, OsrmProfile = "driving" }]
    };

    private sealed record Fixture(
        ApplicationDbContext Db, RoutingProviderAdministrationService Service, RoutingProviderConfiguration Provider,
        ApplicationSettings Settings, TransportProfile Profile, RoutingProviderPacer Pacer, ControlledTimeProvider Time);

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly List<ControlledTimer> _timers = [];
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state, _timestamp + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
            foreach (var timer in _timers.Where(item => !item.Disposed && item.Due <= _timestamp).ToArray()) timer.Fire();
        }
        private sealed class ControlledTimer(TimerCallback callback, object? state, long due) : ITimer
        {
            public long Due { get; private set; } = due;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due += dueTime.Ticks; return true; }
            public void Fire() { if (!Disposed) callback(state); }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
