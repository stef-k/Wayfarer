namespace Wayfarer.Util;

/// <summary>Shared bounded catalog observation for capture consistency, never release compatibility.</summary>
internal static class QuartzSnapshot
{
    /// <summary>Schema-2 capture retains its original physical-order algorithm verbatim.</summary>
    internal const string LegacySql = """
        SELECT md5(string_agg(table_name || ':' || column_name || ':' || data_type || ':' || is_nullable,
            '|' ORDER BY table_name, ordinal_position)) AS "Value"
        FROM information_schema.columns WHERE table_schema=current_schema() AND left(table_name,5)='qrtz_'
        """;

    /// <summary>Schema-3 hashes logical column facts, including type bounds and defaults, in C identifier order.</summary>
    internal const string CanonicalSql = """
        SELECT md5(string_agg(jsonb_build_array(table_name, column_name, data_type, udt_schema, udt_name,
            is_nullable, character_maximum_length, numeric_precision, numeric_scale, datetime_precision,
            column_default, is_identity, identity_generation, is_generated, generation_expression)::text,
            '|' ORDER BY table_name COLLATE "C", column_name COLLATE "C")) AS "Value"
        FROM information_schema.columns WHERE table_schema=current_schema() AND left(table_name,5)='qrtz_'
        """;
}
