import { message } from '../localization/messages.js';
import { encodeFeatureText as encode } from './feature-metadata.js';

/** Both Location forms read authored options from the existing catalog and submit exact activity IDs. */
export const mountLocationActivitySelect = select => {
    if (!select || typeof TomSelect === 'undefined') return;
    new TomSelect(select, {
        valueField: 'id',
        labelField: 'name',
        searchField: ['name', 'description'],
        create: false,
        sortField: { field: 'name', direction: 'asc' },
        placeholder: message('Shell.LocationActivitySearch', 'Search for an activity…'),
        allowEmptyOption: true,
        preload: 'focus',
        render: {
            no_results: () => `<div class="no-results">${encode(message('Locations.ActivityNoResults',
                'No activities match the search.'))}</div>`
        },
        load: async (_query, callback) => {
            try {
                const response = await fetch(select.dataset.apiUrl || '/api/activity');
                if (!response.ok) throw new Error(`Activity catalog returned HTTP ${response.status}`);
                callback(await response.json());
            } catch (error) {
                console.error(error);
                callback();
                // Loading feedback is separate from protected save and recovery messages.
                const feedback = select.parentElement.querySelector('[data-activity-feedback]');
                if (feedback) feedback.textContent = message('Shell.LocationActivityLoadFailed', 'Could not load activities.');
            }
        }
    });
};
