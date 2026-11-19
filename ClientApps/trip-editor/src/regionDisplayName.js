import { message } from '../../../wwwroot/js/localization/messages.js';

/** Localizes the canonical grouping while preserving names and stored identity. */
export const regionDisplayName = region =>
  region.isShadow ? message('Shell.UnassignedPlaces', 'Unassigned Places') : region.name;
