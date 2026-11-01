/** Owner intent requires explicit recovery after a conflict or uncertain mutation; no replay. */
export class PhotoIntent {
  state = null;
  refreshRequired = false;
  adopt(state) { this.state = state; this.refreshRequired = false; }
  failed(code) { if (code === 'photo_changed' || code === 'photo_outcome_unknown') this.refreshRequired = true; }
  get canSubmit() { return Boolean(this.state?.stateToken) && !this.refreshRequired; }
}

/** Integer coordinates use the orientation-aware bitmap; server decoding remains authoritative. */
export const photoCrop = (width, height, zoom = 1, panX = 50, panY = 50) => {
  const cropSize = Math.max(1, Math.floor(Math.min(width, height) / zoom));
  return { cropX: Math.floor((width - cropSize) * panX / 100), cropY: Math.floor((height - cropSize) * panY / 100), cropSize };
};

/** Wire the independent editor without submitting or changing the public-consent form. */
const bindEditor = root => {
  const get = id => root.querySelector(`#photo-${id}`);
  const intent = new PhotoIntent();
  const status = get('status');
  let file = null;
  let bitmap = null;
  let busy = false;
  const current = root.querySelector('#current-profile-photo');
  const controls = () => {
    get('save').disabled = busy || !file || !intent.canSubmit;
    get('remove').disabled = busy || !intent.canSubmit;
    get('refresh').hidden = !intent.refreshRequired;
    get('refresh').disabled = busy;
    get('file').disabled = busy;
  };
  const adopt = state => {
    intent.adopt(state);
    current.hidden = !state.profilePhoto;
    if (state.profilePhoto) current.src = state.profilePhoto.url;
    else current.removeAttribute('src');
    get('consent').textContent = state.showProfilePhotoOnPublicTrips
      ? 'Saved public-Trip consent is ON. This photo and future replacements can appear on your public Trips.'
      : 'Saved public-Trip consent is OFF. Your photo is private to you and eligible Group members.';
    controls();
  };
  const crop = () => photoCrop(bitmap.width, bitmap.height, +get('zoom').value, +get('pan-x').value, +get('pan-y').value);
  const preview = () => {
    if (!bitmap) return;
    const { cropX, cropY, cropSize } = crop();
    const context = get('preview').getContext('2d');
    context.clearRect(0, 0, 256, 256);
    context.drawImage(bitmap, cropX, cropY, cropSize, cropSize, 0, 0, 256, 256);
  };
  const reset = () => {
    get('zoom').value = '1'; get('pan-x').value = '50'; get('pan-y').value = '50'; preview();
  };
  const cancel = () => {
    bitmap?.close(); bitmap = null; file = null;
    get('file').value = ''; get('framing').hidden = true; controls();
  };
  const errors = {
    photo_changed: 'The photo state changed. Choose Refresh photo state, check saved public consent, then choose Save or Remove again.',
    photo_outcome_unknown: 'The result could not be confirmed. Choose Refresh photo state before a separate Save or Remove.',
    unsupported_photo_format: 'Select or export JPEG, PNG or static WebP. HEIC/HEIF and animation are not supported.',
    photo_too_large: 'Choose a photo within 15 MiB, 40 megapixels and 16,384 pixels per side.',
    invalid_photo: 'This photo is malformed, incomplete or animated. Select another photo.',
    invalid_crop: 'The crop is outside the decoded photo. Adjust or reset the crop.',
    photo_upload_timeout: 'The upload timed out. Your current photo remains available. Save again when ready.'
  };
  const request = async (method, body) => {
    busy = true; controls(); status.textContent = method === 'GET' ? 'Refreshing photo state…' : 'Saving photo…';
    try {
      const response = await fetch('/api/profile-photo/me', { method, body,
        headers: { RequestVerificationToken: document.querySelector('#profile-form input[name="__RequestVerificationToken"]').value,
          ...(method === 'DELETE' ? { 'Content-Type': 'application/json' } : {}) } });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        if (method === 'GET') intent.failed('photo_outcome_unknown');
        intent.failed(data.code);
        status.textContent = method === 'GET' ? 'Photo state is unavailable. Choose Refresh photo state.' : errors[data.code] || 'The photo could not be saved. Your current photo is retained unless the result is uncertain.';
        return;
      }
      if (!data.stateToken) throw new Error('Unconfirmed photo state');
      adopt(data);
      status.textContent = method === 'GET' ? 'Photo state refreshed. Check saved consent, then choose Save or Remove.' : method === 'DELETE' ? 'Photo removed.' : 'Photo saved.';
      if (method !== 'GET') cancel();
    } catch {
      intent.failed('photo_outcome_unknown');
      status.textContent = 'The result could not be confirmed. Refresh photo state before a separate Save or Remove.';
    } finally { busy = false; controls(); }
  };
  get('file').addEventListener('change', async event => {
    const selected = event.target.files[0];
    if (!selected) return;
    if (selected.size > 15 * 1024 * 1024) { status.textContent = errors.photo_too_large; return; }
    try {
      const decoded = await createImageBitmap(selected, { imageOrientation: 'from-image' });
      bitmap?.close(); bitmap = decoded; file = selected;
      get('framing').hidden = false; reset(); controls(); status.textContent = 'Adjust the crop or save the centered preview.';
    } catch { status.textContent = errors.unsupported_photo_format; }
  });
  for (const id of ['zoom', 'pan-x', 'pan-y']) get(id).addEventListener('input', preview);
  get('reset').addEventListener('click', reset);
  get('cancel').addEventListener('click', cancel);
  get('refresh').addEventListener('click', () => request('GET'));
  get('remove').addEventListener('click', () => { if (intent.canSubmit) request('DELETE', JSON.stringify({ expectedStateToken: intent.state.stateToken })); });
  get('save').addEventListener('click', () => {
    if (!file || !intent.canSubmit) return;
    const body = new FormData(); body.append('photo', file); body.append('expectedStateToken', intent.state.stateToken);
    for (const [key, value] of Object.entries(crop())) body.append(key, value.toString());
    request('POST', body);
  });
  // Pointer dragging supplements the labelled keyboard-operable sliders.
  let pointer = null;
  get('preview').addEventListener('pointerdown', event => { pointer = [event.clientX, event.clientY]; get('preview').setPointerCapture(event.pointerId); });
  get('preview').addEventListener('pointermove', event => {
    if (!pointer) return;
    for (const [id, delta] of [['pan-x', event.clientX - pointer[0]], ['pan-y', event.clientY - pointer[1]]])
      get(id).value = Math.max(0, Math.min(100, +get(id).value - delta / 2));
    pointer = [event.clientX, event.clientY]; preview();
  });
  get('preview').addEventListener('pointerup', () => { pointer = null; });
  get('preview').addEventListener('pointercancel', () => { pointer = null; });
  window.addEventListener('pagehide', () => bitmap?.close(), { once: true });
  request('GET');
};

if (typeof document !== 'undefined') {
  const root = document.getElementById('profile-photo-editor');
  if (root) bindEditor(root);
}
