(() => {
  const mapEl = document.getElementById('groupMap');
  const groupId = document.getElementById('groupId')?.value;
  if (!mapEl || !groupId) return;

  const map = L.map('groupMap').setView([0, 0], 2);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; OpenStreetMap contributors'
  }).addTo(map);

  const latestMarkers = new Map(); // userId -> marker (live or last)
  let restLayer = L.layerGroup().addTo(map); // non-latest viewport points
  let subscriptions = new Map(); // userName -> EventSource

  function selectedUsers() {
    const boxes = document.querySelectorAll('#userSidebar input.user-select:checked');
    return Array.from(boxes).map(b => ({ id: b.getAttribute('data-user-id'), username: b.getAttribute('data-username') }));
  }

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

  function styleForLatest(loc) {
    const now = new Date();
    const localTs = new Date(loc.LocalTimestamp);
    const diffMin = Math.abs(now - localTs) / 60000;
    const isLive = diffMin <= (loc.LocationTimeThresholdMinutes || 10);
    return {
      radius: isLive ? 7 : 6,
      color: isLive ? '#28a745' : '#007bff',
      weight: isLive ? 3 : 2
    };
  }

  function upsertLatestForUser(userId, loc) {
    const latlng = [loc.Coordinates.y, loc.Coordinates.x];
    const style = styleForLatest(loc);
    const popup = `${new Date(loc.LocalTimestamp).toLocaleString()}<br/>${loc.Place || ''}`;
    if (latestMarkers.has(userId)) {
      const m = latestMarkers.get(userId);
      m.setLatLng(latlng);
      m.setStyle(style);
      m.setPopupContent(popup);
    } else {
      const m = L.circleMarker(latlng, style).bindPopup(popup);
      m.addTo(map);
      latestMarkers.set(userId, m);
    }
  }

  async function loadLatest(userIds) {
    const url = `/api/groups/${groupId}/locations/latest`;
    const include = userIds && userIds.length ? userIds : selectedUsers().map(u => u.id);
    const data = await postJson(url, { includeUserIds: include });
    (Array.isArray(data) ? data : []).forEach((loc, idx) => {
      const uid = include[idx] || include[0]; // approximate mapping for batch; per-user refresh uses single id
      upsertLatestForUser(uid, loc);
    });
    // fit bounds if we have points
    const latlngs = Array.from(latestMarkers.values()).map(m => m.getLatLng());
    if (latlngs.length) map.fitBounds(L.latLngBounds(latlngs), { padding: [20, 20] });
  }

  async function loadViewport() {
    const url = `/api/groups/${groupId}/locations/query`;
    const body = toBBox();
    const sel = selectedUsers().map(u => u.id);
    body.UserIds = sel;
    const res = await postJson(url, body);
    // redraw rest points layer
    map.removeLayer(restLayer);
    restLayer = L.layerGroup();
    (res.results || []).forEach(function(loc){
      const isLatest = !!loc.IsLatestLocation;
      if (!isLatest) {
        const dot = L.circleMarker([loc.Coordinates.y, loc.Coordinates.x], { radius: 3, color: '#666', weight: 1 });
        restLayer.addLayer(dot);
      }
    });
    restLayer.addTo(map);
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
  function subscribeSseForUsers(users) {
    // close existing
    subscriptions.forEach(es => es.close());
    subscriptions.clear();
    users.forEach(function(u){
      try {
        const es = new EventSource(`/api/sse/stream/location-update/${encodeURIComponent(u.username)}`);
        es.onmessage = (ev) => {
          try {
            const payload = JSON.parse(ev.data);
            if (payload && payload.LocationId) {
              // refresh latest for this user
              loadLatest([u.id]).catch(() => {});
            }
          } catch (_) { }
        };
        es.onerror = () => { es.close(); };
        subscriptions.set(u.username, es);
      } catch (_) { /* ignore */ }
    });
  }

  let pollTimer;
  function startPolling() {
    clearInterval(pollTimer);
    pollTimer = setInterval(() => loadLatest().catch(() => {}), 15000);
  }

  subscribeSseForUsers(selectedUsers());

  // handle sidebar selection changes
  document.getElementById('selectAllUsers')?.addEventListener('change', function(){
    const checked = this.checked;
    document.querySelectorAll('#userSidebar input.user-select').forEach(el => { el.checked = checked; });
    subscribeSseForUsers(selectedUsers());
    loadLatest().catch(()=>{});
    loadViewport().catch(()=>{});
  });
  document.querySelectorAll('#userSidebar input.user-select').forEach(function(cb){
    cb.addEventListener('change', function(){
      subscribeSseForUsers(selectedUsers());
      loadLatest().catch(()=>{});
      loadViewport().catch(()=>{});
    });
  });
})();
