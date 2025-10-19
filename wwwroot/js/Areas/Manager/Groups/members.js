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
      if (!uid || !uid.value || !uid.value.trim()) {
        e.preventDefault();
        if (results) results.classList.add('is-invalid');
      } else {
        if (results) results.classList.remove('is-invalid');
      }
    });
  }

  // Confirmation modal for destructive actions (remove member / revoke invite)
  document.addEventListener('DOMContentLoaded', function() {
    document.querySelectorAll('form.js-confirm').forEach(function(f) {
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
                fetch(f.action.replace(/(Invite|RemoveMember|RevokeInvite)$/,'$1Ajax'), {
                  method: 'POST',
                  body: fd
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
                        if (typeof showAlert === 'function') showAlert('success', 'Invitation revoked.');
                      } else if (f.action.endsWith('Invite')) {
                        const invitesTable = document.getElementById('invitesTable').querySelector('tbody');
                        if (invitesTable && data.invite) {
                          const row = document.createElement('tr');
                          row.setAttribute('data-invite-id', data.invite.id);
                          row.innerHTML = '<td>' + (fd.get('inviteeUserId') || '') + '</td>' +
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
    });
  });
})();
