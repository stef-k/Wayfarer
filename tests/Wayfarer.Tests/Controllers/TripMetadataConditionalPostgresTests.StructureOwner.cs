using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Moq;
using Wayfarer.Models;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Actual owner snapshots preserve null and zero order and legacy fill for checked Area consent.</summary>
    [PostgresFact]
    public async Task StructureReceipt_OwnerSnapshotsSupplyExactAreaDeletionOriginals()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var region = await SeedStructureRegionAsync(db, trip);
        var polygon = new Polygon(new LinearRing([new(0, 0), new(0, 2), new(2, 2), new(0, 0)])) { SRID = 4326 };
        var area = new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = "Legacy fill",
            Geometry = polygon, DisplayOrder = null, FillHex = " #123aCD " };
        var sibling = new Area { Id = Guid.NewGuid(), RegionId = region.Id, Name = "Zero order", Geometry = polygon, DisplayOrder = 0 };
        db.Areas.AddRange(area, sibling);
        await db.SaveChangesAsync();
        using var logs = new TestLogProvider();
        var webRoot = CreateTestDirectory();
        await using var app = await StartAsync(db, servicesOverride: services =>
        {
            services.AddLogging(options => options.AddProvider(logs));
            services.AddSingleton<IWebHostEnvironment>(Mock.Of<IWebHostEnvironment>(environment => environment.WebRootPath == webRoot));
        });
        using var bearer = Bearer(app, trip.UserId);
        using var web = await WebAsync(app, trip.UserId);
        const string contract = TripMetadataIdentity.StructureVersion;
        var endpoint = $"/api/trips/{trip.Id}";
        using var ownerReply = await bearer.GetAsync(endpoint + "/planning?contract=" + contract);
        Assert.Equal(HttpStatusCode.OK, ownerReply.StatusCode);
        Assert.True(ownerReply.Headers.CacheControl is { Private: true, NoStore: true });
        var owner = await ownerReply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(trip.UserId, owner.GetProperty("subjectId").GetString());
        var areas = owner.GetProperty("regions")[0].GetProperty("areas");
        var stored = areas.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == area.Id);
        var siblingStored = areas.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == sibling.Id);
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("displayOrder").ValueKind);
        Assert.Equal(0, siblingStored.GetProperty("displayOrder").GetInt32());
        Assert.Equal(area.FillHex, stored.GetProperty("fillHex").GetString());

        using var editorReply = await web.GetAsync(endpoint + "/editor");
        Assert.True(editorReply.IsSuccessStatusCode,
            string.Join("\n", logs.Entries.Where(entry => entry.Exception != null).Select(entry => entry.Exception!.GetType().Name)));
        Assert.Equal("3", Assert.Single(editorReply.Headers.GetValues("X-Wayfarer-Trip-Dispatch")));
        var editor = (await editorReply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("areasById");
        Assert.Equal(JsonValueKind.Null, editor.GetProperty(area.Id.ToString()).GetProperty("originalValues").GetProperty("displayOrder").ValueKind);
        Assert.Equal(0, editor.GetProperty(sibling.Id.ToString()).GetProperty("originalValues").GetProperty("displayOrder").GetInt32());

        var comparison = new { area.Id, regionId = region.Id, displayOrder = stored.GetProperty("displayOrder"),
            name = stored.GetProperty("name"), notes = stored.GetProperty("notes"), fillHex = stored.GetProperty("fillHex"),
            geometry = stored.GetProperty("geometryIdentity"), planning = stored.GetProperty("planning") };
        var originalSiblings = areas.EnumerateArray().Select(row => new { id = row.GetProperty("id"), displayOrder = row.GetProperty("displayOrder") }).ToArray();
        var protectedBase = endpoint + $"/conditional/areas/{area.Id}";
        using var preview = await bearer.PostAsJsonAsync(protectedBase + "/deletion-preview",
            new { expected = new { area = comparison, areas = originalSiblings } }, JsonSerializerOptions.Web);
        Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        var body = JsonSerializer.Serialize(new { receiptContract = contract, confirmationToken =
            (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmation").GetProperty("token").GetString() });
        var route = protectedBase + "/deletion/receipt";
        var identity = new TripMetadataIdentity(contract, trip.UserId, "bearer", "area-delete", trip.Id, "", Guid.Empty, default)
            { Target = new("area", area.Id) };
        var permit = await IssueTypedAsync(bearer, identity, route, body);
        using var reply = await TripMetadataReceiptHttp.SendAsync(bearer, HttpMethod.Post, route, permit, body);
        Assert.True(reply.IsSuccessStatusCode, await reply.Content.ReadAsStringAsync());
        var result = (await TripMetadataReceiptHttp.EvidenceAsync(reply, permit, "applied")).GetProperty("result");
        Assert.Equal(area.Id, result.GetProperty("id").GetGuid());
        Assert.Equal(sibling.Id, result.GetProperty("areaOrder").GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(1, result.GetProperty("areaOrder").GetProperty("items")[0].GetProperty("displayOrder").GetInt32());
    }
}
