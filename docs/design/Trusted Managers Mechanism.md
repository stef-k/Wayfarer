Trusted Managers Mechanism — Detailed Design

Overview

- Goal: Implement a trusted user/manager mechanism so managers can see user locations in real time, scoped strictly to groups both parties belong to.
- Scope: End-to-end feature covering data model, invitations, membership, access control, UI for Managers and Users, API endpoints and SSE-backed live map, auditing for all actions.
- Constraints: Reuse existing infrastructure and code where possible; avoid duplication. Align with existing areas, services, and coding conventions.

Existing Infrastructure To Reuse

- Identity and roles: `Util/ApplicationRoles.cs` and Identity setup in `Program.cs` for role-based authorization (`Manager`, `User`).
- Base controller and auditing: `Controllers/BaseController.cs` provides `LogAudit(...)`, `SetAlert(...)`, and shared helpers; Manager/User controllers already derive from it.
- Location ingestion and retrieval:
  - API: `Areas/Api/Controllers/LocationController.cs` for logging locations, caching last location, and broadcasting SSE updates.
  - Services: `Services/LocationService.cs` and `Services/LocationStatsService.cs` for spatial queries and DTO shaping.
- Live updates: `Services/SseService.cs` and `Areas/Api/Controllers/SseController.cs` provide channel-based SSE streaming used by public timelines; we will subscribe per user channel(s) for group maps.
- Map/UI foundation: Leaflet usage and map modules in `wwwroot/js`, especially `wwwroot/js/map-utils.js` and the public timeline views in `Areas/Public/Views/UsersTimeline/*.cshtml` with JS at `wwwroot/js/Areas/Public/Timeline/*`.
- App settings: `Services/ApplicationSettingsService.cs` and `Models/ApplicationSettings.cs` for thresholds (used when rendering markers as “live”).

Functional Requirements Mapping

- Invitations with consent (TODO 1): Managers initiate invitations to add Users into a Group; Users must accept. Group type label chosen from predefined list [Organisation, Family, Friends] (configurable).
- Group access and membership lifecycle (TODO 2–4, 10, 12–15):
  - Both Managers and Users can see pending invitations.
  - Managers see “managed groups”; Users see “joined groups”.
  - Groups are private; only members can access.
  - Groups can be created by Managers or Users. If created by a User, they are owner with CRUD powers (member adds still require acceptance). If created by a Manager, any Manager added becomes a co-admin.
- Location visibility (TODO 5-7, 9):
  - After acceptance, Managers can see a User's live location.
  - Map view with left-side group/user picker and main map; can show one user or the entire group.
  - Managers only access data for groups they belong to.
  - Visibility by group type:
    - Friends/Family: Users can see all group members’ locations; managers can also see all.
    - Organization: Managers can see all members’ locations. Users may see other users’ locations only if a manager enables a group-level peer visibility setting; each user can disable their own access even when enabled.
  - User-facing map: Users have a map view similar to managers to see locations for groups they belong to and are authorized to view (Friends/Family always; Organization only when enabled by managers and personal access is not disabled).
- Cardinality (TODO 8–9): Many-to-many between Managers↔Groups and Users↔Groups.
- Auto-delete empty groups (TODO 11): Investigate and, if enabled, prune on last member leaving.
- Auditing (TODO 16): All actions [invite, join, remove, decline, leave] are logged via `BaseController.LogAudit`.
- Full feature completeness (TODO 17): Back-end logic, UI, and flows implemented across both Manager and User areas.

Non-Goals

- No public exposure of private group data.
- No changes to ingestion endpoints; only retrieval and UI for managers/users.
- No new front-end framework; continue with plain modern JavaScript modules.

Domain Model

- Group
  - Id (Guid)
  - Name (string, required): Creator-provided group name (e.g., “Acme Team”).
  - Type (string, required): One of allowed types (initially [Organisation, Family, Friends]). Enforced via validation and/or config.
  - CreatedByUserId (string, FK to `ApplicationUser`)
  - CreatedAt (UTC, DateTime)
  - IsAutoDeleteWhenEmpty (bool, default false; optional global default)
  - OrgPeerVisibilityEnabled (bool, default false; Organization only)

