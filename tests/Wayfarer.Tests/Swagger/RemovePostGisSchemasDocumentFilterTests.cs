using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using NetTopologySuite.Geometries;
using Wayfarer.Swagger;
using Xunit;

namespace Wayfarer.Tests.Swagger;

/// <summary>Ensures PostGIS-related schemas and references are removed from OpenAPI output.</summary>
public sealed class RemovePostGisSchemasDocumentFilterTests
{
    [Fact]
    public async Task GeneratedDocument_IsOpenApi3WithApiPathsAndNoPostGisReferences()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(SwaggerContractController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(WayfarerSwaggerConfiguration.Configure);
        await using WebApplication application = builder.Build();
        application.UseSwagger();
        application.MapControllers();
        await application.StartAsync();

        string json = await application.GetTestClient().GetStringAsync("/swagger/v1/swagger.json");

        Assert.Contains("\"openapi\": \"3.0", json, StringComparison.Ordinal);
        Assert.Contains("/api/swagger-contract", json, StringComparison.Ordinal);
        Assert.DoesNotContain("#/components/schemas/Point", json, StringComparison.Ordinal);
        Assert.DoesNotContain("#/components/schemas/Geometry", json, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"wkt\"", json, StringComparison.Ordinal);
        Assert.Contains("48.8588443, 2.2943506", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RemovesSchemasAndDirectOrCollectionReferences()
    {
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>
                {
                    ["Point"] = new OpenApiSchema(),
                    ["Geometry"] = new OpenApiSchema(),
                    ["Trip"] = new OpenApiSchema
                    {
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["location"] = new OpenApiSchema(),
                            ["path"] = new OpenApiSchema { Items = new OpenApiSchema() }
                        }
                    }
                }
            }
        };
        var trip = (OpenApiSchema)document.Components.Schemas["Trip"];
        trip.Properties!["location"] = new OpenApiSchemaReference("Point", document, null);
        ((OpenApiSchema)trip.Properties["path"]).Items = new OpenApiSchemaReference("Geometry", document, null);
        var context = new DocumentFilterContext(
            Array.Empty<ApiDescription>(),
            new SchemaGenerator(
                new SchemaGeneratorOptions(),
                new JsonSerializerDataContractResolver(new System.Text.Json.JsonSerializerOptions())),
            new SchemaRepository());

        new RemovePostGisSchemasDocumentFilter().Apply(document, context);

        Assert.DoesNotContain("Point", document.Components.Schemas.Keys);
        Assert.DoesNotContain("Geometry", document.Components.Schemas.Keys);
        var filteredTrip = Assert.IsType<OpenApiSchema>(document.Components.Schemas["Trip"]);
        Assert.IsType<OpenApiSchema>(filteredTrip.Properties!["location"]);
        Assert.IsType<OpenApiSchema>(Assert.IsType<OpenApiSchema>(filteredTrip.Properties["path"]).Items);
    }
}

/// <summary>Provides one API action for generated-document contract validation.</summary>
[ApiController]
[Area("Api")]
[Route("api/swagger-contract")]
public sealed class SwaggerContractController : ControllerBase
{
    /// <summary>Returns a point so the production schema mapping is generated.</summary>
    [HttpGet]
    public ActionResult<Point> Get() => new Point(2.2943506, 48.8588443);
}
