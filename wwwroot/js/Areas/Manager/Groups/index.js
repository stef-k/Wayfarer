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
})();
