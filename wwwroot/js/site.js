window.wayfarer = window.wayfarer || {};

function hideAlert() {
    let alertBox = document.getElementById("alertBox");
    if (alertBox) {
        alertBox.classList.remove("show");
        setTimeout(() => {
            alertBox.style.display = "none";
        }, 150);
    }
}

function showAlert(alertType, alertMessage) {
    let alertBox = document.getElementById("alertBox");
    let alertMessageSpan = document.getElementById("alertMessage");

    if (!alertBox || !alertMessageSpan) {
        console.error("❌ Alert elements NOT found in the DOM!");
        return;
    }
    alertMessageSpan.textContent = alertMessage;

    // Reset class and show alert
    alertBox.className = `alert alert-${alertType} alert-dismissible show`;
    alertBox.style.display = "block";
    alertBox.style.opacity = "1";
}

/**
 * Shows a Bootstrap toast notification (non-intrusive, doesn't cause viewport jumps)
 * @param {string} type - Toast type: 'success', 'danger', 'warning', 'info', 'primary', 'secondary'
 * @param {string} message - Message to display
 * @param {number} duration - Auto-hide duration in ms (default: 3000)
 */
function showToast(type, message, duration = 3000) {
    const container = document.querySelector('.toast-container');
    if (!container) {
        console.error('Toast container not found');
        return;
    }

    // Create a new toast from template
    const template = document.getElementById('toastTemplate');
    const toast = template.cloneNode(true);
    toast.removeAttribute('id');

    // Set background color based on type
    const bgColorMap = {
        'success': 'bg-success text-white',
        'danger': 'bg-danger text-white',
        'warning': 'bg-warning text-dark',
        'info': 'bg-info text-white',
        'primary': 'bg-primary text-white',
        'secondary': 'bg-secondary text-white'
    };

    const bgClass = bgColorMap[type] || 'bg-secondary text-white';
    toast.classList.add(...bgClass.split(' '));

    // Adjust close button color for dark backgrounds
    const closeBtn = toast.querySelector('.btn-close');
    if (type === 'warning') {
        closeBtn.classList.remove('btn-close-white');
    }

    // Set message
    const toastBody = toast.querySelector('.toast-body');
    toastBody.textContent = message;

    // Add to container
    container.appendChild(toast);

    // Initialize and show
    const bsToast = new bootstrap.Toast(toast, {
        autohide: true,
        delay: duration
    });

    bsToast.show();

    // Remove from DOM after hidden
    toast.addEventListener('hidden.bs.toast', () => {
        toast.remove();
    });
}


function showConfirmationModal(options) {
    let modal = document.getElementById('deleteConfirmationModal');
    if (!modal) {
        console.error('Modal element #deleteConfirmationModal not found in the DOM.');
        return;
    }

    const modalInstance = new bootstrap.Modal(modal);

    // Set up the modal content dynamically
    const modalTitle = modal.querySelector('.modal-title');
    const modalBody = modal.querySelector('.modal-body');
    const confirmButton = modal.querySelector('.btn-danger');
    const cancelButton = modal.querySelector('.btn-secondary, [data-bs-dismiss="modal"]');

    // Set the default values if the options are not provided
    modalTitle.textContent = options.title || 'Confirm Action';
    modalBody.textContent = options.message || 'Are you sure you want to proceed?';
    confirmButton.textContent = options.confirmText || 'Confirm';

    // Track if confirm was clicked to distinguish from cancel/dismiss
    let confirmed = false;

    // Handle the confirmation action
    confirmButton.onclick = function () {
        confirmed = true;
        if (options.onConfirm && typeof options.onConfirm === 'function') {
            options.onConfirm();
        }
        modalInstance.hide();
    };

    // Handle cancel via hidden event (covers cancel button, X button, backdrop click, Escape key)
    const onHidden = () => {
        modal.removeEventListener('hidden.bs.modal', onHidden);
        if (!confirmed && options.onCancel && typeof options.onCancel === 'function') {
            options.onCancel();
        }
    };
    modal.addEventListener('hidden.bs.modal', onHidden);

    // Show the modal
    modalInstance.show();
}

