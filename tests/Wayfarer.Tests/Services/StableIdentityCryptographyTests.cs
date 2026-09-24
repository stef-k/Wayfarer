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
        var legacy = profile.ProtectedCredential;
        var stable = profile.StableProtectedCredential;
        Assert.True(owner.ReadStable(profile).Succeeded);
        Assert.NotEqual(DataProtectionAuthority.StableApplicationName,
            source.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        Assert.Equal(sourceRoot + Path.DirectorySeparatorChar,
            source.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);

        var copiedRing = Directory.CreateDirectory(Path.Combine(directory.Path, "copied-ring")).FullName;
        foreach (var file in Directory.GetFiles(ring))
            File.Copy(file, Path.Combine(copiedRing, Path.GetFileName(file)));
        var keysBefore = Directory.GetFiles(ring).ToDictionary(Path.GetFileName, File.ReadAllBytes);
        await using var target = Host(targetRoot, copiedRing);
        var targetOwner = target.Services.GetRequiredService<PersonalProviderCredentialService>();
        Assert.False(targetOwner.Read(profile).Succeeded);
        Assert.True(string.Equals(Secret, targetOwner.ReadStable(profile).Credential, StringComparison.Ordinal));
        await using var restored = Host(sourceRoot, ring);
        profile.StableProtectedCredential = null;
        Assert.True(string.Equals(Secret, restored.Services.GetRequiredService<PersonalProviderCredentialService>().Read(profile).Credential, StringComparison.Ordinal));
        Assert.True(string.Equals(legacy, profile.ProtectedCredential, StringComparison.Ordinal));
        profile.StableProtectedCredential = stable;
        foreach (var file in Directory.GetFiles(ring))
            Assert.True(keysBefore[Path.GetFileName(file)]!.SequenceEqual(File.ReadAllBytes(file)));
    }

    /// <summary>Either protection failure leaves every profile field unchanged and strips secret-bearing exceptions.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacementFailure_IsAtomicAndRedacted(bool legacyFailure)
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
        var broken = new PersonalProviderCredentialService(legacyFailure ? failure : provider,
            new StableDataProtectionProvider(legacyFailure ? provider : failure));
        var exception = Assert.Throws<InvalidOperationException>(() => broken.Replace(profile, Secret));
        Assert.True(before == JsonSerializer.Serialize(profile));
        Assert.DoesNotContain(Secret, exception.ToString());
        healthy.Replace(profile, "replacement");
        Assert.Equal(3, profile.CredentialGeneration);
        Assert.True(healthy.ReadStable(profile).Succeeded);
        Assert.True(profile.GeocodingAuthorized);
        Assert.False(profile.HasCurrentPermanentGeocodingConsent());
        healthy.Revoke(profile);
        Assert.Null(profile.ProtectedCredential);
        Assert.Null(profile.StableProtectedCredential);
    }

    /// <summary>Startup accepts pending rows, rejects corrupt/mismatched companions, and never changes durable state.</summary>
    [Theory]
    [InlineData("pending", true)]
    [InlineData("matching", true)]
    [InlineData("unreadable", false)]
    [InlineData("mismatch", false)]
    [InlineData("legacy-unreadable", false)]
    [InlineData("stable-only", false)]
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
        if (state == "pending") profile.StableProtectedCredential = null;
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
        Assert.DoesNotContain(Secret, exception?.ToString() ?? "");
        Assert.DoesNotContain(Secret, string.Join("\n", logs.Messages));
        Assert.DoesNotContain(profile.UserId, string.Join("\n", logs.Messages));
        db.ChangeTracker.Clear();
        Assert.True(before == JsonSerializer.Serialize(await db.PersonalLocationProviderProfiles.SingleAsync()));
        if (state == "pending")
            Assert.Contains(logs.Messages, message => message.Contains("pending for 1"));
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
