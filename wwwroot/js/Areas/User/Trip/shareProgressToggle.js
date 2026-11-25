import { message } from '../../../localization/messages.js';

/**
 * Share Progress Toggle - Auto-saves via AJAX
 * Handles the share progress toggle in trip edit view, syncs between
 * form toggle and modal toggle, and auto-saves to database.
 */

let isPublicSwitch = null;
let shareProgressContainer = null;
let shareProgressSwitch = null;
let shareProgressStatus = null;
let modalShareSwitch = null;
let modalShareStatus = null;
let btnCopyProgressLink = null;

/**
 * Update all status badges and copy link button visibility
 * @param {boolean} enabled - Whether share progress is enabled
 */
const updateAllBadges = (enabled) => {
    if (shareProgressStatus) {
        shareProgressStatus.className = enabled ? 'badge bg-success' : 'badge bg-secondary';
        shareProgressStatus.textContent = enabled ? message("Viewer.Public", 'Visible to public') : message("Viewer.Private", 'Private');
    }
    if (modalShareStatus) {
        modalShareStatus.className = enabled ? 'badge bg-success small' : 'badge bg-secondary small';
        modalShareStatus.textContent = enabled ? message("Viewer.Public", 'Visible to public') : message("Viewer.Private", 'Private');
    }
    // Show/hide copy link button based on enabled state
    if (btnCopyProgressLink) {
        btnCopyProgressLink.classList.toggle('d-none', !enabled);
    }
};

/**
 * Sync all toggles to the same state
 * @param {boolean} enabled - Whether share progress is enabled
 */
const syncToggles = (enabled) => {
    if (shareProgressSwitch) shareProgressSwitch.checked = enabled;
    if (modalShareSwitch) modalShareSwitch.checked = enabled;
    updateAllBadges(enabled);
};

/** Localizes the controller's existing refusal values. */
const shareProgressRefusal = (error) => {
    switch (error) {
        case 'Trip not found or unauthorized':
            return message('Viewer.ShareAuthorityUnavailable', 'This Trip is unavailable or you no longer have permission to change its sharing settings.');
        case 'Trip must be public to share progress':
            return message('Viewer.ShareRequiresPublicTrip', 'Make this Trip public before sharing visit progress.');
        default:
            return message('Viewer.ShareSaveFailed', 'Share progress could not be saved.');
    }
};

/**
 * AJAX call to save share progress setting
 * @param {boolean} enabled - The new enabled state
 */
const saveShareProgress = async (enabled) => {
    const tripId = shareProgressSwitch?.dataset.tripId || modalShareSwitch?.dataset.tripId;
    if (!tripId) return;

    // Show saving state
    if (shareProgressStatus) {
        shareProgressStatus.className = 'badge bg-warning';
        shareProgressStatus.textContent = message("Viewer.ShareSaving", 'Saving...');
    }

    let feedback;
    try {
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
        const response = await fetch(`/User/Trip/ToggleShareProgress?id=${tripId}&enabled=${enabled}`, {
            method: 'POST',
            headers: {
                'Accept': 'application/json',
                'Content-Type': 'application/x-www-form-urlencoded',
                'RequestVerificationToken': token
            }
        });

        if (response.status === 401) {
            feedback = message('Viewer.ShareSessionEnded', 'Your session has ended or your account is disabled. Sign in again. Keep any unsaved changes before leaving this page.');
        } else if (response.ok && !response.redirected) {
            const result = await response.json();
            if (result?.success === true && typeof result.enabled === 'boolean') {
                syncToggles(result.enabled);
                return;
            }
            if (result?.success === false) feedback = shareProgressRefusal(result.error);
        }
    } catch (error) {
        console.error('Failed to save share progress:', error);
    }

    // Restore both controls to their prior display on refusal or an unconfirmed result.
    syncToggles(!enabled);
    wayfarer.showAlert('danger', feedback ?? message('Viewer.ShareSaveUnconfirmed',
        'The visit progress sharing change may still finish. Refresh the page to check the current setting.'));
};

/**
 * Copy text to clipboard with fallback for older browsers
 * @param {string} text - Text to copy
 * @returns {Promise<boolean>} - Success status
 */
const copyToClipboard = async (text) => {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        // Fallback for older browsers
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        textarea.select();
        const success = document.execCommand('copy');
        document.body.removeChild(textarea);
        return success;
    }
};

/**
 * Initialize the share progress toggle functionality
 */
export const initShareProgressToggle = () => {
    isPublicSwitch = document.getElementById('isPublicSwitch');
    shareProgressContainer = document.getElementById('shareProgressContainer');
    shareProgressSwitch = document.getElementById('shareProgressSwitch');
    shareProgressStatus = document.getElementById('shareProgressStatus');
    modalShareSwitch = document.getElementById('modalShareProgressSwitch');
    modalShareStatus = document.getElementById('modalShareStatus');
    btnCopyProgressLink = document.getElementById('btnCopyProgressLink');

    // Handle share progress toggle change (auto-saves)
    if (shareProgressSwitch) {
        shareProgressSwitch.addEventListener('change', () => {
            saveShareProgress(shareProgressSwitch.checked);
        });
    }

    // Handle modal toggle change (syncs with main toggle and auto-saves)
    if (modalShareSwitch) {
        modalShareSwitch.addEventListener('change', () => {
            saveShareProgress(modalShareSwitch.checked);
        });
    }

    // Copy Link button - copies public URL with progress param
    if (btnCopyProgressLink) {
        btnCopyProgressLink.addEventListener('click', async () => {
            const baseUrl = btnCopyProgressLink.dataset.publicUrl;
            if (!baseUrl) return;

            // Build full URL with progress param
            const url = new URL(baseUrl, window.location.origin);
            url.searchParams.set('progress', '1');
            const fullUrl = url.toString();

            const success = await copyToClipboard(fullUrl);
            if (success) {
                // Show feedback
                const originalHtml = btnCopyProgressLink.innerHTML;
                btnCopyProgressLink.textContent = message('Shell.UrlCopied', 'URL copied to clipboard!');
                btnCopyProgressLink.classList.remove('btn-outline-success');
                btnCopyProgressLink.classList.add('btn-success');
                setTimeout(() => {
                    btnCopyProgressLink.innerHTML = originalHtml;
                    btnCopyProgressLink.classList.remove('btn-success');
                    btnCopyProgressLink.classList.add('btn-outline-success');
                }, 2000);
            }
        });
    }

    // Show/hide Share Progress container when IsPublic changes
    if (isPublicSwitch && shareProgressContainer) {
        isPublicSwitch.addEventListener('change', () => {
            if (isPublicSwitch.checked) {
                shareProgressContainer.classList.remove('d-none');
            } else {
                shareProgressContainer.classList.add('d-none');
                // Auto-disable share progress when making trip private
                if (shareProgressSwitch?.checked) {
                    saveShareProgress(false);
                }
            }
        });
    }
};
