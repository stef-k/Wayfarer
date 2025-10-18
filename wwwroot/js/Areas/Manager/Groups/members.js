(() => {
  const form = document.getElementById('inviteForm');
  if (form) {
    form.addEventListener('submit', (e) => {
      const uid = form.querySelector('#inviteeUserId');
      const email = form.querySelector('#inviteeEmail');
      const hasUid = uid && uid.value && uid.value.trim().length > 0;
      const hasEmail = email && email.value && email.value.trim().length > 0;
      if (!hasUid && !hasEmail) {
        e.preventDefault();
        if (uid) uid.classList.add('is-invalid');
        if (email) email.classList.add('is-invalid');
      } else {
        if (uid) uid.classList.remove('is-invalid');
        if (email) email.classList.remove('is-invalid');
      }
    });
  }
})();

