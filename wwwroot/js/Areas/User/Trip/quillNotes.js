export const setupQuill = async (selector = '#notes', inputSelector = '#Notes', formSelector = '#trip-form') => {
    // ✅ Ensure Quill is available (handles defer'd script loading)
    while (typeof window.Quill === 'undefined') {
        await new Promise(resolve => setTimeout(resolve, 50));
    }
    
    const ImageBlot = Quill.import('formats/image');

    class CustomImage extends ImageBlot {
        static create(value) {
            const node = super.create(value.url || value);
            if (value.class) {
                node.setAttribute('class', value.class);
            }
            return node;
        }

        static value(node) {
            return {
                url: node.getAttribute('src'),
                class: node.getAttribute('class'),
            };
        }
    }

    CustomImage.blotName = 'image';
    CustomImage.tagName = 'img';

    Quill.register(CustomImage, true);

    const container = document.querySelector(selector);
    if (!container) return;

    const quill = new Quill(container, {
        theme: 'snow',
        placeholder: 'Add your notes...',
        modules: {
            clipboard: { matchVisual: false },
            toolbar: [
                [{ header: [1, 2, 3, 4, 5, 6, false] }],
                ['bold', 'italic', 'underline'],
                [{ list: 'ordered' }, { list: 'bullet' }],
                ['link', 'image'],
                [{ font: [] }],
                ['clean']
            ]
        }
    });

    const proxyImages = root => {
        root.querySelectorAll('img').forEach(img => {
            // skip data: URIs, and skip any <img> that we've already proxied
            if (img.src.startsWith('data:') || img.dataset.original) return;

            // mark original URL, then swap to proxy
            img.dataset.original = img.src;
            img.src = `/Public/ProxyImage?url=${encodeURIComponent(img.dataset.original)}`;
        });
    };

    const getCleanedHtml = () => {
        const html = quill.root.innerHTML.trim();
        return html === '<p><br></p>' ? '' : html;
    };

    // 6) whenever text changes, remove stray data-uris *and* re-proxy
    quill.on('text-change', () => {
        const editor = container.querySelector('.ql-editor');

        // remove any data-uri embeds
        editor.querySelectorAll('img').forEach(img => {
            if (img.src.startsWith('data:image')) img.remove();
        });

        // only proxy new external images
        proxyImages(editor);

        // sync hidden input
        document.querySelector(inputSelector).value = getCleanedHtml();
    });

    // 7) paste initial HTML + then proxy any images in it
    if (container.dataset.notesContent) {
        quill.root.innerHTML = container.dataset.notesContent;
        proxyImages(quill.root);
        document.querySelector(inputSelector).value = getCleanedHtml();
    }

    const toolbar = quill.getModule('toolbar');
    const clearText = document.createElement('span');
    clearText.classList.add('ql-clear');
    clearText.innerText = 'Clear';
    clearText.setAttribute('data-tooltip', 'Clear editor text');
    toolbar.container.appendChild(clearText);
    clearText.addEventListener('click', () => quill.setText(''));

    toolbar.addHandler('image', () => {
        const modalEl = document.getElementById('imageModal');
        const modal = new bootstrap.Modal(modalEl);
        modal.show();

        const insertBtn = document.getElementById('insertImageBtn');

        const handler = () => {
            const imageUrl = document.getElementById('imageUrl').value;
            if (!imageUrl) return alert('Please enter a valid image URL.');

            if (savedRange) {
                quill.setSelection(savedRange.index, savedRange.length);
                quill.insertEmbed(savedRange.index, 'image', {
                    url: imageUrl,
                    class: 'trip-img-modal'
                });
            } else {
                quill.insertEmbed(quill.getLength(), 'image', imageUrl);
            }
            
            modal.hide();
        };

        // Always remove existing handler first
        insertBtn.removeEventListener('click', insertBtn._quillHandler);
        insertBtn._quillHandler = handler;
        insertBtn.addEventListener('click', handler);
    });


    let savedRange = null;
    quill.on('selection-change', range => {
        if (range) savedRange = range;
    });

    const insertBtn = document.getElementById('insertImageBtn');
    if (insertBtn && !insertBtn.dataset.bound) {
        insertBtn.addEventListener('click', () => {
            const imageUrl = document.getElementById('imageUrl').value;
            if (!imageUrl) return alert('Please enter a valid image URL.');

            if (savedRange) {
                quill.setSelection(savedRange.index, savedRange.length);
                quill.insertEmbed(savedRange.index, 'image', imageUrl);
            } else {
                quill.insertEmbed(quill.getLength(), 'image', imageUrl);
            }

            bootstrap.Modal.getInstance(document.getElementById('imageModal')).hide();
        });
        insertBtn.dataset.bound = 'true';
    }

    const imageModal = document.getElementById('imageModal');
    if (imageModal && !imageModal.dataset.bound) {
        imageModal.addEventListener('hidden.bs.modal', () => {
            document.getElementById('imageUrl').value = '';
        });
        imageModal.dataset.bound = 'true';
    }

    document.querySelector(formSelector)?.addEventListener('submit', () => {
        document.querySelector(inputSelector).value = getCleanedHtml();
    });
};

export const waitForQuill = async (selector, maxRetries = 10, delay = 100) => {
    for (let i = 0; i < maxRetries; i++) {
        const container = document.querySelector(selector);
        if (container && window.Quill) return;
        await new Promise(r => setTimeout(r, delay));
    }
    console.warn(`⚠️ Quill or container "${selector}" not ready after ${maxRetries} attempts.`);
};
