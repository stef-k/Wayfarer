import { initQuickPreview, initShareButton, initCloneForms } from './discoveryActions.js';
import { initTagFilters } from './tagFilters.js';

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

                        // Remove any width/height attributes that might interfere with CSS
                        img.removeAttribute('width');
                        img.removeAttribute('height');

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
                const isHeroImage = img.classList.contains('trip-photo');

                if (isHeroImage) {
                    img.removeAttribute('width');
                    img.removeAttribute('height');
                    img.style.cssText = 'position: absolute !important; top: 0 !important; left: 0 !important; right: 0 !important; bottom: 0 !important; width: auto !important; height: 100% !important; min-width: 100% !important; min-height: 100% !important; max-width: none !important; max-height: none !important; object-fit: cover !important; object-position: center !important; display: block !important;';
                }

                img.src = img.dataset.src;

                img.addEventListener('load', () => {
                    if (isHeroImage) {
                        img.removeAttribute('width');
                        img.removeAttribute('height');
                        img.style.cssText = 'position: absolute !important; top: 0 !important; left: 0 !important; right: 0 !important; bottom: 0 !important; width: auto !important; height: 100% !important; min-width: 100% !important; min-height: 100% !important; max-width: none !important; max-height: none !important; object-fit: cover !important; object-position: center !important; display: block !important;';
                    }
                }, { once: true });

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
     * Lazy Load Thumbnails Asynchronously (on viewport intersection)
     * Fetches map thumbnails via API only when trip cards enter viewport
     */
    const initAsyncThumbnails = () => {
        // Find all trip cards with data-trip-id attribute
        const tripCards = document.querySelectorAll('[data-trip-id]');

        if (!tripCards.length) {
            return;
        }

        // Use IntersectionObserver to fetch thumbnails only when cards enter viewport
        if ('IntersectionObserver' in window) {
            const thumbnailObserver = new IntersectionObserver(async (entries) => {
                entries.forEach(async (entry) => {
                    if (entry.isIntersecting) {
                        const card = entry.target;
                        const tripId = card.getAttribute('data-trip-id');

                        // Stop observing this card (fetch only once)
                        thumbnailObserver.unobserve(card);

                        if (!tripId) return;

                        // Find all thumbnail images for this trip
                        const thumbImages = card.querySelectorAll('[data-thumb-placeholder]');
                        if (!thumbImages.length) return;

                        try {
                            // Fetch thumbnail URL from API
                            const response = await fetch(`/Public/Trips/${tripId}/Thumbnail?size=800x450`);

                            if (!response.ok) {
                                console.warn(`Failed to fetch thumbnail for trip ${tripId}`);
                                return;
                            }

                            const data = await response.json();

                            if (data.thumbUrl) {
                                // Update all thumbnail images for this trip
                                thumbImages.forEach(img => {
                                    // Directly set the src to load the image immediately
                                    img.src = data.thumbUrl;
                                    img.removeAttribute('data-thumb-placeholder');
                                });
                            }
                        } catch (error) {
                            console.error(`Error fetching thumbnail for trip ${tripId}:`, error);
                        }
                    }
                });
            }, {
                rootMargin: '600px' // Start fetching 600px before entering viewport (buffer ~1-2 cards)
            });

            // Observe all trip cards
            tripCards.forEach(card => thumbnailObserver.observe(card));
        } else {
            // Fallback for browsers without IntersectionObserver support (rare)
            // Fetch all thumbnails immediately
            tripCards.forEach(async (card) => {
                const tripId = card.getAttribute('data-trip-id');
                if (!tripId) return;

                const thumbImages = card.querySelectorAll('[data-thumb-placeholder]');
                if (!thumbImages.length) return;

                try {
                    const response = await fetch(`/Public/Trips/${tripId}/Thumbnail?size=800x450`);
                    if (!response.ok) return;

                    const data = await response.json();
                    if (data.thumbUrl) {
                        thumbImages.forEach(img => {
                            img.src = data.thumbUrl;
                            img.removeAttribute('data-thumb-placeholder');
                        });
                    }
                } catch (error) {
                    console.error(`Error fetching thumbnail for trip ${tripId}:`, error);
                }
            });
        }
    };

    /**
     * Adjust sticky control bar position based on scroll
     * Moves to top when navbar is out of view
     */
    const initStickyControlBar = () => {
        const stickyBar = document.getElementById('stickyControlBar');
        if (!stickyBar) return;

        const navbar = document.querySelector('nav.navbar');
        const navbarHeight = navbar ? navbar.offsetHeight : 64; // Default 4rem = 64px

        let lastScrollY = window.scrollY;
        let ticking = false;

        const updateStickyPosition = () => {
            const scrollY = window.scrollY;

            // If scrolled past navbar, move sticky bar to top (0)
            // Otherwise keep it below navbar (4rem)
            if (scrollY > navbarHeight) {
                stickyBar.style.top = '0';
            } else {
                stickyBar.style.top = '4rem';
            }

            lastScrollY = scrollY;
            ticking = false;
        };

        const requestTick = () => {
            if (!ticking) {
                window.requestAnimationFrame(updateStickyPosition);
                ticking = true;
            }
        };

        window.addEventListener('scroll', requestTick, { passive: true });

        // Initial position
        updateStickyPosition();
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
        initTagFilters();
        initAsyncThumbnails(); // Fetch thumbnails asynchronously after page loads
        initCloneForms(); // Handle clone trip confirmation
        initStickyControlBar(); // Dynamic sticky bar positioning
    };

    // Run initialization
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
