import { message, number } from './messages.js';

/** Supplies the two ruler instructions for inline or stacked display. */
export const measureInstructions = () => [
    message('Shell.MeasureAddPoints', 'Click to add points.'),
    message('Shell.MeasureCancel', 'Press Esc to cancel.')
];

/** Formats the map label without changing the measured value. */
export const formatMapDistance = kilometers => message('Shell.DisplayKilometres', '{0} km',
    number(kilometers, { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