- GroupMember
  - Id (Guid)
  - GroupId (Guid, FK Group)
  - UserId (string, FK `ApplicationUser`)
  - Status (enum|string): `Active`, `Pending`, `Declined`, `Removed`
  - IsAdmin (bool): true if can manage group (owner/co-admin) — independent of Identity role; Identity still gates area access (Manager vs User account).
  - JoinedAt (DateTime, nullable)
  - LeftAt (DateTime, nullable)
  - OrgPeerVisibilityAccessDisabled (bool, default false; Organization only)

- GroupInvitation
  - Id (Guid)
  - GroupId (Guid)
  - InvitedUserId (string)
  - InvitedByUserId (string)
  - RoleOffered (enum|string): `User` or `Manager` at group-level
  - Status (enum|string): `Pending`, `Accepted`, `Declined`, `Cancelled`, `Expired`
  - CreatedAt (DateTime)
  - RespondedAt (DateTime, nullable)
  - ExpiresAt (DateTime, optional, for future use)

Relationships and Constraints

- Unique index: `GroupMember(GroupId, UserId)`
- Unique active invitation: enforce at most one pending invitation per `(GroupId, InvitedUserId)`.
- Status transitions:
  - Invitation: Pending → Accepted/Declined/Cancelled/Expired.
  - Member: Pending → Active; Active → Removed; Declined terminal.
- Deletion rules:
  - If auto-delete-empty is enabled, when the last member leaves or is removed, delete the group (soft or hard per business choice; default hard delete).

Migrations Plan

- Add new tables: `Groups`, `GroupMembers`, `GroupInvitations` with appropriate FKs and indices.
- No changes to existing `Location` schema; retrieval is filtered by member lists.
- Seed allowed group types into a configuration source:
  - Option A: App settings array (e.g., `GroupSettings:AllowedTypes`), read via options.
  - Option B: Simple lookup table `GroupTypes` (extensible by admin). Begin with Option A for minimal schema changes.
 - Add columns: `Groups.OrgPeerVisibilityEnabled` and `GroupMembers.OrgPeerVisibilityAccessDisabled`.

Authorization and Security

- Area-level auth unchanged: Manager area requires Identity role `Manager` (see `Areas/Manager/*` controllers with `[Authorize(Roles = "Manager")]`).
- Group-level access checks:
  - For every group action, verify the current user is a member of the group with `Active` status.
  - Admin actions (rename group, delete group, invite/remove members): allowed if `GroupMember.IsAdmin == true`.
  - When retrieving locations, enforce group-type visibility rules:
    - Friends/Family: Managers and Users may view all members’ locations within the group.
    - Organization: Managers may view all members. Users may view peers only if `OrgPeerVisibilityEnabled == true` and their own `OrgPeerVisibilityAccessDisabled == false`.
- Public vs private:
  - No public endpoints for group data; controllers reside under Manager/User areas or under `Api` with strict auth and group checks.
  - Do not rely on username-only endpoints like `Public/Users/Timeline`; use private APIs.

Backend Design

- Services (new, minimal and focused):
  - GroupService
    - CreateGroup(name, type, creatorUserId, isAutoDeleteWhenEmpty)
    - GetUserGroups(userId)
    - GetManagerGroups(managerUserId)
    - RenameGroup, DeleteGroup (enforce admin rights)
    - EnsureAccess(groupId, userId) helper
    - AutoDeleteIfEmpty(groupId) invoked on leave/remove
    - SetOrgPeerVisibilityEnabled(groupId, enabled)
    - SetMemberOrgPeerVisibilityAccessDisabled(groupId, userId, disabled)
  - InvitationService
    - CreateInvitation(groupId, invitedUserId, invitedByUserId, roleOffered)
    - GetPendingInvitationsForUser(userId)
    - GetPendingInvitationsForGroup(groupId)
    - AcceptInvitation(invitationId, userId)
    - DeclineInvitation(invitationId, userId)
    - CancelInvitation(invitationId, adminUserId)
  - LocationQueryFacade (wrapper around existing `LocationService` to fetch latest per user or filtered by bbox for multiple users):
    - GetLatestLocationsForUsers(IEnumerable<string> userIds)
    - GetLocationsForUsersInViewport(bbox, zoom, userIds)
    - Internally reuse `LocationService` methods and DTOs.

