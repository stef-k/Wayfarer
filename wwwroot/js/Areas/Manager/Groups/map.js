(() => {
  const mapEl = document.getElementById('groupMap');
  const groupId = document.getElementById('groupId')?.value;
  if (!mapEl || !groupId) return;

  const map = L.map('groupMap').setView([0, 0], 2);
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '&copy; OpenStreetMap contributors' }).addTo(map);
  if (map.attributionControl && typeof map.attributionControl.setPrefix === 'function') {
    map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
  }
  if (window.__addZoomLevelControl) { try { window.__addZoomLevelControl(map); } catch(e){} }

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
    const dt = document.querySelector('input[name="dateType"]:checked');
    const y = document.getElementById('filterYear');
    const m = document.getElementById('filterMonth');
    const d = document.getElementById('filterDay');
    if (dt && y && y.value) {
      body.DateType = dt.value;
      body.Year = parseInt(y.value, 10);
      if (m && m.value) body.Month = parseInt(m.value, 10);
      if (d && d.value) body.Day = parseInt(d.value, 10);
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
          group = L.markerClusterGroup({
            iconCreateFunction: function (cluster) {
              const count = cluster.getChildCount();
              return L.divIcon({
                html: '<div style="background:' + base + ';color:#fff;border:2px solid ' + base + ';border-radius:20px; padding:2px 6px;">' + count + '</div>',
                className: 'user-cluster-icon',
                iconSize: L.point(30, 30)
              });
            }
          });
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

  const userSearch=document.getElementById('userSearch'); if (userSearch) { userSearch.addEventListener('input', ()=>{ const q=userSearch.value.trim().toLowerCase(); document.querySelectorAll('#userSidebar .user-item').forEach(li=>{ const text=(li.getAttribute('data-filter')||'').toLowerCase(); li.style.display = !q || text.indexOf(q)!==-1 ? '' : 'none'; }); }); }
  // Date filter apply
  const applyBtn = document.getElementById('applyDateFilter'); if (applyBtn) { applyBtn.addEventListener('click', ()=>{ loadViewport().catch(()=>{}); }); }
  const showAllBtn = document.getElementById('showAllUsers'); if (showAllBtn) { showAllBtn.addEventListener('click', ()=>{ document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = true); const all = document.getElementById('selectAllUsers'); if (all) all.checked = true; subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); }); }
  const hideAllBtn = document.getElementById('hideAllUsers'); if (hideAllBtn) { hideAllBtn.addEventListener('click', ()=>{ document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = false); const all = document.getElementById('selectAllUsers'); if (all) all.checked = false; subscribeSseForUsers(selectedUsers()); loadLatest().catch(()=>{}); loadViewport().catch(()=>{}); }); }
  // Color chips
  document.querySelectorAll('#userSidebar .user-item').forEach(li => {
    const cb = li.querySelector('input.user-select'); const chip = li.querySelector('.user-color');
    if (cb && chip) { const color = colorFromString(cb.getAttribute('data-username')||'user'); chip.style.backgroundColor = color; }
  });

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
})();