// Now the rest of the code inside the DOMContentLoaded event listener
document.addEventListener('DOMContentLoaded', () => {
    const toggleButton = document.getElementById('themeToggle');
    const themeIcon    = document.getElementById('themeIcon');
    const body         = document.body;
    const navbar       = document.getElementById('mainNavbar');
    const footer       = document.querySelector('footer');

    // Check if user is authenticated (set in _LoginPartial.cshtml when signed in)
    const isAuthenticated = !!window.__currentUserId;

    // Pending invitations badge updater (authenticated users only)
    const invitesBadge = document.getElementById('userInvitesBadge');
    const updateInvitesBadge = async () => {
        if (!invitesBadge || !isAuthenticated) return;
        try {
            const res = await fetch('/api/invitations');
            const list = res.ok ? await res.json() : [];
            const count = Array.isArray(list) ? list.length : 0;
            invitesBadge.textContent = count;
            invitesBadge.classList.toggle('d-none', count === 0);
            // One-time session notification after login
            if (count > 0 && !sessionStorage.getItem('invites.notified')) {
                if (typeof showAlert === 'function') {
                    const plural = count === 1 ? '' : 's';
                    // Provide instruction to visit Invitations page
                    showAlert('info', `You have ${count} pending invitation${plural}. Open User → Invitations to review.`);
                }
                sessionStorage.setItem('invites.notified', '1');
            }
        } catch { /* ignore */ }
    };
    if (isAuthenticated) {
        updateInvitesBadge();
        setInterval(updateInvitesBadge, 60000);
    }

    // User offline check for pending invitations: compare with last stored list (authenticated users only)
    const checkPendingInvitesDiff = async () => {
        if (!isAuthenticated) return;
        try {
            const res = await fetch('/api/invitations');
            if (!res.ok) return;
            const cur = await res.json();
            const invites = Array.isArray(cur) ? cur.map(x => ({ id: x.id, groupName: x.groupName || '' })) : [];
            const prevRaw = localStorage.getItem('user.pending.invites');
            const prev = prevRaw ? JSON.parse(prevRaw) : [];
            const prevIds = new Set(prev.map(x => x.id));
            const newInvs = invites.filter(x => !prevIds.has(x.id)).map(x => x.groupName).filter(Boolean);
            const notified = sessionStorage.getItem('user.invites.diff.notified') === '1';
            if (!notified && newInvs.length) {
                if (typeof showAlert === 'function') showAlert('info', `New invitation(s) for: ${newInvs.join(', ')}. Open User → Invitations.`);
                sessionStorage.setItem('user.invites.diff.notified', '1');
            }
            localStorage.setItem('user.pending.invites', JSON.stringify(invites));
        } catch { /* ignore */ }
    };
    if (isAuthenticated) {
        checkPendingInvitesDiff();
    }

    // Manager: show recent activity digest + badge if present (authenticated managers only)
    const mgrBadge = document.getElementById('managerGroupsBadge');
    const updateManagerActivity = async () => {
        if (!mgrBadge || !isAuthenticated) return; // only on pages where Manager menu is present
        try {
            const res = await fetch('/api/groups/managed/activity?sinceHours=24');
            if (!res.ok) return; // not a manager or not authorized
            const data = await res.json();
            const items = (data && Array.isArray(data.items)) ? data.items : [];
            const lastSeenRaw = localStorage.getItem('manager.activity.lastSeenAt');
            const lastSeen = lastSeenRaw ? new Date(lastSeenRaw) : null;
            const recent = lastSeen ? items.filter(i => new Date(i.timestamp) > lastSeen) : items;
            const cnt = recent.length;
            mgrBadge.textContent = cnt;
            mgrBadge.classList.toggle('d-none', cnt === 0);
            if (cnt > 0 && !sessionStorage.getItem('mgr.activity.notified')) {
                if (typeof showAlert === 'function') showAlert('info', `You have ${cnt} recent group updates. Open Manager → Groups.`);
                sessionStorage.setItem('mgr.activity.notified', '1');
            }
        } catch { /* ignore */ }
    };
    if (isAuthenticated) {
        updateManagerActivity();
        setInterval(updateManagerActivity, 60000);
    }

    try {
        document.querySelectorAll('div.dropdown > button.btn.dropdown-toggle').forEach(btn => {
            btn.addEventListener('show.bs.dropdown', () => {
                localStorage.setItem('manager.activity.lastSeenAt', new Date().toISOString());
                const badge = document.getElementById('managerGroupsBadge');
                if (badge) {
                    badge.textContent = '0';
                    badge.classList.add('d-none');
                }
            });
        });
    } catch { /* ignore */ }

    // Optional SSE to refresh badge in real-time
    // Skip invitation SSE on Invitations page (it has its own handler to avoid duplicates)
    const isInvitationsPage = location.pathname.toLowerCase().includes('/user/invitations');
    try {
        if (window.__currentUserId && typeof EventSource !== 'undefined') {
            // Invitations channel: alert on every new invite (skip on Invitations page)
            if (!isInvitationsPage) {
                const esInv = new EventSource(`/api/sse/stream/invitation-update/${window.__currentUserId}`);
                esInv.onmessage = (evt) => {
                    updateInvitesBadge();
                    let g = '';
                    try { const d = evt && evt.data ? JSON.parse(evt.data) : null; if (d && d.groupName) g = ` for ${d.groupName}`; } catch {}
                    if (typeof showAlert === 'function') showAlert('info', `You received a new invitation${g}. Open User → Invitations.`);
                };
            }

            // Membership channel: removal/left/joined alerts
            const esMem = new EventSource(`/api/sse/stream/membership-update/${window.__currentUserId}`);
            esMem.onmessage = (evt) => {
                try {
                    const data = evt && evt.data ? JSON.parse(evt.data) : null;
                    if (!data || !data.action) return;
                    if (data.action === 'removed') {
                        const g = data.groupName ? ` ${data.groupName}` : '';
                        if (typeof showAlert === 'function') showAlert('warning', `You were removed from group${g}.`);
                    } else if (data.action === 'left') {
                        const g = data.groupName ? ` ${data.groupName}` : '';
                        if (typeof showAlert === 'function') showAlert('secondary', `You left group${g}.`);
                    } else if (data.action === 'joined') {
                        const g = data.groupName ? ` ${data.groupName}` : '';
                        if (typeof showAlert === 'function') showAlert('success', `You joined group${g}.`);
                    }
                } catch { /* ignore parse errors */ }
            };
        }
    } catch { /* ignore SSE errors */ }

    // User offline check: server-driven activity digest (authenticated users only)
    // Session guard prevents showing same notifications multiple times per session
    const checkUserActivityDigest = async () => {
        if (!isAuthenticated || sessionStorage.getItem('user.activity.notified') === '1') return;
        try {
            const lastSeenRaw = localStorage.getItem('user.activity.lastSeenAt');
            const lastSeen = lastSeenRaw ? new Date(lastSeenRaw) : null;
            const res = await fetch('/api/users/activity?sinceHours=24');
            if (!res.ok) return;
            const data = await res.json();
            if (data) {
                let invites = Array.isArray(data.invites) ? data.invites : [];
                let joined  = Array.isArray(data.joined) ? data.joined : [];
                let removed = Array.isArray(data.removed) ? data.removed : [];
                let left    = Array.isArray(data.left) ? data.left : [];
                if (lastSeen) {
                    invites = invites.filter(x => x.createdAt && new Date(x.createdAt) > lastSeen);
                    joined  = joined.filter(x => x.joinedAt && new Date(x.joinedAt) > lastSeen);
                    removed = removed.filter(x => x.at && new Date(x.at) > lastSeen);
                    left    = left.filter(x => x.at && new Date(x.at) > lastSeen);
                }
                if (invites.length) {
                    const names = invites.map(x => x.groupName).filter(Boolean);
                    if (names.length && typeof showAlert === 'function') showAlert('info', `New invitation(s) for: ${names.join(', ')}. Open User → Invitations.`);
                }
                const joinedNames = joined.map(x => x.groupName).filter(Boolean);
                const removedNames = removed.map(x => x.groupName).filter(Boolean);
                const leftNames = left.map(x => x.groupName).filter(Boolean);
                if (joinedNames.length && typeof showAlert === 'function') showAlert('success', `You joined: ${joinedNames.join(', ')}`);
                if (removedNames.length && typeof showAlert === 'function') showAlert('warning', `You were removed from: ${removedNames.join(', ')}`);
                if (leftNames.length && typeof showAlert === 'function') showAlert('secondary', `You left: ${leftNames.join(', ')}`);
                if (invites.length || joinedNames.length || removedNames.length || leftNames.length) {
                    localStorage.setItem('user.activity.lastSeenAt', new Date().toISOString());
                    sessionStorage.setItem('user.activity.notified', '1');
                }
            }
        } catch { /* ignore */ }
    };
    if (isAuthenticated) {
        checkUserActivityDigest();
    }

    // Client-side fallback: compare joined count on session start (authenticated users only)
    const checkJoinedGroups = async () => {
        if (!isAuthenticated) return;
        try {
            const res = await fetch('/api/groups?scope=joined');
            if (!res.ok) return;
            const list = await res.json();
            const joined = Array.isArray(list) ? list.map(x => ({ id: x.id, name: x.name })) : [];
            const prevRaw = localStorage.getItem('user.joined.groups');
            const prev = prevRaw ? JSON.parse(prevRaw) : [];
            const prevMap = new Map(prev.map(x => [x.id, x.name]));
            const curMap = new Map(joined.map(x => [x.id, x.name]));
            const removed = prev.filter(x => !curMap.has(x.id)).map(x => x.name).filter(Boolean);
            const added = joined.filter(x => !prevMap.has(x.id)).map(x => x.name).filter(Boolean);
            const notifiedThisSession = sessionStorage.getItem('user.joined.diff.notified') === '1';
            if (!notifiedThisSession) {
                if (removed.length && typeof showAlert === 'function') showAlert('warning', `You were removed from: ${removed.join(', ')}`);
                if (added.length && typeof showAlert === 'function') showAlert('success', `You joined: ${added.join(', ')}`);
                if (removed.length || added.length) sessionStorage.setItem('user.joined.diff.notified', '1');
            }
            localStorage.setItem('user.joined.groups', JSON.stringify(joined));
        } catch { /* ignore */ }
    };
    if (isAuthenticated) {
        checkJoinedGroups();
    }

    // Note: checkPendingInvitesDiff() is already called at line 170

    // Only initialize theme toggle if the toggle button is on this page
    if (!toggleButton) return;

    // Function to set theme
    const setTheme = (theme) => {
        // body always exists
        body.setAttribute('data-bs-theme', theme);

        // icon
        if (themeIcon) {
            themeIcon.classList.toggle('bi-sun',   theme === 'light');
            themeIcon.classList.toggle('bi-moon',  theme === 'dark');
        }

        // navbar
        if (navbar) {
            navbar.classList.toggle('navbar-dark', theme === 'dark');
            navbar.classList.toggle('bg-dark',     theme === 'dark');
            navbar.classList.toggle('navbar-light',theme === 'light');
            navbar.classList.toggle('bg-white',    theme === 'light');
        }

        // footer
        if (footer) {
            footer.setAttribute('data-bs-theme', theme);
        }

        // Persist and update link colors
        localStorage.setItem('theme', theme);
        updateNavbarTextColor(theme);
    };

    // Function to update navbar link text color based on theme
    const updateNavbarTextColor = (theme) => {
        document.querySelectorAll('.theme-toggle').forEach(link => {
            link.classList.toggle('text-dark',  theme === 'light');
            link.classList.toggle('text-light', theme === 'dark');
        });
    };

    // Initialize theme
    const storedTheme = localStorage.getItem('theme') || 'light';
    setTheme(storedTheme);

    // Wire up the toggle
    toggleButton.addEventListener('click', () => {
        const next = body.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
        setTheme(next);
    });
});

