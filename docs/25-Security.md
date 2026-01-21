# Security

Identity & Roles
- ASP.NET Core Identity with roles: `Admin`, `Manager`, `User`.
- Registration can be open/closed in `ApplicationSettings`.
- Usernames are the unique login identifier; email is optional and not used for verification flows.

Passwords
- Never commit or document real passwords. For local dev, use throwaway credentials and rotate.
- Admin seeding creates a protected admin account; change credentials immediately after first run.
- Policy: minimum eight characters with at least one upper-case letter, one lower-case letter, one digit, and one special character.

Account Lockout
- Accounts are locked after 5 failed login attempts to protect against brute-force attacks.
- Lockout duration: 15 minutes.
- Applies to all users including new accounts.

API Tokens
- **Wayfarer API tokens** (used for mobile app and API authentication) are stored as SHA-256 hashes—never in plain text. If the database is compromised, the tokens cannot be recovered or reused.
- Tokens are shown **only once** when created or regenerated. Users must copy and store them securely.
- **Third-party tokens** (e.g., Mapbox API keys) are stored as provided since the application needs them for outgoing API calls. Use scoped/restricted keys from providers when possible.
- Rotate API tokens regularly and revoke any that may have been exposed.

Authorization
- Admin/Manager areas require roles.
- API endpoints enforce ownership or public flags for trips and group membership for timelines.

Headers & Proxies
- Configure forwarded headers and trusted networks in `Program.cs` for your reverse proxy.

Data Privacy
- Self‑hosted: operators are responsible for retention, backups, and legal compliance.
- Avoid logging sensitive PII; mask or omit where possible.

Secrets Management
- Use environment variables or user‑secrets in development; use secret managers/VAULTs in production.


Two-Factor Authentication (2FA)
- Identity area includes Enable Authenticator pages for TOTP-based 2FA.
- Encourage users to enable 2FA, especially for admin accounts.

CSRF Protection
- All admin endpoints are protected with anti-forgery tokens.
- AJAX calls to sensitive endpoints (cache deletion, settings changes) include CSRF tokens.
- Forms use `@Html.AntiForgeryToken()` and controllers validate with `[ValidateAntiForgeryToken]`.

Rate Limiting
- **Tile requests** — Anonymous users limited to 500 requests/minute per IP (configurable).
- **Check-in endpoint** — Rate-limited to prevent spam (default: 10 second cooldown).
- **Location logging** — Filtered by time and distance thresholds.
- Rate limit headers included in API responses.

XSS Prevention
- Tile provider attribution is sanitized using HtmlSanitizer before rendering.
- User-generated content (notes, names) escaped in views.
- Rich HTML content (trip notes) rendered in controlled contexts.

IP Address Handling
- X-Forwarded-For header trusted only from localhost and private IP ranges.
- Prevents IP spoofing attacks when behind reverse proxies.
- Configure trusted proxies in `Program.cs` for your deployment environment.

Uploads & Secrets
- Do not store tokens or secrets in exports or logs.
- Avoid logging PII. Use role-based checks on admin endpoints.
- API keys are redacted from tile service logs to prevent exposure.

