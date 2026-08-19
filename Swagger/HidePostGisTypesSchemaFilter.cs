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

        SanitizeNestedSchemas(mutableSchema.Properties, visited);
        SanitizeNestedSchemas(mutableSchema.PatternProperties, visited);
        SanitizeNestedSchemas(mutableSchema.Definitions, visited);

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

        if (mutableSchema.UnevaluatedPropertiesSchema != null)
        {
            mutableSchema.UnevaluatedPropertiesSchema =
                SanitizeNestedSchema(mutableSchema.UnevaluatedPropertiesSchema, visited);
        }
    }

    /// <summary>Sanitizes mutable schema dictionary values in place while preserving keys and ordering.</summary>
    private static void SanitizeNestedSchemas(
        IDictionary<string, IOpenApiSchema>? schemas,
        HashSet<IOpenApiSchema> visited)
    {
        if (schemas == null) return;

        foreach (string name in schemas.Keys.ToArray())
        {
            schemas[name] = SanitizeNestedSchema(schemas[name], visited);
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
