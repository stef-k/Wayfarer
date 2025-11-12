/**
 * Public Trips Index - ES Module
 * Handles lazy image loading and quick preview modal functionality
 */

(() => {
    'use strict';

    /**
     * Lazy image loading using IntersectionObserver
     * Loads images as they enter the viewport for better performance
     */
    const initLazyImages = () => {
        const images = document.querySelectorAll('img.js-lazy[data-src]');

        if (!images.length) {
            return;
        }

        if ('IntersectionObserver' in window) {
            const imageObserver = new IntersectionObserver((entries, observer) => {
                entries.forEach(entry => {
                    if (entry.isIntersecting) {
                        const img = entry.target;

                        // Set the actual src from data-src
                        img.src = img.dataset.src;

                        // Remove the lazy class and data-src attribute
                        img.classList.remove('js-lazy');
                        delete img.dataset.src;

                        // Stop observing this image
                        observer.unobserve(img);
                    }
                });
            }, {
                rootMargin: '200px' // Start loading 200px before entering viewport
            });

            // Observe all lazy images
            images.forEach(img => imageObserver.observe(img));
        } else {
            // Fallback for browsers without IntersectionObserver support
            images.forEach(img => {
                img.src = img.dataset.src;
                img.classList.remove('js-lazy');
                delete img.dataset.src;
            });
        }
    };

    /**
     * PiP Map Badge Toggle (touch + keyboard)
     * Toggles the map overlay when clicking the PiP badge
     */
    const initPiPToggle = () => {
        document.addEventListener('click', (ev) => {
            const btn = ev.target.closest('[data-hero-toggle]');
            if (!btn) {
                return;
            }

            // Don't trigger if this is also going to open a modal
            if (!btn.closest('.trip-hero-wrap')) {
                return;
            }

            ev.preventDefault();
            ev.stopPropagation();

            const wrap = btn.closest('.trip-hero-wrap');
            const overlay = wrap?.querySelector('.trip-map-overlay');
            if (!wrap || !overlay) {
                return;
            }

            // Lazy-load overlay the first time
            if (overlay.dataset?.src && !overlay.src) {
                overlay.src = overlay.dataset.src;
                overlay.classList.remove('js-lazy');
                delete overlay.dataset.src;
            }

            // Hide bubble & stop pulsing once interacted
            const bubble = btn.querySelector('.pip-tip-bubble');
            bubble?.classList.remove('is-visible');
            wrap.classList.add('tip-dismissed');

            // Persist dismissal
            try {
                const id = btn.getAttribute('data-trip-preview');
                const key = `pipTipDismissed:${id || 'global'}`;
                localStorage.setItem(key, '1');
            } catch {}

            wrap.classList.toggle('show-map');
        });

        // Optional: collapse on mouseleave (pointer devices)
        document.addEventListener('mouseleave', (ev) => {
            const wrap = ev.target?.closest?.('.trip-hero-wrap');
            if (!wrap) {
                return;
            }
            if (matchMedia('(hover: hover)').matches) {
                wrap.classList.remove('show-map');
            }
        }, true);

        // ESC to close when focused within a hero
        document.addEventListener('keydown', (ev) => {
            if (ev.key !== 'Escape') {
                return;
            }
            const focusedWrap = document.activeElement?.closest?.('.trip-hero-wrap') ||
                document.querySelector('.trip-hero-wrap.show-map');
            focusedWrap?.classList.remove('show-map');
        });
    };

    /**
     * One-time UI Tip Bootstrap
     * Shows hint bubble and pulse animation for PiP badges
     */
    const initPiPTips = () => {
        // Respect reduced motion: skip pulse + bubble
        if (matchMedia('(prefers-reduced-motion: reduce)').matches) {
            return;
        }

        document.querySelectorAll('.trip-map-badge').forEach((btn, idx) => {
            const wrap = btn.closest('.trip-hero-wrap');
            const bubble = btn.querySelector('.pip-tip-bubble');
            if (!wrap || !bubble) {
                return;
            }

            // If user has dismissed previously, skip
            let dismissed = false;
            try {
                const id = btn.getAttribute('data-trip-preview');
                const key = `pipTipDismissed:${id || 'global'}`;
                dismissed = localStorage.getItem(key) === '1';
            } catch {}

            if (dismissed) {
                wrap.classList.add('tip-dismissed');
                return;
            }

            // Show bubble shortly after load (stagger first few to avoid clutter)
            const delay = 600 + (idx % 3) * 200;
            setTimeout(() => {
                bubble.classList.add('is-visible');
                // Auto hide after a few seconds to avoid noise
                setTimeout(() => bubble.classList.remove('is-visible'), 3000);
            }, delay);
        });
    };

    /**
     * Quick Preview Modal
     * Loads trip preview content via AJAX when preview button is clicked
     */
    const initQuickPreview = () => {
        const modal = document.getElementById('tripQuickView');
        const modalBody = modal?.querySelector('.modal-body');

        if (!modal || !modalBody) {
            return;
        }

        // Listen for preview button clicks (NOT the PiP badge)
        document.addEventListener('click', async (event) => {
            const previewButton = event.target.closest('[data-trip-preview]');

            // Skip if this is a PiP badge (handled by initPiPToggle)
            if (!previewButton || previewButton.hasAttribute('data-hero-toggle')) {
                return;
            }

            const tripId = previewButton.getAttribute('data-trip-preview');

            if (!tripId) {
                console.error('Trip ID not found on preview button');
                return;
            }

            // Show loading state
            modalBody.innerHTML = `
                <div class="p-5 text-center text-secondary">
                    <div class="spinner-border mb-3" role="status">
                        <span class="visually-hidden">Loading...</span>
                    </div>
                    <div>Loading trip preview...</div>
                </div>
            `;

            try {
                // Fetch the preview content
                const response = await fetch(`/Public/Trips/Preview/${tripId}`, {
                    headers: {
                        'X-Requested-With': 'XMLHttpRequest'
                    }
                });

                if (!response.ok) {
                    throw new Error(`HTTP ${response.status}: ${response.statusText}`);
                }

                // Get the HTML content
                const html = await response.text();

                // Inject into modal body
                modalBody.innerHTML = html;

            } catch (error) {
                console.error('Failed to load trip preview:', error);

                // Show error state with link to full page
                modalBody.innerHTML = `
                    <div class="alert alert-danger m-3">
                        <i class="bi bi-exclamation-triangle"></i>
                        Failed to load preview.
                        <a href="/Public/Trips/${tripId}" class="alert-link">Open full trip page</a>.
                    </div>
                `;
            }
        });
    };

    /**
     * Share Button - Copy URL to Clipboard
     * Handles copying public trip URLs when share button is clicked
     */
    const initShareButton = () => {
        document.addEventListener('click', async (event) => {
            const shareButton = event.target.closest('.copy-url');
            if (!shareButton) {
                return;
            }

            event.preventDefault();

            const url = shareButton.getAttribute('data-url');
            if (!url) {
                console.error('URL not found on share button');
                return;
            }

            try {
                // Copy URL to clipboard
                await navigator.clipboard.writeText(url);

                // Show success toast (non-intrusive)
                if (window.wayfarer?.showToast) {
                    window.wayfarer.showToast('success', 'URL copied to clipboard!');
                } else if (window.showToast) {
                    window.showToast('success', 'URL copied to clipboard!');
                } else {
                    // Fallback: show brief visual feedback
                    const icon = shareButton.querySelector('i');
                    const originalClass = icon?.className;
                    if (icon) {
                        icon.className = 'bi bi-check-lg';
                        setTimeout(() => {
                            icon.className = originalClass;
                        }, 1500);
                    }
                }
            } catch (error) {
                console.error('Failed to copy URL:', error);

                // Show error toast
                if (window.wayfarer?.showToast) {
                    window.wayfarer.showToast('danger', 'Failed to copy URL.');
                } else if (window.showToast) {
                    window.showToast('danger', 'Failed to copy URL.');
                } else {
                    alert('Failed to copy URL to clipboard.');
                }
            }
        });
    };

    /**
     * Initialize all functionality when DOM is ready
     */
    const init = () => {
        initLazyImages();
        initPiPToggle();
        initPiPTips();
        initQuickPreview();
        initShareButton();
    };

    // Run initialization
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
