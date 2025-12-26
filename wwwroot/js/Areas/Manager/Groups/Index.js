(() => {
  const forms = [document.getElementById('groupCreateForm'), document.getElementById('groupEditForm')].filter(Boolean);
  forms.forEach(form => {
    form.addEventListener('submit', (e) => {
      const name = form.querySelector('#name');
      const type = form.querySelector('#groupType');
      if (!name || !name.value || !name.value.trim()) {
        e.preventDefault();
        name.classList.add('is-invalid');
        name.focus();
      } else {
        name.classList.remove('is-invalid');
      }

      if (type && (!type.value || !type.value.trim())) {
        e.preventDefault();
        type.classList.add('is-invalid');
        if (name && !name.classList.contains('is-invalid')) {
          type.focus();
        }
      } else if (type) {
        type.classList.remove('is-invalid');
      }
    });
  });

  // Live update member counts via SSE (consolidated group endpoint)
  document.addEventListener('DOMContentLoaded', function() {
    try {
      if (typeof EventSource === 'undefined') return;
      document.querySelectorAll('tr[data-group-id]').forEach(function(row){
        const gid = row.getAttribute('data-group-id');
        if (!gid) return;
        const es = new EventSource('/api/sse/group/' + gid);
        es.onmessage = function(evt){
          try {
            const d = evt && evt.data ? JSON.parse(evt.data) : null;
            if (!d || !d.type) return;
            const badge = row.querySelector('.js-member-count');
            if (!badge) return;
            let n = parseInt(badge.textContent || '0', 10);
            if (d.type === 'member-joined') n += 1;
            if (d.type === 'member-left' || d.type === 'member-removed') n = Math.max(0, n - 1);
            badge.textContent = String(n);
          } catch { /* ignore */ }
        };
      });
    } catch { /* ignore SSE errors */ }
  });
})();
