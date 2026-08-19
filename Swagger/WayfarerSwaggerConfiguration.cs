using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using NetTopologySuite.Geometries;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Wayfarer.Swagger;

/// <summary>Defines the public Wayfarer OpenAPI document contract.</summary>
public static class WayfarerSwaggerConfiguration
{
    /// <summary>Applies Wayfarer's API document and schema rules.</summary>
    public static void Configure(SwaggerGenOptions options)
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "Wayfarer API" });
        options.MapType<Point>(() => new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Format = "wkt",
            Description = "The coordinates in WKT format (Point)",
            Example = JsonValue.Create("48.8588443, 2.2943506")
        });
        options.DocumentFilter<RemovePostGisSchemasDocumentFilter>();
        options.DocInclusionPredicate((_, apiDescription) =>
            apiDescription.ActionDescriptor.RouteValues.TryGetValue("area", out string? area) &&
            area == "Api");
    }
}
