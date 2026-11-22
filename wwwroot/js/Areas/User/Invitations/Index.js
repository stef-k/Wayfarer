import { groupDateTime, groupError, groupMessage as t } from '../../../util/group-web-messages.js';

const config = window.__userInvitationsConfig;
const token = window.__antiForgeryToken || document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
const tbody = document.getElementById('invitesTbody');
const emptyRow = document.getElementById('invitesEmptyRow');
const loadingRow = document.getElementById('invitesLoadingRow');
const inFlight = new Set();
const settled = new Set();

const actions = {
  accept: {
    url: config.acceptUrl,
    label: t('Accept', 'Accept'),
    title: t('AcceptTitle', 'Accept Invitation'),
    confirm: t('AcceptConfirm', 'Join this group?'),
    success: t('InviteAccepted', 'Invitation accepted.'),
    failure: t('AcceptFailed', 'Failed to accept invitation.'),
    className: 'btn-success',
  },
  decline: {
    url: config.declineUrl,
    label: t('Decline', 'Decline'),
    title: t('DeclineTitle', 'Decline Invitation'),
    confirm: t('DeclineConfirm', 'Decline this invitation?'),
    success: t('InviteDeclined', 'Invitation declined.'),
    failure: t('DeclineFailed', 'Failed to decline invitation.'),
    className: 'btn-outline-danger',
  },
};

/** Renders durable invitation state and retains the controls of an action in flight. */
const render = invites => {
  loadingRow?.remove();
  tbody?.querySelectorAll('tr[data-invite-id]').forEach(row => row.remove());
  for (const invite of invites) {
    if (settled.has(invite.id)) continue;
    const row = tbody.insertRow();
    row.dataset.inviteId = invite.id;
    const inviter = [invite.inviterUserName, invite.inviterDisplayName && `(${invite.inviterDisplayName})`].filter(Boolean).join(' ');
    const expires = invite.expiresAt ? groupDateTime(invite.expiresAt) : t('NoExpiry', 'Does not expire');
    for (const value of [invite.groupName || invite.groupId, invite.groupDescription, inviter, expires]) {
      row.insertCell().textContent = value ?? '';
    }
    const cell = row.insertCell();
    cell.className = 'text-end';
    const buttons = document.createElement('div');
    buttons.className = 'btn-group btn-group-sm';
    Object.entries(actions).forEach(([kind, action]) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `btn ${action.className} js-${kind}`;
      button.textContent = action.label;
      button.disabled = inFlight.has(invite.id);
      button.addEventListener('click', () => confirmInvitation(invite.id, kind, action));
      buttons.append(button);
    });
    cell.append(buttons);
  }
  updateEmptyRow();
};

const findRow = id => tbody?.querySelector(`tr[data-invite-id="${id}"]`);
const updateEmptyRow = () => emptyRow?.classList.toggle('d-none', Boolean(tbody.querySelector('tr[data-invite-id]')));
const disableRow = (id, disabled) => findRow(id)?.querySelectorAll('button').forEach(button => { button.disabled = disabled; });

/** Submits the existing bodyless accept or decline command and translates its outcome. */
const respond = async (id, kind) => {
  if (inFlight.has(id) || settled.has(id)) return;
  const action = actions[kind];
  inFlight.add(id);
  disableRow(id, true);
  try {
    const response = await fetch(action.url(id), {
      method: 'POST',
      headers: token ? { RequestVerificationToken: token } : {},
    });
    if (!response.ok || response.redirected) {
      wayfarer.showAlert?.('danger', await groupError(response, kind, action.failure));
      return;
    }

    settled.add(id);
    findRow(id)?.remove();
    updateEmptyRow();
    wayfarer.showAlert?.('success', action.success);
  } catch {
    wayfarer.showAlert?.('danger', t('Unconfirmed', 'The outcome could not be confirmed. Refresh the page before trying again.'));
  } finally {
    inFlight.delete(id);
    disableRow(id, false);
  }
};

const confirmInvitation = (id, kind, action) => {
  if (wayfarer.showConfirmationModal) {
    wayfarer.showConfirmationModal({
      title: action.title,
      message: action.confirm,
      confirmText: action.label,
      onConfirm: () => respond(id, kind),
    });
  } else {
    void respond(id, kind);
  }
};

document.addEventListener('DOMContentLoaded', async () => {
  // A fresh authenticated notification read wins over the page's initial read.
  let receivedState = false;
  const reloadInvitations = event => {
    receivedState = true;
    render(Array.isArray(event.detail) ? event.detail : []);
  };
  document.addEventListener('wayfarer:invitation-state', reloadInvitations);
  window.addEventListener('pagehide', () => {
    document.removeEventListener('wayfarer:invitation-state', reloadInvitations);
  }, { once: true });

  const failure = t('InvitationsFailed', 'Failed to load invitations.');
  try {
    const response = await fetch(config.listUrl);
    if (!response.ok || response.redirected) {
      const detail = await groupError(response, 'list', failure);
      if (!receivedState) {
        loadingRow?.remove();
        wayfarer.showAlert?.('danger', detail);
      }
      return;
    }
    const invitations = await response.json();
    if (!receivedState) render(invitations);
  } catch {
    if (!receivedState) {
      loadingRow?.remove();
      wayfarer.showAlert?.('danger', failure);
    }
  }
});
