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
public sealed class LocationEditViewContractTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>The real MVC hidden-input formatter must preserve the database timestamp exactly.</summary>
    [Fact]
    public async Task EditHiddenTimestampRoundTripsWithoutInventingAConflict()
    {
        var timestamp = new DateTimeOffset(2026, 9, 4, 18, 12, 34, TimeSpan.Zero).AddTicks(1234560);
        var model = new AddLocationViewModel { OriginalReverseGeocodedAt = timestamp };
        var rendered = RenderInput(model, new ModelStateDictionary(), nameof(model.OriginalReverseGeocodedAt));
        var (bound, state) = await BindAsync(new() { [nameof(model.OriginalReverseGeocodedAt)] = rendered });
        var location = new Location
        {
            UserId = "synthetic", TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(0, 0), ReverseGeocodedAt = timestamp
        };
        var current = LocationManualAddressEdit.HasCurrentProviderTuple(location, bound);
        output.WriteLine($"Original={timestamp:O}; rendered={rendered}; bound={bound.OriginalReverseGeocodedAt:O}; errors={state.ErrorCount}; current={current}");
        Assert.True(current, $"Original={timestamp:O}; rendered={rendered}; bound={bound.OriginalReverseGeocodedAt:O}; errors={state.ErrorCount}; current={current}");
        Assert.Equal(0, state.ErrorCount);
        location.ReverseGeocodedAt = timestamp.AddTicks(10);
        Assert.False(LocationManualAddressEdit.HasCurrentProviderTuple(location, bound));
        model.OriginalReverseGeocodedAt = null;
        rendered = RenderInput(model, new ModelStateDictionary(), nameof(model.OriginalReverseGeocodedAt));
        (bound, state) = await BindAsync(new() { [nameof(model.OriginalReverseGeocodedAt)] = rendered });
        location.ReverseGeocodedAt = null;
        Assert.Equal(0, state.ErrorCount);
        Assert.True(LocationManualAddressEdit.HasCurrentProviderTuple(location, bound));
    }

    /// <summary>Runs the production input attributes and property metadata through MVC's InputTagHelper.</summary>
    internal static string RenderInput(AddLocationViewModel model, ModelStateDictionary state, string field)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews();
        using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var metadata = services.GetRequiredService<IModelMetadataProvider>();
        var data = new ViewDataDictionary<AddLocationViewModel>(metadata, state) { Model = model };
        var context = new ViewContext
        {
            ViewData = data, FormContext = new FormContext(),
            HttpContext = new DefaultHttpContext { RequestServices = services }
        };
        var input = Regex.Match(Read("Areas", "User", "Views", "Location", "Edit.cshtml"),
            $"<input[^>]*asp-for=\"{field}\"[^>]*>").Value;
        Assert.NotEmpty(input);
        var format = Regex.Match(input, "asp-format=\"([^\"]+)\"");
        var type = Regex.Match(input, "type=\"([^\"]+)\"");
        var helper = new Microsoft.AspNetCore.Mvc.TagHelpers.InputTagHelper(
            services.GetRequiredService<IHtmlGenerator>())
        {
            ViewContext = context,
            For = new ModelExpression(field, data.ModelExplorer.GetExplorerForProperty(field)),
            InputTypeName = type.Success ? type.Groups[1].Value : null,
            Format = format.Success ? format.Groups[1].Value : null
        };
        var attributes = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperAttributeList
        {
            { "asp-for", helper.For }
        };
        if (type.Success) attributes.Add("type", helper.InputTypeName);
        var output = new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperOutput("input", attributes,
            (_, _) => Task.FromResult<Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContent>(
                new Microsoft.AspNetCore.Razor.TagHelpers.DefaultTagHelperContent()));
        helper.Process(new Microsoft.AspNetCore.Razor.TagHelpers.TagHelperContext(attributes,
            new Dictionary<object, object>(), field), output);
        return output.Attributes["value"].Value.ToString()!;
    }

    /// <summary>Binds posted form values using the registered MVC binder and retains realistic ModelState.</summary>
    internal static async Task<(AddLocationViewModel Model, ModelStateDictionary State)> BindAsync(
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> values)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddControllersWithViews();
        using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var action = new ActionContext(new DefaultHttpContext { RequestServices = services },
            new RouteData(), new ActionDescriptor());
        var metadata = services.GetRequiredService<IModelMetadataProvider>()
            .GetMetadataForType(typeof(AddLocationViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(
            new ModelBinderFactoryContext { Metadata = metadata, CacheToken = typeof(AddLocationViewModel) });
        var binding = DefaultModelBindingContext.CreateBindingContext(action,
            new FormValueProvider(BindingSource.Form, new FormCollection(values), CultureInfo.CurrentCulture),
            metadata, null, "");
        await binder.BindModelAsync(binding);
        services.GetRequiredService<Microsoft.AspNetCore.Mvc.ModelBinding.Validation.IObjectModelValidator>()
            .Validate(action, binding.ValidationState, "", binding.Result.Model);
        return (Assert.IsType<AddLocationViewModel>(binding.Result.Model), action.ModelState);
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
        Assert.Contains("data-address=\"@addressPresentation\"", source);
        Assert.DoesNotContain("asp-for=\"ProviderAddressLine1\"", source);
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
