using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations
{
    /// <inheritdoc />
    public partial class CorrectConcurrentCompatibilityProfileCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public."SetSegmentTransportProfile"() RETURNS trigger AS $$
                DECLARE
                    normalized_key text;
                    resolved_id uuid;
                BEGIN
                    IF NEW."Mode" IS NULL OR btrim(NEW."Mode") = '' THEN
                        NEW."TransportProfileId" := NULL;
                        RETURN NEW;
                    END IF;

                    normalized_key := CASE
                        WHEN length(lower(btrim(NEW."Mode"))) <= 80 THEN lower(btrim(NEW."Mode"))
                        ELSE 'legacy-' || md5(NEW."Mode")
                    END;
                    SELECT "Id" INTO resolved_id FROM public."TransportProfiles" WHERE "Key" = normalized_key;
                    IF resolved_id IS NULL THEN
                        resolved_id := (substr(md5('transport-profile:' || normalized_key), 1, 8) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 9, 4) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 13, 4) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 17, 4) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 21, 12))::uuid;
                        INSERT INTO public."TransportProfiles"
                            ("Id", "Key", "Label", "Category", "PlanningSpeedKmh", "SortOrder", "IsActive", "Description", "IsSeeded")
                        VALUES
                            (resolved_id, normalized_key, 'Legacy: ' || left(btrim(NEW."Mode"), 112), 'Legacy', NULL, 10000, FALSE,
                             'Inactive compatibility profile preserving an unknown segment mode.', FALSE)
                        ON CONFLICT DO NOTHING;
                        SELECT "Id" INTO resolved_id FROM public."TransportProfiles" WHERE "Key" = normalized_key;
                        IF resolved_id IS NULL THEN
                            RAISE EXCEPTION 'Derived transport profile identity is already assigned to a different key.'
                                USING ERRCODE = '23505';
                        END IF;
                    END IF;
                    NEW."TransportProfileId" := resolved_id;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION public."SetSegmentTransportProfile"() RETURNS trigger AS $$
                DECLARE
                    normalized_key text;
                    resolved_id uuid;
                BEGIN
                    IF NEW."Mode" IS NULL OR btrim(NEW."Mode") = '' THEN
                        NEW."TransportProfileId" := NULL;
                        RETURN NEW;
                    END IF;

                    normalized_key := CASE
                        WHEN length(lower(btrim(NEW."Mode"))) <= 80 THEN lower(btrim(NEW."Mode"))
                        ELSE 'legacy-' || md5(NEW."Mode")
                    END;
                    SELECT "Id" INTO resolved_id FROM public."TransportProfiles" WHERE "Key" = normalized_key;
                    IF resolved_id IS NULL THEN
                        -- A derived UUID collision with a different key is an integrity failure;
                        -- only a concurrent insert of this exact normalized key may be reused.
                        resolved_id := (substr(md5('transport-profile:' || normalized_key), 1, 8) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 9, 4) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 13, 4) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 17, 4) || '-' ||
                            substr(md5('transport-profile:' || normalized_key), 21, 12))::uuid;
                        INSERT INTO public."TransportProfiles"
                            ("Id", "Key", "Label", "Category", "PlanningSpeedKmh", "SortOrder", "IsActive", "Description", "IsSeeded")
                        VALUES
                            (resolved_id, normalized_key, 'Legacy: ' || left(btrim(NEW."Mode"), 112), 'Legacy', NULL, 10000, FALSE,
                             'Inactive compatibility profile preserving an unknown segment mode.', FALSE)
                        ON CONFLICT ("Key") DO NOTHING;
                        SELECT "Id" INTO resolved_id FROM public."TransportProfiles" WHERE "Key" = normalized_key;
                    END IF;
                    NEW."TransportProfileId" := resolved_id;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}
