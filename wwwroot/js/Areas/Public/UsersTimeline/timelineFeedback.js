import { message, number } from '../../../localization/messages.js';
import { formatTimelineDate } from './timelineDetail.js';

/** Uses the public adapter's established paging envelope, keeping legacy error prose out of the UI. */
export const readTimelineLocations = async (response, renderLocations) => {
    if (response.status === 404) {
        renderLocations([]);
        return message('PublicTimeline.Unavailable', 'This timeline is unavailable.');
    }
    if (!response.ok) throw new Error(`Timeline response: ${response.status}`);
    const result = await response.json();
    if (!result.success || !Array.isArray(result.data)) throw new Error('Timeline locations unavailable');
    renderLocations(result.data);
    return result.data.length ? '' :
        message('PublicTimeline.NoLocations', 'No public locations in this map view.');
};

/** Formats available history statistics in the viewer's existing timezone. */
export const formatTimelineSummary = (stats, timeZone) => {
    const fields = [
        [stats.totalLocations, 'TotalLocations', 'Total locations: {0}', number],
        [stats.fromDate, 'FromDate', 'From: {0}', value => formatTimelineDate(value, timeZone)],
        [stats.toDate, 'ToDate', 'To: {0}', value => formatTimelineDate(value, timeZone)],
        [stats.countriesVisited, 'Countries', 'Countries: {0}', number],
        [stats.regionsVisited, 'Regions', 'Regions: {0}', number],
        [stats.citiesVisited, 'Cities', 'Cities: {0}', number],
    ];
    return fields.flatMap(([value, key, readable, format]) =>
        value == null ? [] : [message(`PublicTimeline.${key}`, readable, format(value))]).join(' | ');
};
