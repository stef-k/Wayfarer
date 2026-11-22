import { readLocationDetail, saveLocationMutation, locationWork, retainLocationDraft } from './location-mutation.js';
import { message } from '../localization/messages.js';
import { registerLanguageDraft } from '../localization/languageNavigation.js';
import { encodeFeatureText as encode } from './feature-metadata.js';
import { locationMutationText } from './location-mutation-text.js';

/** Escape shared activity labels at the HTML sink. */
const activityText = (key, english) => message(`Shell.LocationActivity${key}`, english);
const label = (key, english) => encode(activityText(key, english));

// Edit-start originals survive a pending or rejected save.
const activityOriginals = new Map();
/** Retains selection intent immediately, independently of the form and any in-flight immutable command. */
const retainActivitySelection = (locationId, activityTypeId) => {
    const state = locationWork(locationId, 'activity');
    const retained = state.draft?.generation !== state.settledGeneration ? state.draft?.value : null;
    const detail = activityOriginals.get(locationId) || retained?.draft?.activityDetail || state.fresh;
    if (!detail) throw new Error('Open the Activity editor to observe its originals before saving.');
    const changes = { activity: activityTypeId == null ? { op: 'clear' } : { op: 'set', id: activityTypeId } };
    const values = detail.locationMutation.baseValues;
    const expected = { uuid: values.uuid, activityTypeId: values.activityTypeId };
    retainLocationDraft(locationId, { kind: 'update', changes, expected, detail, draft: { activityDetail: detail } });
    return { detail, changes, expected };
};
/**
 * Shared utility for inline activity editing in Location modals.
 * Provides UI generation and API integration for editing location activities.
 */

/**
 * Generates the HTML for the activity editor section in a modal.
 * @param {Object} location - The location object containing activity data
 * @param {number} location.id - The location ID
 * @param {number|null} location.activityTypeId - Current activity type ID (if any)
 * @param {string|null} location.activityType - Current activity type name (if any)
 * @param {Object} [options] - Rendering options for the editor
 * @param {boolean} [options.showLabel=true] - Whether to show the "Activity" label
 * @param {boolean} [options.compact=false] - Whether to use compact spacing for tables
 * @returns {string} HTML string for the activity editor
 */
export const generateActivityEditorHtml = (location, options = {}) => {
    const { showLabel = true, compact = false } = options;
    const activityName = location.activityType || location.activity || null;
    const currentActivity = activityName && (location.activityTypeId || activityName !== 'Unknown')
        ? activityName
        : null;
    const currentActivityId = location.activityTypeId || '';
    const hasActivity = Boolean(currentActivity);
    const spacingClass = compact ? '' : 'mt-1';

    return `
        <div class="activity-editor" data-location-id="${location.id}">
            ${showLabel ? `<strong>${label('Label', 'Activity:')}</strong>` : ''}
            <span class="activity-view align-items-center gap-2 ${spacingClass} d-inline-flex flex-wrap">
                <a href="#"
                   class="activity-edit-toggle activity-current-link ${hasActivity ? '' : 'd-none'}"
                   data-location-id="${location.id}">
                    ${encode(currentActivity || '')}
                </a>
                <span class="activity-empty-icon ${hasActivity ? 'd-none' : ''}">
                    <i class="bi bi-patch-question" title="${label('Missing', 'No available data for Activity')}"></i>
                </span>
                <a href="#"
                   class="activity-edit-toggle activity-empty-link ${hasActivity ? 'd-none' : ''}"
                   data-location-id="${location.id}">
                    ${label('Edit', 'Edit')}
                </a>
            </span>
            <div class="activity-edit d-none">
                <div class="d-flex align-items-center gap-2 ${spacingClass}">
                    <select id="activitySelect-${location.id}"
                            class="form-select form-select-sm activity-select"
                            data-api-url="/api/activity"
                            data-location-id="${location.id}"
                            aria-label="${label('Select', 'Select an Activity')}" style="flex: 1;">
                        <option value="">${label('Select', 'Select an Activity')}</option>
                        ${currentActivityId ? `<option value="${currentActivityId}" selected>${encode(currentActivity || '')}</option>` : ''}
                    </select>
                    <button type="button"
                            class="btn btn-sm btn-outline-primary activity-save-btn"
                            data-location-id="${location.id}"
                            title="${label('Save', 'Save activity')}" aria-label="${label('Save', 'Save activity')}">
                        <i class="bi bi-check-lg"></i>
                    </button>
                    <button type="button"
                            class="btn btn-sm btn-outline-secondary activity-clear-btn"
                            data-location-id="${location.id}"
                            title="${label('Clear', 'Clear activity')}" aria-label="${label('Clear', 'Clear activity')}">
                        <i class="bi bi-x-lg"></i>
                    </button>
                </div>
            </div>
            <div class="activity-feedback mt-1 small"></div>
        </div>
    `;
};

/**
 * Initializes TomSelect on an activity dropdown within a modal.
 * Should be called after the modal content is rendered.
 * @param {number} locationId - The location ID to identify the select element
 * @returns {TomSelect|null} The TomSelect instance or null if initialization failed
 */
