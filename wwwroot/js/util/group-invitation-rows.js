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

/** Refreshes pending rows through the authorized Members view after Invite is confirmed. */
export const refreshInviteRows = async attachConfirmHandler => {
    try {
        const response = await fetch(window.location.href);
        if (!response.ok) throw new Error('Unable to read invitations');
        const page = new DOMParser().parseFromString(await response.text(), 'text/html');
        const incoming = page.querySelector('#invitesTable tbody');
        const current = document.querySelector('#invitesTable tbody');
        if (!incoming || !current) throw new Error('Missing pending invitations');
        formatInviteRows(incoming);
        incoming.querySelectorAll('form.js-confirm').forEach(attachConfirmHandler);
        current.replaceChildren(...incoming.children);
    } catch {
        wayfarer.showAlert?.('warning', 'Invitation sent. Refresh this page to update pending invitations.');
    }
};
