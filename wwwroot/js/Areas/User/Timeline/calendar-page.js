import { validateSelection, selectionSearch } from './calendar-scope.js';
import { emptyFilters, filterBody, filtersActive } from './calendar-intent.js';
/** Stable structural comparison excludes response-local observation identities. */
const stable = value => JSON.stringify(value, (_, item) => item && typeof item === 'object' && !Array.isArray(item)
    ? Object.fromEntries(Object.keys(item).sort().map(key => [key, item[key]])) : item);
const matchingIdentity = response => stable({ timeRules: response.timeRules, matchingRules: response.matchingRules,
    timeBasis: response.scope.timeBasis, viewerZone: response.scope.viewerZone, q: response.scope.q,
    activityIds: response.scope.activityIds, includeUnassigned: response.scope.includeUnassigned, area: response.scope.area });
/** Concrete page owner serializes collection work; abort never implies immediate server lease release. */
export class TimelinePage {
    constructor({ subject, selection, context, filters, returnState = {}, intent, preferences, maximumOffset,
        fetchRead, replaceUrl, publish = () => {}, currentSubject = () => subject, restorationPending = false }) {
        Object.assign(this, { subject, intent, preferences, maximumOffset, fetchRead, replaceUrl, publish, currentSubject });
        this.selection = selection; this.context = context; this.filters = filters;
        this.rules = preferences.value.rules; this.path = returnState.path === true;
        this.selectedId = Number.isInteger(returnState.selectedId) ? returnState.selectedId : null;
        this.camera = returnState.camera; this.fitOnNext = !returnState.camera; this.panel = returnState.panel === 'records' ? 'records' : 'map';
        this.scroll = returnState.scroll || 0; this.ruleDraft = returnState.ruleDraft || null;
        this.epoch = 0; this.analysisVersion = 0; this.monthVersion = 0; this.detailVersion = 0;
        this.tail = Promise.resolve(); this.state = { records: [], status: {}, errors: {} };
        this.editor = null; this.restorationPending = restorationPending; this.blocked = restorationPending;
    }
    emit() { this.publish(this); }
    returnState() { return { path: this.path, selectedId: this.selectedId, camera: this.camera, panel: this.panel,
        scroll: this.scroll, ruleDraft: this.preferences.draft || this.ruleDraft }; }
    /** Ordinary retention cannot resolve missing private intent or recreate an ended session slot. */
    retain(strict = false) {
        if (this.blocked || this.restorationPending || this.authEnded) return;
        try { this.intent.save(this.selection, this.context, this.filters, this.returnState()); }
        catch (error) { if (strict) throw error; this.state.storageError = error.message; }
    }
    openEditor(kind, draft) {
        if (this.editor && this.editor.kind !== kind) throw new Error('Apply or Cancel the open query editor first.');
        this.editor = { kind, draft }; this.emit();
    }
    cancelEditor() { this.editor = null; this.closeMonth(); this.emit(); }
    /** Filter persistence is the transaction boundary; failed writes change neither URL nor applied scope. */
    commitFilters(filters) {
        if (this.authEnded) throw new Error('Sign in again before restoring Timeline.');
        filterBody(filters);
        const context = filtersActive(filters) ? crypto.randomUUID() : null;
        this.intent.save(this.selection, context, filters, this.returnState());
        this.filters = structuredClone(filters); this.context = context; this.restorationPending = false; this.blocked = false;
        if (this.editor?.kind === 'filters') this.editor = null;
        this.replaceUrl(selectionSearch(this.selection, context));
        return this.refresh(true);
    }
    clearFilters() { return this.commitFilters(emptyFilters()); }
    commitSelection(selection, fromDateEditor = false) {
        if (this.editor && (this.editor.kind !== 'date' || !fromDateEditor)) throw new Error('Apply or Cancel the open query editor first.');
        validateSelection(selection, this.maximumOffset);
        this.selection = selection; this.fitOnNext = true; this.editor = null; this.closeMonth();
        this.replaceUrl(selectionSearch(selection, this.context)); this.retain();
        return this.refresh(true);
    }
    /** Basis changes save the whole preference first and preserve the exact civil period. */
    applyBasis(basis, zone) {
        const next = { ...this.selection, timeBasis: basis, viewerZone: zone };
        validateSelection(next, this.maximumOffset);
        this.preferences.save({ ...this.preferences.value, timeBasis: basis });
        this.editor = null; return this.commitSelection(next);
    }
    setRules() {
        this.rules = this.preferences.applyDraft(); this.ruleDraft = null;
        this.analysisVersion++; this.state.status.analysis = 'refreshing';
        if (this.active?.operation === 'analysis') this.active.controller.abort();
        this.retain(); this.emit(); return this.enqueue('analysis');
    }
    setPath(enabled) {
        this.path = enabled; this.analysisVersion++;
        const interrupted = this.active?.operation === 'analysis';
        if (interrupted) this.active.controller.abort();
        if (!enabled && this.state.analysis) this.state.analysis = { ...this.state.analysis, geometry: { state: 'off', islands: null } };
        this.retain(); this.emit();
        if (enabled || interrupted || !this.state.analysis) return this.enqueue('analysis');
    }
    /** Refresh replaces traversal ownership and month observations, retaining the open editor and month intent. */
    refresh(newScope = false) {
        if (this.blocked || this.restorationPending || this.authEnded) return Promise.resolve();
        this.epoch++; this.analysisVersion++; this.detailVersion++; this.monthVersion++;
        this.state.availability = null; this.state.status.availability = this.month ? 'loading' : 'idle';
        this.active?.controller.abort(); this.effective = null; this.identity = null;
        this.state.records = []; this.state.nextContinuation = null; this.state.recordsStarted = false;
        this.state.status.records = 'idle';
        this.state.statistics = null;
        if (newScope) { this.state.period = null; this.state.analysis = null; }
        this.state.status.period = 'refreshing'; this.state.status.analysis = 'refreshing'; this.state.errors = {};
        this.emit();
        const period = this.enqueue('period'); const analysis = this.enqueue('analysis');
        const records = this.panel === 'records' ? this.loadRecords() : Promise.resolve();
        const availability = this.month ? this.enqueue('availability', this.month) : Promise.resolve();
        return Promise.all([period, analysis, records, availability]);
    }
    cancelCalculation() {
        this.analysisVersion++;
        if (this.active?.operation === 'analysis') this.active.controller.abort();
        this.state.status.analysis = 'cancelled'; this.emit();
    }
    openRecords() { this.panel = 'records'; this.retain(); this.emit(); if (!this.state.recordsStarted) return this.loadRecords(); }
    loadRecords() {
        if (this.state.status.records === 'loading') return Promise.resolve();
        this.state.status.records = 'loading'; this.emit();
        return this.enqueue('records', this.state.nextContinuation ? { continuation: this.state.nextContinuation } : {});
    }
    openMonth(year, month) {
        if (this.active?.operation === 'availability') this.active.controller.abort();
        this.monthVersion++; this.month = { year, month }; this.state.availability = null;
        this.state.status.availability = 'loading'; this.emit(); return this.enqueue('availability', { year, month });
    }
    closeMonth() {
        this.monthVersion++; this.month = null; this.state.availability = null;
        if (this.active?.operation === 'availability') this.active.controller.abort();
    }
    /** Account/session loss clears this page only; mutation drafts retain their original subject authority. */
    sessionEnded() {
        this.authEnded = true; this.epoch++; this.detailVersion++; this.blocked = true; this.active?.controller.abort(); this.closeMonth(); this.intent.clear();
        this.filters = emptyFilters(); this.editor = null; this.state = { records: [], status: {}, errors: { session: 'Session ended or account changed. Sign in again before reading Timeline.' } };
        this.emit();
    }
    /** One serial chain prioritizes already-enqueued core work over optional consumers. */
    enqueue(operation, extra = {}) {
        const epoch = this.epoch, analysis = this.analysisVersion, month = this.monthVersion;
        const current = () => epoch === this.epoch && !this.blocked && !this.restorationPending && !this.authEnded
            && (operation !== 'analysis' || analysis === this.analysisVersion)
            && (operation !== 'availability' || month === this.monthVersion && this.month !== null);
        const run = async () => {
            if (!current()) return;
            if (this.currentSubject() !== this.subject) { this.sessionEnded(); return; }
            const controller = new AbortController(); this.active = { operation, controller };
            const values = this.body(operation, extra);
            this.state.status[operation] = 'loading'; this.emit();
            try {
                const response = await this.fetchRead(operation, values, controller.signal);
                if (!current()) return;
                if (response.subjectId !== this.subject) { this.sessionEnded(); return; }
                this.acceptIdentity(response, operation, values);
                if (!current()) return;
                this.accept(operation, response); delete this.state.errors[operation]; this.state.status[operation] = 'current';
            } catch (error) {
                if (!current()) return;
                if (error.status === 401 || error.status === 403) { this.sessionEnded(); return; }
                this.state.status[operation] = error.name === 'AbortError' ? 'cancelled' : 'failed';
                this.state.errors[operation] = error.code || error.message || 'Read unavailable. Retain this scope and retry.';
                if (error.retryAfter) this.state.retryAt = Date.now() + error.retryAfter * 1000;
                if (error.code === 'continuationInvalid') { this.state.records = []; this.state.nextContinuation = null; this.state.recordsStarted = false; }
            } finally {
                if (this.active?.controller === controller) this.active = null;
                if (current()) this.emit();
            }
        };
        const pending = this.tail.then(run, run); this.tail = pending.catch(() => {}); return pending;
    }
    body(operation, extra) {
        const filters = this.effective || this.filters;
        const selection = operation === 'availability' ? { year: extra.year, month: extra.month,
            timeBasis: this.selection.timeBasis, viewerZone: this.selection.viewerZone } : this.selection;
        const values = { ...selection, ...filterBody(filters) };
        if (operation === 'analysis') Object.assign(values, this.rules, { includeGeometry: this.path });
        if (operation === 'records') Object.assign(values, extra);
        return values;
    }
    /** First successful response establishes canonical matching meaning, even when period failed. */
    acceptIdentity(response, operation, values) {
        if ((response.calendarContract || response.contract) !== 'private-calendar/2' || !response.scope || !response.timeRules || !response.matchingRules) throw new Error('Invalid response identity.');
        const expected = operation === 'availability' ? { dateType: 'month', year: values.year, month: values.month, day: 1,
            timeBasis: values.timeBasis, viewerZone: response.scope.viewerZone } : { ...this.selection, viewerZone: response.scope.viewerZone };
        if (stable(response.selection) !== stable(expected)) throw new Error('Mismatched calendar selection.');
        const identity = matchingIdentity(response);
        if (this.identity && this.identity !== identity) {
            this.state.period = null; this.state.analysis = null; this.state.records = []; this.state.availability = null; this.state.statistics = null;
            this.blocked = true; this.state.errors.identity = 'Matching interpretation changed. Refresh to revalidate the retained filters.'; this.emit();
            throw new Error('Matching interpretation changed.');
        }
        if (!this.identity) {
            this.identity = identity;
            this.effective = { q: response.scope.q, activityIds: response.scope.activityIds || [], includeUnassigned: response.scope.includeUnassigned, area: response.scope.area };
            const intended = [...this.filters.activityIds].sort((a, b) => a - b);
            if (stable(intended) !== stable(this.effective.activityIds)) this.state.activityNotice = 'Retained activity IDs resolved to their current catalog identities.';
            this.selection = { ...this.selection, viewerZone: response.scope.viewerZone };
            this.replaceUrl(selectionSearch(this.selection, this.context)); this.retain();
        }
    }
    accept(operation, response) {
        if (operation === 'records') {
            const ids = new Set(this.state.records.map(item => item.locationId));
            this.state.records.push(...response.items.filter(item => !ids.has(item.locationId)));
            this.state.nextContinuation = response.nextContinuation; this.state.recordsStarted = true; this.state.recordsAsOf = response.asOfUtc;
        } else this.state[operation] = response;
    }
    /** Retry is deliberate and honors shared-admission backoff; it never removes filters to obtain success. */
    retry() {
        if (this.authEnded) throw new Error('Sign in again before restoring Timeline.');
        if (this.restorationPending) throw new Error('Re-enter the unavailable filters or explicitly Continue without filters before restoring Timeline.');
        if (this.state.retryAt > Date.now()) throw new Error('Read admission is cooling down. Retry after the displayed time.');
        this.blocked = false; return this.refresh();
    }
}
