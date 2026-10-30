import { readLocationDetail, saveLocationMutation, locationWork } from './location-mutation.js';

// Edit-start originals survive a pending or rejected save.
const activityOriginals = new Map();
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
    const currentActivity = activityName && activityName !== 'Unknown'
        ? activityName
        : null;
    const currentActivityId = location.activityTypeId || '';
    const hasActivity = Boolean(currentActivity);
    const spacingClass = compact ? '' : 'mt-1';

    return `
        <div class="activity-editor" data-location-id="${location.id}">
            ${showLabel ? '<strong>Activity:</strong>' : ''}
            <span class="activity-view align-items-center gap-2 ${spacingClass} d-inline-flex flex-wrap">
                <a href="#"
                   class="activity-edit-toggle activity-current-link ${hasActivity ? '' : 'd-none'}"
                   data-location-id="${location.id}">
                    ${currentActivity || ''}
                </a>
                <span class="activity-empty-icon ${hasActivity ? 'd-none' : ''}">
                    <i class="bi bi-patch-question" title="No available data for Activity"></i>
                </span>
                <a href="#"
                   class="activity-edit-toggle activity-empty-link ${hasActivity ? 'd-none' : ''}"
                   data-location-id="${location.id}">
                    Edit
                </a>
            </span>
            <div class="activity-edit d-none">
                <div class="d-flex align-items-center gap-2 ${spacingClass}">
                    <select id="activitySelect-${location.id}"
                            class="form-select form-select-sm activity-select"
                            data-api-url="/api/activity"
                            data-location-id="${location.id}"
                            style="flex: 1;">
                        <option value="">Select an Activity</option>
                        ${currentActivityId ? `<option value="${currentActivityId}" selected>${currentActivity}</option>` : ''}
                    </select>
                    <button type="button"
                            class="btn btn-sm btn-outline-primary activity-save-btn"
                            data-location-id="${location.id}"
                            title="Save activity">
                        <i class="bi bi-check-lg"></i>
                    </button>
                    <button type="button"
                            class="btn btn-sm btn-outline-secondary activity-clear-btn"
                            data-location-id="${location.id}"
                            title="Clear activity">
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
        placeholder: 'Search for an activity...',
        allowEmptyOption: true,
        preload: 'focus',
        load: function(query, callback) {
            fetch(apiUrl)
                .then(response => response.json())
                .then(data => callback(data))
                .catch(() => callback());
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
        const retained = locationWork(locationId).draft?.value;
        const detail = retained?.draft?.activityDetail || activityOriginals.get(locationId);
        if (!detail) return { success: false, message: 'Open the Activity editor to observe its originals before saving.' };
        const changes = { activity: clearActivity || !activityTypeId ? { op: 'clear' } : { op: 'set', id: activityTypeId } };
        const values = detail.locationMutation.baseValues;
        const result = await saveLocationMutation(locationId, detail, 'update', changes,
            { uuid: values.uuid, activityTypeId: values.activityTypeId }, { activityDetail: detail });
        if (result.success) activityOriginals.set(locationId, result.detail);
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

    // Auto-hide after 3 seconds
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

    const trimmedName = typeof activityName === 'string' ? activityName.trim() : '';
    const currentLink = editor.querySelector('.activity-current-link');
    const emptyIcon = editor.querySelector('.activity-empty-icon');
    const emptyLink = editor.querySelector('.activity-empty-link');
    const hasActivity = trimmedName.length > 0;

    if (currentLink) {
        currentLink.textContent = trimmedName;
        currentLink.classList.toggle('d-none', !hasActivity);
    }

    if (emptyIcon) {
        emptyIcon.classList.toggle('d-none', hasActivity);
    }

    if (emptyLink) {
        emptyLink.classList.toggle('d-none', hasActivity);
    }
};

/**
 * Sets up event delegation for activity save/clear buttons.
 * Call this once per page after the modal container exists.
 * @param {string} modalContentSelector - CSS selector for the modal content container
 * @param {Function} [onActivityUpdated] - Optional callback when activity is successfully updated
 */
export const setupActivityEditorEvents = (modalContentSelector, onActivityUpdated) => {
    const container = document.querySelector(modalContentSelector);
    if (!container) {
        console.warn(`Modal content container not found: ${modalContentSelector}`);
        return;
    }

    // Use event delegation for dynamically created buttons
    container.addEventListener('click', async (e) => {
        const editToggle = e.target.closest('.activity-edit-toggle');
        const saveBtn = e.target.closest('.activity-save-btn');
        const clearBtn = e.target.closest('.activity-clear-btn');

        if (editToggle) {
            // Enter edit mode from the view-only link.
            e.preventDefault();
            const editor = editToggle.closest('.activity-editor');
            if (!editor) return;

            const locationId = parseInt(editor.dataset.locationId, 10);
            if (!Number.isNaN(locationId)) {
                try {
                    const retained = locationWork(locationId).draft?.value;
                    const detail = retained?.draft?.activityDetail || await readLocationDetail(locationId);
                    activityOriginals.set(locationId, detail);
                    setActivityEditMode(editor, true);
                    const select = initActivitySelect(locationId);
                    const activity = detail.record.activityTypeId;
                    if (select && activity != null) select.addOption({ id: activity, name: detail.record.activityName || String(activity) });
                    select?.setValue(activity == null ? '' : String(activity), true);
                } catch (error) { showActivityFeedback(locationId, error.message, true); }
            }

            return;
        }

        if (saveBtn) {
            e.preventDefault();
            const locationId = parseInt(saveBtn.dataset.locationId, 10);
            const selectElement = document.getElementById(`activitySelect-${locationId}`);
            const activityTypeId = selectElement?.value ? parseInt(selectElement.value, 10) : null;
            const editor = saveBtn.closest('.activity-editor');

            saveBtn.disabled = true;
            const result = await updateLocationActivity(locationId, activityTypeId, false);
            saveBtn.disabled = false;

            showActivityFeedback(locationId, result.message, !result.success);

            if (result.success) {
                // Prefer the selected option label so view mode stays in sync.
                const selectedLabel = activityTypeId
                    ? selectElement?.selectedOptions?.[0]?.textContent?.trim()
                    : null;
                const updatedLabel = selectedLabel || result.activityType || null;

                updateActivityView(editor, updatedLabel);
                setActivityEditMode(editor, false);
            }

            if (result.success && onActivityUpdated) {
                onActivityUpdated(locationId, result.activityType);
            }
        }

        if (clearBtn) {
            e.preventDefault();
            const locationId = parseInt(clearBtn.dataset.locationId, 10);
            const editor = clearBtn.closest('.activity-editor');

            clearBtn.disabled = true;
            const result = await updateLocationActivity(locationId, null, true);
            clearBtn.disabled = false;

            showActivityFeedback(locationId, result.message, !result.success);

            if (result.success) {
                // Clear the select value
                const selectElement = document.getElementById(`activitySelect-${locationId}`);
                if (selectElement?.tomselect) {
                    selectElement.tomselect.clear();
                }

                updateActivityView(editor, null);
                setActivityEditMode(editor, false);

                if (onActivityUpdated) {
                    onActivityUpdated(locationId, null);
                }
            }
        }
    });
};
