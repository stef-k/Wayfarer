# Troubleshooting

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
