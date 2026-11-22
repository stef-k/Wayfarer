import { prepareLocationDeletion } from '../../../util/location-mutation.js';
import { locationDeletionText, locationMutationText } from '../../../util/location-mutation-text.js';
import { message } from '../../../localization/messages.js';
import { registerLanguageDraft } from '../../../localization/languageNavigation.js';

/** The protected Location owner still observes, verifies and submits the exact conditional deletion. */
export const confirmTimelineDeletion = async (id, deleted) => {
    const release = registerLanguageDraft(() => true);
    try {
        const execute = await prepareLocationDeletion(id);
        wayfarer.showConfirmationModal({
            title: message('Shell.LocationConfirmDeletion', 'Confirm deletion'),
            message: locationDeletionText(execute.confirmation),
            confirmText: message('Shell.LocationDelete', 'Delete'),
            onCancel: release,
            onConfirm: async () => {
                const unknown = message('Shell.LocationDeletionUnknown', 'Deletion outcome unknown. Inspect current history before deciding again.');
                try {
                    const result = await execute();
                    if (result.success) deleted();
                    else wayfarer.showAlert('warning', locationMutationText(result, unknown));
                } catch (error) { wayfarer.showAlert('warning', locationMutationText(error, unknown)); }
                finally { release(); }
            }
        });
    } catch (error) {
        release();
        wayfarer.showAlert('warning', locationMutationText(error, message('Shell.LocationDeletionPrepareFailed',
            'Deletion could not be prepared. Reopen the location details to inspect the current record.')));
    }
};
