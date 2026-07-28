using Microsoft.AspNetCore.Authorization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Moq;
using Wayfarer.Areas.Admin.Controllers;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Verifies the secured allowlisted transport-profile administration boundary.</summary>
public sealed class AdminTransportProfileControllerTests : TestBase
{
    /// <summary>Proves the controller requires the Admin role and every mutation requires antiforgery.</summary>
    [Fact]
    public void Mutations_HaveRequiredSecurityMetadata()
    {
        var authorization = Assert.Single(typeof(TransportProfileController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("Admin", authorization.Roles);
        foreach (var method in typeof(TransportProfileController).GetMethods().Where(method => method.GetCustomAttributes<HttpPostAttribute>().Any()))
        {
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }
    }

    /// <summary>Proves create normalizes keys and ignores persistence-only fields.</summary>
    [Fact]
    public async Task Create_NormalizesKey_AndCreatesNonSeededProfile()
    {
        await using var db = CreateDbContext();
        var controller = BuildController(db);
        var model = new TransportProfileCreateViewModel { Key = "  SCOOTER  ", Label = "Scooter", Category = "Road", PlanningSpeedKmh = 18, SortOrder = 45 };

        var result = await controller.Create(model, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        var profile = await db.Set<TransportProfile>().SingleAsync(item => item.Key == "scooter");
        Assert.False(profile.IsSeeded);
        Assert.Contains(db.AuditLogs, audit => audit.Action == "TransportProfileCreate"
            && !audit.Details.Contains("trip", StringComparison.OrdinalIgnoreCase)
            && !audit.Details.Contains("scooter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Proves the immutable key is not changed by an overposted edit model.</summary>
    [Fact]
    public async Task Edit_DoesNotChangeImmutableKey()
    {
        await using var db = CreateDbContext();
        var profile = await db.Set<TransportProfile>().SingleAsync(item => item.Key == "walk");
        var controller = BuildController(db);
        var model = new TransportProfileEditViewModel
        {
            Id = profile.Id, Key = "attacker-key", Label = "Walking", Category = profile.Category,
            PlanningSpeedKmh = profile.PlanningSpeedKmh, SortOrder = profile.SortOrder, IsActive = true, RowVersion = profile.RowVersion
        };

        await controller.Edit(profile.Id, model, CancellationToken.None);

        Assert.Equal("walk", (await db.Set<TransportProfile>().FindAsync(profile.Id))!.Key);
    }

    /// <summary>Proves referenced speed mutations fail without changing either record.</summary>
    [Fact]
    public async Task Edit_RejectsReferencedSpeedChange()
    {
        await using var db = CreateDbContext();
        var profile = await db.Set<TransportProfile>().SingleAsync(item => item.Key == "walk");
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), UserId = "u", TripId = Guid.NewGuid(), Mode = "walk", TransportProfileId = profile.Id, EstimatedDuration = TimeSpan.FromMinutes(30) });
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        var model = new TransportProfileEditViewModel
        {
            Id = profile.Id, Key = profile.Key, Label = profile.Label, Category = profile.Category,
            PlanningSpeedKmh = 6, SortOrder = profile.SortOrder, IsActive = true, RowVersion = profile.RowVersion
        };

        var result = await controller.Edit(profile.Id, model, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Equal(5, (await db.Set<TransportProfile>().FindAsync(profile.Id))!.PlanningSpeedKmh);
        Assert.Equal("walk", Assert.Single(db.Segments).Mode);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(db.Segments).EstimatedDuration);
    }

    /// <summary>Proves unreferenced planning configuration remains editable before #405.</summary>
    [Fact]
    public async Task Edit_AllowsUnreferencedSpeedChange()
    {
        await using var db = CreateDbContext();
        var profile = new TransportProfile { Id = Guid.NewGuid(), Key = "scooter", Label = "Scooter", Category = "Road", PlanningSpeedKmh = 18, IsActive = true };
        db.Set<TransportProfile>().Add(profile);
        await db.SaveChangesAsync();
        var controller = BuildController(db);
        var model = new TransportProfileEditViewModel
        {
            Id = profile.Id, Key = profile.Key, Label = profile.Label, Category = profile.Category,
            PlanningSpeedKmh = 20, IsActive = true, RowVersion = profile.RowVersion, WasActive = true
        };

        var result = await controller.Edit(profile.Id, model, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(20, (await db.Set<TransportProfile>().FindAsync(profile.Id))!.PlanningSpeedKmh);
    }

    /// <summary>Proves an unreferenced deployment profile can be deleted.</summary>
    [Fact]
    public async Task DeleteConfirmed_DeletesOnlyUnreferencedNonSeededProfile()
    {
        await using var db = CreateDbContext();
        var profile = new TransportProfile { Id = Guid.NewGuid(), Key = "scooter", Label = "Scooter", Category = "Road", PlanningSpeedKmh = 18, IsActive = true };
        db.Set<TransportProfile>().Add(profile);
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(profile.Id, profile.RowVersion, CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null(await db.Set<TransportProfile>().FindAsync(profile.Id));
    }

    /// <summary>Proves seeded profiles remain recoverable even when unreferenced.</summary>
    [Fact]
    public async Task DeleteConfirmed_RejectsSeededProfile()
    {
        await using var db = CreateDbContext();
        var profile = await db.Set<TransportProfile>().SingleAsync(item => item.Key == "walk");
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(profile.Id, profile.RowVersion, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.NotNull(await db.Set<TransportProfile>().FindAsync(profile.Id));
    }

    /// <summary>Proves index ordering is stable across sort order, label, and key.</summary>
    [Fact]
    public async Task Index_OrdersBySortOrderLabelThenKey()
    {
        await using var db = CreateDbContext();
        db.Set<TransportProfile>().RemoveRange(db.Set<TransportProfile>());
        db.Set<TransportProfile>().AddRange(
            new TransportProfile { Id = Guid.NewGuid(), Key = "z", Label = "Beta", Category = "Test", SortOrder = 1 },
            new TransportProfile { Id = Guid.NewGuid(), Key = "b", Label = "Alpha", Category = "Test", SortOrder = 1 },
            new TransportProfile { Id = Guid.NewGuid(), Key = "a", Label = "Alpha", Category = "Test", SortOrder = 1 });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.Index(cancellationToken: CancellationToken.None);

        var model = Assert.IsType<TransportProfileIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(["a", "b", "z"], model.Items.Select(item => item.Key));
    }

    /// <summary>Proves xmin is configured as the optimistic-concurrency token.</summary>
    [Fact]
    public void RowVersion_IsConfiguredForOptimisticConcurrency()
    {
        using var db = CreateDbContext();
        var property = db.Model.FindEntityType(typeof(TransportProfile))!.FindProperty(nameof(TransportProfile.RowVersion))!;

        Assert.True(property.IsConcurrencyToken);
        Assert.Equal("xmin", property.GetColumnName());
    }

    private TransportProfileController BuildController(ApplicationDbContext db)
    {
        var controller = new TransportProfileController(db);
        var httpContext = BuildHttpContextWithUser("admin", "Admin");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }
}
