(() => {
  const forms = [document.getElementById('groupCreateForm'), document.getElementById('groupEditForm')].filter(Boolean);
  forms.forEach(form => {
    form.addEventListener('submit', (e) => {
      const name = form.querySelector('#name');
      if (!name || !name.value || !name.value.trim()) {
        e.preventDefault();
        name.classList.add('is-invalid');
        name.focus();
      } else {
        name.classList.remove('is-invalid');
      }
    });
  });
})();

