import { formatDateTime, getViewerTimeZone } from '../../../util/datetime.js';

(() => {
  const viewerTimeZone = getViewerTimeZone();
  document.querySelectorAll('.js-invite-created').forEach(el => {
    const iso = el?.getAttribute('data-created-at');
    if (!iso) return;
    const formatted = formatDateTime({ iso, displayTimeZone: viewerTimeZone, includeOffset: true });
    if (formatted) el.textContent = formatted;
  });

  const form = document.getElementById('inviteForm');
  const searchBtn = document.getElementById('userSearchBtn');
  const searchInput = document.getElementById('userSearch');
  const results = document.getElementById('userResults');

  async function searchUsers(q) {
    if (!q || q.trim().length < 2) { if (results) results.innerHTML = ''; return; }
    try {
      const spinner = document.getElementById('userSearchSpinner');
      spinner?.classList.remove('d-none');
      searchBtn?.setAttribute('disabled','disabled');
      searchInput?.setAttribute('disabled','disabled');
      const gid = form?.querySelector('input[name="groupId"]').value || '';
      const url = '/api/users/search?query=' + encodeURIComponent(q) + (gid ? ('&groupId=' + encodeURIComponent(gid)) : '');
      const resp = await fetch(url);
      if (!resp.ok) return;
      const data = await resp.json();
      if (!results) return;
      results.innerHTML = '';
      data.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.id; const display = u.displayName ? (' ('+u.displayName+')') : '';
        opt.textContent = u.userName + display; results.appendChild(opt);
      });
      if (results.options.length > 0) {
        if (results.selectedIndex < 0) results.selectedIndex = 0;
        const hidden = form?.querySelector('#inviteeUserId'); if (hidden) hidden.value = results.value || '';
        results.classList.remove('is-invalid');
      }
    } finally { document.getElementById('userSearchSpinner')?.classList.add('d-none'); searchBtn?.removeAttribute('disabled'); searchInput?.removeAttribute('disabled'); }
  }

  searchBtn?.addEventListener('click', () => searchUsers(searchInput.value));
  if (searchInput && results) {
    const debounce = (fn, d) => { let t; return function(){ clearTimeout(t); t=setTimeout(()=>fn.apply(this, arguments), d); } };
    const debounced = debounce(()=> searchUsers(searchInput.value), 300);
    searchInput.addEventListener('input', debounced);
    searchInput.addEventListener('keydown', ev => { if (ev.key==='Enter'){ ev.preventDefault(); searchUsers(searchInput.value); } if (ev.key==='Escape'){ searchInput.value=''; results.innerHTML=''; }});
    results.addEventListener('change', () => { const hidden = form?.querySelector('#inviteeUserId'); if (hidden) hidden.value = results.value || ''; });
  }

  if (form) {
    form.addEventListener('submit', function(e){
      const uid = form.querySelector('#inviteeUserId');
      if (uid && (!uid.value || !uid.value.trim()) && results && results.value) uid.value = results.value;
      if (!uid || !uid.value || !uid.value.trim()) { e.preventDefault(); results?.classList.add('is-invalid'); if (wayfarer.showAlert) wayfarer.showAlert('danger','Please select a user to invite.'); return; }
      e.preventDefault();
      const fd = new FormData(form);
      const tokenEl = form.querySelector('input[name="__RequestVerificationToken"]');
      const url = form.action.replace(/Invite$/, 'InviteAjax');
      fetch(url, { method: 'POST', body: fd, headers: tokenEl ? { 'RequestVerificationToken': tokenEl.value } : {} })
        .then(r => r.json())
        .then(data => { if (data?.success){ if (wayfarer.showAlert) wayfarer.showAlert('success','Invitation sent'); addInviteRow(data.invite?.id, results); } else { if (wayfarer.showAlert) wayfarer.showAlert('danger', data?.message || 'Failed'); } });
    });
  }

  function addInviteRow(invitationId, resultsSelect){
    const tbody = document.querySelector('#invitesTable tbody'); if (!tbody) return;
    const row = document.createElement('tr'); row.setAttribute('data-invite-id', invitationId);
    const selectedText = resultsSelect?.selectedOptions?.length ? resultsSelect.selectedOptions[0].textContent : '';
    const groupId = document.querySelector('#inviteForm input[name="groupId"]').value;
    const tokenVal = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const revokeAction = '/User/Groups/RevokeInvite';
    const createdIso = new Date().toISOString();
    const createdText = formatDateTime({ iso: createdIso, displayTimeZone: viewerTimeZone, includeOffset: true });
    row.innerHTML = `
      <td>${selectedText || ''}</td>
      <td>
        <time class="js-invite-created" data-created-at="${createdIso}">
          ${createdText}
        </time>
      </td>
      <td>
        <form class="d-inline js-confirm js-ajax" method="post" action="${revokeAction}" data-confirm-title="Cancel Invitation" data-confirm-message="Cancel this pending invite?">
          <input type="hidden" name="groupId" value="${groupId}" />
          <input type="hidden" name="inviteId" value="${invitationId}" />
          ${tokenVal ? (`<input type="hidden" name="__RequestVerificationToken" value="${tokenVal}" />`) : ''}
          <button type="submit" class="btn btn-sm btn-outline-secondary">Cancel</button>
        </form>
      </td>
    `;
    tbody.appendChild(row);
    attachConfirmHandler(row.querySelector('form.js-confirm'));
  }

  function attachConfirmHandler(f){ if (!f) return; f.addEventListener('submit', function(e){ e.preventDefault(); const title=f.dataset.confirmTitle||'Confirm', message=f.dataset.confirmMessage||'Confirm?'; if (wayfarer.showConfirmationModal){ wayfarer.showConfirmationModal({ title, message, confirmText:'Continue', onConfirm: ()=> submitAjax(f)});} else { if (confirm(message)) submitAjax(f); } }); }
  function submitAjax(f){ const fd=new FormData(f); fetch(f.action.replace(/(Invite|RemoveMember|RevokeInvite)$/, '$1Ajax'), { method:'POST', body: fd }).then(r => r.json()).then(data => { if (data?.success){ if (f.action.endsWith('RemoveMember')) removeRosterRow(fd.get('userId')); if (f.action.endsWith('RevokeInvite')) removeInviteRow(fd.get('inviteId')); } else { if (wayfarer.showAlert) wayfarer.showAlert('danger', data?.message || 'Failed'); } }); }
  async function addRosterRow(userId){
    const tbody = document.querySelector('#rosterTable tbody'); if (!tbody) return;
    if (document.querySelector('tr[data-user-id="' + userId + '"]')) return;
    try {
      const resp = await fetch('/api/users/' + encodeURIComponent(userId) + '/basic');
      if (!resp.ok) throw new Error('basic user fetch failed');
      const u = await resp.json();
      const tr = document.createElement('tr');
      tr.setAttribute('data-user-id', userId);
      const tokenVal = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
      const groupId = document.querySelector('#inviteForm input[name="groupId"]').value;
      const removeAction = '/User/Groups/RemoveMember';
      tr.innerHTML = '<td>' + (u.userName || '') + '</td>' +
                     '<td>' + (u.displayName || '') + '</td>' +
                     '<td>Member</td>' +
                     '<td>Active</td>' +
                     '<td>' +
                       '<form class="d-inline js-confirm js-ajax" method="post" action="' + removeAction + '" data-confirm-title="Remove Member" data-confirm-message="Are you sure you want to remove this member from the group?">' +
                         '<input type="hidden" name="groupId" value="' + groupId + '" />' +
                         '<input type="hidden" name="userId" value="' + userId + '" />' +
                         (tokenVal ? ('<input type="hidden" name="__RequestVerificationToken" value="' + tokenVal + '" />') : '') +
                         '<button type="submit" class="btn btn-sm btn-outline-danger">Remove</button>' +
                       '</form>' +
                     '</td>';
      tbody.appendChild(tr);
      attachConfirmHandler(tr.querySelector('form.js-confirm'));
    } catch (e) { setTimeout(function(){ window.location.reload(); }, 800); }
  }
  function removeRosterRow(userId){ const tr=document.querySelector('tr[data-user-id="'+userId+'"]'); if (tr) tr.remove(); }
  function removeInviteRow(inviteId){ const tr=document.querySelector('tr[data-invite-id="'+inviteId+'"]'); if (tr) tr.remove(); }

  // SSE live updates (consolidated group endpoint with type discriminator)
  document.addEventListener('DOMContentLoaded', function(){ try { const gid = document.querySelector('#inviteForm input[name="groupId"]').value; if (!gid || typeof EventSource==='undefined') return; const es=new EventSource('/api/sse/group/' + gid); es.onmessage=function(evt){ try { const d = evt && evt.data ? JSON.parse(evt.data) : null; if (!d||!d.type) return; if (d.type==='member-joined' && d.userId){ if (wayfarer.showAlert) wayfarer.showAlert('success','A user joined the group.'); addRosterRow(d.userId); if (d.invitationId) removeInviteRow(d.invitationId); } else if (d.type==='member-left' && d.userId){ if (wayfarer.showAlert) wayfarer.showAlert('warning','A user left the group.'); removeRosterRow(d.userId); } else if (d.type==='member-removed' && d.userId){ if (wayfarer.showAlert) wayfarer.showAlert('info','A user was removed from the group.'); removeRosterRow(d.userId); } else if (d.type==='invite-declined' && d.invitationId){ if (wayfarer.showAlert) wayfarer.showAlert('secondary','An invite was declined.'); removeInviteRow(d.invitationId); } else if (d.type==='invite-revoked' && d.inviteId){ removeInviteRow(d.inviteId); } } catch {} }; } catch {} });
})();
