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
                mutableSchema.Properties[name] = SanitizeNestedSchema(mutableSchema.Properties[name], visited);
            }
        }

        if (mutableSchema.Items != null)
        {
            mutableSchema.Items = SanitizeNestedSchema(mutableSchema.Items, visited);
        }

        SanitizeNestedSchemas(mutableSchema.AllOf, visited);
        SanitizeNestedSchemas(mutableSchema.AnyOf, visited);
        SanitizeNestedSchemas(mutableSchema.OneOf, visited);

        if (mutableSchema.Not != null)
        {
            mutableSchema.Not = SanitizeNestedSchema(mutableSchema.Not, visited);
        }

        if (mutableSchema.AdditionalProperties != null)
        {
            mutableSchema.AdditionalProperties = SanitizeNestedSchema(mutableSchema.AdditionalProperties, visited);
        }
    }

    /// <summary>Sanitizes a mutable schema list in place while preserving its shape and ordering.</summary>
    private static void SanitizeNestedSchemas(IList<IOpenApiSchema>? schemas, HashSet<IOpenApiSchema> visited)
    {
        if (schemas == null) return;

        for (int index = 0; index < schemas.Count; index++)
        {
            schemas[index] = SanitizeNestedSchema(schemas[index], visited);
        }
    }

    /// <summary>Replaces a removed reference or recursively sanitizes an otherwise retained schema.</summary>
    private static IOpenApiSchema SanitizeNestedSchema(
        IOpenApiSchema schema,
        HashSet<IOpenApiSchema> visited)
    {
        if (ReferencesRemovedType(schema)) return new OpenApiSchema();

        RemoveInvalidReferences(schema, visited);
        return schema;
    }

    private static bool ReferencesRemovedType(IOpenApiSchema schema)
        => schema is OpenApiSchemaReference reference &&
           reference.Reference.Id is string id &&
           PostGisTypes.Contains(id);
}
