using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wayfarer.Migrations;

/// <summary>Atomically reconciles catalog identity and retains every removed client ID.</summary>
public partial class ActivityCatalogIntegrity : Migration
{
    /// <summary>Requires quiesced writers and compatible API/Admin code in the same deployment.</summary>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $precondition$
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                   OR current_setting('server_version_num')::integer / 10000 <> 18 THEN
                    RAISE EXCEPTION 'ACTIVITY_REQUIRES_PG18_UTF8';
                END IF;
            END $precondition$;

            -- This version is immutable: never replace its semantics under stored generated keys.
            CREATE FUNCTION public.wayfarer_activity_name_v1(raw text, identity boolean)
            RETURNS text LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
            SET search_path = pg_catalog AS $name$
            DECLARE
                display text;
            BEGIN
                -- Unicode 16 White_Space, applied only at the two ends.
                display := pg_catalog.btrim(raw, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000');
                IF display COLLATE pg_catalog."C" ~ U&'[\0001-\0008\000B-\000C\000E-\001F\FFFE\FFFF]' THEN
                    RETURN NULL;
                END IF;
                display := normalize(display, NFC);
                IF identity THEN
                    RETURN normalize(pg_catalog.casefold(display COLLATE pg_catalog.pg_unicode_fast), NFC);
                END IF;
                RETURN display;
            END $name$;

            -- IF NOT EXISTS supports re-Up after the deliberately lossy Down retains redirects.
            CREATE TABLE IF NOT EXISTS public."ActivityTypeIdRedirects" (
                "LegacyId" integer CONSTRAINT "PK_ActivityTypeIdRedirects" PRIMARY KEY,
                "ActivityTypeId" integer NOT NULL,
                CONSTRAINT "CK_ActivityTypeIdRedirects_Ids" CHECK
                    ("LegacyId" > 0 AND "ActivityTypeId" > 0 AND "LegacyId" <> "ActivityTypeId"),
                CONSTRAINT "FK_ActivityTypeIdRedirects_ActivityTypes_ActivityTypeId"
                    FOREIGN KEY ("ActivityTypeId") REFERENCES public."ActivityTypes"("Id")
                    ON DELETE NO ACTION NOT DEFERRABLE
            );
            LOCK TABLE public."ActivityTypes", public."Locations", public."ActivityTypeIdRedirects"
                IN ACCESS EXCLUSIVE MODE;

            -- Fail closed on new references or weakened existing FK semantics.
            DO $inventory$
            DECLARE reference record;
            BEGIN
                FOR reference IN SELECT * FROM pg_catalog.pg_constraint
                    WHERE contype = 'f' AND confrelid = 'public."ActivityTypes"'::regclass
                LOOP
                    IF reference.conrelid NOT IN ('public."Locations"'::regclass, 'public."ActivityTypeIdRedirects"'::regclass)
                       OR reference.conkey <> ARRAY[(SELECT attnum FROM pg_catalog.pg_attribute
                           WHERE attrelid = reference.conrelid AND attname = 'ActivityTypeId')]
                       OR reference.confkey <> ARRAY[(SELECT attnum FROM pg_catalog.pg_attribute
                           WHERE attrelid = reference.confrelid AND attname = 'Id')]
                       OR reference.confdeltype <> 'a' OR reference.confupdtype <> 'a'
                       OR reference.condeferrable OR NOT reference.convalidated THEN
                        RAISE EXCEPTION 'ACTIVITY_UNACCOUNTED_FK constraint=%', reference.conname;
                    END IF;
                END LOOP;
                IF (SELECT count(*) FROM pg_catalog.pg_constraint WHERE contype = 'f'
                    AND confrelid = 'public."ActivityTypes"'::regclass) <> 2 THEN
                    RAISE EXCEPTION 'ACTIVITY_MISSING_FK';
                END IF;
            END $inventory$;

            CREATE TEMP TABLE activity_cleanup ON COMMIT DROP AS
            SELECT "Id", "Name", "Description",
                   public.wayfarer_activity_name_v1("Name", false) AS display,
                   public.wayfarer_activity_name_v1("Name", true) COLLATE "C" AS key,
                   min("Id") OVER (PARTITION BY public.wayfarer_activity_name_v1("Name", true) COLLATE "C") AS survivor
            FROM public."ActivityTypes";

            DO $validity$
            DECLARE invalid_id integer;
            BEGIN
                SELECT "Id" INTO invalid_id FROM activity_cleanup
                    WHERE "Id" <= 0 OR display IS NULL OR display = '' ORDER BY "Id" LIMIT 1;
                IF FOUND THEN RAISE EXCEPTION 'ACTIVITY_INVALID_LEGACY_NAME_OR_ID id=%', invalid_id; END IF;
                IF EXISTS (SELECT 1 FROM public."ActivityTypeIdRedirects" r
                    JOIN public."ActivityTypes" a ON a."Id" = r."LegacyId") THEN
                    RAISE EXCEPTION 'ACTIVITY_REDIRECT_LIVE_ID_OVERLAP';
                END IF;
            END $validity$;

            -- Prove full-key B-tree capacity before modifying product rows. No name length ceiling.
            CREATE TEMP TABLE activity_keys ON COMMIT DROP AS SELECT DISTINCT key COLLATE "C" FROM activity_cleanup;
            CREATE UNIQUE INDEX ON activity_keys (key);

            -- Exact C equality preserves distinct prose; first source ID owns exact repeats.
            CREATE TEMP TABLE activity_descriptions ON COMMIT DROP AS
            SELECT DISTINCT ON (survivor, "Description" COLLATE "C") survivor, "Id", "Name", "Description"
            FROM activity_cleanup
            WHERE pg_catalog.btrim("Description", U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000') <> ''
            ORDER BY survivor, "Description" COLLATE "C", "Id";
            CREATE TEMP TABLE activity_survivors ON COMMIT DROP AS
            SELECT s."Id", s.display,
                   coalesce(primary_text."Description", s."Description") || coalesce(extras.prose, '') AS description
            FROM activity_cleanup s
            LEFT JOIN LATERAL (
                SELECT d."Description" FROM activity_descriptions d WHERE d.survivor = s."Id"
                ORDER BY (d."Description" COLLATE "C" = s."Description" COLLATE "C") DESC NULLS LAST, d."Id" LIMIT 1
            ) primary_text ON true
            LEFT JOIN LATERAL (
                SELECT string_agg(E'\n\nPreserved description from Activity ' || d."Id" || ' (' || d."Name" || E'):\n' || d."Description", '' ORDER BY d."Id") AS prose
                FROM activity_descriptions d WHERE d.survivor = s."Id"
                    AND d."Description" COLLATE "C" IS DISTINCT FROM primary_text."Description" COLLATE "C"
            ) extras ON true
            WHERE s."Id" = s.survivor;

            -- Retained guards are temporarily removed inside this same locked transaction for re-Up.
            DROP TRIGGER IF EXISTS activity_redirect_guard ON public."ActivityTypeIdRedirects";
            UPDATE public."ActivityTypes" a SET "Name" = s.display, "Description" = s.description
                FROM activity_survivors s WHERE a."Id" = s."Id";
            UPDATE public."Locations" l SET "ActivityTypeId" = c.survivor
                FROM activity_cleanup c WHERE l."ActivityTypeId" = c."Id" AND c."Id" <> c.survivor;
            UPDATE public."ActivityTypeIdRedirects" r SET "ActivityTypeId" = c.survivor
                FROM activity_cleanup c WHERE r."ActivityTypeId" = c."Id" AND c."Id" <> c.survivor;
            INSERT INTO public."ActivityTypeIdRedirects" ("LegacyId", "ActivityTypeId")
                SELECT "Id", survivor FROM activity_cleanup WHERE "Id" <> survivor;
            DELETE FROM public."ActivityTypes" a USING activity_cleanup c
                WHERE a."Id" = c."Id" AND c."Id" <> c.survivor;

            ALTER TABLE public."ActivityTypes" ADD COLUMN "NormalizedName" text COLLATE "C"
                GENERATED ALWAYS AS (public.wayfarer_activity_name_v1("Name", true)) STORED NOT NULL;
            ALTER TABLE public."ActivityTypes" ADD CONSTRAINT "CK_ActivityTypes_CanonicalName" CHECK
                (("Name" COLLATE "C" = public.wayfarer_activity_name_v1("Name", false) COLLATE "C" AND "Name" <> '') IS TRUE);
            CREATE UNIQUE INDEX "IX_ActivityTypes_NormalizedName" ON public."ActivityTypes" ("NormalizedName");
            CREATE INDEX IF NOT EXISTS "IX_ActivityTypeIdRedirects_ActivityTypeId" ON public."ActivityTypeIdRedirects" ("ActivityTypeId");

            -- Protect retired IDs even if a future operator resets the sequence or supplies an explicit ID.
            CREATE OR REPLACE FUNCTION public.wayfarer_activity_live_id_guard() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $guard$
            BEGIN
                IF EXISTS (SELECT 1 FROM public."ActivityTypeIdRedirects" WHERE "LegacyId" = NEW."Id") THEN
                    RAISE EXCEPTION 'ACTIVITY_RETIRED_ID' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $guard$;
            DROP TRIGGER IF EXISTS activity_live_id_guard ON public."ActivityTypes";
            CREATE TRIGGER activity_live_id_guard BEFORE INSERT OR UPDATE OF "Id" ON public."ActivityTypes"
                FOR EACH ROW EXECUTE FUNCTION public.wayfarer_activity_live_id_guard();
            CREATE OR REPLACE FUNCTION public.wayfarer_activity_redirect_guard() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $guard$
            BEGIN
                IF EXISTS (SELECT 1 FROM public."ActivityTypes" WHERE "Id" = NEW."LegacyId") THEN
                    RAISE EXCEPTION 'ACTIVITY_REDIRECT_LIVE_ID_OVERLAP' USING ERRCODE = '23514';
                END IF;
                RETURN NEW;
            END $guard$;
            CREATE TRIGGER activity_redirect_guard BEFORE INSERT OR UPDATE ON public."ActivityTypeIdRedirects"
                FOR EACH ROW EXECUTE FUNCTION public.wayfarer_activity_redirect_guard();

            DO $sequence$
            DECLARE sequence_name text; current_id bigint; called boolean; high_water bigint;
            BEGIN
                sequence_name := pg_catalog.pg_get_serial_sequence('public."ActivityTypes"', 'Id');
                IF sequence_name IS NULL THEN RAISE EXCEPTION 'ACTIVITY_MISSING_SEQUENCE'; END IF;
                EXECUTE format('SELECT last_value, is_called FROM %s', sequence_name) INTO current_id, called;
                SELECT greatest(coalesce(max("Id"), 0), current_id - CASE WHEN called THEN 0 ELSE 1 END,
                    coalesce((SELECT max("LegacyId") FROM public."ActivityTypeIdRedirects"), 0)) INTO high_water FROM activity_cleanup;
                PERFORM pg_catalog.setval(sequence_name::regclass, greatest(high_water, 1), high_water > 0);
            END $sequence$;
            """);
    }

    /// <summary>Lossy schema rollback retains descriptions, survivor FKs, redirects and retired-ID guards.</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_ActivityTypes_NormalizedName", table: "ActivityTypes");
        migrationBuilder.DropCheckConstraint(name: "CK_ActivityTypes_CanonicalName", table: "ActivityTypes");
        migrationBuilder.DropColumn(name: "NormalizedName", table: "ActivityTypes");
        migrationBuilder.Sql("DROP FUNCTION public.wayfarer_activity_name_v1(text, boolean);");
    }
}
