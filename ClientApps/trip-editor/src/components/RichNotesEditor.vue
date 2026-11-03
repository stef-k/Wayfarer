<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue';
import { createNotesEditor, isSafeNotesLink, type NotesEditor } from '../../../rich-notes/editor';
import '../../../rich-notes/editor.css';

const props = defineProps<{
  editorId: string;
  label: string;
  modelValue: string;
  validationMessages: string[];
}>();
const emit = defineEmits<{ 'update:modelValue': [value: string] }>();

// Internal IDs remain unique even when two mounted forms use the same public editor key.
const instanceId = `${props.editorId}-${useId()}`;
const labelId = `${instanceId}-label`;
const feedbackId = `${instanceId}-feedback`;
const validationId = `${instanceId}-validation`;
const descriptionId = computed(() => [props.validationMessages.length ? validationId : '', feedbackMessage.value ? feedbackId : ''].filter(Boolean).join(' '));
const editorHost = ref<HTMLDivElement | null>(null);
const dialog = ref<HTMLDialogElement | null>(null);
const urlInput = ref<HTMLInputElement | null>(null);
const dialogKind = ref<'link' | 'image'>('image');
const url = ref('');
const dialogError = ref('');
const feedbackMessage = ref('');
const controls = ref({ bold: false, italic: false, underline: false, ordered: false, bullet: false, heading: '0', font: '', align: 'left' });
let handle: NotesEditor | null = null;
// Only values emitted since the last parent acknowledgement can be echoes. Retired values
// never suppress a later owner reset; external adoption uses the engine's explicit history reset.
const pendingEchoes: string[] = [];

/** Preserve the public focus contract used by Segment Reset and surface navigation. */
function focusEditor(): void { handle?.focus(); }
defineExpose({ focusEditor });

/** Reflect native selection/marks without making the editor instance reactive. */
function syncControls(): void {
  if (!handle) return;
  const editor = handle.editor;
  controls.value = {
    bold: editor.isActive('bold'), italic: editor.isActive('italic'), underline: editor.isActive('underline'),
    ordered: editor.isActive('orderedList'), bullet: editor.isActive('bulletList'),
    heading: String(editor.getAttributes('heading').level ?? 0),
    font: editor.getAttributes('textStyle').fontFamily ?? '',
    align: editor.getAttributes('paragraph').textAlign ?? editor.getAttributes('heading').textAlign ?? 'left'
  };
  handle.saveSelection();
}

onMounted(() => {
  handle = createNotesEditor(editorHost.value!, props.modelValue, labelId, message => { feedbackMessage.value = message; });
  handle.onChange(() => {
    const value = handle!.getValue();
    pendingEchoes.push(value);
    emit('update:modelValue', value);
  });
  handle.editor.on('transaction', syncControls);
  syncControls();
});

watch(() => props.modelValue, value => {
  if (!handle) return;
  const echo = pendingEchoes.lastIndexOf(value);
  if (echo >= 0) {
    pendingEchoes.splice(0, echo + 1);
    return;
  }
  if (value === handle.getValue()) return;
  pendingEchoes.length = 0;
  dialog.value?.close();
  handle.adopt(value);
  syncControls();
});

onBeforeUnmount(() => {
  const retiring = handle;
  handle = null;
  dialog.value?.close();
  retiring?.dispose();
  pendingEchoes.length = 0;
});

/** Pointer controls retain the live range; keyboard controls restore the last native selection. */
function keepSelection(event: PointerEvent): void {
  if ((event.target as Element).closest('button')) event.preventDefault();
}

/** The Trip toolbar keeps its existing marks, lists, fonts and alignment capabilities. */
function command(action: string): void {
  if (!handle) return;
  handle.restoreSelection();
  const editor = handle.editor;
  switch (action) {
    case 'bold': editor.chain().focus().toggleBold().run(); break;
    case 'italic': editor.chain().focus().toggleItalic().run(); break;
    case 'underline': editor.chain().focus().toggleUnderline().run(); break;
    case 'ordered': editor.chain().focus().toggleOrderedList().run(); break;
    case 'bullet': editor.chain().focus().toggleBulletList().run(); break;
    case 'remove': handle.removeFormatting(); break;
  }
}

/** Apply only the finite, server-supported block/font choices. */
function choose(kind: 'heading' | 'font' | 'align', event: Event): void {
  if (!handle) return;
  const value = (event.target as HTMLSelectElement).value;
  handle.restoreSelection();
  const chain = handle.editor.chain().focus();
  if (kind === 'heading') {
    if (value === '0') chain.setParagraph().run();
    else chain.setHeading({ level: Number(value) as 1 | 2 | 3 | 4 | 5 | 6 }).run();
  } else if (kind === 'font') {
    if (value) chain.setFontFamily(value).run();
    else chain.unsetFontFamily().run();
  } else chain.setTextAlign(value).run();
}

/** A component-owned native dialog isolates selection, Escape, focus and URL feedback. */
function openDialog(kind: 'link' | 'image'): void {
  if (!handle) return;
  handle.saveSelection();
  dialogKind.value = kind;
  url.value = kind === 'link' ? handle.editor.getAttributes('link').href ?? '' : '';
  dialogError.value = '';
  dialog.value?.showModal();
  urlInput.value?.focus();
}

