# Troubleshooting

For unreadable protected credentials, legacy Mapbox conflicts, exhaustion, guard recovery, and key-ring restore, see [Personal Location Providers](24-Personal-Location-Providers.md).

If Mapbox is configured but paused, complete each distinct step shown in settings: Permanent consent, generic geocoding authorization, explicit verification, active selection, and available Permanent meter capacity. Capture/import remains usable while paused and retains existing enrichment. Verification and provider failures are safe to retry explicitly; no automatic queue is implied.

Sign‑In Issues
- Wrong password: reset via account page or ask an admin.
- Locked out: your admin can unlock accounts. Enable 2FA for extra security.

Maps Not Loading
- Check connectivity.
- Ask your admin if tile cache path is configured and accessible.
- **403 "Referrer is required"**: Configure `AllowedHosts=wayfarer.example.com` for one hostname or `AllowedHosts=wayfarer.example.com;www.wayfarer.example.com` for several. Entries are semicolon-separated exact public DNS hostnames; wildcards, IP literals, localhost/private names, ports, and URL schemes are invalid. Also configure `Application:ContactEmail` for the User-Agent contact identity; it does not configure Referer.

Imports Fail or Hang
- Verify file format/size and required fields.
- Refresh to see progress; if still failing, ask admin to review logs.

Mobile App Not Syncing
- Verify server URL and API token.
- Ensure your account is active.

Permissions
- Some pages require specific roles (Admin, Manager, User). Ask your admin if you lack access.
