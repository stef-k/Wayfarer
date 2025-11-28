// Bridge to expose ESM map-utils to non-module scripts
import { addZoomLevelControl } from '../../../map-utils.js';
window.addZoomLevelControl = addZoomLevelControl;

