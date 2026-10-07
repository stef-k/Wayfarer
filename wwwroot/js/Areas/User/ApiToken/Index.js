/** One owner controls transient plaintext, QR state and asynchronous generations for the connection page. */
export const createConnectionPage = ({ root, browser = window, request = fetch, QrCode = window.QRCode }) => {
    const find = id => root.querySelector(`#${id}`);
    const tokenNode = find('connection-token');
    const qrNode = find('connection-qr');
    const reveal = find('connection-reveal');
    const confirmation = find('replace-confirmation');
    const issueButton = find('issue-connection');
    const hideButton = find('hide-connection');
    const message = find('connection-message');
    const origin = browser.location.origin;
    const https = browser.location.protocol === 'https:' && root.dataset.effectiveHttps === 'true';
    let status = root.dataset.tokenId ? { tokenId: Number(root.dataset.tokenId), issuedAt: root.dataset.issuedAt } : null;
    let phase = 'ready';
    let generation = 0;
    let plaintext = '';
    let qr = null;
    let pending = null;

    /** Mutation controls remain blocked whenever current safe status is uncertain. */
    const renderStatus = () => {
        find('connection-status').textContent = status ? 'Connection token exists' : 'No connection token';
        find('connection-issued').textContent = status ? `Last issued: ${status.issuedAt}` : '';
        issueButton.textContent = status ? 'Replace connection token' : 'Create connection token';
        issueButton.disabled = !https || phase !== 'ready';
        find('confirm-replacement').disabled = issueButton.disabled;
    };

    /** Removes every app-owned reveal reference synchronously and invalidates late completions. */
    const clear = () => {
        generation++;
        pending?.abort();
        pending = null;
        plaintext = '';
        if (qr) {
            qr.clear();
            qr._htOption.text = '';
            qr._oQRCode = null;
            qr._oDrawing = null;
            qr._el = null;
            qr = null;
        }
        tokenNode.replaceChildren();
        qrNode.replaceChildren();
        qrNode.removeAttribute('title');
        reveal.hidden = true;
        confirmation.hidden = true;
        hideButton.hidden = true;
    };

    /** Bounded local messages never echo a server exception or a credential. */
    const showMessage = text => {
        message.textContent = text;
        message.hidden = false;
    };

    /** A single safe GET refreshes status after failed or uncertain issuance; it never replays the POST. */
    const refreshStatus = async () => {
        phase = 'refreshing';
        renderStatus();
        const observed = generation;
        try {
            const response = await request('/User/ApiToken', {
                credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'application/json' }
            });
            if (!response.ok) throw new Error('Safe status unavailable');
            const metadata = await response.json();
            if (observed !== generation) return;
            status = metadata;
            phase = 'ready';
            renderStatus();
        } catch {
            if (observed === generation) showMessage('Refresh this page to check connection token status before trying again.');
        }
    };

    /** Reveals only the committed winning response for the currently active operation generation. */
    const issue = async () => {
        if (!https || phase !== 'ready') return;
        const expected = status;
        clear();
        phase = 'pending';
        renderStatus();
        hideButton.hidden = false;
        message.hidden = true;
        const observed = generation;
        pending = new AbortController();
        try {
            const response = await request(`/User/ApiToken/${expected ? 'Replace' : 'Create'}`, {
                method: 'POST', credentials: 'same-origin', cache: 'no-store', signal: pending.signal,
                headers: { 'Content-Type': 'application/json',
                    RequestVerificationToken: root.querySelector('[name="__RequestVerificationToken"]').value },
                body: expected ? JSON.stringify({ ...expected, confirmed: true }) : undefined
            });
            if (observed !== generation) return;
            if (!response.ok) throw new Error('Issuance was not confirmed');
            let result = await response.json();
            if (observed !== generation) { result = null; return; }
            status = { tokenId: result.tokenId, issuedAt: result.issuedAt };
            plaintext = result.token;
            result = null;
            tokenNode.textContent = plaintext;
            reveal.hidden = false;
            // The bundled SVG renderer is synchronous, unlike its delayed canvas-to-image path.
            qr = new QrCode(qrNode, { text: JSON.stringify({ serverUrl: origin, apiToken: plaintext }),
                width: 220, height: 220, useSVG: true });
            qrNode.removeAttribute('title');
            pending = null;
            phase = 'ready';
            renderStatus();
        } catch {
            if (observed !== generation) return;
            clear();
            showMessage('No token can be shown. The request may have committed and stopped apps using the old token. Checking safe status; choose replacement explicitly if needed.');
            await refreshStatus();
        }
    };

    /** Hide never revokes; cancellation still needs safe status because a server commit may already have happened. */
    const hide = () => {
        const uncertain = phase === 'pending';
        clear();
        if (uncertain) {
            showMessage('The request may already have committed. Checking safe status before another explicit action.');
            void refreshStatus();
        } else renderStatus();
    };

    /** Copies intentionally to the OS clipboard without retaining a secret argument across the asynchronous wait. */
    const copy = async value => {
        const observed = generation;
        try {
            const copying = browser.navigator.clipboard.writeText(value);
            value = '';
            await copying;
            if (observed === generation) showMessage('Copied.');
        } catch {
            if (observed === generation) showMessage('Copy was unavailable. Select and copy the displayed value manually.');
        }
    };

    issueButton.addEventListener('click', () => {
        if (!https || phase !== 'ready') return;
        if (status) confirmation.hidden = false;
        else void issue();
    });
    find('confirm-replacement').addEventListener('click', () => { if (!confirmation.hidden) void issue(); });
    find('cancel-replacement').addEventListener('click', () => { confirmation.hidden = true; });
    hideButton.addEventListener('click', hide);
    find('copy-token').addEventListener('click', () => { if (plaintext) void copy(plaintext); });
    find('copy-server').addEventListener('click', () => { if (https) void copy(origin); });
    browser.addEventListener('pagehide', () => { clear(); phase = 'away'; renderStatus(); });
    browser.addEventListener('pageshow', event => {
        const restored = event.persisted || phase === 'away';
        const uncertain = phase !== 'ready';
        clear();
        if (restored) { phase = 'refreshing'; renderStatus(); browser.location.reload(); }
        else if (uncertain) void refreshStatus();
        else renderStatus();
    });
    clear();
    find('connection-origin').textContent = origin;
    find('copy-server').disabled = !https;
    if (!https) showMessage('HTTPS is required to connect apps. The instance or proxy configuration needs correction.');
    renderStatus();
    return { hide, issue };
};

// Production initialization and deterministic tests share this same state owner.
const root = typeof document === 'undefined' ? null : document.getElementById('connect-apps');
if (root) createConnectionPage({ root });
