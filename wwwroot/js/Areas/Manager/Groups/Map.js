import { addZoomLevelControl } from '../../../map-utils.js';
import {
  formatViewerAndSourceTimes,
  currentDateInputValue,
  currentMonthInputValue,
  currentYearInputValue,
  shiftMonth as shiftMonthValue,
  getViewerTimeZone,
} from '../../../util/datetime.js';

// Map tiles config (proxy URL + attribution) injected by layout.
const tilesConfig = window.wayfarerTileConfig || {};
const tilesUrl = tilesConfig.tilesUrl || `${window.location.origin}/Public/tiles/{z}/{x}/{y}.png`;
const tilesAttribution = tilesConfig.attribution || '&copy; OpenStreetMap contributors';

(() => {
  const viewerTimeZone = getViewerTimeZone();
  const getLocationSourceTimeZone = location => location?.timezone || location?.timeZoneId || location?.timeZone || null;
  const getLocationTimestampInfo = location => formatViewerAndSourceTimes({
    iso: location?.localTimestamp,
    sourceTimeZone: getLocationSourceTimeZone(location),
    viewerTimeZone,
  });
  const renderTimestampBlock = location => {
    const info = getLocationTimestampInfo(location);
    const recorded = info.source
      ? `<div>${info.source}</div>`
      : `<div class="fst-italic text-muted">Source timezone unavailable</div>`;
    const zone = getLocationSourceTimeZone(location);
    return {
      viewer: info.viewer,
      recorded: recorded + (zone && !info.source ? `<div class="small text-muted">${zone}</div>` : ''),
    };
  };

  const mapEl = document.getElementById('groupMap');
  const groupId = document.getElementById('groupId')?.value;
  if (!mapEl || !groupId) return;

  const map = L.map('groupMap').setView([0, 0], 2);
  L.tileLayer(tilesUrl, { maxZoom: 19, attribution: tilesAttribution }).addTo(map);
  if (map.attributionControl && typeof map.attributionControl.setPrefix === 'function') {
    map.attributionControl.setPrefix('&copy; <a href="https://wayfarer.stefk.me" title="Powered by Wayfarer, made by Stef" target="_blank">Wayfarer</a> | <a href="https://stefk.me" title="Check my blog" target="_blank">Stef K</a> | &copy; <a href="https://leafletjs.com/" target="_blank">Leaflet</a>');
  }
  try { addZoomLevelControl(map); } catch(e){}
  // Initialize default pickers to today/current if empty
  (function initDefaultPickers(){
    const datePicker = document.getElementById('datePicker');
    const monthPicker = document.getElementById('monthPicker');
    const yearPicker = document.getElementById('yearPicker');
    if (datePicker && !datePicker.value) datePicker.value = currentDateInputValue();
    if (monthPicker && !monthPicker.value) monthPicker.value = currentMonthInputValue();
    if (yearPicker && !yearPicker.value) yearPicker.value = currentYearInputValue();
  })();

  const latestMarkers = new Map();
  let restClusters = new Map(); // userId -> MarkerClusterGroup
  let groupSseSubscription = null;

  /**
   * Checks if we're currently viewing today's date in day view
   * @returns {boolean} True if viewing today
   */
  function isViewingToday() {
    const viewDay = document.getElementById('viewDay');
    if (!viewDay || !viewDay.checked) return false;

    const datePicker = document.getElementById('datePicker');
    if (!datePicker || !datePicker.value) return true; // Default is today

    const selectedDate = new Date(datePicker.value + 'T00:00:00');
    const today = new Date();
    today.setHours(0, 0, 0, 0);

    return selectedDate.getTime() === today.getTime();
  }

  /**
   * Updates visibility of the historical locations toggle based on current date selection
   */
  function updateHistoricalToggleVisibility() {
    const container = document.getElementById('historicalLocationsToggleContainer');
    if (!container) return;

    if (isViewingToday()) {
      container.style.display = '';
    } else {
      container.style.display = 'none';
    }
  }

  function selectedUsers() {
    const boxes = document.querySelectorAll('#userSidebar input.user-select:checked');
    return Array.from(boxes).map(b => ({ id: b.getAttribute('data-user-id'), username: b.getAttribute('data-username') }));
  }
  // Keep the "Select All" checkbox aligned with the current selection state.
  function updateSelectAllState() {
    const selectAll = document.getElementById('selectAllUsers');
    if (!selectAll) return;
    const boxes = Array.from(document.querySelectorAll('#userSidebar input.user-select'));
    const checkedCount = boxes.filter(cb => cb.checked).length;
    selectAll.checked = boxes.length > 0 && checkedCount === boxes.length;
    selectAll.indeterminate = checkedCount > 0 && checkedCount < boxes.length;
  }
  function idToInfoMap() {
    const m = new Map();
    document.querySelectorAll('#userSidebar input.user-select').forEach(b => m.set(b.getAttribute('data-user-id'), { username: b.getAttribute('data-username'), display: b.getAttribute('data-display') }));
    return m;
  }
  // Clear all selection-driven layers to reflect an empty selection.
  function clearSelectionLayers() {
    latestMarkers.forEach(marker => map.removeLayer(marker));
    latestMarkers.clear();
    restClusters.forEach(group => map.removeLayer(group));
    restClusters.clear();
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
  function buildTooltipHtml(info, loc){
    const u=(info.username||'') + (info.display? (' ('+info.display+')') : '');
    const timestamps = getLocationTimestampInfo(loc);
    const addr=loc.fullAddress||loc.address||loc.place||'';
    let recordedLine = timestamps.source || 'Recorded time unavailable';
    const zone = getLocationSourceTimeZone(loc);
    if (!timestamps.source && zone) {
      recordedLine = `${recordedLine} (${zone})`;
    }
    return `${u}<br/>${timestamps.viewer}<br/>Recorded: ${recordedLine}<br/>${addr}`;
  }
  function upsertLatestForUser(userId, loc) {
    const latlng=[loc.coordinates.latitude, loc.coordinates.longitude];
    const info=idToInfoMap().get(userId)||{ username:'', display:''};
    const base=colorFromString(info.username||'user');
    const live=isLiveLocation(loc);
    const icon=L.divIcon({ html: latestIconHtml(base, live), className:'', iconSize:[20,20], iconAnchor:[10,10] });
    const statusHtml = live ? ' <span style="color:#dc3545">Live Location</span>' : ' <span style="color:#198754">Latest Location</span>';
    const tooltip = buildTooltipHtml(info, loc) + statusHtml;
    if (latestMarkers.has(userId)) { const m=latestMarkers.get(userId); m.setLatLng(latlng); m.setIcon(icon); m.bindTooltip(tooltip, {direction:'top'}); m.setZIndexOffset(live?10000:9000); }
    else { const m=L.marker(latlng, { icon, zIndexOffset:(live?10000:9000) }).bindTooltip(tooltip, {direction:'top'}); m.on('click', ()=>{ try{ openLocationModal(loc);}catch(e){} }); m.addTo(map); latestMarkers.set(userId, m); }
  }
  /**
   * Loads latest locations for specified users and removes markers for deselected users.
   * @param {Array<string>} userIds - Optional array of user IDs to load. If not provided, uses currently selected users.
   * @param {boolean} skipMarkerCleanup - If true, skip removing markers for users not in the list (used for SSE single-user updates).
   */
  async function loadLatest(userIds, skipMarkerCleanup = false) {
    const url='/api/groups/' + groupId + '/locations/latest';
    const include = userIds && userIds.length ? userIds : selectedUsers().map(u=>u.id);
    // Avoid fetching all members when nothing is selected.
    if (!include.length) {
      clearSelectionLayers();
      return;
    }
    // Remove markers for users that are no longer selected (skip when updating a single user via SSE)
    if (!skipMarkerCleanup) {
      const includeSet = new Set(include);
      latestMarkers.forEach((marker, userId) => {
        if (!includeSet.has(userId)) {
          map.removeLayer(marker);
          latestMarkers.delete(userId);
        }
      });
    }
    const data = await postJson(url, { includeUserIds: include });
    (Array.isArray(data)?data:[]).forEach(loc=>{ const uid=loc.userId; if (uid) upsertLatestForUser(uid, loc); });
    // Only fit bounds when loading all users, not for single-user SSE updates
    if (!skipMarkerCleanup) {
      const latlngs=Array.from(latestMarkers.values()).map(m=>m.getLatLng()); if (latlngs.length) map.fitBounds(L.latLngBounds(latlngs), { padding:[20,20] });
    }
  }
  /**
   * Loads locations in current viewport with date filters applied
   */
  async function loadViewport() {
    const url='/api/groups/' + groupId + '/locations/query';
    const selectedIds = selectedUsers().map(u=>u.id);
    // Avoid fetching all members when nothing is selected.
    if (!selectedIds.length) {
      clearSelectionLayers();
      return;
    }
    const body=toBBox(); body.UserIds=selectedIds;
    // include optional date filters
    const dt = document.querySelector('input[name="viewType"]:checked');
    const y = document.getElementById('yearPicker');
    const m = document.getElementById('monthPicker');
    const d = document.getElementById('datePicker');
    if (dt) {
      const view = dt.value;
      if (view === 'day' && d && d.value) {
        try { const parts=d.value.split('-'); body.DateType='day'; body.Year=parseInt(parts[0],10); body.Month=parseInt(parts[1],10); body.Day=parseInt(parts[2],10);} catch{}
      } else if (view === 'month' && m && m.value) {
        try { const parts=m.value.split('-'); body.DateType='month'; body.Year=parseInt(parts[0],10); body.Month=parseInt(parts[1],10);} catch{}
      } else if (view === 'year' && y && y.value) {
        body.DateType='year'; body.Year=parseInt(y.value,10);
      }
    }

    // Check if we should skip historical locations
    const showHistoricalToggle = document.getElementById('showHistoricalLocations');
    const shouldSkipHistorical = isViewingToday() && showHistoricalToggle && !showHistoricalToggle.checked;

    const res=await postJson(url, body);
    // clear existing clusters
    restClusters.forEach(g => map.removeLayer(g));
    restClusters.clear();

    // If viewing today with toggle off, skip rendering historical locations
    if (shouldSkipHistorical) {
      return; // Only show latest markers, which are handled by loadLatest()
    }

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
      if (datePicker && !datePicker.value) { const t=new Date(); datePicker.valueAsDate=t; }
      if (datePicker) datePicker.style.display = '';
      if (monthPicker) monthPicker.style.display = 'none';
      if (yearPicker) yearPicker.style.display = 'none';
    } else {
      if (viewMonth) viewMonth.disabled = false;
      if (viewYear) viewYear.disabled = false;
    }
  }
  enforceMultiUserDayOnly();
  // Sync select-all state on initial load.
  updateSelectAllState();
  // Initialize historical toggle visibility
  updateHistoricalToggleVisibility();
  loadLatest().catch(()=>{});
  loadViewport().catch(()=>{});
  // move refresh
  let pending; map.on('moveend', ()=>{ clearTimeout(pending); pending=setTimeout(()=> loadViewport().catch(()=>{}), 200); });

  // Subscribe to consolidated group SSE stream (all group events: locations, membership, visibility)
  /**
   * Handles all SSE events for the group using the consolidated endpoint.
   * Event types: location, location-deleted, visibility-changed, member-joined, member-left, etc.
   */
  function subscribeToGroupSse() {
    if (groupSseSubscription) {
      groupSseSubscription.close();
    }
    try {
      const es = new EventSource('/api/sse/group/' + groupId);
      es.onmessage = (ev) => {
        try {
          const payload = JSON.parse(ev.data);
          if (!payload || !payload.type) return;

          // Handle location update events
          if (payload.type === 'location') {
            // Check if this user is currently selected
            const selected = selectedUsers();
            const isSelected = selected.some(u => u.id === payload.userId);
            if (isSelected) {
              loadLatest([payload.userId], true).catch(() => {});
              loadViewport().catch(() => {});
            }
          }
          // Handle location deletion events
          else if (payload.type === 'location-deleted') {
            handleLocationDeleted(payload.locationId, payload.userId);
          }
          // Handle visibility change events
          else if (payload.type === 'visibility-changed') {
            updateMemberVisibility(payload.userId, payload.disabled);
          }
          // Membership events (member-joined, member-left, etc.) could trigger UI updates
        } catch(e) {
          console.error('Error processing group SSE event:', e);
        }
      };
      es.onerror = () => {
        es.close();
        // Attempt to reconnect after a delay
        setTimeout(() => subscribeToGroupSse(), 5000);
      };
      groupSseSubscription = es;
    } catch(e) {
      console.error('Error subscribing to group updates:', e);
    }
  }

  /**
   * Updates the UI to show/dim a member based on their peer visibility setting and removes/reloads markers
   * @param {string} userId - The user ID whose visibility changed
   * @param {boolean} disabled - Whether the user's peer visibility is disabled
   */
  function updateMemberVisibility(userId, disabled) {
    const currentUserId = document.getElementById('currentUserId')?.value;

    // Find the member item in the UI
    const memberItem = document.querySelector(`#userSidebar .user-item [data-user-id="${userId}"]`)?.closest('.user-item');
    if (memberItem) {
      // Update visual dimming for all members (including self for consistency)
      if (disabled) {
        memberItem.classList.add('peer-visibility-disabled');
      } else {
        memberItem.classList.remove('peer-visibility-disabled');
      }
    }

    // Current user always sees their own marker (managers see all)
    if (userId === currentUserId) {
      console.log(`Updated visibility state for self (${userId}): disabled=${disabled}`);
      return;
    }

    // For other members, handle marker visibility
    if (memberItem) {
      if (disabled) {
        // Remove marker from map when visibility is disabled
        if (latestMarkers.has(userId)) {
          map.removeLayer(latestMarkers.get(userId));
          latestMarkers.delete(userId);
          console.log(`Removed marker for user ${userId} (visibility disabled)`);
        }
      } else {
        // Reload all selected users' locations to include this newly visible user
        const checkbox = memberItem.querySelector('input.user-select');
        if (checkbox && checkbox.checked) {
          loadLatest().then(() => {
            console.log(`Reloaded locations including user ${userId} (visibility enabled)`);
          }).catch(err => {
            console.error(`Failed to reload locations:`, err);
          });
        }
      }
    }
  }

  /**
   * Handles location-deleted SSE events by removing markers and reloading latest locations
   * @param {number} locationId - The deleted location ID
   * @param {string} userId - The user ID whose location was deleted
   */
  function handleLocationDeleted(locationId, userId) {
    // Check if the deleted location is the current latest marker for this user
    const latestMarker = latestMarkers.get(userId);
    if (latestMarker) {
      // Reload latest location for this user to get the new latest
      const checkbox = document.querySelector(`#userSidebar input.user-select[data-user-id="${userId}"]`);
      if (checkbox && checkbox.checked) {
        loadLatest([userId], true).then(() => {
          console.log(`Reloaded latest location for user ${userId} after deletion`);
        }).catch(err => {
          console.error(`Failed to reload latest location for user ${userId}:`, err);
        });
      }
    }

    // Remove from cluster groups if present
    restClusters.forEach((cluster) => {
      cluster.eachLayer(layer => {
        // Note: We can't easily identify markers by locationId in clusters
        // A full viewport reload handles this case
      });
    });

    // Reload viewport to refresh historical locations display
    loadViewport().catch(() => {});
    console.log(`Handled location deletion: locationId=${locationId}, userId=${userId}`);
  }

  subscribeToGroupSse();

  // sidebar toggles - consolidated event listeners with enforceMultiUserDayOnly
  document.getElementById('selectAllUsers')?.addEventListener('change', function(){
    const checked=this.checked;
    document.querySelectorAll('#userSidebar input.user-select').forEach(el=>{el.checked=checked;});
    updateSelectAllState();
    enforceMultiUserDayOnly();
    loadLatest().catch(()=>{});
    loadViewport().catch(()=>{});
  });
  document.querySelectorAll('#userSidebar input.user-select').forEach(cb=>{
    cb.addEventListener('change', ()=>{
      updateSelectAllState();
      enforceMultiUserDayOnly();
      loadLatest().catch(()=>{});
      loadViewport().catch(()=>{});
    });
  });

  // User search/filter functionality
  const userSearch = document.getElementById('userSearch');
  let searchActive = false;
  let savedSelectionState = new Map(); // Store selection state when search is active
  let originalLabelHTML = new Map(); // Store original label HTML before highlighting

  // Helper function to escape HTML special characters
  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }

  // Helper function to highlight search term in text
  function highlightText(text, searchTerm) {
    if (!searchTerm) return escapeHtml(text);
    const escapedText = escapeHtml(text);
    const escapedSearch = escapeHtml(searchTerm);
    const regex = new RegExp(`(${escapedSearch})`, 'gi');
    return escapedText.replace(regex, '<span class="search-highlight">$1</span>');
  }

  if (userSearch) {
    userSearch.addEventListener('input', () => {
      const q = userSearch.value.trim().toLowerCase();
      let visibleCount = 0;
      const wasSearchActive = searchActive;
      searchActive = q.length > 0;

      // If activating search, save current selection state and original HTML
      if (searchActive && !wasSearchActive) {
        savedSelectionState.clear();
        originalLabelHTML.clear();
        document.querySelectorAll('#userSidebar .user-item input.user-select').forEach(checkbox => {
          savedSelectionState.set(checkbox.getAttribute('data-user-id'), checkbox.checked);
          const label = checkbox.closest('label');
          if (label) {
            originalLabelHTML.set(checkbox.getAttribute('data-user-id'), label.innerHTML);
          }
        });
      }

      document.querySelectorAll('#userSidebar .user-item').forEach(li => {
        const text = (li.getAttribute('data-filter') || '').toLowerCase();
        const matches = !q || text.indexOf(q) !== -1;
        li.style.display = matches ? '' : 'none';

        // Highlight matched text in label
        const checkbox = li.querySelector('input.user-select');
        const label = checkbox?.closest('label');
        if (label && searchActive && matches) {
          const username = checkbox.getAttribute('data-username') || '';
          const displayName = checkbox.getAttribute('data-display') || '';
          const originalHTML = originalLabelHTML.get(checkbox.getAttribute('data-user-id'));
          if (originalHTML) {
            // Reconstruct label with highlighting
            const highlightedUsername = highlightText(username, q);
            const highlightedDisplay = highlightText(displayName, q);
            const colorChip = '<span class="user-color d-inline-block me-2" style="width:12px;height:12px;border-radius:50%;vertical-align:middle;"></span>';
            const checkboxHTML = checkbox.outerHTML;
            label.innerHTML = `${colorChip}${checkboxHTML}${highlightedUsername} (${highlightedDisplay})`;
            // Re-apply color to chip after HTML replacement
            const chip = label.querySelector('.user-color');
            const originalChip = li.querySelector('.user-color');
            if (chip && originalChip) {
              chip.style.backgroundColor = originalChip.style.backgroundColor || colorFromString(username);
            }
          }
        }

        // Uncheck hidden items so they don't appear on map
        if (checkbox && searchActive) {
          if (!matches) {
            checkbox.checked = false;
          }
        }

        if (matches) visibleCount++;
      });

      // If deactivating search, restore saved selection state and original HTML
      if (!searchActive && wasSearchActive) {
        document.querySelectorAll('#userSidebar .user-item input.user-select').forEach(checkbox => {
          const userId = checkbox.getAttribute('data-user-id');
          const savedState = savedSelectionState.get(userId);
          if (savedState !== undefined) {
            checkbox.checked = savedState;
          }
          // Restore original label HTML (removes highlighting)
          const label = checkbox.closest('label');
          const originalHTML = originalLabelHTML.get(userId);
          if (label && originalHTML) {
            label.innerHTML = originalHTML;
          }
        });
        savedSelectionState.clear();
        originalLabelHTML.clear();
      }

      // Reload map with filtered selection (latest + historical).
      if (searchActive || wasSearchActive) {
        loadLatest().catch(() => {});
        loadViewport().catch(() => {});
      }
      updateSelectAllState();

      console.log(`Search: "${q}" - ${visibleCount} members visible`);
    });
  }

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
  [viewDay2, viewMonth2, viewYear2].forEach(el => { if (el) el.addEventListener('change', ()=>{ updatePickerVisibility(); updateHistoricalToggleVisibility(); loadViewport().catch(()=>{}); }); });
  if (datePicker2) datePicker2.addEventListener('change', ()=> { updateHistoricalToggleVisibility(); loadViewport().catch(()=>{}); });
  if (monthPicker2) monthPicker2.addEventListener('change', ()=> { updateHistoricalToggleVisibility(); loadViewport().catch(()=>{}); });
  if (yearPicker2) yearPicker2.addEventListener('change', ()=> { updateHistoricalToggleVisibility(); loadViewport().catch(()=>{}); });
  const btnToday = document.getElementById('btnToday'); if (btnToday && datePicker2 && viewDay2) btnToday.addEventListener('click', ()=>{ datePicker2.value = currentDateInputValue(); viewDay2.checked=true; updatePickerVisibility(); updateHistoricalToggleVisibility(); loadViewport().catch(()=>{}); });
  function shiftDay(delta){
    if (!datePicker2) return;
    const base = datePicker2.value ? new Date(datePicker2.value) : new Date();
    base.setDate(base.getDate()+delta);
    datePicker2.value = currentDateInputValue(base);
    if (viewDay2) viewDay2.checked=true;
    updatePickerVisibility();
    updateHistoricalToggleVisibility();
    loadViewport().catch(()=>{});
  }
  function shiftMonth(delta){
    if (!monthPicker2) return;
    const base = monthPicker2.value || currentMonthInputValue();
    monthPicker2.value = shiftMonthValue(base, delta, viewerTimeZone);
    if (viewMonth2) viewMonth2.checked=true;
    updatePickerVisibility();
    updateHistoricalToggleVisibility();
    loadViewport().catch(()=>{});
  }
  function shiftYear(delta){
    if (!yearPicker2) return;
    const current = yearPicker2.value || currentYearInputValue();
    const y = parseInt(current,10)+delta;
    yearPicker2.value = String(y);
    if (viewYear2) viewYear2.checked=true;
    updatePickerVisibility();
    updateHistoricalToggleVisibility();
    loadViewport().catch(()=>{});
  }
  const btnPrevDay=document.getElementById('btnPrevDay'); if (btnPrevDay) btnPrevDay.addEventListener('click', ()=> shiftDay(-1));
  const btnNextDay=document.getElementById('btnNextDay'); if (btnNextDay) btnNextDay.addEventListener('click', ()=> shiftDay(1));
  const btnPrevMonth=document.getElementById('btnPrevMonth'); if (btnPrevMonth) btnPrevMonth.addEventListener('click', ()=> shiftMonth(-1));
  const btnNextMonth=document.getElementById('btnNextMonth'); if (btnNextMonth) btnNextMonth.addEventListener('click', ()=> shiftMonth(1));
  const btnPrevYear=document.getElementById('btnPrevYear'); if (btnPrevYear) btnPrevYear.addEventListener('click', ()=> shiftYear(-1));
  const btnNextYear=document.getElementById('btnNextYear'); if (btnNextYear) btnNextYear.addEventListener('click', ()=> shiftYear(1));
  updatePickerVisibility();

  // Historical locations toggle event listener
  const showHistoricalLocations = document.getElementById('showHistoricalLocations');
  if (showHistoricalLocations) {
    showHistoricalLocations.addEventListener('change', ()=> {
      loadViewport().catch(()=>{});
    });
  }

  // Date filter apply
  const applyBtn = document.getElementById('applyDateFilter'); if (applyBtn) { applyBtn.addEventListener('click', ()=>{ loadViewport().catch(()=>{}); }); }
  // Show All / Hide All buttons
  const showAllBtn = document.getElementById('showAllUsers'); if (showAllBtn) {
    showAllBtn.addEventListener('click', ()=>{
      document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = true);
      const all = document.getElementById('selectAllUsers'); if (all) all.checked = true;
      updateSelectAllState();
      enforceMultiUserDayOnly();
      loadLatest().catch(()=>{}); loadViewport().catch(()=>{});
    });
  }
  const hideAllBtn = document.getElementById('hideAllUsers'); if (hideAllBtn) {
    hideAllBtn.addEventListener('click', ()=>{
      document.querySelectorAll('#userSidebar input.user-select').forEach(el=> el.checked = false);
      const all = document.getElementById('selectAllUsers'); if (all) all.checked = false;
      updateSelectAllState();
      enforceMultiUserDayOnly();
      loadLatest().catch(()=>{}); loadViewport().catch(()=>{});
    });
  }
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
      updateSelectAllState();
      enforceMultiUserDayOnly();
      loadLatest().catch(()=>{}); loadViewport().catch(()=>{});
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
              if (wayfarer.showAlert) wayfarer.showAlert('success', 'Member removed.');
            } else {
              if (wayfarer.showAlert) wayfarer.showAlert('danger', (data && data.message) || 'Remove failed.');
            }
          });
      };
      if (wayfarer.showConfirmationModal) wayfarer.showConfirmationModal({ title: 'Remove Member', message: 'Remove this member from the group?', confirmText: 'Remove', onConfirm: doRemove });
      else if (confirm('Remove this member from the group?')) doRemove();
    });
  });

  // Enforce initial multi-user day-only state
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
    // In group map context, displayed locations are always the latest for each user
    const isLatest = !isLive;
    const badge = isLive ? '<span class=\"badge bg-danger float-end ms-2\">LIVE LOCATION</span>' : (isLatest ? '<span class=\"badge bg-success float-end ms-2\">LATEST LOCATION</span>' : '');
    const notes = location.notes || '';
    const hasNotes = !!notes && notes.length>0;
    const timestamps = renderTimestampBlock(location);
    const html = `<div class=\\\"container-fluid\\\">`
      + `<div class=\\\"row mb-2\\\"><div class=\\\"col-12\\\">${badge}</div></div>`
      + `<div class=\\\"row mb-2\\\">`
      + `<div class=\\\"col-6\\\"><strong>Datetime (your timezone):</strong><div>${timestamps.viewer}</div></div>`
      + `<div class=\\\"col-6\\\"><strong>Recorded local time:</strong>${timestamps.recorded}</div>`
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
