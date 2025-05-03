using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Wayfarer.Swagger
{
    public class RemovePostGisSchemasDocumentFilter : IDocumentFilter
    {
        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            // List of PostGIS-related types to hide
            string[] postGisTypes = new[]
            {
                "Coordinate", "CoordinateEqualityComparer", "CoordinateSequence", "CoordinateSequenceFactory",
                "Dimension", "Envelope", "Geometry", "GeometryFactory", "GeometryOverlay",
                "NtsGeometryServices", "OgcGeometryType", "Ordinates", "Point",
                "PrecisionModel", "PrecisionModels"
            };

            // Remove schemas from the Swagger document that are in the PostGIS-related types list
            foreach (string type in postGisTypes)
            {
                if (swaggerDoc.Components.Schemas.ContainsKey(type))
                {
                    swaggerDoc.Components.Schemas.Remove(type);
                }
            }

            // Now iterate through all schemas and remove references to the removed types
            foreach (OpenApiSchema? schema in swaggerDoc.Components.Schemas.Values)
            {
                RemoveInvalidReferences(schema, postGisTypes);
            }
        }

        private void RemoveInvalidReferences(OpenApiSchema schema, string[] postGisTypes)
        {
            // Iterate through all properties of the schema
            if (schema.Properties != null)
            {
                foreach (OpenApiSchema? property in schema.Properties.Values)
                {
                    // Check if the property is a reference ($ref)
                    if (property.Reference != null && postGisTypes.Contains(property.Reference.Id))
                    {
                        // Set it to null or a default schema
                        property.Reference = null; // Or replace with another schema if necessary
                    }
                }
            }

            // If there are any items in array type, check recursively for $ref in items as well
            if (schema.Items != null && schema.Items.Reference != null && postGisTypes.Contains(schema.Items.Reference.Id))
            {
                schema.Items.Reference = null; // Or replace with another schema if necessary
            }
        }
    }
}
