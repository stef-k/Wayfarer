<script setup lang="ts">
import Quill, { type Range } from 'quill';
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { displayImageSource } from '../displayHelpers';
import { canonicalImageSource, containsDataImageReference, isDataImageSource, isUnsafeImageSource, normalizeNotesHtml } from '../notes/notesHtml';

const props = defineProps<{
  editorId: string;
  label: string;
  modelValue: string;
  validationMessages: string[];
}>();

const emit = defineEmits<{
  'update:modelValue': [value: string];
}>();

const editorHost = ref<HTMLDivElement | null>(null);
const feedbackMessage = ref('');
const isImageDialogOpen = ref(false);
const imageUrl = ref('');
const imageUrlError = ref('');
const imageUrlInput = ref<HTMLInputElement | null>(null);
const labelId = computed(() => `${props.editorId}-label`);
const feedbackId = computed(() => `${props.editorId}-feedback`);
let quill: Quill | null = null;
let savedRange: Range | null = null;
let isLoadingExternalValue = false;
let feedbackTimer: number | null = null;
const pendingInsertedImageSources = new Set<string>();

const toolbarOptions = [
  [{ header: [1, 2, 3, 4, 5, 6, false] }],
  ['bold', 'italic', 'underline'],
  [{ list: 'ordered' }, { list: 'bullet' }],
  ['link', 'image'],
  [{ font: [] }],
  ['clean']
];

onMounted(() => {
  if (!editorHost.value) {
    return;
  }

  quill = new Quill(editorHost.value, {
    modules: {
      clipboard: { matchVisual: false },
      toolbar: {
        container: toolbarOptions,
        handlers: {
          image: openImageDialog
        }
      }
    },
    placeholder: 'Add your notes...',
    theme: 'snow'
  });

  quill.on('selection-change', handleSelectionChange);
  quill.on('text-change', handleTextChange);
  quill.root.addEventListener('paste', handlePaste);
  quill.root.addEventListener('drop', handleDrop);
  quill.root.addEventListener('input', handleInput);
  quill.root.addEventListener('load', handleImageLoad, true);
  quill.root.addEventListener('error', handleImageLoadFailure, true);
  loadHtml(props.modelValue);
});

onUnmounted(() => {
  if (feedbackTimer !== null) {
    window.clearTimeout(feedbackTimer);
  }

  if (!quill) {
    return;
  }

  quill.root.removeEventListener('paste', handlePaste);
  quill.root.removeEventListener('drop', handleDrop);
  quill.root.removeEventListener('input', handleInput);
  quill.root.removeEventListener('load', handleImageLoad, true);
  quill.root.removeEventListener('error', handleImageLoadFailure, true);
  quill.off('selection-change', handleSelectionChange);
  quill.off('text-change', handleTextChange);
  pendingInsertedImageSources.clear();
  quill = null;
});

watch(
  () => props.modelValue,
  value => {
    if (!quill || normalizeNotesHtml(value) === currentHtml()) {
      return;
    }

    loadHtml(value);
  }
);

function loadHtml(value: string): void {
  if (!quill) {
    return;
  }

  isLoadingExternalValue = true;
  quill.setContents([], 'silent');
  quill.clipboard.dangerouslyPasteHTML(normalizeNotesHtml(value), 'silent');
  normalizeEditorImagesForDisplay();
  ensureEditableContinuationLine();
  isLoadingExternalValue = false;
}

function handleSelectionChange(range: Range | null): void {
  if (range) {
    savedRange = range;
  }
}

function handleTextChange(): void {
  if (!quill || isLoadingExternalValue) {
    return;
  }

  if (normalizeEditorImagesForDisplay()) {
    showFeedback('Embedded data images are not allowed. Use an external image URL.');
  }

  ensureEditableContinuationLine();
  emit('update:modelValue', currentHtml());
}

/// Adds a real editor-local trailing line so users can click and type after terminal rich content.
function ensureEditableContinuationLine(): void {
  if (!quill) {
    return;
  }

  if (!normalizeNotesHtml(quill.root.innerHTML) || isLastEditorBlockBlank()) {
    return;
  }

  quill.insertText(Math.max(0, quill.getLength() - 1), '\n', 'silent');
}

function handlePaste(event: ClipboardEvent): void {
  const clipboard = event.clipboardData;
  if (!clipboard || !containsDataImage(clipboard)) {
    return;
  }

  event.preventDefault();
  showFeedback('Embedded data images are not allowed. Use an external image URL.');
}

function handleDrop(event: DragEvent): void {
  const transfer = event.dataTransfer;
  if (!transfer || !containsDataImage(transfer)) {
    return;
  }

  event.preventDefault();
  showFeedback('Embedded data images are not allowed. Use an external image URL.');
}

function handleInput(): void {
  if (isLoadingExternalValue) {
    return;
  }

  if (normalizeEditorImagesForDisplay()) {
    emit('update:modelValue', currentHtml());
    showFeedback('Embedded data images are not allowed. Use an external image URL.');
  }
}

function isLastEditorBlockBlank(): boolean {
  if (!quill || quill.root.children.length === 0) {
    return false;
  }

  const lastBlock = quill.root.children.item(quill.root.children.length - 1);
  if (!(lastBlock instanceof HTMLElement)) {
    return false;
  }

  return !lastBlock.textContent?.trim() && !lastBlock.querySelector('img, video, iframe');
}

