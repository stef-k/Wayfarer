import { groupDateTime, groupMessage as t } from './group-web-messages.js';

/** Shows the stored invitation timestamps in the viewer's time zone. */
export const formatInviteRows = root => {
    root.querySelectorAll('.js-invite-created').forEach(time => {
        const iso = time.getAttribute('data-created-at');
        if (!iso) return;
        const formatted = groupDateTime(iso);
        if (formatted) time.textContent = formatted;
    });
};

/** Keeps the server-rendered empty state aligned with confirmed pending rows. */
const showEmptyState = table => {
    const empty = table?.querySelector('.js-invites-empty');
    if (empty) empty.hidden = Boolean(table.querySelector('tr[data-invite-id]'));
};

const inviteRowsSelector = '#invitesTable tbody';
// A replacement invitation gets a new ID. Keep settled IDs for the life of the Members page.
const removedInviteIds = new Set();
let latestRead;

/** Records a confirmed removal even when the pending row is absent. */
export const removeInviteRow = inviteId => {
    removedInviteIds.add(inviteId);
    const table = document.querySelector(inviteRowsSelector);
    table?.querySelector(`tr[data-invite-id="${inviteId}"]`)?.remove();
    showEmptyState(table);
};

/** Refreshes pending rows after a confirmed Invite and publishes only to the current Members view. */
export const refreshInviteRows = async () => {
    const current = document.querySelector(inviteRowsSelector);
    if (!current) return;

    const url = window.location.href;
    const read = Symbol();
    latestRead = read;
    const isCurrent = () => latestRead === read && window.location.href === url
        && document.querySelector(inviteRowsSelector) === current;

    try {
        const response = await fetch(url);
        if (!response.ok) throw new Error('Unable to read invitations');
        const html = await response.text();
        if (!isCurrent()) return;

        const page = new DOMParser().parseFromString(html, 'text/html');
        const incoming = page.querySelector(inviteRowsSelector);
        if (!incoming) throw new Error('Missing pending invitations');
        incoming.querySelectorAll('tr[data-invite-id]').forEach(row => {
            if (removedInviteIds.has(row.dataset.inviteId)) row.remove();
        });
        formatInviteRows(incoming);
        current.replaceChildren(...incoming.children);
        showEmptyState(current);
    } catch {
        if (isCurrent())
            wayfarer.showAlert?.('warning', t('InviteRefreshFailed', 'Refresh this page to update pending invitations.'));
    }
};
