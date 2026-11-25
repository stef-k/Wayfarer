using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Reports unavailable when a deletion or ownership change wins the row lock.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewerSharing_ReportsUnavailableAfterConcurrentDeletionOrReassignment(bool reassign)
    {
        var trip = await SeedAsync();
        var newOwner = reassign ? await fixture.CreateUserAsync() : null;
        var probe = new MutationProbe();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe]);
        using var web = await WebAsync(app, trip.UserId);
        await using var writer = fixture.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        var target = writer.Trips.Where(t => t.Id == trip.Id);
        var affected = newOwner == null
            ? await target.ExecuteDeleteAsync()
            : await target.ExecuteUpdateAsync(setters => setters.SetProperty(t => t.UserId, newOwner.Id)
                .SetProperty(t => t.ShareProgressEnabled, false));
        Assert.Equal(1, affected);
        var sharing = web.PostAsync($"/User/Trip/ToggleShareProgress/{trip.Id}?enabled=true", null);
        try
        {
            await probe.UpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForBlockAsync(probe.ProcessId);
            await transaction.CommitAsync();

            using var response = await sharing.WaitAsync(TimeSpan.FromSeconds(10));
            response.EnsureSuccessStatusCode();
            var reply = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(reply.GetProperty("success").GetBoolean());
            await using var verification = fixture.CreateContext();
            var stored = await verification.Trips.FindAsync(trip.Id);
            if (newOwner == null)
            {
                Assert.Null(stored);
            }
            else
            {
                Assert.NotNull(stored);
                Assert.Equal(newOwner.Id, stored.UserId);
                Assert.True(stored.IsPublic);
                Assert.False(stored.ShareProgressEnabled);
            }
            Assert.Equal("Trip not found or unauthorized", reply.GetProperty("error").GetString());
        }
        finally
        {
            await transaction.DisposeAsync();
            using var response = await sharing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
