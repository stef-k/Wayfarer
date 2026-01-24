# Groups

Groups enable trusted location sharing among family, friends, or teams.

![Groups List](images/groups-index.JPG)

---

## What Are Groups?

Groups let trusted members share live or recent locations in real-time. Each group has an owner who manages membership, roles, and settings.

---

## Group Roles

Groups support three member roles with different permissions:

| Role | Permissions |
|------|-------------|
| **Owner** | Full control: manage members, change settings, delete group. Cannot leave the group. |
| **Manager** | Add/remove members (except owner), manage invitations. Required in Organization groups. |
| **Member** | View group locations, receive updates. Can leave the group. |

**Note:** Organization-type groups must always have at least one Manager. Attempting to remove the last manager is blocked.

---

## Group Types

When creating a group, choose a type that matches your sharing needs:

| Type | Visibility Behavior |
|------|---------------------|
| **Organization** | Structured sharing with required manager role — must always have at least one manager |
| **Family** | All members see each other's locations automatically |
| **Friends** | Casual sharing with peer visibility controls — members can adjust who sees them |

Choose **Organization** for teams or formal groups that need oversight. Choose **Family** for close-knit groups where everyone should see everyone. Choose **Friends** for larger or more casual groups where members may want granular control.

---

## Creating a Group

1. Open **Groups** and click **Create**.
2. Name the group and add an optional description.
3. Select the **Group Type** (Family or Friends).
4. Click **Create** to save.
5. Invite members using the Members page.

![Group Members](images/group-members.JPG)

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

![Group Invitations](images/group-invitations.JPG)

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

## Group Map

The group map provides rich location visualization:

**Chronological Navigation:**
- Day/Month/Year view modes with navigation buttons.
- Jump to Today or pick a specific date.
- Historical locations toggle to view past data.

**Member Controls:**
- Search members by username or display name.
- Select All / Deselect All for quick filtering.
- Show All / Hide All location markers.
- Per-member visibility toggles.
- "Only" button to isolate one member's locations.
- Visual indicator for disabled peer visibility.

**Location Display:**
- Color-coded markers per member.
- Click locations for detailed information modal.
- Live/latest location indicators.
- Sampled data for large date ranges (month/year views).

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

The WayfarerMobile app supports groups:

- View group members and their locations.
- Subscribe to live updates via SSE.
- Send location updates to group members.
- Accept/decline invitations from the app.

