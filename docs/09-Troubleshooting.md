# Troubleshooting

For unreadable protected credentials, legacy Mapbox conflicts, exhaustion, guard recovery, and key-ring restore, see [Personal Location Providers](24-Personal-Location-Providers.md).

If Mapbox is configured but paused, complete each distinct step shown in settings: Permanent consent, generic geocoding authorization, explicit verification, active selection, and available Permanent meter capacity. Capture/import remains usable while paused and retains existing enrichment. An explicitly opted-in enrichment workflow resumes through bounded Quartz one-shot continuations only after current authority is restored; provider configuration alone never infers consent.

Geoapify route suggestions require an explicit mode from its closed provider-native catalog. A Wayfarer Transport Profile remains independent planning provenance and never selects or constrains that mode. Temporary failures and rolling-credit exhaustion never clear accepted geometry. Wayfarer does not fall back to Mapbox, public OSRM, or another provider.

Sign‑In Issues
- Wrong password: reset via account page or ask an admin.
- Locked out: your admin can unlock accounts. Enable 2FA for extra security.

Maps Not Loading
- Check connectivity.
- Ask your admin if tile cache path is configured and accessible.
- **403 "Referrer is required"**: Configure `AllowedHosts=wayfarer.example.com` for one hostname or `AllowedHosts=wayfarer.example.com;www.wayfarer.example.com` for several. Entries are semicolon-separated exact public DNS hostnames; wildcards, IP literals, localhost/private names, ports, and URL schemes are invalid. Also configure `Application:ContactEmail` for the User-Agent contact identity; it does not configure Referer.

Imports Fail or Hang

If imported data completed while addresses remain blank, inspect the separate enrichment state. **PausedByAuthority** requires correcting selection/credential/consent/verification and then Resume; **PausedByBudget** retains the provider-specific wake; **BackingOff** waits for retry. Use **Retry deferred** only to reconsider deterministic poison/no-result rows. Restart is safe: reconciliation repairs the current one-shot trigger without replaying completed batches.
- Verify file format/size and required fields.
- Refresh to see progress; if still failing, ask admin to review logs.

Mobile App Not Syncing
- Verify server URL and API token.
- Ensure your account is active.

Permissions
- Some pages require specific roles (Admin, Manager, User). Ask your admin if you lack access.
