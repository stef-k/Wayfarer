import { formatDateTime, getViewerTimeZone } from './datetime.js';

/** Shows the stored invitation timestamps in the viewer's time zone. */
export const formatInviteRows = root => {
    const displayTimeZone = getViewerTimeZone();
    root.querySelectorAll('.js-invite-created').forEach(time => {
        const iso = time.getAttribute('data-created-at');
        if (!iso) return;
        const formatted = formatDateTime({ iso, displayTimeZone, includeOffset: true });
        if (formatted) time.textContent = formatted;
    });
};

const inviteRowsSelector = '#invitesTable tbody';
let latestRead;

/** Refreshes pending rows after a confirmed Invite and publishes only to the current Members view. */
export const refreshInviteRows = async attachConfirmHandler => {
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
        formatInviteRows(incoming);
        incoming.querySelectorAll('form.js-confirm').forEach(attachConfirmHandler);
        current.replaceChildren(...incoming.children);
    } catch {
        if (isCurrent())
            wayfarer.showAlert?.('warning', 'Invitation sent. Refresh this page to update pending invitations.');
    }
};
