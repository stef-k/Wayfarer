(() => {
  const form = document.getElementById('inviteForm');
  const searchBtn = document.getElementById('userSearchBtn');
  const searchInput = document.getElementById('userSearch');
  const results = document.getElementById('userResults');

  async function searchUsers(q) {
    if (!q || q.trim().length < 2) {
      if (results) results.innerHTML = '';
      return;
    }
    try {
      const spinner = document.getElementById('userSearchSpinner');
      if (spinner) spinner.classList.remove('d-none');
      if (searchBtn) searchBtn.setAttribute('disabled', 'disabled');
      if (searchInput) searchInput.setAttribute('disabled', 'disabled');
      const groupIdInput = form ? form.querySelector('input[name="groupId"]') : null;
      const gid = groupIdInput ? groupIdInput.value : '';
      const url = '/api/users/search?query=' + encodeURIComponent(q) + (gid ? ('&groupId=' + encodeURIComponent(gid)) : '');
      const resp = await fetch(url);
      if (!resp.ok) return;
      const data = await resp.json();
      if (!results) return;
      results.innerHTML = '';
      data.forEach(function(u) {
        const opt = document.createElement('option');
        opt.value = u.id;
        const display = u.displayName ? (' (' + u.displayName + ')') : '';
        opt.textContent = u.userName + display;
        results.appendChild(opt);
      });
      // auto-select when single result returned, sync hidden field
      if (results.options.length > 0) {
        if (results.selectedIndex < 0) results.selectedIndex = 0;
        const hidden = form ? form.querySelector('#inviteeUserId') : null;
        if (hidden) hidden.value = results.value || '';
        results.classList.remove('is-invalid');
      }
    } catch (e) {
      // no-op
    } finally {
      const spinner = document.getElementById('userSearchSpinner');
      if (spinner) spinner.classList.add('d-none');
      if (searchBtn) searchBtn.removeAttribute('disabled');
      if (searchInput) searchInput.removeAttribute('disabled');
    }
  }

  if (searchBtn && searchInput && results) {
    searchBtn.addEventListener('click', function() { searchUsers(searchInput.value); });
  }

  // Debounced live search when typing (min 2 chars)
  function debounce(fn, delay) {
    let t;
    return function() {
      const ctx = this, args = arguments;
      clearTimeout(t);
      t = setTimeout(function() { fn.apply(ctx, args); }, delay);
    };
  }

  if (searchInput && results) {
    const debounced = debounce(function() { searchUsers(searchInput.value); }, 300);
    searchInput.addEventListener('input', debounced);
    searchInput.addEventListener('keydown', function(ev){
      if (ev.key === 'Enter') { ev.preventDefault(); searchUsers(searchInput.value); }
      if (ev.key === 'Escape') { searchInput.value=''; results.innerHTML=''; }
    });
  }

  if (results && form) {
    results.addEventListener('change', function() {
      const hidden = form.querySelector('#inviteeUserId');
      if (hidden) hidden.value = results.value || '';
    });
  }

  if (form) {
    form.addEventListener('submit', function(e) {
      const uid = form.querySelector('#inviteeUserId');
      // ensure hidden sync with current selection
      if (uid && (!uid.value || !uid.value.trim()) && results && results.value) {
        uid.value = results.value;
      }
      if (!uid || !uid.value || !uid.value.trim()) {
        e.preventDefault();
        if (results) results.classList.add('is-invalid');
        if (typeof showAlert === 'function') showAlert('danger', 'Please select a user to invite.');
      } else {
        if (results) results.classList.remove('is-invalid');
        // AJAX submit invite with anti-forgery header
        e.preventDefault();
        const fd = new FormData(form);
        const tokenEl = form.querySelector('input[name="__RequestVerificationToken"]');
        const url = form.action.replace(/Invite$/, 'InviteAjax');
        fetch(url, {
          method: 'POST',
          body: fd,
          headers: tokenEl ? { 'RequestVerificationToken': tokenEl.value } : {}
        }).then(function(resp){ return resp.json(); })
          .then(function(data){
            if (data && data.success) {
              const invitesTable = document.getElementById('invitesTable').querySelector('tbody');
              if (invitesTable && data.invite) {
                const row = document.createElement('tr');
                row.setAttribute('data-invite-id', data.invite.id);
                var inviteeLabel = '';
                if (results && results.selectedOptions && results.selectedOptions.length) {
                  inviteeLabel = results.selectedOptions[0].textContent;
                } else {
                  inviteeLabel = (fd.get('inviteeUserId') || '');
                }
                const tokenEl = form.querySelector('input[name="__RequestVerificationToken"]');
                const tokenVal = tokenEl ? tokenEl.value : '';
                const revokeAction = form.action.replace(/InviteAjax$/, 'RevokeInviteAjax');
                row.innerHTML = '<td>' + inviteeLabel + '</td>' +
                                '<td>' + new Date().toLocaleString() + '</td>' +
                                '<td>' +
                                  '<form class="d-inline js-confirm js-ajax" method="post" action="' + revokeAction + '" data-confirm-title="Cancel Invitation" data-confirm-message="Cancel this pending invite?">' +
                                    '<input type="hidden" name="groupId" value="' + (fd.get('groupId') || '') + '" />' +
                                    '<input type="hidden" name="inviteId" value="' + data.invite.id + '" />' +
                                    (tokenVal ? ('<input type="hidden" name="__RequestVerificationToken" value="' + tokenVal + '" />') : '') +
                                    '<button type="submit" class="btn btn-sm btn-outline-secondary">Cancel</button>' +
                                  '</form>' +
                                '</td>';
                invitesTable.appendChild(row);
                // Attach confirm handler to the new form
                attachConfirmHandler(row.querySelector('form.js-confirm'));
              }
              if (typeof showAlert === 'function') showAlert('success', 'Invitation sent.');
              // clear search fields
              if (searchInput) searchInput.value = '';
              results.innerHTML = '';
              uid.value = '';
            } else {
              if (typeof showAlert === 'function') showAlert('danger', (data && data.message) || 'Failed to send invite.');
            }
          })
          .catch(function(err){ if (typeof showAlert === 'function') showAlert('danger', 'Failed to send invite: ' + err); });
      }
    });
  }

  // Confirmation modal for destructive actions (remove member / revoke invite)
  function attachConfirmHandler(f) {
    if (!f) return;
    f.addEventListener('submit', function(ev) {
        ev.preventDefault();
        const title = f.dataset.confirmTitle || 'Confirm';
        const message = f.dataset.confirmMessage || 'Are you sure?';
        if (typeof showConfirmationModal === 'function') {
          showConfirmationModal({
            title: title,
            message: message,
            confirmText: 'Yes',
            onConfirm: function() {
              if (f.classList.contains('js-ajax')) {
                const fd = new FormData(f);
                const tokenEl = f.querySelector('input[name="__RequestVerificationToken"]');
                fetch(f.action.replace(/(Invite|RemoveMember|RevokeInvite)$/,'$1Ajax'), {
                  method: 'POST',
                  body: fd,
                  headers: tokenEl ? { 'RequestVerificationToken': tokenEl.value } : {}
                }).then(function(resp){ return resp.json(); })
                  .then(function(data){
                    if (data && data.success) {
                      if (f.action.endsWith('RemoveMember')) {
                        const tr = document.querySelector('tr[data-user-id="' + fd.get('userId') + '"]');
                        if (tr) tr.remove();
                        if (typeof showAlert === 'function') showAlert('success', 'Member removed.');
                      } else if (f.action.endsWith('RevokeInvite')) {
                        const tr = document.querySelector('tr[data-invite-id="' + fd.get('inviteId') + '"]');
                        if (tr) tr.remove();
                        if (typeof showAlert === 'function') showAlert('success', 'Invitation canceled.');
                      } else if (f.action.endsWith('Invite')) {
                        const invitesTable = document.getElementById('invitesTable').querySelector('tbody');
                        if (invitesTable && data.invite) {
                          const row = document.createElement('tr');
                          row.setAttribute('data-invite-id', data.invite.id);
                          var inviteeLabel = '';
                          if (results && results.selectedOptions && results.selectedOptions.length) {
                            inviteeLabel = results.selectedOptions[0].textContent;
                          } else {
                            inviteeLabel = (fd.get('inviteeUserId') || '');
                          }
                          row.innerHTML = '<td>' + inviteeLabel + '</td>' +
                                          '<td>' + new Date().toLocaleString() + '</td>' +
                                          '<td></td>';
                          invitesTable.appendChild(row);
                        }
                        if (typeof showAlert === 'function') showAlert('success', 'Invitation sent.');
                      }
                    } else {
                      if (typeof showAlert === 'function') showAlert('danger', (data && data.message) || 'Action failed.');
                    }
                  })
                  .catch(function(err){ if (typeof showAlert === 'function') showAlert('danger', 'Action failed: ' + err); });
              } else {
                f.submit();
              }
            }
          });
        } else {
          // Fallback if modal helper missing
          if (confirm(message)) {
            if (f.classList.contains('js-ajax')) {
              const fd = new FormData(f);
              fetch(f.action.replace(/(Invite|RemoveMember|RevokeInvite)$/,'$1Ajax'), { method: 'POST', body: fd });
            } else {
              f.submit();
            }
          }
        }
      });
  }

  document.addEventListener('DOMContentLoaded', function() {
    document.querySelectorAll('form.js-confirm').forEach(function(f) { attachConfirmHandler(f); });
  });

  // SSE: refresh roster/invites when membership changes
  document.addEventListener('DOMContentLoaded', function() {
    try {
      const gidInput = document.querySelector('#inviteForm input[name="groupId"]');
      const gid = gidInput ? gidInput.value : '';
      if (!gid || typeof EventSource === 'undefined') return;
      const es = new EventSource('/api/sse/stream/group-membership-update/' + gid);
      es.onmessage = function(evt) {
        try {
          const data = evt && evt.data ? JSON.parse(evt.data) : null;
          const action = data && data.action;
          if (typeof showAlert === 'function') {
            if (action === 'member-joined') showAlert('success', 'A user joined the group.');
            else if (action === 'member-left') showAlert('warning', 'A user left the group.');
            else if (action === 'member-removed') showAlert('info', 'A user was removed from the group.');
            else if (action === 'invite-created') showAlert('info', 'New invite created.');
            else if (action === 'invite-declined') showAlert('secondary', 'An invite was declined.');
          }
        } catch { /* ignore parse errors */ }
        // Simple approach: reload to sync roster/invites
        setTimeout(function(){ window.location.reload(); }, 800);
      };
    } catch { /* ignore SSE errors */ }
  });
})();
