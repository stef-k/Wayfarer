/**
 * Shared helpers for formatting location timestamps and numeric values.
 * Provides deterministic output (YYYY-MM-DD HH:mm) regardless of navigator language.
 */

/**
 * Formats a numeric value to a specified number of decimal places for display.
 * Coordinates should NOT use this function - they need full precision.
 * Use for: accuracy, speed, altitude, and other float/double display values.
 * @param {number|string|null|undefined} value - The value to format
 * @param {number} [decimals=2] - Number of decimal places (default: 2)
 * @returns {string|null} Formatted number string, or null if value is null/undefined
 */
export const formatDecimal = (value, decimals = 2) => {
    if (value == null) return null;
    const num = typeof value === 'string' ? parseFloat(value) : value;
    if (Number.isNaN(num)) return null;
    return num.toFixed(decimals);
};

const DEFAULT_LOCALE = 'en-GB';
const DEFAULT_DATETIME_PARTS = {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
};
const DEFAULT_DATE_PARTS = {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
};

const pad = (value, length = 2) => String(value ?? '').padStart(length, '0');

/**
 * Safely turns ISO string or Date into a valid Date.
 * @param {string|Date|null|undefined} value
 * @returns {Date|null}
 */
const ensureDate = (value) => {
    if (!value) return null;
    if (value instanceof Date) {
        return Number.isNaN(value.getTime()) ? null : value;
    }
    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
};

/**
 * Returns the viewer's current IANA timezone identifier.
 * @returns {string}
 */
export const getViewerTimeZone = () => {
    try {
        return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    } catch {
        return 'UTC';
    }
};

/**
 * Formats a date to YYYY-MM-DD HH:mm for a given timezone.
 * @param {Object} opts
 * @param {string|Date} opts.iso ISO string or Date instance.
 * @param {string} [opts.displayTimeZone] IANA timezone id. Defaults to viewer TZ.
 * @param {boolean} [opts.includeOffset=true] When true append " (GMT+/-HH:mm ZoneName)".
 * @param {string} [opts.locale='en-GB'] Locale passed to Intl (for numerals). Uses en-GB to stabilise separators.
 * @returns {string}
 */
export const formatDateTime = ({
    iso,
    displayTimeZone,
    includeOffset = true,
    locale = DEFAULT_LOCALE,
} = {}) => {
    const date = ensureDate(iso);
    if (!date) return '';

    const tz = displayTimeZone || getViewerTimeZone();
    let formatted = '';

    try {
        const parts = new Intl.DateTimeFormat(locale, {
            ...DEFAULT_DATETIME_PARTS,
            timeZone: tz,
        }).formatToParts(date);

        const lookup = Object.fromEntries(parts.map(p => [p.type, p.value]));
        formatted = `${lookup.year}-${lookup.month}-${lookup.day} ${lookup.hour}:${lookup.minute}`;
    } catch {
        // Fallback: manual UTC formatting if Intl fails
        formatted = `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())} ${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}`;
    }

    if (includeOffset) {
        const offsetLabel = formatOffsetLabel(date, tz);
        formatted = offsetLabel ? `${formatted} ${offsetLabel}` : formatted;
    }

    return formatted;
};

/**
 * Formats a date to YYYY-MM-DD for a given timezone.
 * @param {Object} opts
 * @param {string|Date} opts.iso
 * @param {string} [opts.displayTimeZone]
 * @param {string} [opts.locale='en-GB']
 * @returns {string}
 */
