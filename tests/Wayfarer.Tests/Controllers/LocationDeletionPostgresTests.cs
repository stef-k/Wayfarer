using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Durable owner deletion decisions survive retries in independent PostgreSQL contexts.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class LocationDeletionPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>A committed bearer deletion is terminal on both ingest routes, even under saturation or passive filtering.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedKey_IsTerminalBeforeAdmissionAndEffects(bool checkIn)
    {
        var user = await fixture.CreateUserAsync();
        var key = Guid.NewGuid();
        int id;
        await using (var db = fixture.CreateContext())
        {
            db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "838", Token = user.Id });
            await db.SaveChangesAsync();
            var controller = ApiLocationAdmissionTests.Controller(db, new ApiWorkAdmission(64, 8));
            controller.Request.Headers.Authorization = $"Bearer {user.Id}";
            controller.Request.Headers["Idempotency-Key"] = key.ToString();
            Assert.IsType<OkObjectResult>(await controller.CheckIn(ApiLocationAdmissionTests.Dto()));
            id = await db.Locations.Where(row => row.UserId == user.Id).Select(row => row.Id).SingleAsync();
            Assert.IsType<OkObjectResult>(await controller.Delete(id));
        }
        await using var fresh = fixture.CreateContext();
        var admission = new ApiWorkAdmission(1, 1);
        using var saturated = admission.TryAcquire(user.Id, out _);
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        var events = new Mock<SseService>(MockBehavior.Strict);
        var retry = ApiLocationAdmissionTests.Controller(fresh, admission, visits.Object, events.Object);
        retry.Request.Headers.Authorization = $"Bearer {user.Id}";
        retry.Request.Headers["Idempotency-Key"] = key.ToString();
        var dto = ApiLocationAdmissionTests.Dto();
        dto.Accuracy = 1000;
        var response = Assert.IsType<ObjectResult>(checkIn ? await retry.CheckIn(dto) : await retry.LogLocation(dto));
        Assert.Equal(410, response.StatusCode);
        var body = JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal(new[] { "success", "code", "message" }, body.EnumerateObject().Select(property => property.Name));
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("location_deleted", body.GetProperty("code").GetString());
        Assert.Equal("Location was deleted.", body.GetProperty("message").GetString());
        Assert.False(retry.Response.Headers.ContainsKey("Retry-After"));
        Assert.False(await fresh.Locations.AnyAsync(row => row.UserId == user.Id));
        visits.VerifyNoOtherCalls();
        events.VerifyNoOtherCalls();
    }
}