export const initActivitySelect = (locationId) => {
    if (typeof TomSelect === 'undefined') {
        console.warn('TomSelect is not loaded');
        return null;
    }

    const selectElement = document.getElementById(`activitySelect-${locationId}`);
    if (!selectElement) {
        console.warn(`Activity select element not found for location ${locationId}`);
        return null;
    }

    // Check if already initialized
    if (selectElement.tomselect) {
        return selectElement.tomselect;
    }

    const apiUrl = selectElement.dataset.apiUrl || '/api/activity';

    return new TomSelect(selectElement, {
        valueField: 'id',
        labelField: 'name',
        searchField: ['name', 'description'],
        create: false,
        sortField: { field: 'name', direction: 'asc' },
        placeholder: activityText('Search', 'Search for an activity…'),
        allowEmptyOption: true,
        preload: 'focus',
        render: { no_results: () => `<div class="no-results">${label('Empty', 'No matching activities')}</div>` },
        load: async (_query, callback) => {
            try {
                const response = await fetch(apiUrl);
                if (response.redirected || response.status === 401 || response.status === 403) {
                    callback();
                    showActivityFeedback(locationId, message('Shell.LocationMutationSessionEnded',
                        'Your session has ended. Keep your draft before leaving.'), true);
                    return;
                }
                if (!response.ok) throw new Error();
                callback(await response.json());
            } catch {
                callback();
                showActivityFeedback(locationId, activityText('LoadFailed',
                    'Could not load activities. Close and reopen the list to try again.'), true);
            }
        }
    });
};

/**
 * Updates the activity for a location via API.
 * @param {number} locationId - The location ID
 * @param {number|null} activityTypeId - The new activity type ID (null to clear)
 * @param {boolean} clearActivity - Whether to explicitly clear the activity
 * @returns {Promise<{success: boolean, message: string, activityType?: string}>}
 */
export const updateLocationActivity = async (locationId, activityTypeId, clearActivity = false) => {
    try {
        const { detail, changes, expected } = retainActivitySelection(locationId, clearActivity ? null : activityTypeId);
        const result = await saveLocationMutation(locationId, detail, 'update', changes,
            expected, { activityDetail: detail });
        // Known completion supplies the next edit's baseline even if newer desired work keeps this editor open.
        if (result.detail && (result.success || result.applied)) activityOriginals.set(locationId, result.detail);
        return { ...result, activityType: result.detail?.record?.activityName || null };
    } catch (error) {
        return { success: false, message: error.message || 'Activity outcome unknown. Your draft is retained.' };
    }
};

/**
 * Shows feedback message in the activity editor.
 * @param {number} locationId - The location ID
 * @param {string} message - The message to display
 * @param {boolean} isError - Whether this is an error message
 */
export const showActivityFeedback = (locationId, message, isError = false) => {
    const editor = document.querySelector(`.activity-editor[data-location-id="${locationId}"]`);
    if (!editor) return;

    const feedback = editor.querySelector('.activity-feedback');
    if (!feedback) return;

    feedback.textContent = message;
    feedback.className = `activity-feedback mt-1 small ${isError ? 'text-danger' : 'text-success'}`;

    // Keep conflict/uncertain guidance visible while the retained draft still needs attention.
    if (isError) return;
    setTimeout(() => {
        feedback.textContent = '';
        feedback.className = 'activity-feedback mt-1 small';
    }, 3000);
};

/**
 * Toggles activity editor view/edit mode for a specific editor block.
 * @param {HTMLElement} editor - The activity editor container
 * @param {boolean} isEditing - Whether to show edit mode
 */
const setActivityEditMode = (editor, isEditing) => {
    if (!editor) return;

    const view = editor.querySelector('.activity-view');
    const edit = editor.querySelector('.activity-edit');

    if (!view || !edit) return;

    view.classList.toggle('d-none', isEditing);
    edit.classList.toggle('d-none', !isEditing);
};

/**
 * Updates the activity view labels for the editor after a save/clear.
 * @param {HTMLElement} editor - The activity editor container
 * @param {string|null} activityName - New activity label or null if cleared
 */
const updateActivityView = (editor, activityName) => {
    if (!editor) return;

    const name = typeof activityName === 'string' ? activityName : '';
    const currentLink = editor.querySelector('.activity-current-link');
    const emptyIcon = editor.querySelector('.activity-empty-icon');
    const emptyLink = editor.querySelector('.activity-empty-link');
    const hasActivity = name.trim().length > 0;

    if (currentLink) {
        currentLink.textContent = name;
        currentLink.classList.toggle('d-none', !hasActivity);
    }

    if (emptyIcon) {
        emptyIcon.classList.toggle('d-none', hasActivity);
    }

    if (emptyLink) {
        emptyLink.classList.toggle('d-none', hasActivity);
    }
};

/** Reads the activity owner's retained generations, plus changes whose storage write failed. */
const activityDraftOwner = () => {
    const observed = new Set(), unretained = new Set();
    registerLanguageDraft(() => unretained.size > 0 || [...observed].some(id => {
        try {
            const state = locationWork(id, 'activity');
            return state.draft && state.draft.generation !== state.settledGeneration;
        } catch {
            return true;
        }
    }));
    return { observed, unretained };
};

