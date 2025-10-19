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
    const confirmButton = modal.querySelector('.btn-danger'); // Assuming the confirm button has a 'btn-danger' class

    // Set the default values if the options are not provided
    modalTitle.textContent = options.title || 'Confirm Action';
    modalBody.textContent = options.message || 'Are you sure you want to proceed?';
    confirmButton.textContent = options.confirmText || 'Confirm';

    // Handle the confirmation action
    confirmButton.onclick = function () {
        if (options.onConfirm && typeof options.onConfirm === 'function') {
            options.onConfirm(); // Execute the provided callback
        }
        modalInstance.hide(); // Close the modal after the action
    };

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

    // Pending invitations badge updater
    const invitesBadge = document.getElementById('userInvitesBadge');
    const updateInvitesBadge = async () => {
        if (!invitesBadge) return;
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
    updateInvitesBadge();
    setInterval(updateInvitesBadge, 60000);

    // Manager: show recent activity digest + badge if present
    const mgrBadge = document.getElementById('managerGroupsBadge');
    const updateManagerActivity = async () => {
        if (!mgrBadge) return; // only on pages where Manager menu is present
        try {
            const res = await fetch('/api/groups/managed/activity?sinceHours=24');
            if (!res.ok) return; // not a manager or not authorized
            const data = await res.json();
            const cnt = data && typeof data.count === 'number' ? data.count : 0;
            mgrBadge.textContent = cnt;
            mgrBadge.classList.toggle('d-none', cnt === 0);
            if (cnt > 0 && !sessionStorage.getItem('mgr.activity.notified')) {
                if (typeof showAlert === 'function') showAlert('info', `You have ${cnt} recent group updates. Open Manager → Groups.`);
                sessionStorage.setItem('mgr.activity.notified', '1');
            }
        } catch { /* ignore */ }
    };
    updateManagerActivity();
    setInterval(updateManagerActivity, 60000);

    // Optional SSE to refresh badge in real-time
    try {
        if (window.__currentUserId && invitesBadge && typeof EventSource !== 'undefined') {
            const es = new EventSource(`/api/sse/stream/invitation-update/${window.__currentUserId}`);
            es.onmessage = () => updateInvitesBadge();
        }
    } catch { /* ignore SSE errors */ }

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

window.showConfirmationModal = showConfirmationModal;
wayfarer.showConfirmationModal = showConfirmationModal;
window.showAlert = showAlert;
wayfarer.showAlert = showAlert;
window.hideAlert = hideAlert;
wayfarer.hideAlert = hideAlert;
