/* Controls the existing group member sidebar as a phone-only map overlay. */
(() => {
    const view = document.querySelector('.group-map-view');
    const toggle = document.querySelector('.group-map-members-toggle');
    const panel = document.querySelector('.group-map-sidebar');
    const close = document.querySelector('.group-map-members-close');
    const phoneViewport = window.matchMedia('(max-width: 575.98px)');

    if (!view || !toggle || !panel || !close) {
        return;
    }

    const setOpen = open => {
        const isPhone = phoneViewport.matches;
        view.classList.toggle('group-map-members-open', isPhone && open);
        toggle.setAttribute('aria-expanded', String(isPhone && open));
        panel.setAttribute('aria-hidden', String(isPhone && !open));
    };

    toggle.addEventListener('click', () => {
        setOpen(true);
        close.focus();
    });

    close.addEventListener('click', () => {
        setOpen(false);
        toggle.focus();
    });

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && view.classList.contains('group-map-members-open')) {
            setOpen(false);
            toggle.focus();
        }
    });

    phoneViewport.addEventListener('change', () => setOpen(false));
    setOpen(false);
})();