/**
 * Sets up event delegation for activity save/clear buttons.
 * Call this once per page after the modal container exists.
 * @param {string} modalContentSelector - CSS selector for the modal content container
 * @param {Function} [onActivityUpdated] - Optional callback when activity is successfully updated
 * @param {Object} [options] - Language draft integration for workspaces using a guarded reload.
 */
export const setupActivityEditorEvents = (modalContentSelector, onActivityUpdated, { protectLanguage = false } = {}) => {
    const container = document.querySelector(modalContentSelector);
    if (!container) {
        console.warn(`Modal content container not found: ${modalContentSelector}`);
        return;
    }

    const draftOwner = protectLanguage ? activityDraftOwner() : null;
    const feedback = result => protectLanguage ? locationMutationText(result, activityText('Failed',
        'The activity change could not be confirmed. Reopen the activity editor to review it.')) : result.message;
    const retainSelection = (id, activity) => {
        draftOwner?.observed.add(id);
        try {
            retainActivitySelection(id, activity);
            draftOwner?.unretained.delete(id);
        } catch (error) {
            draftOwner?.unretained.add(id);
            throw error;
        }
    };

    // TomSelect forwards changes to its underlying select; native selection and Clear share the same owner.
    container.addEventListener('change', event => {
        const select = event.target.closest('.activity-select');
        if (!select) return;
        const locationId = Number(select.dataset.locationId || select.id?.replace('activitySelect-', ''));
        try { retainSelection(locationId, select.value ? Number(select.value) : null); }
        catch (error) { showActivityFeedback(locationId, feedback(error), true); }
    });
    /** Read and restore the Activity draft before opening its list. */
    const openEditor = async editToggle => {
        const editor = editToggle.closest('.activity-editor');
        const id = Number(editor?.dataset.locationId);
        if (!editor || !Number.isInteger(id)) return;
        try {
            draftOwner?.observed.add(id);
            const state = locationWork(id, 'activity');
            const retained = state.draft?.generation !== state.settledGeneration ? state.draft?.value : null;
            const completed = state.attempts.find(item => item.actionId === state.active)?.receipt?.state === 'applied';
            const detail = (!completed && retained?.draft?.activityDetail) || await readLocationDetail(id);
            activityOriginals.set(id, detail);
            setActivityEditMode(editor, true);
            const select = initActivitySelect(id);
            // Restore unresolved intent against its original comparison base.
            const desired = retained?.changes?.activity;
            const activity = desired ? (desired.op === 'clear' ? null : desired.id) : detail.record.activityTypeId;
            if (select && activity != null && !select.options?.[activity]) select.addOption({ id: activity,
                name: activity === detail.record.activityTypeId ? detail.record.activityName || String(activity) : String(activity) });
            select?.setValue(activity == null ? '' : String(activity), true);
        } catch (error) { showActivityFeedback(id, feedback(error), true); }
    };

    /** Persist the desired Activity independently before asking the protected owner to submit it. */
    const submitActivity = async (button, clear) => {
        if (button.disabled) return;
        const id = Number(button.dataset.locationId);
        const select = document.getElementById(`activitySelect-${id}`);
        const editor = button.closest('.activity-editor');
        if (clear) {
            // A clear is newer desired work even while an older save is pending.
            select?.tomselect?.setValue('', true);
            if (select) select.value = '';
            try { retainSelection(id, null); }
            catch (error) { showActivityFeedback(id, feedback(error), true); return; }
            if (editor.dataset.activitySaving === 'true') {
                showActivityFeedback(id, activityText('NewerClear', 'Newer Clear is retained and still needs saving.'), true);
                return;
            }
        }
        const activityId = select?.value ? Number(select.value) : null;
        // The completed action never borrows a label selected during its request.
        const submittedLabel = activityId ? select?.selectedOptions?.[0]?.textContent : null;
        draftOwner?.observed.add(id);
        button.disabled = true; editor.dataset.activitySaving = 'true';
        const result = await updateLocationActivity(id, activityId, clear);
        button.disabled = false; editor.dataset.activitySaving = 'false';

        if (result.success) draftOwner?.unretained.delete(id);
        showActivityFeedback(id, feedback(result), !result.success);
        if (result.success || result.applied && result.detail) updateActivityView(editor, result.activityType || submittedLabel || null);
        if (result.success) {
            setActivityEditMode(editor, false);
            onActivityUpdated?.(id, result.activityType);
        }
    };

    // Delegate only events from the mounted activity editor.
    container.addEventListener('click', event => {
        const editToggle = event.target.closest('.activity-edit-toggle');
        if (editToggle) {
            event.preventDefault();
            return openEditor(editToggle);
        }
        const save = event.target.closest('.activity-save-btn');
        const clear = event.target.closest('.activity-clear-btn');
        if (save || clear) {
            event.preventDefault();
            return submitActivity(save || clear, Boolean(clear));
        }
    });
};
