import { addZoomLevelControl } from '/js/map-utils.js';

(() => {
  const mapEl = document.getElementById('groupMap');
  const groupId = document.getElementById('groupId')?.value;
  if (!mapEl || !groupId) return;

  const map = L.map('groupMap').setView([0, 0], 2);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '&copy; OpenStreetMap contributors' }).addTo(map);
  if (map.attributionControl && typeof map.attributionControl.setPrefix === 'function') {
    map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
  }
  try { addZoomLevelControl(map); } catch(e){}

  const latestMarkers = new Map();
  let restClusters = new Map(); // userId -> MarkerClusterGroup
  let subscriptions = new Map();

  function selectedUsers() {
    const boxes = document.querySelectorAll('#userSidebar input.user-select:checked');
    return Array.from(boxes).map(b => ({ id: b.getAttribute('data-user-id'), username: b.getAttribute('data-username') }));
  }
  function idToUsernameMap() {
    const m = new Map();
    document.querySelectorAll('#userSidebar input.user-select').forEach(b => m.set(b.getAttribute('data-user-id'), b.getAttribute('data-username')));
    return m;
  }
  function idToInfoMap() {
    const m = new Map();
    document.querySelectorAll('#userSidebar input.user-select').forEach(b => m.set(b.getAttribute('data-user-id'), { username: b.getAttribute('data-username'), display: b.getAttribute('data-display') }));
    return m;
  }
  function toBBox() {
    const b = map.getBounds(); const sw = b.getSouthWest(), ne = b.getNorthEast();
    return { MinLng: sw.lng, MinLat: sw.lat, MaxLng: ne.lng, MaxLat: ne.lat, ZoomLevel: map.getZoom(), UserIds: [] };
  }
  async function postJson(url, body) {
    const resp = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!resp.ok) throw new Error('Request failed');
    return await resp.json();
  }
  function colorFromString(str) { let h=0; for (let i=0;i<str.length;i++) h=(h*31+str.charCodeAt(i))%360; return 'hsl(' + h + ',80%,45%)'; }
  function isLiveLocation(loc){ const now=new Date(), localTs=new Date(loc.localTimestamp); const diffMin=Math.abs(now-localTs)/60000; return diffMin <= (loc.locationTimeThresholdMinutes||10); }
  function latestIconHtml(baseColor, isLive){ const cls = isLive ? 'wf-marker wf-marker--live' : 'wf-marker wf-marker--latest'; return `<div class="${cls}" style="--wf-color:${baseColor}"></div>`; }
  function restIconHtml(baseColor){ return `<div class="wf-marker wf-marker--dot" style="--wf-color:${baseColor}"></div>`; }
  function buildTooltipHtml(info, loc){ const u=(info.username||'') + (info.display? (' ('+info.display+')') : ''); const dt=new Date(loc.localTimestamp).toLocaleString(); const addr=loc.fullAddress||loc.address||loc.place||''; return `${u}<br/>${dt}<br/>${addr}`; }
  function upsertLatestForUser(userId, loc) {
    const latlng=[loc.coordinates.latitude, loc.coordinates.longitude];
    const info=idToInfoMap().get(userId)||{ username:'', display:''};
    const base=colorFromString(info.username||'user');
    const live=isLiveLocation(loc);
    const icon=L.divIcon({ html: latestIconHtml(base, live), className:'', iconSize:[20,20], iconAnchor:[10,10] });
    const tooltip = buildTooltipHtml(info, loc);
    if (latestMarkers.has(userId)) { const m=latestMarkers.get(userId); m.setLatLng(latlng); m.setIcon(icon); m.bindTooltip(tooltip, {direction:'top'}); }
    else { const m=L.marker(latlng, { icon }).bindTooltip(tooltip, {direction:'top'}); m.on('click', ()=>{ try{ openLocationModal(loc);}catch(e){} }); m.addTo(map); latestMarkers.set(userId, m); }
  }
  async function loadLatest(userIds) {
    const url='/api/groups/' + groupId + '/locations/latest';
    const include = userIds && userIds.length ? userIds : selectedUsers().map(u=>u.id);
    const data = await postJson(url, { includeUserIds: include });
    (Array.isArray(data)?data:[]).forEach((loc,idx)=>{ const uid=include[idx]; if (uid) upsertLatestForUser(uid, loc); });
    const latlngs=Array.from(latestMarkers.values()).map(m=>m.getLatLng()); if (latlngs.length) map.fitBounds(L.latLngBounds(latlngs), { padding:[20,20] });
  }
  async function loadViewport() {
    const url='/api/groups/' + groupId + '/locations/query';
    const body=toBBox(); body.UserIds=selectedUsers().map(u=>u.id);
    // include optional date filters
    const dt = document.querySelector('input[name="viewType"]:checked');
    const y = document.getElementById('yearPicker');
    const m = document.getElementById('monthPicker');
    const d = document.getElementById('datePicker');
    if (dt && y && y.value) {
      body.DateType = dt.value;
      body.Year = parseInt(y.value, 10);
      if (m && m.value) { try { body.Month = parseInt(m.value.split('-')[1], 10); } catch{} }
      if (d && d.value) { try { body.Day = parseInt(d.value.split('-')[2], 10); } catch{} }
    }
    const res=await postJson(url, body);
    // clear existing clusters
    restClusters.forEach(g => map.removeLayer(g));
    restClusters.clear();
    // build clusters per user for consistent color coding
    (res.results||[]).forEach(loc=>{
      if (!loc.isLatestLocation) {
        const uid = loc.userId || '';
        const info = idToInfoMap().get(uid) || { username: uid, display: '' };
        const base = colorFromString(info.username || uid || 'user');
        let group = restClusters.get(uid);
        if (!group) {
          group = L.markerClusterGroup({ maxClusterRadius: 40, disableClusteringAtZoom: 17, spiderfyOnMaxZoom: true, chunkedLoading: true });
          map.addLayer(group);
          restClusters.set(uid, group);
        }
        const tooltip = buildTooltipHtml(info, loc);
        const marker = L.marker([loc.coordinates.latitude, loc.coordinates.longitude], {
          icon: L.divIcon({ html: restIconHtml(base), className:'', iconSize:[20,20], iconAnchor:[10,10] })
        }).bindTooltip(tooltip, {direction:'top'});
        marker.on('click', ()=>{ try{ openLocationModal(loc);}catch(e){} });
        group.addLayer(marker);
      }
    });
  }

  // initial
  loadLatest().catch(()=>{});
  // move refresh
  let pending; map.on('moveend', ()=>{ clearTimeout(pending); pending=setTimeout(()=> loadViewport().catch(()=>{}), 200); });

  // SSE
  function subscribeSseForUsers(users){
    subscriptions.forEach(es=>es.close()); subscriptions.clear();
    users.forEach(u=>{ try { const es=new EventSource('/api/sse/stream/location-update/' + encodeURIComponent(u.username)); es.onmessage=(ev)=>{ try { const payload=JSON.parse(ev.data); if (payload && (payload.locationId || payload.LocationId)) { loadLatest([u.id]).catch(()=>{}); loadViewport().catch(()=>{}); } } catch(e){} }; es.onerror=()=>{ es.close(); }; subscriptions.set(u.username, es);} catch(e){} });
  }
  subscribeSseForUsers(selectedUsers());

  // sidebar toggles
  document.getElementById('selectAllUsers')?.addEventListener('change', function(){ const checked=this.checked; document.querySelectorAll('#userSidebar input.user-select').forEach(el=>{el.checked=checked;}); subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); });
  document.querySelectorAll('#userSidebar input.user-select').forEach(cb=>{ cb.addEventListener('change', ()=>{ subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); }); });

  const userSearch=document.getElementById('userSearch'); if (userSearch) { userSearch.addEventListener('input', ()=>{ const q=userSearch.value.trim().toLowerCase(); document.querySelectorAll('#userSidebar .user-item').forEach(li=>{ const text=(li.getAttribute('data-filter')||'').toLowerCase(); li.style.display = !q || text.indexOf(q)!==-1 ? '' : 'none'; }); }); }

  // Chronological controls wiring
  const datePicker2 = document.getElementById('datePicker');
  const monthPicker2 = document.getElementById('monthPicker');
  const yearPicker2 = document.getElementById('yearPicker');
  const viewDay2 = document.getElementById('viewDay');
  const viewMonth2 = document.getElementById('viewMonth');
  const viewYear2 = document.getElementById('viewYear');
  function updatePickerVisibility(){
    if (viewDay2 && viewDay2.checked) { if (datePicker2) datePicker2.style.display=''; if (monthPicker2) monthPicker2.style.display='none'; if (yearPicker2) yearPicker2.style.display='none'; }
    else if (viewMonth2 && viewMonth2.checked) { if (datePicker2) datePicker2.style.display='none'; if (monthPicker2) monthPicker2.style.display=''; if (yearPicker2) yearPicker2.style.display='none'; }
    else if (viewYear2 && viewYear2.checked) { if (datePicker2) datePicker2.style.display='none'; if (monthPicker2) monthPicker2.style.display='none'; if (yearPicker2) yearPicker2.style.display=''; }
  }
  [viewDay2, viewMonth2, viewYear2].forEach(el => { if (el) el.addEventListener('change', ()=>{ updatePickerVisibility(); loadViewport().catch(()=>{}); }); });
  if (datePicker2) datePicker2.addEventListener('change', ()=> loadViewport().catch(()=>{}));
  if (monthPicker2) monthPicker2.addEventListener('change', ()=> loadViewport().catch(()=>{}));
  if (yearPicker2) yearPicker2.addEventListener('change', ()=> loadViewport().catch(()=>{}));
  const btnToday = document.getElementById('btnToday'); if (btnToday && datePicker2 && viewDay2) btnToday.addEventListener('click', ()=>{ const now = new Date(); datePicker2.valueAsDate = now; viewDay2.checked=true; updatePickerVisibility(); loadViewport().catch(()=>{}); });
  const btnYesterday = document.getElementById('btnYesterday'); if (btnYesterday && datePicker2 && viewDay2) btnYesterday.addEventListener('click', ()=>{ const d=new Date(); d.setDate(d.getDate()-1); datePicker2.valueAsDate=d; viewDay2.checked=true; updatePickerVisibility(); loadViewport().catch(()=>{}); });
  function shiftDay(delta){ if (!datePicker2) return; const d = datePicker2.value ? new Date(datePicker2.value) : new Date(); d.setDate(d.getDate()+delta); datePicker2.valueAsDate=d; if (viewDay2) viewDay2.checked=true; updatePickerVisibility(); loadViewport().catch(()=>{}); }
  function shiftMonth(delta){ if (!monthPicker2) return; let base = monthPicker2.value || new Date().toISOString().slice(0,7); let parts = base.split('-'); let y=parseInt(parts[0]||new Date().getFullYear(),10); let m=parseInt(parts[1]||1,10); m=m+delta; if (m<1){m=12;y--;} if (m>12){m=1;y++;} monthPicker2.value = y.toString().padStart(4,'0') + '-' + m.toString().padStart(2,'0'); if (viewMonth2) viewMonth2.checked=true; updatePickerVisibility(); loadViewport().catch(()=>{}); }
  function shiftYear(delta){ if (!yearPicker2) return; const y = parseInt(yearPicker2.value || (new Date().getFullYear()),10)+delta; yearPicker2.value = y; if (viewYear2) viewYear2.checked=true; updatePickerVisibility(); loadViewport().catch(()=>{}); }
  const btnPrevDay=document.getElementById('btnPrevDay'); if (btnPrevDay) btnPrevDay.addEventListener('click', ()=> shiftDay(-1));
  const btnNextDay=document.getElementById('btnNextDay'); if (btnNextDay) btnNextDay.addEventListener('click', ()=> shiftDay(1));
  const btnPrevMonth=document.getElementById('btnPrevMonth'); if (btnPrevMonth) btnPrevMonth.addEventListener('click', ()=> shiftMonth(-1));
  const btnNextMonth=document.getElementById('btnNextMonth'); if (btnNextMonth) btnNextMonth.addEventListener('click', ()=> shiftMonth(1));
  const btnPrevYear=document.getElementById('btnPrevYear'); if (btnPrevYear) btnPrevYear.addEventListener('click', ()=> shiftYear(-1));
  const btnNextYear=document.getElementById('btnNextYear'); if (btnNextYear) btnNextYear.addEventListener('click', ()=> shiftYear(1));
  updatePickerVisibility();

  // Date filter apply
  const applyBtn = document.getElementById('applyDateFilter'); if (applyBtn) { applyBtn.addEventListener('click', ()=>{ loadViewport().catch(()=>{}); }); }
  const showAllBtn = document.getElementById('showAllUsers'); if (showAllBtn) { showAllBtn.addEventListener('click', ()=>{ document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = true); const all = document.getElementById('selectAllUsers'); if (all) all.checked = true; subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); }); }
  const hideAllBtn = document.getElementById('hideAllUsers'); if (hideAllBtn) { hideAllBtn.addEventListener('click', ()=>{ document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = false); const all = document.getElementById('selectAllUsers'); if (all) all.checked = false; subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); }); }
  // Color chips
  document.querySelectorAll('#userSidebar .user-item').forEach(li => {
    const cb = li.querySelector('input.user-select'); const chip = li.querySelector('.user-color');
    if (cb && chip) { const color = colorFromString(cb.getAttribute('data-username')||'user'); chip.style.backgroundColor = color; }
  });

  // Disable remove buttons for owner and last manager (org)
  function updateRemoveButtons(){
    const groupType = (document.getElementById('groupType')?.value || '').toLowerCase();
    const isOrg = groupType === 'organization';
    // count managers
    let managerCount = 0;
    document.querySelectorAll('#userSidebar input.user-select').forEach(cb=>{
      const role = (cb.getAttribute('data-role')||'').toLowerCase();
      const statusManager = role === 'owner' || role === 'manager';
      if (statusManager) managerCount++;
    });
    document.querySelectorAll('#userSidebar .user-item').forEach(li=>{
      const cb = li.querySelector('input.user-select');
      const btn = li.querySelector('.remove-user');
      if (!cb || !btn) return;
      const isOwner = cb.getAttribute('data-owner') === 'True' || cb.getAttribute('data-owner') === 'true';
      const role = (cb.getAttribute('data-role')||'').toLowerCase();
      const isManagerRole = role === 'owner' || role === 'manager';
      let disabled = false;
      let title = '';
      if (isOwner) { disabled = true; title = 'Owner cannot be removed'; }
      else if (isOrg && isManagerRole && managerCount <= 1) { disabled = true; title = 'Cannot remove the last manager from an Organization group'; }
      btn.disabled = disabled; if (title) btn.title = title; else btn.removeAttribute('title');
    });
  }
  updateRemoveButtons();

  // Only this button handling
  document.querySelectorAll('#userSidebar .only-this').forEach(btn => {
    btn.addEventListener('click', function(){
      const targetId = this.getAttribute('data-user-id');
      document.querySelectorAll('#userSidebar input.user-select').forEach(el=>{ el.checked = (el.getAttribute('data-user-id') === targetId); });
      const all = document.getElementById('selectAllUsers'); if (all) all.checked = false;
      subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{});
    });
  });

  // Remove user inline (AJAX)
  document.querySelectorAll('#userSidebar .remove-user').forEach(btn => {
    btn.addEventListener('click', function(){
      const uid = this.getAttribute('data-user-id');
      const tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
      const doRemove = () => {
        const fd = new FormData(); fd.append('groupId', groupId); fd.append('userId', uid);
        fetch('/Manager/Groups/RemoveMemberAjax', { method: 'POST', body: fd, headers: tokenEl ? { 'RequestVerificationToken': tokenEl.value } : {} })
          .then(r=>r.json()).then(data => {
            if (data && data.success) {
              const item = this.closest('.user-item'); if (item) item.remove();
              if (latestMarkers.has(uid)) { map.removeLayer(latestMarkers.get(uid)); latestMarkers.delete(uid); }
              loadViewport().catch(()=>{});
              if (typeof showAlert === 'function') showAlert('success', 'Member removed.');
            } else {
              if (typeof showAlert === 'function') showAlert('danger', (data && data.message) || 'Remove failed.');
            }
          });
      };
      if (typeof showConfirmationModal === 'function') showConfirmationModal({ title: 'Remove Member', message: 'Remove this member from the group?', confirmText: 'Remove', onConfirm: doRemove });
      else if (confirm('Remove this member from the group?')) doRemove();
    });
  });

  // Enforce performance rule: if multiple users selected, force Day-only view
  function enforceMultiUserDayOnly(){
    const boxes = document.querySelectorAll('#userSidebar input.user-select:checked');
    const multi = boxes.length > 1;
    const viewDay = document.getElementById('viewDay');
    const viewMonth = document.getElementById('viewMonth');
    const viewYear = document.getElementById('viewYear');
    const datePicker = document.getElementById('datePicker');
    const monthPicker = document.getElementById('monthPicker');
    const yearPicker = document.getElementById('yearPicker');
    if (multi){
      if (viewDay) viewDay.checked = true;
      if (viewMonth) viewMonth.disabled = true;
      if (viewYear) viewYear.disabled = true;
      if (datePicker) datePicker.style.display = '';
      if (monthPicker) monthPicker.style.display = 'none';
      if (yearPicker) yearPicker.style.display = 'none';
      if (datePicker && !datePicker.value){ const today=new Date(); datePicker.value=today.toISOString().slice(0,10); }
    } else {
      if (viewMonth) viewMonth.disabled = false;
      if (viewYear) viewYear.disabled = false;
    }
  }
  document.getElementById('selectAllUsers')?.addEventListener('change', ()=>{ enforceMultiUserDayOnly(); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); });
  document.querySelectorAll('#userSidebar input.user-select').forEach(cb=> cb.addEventListener('change', ()=>{ enforceMultiUserDayOnly(); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); }));
  enforceMultiUserDayOnly();

  // Modal generator aligned with public timeline
  function googleMapsLink(location){
    const addr=location?.fullAddress||''; const lat=location?.coordinates?.latitude; const lon=location?.coordinates?.longitude;
    const has=Number.isFinite(+lat)&&Number.isFinite(+lon);
    const query = addr && has ? `${addr} (${(+lat).toFixed(6)},${(+lon).toFixed(6)})` : has ? `${(+lat).toFixed(6)},${(+lon).toFixed(6)}` : addr;
    const q=encodeURIComponent(query||'');
    return `<a href=\"https://www.google.com/maps/search/?api=1&query=${q}\" target=\"_blank\" class=\"ms-2 btn btn-outline-primary btn-sm\" title=\"View in Google Maps\"><i class=\"bi bi-globe-europe-africa\"></i> Maps</a>`;
  }
  function openLocationModal(location){
    const nowMin = Math.floor(Date.now()/60000);
    const locMin = Math.floor(new Date(location.localTimestamp).getTime()/60000);
    const isLive = (nowMin - locMin) <= (location.locationTimeThresholdMinutes||10);
    const isLatest = !!location.isLatestLocation;
    const badge = isLive ? '<span class=\"badge bg-danger float-end ms-2\">LIVE LOCATION</span>' : (isLatest ? '<span class=\"badge bg-success float-end ms-2\">LATEST LOCATION</span>' : '');
    const notes = location.notes || '';
    const hasNotes = !!notes && notes.length>0;
    const html = `<div class=\\\"container-fluid\\\">`
      + `<div class=\\\"row mb-2\\\"><div class=\\\"col-12\\\">${badge}</div></div>`
      + `<div class=\\\"row mb-2\\\">`
      + `<div class=\\\"col-6\\\"><strong>Local Datetime:</strong> <span>${new Date(location.localTimestamp).toISOString().replace('T',' ').split('.')[0]}</span></div>`
      + `<div class=\\\"col-6\\\"><strong>Timezone:</strong> <span>${location.timezone || location.timeZoneId || ''}</span></div>`
      + `</div>`
      + `<div class=\\\"row mb-2\\\">`
      + `<div class=\\\"col-12\\\"><strong>Address:</strong> <span>${location.fullAddress || location.address || location.place || '<i class=\\\"bi bi-patch-question\\\" title=\\\"No available data for Address\\\"></i>'}</span>${googleMapsLink(location)}</div>`
      + `</div>`
      + `<div class=\\\"row mb-2\\\">`
      + `<div class=\\\"col-6\\\"><strong>Latitude:</strong> <span class=\\\"fw-bold text-primary\\\">${location.coordinates.latitude}</span></div>`
      + `<div class=\\\"col-6\\\"><strong>Longitude:</strong> <span class=\\\"fw-bold text-primary\\\">${location.coordinates.longitude}</span></div>`
      + `</div>`
      + `<div class=\\\"row mb-2\\\" ${hasNotes? '' : 'style=\\\"display:none;\\\"'}>`
      + `<div class=\\\"col-12\\\"><strong>Notes:</strong><div class=\\\"border p-1\\\">${hasNotes? notes : ''}</div></div>`
      + `</div>`
      + `</div>`;
    const el = document.getElementById('modalContent'); if (el) el.innerHTML = html; new bootstrap.Modal(document.getElementById('locationModal')).show();
  }
})();
