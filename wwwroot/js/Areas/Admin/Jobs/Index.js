import { date, message } from "../../../localization/messages.js";

/**
 * Admin Jobs Index - SSE for real-time job status updates.
 * Uses MvcFrontendKit convention: automatically loaded for Admin/Jobs/Index view.
 */

document.addEventListener('DOMContentLoaded', () => {
    initJobStatusSse();
    // Delegation keeps the same confirmation on forms inserted after an event.
    document.addEventListener('submit', event => {
        if (event.target.matches('form[data-job-cancel]') &&
            !window.confirm(message('AdminJobs.CancelConfirm', 'Cancel this running job?'))) {
            event.preventDefault();
        }
    });
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
                wayfarer.showAlert('warning', message('AdminJobs.LostConnection', 'Lost connection to job status updates. Refresh to reconnect.'));
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
    updateActionButtons(row, status);

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
    return Array.from(document.querySelectorAll('tr[data-job-name]')).find(row =>
        row.dataset.jobName === jobName && row.dataset.jobGroup === jobGroup
    );
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

    const badge = document.createElement('span');
    badge.className = `badge ${badgeClass}`;
    const statusKey = status === 'Not Started' ? 'NotStarted' : status === 'In Progress' ? 'InProgress' : status;
    badge.textContent = message(`AdminJobs.Status.${statusKey}`, message('AdminJobs.Status.Unknown', 'Unknown status'));
    statusCell.replaceChildren(badge);
};

/**
 * Update the action buttons based on current status.
 */
const updateActionButtons = (row, status) => {
    const actionsCell = row.querySelector('td:last-child');
    if (!actionsCell) return;

    let state = 'idle';
    if (status === 'Running') {
        state = row.dataset.interruptable === 'true' ? 'running' : 'running-disabled';
    } else if (status === 'Paused') {
        state = 'paused';
    }

    const template = document.getElementById(`job-${state}-actions`);
    const actions = template.content.cloneNode(true);
    actions.querySelectorAll('input[name="jobName"]').forEach(input => { input.value = row.dataset.jobName; });
    actions.querySelectorAll('input[name="jobGroup"]').forEach(input => { input.value = row.dataset.jobGroup; });
    actionsCell.replaceChildren(actions);
};

/**
 * Update the last run time cell.
 */
const updateLastRunTime = (row, timestampUtc) => {
    const lastRunCell = row.querySelector('td:nth-child(3)');
    if (!lastRunCell) return;

    const timestamp = new Date(timestampUtc);
    if (Number.isNaN(timestamp.getTime())) return;
    lastRunCell.textContent = date(timestamp, {
        weekday: 'short',
        year: 'numeric',
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit'
    });
};

/**
 * Show notification for job events using wayfarer utilities.
 */
const showJobNotification = (eventType, jobName, errorMessage) => {
    switch (eventType) {
        case 'job_started':
            wayfarer.showAlert('info', message('AdminJobs.EventStarted', 'Job "{0}" started.', jobName));
            break;
        case 'job_completed':
            wayfarer.showAlert('success', message('AdminJobs.EventCompleted', 'Job "{0}" completed.', jobName));
            break;
        case 'job_failed':
            wayfarer.showAlert('danger', errorMessage
                ? message('AdminJobs.EventFailedDetail', 'Job "{0}" failed: {1}', jobName, errorMessage)
                : message('AdminJobs.EventFailed', 'Job "{0}" failed.', jobName));
            break;
        case 'job_cancelled':
            wayfarer.showAlert('warning', message('AdminJobs.EventCancelled', 'Job "{0}" cancelled.', jobName));
            break;
        default:
            wayfarer.showAlert('info', message('AdminJobs.EventUpdated', 'The status of job "{0}" changed.', jobName));
    }
};
