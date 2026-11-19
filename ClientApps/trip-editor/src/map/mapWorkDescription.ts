import { message } from '../../../../wwwroot/js/localization/messages.js';

/** Projects the authoritative map-work owner into the map landmark's instruction. */
export const describeMapWork = (kind?: string): string => {
  switch (kind) {
    case 'place':
      return message('Editor.MapPickPlace', 'Select the Place location. Click the map or drag the marker; Done updates the draft.');
    case 'area':
      return message('Editor.MapDrawArea', 'Edit the Area geometry. Click the map to place polygon vertices; Done updates the draft.');
    case 'segment':
      return message('Editor.MapDrawRoute', 'Edit the Segment route. Saved Place anchors are fixed; add, move, or remove other route points; Done updates the draft.');
    default:
      return message('Editor.MapDescription', 'View and navigate trip geography.');
  }
};
