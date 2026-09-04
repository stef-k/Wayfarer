using Xunit;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Services;

namespace Wayfarer.Tests.Views;

/// <summary>Guards the responsive Location address disclosure and shared project link.</summary>
public sealed class LocationEditViewContractTests
{
    /// <summary>The real MVC hidden-input formatter must preserve the database timestamp exactly.</summary>
    [Fact]
    public void EditHiddenTimestampRoundTripsWithoutInventingAConflict()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews();
        using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var timestamp = DateTimeOffset.Parse("2026-09-04T18:12:34.1234560+00:00", CultureInfo.InvariantCulture);
        var model = new AddLocationViewModel { OriginalReverseGeocodedAt = timestamp };
        var metadata = services.GetRequiredService<IModelMetadataProvider>();
        var data = new ViewDataDictionary<AddLocationViewModel>(metadata, new ModelStateDictionary()) { Model = model };
        var context = new ViewContext
        {
            ViewData = data,
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        var input = Regex.Match(Read("Areas", "User", "Views", "Location", "Edit.cshtml"),
            "<input[^>]*asp-for=\"OriginalReverseGeocodedAt\"[^>]*>").Value;
        Assert.NotEmpty(input);
        var format = Regex.Match(input, "asp-format=\"([^\"]+)\"");
        var generator = services.GetRequiredService<IHtmlGenerator>();
        var explorer = metadata.GetModelExplorerForType(typeof(DateTimeOffset?), timestamp);
        // InputTagHelper formats the value before passing it to GenerateHidden.
        var value = format.Success ? string.Format(CultureInfo.CurrentCulture, format.Groups[1].Value, timestamp) : (object)timestamp;
        var tag = generator.GenerateHidden(context, explorer, nameof(model.OriginalReverseGeocodedAt), value, false, null);
        model.OriginalReverseGeocodedAt = DateTimeOffset.Parse(tag.Attributes["value"]!, CultureInfo.CurrentCulture);
        var location = new Location { ReverseGeocodedAt = timestamp };
        Assert.True(LocationManualAddressEdit.HasCurrentProviderTuple(location, model));
        location.ReverseGeocodedAt = timestamp.AddTicks(10);
        Assert.False(LocationManualAddressEdit.HasCurrentProviderTuple(location, model));
    }

    [Fact]
    public void EditViewUsesResponsiveAddressDisclosureWithoutNativeAlerts()
    {
        var source = Read("Areas", "User", "Views", "Location", "Edit.cshtml");

        Assert.Contains("<details class=\"border rounded my-2\"", source);
        Assert.Contains("col-12 col-lg-8", source);
        Assert.Contains("col-12 col-md-6", source);
        Assert.Contains("expandAddress ? \"open\" : null", source);
        Assert.Contains("string.Join(\", \"", source);
        Assert.Contains("resolved to local-area level", source);
        foreach (var field in new[] { "FullAddress", "Address", "StreetName", "AddressNumber",
                     "PostCode", "Place", "Region", "Country" })
            Assert.Contains($"asp-for=\"{field}\"", source);
        Assert.DoesNotContain("alert(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFooterLinksSafelyToProject()
    {
        var source = Read("Views", "Shared", "_Layout.cshtml");

        Assert.Contains("href=\"https://github.com/stef-k/Wayfarer\"", source);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", source);
        Assert.Contains("aria-label=\"Wayfarer source code on GitHub\"", source);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts])));
}
