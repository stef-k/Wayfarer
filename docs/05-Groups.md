# Groups

Groups enable trusted location sharing among family, friends, or teams.

---

## What Are Groups?

Groups let trusted members share live or recent locations in real-time. Each group has an owner who manages membership, roles, and settings.

---

## Group Roles

Groups support three member roles with different permissions:

| Role | Permissions |
|------|-------------|
| **Owner** | Full control: manage members, change settings, delete group |
| **Manager** | Add/remove members, manage invitations |
| **Member** | View group locations, receive updates |

---

## Creating a Group

1. Open **Groups** and click **Create**.
2. Name the group and save.
3. Invite members using usernames or email addresses.
4. Configure visibility and privacy settings.

---

## Invitation System

Groups use a token-based invitation system:

1. Owner/Manager creates an invitation for a user.
2. Invited user receives notification (via SSE if connected).
3. User can **Accept** or **Decline** the invitation.
4. Accepted users become group members.

### Invitation States

- **Pending** — awaiting user response
- **Accepted** — user joined the group
- **Declined** — user rejected the invitation
- **Expired** — invitation timed out

---

## Real-Time Location Sharing

Once in a group, members can:

- **View live locations** of other members on a shared map.
- **Receive instant updates** via Server-Sent Events (SSE).
- **Query location history** within privacy thresholds.

### SSE Channels

Groups receive real-time updates through dedicated SSE channels:

- **Location updates** — member position changes
- **Membership changes** — members joining/leaving
- **Visit notifications** — when members visit planned places

---

## Group Timeline

The group timeline view shows:

- All member locations respecting privacy settings.
- Color-coded markers per member.
- Time-filtered queries (last hour, day, week).
- Spatial queries within bounding boxes.

---

## Privacy and Visibility

- Group data is shared **only among members**.
- Members can control their own visibility settings.
- **Organization peer visibility** — optional setting for larger groups.
- Owners can remove members at any time.

---

## Best Practices

- Invite only trusted people.
- Rotate API tokens regularly when used with mobile clients.
- Use descriptive group names for easy identification.
- Configure visibility settings appropriate for your group type.

---

## Mobile App Integration

The Wayfarer.Mobile app supports groups:

- View group members and their locations.
- Subscribe to live updates via SSE.
- Send location updates to group members.
- Accept/decline invitations from the app.

