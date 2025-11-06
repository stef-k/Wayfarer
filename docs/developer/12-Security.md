# Security

Identity & Roles
- ASP.NET Core Identity with roles: `Admin`, `Manager`, `User`.
- Registration can be open/closed in `ApplicationSettings`.

Passwords
- Never commit or document real passwords. For local dev, use throwaway credentials and rotate.
- Admin seeding creates a protected admin account; change credentials immediately after first run.

API Tokens
- Stored per‑user; used for mobile and API access. Rotate regularly.

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

Uploads & Secrets
- Do not store tokens or secrets in exports or logs.
- Avoid logging PII. Use role-based checks on admin endpoints.

