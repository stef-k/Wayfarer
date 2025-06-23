/* =========================================================================
   Wayfarer central reactive store  –  alias-free edition
   =========================================================================
   • Immutable snapshots via getState()
   • Fine-grained subscriptions via subscribe() / subscribeOnce()
   • Single–pass dispatch(): mutate state → broadcast that *same* action
   • Optional in-browser dev panel & global exposure for debugging
   ------------------------------------------------------------------------- */

export const createStore = (config = {}) => {
    /* ------------------------------------------------------------------ *
     *  Configuration
     * ------------------------------------------------------------------ */
    const opts = typeof config === 'string' ? {name: config} : config;
    const {
        name = undefined,   // e.g. "Trip"
        initialState = {},          // { context:null, tripId:null, quill:null, … }
        enableDevPanel = false,
        exposeGlobal = true
    } = opts;

    /* ------------------------------------------------------------------ *
     *  Private data
     * ------------------------------------------------------------------ */
    const state = structuredClone(initialState);
    state.segmentVisibility = {};
    const listeners = new Set();

    /* ------------------------------------------------------------------ *
     *  Public API
     * ------------------------------------------------------------------ */
    const store = {
        /** Immutable, serialisable snapshot of internal state */
        getState: () => structuredClone(state),

        /**
         * Subscribe to *every* store event.
         * @param {(evt:{type:string,payload:any})=>void} fn
         * @return {() => void} – unsubscribe handle
         */
        subscribe: (fn) => {
            listeners.add(fn);
            return () => listeners.delete(fn);
        },

        /**
         * Subscribe once – auto-unsubscribe after first event that passes filter.
         * @param {(evt)=>boolean} filterFn
         * @param {(evt)=>void}    listener
         */
        subscribeOnce: (filterFn, listener) => {
            const off = store.subscribe((evt) => {
                if (filterFn(evt)) {
                    off();
                    listener(evt);
                }
            });
        },

        /**
         * Dispatch an action.
         * 1) Mutate state if the action is state-changing
         * 2) Notify **all** subscribers with {type, payload}
         *
         * @param {string} action
         * @param {*}      payload
         */
        dispatch: (action, payload) => {
            /* -------- 1️⃣  Mutate state (only recognised actions) -------- */
            switch (action) {
                case 'set-context':
                    state.context = payload;  // { type:'place', id, action:'edit', meta:{…} }
                    break;

                case 'clear-context':
                    state.context = null;
                    break;

                case 'set-trip-id':
                    state.tripId = payload;   // number | string
                    break;

                case 'set-quill':
                    state.quill = payload;    // Quill instance
                    break;

                case 'set-segment-visibility':
                    state.segmentVisibility = {
                        ...state.segmentVisibility,
                        [payload.segmentId]: !!payload.visible
                    };
                    break;

                case 'toggle-segment-visibility':
                    const current = state.segmentVisibility[payload.segmentId];
                    state.segmentVisibility = {
                        ...state.segmentVisibility,
                        [payload.segmentId]: !current
                    };
                    break;

                /* default: unknown ⇒ “event-only” action (valid) */
            }

            /* -------- 2️⃣  Broadcast the action exactly once -------- */
            listeners.forEach(fn => fn({type: action, payload}));

            /* -------- 3️⃣  Optional live dev panel refresh -------- */
            if (typeof window.__storeDebugUpdate === 'function') window.__storeDebugUpdate();
        }
    };

    /* ------------------------------------------------------------------ *
     *  Optional: expose store on window for quick debugging
     * ------------------------------------------------------------------ */
    if (exposeGlobal && name) {
        const key = `${name}Store`;
        window[key] = store;
        window[`${name}State`] = () =>
            console.log(`🧠 ${key} snapshot`, store.getState());
    }

    /* ------------------------------------------------------------------ *
     *  Optional: tiny developer overlay panel
     * ------------------------------------------------------------------ */
    /* ------------------------------------------------------------------ *
     *  Optional: tiny developer overlay panel (initially collapsed)
     * ------------------------------------------------------------------ */
    if (enableDevPanel) {
        const panel = document.createElement('div');
        panel.style = `
        position:fixed;bottom:14px;right:0;z-index:9999;
        font:12px monospace;max-width:480px;
        background:#fff;border:1px solid #ccc;box-shadow:0 0 4px rgba(0,0,0,.25);
        height:20px; /* explicitly collapsed */
    `;
        panel.innerHTML = `
        <div style="display:flex;justify-content:space-between;padding:4px 6px;background:#f5f5f5">
            <strong>🧠 Store</strong>
            <button id="wfToggleStore" class="btn btn-sm btn-outline-secondary">Show</button>
        </div>
        <div id="wfStoreControls" style="padding:4px 6px;display:flex;gap:4px">
            <button class="btn btn-sm btn-outline-primary" id="wfStoreRefresh">Refresh</button>
            <button class="btn btn-sm btn-outline-danger"  id="wfStoreClear">Clear</button>
        </div>
        <pre id="wfStoreDump" style="margin:0;padding:6px 6px 8px;height:140px;overflow:auto;display:none;"></pre>`;
        document.body.appendChild(panel);

        const $dump = document.getElementById('wfStoreDump');
        const $toggle = document.getElementById('wfToggleStore');
        const $refresh = document.getElementById('wfStoreRefresh');
        const $clear = document.getElementById('wfStoreClear');
        let visible = false; // ✅ initially collapsed

        const repaint = () => {
            $dump.textContent = JSON.stringify(store.getState(), null, 2);
        };
        window.__storeDebugUpdate = repaint;

        $refresh.onclick = repaint;
        $clear.onclick = () => ($dump.textContent = '');
        $toggle.onclick = () => {
            visible = !visible;
            panel.style.height = visible ? 'auto' : '20px';
            $dump.style.display = visible ? 'block' : 'none';
            $toggle.textContent = visible ? 'Hide' : 'Show';
        };

        repaint();
    }

    return store;
};

