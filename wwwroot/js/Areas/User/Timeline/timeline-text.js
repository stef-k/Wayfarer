import { date, locale, message, number, plural } from '../../../localization/messages.js';
import { encodeFeatureText as encode } from '../../../util/feature-metadata.js';

/** Timeline presentation uses the initiating document's locale and leaves protocol values intact. */
export const text = (key, english, ...args) => message(`Timeline.${key}`, english, ...args);
export const html = (key, english, ...args) => encode(text(key, english, ...args));
export const countText = (key, count, one, other) => plural(`Timeline.${key}`, count, one, other);

/** Calendar counts arrive as exact decimal strings, including values beyond JS safe integers. */
export const count = value => number(typeof value === 'string' && /^\d+$/.test(value) ? BigInt(value) : value);
export const coordinate = value => number(value, { useGrouping: false, maximumSignificantDigits: 21 });

/** ISO civil dates use a UTC carrier solely for display. The viewer zone cannot shift the selected day. */
export const civilDate = (iso, options = { dateStyle: 'medium' }) =>
    date(new Date(`${iso}T00:00:00Z`), { ...options, timeZone: 'UTC' });

/** Observation times display in UTC, as they did when presented as protocol timestamps. */
export const observedAt = iso => date(new Date(iso), { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'UTC' }) + ' UTC';

export const basisText = basis => basis === 'capture' ? text('RecordedCalendar', 'Recorded-location calendar')
    : text('ViewerCalendar', 'My time zone');

export const scopeText = scope => ({
    day: () => text('Day', 'Day'),
    week: () => text('Week', 'Week'),
    month: () => text('Month', 'Month'),
    year: () => text('Year', 'Year'),
    custom: () => text('Custom', 'Custom')
})[scope]();

export const periodCaption = (from, through) => from === through ? civilDate(from)
    : text('DateRange', '{0} – {1}', civilDate(from), civilDate(through));

/** The ordering of named weekdays remains the browser/frozen selection's policy. */
export const weekdayName = (day, width = 'long') => civilDate(`2024-01-0${day}`, { weekday: width });

export const list = values => new Intl.ListFormat(locale).format(values);
export const statusText = status => ({
    idle: () => text('NotLoaded', 'Not loaded'),
    loading: () => text('Loading', 'Loading…'),
    refreshing: () => text('Refreshing', 'Refreshing…'),
    current: () => text('Current', 'current'),
    cancelled: () => text('Cancelled', 'Cancelled'),
    failed: () => text('Failed', 'Failed')
})[status]?.() ?? text('Pending', 'Pending');

/** Localize Leaflet control names without changing keyboard or zoom behavior. */
export const installTimelineZoom = map => L.control.zoom({
    zoomInTitle: message('Shell.ZoomIn', 'Zoom in'),
    zoomOutTitle: message('Shell.ZoomOut', 'Zoom out')
}).addTo(map);
