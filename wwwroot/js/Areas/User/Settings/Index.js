import { handleEmbedCopy } from '../../../embedSharing.js';
import { message } from '../../../localization/messages.js';

document.addEventListener('DOMContentLoaded', () => {
    document.addEventListener('click', handleEmbedCopy);
    const button = document.getElementById('deleteAllUserLocations');
    button.addEventListener('click', () => confirmLocationsDeletion(button));
});

/** Sends the existing deletion request only after the shared confirmation is accepted. */
const confirmLocationsDeletion = button => {
    button.disabled = true;
    wayfarer.showConfirmationModal({
        title: message('Settings.DeleteTitle', 'Confirm deletion'),
        message: message('Settings.DeletePrompt', 'Delete all of your location records? This action cannot be undone.'),
        confirmText: message('Settings.Delete', 'Delete'),
        onConfirm: () => deleteLocations(button),
        onCancel: () => { button.disabled = false; }
    });
};

/** Binds feedback to HTTP outcomes without displaying API or exception text. */
const deleteLocations = async btn => {
    try {
        const response = await fetch(`/api/users/${btn.dataset.userId}/locations`, {
            method: 'DELETE',
            credentials: 'include',
            headers: {
                RequestVerificationToken: document.querySelector('input[name="__RequestVerificationToken"]')?.value || '',
                Accept: 'application/json'
            }
        });
        if (response.status === 204) {
            wayfarer.showAlert('success', message('Settings.DeleteSucceeded', 'All locations deleted successfully.'));
        } else if (response.status === 404) {
            wayfarer.showAlert('danger', message('Settings.UserMissing', 'User not found.'));
        } else if (response.status === 401 || response.status === 403 || response.redirected) {
            wayfarer.showAlert('danger', message('Settings.DeleteDenied',
                'You are not authorized to delete these locations. Sign in again if your session ended.'));
        } else {
            throw new Error(`Delete locations returned HTTP ${response.status}`);
        }
    } catch {
        wayfarer.showAlert('danger', message('Settings.DeleteFailed', 'Could not delete locations. Try again.'));
    } finally {
        btn.disabled = false;
    }
};
