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
  function styleForLatest(loc, username) {
    const now=new Date(), localTs=new Date(loc.LocalTimestamp);
    const diffMin=Math.abs(now-localTs)/60000, isLive=diffMin <= (loc.LocationTimeThresholdMinutes||10);
    const base=colorFromString(username||'user');
    return { radius:isLive?7:6, color:base, fillColor:base, fillOpacity:isLive?0.9:0.6, weight:isLive?3:2 };
  }
  function upsertLatestForUser(userId, loc) {
    const latlng=[loc.Coordinates.y, loc.Coordinates.x];
    const info=idToInfoMap().get(userId)||{ username:'', display:''};
    const style=styleForLatest(loc, info.username);
    const label = info.username + (info.display ? (' (' + info.display + ')') : '');
    const popup= label + '<br/>' + new Date(loc.LocalTimestamp).toLocaleString() + '<br/>' + (loc.Place||'');
    if (latestMarkers.has(userId)) { const m=latestMarkers.get(userId); m.setLatLng(latlng); m.setStyle(style); m.setPopupContent(popup); }
    else { const m=L.circleMarker(latlng, style).bindPopup(popup); m.addTo(map); latestMarkers.set(userId, m); }
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
      if (m && m.value) body.Month = parseInt(m.value, 10);
      if (d && d.value) { try { body.Day = parseInt(d.value.split('-')[2], 10); } catch{} }
    }
    const res=await postJson(url, body);
    // clear existing clusters
    restClusters.forEach(g => map.removeLayer(g));
    restClusters.clear();
    // build clusters per user for consistent color coding
    (res.results||[]).forEach(loc=>{
      if (!loc.IsLatestLocation) {
        const uid = loc.UserId || '';
        const info = idToInfoMap().get(uid) || { username: uid, display: '' };
        const base = colorFromString(info.username || uid || 'user');
        let group = restClusters.get(uid);
        if (!group) {
          group = L.markerClusterGroup({ maxClusterRadius: 40, disableClusteringAtZoom: 17, spiderfyOnMaxZoom: true });
          map.addLayer(group);
          restClusters.set(uid, group);
        }
        const tlabel = (info.username || uid) + (info.display ? (' (' + info.display + ')') : '');
        const marker = L.marker([loc.Coordinates.y, loc.Coordinates.x], {
          icon: L.divIcon({html:'<div style="background:'+base+';width:8px;height:8px;border-radius:50%;border:1px solid #333"></div>', className:'rest-dot', iconSize:[10,10]})
        }).bindTooltip(tlabel, {direction:'top'});
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
    users.forEach(u=>{ try { const es=new EventSource('/api/sse/stream/location-update/' + encodeURIComponent(u.username)); es.onmessage=(ev)=>{ try { const payload=JSON.parse(ev.data); if (payload && payload.LocationId) { loadLatest([u.id]).catch(()=>{}); loadViewport().catch(()=>{}); } } catch(e){} }; es.onerror=()=>{ es.close(); }; subscriptions.set(u.username, es);} catch(e){} });
  }
  subscribeSseForUsers(selectedUsers());

  // sidebar toggles
  document.getElementById('selectAllUsers')?.addEventListener('change', function(){ const checked=this.checked; document.querySelectorAll('#userSidebar input.user-select').forEach(el=>{el.checked=checked;}); subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); });
  document.querySelectorAll('#userSidebar input.user-select').forEach(cb=>{ cb.addEventListener('change', ()=>{ subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); }); });

  // Show All / Hide All buttons
  const showAllBtn = document.getElementById('showAllUsers'); if (showAllBtn) {
    showAllBtn.addEventListener('click', ()=>{
      document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = true);
      const all = document.getElementById('selectAllUsers'); if (all) all.checked = true;
      subscribeSseForUsers(selectedUsers());
      loadLatest().catch(()=>{}); loadViewport().catch(()=>{});
    });
  }
  const hideAllBtn = document.getElementById('hideAllUsers'); if (hideAllBtn) {
    hideAllBtn.addEventListener('click', ()=>{
      document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = false);
      const all = document.getElementById('selectAllUsers'); if (all) all.checked = false;
      subscribeSseForUsers(selectedUsers());
      loadLatest().catch(()=>{}); loadViewport().catch(()=>{});
    });
  }

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
  // prev/next helpers
  function shiftDay(delta){ const d=new Date(datePicker2.value||new Date()); d.setDate(d.getDate()+delta); datePicker2.value = d.toISOString().slice(0,10); loadViewport().catch(()=>{}); }
  function shiftMonth(delta){ const m=(monthPicker2.value||new Date().toISOString().slice(0,7)); const dt=new Date(m+'-01T00:00:00Z'); dt.setUTCMonth(dt.getUTCMonth()+delta); monthPicker2.value = dt.toISOString().slice(0,7); loadViewport().catch(()=>{}); }
  function shiftYear(delta){ const y=parseInt(yearPicker2.value||String(new Date().getUTCFullYear()),10); yearPicker2.value=String(y+delta); loadViewport().catch(()=>{}); }
  document.getElementById('btnYesterday')?.addEventListener('click', ()=> shiftDay(-1));
  document.getElementById('btnToday')?.addEventListener('click', ()=> { const today=new Date(); datePicker2.value=today.toISOString().slice(0,10); loadViewport().catch(()=>{}); });
  document.getElementById('btnPrevDay')?.addEventListener('click', ()=> shiftDay(-1));
  document.getElementById('btnNextDay')?.addEventListener('click', ()=> shiftDay(1));
  document.getElementById('btnPrevMonth')?.addEventListener('click', ()=> shiftMonth(-1));
  document.getElementById('btnNextMonth')?.addEventListener('click', ()=> shiftMonth(1));
  document.getElementById('btnPrevYear')?.addEventListener('click', ()=> shiftYear(-1));
  document.getElementById('btnNextYear')?.addEventListener('click', ()=> shiftYear(1));
  updatePickerVisibility();

  // Color chips and Only buttons
  document.querySelectorAll('#userSidebar .user-item').forEach(li => {
    const cb = li.querySelector('input.user-select'); const chip = li.querySelector('.user-color');
    if (cb && chip) { const color = colorFromString(cb.getAttribute('data-username')||'user'); chip.style.backgroundColor = color; }
  });
  document.querySelectorAll('#userSidebar .only-this').forEach(btn => {
    btn.addEventListener('click', function(){
      const targetId = this.getAttribute('data-user-id');
      document.querySelectorAll('#userSidebar input.user-select').forEach(el=>{ el.checked = (el.getAttribute('data-user-id') === targetId); });
      const all = document.getElementById('selectAllUsers'); if (all) all.checked = false;
      subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{});
    });
  });

  // Organisation peer visibility toggle (per-user)
  const groupType = (document.getElementById('groupType')?.value || '').toLowerCase();
  if (groupType === 'friends'){
    const toggle = document.getElementById('peerVisibilityToggle');
    // initialize from hidden field (disabled=false => checked)
    if (toggle){
      const disabledStr = document.getElementById('peerVisibilityDisabled')?.value || 'false';
      const isDisabled = disabledStr === 'true';
      toggle.checked = !isDisabled;
      toggle.addEventListener('change', async ()=>{
        try{
          const myId = document.getElementById('currentUserId')?.value;
          if (!myId) return;
          const url = `/api/groups/${groupId}/members/${encodeURIComponent(myId)}/org-peer-visibility-access`;
          const body = { disabled: !toggle.checked };
          const resp = await fetch(url, { method:'POST', headers:{ 'Content-Type':'application/json' }, body: JSON.stringify(body) });
          if (!resp.ok) toggle.checked = !toggle.checked; // revert
        } catch { toggle.checked = !toggle.checked; }
      });
    }
  }
})();
