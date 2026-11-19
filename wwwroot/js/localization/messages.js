const fallback = 'en-US';
const element = globalThis.document?.getElementById?.('wayfarer-locale');
const catalog = element ? JSON.parse(element.textContent) : { locale: fallback, fallback, messages: {}, english: {} };

/** The locale snapshot of this document, including any subsequent asynchronous feedback. */
export const locale = catalog.locale;

/** Resolves whole messages with positional arguments for text sinks. */
export const createMessages = (bundle) => {
    const active = new Intl.PluralRules(bundle.locale);
    const english = new Intl.PluralRules(bundle.fallback);
    const interpolate = (pattern, args) => pattern.replace(/\{(\d+)\}/g, (slot, index) => String(args[index] ?? slot));
    const message = (key, readable, ...args) =>
        interpolate(bundle.messages[key] ?? bundle.english[key] ?? readable, args);
    const plural = (key, count, one, other) => {
        const value = new Intl.NumberFormat(bundle.locale).format(count);
        const localized = bundle.messages[`${key}.${active.select(count)}`];
        const englishKey = english.select(count);
        return interpolate(localized ?? bundle.english[`${key}.${englishKey}`] ?? (englishKey === 'one' ? one : other), [value]);
    };
    return { message, plural };
};

export const { message, plural } = createMessages(catalog);

/** Formats scalar display values separately from URL, form and protocol values. */
export const number = (value, options) => new Intl.NumberFormat(locale, options).format(value);

/** Formats display dates with the owning view's existing timezone options. */
export const date = (value, options) => new Intl.DateTimeFormat(locale, options).format(value);
