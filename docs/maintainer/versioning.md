---
title: Versioning
---

# Versioning

This page owns maintainer release sequencing, application version preparation,
release-path choice, the supported Compose/update floor and public acceptance policy.
Immutable release semantics belong to the [Release contract](release-contract.md);
application publication belongs to [Publication](publication.md); database
publication belongs to [Production Compose](production-compose.md).

## Application version source

`Version.props` is the application version source. `WayfarerVersion` is manually
updated there and supplies the normal MSBuild version, package version and assembly
informational version.

The compiled application exposes the same value through its version CLI and runtime
version surfaces. Validate it with:

~~~sh
dotnet run --no-launch-profile -- version
~~~

A release source, Git tag and published stable GitHub Release must agree on the same
`vX.Y.Z` identity before stable publication can proceed.

## Choose the release path

### Application-only release

Use this path when the accepted production database baseline is unchanged.

1. Prepare and validate the application version with the
   [release helper](#release-helper).
2. Obtain the repository-required exact-head CI, independent review and maintainer
   acceptance for the release source.
3. Create the intended stable tag/Release from that accepted source.
4. Follow [application publication](publication.md). The release workflow verifies
   accepted database evidence, publishes and anonymously qualifies native application
   images, binds their index, publishes stable bundles/bootstrap assets and performs
   public installation acceptance.
5. Preserve the resulting release evidence. Publication does not update an existing
   installation; operators use the normal [wayfarerctl](../self-hosting/wayfarerctl.md)
   lifecycle.

### Database-only release

Use this path when the production database image recipe changes without an application
release.

1. Review and merge the database recipe/tooling change under
   [Production Compose](production-compose.md).
2. With explicit database-publication authorization, dispatch the database workflow
   from the exact reviewed source. It publishes and anonymously qualifies native
   database manifests and then binds their immutable index.
3. Independently review the resulting evidence.
4. Promote the actual reviewed index artifact unchanged into
   `tools/release/database-release.json` in a separate reviewed source change.

Publication alone does not make a database image accepted release authority. Stable
application/bundle publication consumes the promoted `database-release.json`.

### Combined database and application release

Complete the database-only path, including reviewed evidence promotion, before
preparing and publishing the application release. The application release source
must contain the accepted database evidence it will consume.

A release sequence never authorizes a PostgreSQL-major migration or PostGIS extension
upgrade for an existing physical cluster. Those changes require their own tested
migration contract.

## Compose and update support boundary

The first supported Compose lifecycle/update baseline is **v1.9.21**. v1.9.20 remains
an immutable transitional publication, not a supported installation or update source.

Current operators reject public acquisition below the supported floor. For a later
stable target, release tooling selects an eligible supported predecessor at or above
v1.9.21 and requires exact compatibility evidence. It fails closed when no supported
predecessor or compatible authority exists.

Source-only/native releases are not implicit Compose update sources. A target bundle
must explicitly authorize the installed release authority; semantic-version ordering
alone is insufficient.

The maintained PostgreSQL major outside the pinned Compose image is PostgreSQL 18.
Production Compose separately pins PostgreSQL 18.6 + PostGIS 3.6.4 and rejects use of
an obsolete PG17 physical cluster as an ordinary update path.

## Public acceptance policy

Candidate, PR and disposable qualification prove their own test seams. They do not
prove that public users can acquire and operate the published bytes.

A stable release claim requires the publication-owned public evidence for the actual
release artifacts, including:

- anonymous exact-digest application image qualification on AMD64 and ARM64;
- stable bundle/bootstrap publication with immutable identities;
- fresh public setup acceptance on AMD64 and ARM64;
- the maintained post-publication lifecycle witness for update/recovery boundaries
  where that claim applies.

The continuous lifecycle witness is read-only with respect to release publication
and uses genuine supported stable releases. Fixture TLS, local certificate authorities,
candidate images and source-only tests remain narrower evidence and must be described
as such.

## Release helper

Use the repository-local helper:

~~~sh
python3 tools/release/version.py prepare <next-version>
python3 tools/release/version.py check
python3 tools/release/version.py check --require-tag
python3 tools/release/version.py check --require-github-release
~~~

`prepare` updates the version source and changelog release skeleton while preserving
existing unreleased and historical notes. Normal `check` is offline. Tag and GitHub
Release checks run only when explicitly requested.

The helper never creates, edits, publishes or deletes Git tags or GitHub Releases.
Those are separate maintainer actions after source acceptance.

When the release boundary changes, validate the release-helper tests, compiled version
surface, exact CLI output, diff hygiene and the publication-specific checks owned by
[Publication](publication.md).
