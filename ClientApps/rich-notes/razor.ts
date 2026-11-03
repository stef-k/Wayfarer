import { createNotesEditor, isSafeNotesLink } from './editor';
import './editor.css';

/** Concrete Razor controls; save/recovery and explicit Clear semantics belong to the calling form. */
export const mountRazorNotes = (host: HTMLElement) => {
  const toolbar = document.getElementById('notes-toolbar')!;
  const status = document.getElementById('notes-feedback')!;
  const handle = createNotesEditor(host, host.dataset.notesContent ?? '', 'notes-label', message => { status.textContent = message; });
  const { editor } = handle;
  const lifecycle = new AbortController();
  const events = { signal: lifecycle.signal };
  let clearIntent = false;
  const changed = new Set<() => void>();
  handle.onChange(() => { clearIntent = false; changed.forEach(listener => listener()); });

  // Pointer actions keep the editor's selection; keyboard actions restore the last live selection as well.
  toolbar.addEventListener('pointerdown', event => {
    if ((event.target as Element).closest('button')) event.preventDefault();
  }, events);
  editor.on('selectionUpdate', handle.saveSelection);
  /** Keep controls descriptive when selection, undo or external adoption changes the active formatting. */
  const syncControls = () => {
    toolbar.querySelectorAll<HTMLButtonElement>('[data-mark]').forEach(button => {
      button.setAttribute('aria-pressed', String(editor.isActive(button.dataset.mark!)));
    });
    toolbar.querySelector<HTMLSelectElement>('[data-heading]')!.value = String(editor.getAttributes('heading').level ?? 0);
    const font = toolbar.querySelector<HTMLSelectElement>('[data-font]');
    if (font) font.value = editor.getAttributes('textStyle').fontFamily ?? '';
  };
  editor.on('transaction', () => { handle.saveSelection(); syncControls(); });
  syncControls();

  /** Native upstream commands retain their own history, list and selection behavior. */
  toolbar.addEventListener('click', event => {
    const action = (event.target as Element).closest<HTMLButtonElement>('button')?.dataset.action;
    if (!action) return;
    handle.restoreSelection();
    switch (action) {
      case 'bold': editor.chain().focus().toggleBold().run(); break;
      case 'italic': editor.chain().focus().toggleItalic().run(); break;
      case 'underline': editor.chain().focus().toggleUnderline().run(); break;
      case 'strike': editor.chain().focus().toggleStrike().run(); break;
      case 'ordered': editor.chain().focus().toggleOrderedList().run(); break;
      case 'bullet': editor.chain().focus().toggleBulletList().run(); break;
      case 'remove': handle.removeFormatting(); break;
      case 'clear':
        handle.clear(); clearIntent = true; changed.forEach(listener => listener()); break;
      case 'link': openDialog('link'); break;
      case 'image': openDialog('image'); break;
    }
  }, events);
  toolbar.querySelector<HTMLSelectElement>('[data-heading]')!.addEventListener('change', event => {
    const level = Number((event.target as HTMLSelectElement).value) as 1 | 2 | 3 | 4 | 5 | 6;
    handle.restoreSelection();
    if (level) editor.chain().focus().setHeading({ level }).run();
    else editor.chain().focus().setParagraph().run();
  }, events);
  toolbar.querySelector<HTMLSelectElement>('[data-font]')?.addEventListener('change', event => {
    const font = (event.target as HTMLSelectElement).value;
    handle.restoreSelection();
    if (font) editor.chain().focus().setFontFamily(font).run();
    else editor.chain().focus().unsetFontFamily().run();
  }, events);

  const dialog = document.getElementById('notes-dialog') as HTMLDialogElement;
  const urlInput = document.getElementById('notes-url') as HTMLInputElement;
  const title = document.getElementById('notes-dialog-title')!;
  const error = document.getElementById('notes-dialog-error')!;
  let kind: 'link' | 'image' = 'link';
  /** One labelled native dialog provides Escape, focus containment and explicit safe-follow actions. */
  function openDialog(next: 'link' | 'image') {
    handle.saveSelection(); kind = next;
    title.textContent = next === 'link' ? 'Edit link' : 'Insert image URL';
    urlInput.value = next === 'link' ? editor.getAttributes('link').href ?? '' : '';
    error.textContent = '';
    dialog.querySelectorAll<HTMLElement>('[data-link-only]').forEach(control => { control.hidden = next !== 'link'; });
    dialog.showModal(); urlInput.focus();
  }
  dialog.addEventListener('close', handle.restoreSelection, events);
  document.getElementById('notes-dialog-cancel')!.addEventListener('click', () => dialog.close(), events);
  document.getElementById('notes-dialog-apply')!.addEventListener('click', () => {
    const value = urlInput.value.trim();
    if (!value || kind === 'link' && !isSafeNotesLink(value)) { error.textContent = 'Enter a safe link or HTTP(S) image URL.'; return; }
    handle.restoreSelection();
    const success = kind === 'image' ? handle.insertImage(value)
      : editor.chain().focus().extendMarkRange('link').setLink({ href: value }).run();
    if (success) { handle.saveSelection(); dialog.close(); }
    else error.textContent = 'Image URL was rejected. Use an absolute HTTP(S) URL.';
  }, events);
  document.getElementById('notes-dialog-unlink')!.addEventListener('click', () => {
    handle.restoreSelection(); editor.chain().focus().extendMarkRange('link').unsetLink().run();
    handle.saveSelection(); dialog.close();
  }, events);
  document.getElementById('notes-dialog-follow')!.addEventListener('click', () => {
    const value = urlInput.value.trim();
    if (value && isSafeNotesLink(value)) window.open(value, '_blank', 'noopener,noreferrer');
    else error.textContent = 'Enter a safe link before following it.';
  }, events);
  return { ...handle, isClear: () => clearIntent, onChange: (listener: () => void) => { changed.add(listener); },
    adopt: (html: string) => { clearIntent = false; handle.adopt(html); },
    dispose: () => { lifecycle.abort(); changed.clear(); handle.dispose(); } };
};
