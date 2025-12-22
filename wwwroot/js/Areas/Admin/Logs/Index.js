/**
 * Admin Log Viewer - Client-side functionality for viewing application logs.
 * Provides file selection, content display, real-time polling, search, and download.
 */

(() => {
    // State management
    let currentFileName = '';
    let currentPosition = 0;
    let totalLineCount = 0;
    let autoRefreshEnabled = false;
    let pollIntervalId = null;
    const POLL_INTERVAL_MS = 2500;
    const MAX_LINES_INITIAL = 1000;
    const MAX_LINES_POLL = 100;

    // DOM elements
    const logFileSelect = document.getElementById('logFileSelect');
    const logViewer = document.getElementById('logViewer');
    const searchInput = document.getElementById('searchInput');
    const searchBtn = document.getElementById('searchBtn');
    const clearSearchBtn = document.getElementById('clearSearchBtn');
    const autoRefreshBtn = document.getElementById('autoRefreshBtn');
    const refreshBtn = document.getElementById('refreshBtn');
    const downloadBtn = document.getElementById('downloadBtn');
    const clearBtn = document.getElementById('clearBtn');
    const fileInfo = document.getElementById('fileInfo');
    const searchResults = document.getElementById('searchResults');
    const lineCount = document.getElementById('lineCount');
    const lastUpdated = document.getElementById('lastUpdated');

    /**
     * Initializes the log viewer on page load.
     */
    const init = () => {
        if (!logFileSelect) return;

        logFileSelect.addEventListener('change', handleFileSelect);
        searchBtn.addEventListener('click', handleSearch);
        clearSearchBtn.addEventListener('click', clearSearch);
        searchInput.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') handleSearch();
        });
        autoRefreshBtn.addEventListener('click', toggleAutoRefresh);
        refreshBtn.addEventListener('click', refreshContent);
        downloadBtn.addEventListener('click', downloadLog);
        clearBtn.addEventListener('click', clearDisplay);
    };

    /**
     * Handles log file selection from dropdown.
     */
    const handleFileSelect = async () => {
        const fileName = logFileSelect.value;
        if (!fileName) {
            resetViewer();
            return;
        }

        currentFileName = fileName;
        currentPosition = 0;
        totalLineCount = 0;

        // Enable controls
        enableControls(true);

        // Update file info
        const selectedOption = logFileSelect.options[logFileSelect.selectedIndex];
        const size = selectedOption.dataset.size || '';
        const modified = selectedOption.dataset.modified || '';
        fileInfo.textContent = `${fileName} - ${size} - Last modified: ${modified}`;

        await loadLogContent();
    };

    /**
     * Loads log file content from the server.
     * @param {boolean} append - If true, append to existing content (for polling).
     */
    const loadLogContent = async (append = false) => {
        if (!currentFileName) return;

        logViewer.classList.add('loading');
        const maxLines = append ? MAX_LINES_POLL : MAX_LINES_INITIAL;

        try {
            const response = await fetch(
                `/Admin/Logs/GetLogContent?fileName=${encodeURIComponent(currentFileName)}&lastPosition=${currentPosition}&maxLines=${maxLines}`
            );

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            const data = await response.json();

            if (data.lineCount > 0) {
                const wasAtBottom = isScrolledToBottom();

                if (append) {
                    logViewer.textContent += '\n' + data.content;
                } else {
                    logViewer.textContent = data.content;
                }

                currentPosition = data.newPosition;
                totalLineCount += data.lineCount;

                if (wasAtBottom || !append) {
                    scrollToBottom();
                }
            } else if (!append) {
                logViewer.textContent = '(Empty log file)';
            }

            updateStatusBar();

        } catch (error) {
            console.error('Error loading log content:', error);
            if (wayfarer?.showAlert) {
                wayfarer.showAlert('danger', `Error loading log: ${error.message}`);
            }
            if (autoRefreshEnabled) {
                toggleAutoRefresh();
            }
        } finally {
            logViewer.classList.remove('loading');
        }
    };

    /**
     * Refreshes the current log file content from the beginning.
     */
    const refreshContent = async () => {
        currentPosition = 0;
        totalLineCount = 0;
        clearSearch();
        await loadLogContent();
    };

    /**
     * Toggles auto-refresh polling on/off.
     */
    const toggleAutoRefresh = () => {
        autoRefreshEnabled = !autoRefreshEnabled;

        if (autoRefreshEnabled) {
            autoRefreshBtn.classList.add('active');
            pollIntervalId = setInterval(pollLogContent, POLL_INTERVAL_MS);
            if (wayfarer?.showToast) {
                wayfarer.showToast('info', 'Auto-refresh enabled', 2000);
            }
        } else {
            autoRefreshBtn.classList.remove('active');
            if (pollIntervalId) {
                clearInterval(pollIntervalId);
                pollIntervalId = null;
            }
        }
    };

    /**
     * Polls for new log content (called by interval).
     */
    const pollLogContent = async () => {
        if (!currentFileName || !autoRefreshEnabled) return;
        await loadLogContent(true);
    };

    /**
     * Handles search button click.
     */
    const handleSearch = () => {
        const query = searchInput.value.trim();
        if (!query) {
            clearSearch();
            return;
        }

        searchLogs(query);
    };

    /**
     * Searches and highlights matches in the log viewer.
     * @param {string} query - Search query string.
     */
    const searchLogs = (query) => {
        const content = logViewer.textContent;
        const escapedQuery = escapeRegex(query);
        const regex = new RegExp(`(${escapedQuery})`, 'gi');
        const matches = content.match(regex);
        const matchCount = matches ? matches.length : 0;

        // Highlight matches using innerHTML (careful with XSS - we escape content)
        const escapedContent = escapeHtml(content);
        const highlighted = escapedContent.replace(
            new RegExp(`(${escapeRegex(escapeHtml(query))})`, 'gi'),
            '<mark>$1</mark>'
        );
        logViewer.innerHTML = highlighted;

        // Update search results badge
        searchResults.textContent = `${matchCount} match${matchCount !== 1 ? 'es' : ''}`;
        searchResults.classList.remove('d-none');

        // Scroll to first match
        if (matchCount > 0) {
            const firstMark = logViewer.querySelector('mark');
            if (firstMark) {
                firstMark.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
        }
    };

    /**
     * Clears search highlighting.
     */
    const clearSearch = () => {
        searchInput.value = '';
        searchResults.classList.add('d-none');

        // Restore plain text content (remove highlights)
        const content = logViewer.textContent;
        logViewer.textContent = content;
    };

    /**
     * Downloads the current log file.
     */
    const downloadLog = () => {
        if (!currentFileName) return;
        window.location.href = `/Admin/Logs/DownloadLog?fileName=${encodeURIComponent(currentFileName)}`;
    };

    /**
     * Clears the log viewer display.
     */
    const clearDisplay = () => {
        logViewer.textContent = 'Select a log file from the dropdown above to view its contents.';
        logFileSelect.value = '';
        currentFileName = '';
        currentPosition = 0;
        totalLineCount = 0;
        enableControls(false);
        fileInfo.textContent = 'Select a log file to view';
        clearSearch();
        updateStatusBar();

        if (autoRefreshEnabled) {
            toggleAutoRefresh();
        }
    };

    /**
     * Resets the viewer to initial state.
     */
    const resetViewer = () => {
        clearDisplay();
    };

    /**
     * Enables or disables control buttons.
     * @param {boolean} enabled - Whether controls should be enabled.
     */
    const enableControls = (enabled) => {
        searchInput.disabled = !enabled;
        searchBtn.disabled = !enabled;
        clearSearchBtn.disabled = !enabled;
        autoRefreshBtn.disabled = !enabled;
        refreshBtn.disabled = !enabled;
        downloadBtn.disabled = !enabled;
        clearBtn.disabled = !enabled;
    };

    /**
     * Updates the status bar with current stats.
     */
    const updateStatusBar = () => {
        lineCount.textContent = `${totalLineCount} line${totalLineCount !== 1 ? 's' : ''}`;
        lastUpdated.textContent = `Last updated: ${new Date().toLocaleTimeString()}`;
    };

    /**
     * Checks if the log viewer is scrolled to the bottom.
     * @returns {boolean} True if at bottom.
     */
    const isScrolledToBottom = () => {
        return Math.abs(logViewer.scrollHeight - logViewer.scrollTop - logViewer.clientHeight) < 50;
    };

    /**
     * Scrolls the log viewer to the bottom.
     */
    const scrollToBottom = () => {
        logViewer.scrollTop = logViewer.scrollHeight;
    };

    /**
     * Escapes special regex characters in a string.
     * @param {string} str - String to escape.
     * @returns {string} Escaped string.
     */
    const escapeRegex = (str) => {
        return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    };

    /**
     * Escapes HTML special characters to prevent XSS.
     * @param {string} str - String to escape.
     * @returns {string} HTML-escaped string.
     */
    const escapeHtml = (str) => {
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    };

    // Initialize on DOM ready
    document.addEventListener('DOMContentLoaded', init);
})();
