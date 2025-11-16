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
     * Clone Trip Confirmation
     * Shows a confirmation modal before cloning a trip
     */
    const initCloneForms = () => {
        document.addEventListener('submit', (e) => {
            const form = e.target;
            if (!form.classList.contains('clone-trip-form')) {
                return;
            }

            e.preventDefault();
            const btn = form.querySelector('button[type="submit"]');

            if (typeof window.showConfirmationModal === 'function') {
                window.showConfirmationModal({
                    title: 'Clone Trip',
                    message: 'Clone this trip to your account? You will be able to edit and customize your copy.',
                    confirmText: 'Clone Trip',
                    onConfirm: () => {
                        // Show loading state
                        if (btn) {
                            btn.disabled = true;
                            const originalHTML = btn.innerHTML;
                            btn.innerHTML = '<i class="bi bi-hourglass-split"></i>';
                            // Restore after submit in case of redirect delay
                            setTimeout(() => {
                                btn.innerHTML = originalHTML;
                                btn.disabled = false;
                            }, 5000);
                        }
                        form.submit();
                    }
                });
            } else {
                // Fallback to browser confirm if custom modal not available
                if (confirm('Clone this trip to your account? You will be able to edit and customize your copy.')) {
                    form.submit();
                }
            }
        });
    };

    /**
     * Tag filtering + suggestions
     */
    const initTagFilters = () => {
        const filterRoot = document.querySelector('[data-tag-filter]');
        if (!filterRoot) {
            return;
        }

        const selectedTags = new Set(
            (filterRoot.dataset.tags || '')
                .split(',')
                .map(slug => slug.trim())
                .filter(Boolean)
        );
        let mode = (filterRoot.dataset.tagMode || 'all').toLowerCase() === 'any' ? 'any' : 'all';
        const tagInput = filterRoot.querySelector('[data-tag-input]');
        const tagWrapper = filterRoot.querySelector('[data-tag-input-wrapper]');
        const suggestionsEl = filterRoot.querySelector('[data-tag-suggestions]');
        const suggestUrl = filterRoot.dataset.suggestUrl || '/Public/Tags/Suggest';
        const clearBtn = filterRoot.querySelector('[data-clear-tags]');
        let currentSuggestions = [];
        let debounceHandle;

        // Click wrapper to focus input
        if (tagWrapper) {
            tagWrapper.addEventListener('click', ev => {
                if (ev.target === tagWrapper || ev.target.closest('[data-tag-chip]') === null) {
                    tagInput?.focus();
                }
            });
        }

        const applyFilters = () => {
            const params = new URLSearchParams(window.location.search);
            if (selectedTags.size > 0) {
                params.set('tags', Array.from(selectedTags).join(','));
            } else {
                params.delete('tags');
            }
            params.set('tagMode', mode);
            params.set('page', '1');
            window.location.href = `${window.location.pathname}?${params.toString()}`;
        };

        const renderSuggestions = items => {
            currentSuggestions = items;
            if (!suggestionsEl) {
                return;
            }
            if (!items.length) {
                suggestionsEl.classList.add('d-none');
                suggestionsEl.innerHTML = '';
                return;
            }

            suggestionsEl.innerHTML = items.map(item => `
                <button type="button" class="list-group-item list-group-item-action"
                        data-suggest-slug="${item.slug}" data-suggest-name="${item.name}">
                    <span class="fw-semibold">${item.name}</span>
                    <span class="text-muted small ms-2">${item.count}</span>
                </button>`).join('');
            suggestionsEl.classList.remove('d-none');
        };

        const fetchSuggestions = async query => {
            try {
                const resp = await fetch(`${suggestUrl}?q=${encodeURIComponent(query)}`);
                if (!resp.ok) {
                    throw new Error(resp.statusText);
                }
                const data = await resp.json();
                renderSuggestions(Array.isArray(data) ? data : []);
            } catch (err) {
                console.warn('Failed to load tag suggestions', err);
                renderSuggestions([]);
            }
        };

        const handleTagAdd = slug => {
            if (!slug) {
                return;
            }
            if (!selectedTags.has(slug)) {
                selectedTags.add(slug);
                applyFilters();
            }
        };

        filterRoot.addEventListener('click', ev => {
            const closeBtn = ev.target.closest('[data-tag-chip] .btn-close');
            if (closeBtn) {
                ev.preventDefault();
                const slug = closeBtn.closest('[data-tag-chip]')?.dataset.tagSlug;
                if (slug && selectedTags.has(slug)) {
                    selectedTags.delete(slug);
                    applyFilters();
                }
                return;
            }

            const popularBtn = ev.target.closest('[data-popular-tag]');
            if (popularBtn) {
                ev.preventDefault();
                const slug = popularBtn.dataset.popularTag;
                if (!slug) {
                    return;
                }
                if (selectedTags.has(slug)) {
                    selectedTags.delete(slug);
                } else {
                    selectedTags.add(slug);
                }
                applyFilters();
                return;
            }

            const modeBtn = ev.target.closest('[data-tag-mode-toggle]');
            if (modeBtn) {
                ev.preventDefault();
                // Toggle between 'all' and 'any'
                mode = mode === 'all' ? 'any' : 'all';
                applyFilters();
            }
        });

        if (clearBtn) {
            clearBtn.addEventListener('click', ev => {
                ev.preventDefault();
                if (selectedTags.size === 0) {
                    return;
                }
                selectedTags.clear();
                applyFilters();
            });
        }

        if (tagInput) {
            tagInput.addEventListener('input', () => {
                clearTimeout(debounceHandle);
                const term = tagInput.value.trim();
                if (term.length < 2) {
                    renderSuggestions([]);
                    return;
                }
                debounceHandle = setTimeout(() => fetchSuggestions(term), 200);
            });

            tagInput.addEventListener('keydown', ev => {
                if (ev.key === 'Enter') {
                    ev.preventDefault();
                    if (currentSuggestions.length > 0) {
                        handleTagAdd(currentSuggestions[0].slug);
                    }
                } else if (ev.key === 'Backspace' && tagInput.value === '') {
                    // Backspace on empty input: remove last tag
                    if (selectedTags.size > 0) {
                        const tagsArray = Array.from(selectedTags);
                        const lastTag = tagsArray[tagsArray.length - 1];
                        selectedTags.delete(lastTag);
                        applyFilters();
                    }
                }
            });
        }

        if (suggestionsEl) {
            suggestionsEl.addEventListener('click', ev => {
                const option = ev.target.closest('[data-suggest-slug]');
                if (!option) {
                    return;
                }
                ev.preventDefault();
                renderSuggestions([]);
                if (tagInput) {
                    tagInput.value = '';
                }
                handleTagAdd(option.dataset.suggestSlug);
            });

            document.addEventListener('click', ev => {
                if (!suggestionsEl.contains(ev.target) && ev.target !== tagInput) {
                    renderSuggestions([]);
                }
            });
        }

        document.addEventListener('click', ev => {
            const tagBtn = ev.target.closest('[data-filter-tag]');
            if (!tagBtn) {
                return;
            }
            ev.preventDefault();
            const slug = tagBtn.dataset.filterTag;
            if (!slug) {
                return;
            }
            if (selectedTags.has(slug)) {
                return; // already active
            }
            selectedTags.add(slug);
            applyFilters();
        });
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