/** Restore focus on Apply, Cancel and native Escape; never focus a retired editor. */
function closeDialog(): void { handle?.restoreSelection(); }

/** Validate before insertion; the shared engine owns canonical images and secure proxy display. */
function applyUrl(): void {
  if (!handle) return;
  const value = url.value.trim();
  if (!value || dialogKind.value === 'link' && !isSafeNotesLink(value)) {
    dialogError.value = 'Enter a safe link or HTTP(S) image URL.';
    return;
  }
  handle.restoreSelection();
  const success = dialogKind.value === 'image' ? handle.insertImage(value)
    : handle.editor.chain().focus().extendMarkRange('link').setLink({ href: value }).run();
  if (!success) { dialogError.value = 'Enter an absolute HTTP(S) image URL.'; return; }
  handle.saveSelection();
  dialog.value?.close();
}

/** Unlink preserves selected text; explicit Follow never navigates the editing surface. */
function unlink(): void {
  if (!handle) return;
  handle.restoreSelection();
  handle.editor.chain().focus().extendMarkRange('link').unsetLink().run();
  handle.saveSelection();
  dialog.value?.close();
}
function followLink(): void {
  const value = url.value.trim();
  if (value && isSafeNotesLink(value)) window.open(value, '_blank', 'noopener,noreferrer');
  else dialogError.value = 'Enter a safe link before following it.';
}
</script>

<template>
  <div class="trip-editor-field trip-editor-rich-notes" :data-rich-notes-editor="props.editorId">
    <span :id="labelId">{{ props.label }}</span>
    <div class="rich-notes-toolbar" role="toolbar" :aria-label="`${props.label} formatting`" @pointerdown="keepSelection">
      <select aria-label="Heading" :value="controls.heading" @change="choose('heading', $event)">
        <option value="0">Paragraph</option><option v-for="level in 6" :key="level" :value="level">Heading {{ level }}</option>
      </select>
      <button type="button" :aria-pressed="controls.bold" @click="command('bold')">Bold</button>
      <button type="button" :aria-pressed="controls.italic" @click="command('italic')">Italic</button>
      <button type="button" :aria-pressed="controls.underline" @click="command('underline')">Underline</button>
      <button type="button" :aria-pressed="controls.ordered" @click="command('ordered')">Ordered list</button>
      <button type="button" :aria-pressed="controls.bullet" @click="command('bullet')">Bullet list</button>
      <select aria-label="Alignment" :value="controls.align" @change="choose('align', $event)">
        <option value="left">Left</option><option value="center">Center</option><option value="right">Right</option>
      </select>
      <button type="button" @click="openDialog('link')">Link</button>
      <button type="button" @click="openDialog('image')">Image</button>
      <select aria-label="Font" :value="controls.font" @change="choose('font', $event)">
        <option value="">Default font</option><option value="serif">Serif</option><option value="monospace">Monospace</option>
      </select>
      <button type="button" @click="command('remove')">Remove formatting</button>
    </div>
    <div :id="instanceId" ref="editorHost" class="trip-editor-rich-notes__editor" role="group" :aria-labelledby="labelId"
      :aria-describedby="descriptionId || undefined" :aria-invalid="props.validationMessages.length ? true : undefined"></div>
    <small>Older formatting may need adjustment when edited. Use Image to insert an HTTP(S) image URL.</small>
    <p v-if="feedbackMessage" :id="feedbackId" class="trip-editor-rich-notes__feedback" role="status">{{ feedbackMessage }}</p>
    <div v-if="props.validationMessages.length" :id="validationId">
      <small v-for="message in props.validationMessages" :key="message">{{ message }}</small>
    </div>
    <Teleport to="body">
      <dialog ref="dialog" class="trip-editor-rich-notes-dialog" :aria-labelledby="`${instanceId}-dialog-title`" @close="closeDialog">
        <form class="trip-editor-rich-notes-dialog__panel" novalidate @submit.prevent="applyUrl">
          <h2 :id="`${instanceId}-dialog-title`">{{ dialogKind === 'image' ? 'Insert image URL' : 'Edit link' }}</h2>
          <label class="trip-editor-field">
            <span>{{ dialogKind === 'image' ? 'Image URL' : 'Link URL' }}</span>
            <input ref="urlInput" v-model="url" type="text" autocomplete="off" :aria-describedby="dialogError ? `${instanceId}-dialog-error` : undefined" />
          </label>
          <p v-if="dialogError" :id="`${instanceId}-dialog-error`" role="alert">{{ dialogError }}</p>
          <div class="trip-editor-rich-notes-dialog__actions">
            <button type="submit" class="btn btn-primary btn-sm">{{ dialogKind === 'image' ? 'Insert Image' : 'Apply' }}</button>
            <button v-if="dialogKind === 'link'" type="button" class="btn btn-outline-secondary btn-sm" @click="unlink">Unlink</button>
            <button v-if="dialogKind === 'link'" type="button" class="btn btn-outline-secondary btn-sm" @click="followLink">Follow link</button>
            <button type="button" class="btn btn-outline-secondary btn-sm" @click="dialog?.close()">Cancel</button>
          </div>
        </form>
      </dialog>
    </Teleport>
  </div>
</template>
