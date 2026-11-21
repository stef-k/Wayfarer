import { message, number } from './messages.js';

/** Supplies the two ruler instructions for inline or stacked display. */
export const measureInstructions = () => [
    message('Shell.MeasureAddPoints', 'Click to add points.'),
    message('Shell.MeasureCancel', 'Press Esc to cancel.')
];

/** Adds the localized unit to an already formatted distance. */
export const formatMapDistanceLabel = (value, unit) => unit === 'km'
    ? message('Shell.DisplayKilometres', '{0} km', value)
    : message('Shell.DisplayMetres', '{0} m', value);

/** Formats ruler distances in kilometres to two decimal places. */
export const formatMapDistance = kilometers => formatMapDistanceLabel(
    number(kilometers, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), 'km');
