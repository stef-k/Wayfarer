/** Whole browser-local preferences are subject-bound; canonical binary64 rules never round through display text. */
export const defaultRules = () => ({ maximumGapSeconds: 7200, speedEnabled: true, maximumSpeedMetresPerSecond: 350 });
const validPositive = number => typeof number === 'number' && Number.isFinite(number) && number > 0;
export const validRules = rules => rules && validPositive(rules.maximumGapSeconds)
    && typeof rules.speedEnabled === 'boolean' && validPositive(rules.maximumSpeedMetresPerSecond);
const displayNumber = value => value === 0 || value >= 0.01 && value < 1e8 ? value.toFixed(2) : value.toExponential(8);
/** Extreme finite canonical values must not display Infinity after unit conversion. */
export const ruleDisplay = (value, factor) => {
    const converted = value / factor;
    if (Number.isFinite(converted) && converted > 0) return displayNumber(converted);
    // Scale only the scientific coefficient when the displayed unit exceeds binary64's range.
    const [coefficient, exponent] = value.toExponential(16).split('e').map(Number);
    const scaled = coefficient / factor;
    const shift = Math.floor(Math.log10(scaled));
    return `${(scaled / 10 ** shift).toFixed(10)}e${exponent + shift >= 0 ? '+' : ''}${exponent + shift}`;
};
export class CalendarPreferences {
    constructor(storage, subject, origin) {
        this.storage = storage; this.key = `wayfarer-calendar-preferences/1:${origin}:${subject}`;
        this.value = { version: 1, timeBasis: 'capture', rules: defaultRules() };
        try {
            const raw = storage.getItem(this.key);
            if (raw !== null) {
                const saved = JSON.parse(raw);
                if (saved.version !== 1 || !['capture', 'viewer'].includes(saved.timeBasis) || !validRules(saved.rules)) throw new Error();
                this.value = saved;
            }
        } catch { this.error = 'Saved Timeline preferences are unavailable or use an unknown version. Defaults are shown; Reset and Apply can replace them.'; }
    }
    /** Save/read-back succeeds before any applied generation changes. */
    save(value) {
        const raw = JSON.stringify({ version: 1, ...value });
        try {
            this.storage.setItem(this.key, raw);
            if (this.storage.getItem(this.key) !== raw) throw new Error();
        } catch { throw new Error('Preferences could not be saved. Your applied rules and draft are retained. Retry Apply.'); }
        this.value = JSON.parse(raw); this.error = null;
    }
    openDraft(retained = null) {
        this.draft = retained || { original: structuredClone(this.value.rules), unit: 'hours', gapEdited: false, speedEdited: false,
            gap: ruleDisplay(this.value.rules.maximumGapSeconds, 3600), speed: ruleDisplay(this.value.rules.maximumSpeedMetresPerSecond, 1 / 3.6),
            speedEnabled: this.value.rules.speedEnabled, locale: Intl.NumberFormat().resolvedOptions().locale };
        return this.draft;
    }
    /** Presentation unit changes preserve original seconds until the user actually edits that field. */
    changeUnit(unit) {
        const current = this.rulesFromDraft();
        this.draft.original.maximumGapSeconds = current.maximumGapSeconds;
        this.draft.gapEdited = false; this.draft.unit = unit;
        this.draft.gap = ruleDisplay(current.maximumGapSeconds, unit === 'hours' ? 3600 : 60);
    }
    rulesFromDraft() {
        const draft = this.draft;
        const numeric = text => {
            if (!/^\s*[+]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?\s*$/i.test(text)) throw new Error('Enter a positive finite number using a decimal point.');
            return Number(text);
        };
        const gap = draft.gapEdited ? numeric(draft.gap) * (draft.unit === 'hours' ? 3600 : 60) : draft.original.maximumGapSeconds;
        const speed = draft.speedEdited ? numeric(draft.speed) / 3.6 : draft.original.maximumSpeedMetresPerSecond;
        const rules = { maximumGapSeconds: gap, speedEnabled: draft.speedEnabled, maximumSpeedMetresPerSecond: speed };
        if (!validRules(rules)) throw new Error('Rules must be positive finite values without overflow or underflow.');
        return rules;
    }
    applyDraft() {
        const rules = this.rulesFromDraft();
        this.save({ ...this.value, rules }); this.draft = null;
        return rules;
    }
    resetDraft() {
        const previous = this.value;
        this.value = { ...previous, rules: defaultRules() }; this.openDraft(); this.value = previous;
    }
}
