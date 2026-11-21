import { locale } from './messages.js';
import { hasLanguageDraft, languageDestination, navigateInLanguage } from './languageNavigation.js';

const dialog = document.getElementById('language-dialog');
const form = document.getElementById('language-form');
const open = document.getElementById('language-open');
const cancel = document.getElementById('language-cancel');
const keep = document.getElementById('language-keep');
const apply = document.getElementById('language-apply');
const confirm = document.getElementById('language-confirm');
const options = document.getElementById('language-options');
const discard = document.getElementById('language-discard');
const error = document.getElementById('language-error');

let applying = false;
let confirmed = false;
const safeGet = dialog.dataset.safeGet;
const baselines = new WeakMap();

/** Reads display controls, including file choices and multi-selects, independent of submit serialization. */
const valueOf = control => {
    if (control instanceof HTMLSelectElement) return Array.from(control.options, item => item.selected).join(',');
    if (control.type === 'checkbox' || control.type === 'radio') return control.checked;
    return control.value;
};

/** Finds ordinary form controls without reading antiforgery or other hidden values. */
const ordinaryControls = () => Array.from(document.querySelectorAll('input, select, textarea')).filter(control =>
    control.form && control.form !== form && !['hidden', 'submit', 'reset', 'button', 'image'].includes(control.type));

for (const control of ordinaryControls()) baselines.set(control, valueOf(control));
document.addEventListener('focusin', ({target}) => {
    if (target.form && target.form !== form && !baselines.has(target) && typeof target.value === 'string') {
        baselines.set(target, valueOf(target));
    }
});

const changedOrdinaryForm = () => ordinaryControls().some(control =>
    baselines.has(control) && baselines.get(control) !== valueOf(control));

/** Keeps the dialog modal through confirmation and the request, preventing new edits. */
const showConfirmation = (visible) => {
    confirmed = visible;
    discard.hidden = keep.hidden = confirm.hidden = !visible;
    options.hidden = cancel.hidden = apply.hidden = visible;
    if (visible) keep.focus();
};

const setApplying = (busy) => {
    applying = busy;
    for (const control of [options, apply, confirm, cancel, keep]) control.disabled = busy;
    dialog.setAttribute('aria-busy', String(busy));
};

open.hidden = false;
open.addEventListener('click', () => {
    form.reset();
    error.hidden = true;
    showConfirmation(false);
    dialog.showModal();
});
cancel.addEventListener('click', () => dialog.close());
keep.addEventListener('click', () => dialog.close());
dialog.addEventListener('cancel', event => { if (applying) event.preventDefault(); });
dialog.addEventListener('close', () => open.focus());

form.addEventListener('submit', async event => {
    event.preventDefault();
    if (applying) return;
    const data = new FormData(form);
    const different = data.get('locale') !== locale;
    const destination = languageDestination(window.location.href, safeGet);
    if (different && !confirmed && (safeGet || changedOrdinaryForm() || hasLanguageDraft())) {
        showConfirmation(true);
        return;
    }

    error.hidden = true;
    setApplying(true);
    try {
        if (!destination) throw new Error('Unsafe language destination');
        const reply = await fetch(form.dataset.url, { method: 'POST', body: data, credentials: 'same-origin', redirect: 'error' });
        if (reply.status !== 204) throw new Error('Language preference rejected');
        if (different) {
            navigateInLanguage(destination);
            return;
        }
        setApplying(false);
        dialog.close();
    } catch {
        setApplying(false);
        error.hidden = false;
        (confirmed ? confirm : apply).focus();
    }
});
