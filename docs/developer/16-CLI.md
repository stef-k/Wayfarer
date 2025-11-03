# CLI

Password Reset
- Invoke the app with a command to reset a user’s password:
- `dotnet run -- reset-password <username> <new-password>`
- This spins up minimal services, generates a reset token, and updates the password.
- Use temporary values and rotate immediately; do not document real passwords.

Admin Maintenance
- Additional admin tasks are available via Admin UI (Users, Roles, Jobs, Settings) rather than CLI.

