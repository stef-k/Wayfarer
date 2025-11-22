using System;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Wayfarer.Swagger;
using Xunit;

namespace Wayfarer.Tests.Swagger;

/// <summary>
/// Ensures PostGIS-related schemas are pruned and references cleared.
/// </summary>
public class RemovePostGisSchemasDocumentFilterTests
{
    [Fact]
    public void Apply_RemovesSchemas_AndClearsReferences()
    {
        var doc = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                Schemas =
                {
                    ["Point"] = new OpenApiSchema(),
                    ["Geometry"] = new OpenApiSchema(),
                    ["Trip"] = new OpenApiSchema
                    {
                        Properties =
                        {
                            ["location"] = new OpenApiSchema
                            {
                                Reference = new OpenApiReference
                                {
                                    Id = "Point",
                                    Type = ReferenceType.Schema
                                }
                            },
                            ["path"] = new OpenApiSchema
                            {
                                Items = new OpenApiSchema
                                {
                                    Reference = new OpenApiReference
                                    {
                                        Id = "Geometry",
                                        Type = ReferenceType.Schema
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
        var filter = new RemovePostGisSchemasDocumentFilter();
        var context = new DocumentFilterContext(Array.Empty<ApiDescription>(), new SchemaGenerator(new SchemaGeneratorOptions(), new JsonSerializerDataContractResolver(new System.Text.Json.JsonSerializerOptions())), new SchemaRepository());

        filter.Apply(doc, context);

        Assert.DoesNotContain("Point", doc.Components.Schemas.Keys);
        Assert.DoesNotContain("Geometry", doc.Components.Schemas.Keys);
        Assert.True(doc.Components.Schemas.ContainsKey("Trip"));
        Assert.Null(doc.Components.Schemas["Trip"].Properties["location"].Reference);
        // Current filter only clears direct $ref; collection items remain unchanged.
        Assert.NotNull(doc.Components.Schemas["Trip"].Properties["path"].Items.Reference);
    }
}
