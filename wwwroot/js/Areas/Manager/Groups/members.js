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
    }
  }

  if (searchBtn && searchInput && results) {
    searchBtn.addEventListener('click', function() { searchUsers(searchInput.value); });
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
})();
