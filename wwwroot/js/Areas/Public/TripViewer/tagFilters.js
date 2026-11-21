import { message, number } from '../../../localization/messages.js';

/** Renders authored tag labels as text and keeps slugs as data attributes. */
const suggestionButton = item => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'list-group-item list-group-item-action';
    button.dataset.suggestSlug = item.slug;
    const label = document.createElement('span');
    label.className = 'fw-semibold';
    label.textContent = item.name;
    const count = document.createElement('span');
    count.className = 'text-muted small ms-2';
    count.textContent = number(item.count);
    button.append(label, count);
    return button;
};

/** Preserves discovery's URL filters and resets pagination when a tag filter changes. */
export const initTagFilters = () => {
    const root = document.querySelector('[data-tag-filter]');
    const selected = new Set(root.dataset.tags.split(',').filter(Boolean));
    let mode = root.dataset.tagMode;
    const input = root.querySelector('[data-tag-input]');
    const suggestions = root.querySelector('[data-tag-suggestions]');
    const status = root.querySelector('[data-tag-status]');
    let timer;
    let request;

    const apply = () => {
        const params = new URLSearchParams(window.location.search);
        if (selected.size) params.set('tags', [...selected].join(','));
        else params.delete('tags');
        params.set('tagMode', mode);
        params.set('page', '1');
        window.location.href = `${window.location.pathname}?${params}`;
    };

    const addTag = slug => {
        if (!slug || selected.has(slug)) return;
        selected.add(slug);
        apply();
    };

    const render = (items = [], feedback = '') => {
        suggestions.replaceChildren(...items.map(suggestionButton));
        suggestions.classList.toggle('d-none', items.length === 0);
        status.textContent = feedback;
    };

    const dismiss = () => {
        clearTimeout(timer);
        request?.abort();
        render();
    };

    const load = async term => {
        request = new AbortController();
        const { signal } = request;
        render([], message('Discovery.TagLoading', 'Loading tag suggestions…'));
        try {
            const response = await fetch(`${root.dataset.suggestUrl}?q=${encodeURIComponent(term)}`, { signal });
            if (!response.ok) throw new Error(`Tag suggestions response: ${response.status}`);
            const items = await response.json();
            if (!Array.isArray(items)) throw new Error('Tag suggestions response was not a list');
            if (!signal.aborted) render(items, items.length ? '' : message('Discovery.TagEmpty', 'No matching tags.'));
        } catch (error) {
            if (signal.aborted) return;
            console.warn('Failed to load tag suggestions', error);
            render([], message('Discovery.TagFailed', 'Could not load tag suggestions.'));
        }
    };

    input.addEventListener('input', () => {
        dismiss();
        const term = input.value.trim();
        if (term.length >= 2) timer = setTimeout(() => load(term), 200);
    });
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            addTag(suggestions.firstElementChild?.dataset.suggestSlug);
        } else if (event.key === 'Escape') {
            dismiss();
        } else if (event.key === 'Backspace' && !input.value && selected.size) {
            selected.delete([...selected].at(-1));
            apply();
        }
    });

    root.querySelector('[data-tag-input-wrapper]').addEventListener('click', event => {
        if (!event.target.closest('[data-tag-chip]')) input.focus();
    });
    root.querySelectorAll('[data-tag-chip] .btn-close').forEach(button => {
        button.addEventListener('click', () => {
            if (selected.delete(button.closest('[data-tag-chip]').dataset.tagSlug)) apply();
        });
    });
    root.querySelector('[data-tag-mode-toggle]').addEventListener('click', () => {
        mode = mode === 'all' ? 'any' : 'all';
        apply();
    });
    root.querySelector('[data-clear-tags]').addEventListener('click', () => {
        selected.clear();
        apply();
    });
    suggestions.addEventListener('click', event => {
        addTag(event.target.closest('[data-suggest-slug]')?.dataset.suggestSlug);
    });
    document.addEventListener('click', event => {
        if (event.target !== input && !suggestions.contains(event.target)) dismiss();
    });
};
