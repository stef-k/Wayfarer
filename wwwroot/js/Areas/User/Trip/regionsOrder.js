// regionsOrder.js – enables drag sorting for regions & places
export const initOrdering = () => {
    // Regions accordion
    const regionContainer = document.getElementById('regions-accordion');
    if (regionContainer) enableSortable(regionContainer, 'region');

    // Each region’s place list (called again when region DOM is reloaded)
    document.querySelectorAll('[data-region-places]').forEach(ul =>
        enableSortable(ul, 'place')
    );

    // Segments list
    enableSortable(document.getElementById('segments-list'), 'segment');
};

/**
 * Makes a UL / accordion sortable and persists the new order.
 *
 * @param {HTMLElement|null} el   The container element (UL / .accordion / #segments-list …).
 * @param {'region'|'place'|'segment'} type  Entity kind – drives dataset key and API url.
 *
 * Depends on:
 *   • SortableJS (global or ESM)
 *   • saveOrder(type, [{id, order}]) – see below
 *   • showAlert(level, msg)         – already available in place/segment handlers.
 */
// regionsOrder.js  (only helper you need)
export const enableSortable = (el, type) => {
    if (!el) return;
    const guidRegex = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

    new Sortable(el, {
        handle: '.drag-handle',
        animation: 150,
        ghostClass: 'sortable-ghost',
        chosenClass: 'sortable-chosen',

        onEnd: async () => {
            const ordered = [...el.children]
                .map((li, idx) => ({
                    Id:    li.dataset[`${type}Id`]?.trim(), // keep camel-case
                    Order: idx
                }))
                .filter(o => guidRegex.test(o.Id));     // <- use o.id

            if (ordered.length === 0) return;         // nothing valid to persist

            try {
                await saveOrder(type, ordered);         // still JSON.stringify(ordered)
            } catch (err) {
                console.error('Failed to save order', err);
                showAlert?.('danger', `Could not save ${type} order – reverted.`);
            }
        }
    });

};

const saveOrder = async (type, ordered) => {
    try {
        const resp = await fetch(`/User/${capitalize(type)}s/Reorder`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken':
                document.querySelector('input[name="__RequestVerificationToken"]').value
            },
            body: JSON.stringify(ordered)
        });
        if (!resp.ok) throw new Error(await resp.text());
    } catch (err) {
        console.error('Order save failed', err);
        showAlert('danger', `Failed to save ${type} order`);
    }
};

const capitalize = s => s.charAt(0).toUpperCase() + s.slice(1);
