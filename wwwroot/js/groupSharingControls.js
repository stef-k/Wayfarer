/** Bind existing policy endpoints to server-rendered controls; callers supply their page reload. */
export const bindGroupSharingControls = (groupId, root = document, request = fetch, reload = () => window.location.reload()) => {
  const error = root.getElementById('groupSharingError');
  const bind = (id, url, field, inverted = false) => {
    const toggle = root.getElementById(id);
    if (!toggle) return;
    toggle.addEventListener('change', async () => {
      const previous = !toggle.checked;
      toggle.disabled = true;
      if (error) error.hidden = true;
      try {
        const response = await request(url, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            RequestVerificationToken: root.querySelector('input[name="__RequestVerificationToken"]')?.value || '',
          },
          body: JSON.stringify({ [field]: inverted ? !toggle.checked : toggle.checked }),
        });
        if (!response.ok) throw new Error('Sharing request failed');
        const persisted = await response.json();
        toggle.checked = inverted ? !persisted[field] : persisted[field];
        reload();
      } catch {
        toggle.checked = previous;
        if (error) error.hidden = false;
      } finally {
        toggle.disabled = false;
      }
    });
  };
  bind('orgPeerVisibilityToggle', `/api/groups/${groupId}/settings/org-peer-visibility`, 'enabled');
  const userId = root.getElementById('currentUserId')?.value;
  if (userId) bind('peerVisibilityToggle', `/api/groups/${groupId}/members/${encodeURIComponent(userId)}/org-peer-visibility-access`, 'disabled', true);
};

/** Invalidate the entire map, including history and open details, when sharing or membership changes. */
export const refreshGroupState = (payload, reload = () => window.location.reload()) => {
  if (!['visibility-changed', 'member-joined', 'member-left', 'member-removed'].includes(payload?.type)) return false;
  reload();
  return true;
};
