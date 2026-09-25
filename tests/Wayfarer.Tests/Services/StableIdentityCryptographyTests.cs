using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies actual hosted identity isolation, atomic replacement and rollback without printing secrets.</summary>
public sealed class StableIdentityCryptographyTests
{
    internal const string Secret = "sentinel-private-provider-627";

    /// <summary>A copied ring carries stable companions across roots while preserving the source rollback authority.</summary>
    [Fact]
    public async Task HostedSourceAndCopiedTargetRing_PreserveLegacyAndPortableCompanion()
    {
        using var directory = new TestDirectory();
        var sourceRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "source")).FullName;
        var targetRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "other-root")).FullName;
        var ring = Path.Combine(directory.Path, "ring");
        await using var source = Host(sourceRoot, ring);
        var owner = source.Services.GetRequiredService<PersonalProviderCredentialService>();
        var profile = PersonalLocationProviderProfile.Create("private-user", PersonalLocationProvider.Mapbox);
        owner.Replace(profile, Secret);
        var legacyProvider = DataProtectionProvider.Create(new DirectoryInfo(ring), options =>
            options.SetApplicationName(sourceRoot + Path.DirectorySeparatorChar).DisableAutomaticKeyGeneration());
        var codec = new LegacyCredentialPreparationCodec(legacyProvider,
            new StableDataProtectionProvider(source.Services.GetRequiredService<IDataProtectionProvider>()));
        profile.ProtectedCredential = PersonalProviderCredentialService.Protector(profile, legacyProvider).Protect(Secret);
        var legacy = profile.ProtectedCredential;
        Assert.True(codec.ReadLegacy(profile).Succeeded);
        Assert.Equal(DataProtectionAuthority.StableApplicationName,
            source.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);

        var copiedRing = Directory.CreateDirectory(Path.Combine(directory.Path, "copied-ring")).FullName;
        foreach (var file in Directory.GetFiles(ring))
            File.Copy(file, Path.Combine(copiedRing, Path.GetFileName(file)));
        var keysBefore = Directory.GetFiles(ring).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        await using var target = Host(targetRoot, copiedRing);
        var targetOwner = target.Services.GetRequiredService<PersonalProviderCredentialService>();
        Assert.True(object.Equals(Secret, targetOwner.Read(profile).Credential));
        Assert.True(object.Equals(legacy, profile.ProtectedCredential));
        owner.Replace(profile, "replacement");
        Assert.Null(profile.ProtectedCredential);
        Assert.False(codec.ReadLegacy(profile).Succeeded);
        Assert.True(object.Equals("replacement", targetOwner.Read(profile).Credential));
        foreach (var file in Directory.GetFiles(ring))
            Assert.True(keysBefore[Path.GetFileName(file)]!.SequenceEqual(File.ReadAllBytes(file)));
    }

    /// <summary>Either protection failure leaves every profile field unchanged and strips secret-bearing exceptions.</summary>
    [Fact]
    public void ReplacementFailure_IsAtomicAndRedacted()
    {
        var provider = new EphemeralDataProtectionProvider();
        var healthy = CredentialTestFactory.Create(provider);
        var profile = PersonalLocationProviderProfile.Create("private-user", PersonalLocationProvider.Mapbox);
        healthy.Replace(profile, Secret);
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        healthy.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        var before = JsonSerializer.Serialize(profile);
        var failure = new ThrowingProvider();
        var broken = new PersonalProviderCredentialService(failure);
        var exception = Assert.Throws<InvalidOperationException>(() => broken.Replace(profile, Secret));
        Assert.True(before == JsonSerializer.Serialize(profile));
        Assert.False(exception.ToString().Contains(Secret, StringComparison.Ordinal));
        healthy.Replace(profile, "replacement");
        Assert.Equal(3, profile.CredentialGeneration);
        Assert.True(healthy.Read(profile).Succeeded);
        Assert.True(profile.GeocodingAuthorized);
        Assert.False(profile.HasCurrentPermanentGeocodingConsent());
        healthy.Revoke(profile);
        Assert.Null(profile.ProtectedCredential);
        Assert.Null(profile.StableProtectedCredential);
    }

    /// <summary>Startup rejects pending/corrupt stable rows, accepts stable-only and rollback evidence, and never changes durable state.</summary>
    [Theory]
    [InlineData("pending", false)]
    [InlineData("matching", true)]
    [InlineData("unreadable", false)]
    [InlineData("mismatch", true)]
    [InlineData("legacy-unreadable", true)]
    [InlineData("stable-only", true)]
    [InlineData("revoked", true)]
    [InlineData("revoked-inconsistent", false)]
    [InlineData("empty", true)]
    public async Task Startup_ValidatesWithoutPreparing(string state, bool accepted)
    {
        using var directory = new TestDirectory();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "source")).FullName;
        var logs = new CapturedLogs();
        await using var host = Host(root, Path.Combine(directory.Path, "ring"), logs);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var owner = scope.ServiceProvider.GetRequiredService<PersonalProviderCredentialService>();
        var profile = PersonalLocationProviderProfile.Create("private-user", PersonalLocationProvider.Mapbox);
        owner.Replace(profile, Secret);
        profile.ProtectedCredential = "retained-rollback-evidence";
        if (state == "pending") profile.StableProtectedCredential = null;
        if (state is "revoked" or "revoked-inconsistent") owner.Revoke(profile);
        if (state == "revoked-inconsistent") profile.ProtectedCredential = "inconsistent";
        if (state == "empty") { profile.ProtectedCredential = null; profile.StableProtectedCredential = null; }
        if (state == "unreadable") profile.StableProtectedCredential = Secret;
        if (state == "mismatch")
        {
            var other = PersonalLocationProviderProfile.Create(profile.UserId, PersonalLocationProvider.Mapbox);
            owner.Replace(other, "different-secret");
            profile.StableProtectedCredential = other.StableProtectedCredential;
        }
        if (state == "legacy-unreadable") profile.ProtectedCredential = Secret;
        if (state == "stable-only") profile.ProtectedCredential = null;
        db.Add(profile);
        await db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(profile);
        var exception = await Record.ExceptionAsync(() => DataProtectionAuthority.ValidateAsync(host.Services));
        Assert.Equal(accepted, exception == null);
        Assert.False((exception?.ToString() ?? "").Contains(Secret, StringComparison.Ordinal));
        var leakedSecret = logs.Messages.Any(message => message.Contains(Secret, StringComparison.Ordinal));
        var leakedIdentity = logs.Messages.Any(message => message.Contains(profile.UserId, StringComparison.Ordinal));
        Assert.False(leakedSecret);
        Assert.False(leakedIdentity);
        db.ChangeTracker.Clear();
        Assert.True(before == JsonSerializer.Serialize(await db.PersonalLocationProviderProfiles.SingleAsync()));
    }

    /// <summary>Pins the complete explicit purpose inventory, distinguishing durable credentials from transient operations.</summary>
    [Fact]
    public void ExplicitPurposeInventory_RemainsBounded()
    {
        var root = FindRoot();
        var purposes = Directory.GetFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "\"(Wayfarer\\.[A-Za-z.]+\\.v[0-9]+)\"")
                .Select(match => match.Groups[1].Value)).Distinct().Order().ToArray();
        Assert.Equal(new[]
        {
            "Wayfarer.DataProtection.StartupProbe.v1",
            "Wayfarer.ExternalRouting.ProposalContext.v1",
            "Wayfarer.LocationProviders.PersonalCredentials.v1",
            "Wayfarer.PlaceRegionLifecycle.DependencyConfirmation.v1",
            "Wayfarer.TripEditor.SegmentAggregate.v1",
            "Wayfarer.TripEditor.SegmentRouteClear.v1"
        }, purposes);
    }

    /// <summary>Builds a real hosted global discriminator with an isolated in-memory database for startup tests.</summary>
    internal static WebApplication Host(string root, string ring, ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        if (logs != null) builder.Logging.AddProvider(logs);
        builder.Configuration["DataProtection:KeyRingPath"] = ring;
        builder.AddWayfarerDataProtection();
        builder.Services.AddScoped<PersonalProviderCredentialService>();
        builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(root));
        return builder.Build();
    }

    /// <summary>Finds the repository from the test output directory without a fixed checkout path.</summary>
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }

    /// <summary>Injects a deliberately sensitive protection error to exercise the redaction boundary.</summary>
    internal sealed class ThrowingProvider : IDataProtectionProvider, IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => throw new CryptographicException(Secret);
        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException(Secret);
    }

    /// <summary>Captures formatted diagnostics without printing them on test failure.</summary>
    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        internal List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