// Export all utilities to the wayfarer namespace only
// Usage: wayfarer.showAlert(), wayfarer.showToast(), etc.
wayfarer.showConfirmationModal = showConfirmationModal;
wayfarer.showAlert = showAlert;
wayfarer.hideAlert = hideAlert;
wayfarer.showToast = showToast;

/**
 * Initializes tippy.js tooltips for help icons across the application.
 * Targets elements with data-tippy-content attribute.
 * @param {string} [selector='[data-tippy-content]'] - CSS selector for tooltip elements
 * @param {Object} [options={}] - Additional tippy.js options to merge with defaults
 */
wayfarer.initHelpTooltips = (selector = '[data-tippy-content]', options = {}) => {
    if (typeof tippy === 'undefined') {
        console.warn('tippy.js not loaded, skipping tooltip initialization');
        return;
    }

    const defaults = {
        placement: 'top',
        maxWidth: 300,
        interactive: true,
        allowHTML: true,
        appendTo: () => document.body,
        zIndex: 10000
    };

    tippy(selector, { ...defaults, ...options });
};

// Initialize help tooltips - handles both cases: DOM already ready or not yet ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => wayfarer.initHelpTooltips());
} else {
    // DOM already loaded, initialize immediately
    wayfarer.initHelpTooltips();
}