- Controllers
  - Manager area
    - GroupsController
      - Index: list managed groups; create group (type from allowed list), rename, delete (admin only)
      - Settings: toggle Organization peer visibility (group-level)
      - ManageMembers: list/add/remove managers and users; invite flow entry points
    - InvitationsController
      - Pending: manager-side view of outstanding invitations by group
      - Actions: cancel invitation
    - MapController (or reuse GroupsController with a Map action)
      - Map view: left panel (group/user picker) + map
      - JSON endpoints for fetching members and locations (could call `Api` endpoints internally)
  - User area
    - GroupsController
      - Index: joined groups; leave group; personal toggle for Organization peer-visibility access
      - Create (optional per requirement): user-created groups with owner admin rights
    - InvitationsController
      - Pending: user-side list of invitations; accept/decline
  - Api area (authorized)
    - GroupLocationsController
      - POST `/api/groups/{groupId}/locations/latest` → latest for all active members visible to a manager
      - POST `/api/groups/{groupId}/locations/query` → viewport-filtered paged results for selected users
      - GET `/api/groups/{groupId}/members` → minimal member roster (id, username, display name, role)
      - All endpoints enforce `EnsureAccess(groupId, currentUserId)` and role scoping.

- Auditing
  - Invoke `LogAudit` on: group create/rename/delete; invite create/cancel; accept/decline; member add/remove; leave.
- Include contextual details: group id/name, acting user, target user(s), outcome.
 - Also audit peer-visibility toggles (group-level and per-user) with previous/new values.

- SSE Integration
  - Existing channels pattern: `location-update-{username}` from `LocationController`.
  - Manager map subscribes to all selected users’ channels. On event, refresh only the marker for that user (no full reload).

Frontend/UI Design

- Manager area
  - Views
    - `Areas/Manager/Views/Groups/Index.cshtml` - list groups; create group modal (type dropdown from allowed list), rename/delete.
    - `Areas/Manager/Views/Groups/ManageMembers.cshtml` - roster by role (Managers/Users), invite search/add (username), pending invites.
    - `Areas/Manager/Views/Groups/Map.cshtml` - split layout: left vertical picker (group selection, user multi-select); right map. Renders markers consistent with the Chronological Timeline: green for last (non-live) user location and red for current live user location, reusing existing timeline logic and icons (`wwwroot/icons/location-latest-green.svg` and `wwwroot/icons/user-in-marker-red.svg`).
    - `Areas/Manager/Views/Groups/Settings.cshtml` - Organization-only toggle for peer visibility.
  - JS
    - `wwwroot/js/Areas/Manager/Groups/index.js` - CRUD for groups, form validation.
    - `wwwroot/js/Areas/Manager/Groups/members.js` - manage roster and invites.
    - `wwwroot/js/Areas/Manager/Groups/map.js` - builds Leaflet map using existing `map-utils.js`; subscribes to SSE for selected users; calls GroupLocations API; applies the same live/last marker logic as the chronological timeline (green vs red) and uses the same icons (`location-latest-green.svg`, `user-in-marker-red.svg`).
    - `wwwroot/js/Areas/Manager/Groups/settings.js` - toggle Organization peer visibility.

