const geoapifyModes = new Set(['walk', 'bicycle', 'motorcycle', 'drive', 'bus']);

/** Returns the closed mapping control state for the selected adapter. */
export const mappingControlState = (adapterType, currentValue) => adapterType === '2'
  ? { kind: 'select', value: geoapifyModes.has(currentValue) ? currentValue : '' }
  : { kind: 'input', value: currentValue };

const options = [
  ['', 'Not mapped'], ['walk', 'Walk'], ['bicycle', 'Bicycle'],
  ['motorcycle', 'Motorcycle'], ['drive', 'Drive'], ['bus', 'Bus']
];

/** Replaces one mapping field without changing its stable posted identity. */
const replaceControl = (control, adapterType) => {
  const state = mappingControlState(adapterType, control.value);
  if (control.tagName.toLowerCase() === state.kind) {
    control.value = state.value;
    return control;
  }
  const replacement = document.createElement(state.kind);
  for (const attribute of control.attributes) replacement.setAttribute(attribute.name, attribute.value);
  replacement.dataset.routingMappingControl = '';
  if (state.kind === 'select') {
    replacement.classList.remove('form-control');
    replacement.classList.add('form-select');
    replacement.removeAttribute('placeholder');
    for (const [value, label] of options) replacement.add(new Option(label, value));
  } else {
    replacement.classList.remove('form-select');
    replacement.classList.add('form-control');
    replacement.placeholder = 'Exact OSRM profile, e.g. driving';
  }
  replacement.value = state.value;
  control.replaceWith(replacement);
  return replacement;
};

/** Activates immediate adapter-aware mapping controls on one administration form. */
export const initializeRoutingProviderMappings = (root = document) => {
  const adapter = root.querySelector('#AdapterType');
  if (!adapter) return;
  const update = () => root.querySelectorAll('[data-routing-mapping-control]')
    .forEach(control => replaceControl(control, adapter.value));
  adapter.addEventListener('change', update);
  update();
};

if (typeof document !== 'undefined') {
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => initializeRoutingProviderMappings());
  else initializeRoutingProviderMappings();
}
