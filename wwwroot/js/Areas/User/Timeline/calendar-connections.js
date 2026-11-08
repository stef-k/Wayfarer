/** Presentation rounds distance only; canonical controls live with the independent preference owner. */
export const distanceText = metres => metres === 0 ? '0 m' : metres < 0.01 ? '<0.01 m'
    : metres < 1000 ? `${metres.toLocaleString(undefined, { maximumFractionDigits: 2 })} m`
        : `${(metres / 1000).toLocaleString(undefined, { maximumFractionDigits: 2 })} km`;
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
        const label = !analysis || analysis.coverage === 'unknown' ? 'Distance unavailable'
            : analysis.coverage === 'partial' ? 'Distance in available segments — Partial' : 'Distance between recorded locations';
        document.getElementById('distanceSummary').textContent = `${label}${analysis?.distanceMetres != null ? ': ' + distanceText(analysis.distanceMetres) : ''} · ${status || 'pending'}${analysis ? ' · as of ' + analysis.asOfUtc : ''}${owner.path ? ' · path ' + (analysis?.geometry.state || 'pending') : ''}`;
        document.getElementById('recordedPath').checked = owner.path;
        document.getElementById('analysisFacts').textContent = analysis
            ? `Source: ${analysis.source}\nCalendar: ${JSON.stringify(analysis.selection)}\nObservation: ${analysis.asOfUtc}\nCoverage: ${analysis.coverage}\nApplied gap: ${analysis.rules.maximumGapSeconds} seconds\nSpeed heuristic: ${analysis.rules.speedEnabled ? analysis.rules.maximumSpeedMetresPerSecond + ' m/s' : 'Off'}\nCounts: ${JSON.stringify(analysis.counts)}\nUnplaceable source event: ${analysis.unplaceableEventExists}\nGeometry: ${analysis.geometry.state}${analysis.geometry.reason ? ' · ' + analysis.geometry.reason : ''}\nThese are separate live observations from map, records and date marks.`
            : 'No successful analysis observation yet. Refresh / Retry retains the applied scope.';
    };
    return { open, render };
};
