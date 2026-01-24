# Getting Started

This page helps you sign in, learn the layout, and understand the core concepts. Wayfarer is self-hosted; your administrator (which may be you) controls server address, user accounts, and data retention. No single global domain exists.

![Home Page](images/home.JPG)

Access and Accounts
- Sign In: Use the server URL provided for your installation.
- Accounts: Your admin manages registration settings. If registration is closed, request an invite.
- Account basics: Usernames are the unique identifier for every account. Email addresses are not collected and there is no automated verification flow, so keep track of your credentials.
- Passwords: Your admin sets initial credentials. Change them after first login under your account settings. Two-factor authentication (2FA) is supported via the account management pages.
- Lost passwords: Ask an administrator to reset your password. On self-hosted setups an admin can run `dotnet run -- reset-password <username> <new-password>` from the server to recover access.

Role Overview
- **Admin** — Manages settings and other accounts. Admin users do not have access to timeline features while acting as admin.
- **Manager** — Views location data for users who have explicitly trusted them (e.g., organisations or family coordinators).
- **User** — Personal account for timeline/trip features and for reporting locations from the mobile app.

Password Policy
- Minimum eight characters.
- Requires at least one uppercase letter, one lowercase letter, one number, and one special character.
- Because recovery is manual, rotate credentials regularly and enable 2FA where possible.

App Layout
- Top Navigation: Switch between Dashboard, Trips, Timeline, Groups, and Admin (if you have access).
- Left/Right Panels: Context menus (filters, import, settings) depending on the page.
- Map: Interactive map (Leaflet) with OpenStreetMap tiles (optionally cached locally for performance).

Core Concepts
- Timeline: Your personal location points over time (ingested from device logs or imports).
- Trips: Structured plans composed of Regions, Places, Areas (polygons), and Segments (routes) with notes.
- Groups: Share live or recently uploaded locations with trusted members. Owners manage membership and visibility.

First Steps
1) Review account profile and change your password.
2) Explore Timeline or Trips from the menu.
3) If you have historical data, see Importing Data to upload GPX/KML/CSV/GeoJSON/Google exports.
4) Optional: Install and connect the companion mobile app for live location updates.
5) If you'll self‑host, see "Install & Self‑Hosting" next.

![Account Management](images/account-management.JPG)

User Settings
- Access from the user menu to configure your personal preferences.
- **Timeline settings** — configure privacy, visibility, and time thresholds.
- **API tokens** — manage tokens for mobile app and external integrations.
- **Dark theme** — toggle dark mode for comfortable viewing.

![User Settings](images/user-settings-index.JPG)

![Timeline Settings](images/user-timeline-settings.JPG)

Dark Theme Support
- Wayfarer includes a dark theme for comfortable viewing in low-light conditions.
- Toggle from User Settings or the theme switcher.

![Dark Theme](images/user-settings-dark-theme.JPG)

![Timeline in Dark Theme](images/user-timeline-dark-theme.JPG)
