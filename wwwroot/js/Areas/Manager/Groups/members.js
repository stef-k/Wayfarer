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
})();
