import { Editor } from '@tiptap/core';
import { EditorState } from '@tiptap/pm/state';
import StarterKit from '@tiptap/starter-kit';
import Image from '@tiptap/extension-image';
import TextAlign from '@tiptap/extension-text-align';
import { FontFamily, TextStyle } from '@tiptap/extension-text-style';
import { normalizeNotesHtml } from '../trip-editor/src/notes/notesHtml';
import { canonicalNoteImageSource, noteImageSourceForDisplay } from '../../wwwroot/js/util/notes-images.js';

/** Matches the server's link policy, including relative paths and control-obfuscated schemes. */
export const isSafeNotesLink = (value: string): boolean => {
  const clean = value.replace(/^[\u0000-\u0020\u007f-\u009f]+|[\u0000-\u0020\u007f-\u009f]+$/g, '')
    .replace(/[\t\r\n]/g, '');
  return !/^[^/?#]*:/.test(clean) || /^(https?|mailto|tel):/i.test(clean);
};

/** Canonical document URLs and display-only node views prevent direct origin loads on every ingress. */
const NotesImage = Image.extend({
  // Images are authored through the URL dialog or rich HTML paste, not Markdown input rules.
  addInputRules() { return []; },
  addNodeView() {
    return ({ node }) => {
      const image = document.createElement('img');
      const source = canonicalNoteImageSource(node.attrs.src);
      // Input rules/dragged slices are also untrusted; never assign data, relative or custom sources.
      if (/^https?:\/\//i.test(source)) image.src = noteImageSourceForDisplay(source);
      image.alt = node.attrs.alt ?? '';
      return { dom: image };
    };
  }
}).configure({ inline: true, allowBase64: false });

/** One native document/history per mount; feature owners retain persistence, Clear intent and recovery. */
export const createNotesEditor = (host: HTMLElement, value: string, labelId: string, feedback: (message: string) => void) => {
  const listeners = new Set<() => void>();
  const editor = new Editor({
    element: host,
    content: normalizeNotesHtml(value),
    parseOptions: { preserveWhitespace: 'full' },
    extensions: [
      StarterKit.configure({
        code: false, codeBlock: false, horizontalRule: false,
        // Automatic trailing-node insertion on focus must not become an authored Notes change.
        trailingNode: false,
        link: {
          openOnClick: false, autolink: false, linkOnPaste: false,
          isAllowedUri: isSafeNotesLink,
          HTMLAttributes: { target: null, rel: null }
        }
      }),
      NotesImage, TextStyle, FontFamily,
      TextAlign.configure({ types: ['heading', 'paragraph'], alignments: ['left', 'center', 'right', 'justify'] })
    ],
    editorProps: {
      attributes: { role: 'textbox', 'aria-multiline': 'true', 'aria-labelledby': labelId, class: 'rich-notes-content' },
      transformPastedHTML: html => normalizeNotesHtml(html)
    },
    onUpdate: () => listeners.forEach(listener => listener())
  });
  let original = value;
  let initialDocument = editor.state.doc;
  let savedSelection = editor.state.selection;
  let savedMarks = editor.state.storedMarks;

  /** Reject file/data images before native parsing or live insertion, with visible feedback. */
  function rejectEmbeddedImages(transfer: DataTransfer | null): boolean {
    const html = transfer?.getData('text/html') ?? '';
    if (!transfer?.files.length && !/data\s*:\s*image/i.test(html.replace(/[\u0000-\u0020]/g, ''))) return false;
    feedback('Pasted or dropped files and embedded images are unsupported. Use an HTTP(S) image URL.');
    return true;
  }

  /** File-only drops may have no ProseMirror slice, so reject at the native capture boundary. */
  const rejectTransfer = (event: Event) => {
    const transfer = event instanceof ClipboardEvent ? event.clipboardData
      : event instanceof DragEvent ? event.dataTransfer : null;
    if (!rejectEmbeddedImages(transfer)) return;
    event.preventDefault();
    event.stopPropagation();
  };
  host.addEventListener('paste', rejectTransfer, true);
  host.addEventListener('drop', rejectTransfer, true);

  /** Image errors leave the canonical reference intact, including already-stored failed references. */
  const imageError = (event: Event) => {
    if (event.target instanceof HTMLImageElement) feedback('Image preview unavailable. Its URL is retained; no direct image connection was attempted.');
  };
  host.addEventListener('error', imageError, true);

  return {
    editor,
    /** Mount/focus and native undo to the initial document retain the published input exactly. */
    getValue: () => editor.state.doc.eq(initialDocument) ? original : normalizeNotesHtml(editor.getHTML()),
    /** External adoption is explicit and resets live vendor history; it never emits an authored change. */
    adopt: (html: string) => {
      editor.commands.setContent(normalizeNotesHtml(html), { emitUpdate: false, parseOptions: { preserveWhitespace: 'full' } });
      original = html;
      initialDocument = editor.state.doc;
      editor.view.updateState(EditorState.create({ doc: initialDocument, schema: editor.schema, plugins: editor.state.plugins }));
      savedSelection = editor.state.selection;
      savedMarks = editor.state.storedMarks;
    },
    onChange: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    focus: () => editor.commands.focus(),
    clear: () => editor.commands.clearContent(),
    /** Dialogs retain selection and pending marks, including explicit empty and null mark states. */
    saveSelection: () => { savedSelection = editor.state.selection; savedMarks = editor.state.storedMarks; },
    restoreSelection: () => {
      editor.view.dispatch(editor.state.tr.setSelection(savedSelection).setStoredMarks(savedMarks));
      editor.commands.focus();
    },
    /** Compose maintained commands; references and caret-adjacent text remain untouched. */
    removeFormatting: () => {
      let command = editor.chain().focus().unsetBold().unsetItalic().unsetUnderline().unsetStrike().unsetMark('textStyle');
      if (!editor.state.selection.empty) command = command.clearNodes().unsetTextAlign();
      return command.run();
    },
    /** Reject unsupported sources before the image extension creates a live node. */
    insertImage: (value: string) => {
      const source = canonicalNoteImageSource(value);
      try {
        const url = new URL(source);
        if (!['http:', 'https:'].includes(url.protocol)) throw new Error('scheme');
      } catch { feedback('Enter an absolute HTTP(S) image URL.'); return false; }
      return editor.chain().focus().setImage({ src: source }).run();
    },
    dispose: () => {
      host.removeEventListener('paste', rejectTransfer, true);
      host.removeEventListener('drop', rejectTransfer, true);
      host.removeEventListener('error', imageError, true);
      listeners.clear();
      editor.destroy();
    }
  };
};

export type NotesEditor = ReturnType<typeof createNotesEditor>;