- User area
  - Views
    - `Areas/User/Views/Groups/Index.cshtml` - joined groups list with leave action.
    - `Areas/User/Views/Invitations/Index.cshtml` - pending invitations with accept/decline.
    - Optional: `Areas/User/Views/Groups/Create.cshtml` - user-created groups.
    - Organization-only: show personal switch to disable viewing others.
    - `Areas/User/Views/Groups/Map.cshtml` - same layout and behavior as Manager Map (group selector + user picker + map), but respects user visibility permissions (Friends/Family always; Organization only when enabled and personal access not disabled). Uses the same marker colors/icons (`location-latest-green.svg` for last, `user-in-marker-red.svg` for live).
  - JS
    - `wwwroot/js/Areas/User/Groups/index.js` - leave group, basic list actions.
    - `wwwroot/js/Areas/User/Invitations/index.js` - accept/decline flows.
    - `wwwroot/js/Areas/User/Groups/orgVisibilityToggle.js` - personal opt-out API call for Organization groups.
    - `wwwroot/js/Areas/User/Groups/map.js` - identical approach to Manager map.js: calls GroupLocations API, subscribes to per-user SSE channels, and applies live/last marker logic with the same icons.

- UX Reuse
  - Reuse map scaffolding from public timeline (`Areas/Public/Views/UsersTimeline/Timeline.cshtml`) and `wwwroot/js/Areas/Public/Timeline/index.js` patterns for map creation and marker updates.
  - Use Bootstrap layout for left picker pane; ensure mobile responsiveness.

API Contracts (high level)

- GET `/api/groups/{groupId}/members`
  - Response: [{ userId, username, displayName, groupRole, status }]

- POST `/api/groups/{groupId}/locations/latest`
  - Body: { includeUserIds?: string[] }
  - Response: [{ userId, latestLocation: PublicLocationDto-like }]

- POST `/api/groups/{groupId}/locations/query`
  - Body: { minLng, minLat, maxLng, maxLat, zoomLevel, userIds?: string[] }
  - Response: { data: PublicLocationDto-like[], totalItems }

- POST `/api/groups/{groupId}/settings/org-peer-visibility`
  - Body: { enabled: bool }
  - Auth: group admin; Type=Organisation

- POST `/api/groups/{groupId}/members/{userId}/org-peer-visibility-access`
  - Body: { disabled: bool }
  - Auth: same user only; Type=Organisation

Note: DTOs reuse existing shapes produced by `LocationService`/public timeline where possible; ensure internal endpoints are non-public and authorize per group membership.

Core Workflows

- Create Group
  - Actor: Manager or User.
  - Steps: Provide Name and Type (from allowed list) → Create `Group` with creator as `GroupMember(IsAdmin=true, Role = Manager/User respectively, Status=Active)` → Audit.

- Invite Member
  - Actor: Group admin (Manager creator or co-admin; or User owner for user-created groups).
  - Steps: Create `GroupInvitation(Pending)` → Audit.
  - User sees invitation in User/Invitations → Accept: add `GroupMember(Active)` and close invitation (Accepted) → Audit; Decline: mark invitation Declined → Audit.

- Add Manager Co-Admins
  - Actor: Group admin (Manager with admin rights).
  - Steps: Invite a Manager; upon Accept, mark `GroupMember.IsAdmin=true` and Role=`Manager`. All managers with `IsAdmin=true` can administer.

- Leave/Remove
  - User chooses leave: set member LeftAt and Status=Removed → If auto-delete-empty and no members remain, delete group → Audit.
  - Admin removes member: same state change and audit.

- Map Viewing
  - Manager selects group and (optionally) subset of users → Manager map requests latest or viewport-filtered locations for the selected user IDs → Mark latest location using thresholds from `ApplicationSettings` (already used by `LocationController`).
  - Subscribe to SSE channels `location-update-{username}` for all selected users; update markers on events.
  - Map/state parity with chronological timeline: same presentation and logic for last vs live locations; green marker for last (non-live) user location, red marker for current live user location. Reuse existing marker icons (wwwroot/icons/location-latest-green.svg, wwwroot/icons/user-in-marker-red.svg) and threshold logic from the user timeline implementation.
  - Friends/Family: users can view all members by default.
  - Organization: users can view others only when group-level is enabled and personal access is not disabled.

