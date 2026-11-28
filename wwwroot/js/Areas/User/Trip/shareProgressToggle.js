import { message } from '../../../localization/messages.js';

/**
 * Keeps the Viewer sharing switches in sync with the last confirmed setting.
 */

/**
 * Copy text to clipboard with fallback for older browsers
 * @param {string} text - Text to copy
 * @returns {Promise<boolean>} - Success status
 */
const copyToClipboard = async (text) => {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        // Fallback for older browsers
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        textarea.select();
        const success = document.execCommand('copy');
        document.body.removeChild(textarea);
        return success;
    }
};

/**
 * Initializes sharing from the server-rendered switch and connects both inputs to the same save.
 */
export const initShareProgressToggle = () => {
    const btnCopyProgressLink = document.getElementById('btnCopyProgressLink');

    // Copy Link button - copies public URL with progress param
    if (btnCopyProgressLink) {
        btnCopyProgressLink.addEventListener('click', async () => {
            const baseUrl = btnCopyProgressLink.dataset.publicUrl;
            if (!baseUrl) return;

            // Build full URL with progress param
            const url = new URL(baseUrl, window.location.origin);
            url.searchParams.set('progress', '1');
            const fullUrl = url.toString();

            const success = await copyToClipboard(fullUrl);
            if (success) {
                // Show feedback
                const originalHtml = btnCopyProgressLink.innerHTML;
                btnCopyProgressLink.textContent = message('Shell.UrlCopied', 'URL copied to clipboard!');
                btnCopyProgressLink.classList.remove('btn-outline-success');
                btnCopyProgressLink.classList.add('btn-success');
                setTimeout(() => {
                    btnCopyProgressLink.innerHTML = originalHtml;
                    btnCopyProgressLink.classList.remove('btn-success');
                    btnCopyProgressLink.classList.add('btn-outline-success');
                }, 2000);
            }
        });
    }

};
