<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue';
import { confirmDialogState, settleConfirmDialog } from '../composables/useConfirmDialog';

import RegionDeletionHoldNotice from './RegionDeletionHoldNotice.vue';
import RegionDeletionChoices from './RegionDeletionChoices.vue';
import { regionDeletionLabel } from './regionDeletionState';

const inertBackground = new Map<HTMLElement, boolean>();
/// Reuses the confirmation host's focus trap while making the Region dialog background inert.
const restoreBackground = (): void => { inertBackground.forEach((value, element) => { element.inert = value; }); inertBackground.clear(); };
onUnmounted(restoreBackground);
const dialogElement = ref<HTMLElement | null>(null);
const confirmButton = ref<HTMLButtonElement | null>(null);
const cancelButton = ref<HTMLButtonElement | null>(null);

const dialog = computed(() => confirmDialogState.value);
const titleId = computed(() => (dialog.value ? `trip-editor-confirm-title-${dialog.value.id}` : undefined));
const bodyId = computed(() => (dialog.value ? `trip-editor-confirm-body-${dialog.value.id}` : undefined));
const confirmClass = computed(() => {
  if (dialog.value?.variant === 'danger') {
    return 'btn-danger';
  }

  if (dialog.value?.variant === 'warning') {
    return 'btn-warning';
  }

  return 'btn-primary';
});
const variantClass = computed(() => (dialog.value ? `trip-editor-confirm-dialog--${dialog.value.variant}` : ''));

watch(
  () => dialog.value?.id,
  async id => {
    restoreBackground();
    if (!id) {
      return;
    }

    await nextTick();
    if (dialog.value?.regionDeletion) {
      const overlay = dialogElement.value?.closest('.trip-editor-confirm');
      for (const element of Array.from(document.body.children)) {
        if (element instanceof HTMLElement && element !== overlay) { inertBackground.set(element, element.inert); element.inert = true; }
      }
    }
    const initialButton = dialog.value?.variant === 'default' ? confirmButton.value : cancelButton.value;
    initialButton?.focus();
  }
);

const cancel = (): void => {
  if (dialog.value?.regionDeletion?.state.submitting) return;
  settleConfirmDialog(false);
};

const confirmChoice = async (): Promise<void> => {
  const controller = dialog.value?.regionDeletion;
  if (controller && !(await controller.submit())) return;
  settleConfirmDialog(true);
};

const onDialogKeydown = (event: KeyboardEvent): void => {
  if (event.key === 'Escape') {
    event.preventDefault();
    cancel();
    return;
  }

  if (event.key !== 'Tab') {
    return;
  }

  const focusable = getFocusableElements();
  if (focusable.length === 0) {
    event.preventDefault();
    return;
  }

  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  const active = document.activeElement;

  if (event.shiftKey && active === first) {
    event.preventDefault();
    last.focus();
    return;
  }

  if (!event.shiftKey && active === last) {
    event.preventDefault();
    first.focus();
  }
};

/// Finds controls currently reachable inside this confirmation dialog for keyboard trapping.
function getFocusableElements(): HTMLElement[] {
  if (!dialogElement.value) {
    return [];
  }

  return Array.from(
    dialogElement.value.querySelectorAll<HTMLElement>(
      'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
    )
  ).filter(element => element.offsetParent !== null);
}
</script>

<template>
  <RegionDeletionHoldNotice />
  <Teleport to="body">
    <div v-if="dialog" class="trip-editor-confirm">
      <div class="modal-backdrop fade show trip-editor-confirm__backdrop" aria-hidden="true"></div>
      <div class="modal fade show trip-editor-confirm__modal" tabindex="-1" @keydown="onDialogKeydown">
        <div
          ref="dialogElement"
          class="modal-dialog modal-dialog-centered modal-dialog-scrollable trip-editor-confirm-dialog"
          :class="variantClass"
          role="dialog"
          aria-modal="true"
          :aria-labelledby="titleId"
          :aria-describedby="bodyId"
        >
          <div class="modal-content trip-editor-confirm-dialog__content">
            <div class="modal-header trip-editor-confirm-dialog__header">
              <h2 :id="titleId" class="modal-title trip-editor-confirm-dialog__title">{{ dialog.title }}</h2>
            </div>
            <div :id="bodyId" class="modal-body trip-editor-confirm-dialog__body">
              <p>{{ dialog.message }}</p>
              <RegionDeletionChoices v-if="dialog.regionDeletion" :controller="dialog.regionDeletion" />
            </div>
            <div class="modal-footer trip-editor-confirm-dialog__footer">
              <button ref="cancelButton" type="button" class="btn btn-secondary" :disabled="dialog.regionDeletion?.state.submitting" @click="cancel">{{ dialog.cancelLabel }}</button>
              <button ref="confirmButton" type="button" class="btn" :class="confirmClass" :disabled="Boolean(dialog.regionDeletion && (!dialog.regionDeletion.state.preview || dialog.regionDeletion.state.loading || dialog.regionDeletion.state.submitting || dialog.regionDeletion.state.held))" @click="confirmChoice">{{ dialog.regionDeletion ? regionDeletionLabel(dialog.regionDeletion.state) : dialog.confirmLabel }}</button>
            </div>
          </div>
        </div>
      </div>
    </div>
  </Teleport>
</template>
