## User Accounts and Roles

### User Accounts

User acounts for all roles can be created by the Admin. Accounts created by registration page are all of type `User`.

Accounts are being identified by their *unique* usernames. There is no email and email verification process in the application.

Users have the ability to change their password and display name, but not their username.

In case of lost password, the user can request a password change from the application's Admin.

#### Default Administrator Account

The application comes with a pre-defined `admin` account with the following credentials:

`username: admin`

`password: Admin1!`

This account is created during the first run of the application, it cannot be deleted and it has full access to the application.

Use it to create other accounts and manage the application.

Administrator accounts cannot use the application as a regular user. They can only manage the application and other users.

**Please make sure to change the password after logging in!**

As the application does not have email system, there is no reset mechanism for lost passwords.
Please be sure to write the default administrator password down.

If you lose the defaut administrator password, you can reset it by using the following steps:

1. SSH into the server
2. Stop the application
3. Run the app with the following command and arguments:

 ```
dotnet run reset-password admin TheNewAdminPassword
 ```

### Password Policy

The password policy is as follows:

- At least 8 characters
- At least 1 uppercase letter, 1 lowercase letter, 1 number and 1 special character

### Additional Security

Users can and should enable 2FA (Two Factor Authentication) for their accounts. This is done by scanning the QR code
with a 2FA application (like Google Authenticator) and entering the generated code.

### Application Roles

The application has 3 roles: `Admin`, `Manager`, `User`.

#### Admin

- Can create, read, update, delete and lock any user. Cannot use the application as a regular user (no timeline access, etc). Can change application's settings.

#### Manager

- Can see trusted users' location data but cannot track their own movements through the manager account.

#### User

- Account to for personal use of the application. If looking for a Timeline like experience, this is the role to use.

### Use Cases

#### Personal Use

Use a `User` account to track your own location data, see and share your timeline.

#### Family Use

Users can set a manager as trusted.
When is set, the manager can track user's location.
Useful for tracking children, family members with medical conditions, etc.

#### Fleet Management

- Use a `Manager` account to monitor trusted users or shared organizational devices that report under regular user accounts.

### Location Accuracy Recording and Data Overhead

#### High Accuracy (Most detailed, most data overhead)

In High Accuracy, the application records incoming location data every 1.5 minutes that has a difference of 5 meters distance covered.

- Assuming storing location data with ~250 words or rich text as notes the setting needs ~432 MB for data storage per user per year.

#### Mid Accuracy (Recommended Default)

In Mid Accuracy, the application records incoming location data every 5 minutes that has a difference of 15 meters distance covered.

- Assuming storing location data with ~250 words or rich text as notes the setting needs ~129.5 MB for data storage per user per year.

#### Low Accuracy (Small data overhead)

In Low Accuracy, the application records incoming location data every 10 minutes that has a difference of 50 meters distance covered.

- Assuming storing location data with ~250 words or rich text as notes the setting needs ~64.9 MB for data storage per user per year.

### Tile Caching

The application caches map tiles up to zoom level 8 (included) locally. Additionally,
for zoom levels >= 9 up to max, it has the ability to store tiles, the admin can set the size limit for this; the default
is 1024 Mebabytes (1 Gigabyte).

Zoom levels >= 9 use the Least Recently Used (LRU) mechanism to keep the cache size in accordance to the limit set by
the admin.

#### Purpose of tile cache

The application utilizes the caching mechanism in order to
avoid making many calls to Open Street Map services in the spirit of fair use

### Reverse Geolocation Service

The application offers reverse geolocation (that is to get country/city/street info from coordinates) service through the
[Mapbox API](https://docs.mapbox.com/api/search/geocoding/)

In order to use it, each user must create a [Mapbox](https://www.mapbox.com/) account and generate his own API key.
Mapbox, offers 100.000 free API calls.

### PostGIS - Entity Framework Net Topology Suite useful functions

```
PostGIS function NetTopologySuite / EF Core method Example

ST_DWithin         Distance + compare                 where p.Coordinates.Distance(myPoint) <= 1000
ST_Intersects         Intersects                         where p.Coordinates.Intersects(myAreaPolygon)
ST_Contains         Contains                         where myAreaPolygon.Contains(p.Coordinates)
```

### Auditing

The application writes audit events to the `AuditLogs` table and to Serilog sinks (console/file). Typical events:

- GroupCreate, GroupUpdate, GroupDelete, MemberAdd, MemberRemove, MemberLeave
- InviteCreate, InviteAccept, InviteDecline, InviteRevoke
- SettingsUpdate (Admin → Settings)
- OrgPeerVisibilityToggle (Organization group setting)
- OrgPeerVisibilityAccessSet (per-user toggle in Friends/Organization contexts)
- GroupDelete (auto) when empty and auto-delete is enabled

Each entry contains:
- Action: event name
- Details: short context (actor/target/group/outcome)
- Timestamp: UTC
- UserId: actor identifier

Verification
- File sink: see `Logging:LogFilePath:Default` in `appsettings*.json` for daily rolling logs.
- DB: query `AuditLogs` table to inspect events.
- Manual flow: create a group, invite a user, accept/decline, remove/leave, toggle settings. Confirm events are appended.
