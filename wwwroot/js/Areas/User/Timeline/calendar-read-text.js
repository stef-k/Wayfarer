import { text } from './timeline-text.js';

/** Calendar error codes stay on the wire. The document supplies the action and explanation. */
export const calendarReadFailure = ({ code, name }) => {
    if (name === 'AbortError') return text('ReadCancelled', 'Read cancelled. Refresh / Retry to read the same scope.');
    switch (code) {
        case 'continuationInvalid':
            return text('ContinuationInvalid', 'The record page expired or changed. Load records again to start from the same scope.');
        case 'historyOwnerAdmissionExceeded':
        case 'historyAdmissionUnavailable':
            return text('ReadBusy', 'Timeline reads are busy. Keep this scope and retry after the displayed time.');
        case 'parametersInvalid':
            return text('ReadInvalid', 'The read did not accept these dates or filters. Check the selected scope.');
        case 'historyBudgetExceeded':
            return text('ReadBudget', 'The read exceeded its time limit. Keep this scope and retry.');
        case 'historyQualificationRequired':
        case 'historyStoreUnavailable':
            return text('ReadStoreUnavailable', 'Timeline history is temporarily unavailable. Keep this scope and retry.');
        default:
            return text('ReadUnavailable', 'Read unavailable. Retain this scope and retry.');
    }
};

/** Operation names render separately from status and retry policy. */
export const operationText = operation => ({
    period: () => text('Period', 'Period'),
    analysis: () => text('Analysis', 'Analysis'),
    records: () => text('Records', 'Records'),
    availability: () => text('DateMarks', 'Date marks'),
    statistics: () => text('GeographicStatistics', 'Geographic statistics')
})[operation]?.();