export const formatDate = ({
    iso,
    displayTimeZone,
    locale = DEFAULT_LOCALE,
} = {}) => {
    const date = ensureDate(iso);
    if (!date) return '';

    const tz = displayTimeZone || getViewerTimeZone();
    try {
        const parts = new Intl.DateTimeFormat(locale, {
            ...DEFAULT_DATE_PARTS,
            timeZone: tz,
        }).formatToParts(date);
        const lookup = Object.fromEntries(parts.map(p => [p.type, p.value]));
        return `${lookup.year}-${lookup.month}-${lookup.day}`;
    } catch {
        // Fallback to UTC
        return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`;
    }
};

/**
 * Returns total offset label (e.g., "(GMT+03:00 Europe/Athens)").
 * Calculates the actual offset for the target timezone at the given date (respects DST).
 * @param {Date} date
 * @param {string} tz
 * @returns {string}
 */
export const formatOffsetLabel = (date, tz) => {
    if (!tz) return '';
    try {
        // Use Intl to get offset from the timezone directly
        // The locale only affects digit formatting, not timezone calculation
        const formatter = new Intl.DateTimeFormat(DEFAULT_LOCALE, {
            timeZone: tz,
            timeZoneName: 'shortOffset' // e.g., "GMT+2"
        });

        const parts = formatter.formatToParts(date);
        const offsetPart = parts.find(p => p.type === 'timeZoneName');

        if (offsetPart && offsetPart.value.startsWith('GMT')) {
            return `(${offsetPart.value} ${tz})`;
        }

        // Fallback: just show timezone name
        return `(${tz})`;
    } catch {
        return `(${tz})`;
    }
};

/**
 * Convenience helper returning both viewer-local and source-local formatted timestamps.
 * @param {Object} params
 * @param {string|Date} params.iso
 * @param {string} [params.sourceTimeZone]
 * @param {string} [params.viewerTimeZone]
 * @returns {{ viewer: string, viewerTimeZone: string, source?: string, sourceTimeZone?: string }}
 */
export const formatViewerAndSourceTimes = ({
    iso,
    sourceTimeZone,
    viewerTimeZone,
} = {}) => {
    const viewerTz = viewerTimeZone || getViewerTimeZone();
    const viewer = formatDateTime({ iso, displayTimeZone: viewerTz, includeOffset: true });
    const response = {
        viewer,
        viewerTimeZone: viewerTz,
    };

    if (sourceTimeZone) {
        response.sourceTimeZone = sourceTimeZone;
        response.source = formatDateTime({ iso, displayTimeZone: sourceTimeZone, includeOffset: true });
    }

    return response;
};

/**
 * Converts current Date to an ISO-like YYYY-MM-DD string in viewer timezone for use with <input type="date">.
 * @param {Date} [date]
 * @returns {string}
 */
export const currentDateInputValue = (date = new Date()) => formatDate({ iso: date });

/**
 * Converts current Date to an ISO-like YYYY-MM string (yyyy-MM) in viewer timezone for <input type="month">.
 * @param {Date} [date]
 * @returns {string}
 */
export const currentMonthInputValue = (date = new Date()) => {
    const dayString = formatDate({ iso: date });
    if (!dayString) return '';
    return dayString.slice(0, 7);
};

/**
 * Converts current Date to 4-digit year string in viewer timezone for numeric inputs.
 * @param {Date} [date]
 * @returns {string}
 */
export const currentYearInputValue = (date = new Date()) => {
    const dayString = formatDate({ iso: date });
    return dayString ? dayString.slice(0, 4) : '';
};

/**
 * Calculates the target month (yyyy-MM) after shifting by delta months in viewer timezone.
 * @param {string} currentMonth formatted as yyyy-MM
 * @param {number} delta positive or negative integer
 * @param {string} [timeZone]
 * @returns {string}
 */
export const shiftMonth = (currentMonth, delta = 0, timeZone) => {
    if (!currentMonth) {
        return currentMonthInputValue();
    }
    const [yearStr, monthStr] = currentMonth.split('-');
    const year = Number.parseInt(yearStr, 10);
    const monthIndex = Number.parseInt(monthStr, 10) - 1;
    if (Number.isNaN(year) || Number.isNaN(monthIndex)) {
        return currentMonthInputValue();
    }
    const tz = timeZone || getViewerTimeZone();
    const base = new Date(Date.UTC(year, monthIndex, 1));
    try {
        const zoned = new Date(base.toLocaleString('en-US', { timeZone: tz }));
        zoned.setMonth(zoned.getMonth() + delta);
        return formatDate({ iso: zoned, displayTimeZone: tz }).slice(0, 7);
    } catch {
        // fall back to plain math
        const provisional = new Date(Date.UTC(year, monthIndex + delta, 1));
        return `${provisional.getUTCFullYear()}-${pad(provisional.getUTCMonth() + 1)}`;
    }
};
