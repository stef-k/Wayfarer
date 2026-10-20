using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Owner coordinate-refresh races through routed MVC, real PostgreSQL admission and controlled HTTP.</summary>
public sealed partial class ApiLocationProviderCreatePostgresTests
{
    /// <summary>Deletion after the editor read but before preparation forbids a new admission, including unchanged coordinates.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinateRefresh_DeletionBeforeAdmission_ReturnsMissingWithoutContact(bool changedCoordinates)
    {
        var protection = new EphemeralDataProtectionProvider();
        var owner = await SeedAsync("geoapify", protection);
        var key = Guid.NewGuid();
        var id = await SeedLocationAsync(owner.UserId, key);
        using var handler = new ControlledProvider();
        var events = new Mock<SseService>();
        events.Setup(service => service.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        Func<Task> delete = null!;
        var boundary = new BeforeRefreshPreparation(() => delete());
        await WithHostAsync(protection, handler, [boundary], async client =>
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Token);
            delete = async () =>
            {
                Assert.Equal(0, await AdmissionCountAsync(owner.UserId, "geoapify"));
                Assert.Equal(0, handler.Calls);
                using var deletion = await client.DeleteAsync($"/api/location/{id}");
                Assert.Equal(HttpStatusCode.OK, deletion.StatusCode);
                await AssertRefreshDeletedAsync(client, owner.UserId, key);
                Assert.Equal(0, await AdmissionCountAsync(owner.UserId, "geoapify"));
                Assert.Equal(0, handler.Calls);
            };
            using var response = await client.PutAsJsonAsync($"/api/location/{id}",
                new { latitude = changedCoordinates ? 11 : 10, longitude = changedCoordinates ? 21 : 20 });
            Assert.True(boundary.Triggered);
            await AssertRefreshDeletedAsync(client, owner.UserId, key);
            var admissions = await AdmissionCountAsync(owner.UserId, "geoapify");
            output.WriteLine("Refresh after committed deletion: changed={0}, status={1}, admissions={2}, contacts={3}",
                changedCoordinates, (int)response.StatusCode, admissions, handler.Calls);
            Assert.Equal(0, admissions);
            Assert.Equal(0, handler.Calls);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Location not found.", body.RootElement.GetProperty("message").GetString());
        }, events.Object, visits.Object);
        Assert.All(events.Invocations, call => Assert.Contains("location-deleted", (string)call.Arguments[1]));
        visits.VerifyNoOtherCalls();
    }

    /// <summary>Checks the persisted deletion decision and the owner's public status response in fresh contexts.</summary>
    private async Task AssertRefreshDeletedAsync(HttpClient client, string userId, Guid key)
    {
        await using var db = fixture.CreateContext();
        Assert.False(await db.Locations.AnyAsync(row => row.UserId == userId));
        Assert.True(await db.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == userId && row.IdempotencyKey == key));
        using var status = await client.PostAsJsonAsync("/api/location/key-status", new { keys = new[] { key.ToString() } });
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("deleted", Assert.Single(body.RootElement.GetProperty("results").EnumerateArray())
            .GetProperty("state").GetString());
    }

    /// <summary>Pauses at the review's first provider-selection lock, before any new admission or account fence.</summary>
    private sealed class BeforeRefreshPreparation(Func<Task> mutate) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }

        /// <summary>The editor must already have read its owned Location when the independent mutation commits.</summary>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains("PersonalLocationProviderSelections", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Triggered = true;
                await mutate();
            }
            return result;
        }
    }
}
