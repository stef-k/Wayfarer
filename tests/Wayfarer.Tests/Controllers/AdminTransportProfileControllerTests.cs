using Microsoft.AspNetCore.Authorization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
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

    /// <summary>Proves an unrelated persistence failure is not presented as a duplicate key.</summary>
    [Fact]
    public async Task Create_UnrelatedDbUpdateException_ReturnsBoundedGlobalError()
    {
        var interceptor = new FailingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
        db.Set<TransportProfile>().AddRange(TestDataFixtures.CreateTransportProfiles());
        await db.SaveChangesAsync();
        interceptor.Fail = true;
        var controller = BuildController(db);
        var model = new TransportProfileCreateViewModel { Key = "scooter", Label = "Scooter", Category = "Road", PlanningSpeedKmh = 18 };

        var result = await controller.Create(model, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.False(controller.ModelState.TryGetValue(nameof(model.Key), out var keyState) && keyState.Errors.Count > 0);
        var error = Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage;
        Assert.Equal("The transport profile could not be created. No changes were saved.", error);
        Assert.DoesNotContain("database failure", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    /// <summary>Proves only the expected PostgreSQL key constraint receives duplicate-key guidance.</summary>
    [Fact]
    public async Task Create_ExpectedPostgresUniqueConstraint_ReturnsKeyError()
    {
        var postgres = new PostgresException(
            "duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
            null!, null!, 0, 0, null!, null!, "public", "TransportProfiles", "Key", null!,
            "IX_TransportProfiles_Key", "nbtinsert.c", "1", "_bt_check_unique");
        var interceptor = new FailingSaveChangesInterceptor { Exception = new DbUpdateException("save failed", postgres) };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
        db.Set<TransportProfile>().AddRange(TestDataFixtures.CreateTransportProfiles());
        await db.SaveChangesAsync();
        interceptor.Fail = true;
        var controller = BuildController(db);

        var result = await controller.Create(
            new TransportProfileCreateViewModel { Key = "scooter", Label = "Scooter", Category = "Road" },
            CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.Contains(controller.ModelState[nameof(TransportProfileCreateViewModel.Key)]!.Errors,
            error => error.ErrorMessage.Contains("conflicts with current catalog state", StringComparison.Ordinal));
        Assert.False(controller.ModelState.ContainsKey(string.Empty));
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

    /// <summary>Proves optimistic concurrency retains its bounded reload guidance.</summary>
    [Fact]
    public async Task Edit_DbUpdateConcurrencyException_ReturnsConcurrencyError()
    {
        var interceptor = new FailingSaveChangesInterceptor { Exception = new DbUpdateConcurrencyException("stale row") };
        await using var db = CreateInterceptedDbContext(interceptor);
        var profile = await AddEditableProfileAsync(db);
        interceptor.Fail = true;
        var controller = BuildController(db);

        var result = await controller.Edit(profile.Id, BuildEditModel(profile, "Updated label"), CancellationToken.None);

        Assert.IsType<TransportProfileEditViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("This profile changed after the form was loaded. Review the current values and try again.",
            Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage);
    }

    /// <summary>Proves an EF-wrapped PostgreSQL serialization failure retains retry guidance.</summary>
    [Fact]
    public async Task Edit_WrappedPostgresSerializationFailure_ReturnsConcurrencyError()
    {
        var interceptor = new FailingSaveChangesInterceptor
        {
            Exception = new DbUpdateException("save failed", CreatePostgresException(PostgresErrorCodes.SerializationFailure))
        };
        await using var db = CreateInterceptedDbContext(interceptor);
        var profile = await AddEditableProfileAsync(db);
        interceptor.Fail = true;
        var controller = BuildController(db);

        var result = await controller.Edit(profile.Id, BuildEditModel(profile, "Updated label"), CancellationToken.None);

        Assert.IsType<TransportProfileEditViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("The profile or its dependencies changed concurrently. No changes were saved.",
            Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage);
    }

    /// <summary>Proves unrelated edit persistence failures stay generic, payload-safe, and preserve submitted state.</summary>
    [Fact]
    public async Task Edit_UnrelatedDbUpdateException_ReturnsGenericSaveError()
    {
        var interceptor = new FailingSaveChangesInterceptor
        {
            Exception = new DbUpdateException("SECRET profile contents and connection details")
        };
        await using var db = CreateInterceptedDbContext(interceptor);
        var profile = await AddEditableProfileAsync(db);
        interceptor.Fail = true;
        var logger = new CapturingLogger<TransportProfileController>();
        var controller = BuildController(db, logger);
        var model = BuildEditModel(profile, "Submitted label");

        var result = await controller.Edit(profile.Id, model, CancellationToken.None);

        var returned = Assert.IsType<TransportProfileEditViewModel>(Assert.IsType<ViewResult>(result).Model);
        var error = Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage;
        Assert.Equal("The transport profile could not be saved. Please try again.", error);
        Assert.DoesNotContain("concurrent", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Submitted label", returned.Label);
        Assert.Equal(profile.Key, returned.Key);
        Assert.Equal(0, returned.ReferencedSegments);
        Assert.Equal(["Transport profile update failed without persisting changes."], logger.Messages);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("SECRET", StringComparison.Ordinal));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
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

    /// <summary>Proves referenced non-seeded profiles retain the pre-save dependency rejection.</summary>
    [Fact]
    public async Task DeleteConfirmed_RejectsReferencedProfileBeforeSave()
    {
        await using var db = CreateDbContext();
        var profile = await AddEditableProfileAsync(db);
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), UserId = "u", TripId = Guid.NewGuid(), Mode = profile.Key, TransportProfileId = profile.Id });
        await db.SaveChangesAsync();
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(profile.Id, profile.RowVersion, CancellationToken.None);

        Assert.Equal("Referenced profiles cannot be deleted; deactivate them instead.",
            Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage);
        Assert.IsType<TransportProfileRowViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.NotNull(await db.Set<TransportProfile>().FindAsync(profile.Id));
    }

    /// <summary>Proves only the expected segment/profile FK violation receives dependency-race guidance.</summary>
    [Fact]
    public async Task DeleteConfirmed_ExpectedForeignKeyViolation_ReturnsDependencyRaceError()
    {
        var interceptor = new FailingSaveChangesInterceptor
        {
            Exception = new DbUpdateException("save failed", CreatePostgresException(
                PostgresErrorCodes.ForeignKeyViolation, "FK_Segments_TransportProfiles_TransportProfileId"))
        };
        await using var db = CreateInterceptedDbContext(interceptor);
        var profile = await AddEditableProfileAsync(db);
        interceptor.Fail = true;
        var controller = BuildController(db);

        var result = await controller.DeleteConfirmed(profile.Id, profile.RowVersion, CancellationToken.None);

        Assert.IsType<TransportProfileRowViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal("The profile gained a dependency and cannot be deleted. Deactivate it instead.",
            Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage);
    }

    /// <summary>Proves unrelated delete persistence failures stay generic and payload-safe.</summary>
    [Fact]
    public async Task DeleteConfirmed_UnrelatedDbUpdateException_ReturnsGenericDeleteError()
    {
        var interceptor = new FailingSaveChangesInterceptor
        {
            Exception = new DbUpdateException("SECRET audit payload and SQL")
        };
        await using var db = CreateInterceptedDbContext(interceptor);
        var profile = await AddEditableProfileAsync(db);
        interceptor.Fail = true;
        var logger = new CapturingLogger<TransportProfileController>();
        var controller = BuildController(db, logger);

        var result = await controller.DeleteConfirmed(profile.Id, profile.RowVersion, CancellationToken.None);

        Assert.IsType<TransportProfileRowViewModel>(Assert.IsType<ViewResult>(result).Model);
        var error = Assert.Single(controller.ModelState[string.Empty]!.Errors).ErrorMessage;
        Assert.Equal("The transport profile could not be deleted. Please try again.", error);
        Assert.DoesNotContain("dependency", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Transport profile deletion failed without persisting changes."], logger.Messages);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("SECRET", StringComparison.Ordinal));
        Assert.NotNull(await db.Set<TransportProfile>().FindAsync(profile.Id));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
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

    private TransportProfileController BuildController(
        ApplicationDbContext db,
        ILogger<TransportProfileController>? logger = null)
    {
        var controller = new TransportProfileController(db, logger ?? NullLogger<TransportProfileController>.Instance);
        var httpContext = BuildHttpContextWithUser("admin", "Admin");
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, Mock.Of<ITempDataProvider>());
        return controller;
    }

    private static ApplicationDbContext CreateInterceptedDbContext(FailingSaveChangesInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(interceptor)
            .Options;
        return new ApplicationDbContext(options, new ServiceCollection().BuildServiceProvider());
    }

    private static async Task<TransportProfile> AddEditableProfileAsync(ApplicationDbContext db)
    {
        var profile = new TransportProfile
        {
            Id = Guid.NewGuid(), Key = "scooter", Label = "Scooter", Category = "Road",
            PlanningSpeedKmh = 18, SortOrder = 45, IsActive = true
        };
        db.Set<TransportProfile>().Add(profile);
        await db.SaveChangesAsync();
        return profile;
    }

    private static TransportProfileEditViewModel BuildEditModel(TransportProfile profile, string label) => new()
    {
        Id = profile.Id, Key = profile.Key, Label = label, Category = profile.Category,
        PlanningSpeedKmh = profile.PlanningSpeedKmh, SortOrder = profile.SortOrder,
        IsActive = profile.IsActive, WasActive = profile.IsActive, RowVersion = profile.RowVersion
    };

    private static PostgresException CreatePostgresException(string sqlState, string? constraintName = null) => new(
        "provider failure", "ERROR", "ERROR", sqlState, null!, null!, 0, 0, null!, null!,
        "public", "Segments", "TransportProfileId", null!, constraintName!, "postgres.c", "1", "routine");

    /// <summary>Injects a provider-independent persistence failure after fixture setup.</summary>
    private sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public bool Fail { get; set; }
        public DbUpdateException Exception { get; set; } = new("database failure", new InvalidOperationException("connection interrupted"));

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            Fail
                ? ValueTask.FromException<InterceptionResult<int>>(Exception)
                : ValueTask.FromResult(result);
    }

    /// <summary>Captures rendered operational log messages for bounded-content assertions.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
