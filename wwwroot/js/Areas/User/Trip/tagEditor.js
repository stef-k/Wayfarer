const getRequestVerificationToken = () => {
    return document.querySelector('#trip-form input[name="__RequestVerificationToken"]')?.value;
};

const parseTags = raw => {
    try {
        const parsed = JSON.parse(raw || '[]');
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return [];
    }
};

const renderTagPills = (container, tags) => {
    if (!container) {
        return;
    }

    if (!tags.length) {
        container.innerHTML = '<span class="text-muted small">No tags yet.</span>';
        return;
    }

    container.innerHTML = tags
        .map(tag => `
            <span class="badge text-bg-primary-subtle text-primary d-inline-flex align-items-center gap-1"
                  data-tag-chip data-tag-slug="${tag.slug}">
                <span>${tag.name}</span>
                <button type="button" class="btn-close" style="width: 0.5em; height: 0.5em;" aria-label="Remove tag"></button>
            </span>`)
        .join('');
};

const showFeedback = (el, message, isError = true) => {
    if (!el) {
        return;
    }

    if (!message) {
        el.classList.add('d-none');
        el.textContent = '';
        return;
    }

    el.classList.toggle('text-danger', isError);
    el.classList.toggle('text-success', !isError);
    el.classList.remove('d-none');
    el.textContent = message;
};

const fetchJson = async (url, options = {}) => {
    const resp = await fetch(url, options);
    const data = resp.headers.get('Content-Type')?.includes('application/json')
        ? await resp.json()
        : null;

    if (!resp.ok) {
        throw new Error(data?.error || resp.statusText);
    }

    return data;
};

export const initTripTagEditor = () => {
    const editor = document.querySelector('[data-trip-tags]');
    if (!editor) {
        return;
    }

    const tripId = editor.dataset.tripId;
    const limit = parseInt(editor.dataset.limit ?? '15', 10);
    const tagListEl = editor.querySelector('[data-tag-list]');
    const inputEl = editor.querySelector('[data-tag-input]');
    const addBtn = editor.querySelector('[data-tag-add]');
    const feedbackEl = editor.querySelector('[data-tag-feedback]');
    const suggestionsEl = editor.querySelector('[data-tag-suggestions]');
    const suggestUrl = editor.dataset.suggestUrl || '/Public/Tags/Suggest';
    const token = getRequestVerificationToken();

    let tags = parseTags(editor.dataset.tags);
    let debounceHandle;
    let currentSuggestions = [];

    renderTagPills(tagListEl, tags);

    const refreshTags = newTags => {
        tags = newTags ?? [];
        renderTagPills(tagListEl, tags);
        showFeedback(feedbackEl, '', true);
        if (inputEl) {
            inputEl.value = '';
        }
    };

    const attachTags = async names => {
        if (!names.length) {
            return;
        }
        try {
            const payload = JSON.stringify({ tags: names });
            const headers = { 'Content-Type': 'application/json' };
            if (token) {
                headers['RequestVerificationToken'] = token;
            }
            const data = await fetchJson(`/User/Trip/${tripId}/Tags`, {
                method: 'POST',
                headers,
                body: payload
            });
            refreshTags(data?.tags);
        } catch (err) {
            showFeedback(feedbackEl, err.message || 'Unable to add tag.');
        }
    };

    const detachTag = async slug => {
        try {
            const headers = {};
            if (token) {
                headers['RequestVerificationToken'] = token;
            }
            const data = await fetchJson(`/User/Trip/${tripId}/Tags/${encodeURIComponent(slug)}`, {
                method: 'DELETE',
                headers
            });
            refreshTags(data?.tags);
        } catch (err) {
            showFeedback(feedbackEl, err.message || 'Unable to remove tag.');
        }
    };

    const handleAdd = () => {
        const value = inputEl?.value.trim();
        if (!value) {
            showFeedback(feedbackEl, 'Enter a tag name before adding.');
            return;
        }
        attachTags([value]);
    };

    const renderSuggestions = items => {
        currentSuggestions = items;
        if (!suggestionsEl) {
            return;
        }
        if (!items.length) {
            suggestionsEl.classList.add('d-none');
            suggestionsEl.innerHTML = '';
            return;
        }

        suggestionsEl.innerHTML = items.map(item => `
            <button type="button"
                    class="list-group-item list-group-item-action d-flex justify-content-between align-items-center"
                    data-suggest-slug="${item.slug}">
                <span>${item.name}</span>
                <span class="badge text-bg-light">${item.count}</span>
            </button>`).join('');
        suggestionsEl.classList.remove('d-none');
    };

    const fetchSuggestions = async query => {
        try {
            const resp = await fetch(`${suggestUrl}?q=${encodeURIComponent(query)}`);
            if (!resp.ok) {
                throw new Error(resp.statusText);
            }
            const data = await resp.json();
            renderSuggestions(Array.isArray(data) ? data.slice(0, 8) : []);
        } catch {
            renderSuggestions([]);
        }
    };

    editor.addEventListener('click', ev => {
        const removeBtn = ev.target.closest('[data-tag-chip] .btn-close');
        if (removeBtn) {
            ev.preventDefault();
            const slug = removeBtn.closest('[data-tag-chip]')?.dataset.tagSlug;
            if (slug) {
                detachTag(slug);
            }
            return;
        }

        const addTrigger = ev.target.closest('[data-tag-add]');
        if (addTrigger) {
            ev.preventDefault();
            handleAdd();
        }
    });

    if (inputEl) {
        inputEl.addEventListener('keydown', ev => {
            if (ev.key === 'Enter') {
                ev.preventDefault();
                if (currentSuggestions.length) {
                    attachTags([currentSuggestions[0].name]);
                } else {
                    handleAdd();
                }
            }
        });

        inputEl.addEventListener('input', () => {
            clearTimeout(debounceHandle);
            const term = inputEl.value.trim();
            if (term.length < 2) {
                renderSuggestions([]);
                return;
            }
            debounceHandle = setTimeout(() => fetchSuggestions(term), 200);
        });
    }

    if (suggestionsEl) {
        suggestionsEl.addEventListener('click', ev => {
            const item = ev.target.closest('[data-suggest-slug]');
            if (!item) {
                return;
            }
            ev.preventDefault();
            const slug = item.dataset.suggestSlug;
            const suggestion = currentSuggestions.find(s => s.slug === slug);
            if (suggestion) {
                attachTags([suggestion.name]);
            }
            renderSuggestions([]);
        });

        document.addEventListener('click', ev => {
            if (!suggestionsEl.contains(ev.target) && ev.target !== inputEl) {
                renderSuggestions([]);
            }
        });
    }
};