Validation and Business Rules

- Allowed group types are enforced by server-side validation from config.
- A user cannot be added to a group without accepting an invitation.

- Only group admins can invite or remove members, rename, or delete groups.
- Identity role `Manager` is required to access Manager area; Users manage their invitations and group membership in User area.
 - Organization peer visibility effective rule:
   - EffectiveUserPeerViewAllowed = Group.OrgPeerVisibilityEnabled AND NOT Member.OrgPeerVisibilityAccessDisabled.

Performance and Indexing

- Indices: `GroupMember(GroupId, UserId)` unique; optional indices on `GroupInvitation(GroupId, InvitedUserId, Status)` and `GroupMember(Status)`.
- Location retrieval: reuse `LocationService` which already uses PostGIS types and indices (`IX_Location_Coordinates`). For “latest per user,” prefer a query that selects `ORDER BY LocalTimestamp DESC LIMIT 1` per user, or a derived projection using `DISTINCT ON (UserId)` if needed.
- Caching: reuse in-memory caching of last location (see `LocationController`); optionally hydrate latest from cache first, then fall back to DB.

Security Considerations

- Validate all IDs against current user’s group membership.
- Never expose user locations via public routes for this feature.
- Rate limiting remains on ingestion; read endpoints are server-protected and group-scoped.
- Ensure all actions are audited with meaningful context.

Testing Strategy

- Unit tests
  - Services: GroupService, InvitationService (state transitions, permissions, auto-delete-empty behavior).
  - Validation: allowed group types, uniqueness, membership checks.
- Integration tests
  - Endpoints: members listing, latest/query locations with group checks.
  - SSE smoke: broadcast and client subscription behavior for multiple users (where feasible).
- UI smoke (manual initially): Manager group CRUD, member invites, map rendering and live marker updates; User accept/decline/leave.

Rollout Plan

- Phase 1: Schema migrations and feature flags (hide UI by default).
- Phase 2: Backend services and private APIs.
- Phase 3: Manager UI (Groups, Members, Map) using existing map utilities.
- Phase 4: User UI (Invitations, Groups).
- Phase 5: Enable feature flag and collect logs; iterate.

Risks and Open Questions

- Allowed group types source: App settings vs DB-managed. Start with appsettings for simplicity; consider admin UI later.
- Auto-delete-empty behavior: Hard delete is simplest; soft delete may be preferable for auditability (we already keep audit logs). Decision: start with hard delete guarded by setting.
- Manager accounts do not have user/timeline features: Managers won’t normally produce location data or view a “manager’s” own timeline; if a manager needs user capabilities, they should use a separate account with Identity role `User` (consistent with current app behavior).
- SSE scalability with many selected users: The client subscribes per-user channel; for large groups consider a group-level SSE aggregation channel later.
- Invitation expiry: Not required now; structure allows adding ExpiresAt.

Implementation Checklist (toward 100% completeness)

- Data model and migrations for `Groups`, `GroupMembers`, `GroupInvitations` with indices.
- Configuration for allowed group types; server-side validation.
- Services: GroupService, InvitationService, LocationQueryFacade (wrapping `LocationService`).
- Manager area controllers and views (Groups, Members/Invitations, Map) with JS modules.
- User area controllers and views (Groups, Invitations) with JS modules.
- API endpoints for members and locations scoped by group and current user.
- SSE client integration in Manager map (subscribe to selected users’ `location-update-{username}` channels).
- Comprehensive auditing for all state-changing actions.
- Authorization checks at controller/service boundaries for every action and query.
- Documentation updates for settings and usage.

Reuse Summary (to avoid duplication)

- Reuse `LocationService` and DTO shaping — do not create new location query logic.
- Reuse SSE service and event channel naming — do not build a new push system.
- Reuse BaseController for auditing, alerts, and shared helpers.
- Reuse existing mapping utilities and Leaflet setup patterns from public timeline code.
- Reuse Identity role-based area authorization; implement group-scoped checks on top.
