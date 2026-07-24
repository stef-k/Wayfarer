# Troubleshooting

Sign‑In Issues
- Wrong password: reset via account page or ask an admin.
- Locked out: your admin can unlock accounts. Enable 2FA for extra security.

Maps Not Loading
- Check connectivity.
- Ask your admin if tile cache path is configured and accessible.
- **403 "Referrer is required"**: The tile provider (e.g. OpenStreetMap) is blocking requests. Configure `AllowedHosts` with Wayfarer's real public hostname so the application can derive a trustworthy origin-only Referer. Also configure `Application:ContactEmail` for the User-Agent contact identity; it does not configure Referer.

Imports Fail or Hang
- Verify file format/size and required fields.
- Refresh to see progress; if still failing, ask admin to review logs.

Mobile App Not Syncing
- Verify server URL and API token.
- Ensure your account is active.

Permissions
- Some pages require specific roles (Admin, Manager, User). Ask your admin if you lack access.
