/** Every personal download starts unchecked and bypasses HTTP caches, HEAD probes and iframe fallbacks. */
const dialog = document.getElementById('personal-native-dialog');
const form = document.getElementById('personal-native-form');
// Bootstrap hides the menu after its action; the modal must have a visible ancestor.
if (dialog) document.body.append(dialog);
let downloading = false;
document.getElementById('personal-native-export')?.addEventListener('click', () => {
    if (downloading) return;
    form.reset();
    document.getElementById('personal-native-error').textContent = '';
    dialog.showModal();
});
document.getElementById('cancel-personal-native')?.addEventListener('click', () => dialog.close());
form?.addEventListener('submit', async event => {
    event.preventDefault();
    if (downloading) return;
    downloading = true;
    const submit = form.querySelector('[type=submit]'); submit.disabled = true;
    try {
        const url = new URL(form.action, window.location.href);
        url.search = new URLSearchParams(new FormData(form)).toString();
        const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', redirect: 'error' });
        if (!response.ok || !response.headers.get('Content-Type')?.startsWith('application/vnd.google-earth.kml+xml')) throw new Error();
        const blob = await response.blob();
        const objectUrl = URL.createObjectURL(blob);
        const anchor = document.createElement('a'); anchor.href = objectUrl; anchor.download = 'Wayfarer.kml';
        document.body.append(anchor); anchor.click(); anchor.remove();
        setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
        form.reset(); dialog.close();
    } catch {
        document.getElementById('personal-native-error').textContent = 'The complete download is unavailable. Your export choices are retained.';
    } finally { downloading = false; submit.disabled = false; }
});
