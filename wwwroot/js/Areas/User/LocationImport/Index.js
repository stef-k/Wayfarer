import { message } from '../../../localization/messages.js';
import { createLocationImportRefresh } from './Refresh.js';

const actionText = {
    Start: ['Start import', 'Start this import?'],
    Resume: ['Resume import', 'Resume this import from its saved progress?'],
    Retry: ['Retry import', 'Retry this import?'],
    Stop: ['Stop import', 'Stop this import?']
};

document.addEventListener('DOMContentLoaded', () => {
    // Refresh through the same server presentation used for the initial document.
    const refresh = createLocationImportRefresh({ schedule: window.setTimeout.bind(window),
        cancel: window.clearTimeout.bind(window),
        reload: () => window.location.reload() });
    refresh.connect(globalThis.EventSource, '/api/sse/import');
    window.addEventListener('pagehide', refresh.dispose, { once: true });

    const antiForgeryToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const cfg = window.__locationImportConfig;

    /** Submits the existing protected action with its stable import ID. */
    const postForm = (url, id) => {
        const form = document.createElement('form');
        form.method = 'post';
        form.action = url;
        for (const [name, value] of Object.entries({ __RequestVerificationToken: antiForgeryToken, id })) {
            const input = document.createElement('input');
            input.type = 'hidden';
            input.name = name;
            input.value = value;
            form.appendChild(input);
        }
        document.body.appendChild(form);
        form.submit();
    };

    document.querySelectorAll('.js-import-action[data-action]').forEach(button => {
        button.addEventListener('click', () => {
            const { action, id, textKey } = button.dataset;
            const [label, prompt] = actionText[textKey];
            const title = message(`Transfers.Action.${textKey}`, label);
            wayfarer.showConfirmationModal({
                title,
                message: message(`Transfers.Confirm.${textKey}`, prompt),
                confirmText: title,
                onConfirm: () => postForm(action === 'start' ? cfg.startImportUrl : cfg.stopImportUrl, id)
            });
        });
    });

    document.querySelectorAll('.js-delete-import').forEach(link => {
        link.addEventListener('click', event => {
            event.preventDefault();
            const title = message('Transfers.RemoveImport', 'Remove import');
            wayfarer.showConfirmationModal({
                title,
                message: message('Transfers.Confirm.Remove', 'Remove this import record? This cannot be undone.'),
                confirmText: title,
                onConfirm: () => postForm(cfg.deleteUrl, link.dataset.id)
            });
        });
    });
});
