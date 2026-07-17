using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies import composition cannot bypass reconciliation diagnostics in production DI.</summary>
public sealed class TripImportCompositionLoggingTests
{
    [Fact]
    public async Task ProductionEquivalentDi_ResolvesRegisteredReconcilerAndLogsInvalidToken()
    {
        using var logs = new TestLogProvider();
        await using var provider = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(logs))
            .AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()))
            .AddScoped<ITripImportTagReconciler, TripImportTagReconciler>()
            .AddScoped<ITripImportService, TripImportService>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITripImportService>();

        await Assert.ThrowsAsync<TripImportValidationException>(() => service.ImportWayfarerKmlAsync(
            ToStream(CreateKml(Guid.NewGuid(), "---")), "composition-user", TripImportMode.CreateNew));

        Assert.IsType<TripImportService>(service);
        Assert.IsType<TripImportTagReconciler>(scope.ServiceProvider.GetRequiredService<ITripImportTagReconciler>());
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Category == typeof(TripImportTagReconciler).FullName
            && entry.Message.Contains("cannot be represented safely", StringComparison.Ordinal));
    }

    [Fact]
    public void IsRecognizedTagUniqueConflict_RejectsArbitraryDatabaseErrors()
    {
        var arbitraryError = new DbUpdateException("arbitrary database failure", new InvalidOperationException());

        Assert.False(TripImportTagReconciler.IsRecognizedTagUniqueConflict(arbitraryError));
    }

    private static MemoryStream ToStream(string kml) => new(Encoding.UTF8.GetBytes(kml));

    private static string CreateKml(Guid id, string tags) => $@"<kml xmlns=""http://www.opengis.net/kml/2.2""><Document><name>Trip</name><ExtendedData><Data name=""TripId""><value>{id}</value></Data><Data name=""Tags""><value>{tags}</value></Data></ExtendedData></Document></kml>";
}
