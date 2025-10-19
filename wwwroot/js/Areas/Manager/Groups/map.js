(() => {
  const mapEl = document.getElementById('groupMap');
  const groupId = document.getElementById('groupId')?.value;
  if (!mapEl || !groupId) return;

  const map = L.map('groupMap').setView([0, 0], 2);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; OpenStreetMap contributors'
  }).addTo(map);

  const markers = new Map(); // userId -> marker

  function toBBox() {
    const b = map.getBounds();
    const sw = b.getSouthWest();
    const ne = b.getNorthEast();
    return {
      MinLng: sw.lng,
      MinLat: sw.lat,
      MaxLng: ne.lng,
      MaxLat: ne.lat,
      ZoomLevel: map.getZoom(),
      UserIds: []
    };
  }

  async function postJson(url, body) {
    const resp = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (!resp.ok) throw new Error('Request failed');
    return await resp.json();
  }

  function upsertMarker(loc) {
    // Note: API doesn’t include userId on dto; for now, just add markers without user binding.
    const key = loc.Id;
    if (markers.has(key)) {
      markers.get(key).setLatLng([loc.Coordinates.y, loc.Coordinates.x]);
    } else {
      const m = L.circleMarker([loc.Coordinates.y, loc.Coordinates.x], {
        radius: loc.IsLatestLocation ? 6 : 4,
        color: loc.IsLatestLocation ? '#007bff' : '#666',
        weight: 2
      }).bindPopup(`${new Date(loc.LocalTimestamp).toLocaleString()}<br/>${loc.Place || ''}`);
      m.addTo(map);
      markers.set(key, m);
    }
  }

  async function loadLatest() {
    const url = `/api/groups/${groupId}/locations/latest`;
    const data = await postJson(url, { includeUserIds: [] });
    (Array.isArray(data) ? data : []).forEach(upsertMarker);
    // fit bounds if we have points
    const latlngs = Array.from(markers.values()).map(m => m.getLatLng());
    if (latlngs.length) map.fitBounds(L.latLngBounds(latlngs), { padding: [20, 20] });
  }

  async function loadViewport() {
    const url = `/api/groups/${groupId}/locations/query`;
    const body = toBBox();
    const res = await postJson(url, body);
    (res.results || []).forEach(upsertMarker);
  }

  // initial
  loadLatest().catch(() => {});

  // on move
  let pending;
  map.on('moveend', () => {
    clearTimeout(pending);
    pending = setTimeout(() => loadViewport().catch(() => {}), 200);
  });

  // SSE subscribe, fallback to polling
  function subscribeSse() {
    try {
      const es = new EventSource(`/api/sse/stream/group/${groupId}`);
      es.onmessage = (ev) => {
        try {
          const payload = JSON.parse(ev.data);
          if (payload && payload.type === 'location') {
            upsertMarker(payload.location);
          }
        } catch (_) { }
      };
      es.onerror = () => { es.close(); startPolling(); };
    } catch (_) {
      startPolling();
    }
  }

  let pollTimer;
  function startPolling() {
    clearInterval(pollTimer);
    pollTimer = setInterval(() => loadLatest().catch(() => {}), 15000);
  }

  subscribeSse();
})();

