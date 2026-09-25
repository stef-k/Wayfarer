#!/bin/sh
# Sourced by upstream entrypoint on an empty cluster only. No EF/Quartz mutation here.
set -eu
# Read the mounted secret server-side: never expand passwords into SQL or command arguments.
psql --username postgres --dbname postgres --set ON_ERROR_STOP=1 <<'SQL'
DO $$
DECLARE password text := rtrim(pg_read_file('/run/secrets/app-password'), E'\r\n');
BEGIN
    IF length(password) < 32 THEN RAISE EXCEPTION 'Application database password is too short'; END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'wayfarer') THEN
        EXECUTE format('CREATE ROLE wayfarer LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', password);
    END IF;
END $$;
SELECT 'CREATE DATABASE wayfarer OWNER wayfarer TEMPLATE template0 ENCODING ''UTF8'' LC_COLLATE ''C.UTF-8'' LC_CTYPE ''C.UTF-8'''
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'wayfarer')\gexec
\connect wayfarer
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS citext;
GRANT USAGE, CREATE ON SCHEMA public TO wayfarer;
SQL
