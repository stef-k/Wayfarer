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
    public async Task EditHiddenTimestampRoundTripsWithoutInventingAConflict()
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
            FormContext = new FormContext(),
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        var input = Regex.Match(Read("Areas", "User", "Views", "Location", "Edit.cshtml"),
            "<input[^>]*asp-for=\"OriginalReverseGeocodedAt\"[^>]*>").Value;
        Assert.NotEmpty(input);
        var format = Regex.Match(input, "asp-format=\"([^\"]+)\"");
        var helper = new Microsoft.AspNetCore.Mvc.TagHelpers.InputTagHelper(
            services.GetRequiredService<IHtmlGenerator>())
        {
            ViewContext = context,
            For = new ModelExpression(nameof(model.OriginalReverseGeocodedAt),
                data.ModelExplorer.GetExplorerForProperty(nameof(model.OriginalReverseGeocodedAt))),
            InputTypeName = "hidden",
            Format = format.Success ? format.Groups[1].Value : null
        };
        var attributes = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList
        {
            { "type", "hidden" }, { "asp-for", helper.For }
        };
        var output = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperOutput("input", attributes,
            (_, _) => Task.FromResult<Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContent>(
                new Microsoft.AspNetCore.Razor.TagHelpers.DefaultTagHelperContent()));
        helper.Process(new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContext(attributes,
            new Dictionary<object, object>(), "timestamp"), output);
        var rendered = output.Attributes["value"].Value.ToString()!;
        var action = new ActionContext(context.HttpContext, new RouteData(), new ActionDescriptor());
        var modelMetadata = metadata.GetMetadataForType(typeof(AddLocationViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(
            new ModelBinderFactoryContext { Metadata = modelMetadata, CacheToken = typeof(AddLocationViewModel) });
        var binding = DefaultModelBindingContext.CreateBindingContext(action,
            new FormValueProvider(BindingSource.Form, new FormCollection(
                new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                { [nameof(model.OriginalReverseGeocodedAt)] = rendered }), CultureInfo.CurrentCulture),
            modelMetadata, null, "");
        await binder.BindModelAsync(binding);
        model = Assert.IsType<AddLocationViewModel>(binding.Result.Model);
        var location = new Location { UserId = "synthetic", TimeZoneId = "UTC", Coordinates = new NetTopologySuite.Geometries.Point(0, 0), ReverseGeocodedAt = timestamp };
        var current = LocationManualAddressEdit.HasCurrentProviderTuple(location, model);
        Assert.True(current, $"Original={timestamp:O}; rendered={rendered}; bound={model.OriginalReverseGeocodedAt:O}; errors={string.Join(", ", action.ModelState.Values.SelectMany(entry => entry.Errors).Select(error => error.ErrorMessage))}; current={current}");
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