function containsDataImage(data: DataTransfer): boolean {
  if (Array.from(data.files).some(file => file.type.startsWith('image/'))) {
    return true;
  }

  return ['text/html', 'text/plain'].some(type => containsDataImageReference(data.getData(type) ?? ''));
}

function openImageDialog(): void {
  if (!quill) {
    return;
  }

  savedRange = quill.getSelection() ?? savedRange;
  imageUrl.value = '';
  imageUrlError.value = '';
  isImageDialogOpen.value = true;
  void nextTick(() => imageUrlInput.value?.focus());
}

function closeImageDialog(): void {
  isImageDialogOpen.value = false;
  imageUrl.value = '';
  imageUrlError.value = '';
}

function insertImageUrl(): void {
  if (!quill) {
    return;
  }

  const url = imageUrl.value.trim();
  if (isDataImageSource(url)) {
    imageUrlError.value = 'Embedded data images are not allowed. Use an external image URL.';
    showFeedback('Embedded data images are not allowed. Use an external image URL.');
    return;
  }

  if (!isExternalImageUrl(url)) {
    imageUrlError.value = 'Enter an http or https image URL.';
    return;
  }

  const index = savedRange ? savedRange.index : Math.max(0, quill.getLength() - 1);
  pendingInsertedImageSources.add(canonicalImageSource(url));
  quill.setSelection(index, savedRange?.length ?? 0, 'silent');
  quill.insertEmbed(index, 'image', url, 'user');
  quill.setSelection(index + 1, 0, 'silent');
  normalizeEditorImagesForDisplay();
  emit('update:modelValue', currentHtml());
  closeImageDialog();
}

function isExternalImageUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function currentHtml(): string {
  if (!quill) {
    return normalizeNotesHtml(props.modelValue);
  }

  return normalizeNotesHtml(quill.root.innerHTML);
}

/// Keeps editor display images proxied while preserving canonical external URLs in emitted HTML.
function normalizeEditorImagesForDisplay(): boolean {
  if (!quill) {
    return false;
  }

  let removed = false;
  quill.root.querySelectorAll<HTMLImageElement>('img').forEach(image => {
    const source = canonicalImageSource(image.getAttribute('src') ?? '');
    if (isUnsafeImageSource(source)) {
      image.remove();
      removed = true;
      return;
    }

    image.setAttribute('src', displayImageSource(source));
  });
  return removed;
}

function handleImageLoad(event: Event): void {
  if (event.target instanceof HTMLImageElement) {
    pendingInsertedImageSources.delete(canonicalImageSource(event.target.getAttribute('src') ?? ''));
  }
}

function handleImageLoadFailure(event: Event): void {
  if (!quill || !(event.target instanceof HTMLImageElement)) {
    return;
  }

  const source = canonicalImageSource(event.target.getAttribute('src') ?? '');
  if (!pendingInsertedImageSources.delete(source)) {
    return;
  }

  event.target.remove();
  ensureEditableContinuationLine();
  emit('update:modelValue', currentHtml());
  showFeedback('Image URL could not be loaded. Check that it points to a reachable image file.');
}

function showFeedback(message: string): void {
  feedbackMessage.value = message;
  if (feedbackTimer !== null) {
    window.clearTimeout(feedbackTimer);
  }

  feedbackTimer = window.setTimeout(() => {
    feedbackMessage.value = '';
    feedbackTimer = null;
  }, 5000);
}
</script>

<template>
  <div class="trip-editor-field trip-editor-rich-notes" :data-rich-notes-editor="props.editorId">
    <span :id="labelId">{{ props.label }}</span>
    <div
      :id="props.editorId"
      ref="editorHost"
      class="trip-editor-rich-notes__editor"
      role="group"
      :aria-labelledby="labelId"
      :aria-describedby="feedbackMessage ? feedbackId : undefined"
    ></div>
    <p v-if="feedbackMessage" :id="feedbackId" class="trip-editor-rich-notes__feedback" role="status">{{ feedbackMessage }}</p>
    <small v-for="message in props.validationMessages" :key="message">{{ message }}</small>

    <Teleport to="body">
      <div v-if="isImageDialogOpen" class="trip-editor-rich-notes-dialog" role="dialog" aria-modal="true" aria-labelledby="trip-editor-rich-notes-image-title">
        <div class="trip-editor-rich-notes-dialog__backdrop" aria-hidden="true"></div>
        <form class="trip-editor-rich-notes-dialog__panel" novalidate @submit.prevent="insertImageUrl">
          <h2 id="trip-editor-rich-notes-image-title">Insert image URL</h2>
          <label class="trip-editor-field">
            <span>Image URL</span>
            <input ref="imageUrlInput" v-model="imageUrl" type="url" autocomplete="off" />
            <small v-if="imageUrlError">{{ imageUrlError }}</small>
          </label>
          <div class="trip-editor-rich-notes-dialog__actions">
            <button type="submit" class="btn btn-primary btn-sm">Insert Image</button>
            <button type="button" class="btn btn-outline-secondary btn-sm" @click="closeImageDialog">Cancel</button>
          </div>
        </form>
      </div>
    </Teleport>
  </div>
</template>
