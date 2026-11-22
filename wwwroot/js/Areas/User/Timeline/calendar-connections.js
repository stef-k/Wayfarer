import { analysisFacts, distanceSummary } from './calendar-analysis-text.js';

export { distanceText } from './calendar-analysis-text.js';

/** Bind the connection-rule dialog to its retained draft and the current analysis observation. */
export const createCalendarConnections = owner => {
    const modal = document.getElementById('rulesDialog');
    const gap = document.getElementById('maximumGap'), speed = document.getElementById('maximumSpeed');
    const unit = document.getElementById('gapUnit'), enabled = document.getElementById('speedEnabled');
    const error = document.getElementById('rulesError');
    let trigger;
    const populate = () => {
        const draft = owner.preferences.draft;
        gap.value = draft.gap; speed.value = draft.speed; unit.value = draft.unit; enabled.checked = draft.speedEnabled;
    };
    const open = () => {
        trigger = document.activeElement;
        owner.preferences.openDraft(owner.preferences.draft || owner.ruleDraft); populate();
        error.textContent = owner.preferences.error || ''; bootstrap.Modal.getOrCreateInstance(modal).show();
    };
    for (const [input, name, edited] of [[gap, 'gap', 'gapEdited'], [speed, 'speed', 'speedEdited']]) input.addEventListener('input', () => {
        owner.preferences.draft[name] = input.value; owner.preferences.draft[edited] = true; owner.retain();
    });
    unit.addEventListener('change', () => {
        try { owner.preferences.changeUnit(unit.value); populate(); owner.retain(); }
        catch (failure) { error.textContent = failure.message; unit.value = owner.preferences.draft.unit; }
    });
    enabled.addEventListener('change', () => { owner.preferences.draft.speedEnabled = enabled.checked; owner.retain(); });
    document.getElementById('resetRules').addEventListener('click', () => { owner.preferences.resetDraft(); populate(); owner.retain(); });
    document.getElementById('cancelRules').addEventListener('click', () => {
        owner.preferences.draft = null; owner.ruleDraft = null; owner.retain(); bootstrap.Modal.getInstance(modal)?.hide();
    });
    document.getElementById('applyRules').addEventListener('click', () => {
        try { owner.setRules(); bootstrap.Modal.getInstance(modal)?.hide(); }
        catch (failure) { error.textContent = failure.message; }
    });
    modal.addEventListener('hidden.bs.modal', () => { owner.retain(); trigger?.focus(); });
    document.getElementById('recordedPath').addEventListener('change', event => owner.setPath(event.target.checked));
    document.getElementById('cancelCalculation').addEventListener('click', () => owner.cancelCalculation());
    const render = () => {
        const analysis = owner.state.analysis, status = owner.state.status.analysis;
        document.getElementById('distanceSummary').textContent = distanceSummary(analysis, status, owner.path);
        document.getElementById('recordedPath').checked = owner.path;
        document.getElementById('analysisFacts').textContent = owner.state.errors.analysis || analysisFacts(analysis);
    };
    return { open, render };
};
