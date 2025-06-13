// quillNotes.js
// Initializes the Quill editor and syncs its content with the hidden input

export const setupQuill = () => {
    const quill = new Quill('#notes', {
        theme: 'snow',
        placeholder: 'Add your notes...',
        modules: {
            clipboard: {
                matchVisual: false
            },
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

    // Remove base64 images if pasted
    quill.on("text-change", () => {
        const editor = document.querySelector(".ql-editor");
        const images = editor.querySelectorAll("img");
        images.forEach(img => {
            if (img.src.startsWith("data:image")) img.remove();
        });
    });

    // Custom clear button
    const toolbar = quill.getModule('toolbar');
    const clearText = document.createElement('span');
    clearText.classList.add('ql-clear');
    clearText.innerText = 'Clear';
    clearText.setAttribute('data-tooltip', 'Clear editor text');
    toolbar.container.appendChild(clearText);

    clearText.addEventListener('click', () => quill.setText(''));

    // Hook up image button to modal
    toolbar.addHandler('image', () => {
        const modal = new bootstrap.Modal(document.getElementById('imageModal'));
        modal.show();
    });

    let savedRange = null;
    quill.on('selection-change', range => {
        if (range) savedRange = range;
    });

    document.getElementById('insertImageBtn')?.addEventListener('click', () => {
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

    document.getElementById('imageModal')?.addEventListener('hidden.bs.modal', () => {
        document.getElementById('imageUrl').value = '';
    });

    // Set initial content from data attribute
    const notesElement = document.getElementById('notes');
    const notesContent = notesElement.dataset.notesContent;
    if (notesContent) quill.root.innerHTML = notesContent;

    // Sync hidden input on form submit
    document.getElementById("trip-form")?.addEventListener("submit", () => {
        document.getElementById("hiddenNotes").value = quill.root.innerHTML;
    });
};
