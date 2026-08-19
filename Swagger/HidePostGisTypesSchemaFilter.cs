using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Wayfarer.Swagger;

/// <summary>Removes PostGIS implementation schemas and references from the public API document.</summary>
public sealed class RemovePostGisSchemasDocumentFilter : IDocumentFilter
{
    private static readonly HashSet<string> PostGisTypes = new(StringComparer.Ordinal)
    {
        "Coordinate", "CoordinateEqualityComparer", "CoordinateSequence", "CoordinateSequenceFactory",
        "Dimension", "Envelope", "Geometry", "GeometryFactory", "GeometryOverlay",
        "NtsGeometryServices", "OgcGeometryType", "Ordinates", "Point",
        "PrecisionModel", "PrecisionModels"
    };

    /// <inheritdoc />
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var schemas = swaggerDoc.Components?.Schemas;
        if (schemas == null) return;

        foreach (string type in PostGisTypes)
        {
            schemas.Remove(type);
        }

        var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
        foreach (IOpenApiSchema schema in schemas.Values)
        {
            RemoveInvalidReferences(schema, visited);
        }
    }

    private static void RemoveInvalidReferences(IOpenApiSchema schema, HashSet<IOpenApiSchema> visited)
    {
        if (schema is not OpenApiSchema mutableSchema || !visited.Add(schema)) return;

        if (mutableSchema.Properties != null)
        {
            foreach (string name in mutableSchema.Properties.Keys.ToArray())
            {
                var property = mutableSchema.Properties[name];
                if (ReferencesRemovedType(property)) mutableSchema.Properties[name] = new OpenApiSchema();
                else RemoveInvalidReferences(property, visited);
            }
        }

        if (mutableSchema.Items != null)
        {
            if (ReferencesRemovedType(mutableSchema.Items)) mutableSchema.Items = new OpenApiSchema();
            else RemoveInvalidReferences(mutableSchema.Items, visited);
        }
    }

    private static bool ReferencesRemovedType(IOpenApiSchema schema)
        => schema is OpenApiSchemaReference reference &&
           reference.Reference.Id is string id &&
           PostGisTypes.Contains(id);
}
