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
 * @returns {string} HTML string for the activity editor
 */
export const generateActivityEditorHtml = (location) => {
    const currentActivity = location.activityType && location.activityType !== 'Unknown'
        ? location.activityType
        : null;
    const currentActivityId = location.activityTypeId || '';

    return `
        <div class="activity-editor" data-location-id="${location.id}">
            <strong>Activity:</strong>
            <div class="d-flex align-items-center gap-2 mt-1">
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
        const payload = clearActivity
            ? { clearActivity: true }
            : { activityTypeId: activityTypeId || null };

        const response = await fetch(`/api/location/${locationId}`, {
            method: 'PUT',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify(payload)
        });

        if (!response.ok) {
            const errorData = await response.json().catch(() => ({}));
            return {
                success: false,
                message: errorData.message || `Failed to update activity (${response.status})`
            };
        }

        const data = await response.json();
        return {
            success: true,
            message: data.message || 'Activity updated successfully',
            activityType: data.location?.activityType || null
        };
    } catch (error) {
        console.error('Error updating location activity:', error);
        return {
            success: false,
            message: 'Network error while updating activity'
        };
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
        const saveBtn = e.target.closest('.activity-save-btn');
        const clearBtn = e.target.closest('.activity-clear-btn');

        if (saveBtn) {
            e.preventDefault();
            const locationId = parseInt(saveBtn.dataset.locationId, 10);
            const selectElement = document.getElementById(`activitySelect-${locationId}`);
            const activityTypeId = selectElement?.value ? parseInt(selectElement.value, 10) : null;

            saveBtn.disabled = true;
            const result = await updateLocationActivity(locationId, activityTypeId, false);
            saveBtn.disabled = false;

            showActivityFeedback(locationId, result.message, !result.success);

            if (result.success && onActivityUpdated) {
                onActivityUpdated(locationId, result.activityType);
            }
        }

        if (clearBtn) {
            e.preventDefault();
            const locationId = parseInt(clearBtn.dataset.locationId, 10);

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

                if (onActivityUpdated) {
                    onActivityUpdated(locationId, null);
                }
            }
        }
    });
};
