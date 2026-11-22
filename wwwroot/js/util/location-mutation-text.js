import { message } from '../localization/messages.js';

// Older protected-edit callers depend on these English outcomes. Resolve display copy here.
const legacyKeys = new Map([
    ["Draft was not saved. Keep this page open.", "Shell.LocationDraftNotSaved"],
    ["Location evidence could not be verified. Your draft is retained.", "Shell.LocationEvidenceUnverified"],
    ["Your session has ended. Keep your draft before leaving.", "Shell.LocationMutationSessionEnded"],
    ["This browser cannot safely save this draft. Keep it open in a supported browser.", "Shell.LocationBrowserUnsupported"],
    ["Newer typing is retained and still needs review.", "Shell.LocationNewerDraft"],
    ["Current state satisfies this draft; historical outcome remains unknown.", "Shell.LocationCurrentStateSatisfied"],
    ["Old future effects are excluded; current content differs from your originals. Your draft is retained. Review current history before an informed new conditional edit.", "Shell.LocationOriginalsChanged"],
    ["The earlier action is resolved. Your newer draft is retained; review it against current history before saving.", "Shell.LocationEarlierResolved"],
    ["Historical outcome unknown; old future effects are excluded. Your draft and originals are retained.", "Shell.LocationEarlierExcluded"],
    ["Outcome unknown. Your draft and exact attempt are retained. Save again to check recovery.", "Shell.LocationOutcomeUnknown"],
    ["Outcome could not be verified. Your draft and attempt are retained.", "Shell.LocationOutcomeUnverified"],
    ["Save is pending. Your draft is retained. Save again to check recovery.", "Shell.LocationSavePending"],
    ["The save committed. Current detail is unavailable; your draft and receipt are retained. Save again to refresh.", "Shell.LocationSavedDetailUnavailable"],
    ["The earlier save committed. Newer typing is retained and still needs saving.", "Shell.LocationEarlierSaved"],
    ["Location deleted.", "Shell.LocationDeleted"],
    ["Location saved.", "Shell.LocationSaved"],
    ["The earlier action may still execute. Recover it before adopting current facts.", "Shell.LocationEarlierMayExecute"],
    ["Current facts have the wrong scope.", "Shell.LocationWrongScope"],
    ["Saved command evidence is unavailable or inconsistent. Keep your draft; no save was sent.", "Shell.LocationCommandUnavailable"],
    ["Open the Activity editor to observe its originals before saving.", "Shell.LocationActivityObserve"],
]);

/** A verified rejection keeps its recovery action and gets a localized explanation. */
const rejected = code => {
    switch (code) {
        case 'locationBaseStale':
            return message('Shell.LocationMutationConflict',
                'The record changed since you opened it. Your draft and originals are retained. Review current history.');
        case 'operationExecutionExpired':
            return message('Shell.LocationOperationExpired',
                'The save attempt expired. Review current history before trying again.');
        case 'activityInvalid':
            return message('Shell.LocationInvalidActivity',
                'The selected activity is unavailable. Reopen the editor and choose an available activity.');
        case 'mutationInvalid':
            return message('Shell.LocationInvalidMutation',
                'The submitted change was invalid. Review the recorded details and try again.');
        case 'baseUnavailable':
        case 'baseNormalizationUnsupported':
            return message('Shell.LocationBaseUnavailable',
                'The original record evidence is unavailable. Your draft is retained. Review current history.');
        case 'locationDeleted':
            return message('Shell.LocationAlreadyDeleted', 'This record has already been deleted. Review current history.');
        case 'locationUnavailable':
            return message('Shell.LocationNoLongerAvailable', 'This record is no longer available. Review current history.');
        default:
            return message('Shell.LocationMutationRejected',
                'Save rejected. Your draft and originals are retained. Review current history.');
    }
};

/** Localize a protected outcome without changing its receipt, transport or retry classification. */
export const locationMutationText = (result, fallback) => {
    if (result?.receipt?.state === 'rejected') return rejected(result.receipt.code);
    const original = result?.message;
    const key = legacyKeys.get(original);
    if (key) return message(key, original);
    const status = /^Location detail unavailable [(](\d+)[)]/.exec(original ?? '')?.[1];
    if (status === '401' || status === '403') return message('Shell.LocationMutationSessionEnded',
        'Your session has ended. Keep your draft before leaving.');
    if (status) return message('Shell.LocationDetailUnavailable',
        'Location detail unavailable. Your draft is retained.');
    return fallback;
};

/** Format application copy independently of the diagnostic facts the user is asked to inspect. */
export const locationDeletionText = ({ id, prior, historicalUnknown, previousRecord, record, unavailableFields }) => {
    const earlier = !prior ? '' : historicalUnknown
        ? message('Shell.LocationDeletionRenewUnknown',
            'The earlier action can no longer take effect and its historical outcome is unknown. This is a new conditional deletion. Previously observed detail: {0}.',
            JSON.stringify(previousRecord) ?? '')
        : message('Shell.LocationDeletionRenew',
            'The earlier action can no longer take effect. This is a new conditional deletion. Previously observed detail: {0}.',
            JSON.stringify(previousRecord) ?? '');
    const current = message('Shell.LocationDeletionInspect',
        'Delete the observed location #{0}? This cannot be undone. Stored detail: {1}. Unavailable facts: {2}.',
        id, JSON.stringify(record), JSON.stringify(unavailableFields));
    return earlier ? earlier + ' ' + current : current;
};


/** An unsettled delete recovers through the inspection action, which has no Save button. */
export const locationDeletionOutcomeText = (result, fallback) => {
    const key = legacyKeys.get(result?.message);
    if (['Shell.LocationOutcomeUnknown', 'Shell.LocationOutcomeUnverified', 'Shell.LocationSavePending'].includes(key)) {
        return message('Shell.LocationDeletionRecover',
            'Deletion could not be confirmed. The earlier action may still finish. Open the location details and choose Delete to review the retained attempt.');
    }
    return locationMutationText(result, fallback);
};
