/**
 * Admin Jobs Index - SSE for real-time job status updates.
 * Uses MvcFrontendKit convention: automatically loaded for Admin/Jobs/Index view.
 */

document.addEventListener('DOMContentLoaded', () => {
    initJobStatusSse();
});

/**
 * Initialize SSE connection for job status updates.
 */
const initJobStatusSse = () => {
    let eventSource = null;
    let reconnectAttempts = 0;
    const maxReconnectAttempts = 5;
    const reconnectDelay = 3000;

    const connect = () => {
        if (eventSource) {
            eventSource.close();
        }

        eventSource = new EventSource('/Admin/Jobs/sse');

        eventSource.onopen = () => {
            console.log('[JobStatusSse] Connected');
            reconnectAttempts = 0;
        };

        eventSource.onmessage = (event) => {
            try {
                const data = JSON.parse(event.data);
                handleJobStatusEvent(data);
            } catch (e) {
                console.error('[JobStatusSse] Failed to parse event:', e);
            }
        };

        eventSource.onerror = (error) => {
            console.error('[JobStatusSse] Connection error:', error);
            eventSource.close();

            if (reconnectAttempts < maxReconnectAttempts) {
                reconnectAttempts++;
                console.log(`[JobStatusSse] Reconnecting in ${reconnectDelay}ms (attempt ${reconnectAttempts}/${maxReconnectAttempts})`);
                setTimeout(connect, reconnectDelay);
            } else {
                console.error('[JobStatusSse] Max reconnect attempts reached');
                wayfarer.showAlert('warning', 'Lost connection to job status updates. Refresh to reconnect.');
            }
        };
    };

    connect();
};

/**
 * Handle incoming job status event.
 * @param {Object} data - The job status event data.
 */
const handleJobStatusEvent = (data) => {
    const { eventType, jobName, jobGroup, status, timestampUtc, errorMessage } = data;

    // Find the row for this job
    const row = findJobRow(jobName, jobGroup);
    if (!row) {
        console.warn(`[JobStatusSse] Row not found for job: ${jobName}/${jobGroup}`);
        return;
    }

    // Update the status badge
    updateStatusBadge(row, status);

    // Update the action buttons
    updateActionButtons(row, status, jobName, jobGroup);

    // Update last run time for completion events
    if (eventType !== 'job_started') {
        updateLastRunTime(row, timestampUtc);
    }

    // Show notification using wayfarer utilities
    showJobNotification(eventType, jobName, errorMessage);
};

/**
 * Find the table row for a specific job.
 */
const findJobRow = (jobName, jobGroup) => {
    const rows = document.querySelectorAll('tbody tr');
    for (const row of rows) {
        const nameCell = row.querySelector('td:first-child');
        const groupCell = row.querySelector('td:nth-child(2)');
        if (nameCell?.textContent.trim() === jobName &&
            groupCell?.textContent.trim() === jobGroup) {
            return row;
        }
    }
    return null;
};

/**
 * Update the status badge in the row.
 */
const updateStatusBadge = (row, status) => {
    const statusCell = row.querySelector('td:nth-child(5)');
    if (!statusCell) return;

    let badgeClass = 'bg-secondary';
    if (status === 'Running') {
        badgeClass = 'bg-success';
    } else if (status === 'Paused') {
        badgeClass = 'bg-warning text-dark';
    } else if (status === 'Failed') {
        badgeClass = 'bg-danger';
    }

    statusCell.innerHTML = `<span class="badge ${badgeClass}">${escapeHtml(status)}</span>`;
};

/**
 * Update the action buttons based on current status.
 */
const updateActionButtons = (row, status, jobName, jobGroup) => {
    const actionsCell = row.querySelector('td:last-child');
    if (!actionsCell) return;

    const isRunning = status === 'Running';
    const isPaused = status === 'Paused';
    const escapedName = escapeHtml(jobName);
    const escapedGroup = escapeHtml(jobGroup);

    let buttonsHtml = '<div class="btn-group" role="group">';

    if (isRunning) {
        buttonsHtml += `
            <form method="post" action="/Admin/Jobs/CancelJob" class="d-inline">
                <input type="hidden" name="jobName" value="${escapedName}" />
                <input type="hidden" name="jobGroup" value="${escapedGroup}" />
                <button type="submit" class="btn btn-danger btn-sm"
                        onclick="return confirm('Cancel this running job?')"
                        title="Request cancellation of this running job">
                    Cancel
                </button>
            </form>`;
    } else if (isPaused) {
        buttonsHtml += `
            <form method="post" action="/Admin/Jobs/ResumeJob" class="d-inline">
                <input type="hidden" name="jobName" value="${escapedName}" />
                <input type="hidden" name="jobGroup" value="${escapedGroup}" />
                <button type="submit" class="btn btn-success btn-sm"
                        title="Resume scheduled execution">
                    Resume
                </button>
            </form>`;
    } else {
        buttonsHtml += `
            <form method="post" action="/Admin/Jobs/StartJob" class="d-inline">
                <input type="hidden" name="jobName" value="${escapedName}" />
                <input type="hidden" name="jobGroup" value="${escapedGroup}" />
                <button type="submit" class="btn btn-primary btn-sm"
                        title="Run this job immediately">
                    Start
                </button>
            </form>
            <form method="post" action="/Admin/Jobs/PauseJob" class="d-inline ms-1">
                <input type="hidden" name="jobName" value="${escapedName}" />
                <input type="hidden" name="jobGroup" value="${escapedGroup}" />
                <button type="submit" class="btn btn-warning btn-sm"
                        title="Stop future scheduled runs (does not stop a running job)">
                    Pause
                </button>
            </form>`;
    }

    buttonsHtml += '</div>';
    actionsCell.innerHTML = buttonsHtml;
};

/**
 * Update the last run time cell.
 */
const updateLastRunTime = (row, timestampUtc) => {
    const lastRunCell = row.querySelector('td:nth-child(3)');
    if (!lastRunCell) return;

    const date = new Date(timestampUtc);
    const formatted = date.toLocaleString(undefined, {
        weekday: 'short',
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit'
    });
    lastRunCell.innerHTML = `<span>${formatted}</span>`;
};

/**
 * Show notification for job events using wayfarer utilities.
 */
const showJobNotification = (eventType, jobName, errorMessage) => {
    let alertType = 'info';
    let message = '';

    switch (eventType) {
        case 'job_started':
            alertType = 'info';
            message = `Job "${jobName}" started`;
            break;
        case 'job_completed':
            alertType = 'success';
            message = `Job "${jobName}" completed`;
            break;
        case 'job_failed':
            alertType = 'danger';
            message = `Job "${jobName}" failed${errorMessage ? ': ' + errorMessage : ''}`;
            break;
        case 'job_cancelled':
            alertType = 'warning';
            message = `Job "${jobName}" cancelled`;
            break;
    }

    wayfarer.showAlert(alertType, message);
};

/**
 * Escape HTML to prevent XSS.
 */
const escapeHtml = (text) => {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
};
