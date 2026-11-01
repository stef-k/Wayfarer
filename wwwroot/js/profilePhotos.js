/** First Unicode letter, ignoring leading whitespace/control/format marks and punctuation-only names. */
export const photoInitial = (displayName, userName) => {
  for (const name of [displayName, userName]) {
    const letter = (name || '').match(/\p{L}/u)?.[0];
    if (letter) return letter.toLocaleUpperCase();
  }
  return '';
};

/** Only backend color and same-origin extensionless references enter the presentation. */
export const memberPhotoInfo = checkbox => ({
  userId: checkbox.dataset.userId, username: checkbox.dataset.username || '', display: checkbox.dataset.display || '',
  colorHex: checkbox.dataset.color || '#808080', profilePhoto: checkbox.dataset.photoUrl ? { url: checkbox.dataset.photoUrl } : null
});

/** Safe DOM construction preserves full-name accessibility, backend ring color and image-error fallback. */
export const photoBadge = member => {
  const badge = document.createElement('span');
  badge.className = 'profile-photo';
  badge.setAttribute('role', 'img');
  badge.setAttribute('aria-label', member.display?.trim() || member.displayName?.trim() || member.username?.trim() || member.userName?.trim() || 'Group member');
  badge.style.setProperty('--photo-color', /^#[\da-f]{6}$/i.test(member.colorHex) ? member.colorHex : '#808080');
  badge.textContent = photoInitial(member.display || member.displayName, member.username || member.userName);
  const url = member.profilePhoto?.url;
  if (url?.startsWith('/') && !url.startsWith('//')) {
    const img = document.createElement('img'); img.alt = ''; img.src = url; badge.append(img);
  }
  return badge;
};

/** One roster refresh per failed reference prevents stale image retry loops; focus permits normal refresh. */
export const bindGroupPhotos = (groupId, changed = () => {}) => {
  let pending = false;
  const failed = new Set();
  const refresh = async () => {
    if (pending) return;
    pending = true;
    try {
      const response = await fetch(`/api/groups/${encodeURIComponent(groupId)}/members`, { cache: 'no-store' });
      const members = response.ok ? await response.json() : [];
      const byId = new Map(members.map(member => [member.userId, member]));
      document.querySelectorAll('[data-photo-user]').forEach(container => {
        const member = byId.get(container.dataset.photoUser);
        container.replaceChildren(member ? photoBadge(member) : photoBadge({}));
      });
      document.querySelectorAll('#userSidebar .user-select').forEach(checkbox => {
        const member = byId.get(checkbox.dataset.userId);
        const url = member?.profilePhoto?.url || '';
        checkbox.dataset.photoUrl = failed.has(url) ? '' : url;
        if (member) checkbox.dataset.color = member.colorHex;
        checkbox.closest('.user-item').querySelector('.profile-photo')?.replaceWith(photoBadge(memberPhotoInfo(checkbox)));
      });
      changed();
    } finally { pending = false; }
  };
  const attempt = () => refresh().catch(() => {});
  window.addEventListener('focus', attempt);
  document.addEventListener('profile-photo-error', event => {
    if (failed.has(event.detail)) return;
    failed.add(event.detail); attempt();
  });
  attempt();
};

if (typeof document !== 'undefined') {
  // Capture also covers dynamically inserted quick views, markers and detail modals.
  document.addEventListener('error', event => {
    const img = event.target;
    if (img?.tagName !== 'IMG' || !img.closest('.profile-photo')) return;
    const url = img.getAttribute('src'); img.remove();
    document.dispatchEvent(new CustomEvent('profile-photo-error', { detail: url }));
  }, true);
  const roster = document.querySelector('#rosterTable');
  const groupId = document.querySelector('input[name="groupId"]')?.value;
  if (roster && groupId) bindGroupPhotos(groupId);
}
