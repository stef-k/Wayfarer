// Lists joined groups and handles Leave via API.
// Uses /api/groups?scope=joined and POST /api/groups/{id}/leave

(() => {
  const tbody = document.getElementById('groupsBody');
  if (!tbody) return;

  async function loadJoined(){
    try {
      const resp = await fetch('/api/groups?scope=joined');
      if (!resp.ok) return;
      const data = await resp.json();
      if (!Array.isArray(data)) return;
      // If server rendered, skip replacing; else render
      if (tbody.children.length === 0) {
        tbody.innerHTML = data.map(g => `
          <tr data-group-id="${g.id}">
            <td>${g.name}</td>
            <td>${g.description||''}</td>
            <td></td>
            <td class="text-nowrap">
              <a class="btn btn-sm btn-primary" href="/User/Groups/Map?groupId=${g.id}">Map</a>
              <button type="button" class="btn btn-sm btn-outline-danger js-leave" data-group-id="${g.id}">Leave</button>
            </td>
          </tr>`).join('');
      }
    } catch {}
  }

  async function leaveGroup(groupId){
    try {
      const resp = await fetch(`/api/groups/${groupId}/leave`, { method: 'POST' });
      if (resp.ok) {
        const row = tbody.querySelector(`tr[data-group-id="${groupId}"]`);
        if (row) row.remove();
      }
    } catch {}
  }

  tbody.addEventListener('click', (e) => {
    const btn = e.target.closest('.js-leave');
    if (!btn) return;
    const gid = btn.getAttribute('data-group-id');
    if (!gid) return;
    if (confirm('Leave this group?')) leaveGroup(gid);
  });

  loadJoined();
})();

