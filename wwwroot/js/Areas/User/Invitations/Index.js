// User Invitations page module
// - Fetches pending invites from /api/invitations
// - Accepts/declines with anti-forgery header
// - Uses shared helpers: wayfarer.showConfirmationModal, wayfarer.showAlert

const config = window.__userInvitationsConfig || {};
const token = window.__antiForgeryToken || document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

const el = {
  tbody: document.getElementById('invitesTbody'),
  emptyRow: document.getElementById('invitesEmptyRow')
};

const fmt = {
  date: (iso) => {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      return d.toLocaleString();
    } catch { return iso; }
  }
};

const fetchJson = async (url, options = {}) => {
  const res = await fetch(url, options);
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* ignore */ }
  if (!res.ok) {
    const msg = (data && (data.message || data.error)) || `${res.status} ${res.statusText}`;
    throw new Error(msg);
  }
  return data;
};

const setEmptyVisibility = () => {
  const hasRows = el.tbody && el.tbody.querySelectorAll('tr[data-invite-id]').length > 0;
  if (el.emptyRow) el.emptyRow.classList.toggle('d-none', hasRows);
};

const rowHtml = (inv) => {
  const gname = inv.groupName || inv.groupId || '';
  const gdesc = inv.groupDescription || '';
  const inviter = (inv.inviterUserName || '') + (inv.inviterDisplayName ? ` (${inv.inviterDisplayName})` : '');
  const expires = inv.expiresAt ? fmt.date(inv.expiresAt) : 'Does not expire';
  return `
    <tr data-invite-id="${inv.id}">
      <td>${gname}</td>
      <td>${gdesc}</td>
      <td>${inviter}</td>
      <td>${expires}</td>
      <td class="text-end">
        <div class="btn-group btn-group-sm" role="group">
          <button type="button" class="btn btn-success js-accept" data-id="${inv.id}">Accept</button>
          <button type="button" class="btn btn-outline-danger js-decline" data-id="${inv.id}">Decline</button>
        </div>
      </td>
    </tr>`;
};

const render = (invites) => {
  if (!el.tbody) return;
  el.tbody.querySelectorAll('tr[data-invite-id]').forEach(tr => tr.remove());
  const frag = document.createDocumentFragment();
  const temp = document.createElement('tbody');
  for (const inv of invites) {
    temp.insertAdjacentHTML('beforeend', rowHtml(inv));
  }
  Array.from(temp.children).forEach(tr => frag.appendChild(tr));
  el.tbody.appendChild(frag);
  setEmptyVisibility();
};

const refresh = async () => {
  const invites = await fetchJson(config.listUrl);
  render(invites || []);
};

const disableRow = (id, disabled) => {
  const row = el.tbody?.querySelector(`tr[data-invite-id="${id}"]`);
  row?.querySelectorAll('button').forEach(b => b.disabled = !!disabled);
};

const accept = async (id) => {
  disableRow(id, true);
  try {
    await fetchJson(config.acceptUrl(id), {
      method: 'POST',
      headers: token ? { 'RequestVerificationToken': token } : {}
    });
    if (wayfarer.showAlert) wayfarer.showAlert('success', 'Invitation accepted.');
    // Remove row and update empty state quickly for immediate feedback
    el.tbody?.querySelector(`tr[data-invite-id="${id}"]`)?.remove();
    setEmptyVisibility();
  } catch (e) {
    if (wayfarer.showAlert) wayfarer.showAlert('danger', e.message || 'Failed to accept invitation.');
  } finally {
    disableRow(id, false);
  }
};

const decline = async (id) => {
  disableRow(id, true);
  try {
    await fetchJson(config.declineUrl(id), {
      method: 'POST',
      headers: token ? { 'RequestVerificationToken': token } : {}
    });
    if (wayfarer.showAlert) wayfarer.showAlert('success', 'Invitation declined.');
    el.tbody?.querySelector(`tr[data-invite-id="${id}"]`)?.remove();
    setEmptyVisibility();
  } catch (e) {
    if (wayfarer.showAlert) wayfarer.showAlert('danger', e.message || 'Failed to decline invitation.');
  } finally {
    disableRow(id, false);
  }
};

const onClick = (ev) => {
  const t = ev.target;
  if (!(t instanceof HTMLElement)) return;
  const id = t.getAttribute('data-id');
  if (!id) return;

  if (t.classList.contains('js-accept')) {
    if (wayfarer.showConfirmationModal) {
      wayfarer.showConfirmationModal({
        title: 'Accept Invitation',
        message: 'Join this group?',
        confirmText: 'Accept',
        onConfirm: () => accept(id)
      });
    } else {
      accept(id);
    }
  }

  if (t.classList.contains('js-decline')) {
    if (wayfarer.showConfirmationModal) {
      wayfarer.showConfirmationModal({
        title: 'Decline Invitation',
        message: 'Decline this invitation?',
        confirmText: 'Decline',
        onConfirm: () => decline(id)
      });
    } else {
      decline(id);
    }
  }
};

document.addEventListener('DOMContentLoaded', async () => {
  try {
    await refresh();
    el.tbody?.addEventListener('click', onClick);
    // Live refresh via SSE when invites change
    try {
      if (window.__currentUserId && typeof EventSource !== 'undefined') {
        const es = new EventSource(`/api/sse/stream/invitation-update/${window.__currentUserId}`);
        es.onmessage = async () => { await refresh(); };
      }
    } catch { /* ignore */ }
  } catch (e) {
    if (wayfarer.showAlert) wayfarer.showAlert('danger', e.message || 'Failed to load invitations.');
  }
});
