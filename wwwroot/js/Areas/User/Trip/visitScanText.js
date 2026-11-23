import { message, number, plural } from '../../../localization/messages.js';

/** Cardinal messages for the exact actions shown immediately before applying a visit scan. */
export const visitScanSummary = ({ confirmed, suggested, stale, existing }, countText = plural) => [
    ['summary-create', confirmed, countText('Editor.TripsScanConfirm', confirmed, '{0} confirmed visit will be created', '{0} confirmed visits will be created')],
    ['summary-confirm-suggestions', suggested, countText('Editor.TripsScanSuggest', suggested, '{0} suggested visit will be created', '{0} suggested visits will be created')],
    ['summary-delete-stale', stale, countText('Editor.TripsScanDeleteStale', stale, '{0} stale visit will be deleted', '{0} stale visits will be deleted')],
    ['summary-delete-manual', existing, countText('Editor.TripsScanDeleteExisting', existing, '{0} existing visit will be deleted', '{0} existing visits will be deleted')]
];

/** Uses the scan's saved counts and radii without recomputing eligibility in the browser. */
export const suggestionText = visit => {
    if (visit.hasUserCheckin) return message('Editor.TripsUserCheckedInNearby', 'User checked in nearby');
    const evidence = visit.suggestionEvidence;
    if (!evidence) return message('Editor.TripsGPSPingsNearThisPlace0', 'GPS pings near this place: {0}', number(visit.hitsTotal));
    const pings = count => plural('Editor.TripsScanHits', count, '{0} hit', '{0} hits');
    if (evidence.additionalRadiusMeters != null) {
        return message('Editor.TripsScanCrossTier', 'Cross-tier: {0} within {1} m + {2} within {3} m',
            pings(evidence.count), number(evidence.radiusMeters), pings(evidence.additionalCount), number(evidence.additionalRadiusMeters));
    }
    if (evidence.radiusMeters != null) {
        return message('Editor.TripsScanRadius', '{0} within {1} m', pings(evidence.count), number(evidence.radiusMeters));
    }
    return message('Editor.TripsScanExtended', '{0} within extended range', pings(evidence.count));
};
