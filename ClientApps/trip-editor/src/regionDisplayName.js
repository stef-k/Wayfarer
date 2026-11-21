import { message } from '../../../wwwroot/js/localization/messages.js';

/** Localizes the canonical grouping and leaves a missing Region without a label. */
export const regionDisplayName = region =>
  region?.isShadow ? message('Shell.UnassignedPlaces', 'Unassigned Places') : region?.name;
