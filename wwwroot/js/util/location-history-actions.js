import { message, plural } from '../localization/messages.js';
import { holdLanguageNavigation } from '../localization/languageNavigation.js';
import { prepareLocationDeletion } from './location-mutation.js';
import { locationDeletionOutcomeText, locationDeletionText, locationMutationText } from './location-mutation-text.js';
import { locationText } from './location-history-text.js';

const title = () => message('Shell.LocationConfirmDeletion', 'Confirm deletion');
const confirmText = () => message('Shell.LocationDelete', 'Delete');

/** Let the protected mutation owner prepare and recover the exact conditional deletion. */
export const confirmLocationHistoryDeletion = async (id, deleted) => {
    if (!Number.isInteger(id)) return;
    const release = holdLanguageNavigation();
    try {
        const execute = await prepareLocationDeletion(id);
        wayfarer.showConfirmationModal({
            title: title(), message: locationDeletionText(execute.confirmation), confirmText: confirmText(),
            onCancel: release,
            onConfirm: async () => {
                const fallback = message('Shell.LocationDeletionUnknown',
                    'Deletion outcome unknown. Inspect current history before deciding again.');
                try {
                    const result = await execute();
                    wayfarer.showAlert(result.success ? 'success' : 'warning', locationDeletionOutcomeText(result, fallback));
                    if (result.success) deleted();
                    // Pending or rejected conditional work keeps its evidence in the existing Location store.
                } catch (error) {
                    wayfarer.showAlert('warning', locationMutationText(error, fallback));
                } finally {
                    release();
                }
            }
        });
    } catch (error) {
        release();
        wayfarer.showAlert('warning', locationMutationText(error, message('Shell.LocationDeletionPrepareFailed',
            'Deletion could not be prepared. Reopen the location details to inspect the current record.')));
    }
};

/** Bulk deletion keeps its weaker Apply-time semantics. An unconfirmed reply requires a deliberate history refresh. */
export const bindLocationBulkDeletion = deleted => {
    document.getElementById('deleteSelected').addEventListener('click', () => {
        const ids = Array.from(document.querySelectorAll('input[name="locationCheckbox"]:checked'), row => Number(row.value))
            .filter(Number.isInteger);
        if (ids.length === 0) {
            wayfarer.showAlert('warning', locationText('NoSelected', 'No locations selected.'));
            return;
        }

        wayfarer.showConfirmationModal({
            title: title(),
            message: plural('Locations.BulkDelete', ids.length,
                'Delete {0} selected location with its current content? If the reply is lost, inspect history before deliberately trying again.',
                'Delete {0} selected locations with their current content? If the reply is lost, inspect history before deliberately trying again.'),
            confirmText: confirmText(),
            onConfirm: () => deleteSelectedLocations(ids, deleted)
        });
    });
};

/** Resolve known refusals from the HTTP response and keep the page mounted when the outcome is unknown. */
const deleteSelectedLocations = async (ids, deleted) => {
    const release = holdLanguageNavigation();
    try {
        const response = await fetch('/api/Location/bulk-delete', {
            method: 'POST',
            headers: {
                'RequestVerificationToken': document.querySelector('input[name="__RequestVerificationToken"]')?.value || '',
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ operationScope: 'bulk', locationIds: ids })
        });
        if (response.redirected || [401, 403].includes(response.status)) {
            release();
            wayfarer.showAlert('warning', message('Shell.SessionEnded', 'Your session has ended. Sign in again.'));
            return;
        }
        const data = await response.json();
        if (response.ok && data.success) {
            release();
            wayfarer.showAlert('success', plural('Locations.BulkDeleteDone', data.affectedCount,
                '{0} location deleted.', '{0} locations deleted.'));
            deleted();
            return;
        }
        if (response.status === 404 || response.status === 400 && data.message === 'No location IDs provided.') {
            release();
            wayfarer.showAlert('warning', response.status === 404
                ? locationText('BulkDeleteNoMatches', 'No locations found for deletion.')
                : locationText('BulkDeleteNoIds', 'No locations were submitted for deletion.'));
            return;
        }
    } catch (error) {
        console.error(error);
    }
    const status = document.getElementById('location-deletion-status');
    status.textContent = locationText('BulkDeleteUnknown',
        'Bulk deletion could not be confirmed. Refresh history and inspect the current records before deliberately trying again.');
    status.hidden = false;
};
