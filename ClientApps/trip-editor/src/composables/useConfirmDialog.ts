import { nextTick, readonly, ref } from 'vue';

export type ConfirmDialogVariant = 'default' | 'warning' | 'danger';

export type ConfirmDialogOptions = {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  variant?: ConfirmDialogVariant;
};

export type ActiveConfirmDialog = Required<ConfirmDialogOptions> & {
  id: number;
  returnFocusTarget: HTMLElement | null;
};

const activeDialog = ref<ActiveConfirmDialog | null>(null);
const focusFallback = ref<HTMLElement | null>(null);
let nextDialogId = 0;
let activeResolver: ((confirmed: boolean) => void) | null = null;
let activeSettled = false;
let focusRestorationObserver: MutationObserver | null = null;

/// Identifies accidental overlapping confirmation requests without replacing the active dialog.
export class ConfirmDialogAlreadyOpenError extends Error {
  constructor() {
    super('A Trip Editor confirmation dialog is already open.');
    this.name = 'ConfirmDialogAlreadyOpenError';
  }
}

export const confirmDialogState = readonly(activeDialog);

/// Registers the stable Trip Editor focus target used when the triggering control no longer exists.
export const setConfirmDialogFocusFallback = (element: HTMLElement | null): void => {
  focusFallback.value = element;
};

/// Opens one Trip Editor confirmation dialog and resolves to the user's yes/no choice.
export const confirm = (options: ConfirmDialogOptions): Promise<boolean> => {
  if (activeDialog.value || activeResolver) {
    return Promise.reject(new ConfirmDialogAlreadyOpenError());
  }

  const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const returnFocusTarget = activeElement && activeElement !== document.body ? activeElement : null;
  focusRestorationObserver?.disconnect();
  focusRestorationObserver = null;
  const dialog: ActiveConfirmDialog = {
    id: ++nextDialogId,
    title: options.title,
    message: options.message,
    confirmLabel: options.confirmLabel ?? 'Confirm',
    cancelLabel: options.cancelLabel ?? 'Cancel',
    variant: options.variant ?? 'default',
    returnFocusTarget
  };

  activeDialog.value = dialog;
  activeSettled = false;

  return new Promise<boolean>(resolve => {
    activeResolver = resolve;
  });
};

/// Settles the active confirmation once, then clears dialog state and resolver references.
export const settleConfirmDialog = (confirmed: boolean): void => {
  if (!activeResolver || activeSettled) {
    return;
  }

  const resolver = activeResolver;
  const settledDialogId = activeDialog.value?.id ?? nextDialogId;
  const returnFocusTarget = activeDialog.value?.returnFocusTarget ?? null;
  clearActiveDialog();

  resolver(confirmed);
  void nextTick(() => {
    // A chained dialog owns focus; stale restoration would move focus behind it.
    if (activeDialog.value || nextDialogId !== settledDialogId) {
      return;
    }

    restoreFocusWhenReady(returnFocusTarget);
  });
};

/// Disposes the Trip Editor confirm host and cancels any pending confirmation owned by it.
export const disposeConfirmDialogHost = (): void => {
  if (!activeResolver || activeSettled) {
    clearActiveDialog();
    return;
  }

  const resolver = activeResolver;
  clearActiveDialog();
  resolver(false);
};

function clearActiveDialog(): void {
  activeSettled = true;
  activeResolver = null;
  activeDialog.value = null;
}

function restoreFocusWhenReady(returnFocusTarget: HTMLElement | null): void {
  if (returnFocusTarget && tryFocus(returnFocusTarget)) {
    return;
  }

  // Vue may re-enable the trigger immediately after the dialog resolves; observe that state instead of racing it.
  if (returnFocusTarget && document.contains(returnFocusTarget)) {
    focusRestorationObserver?.disconnect();
    focusRestorationObserver = new MutationObserver(() => {
      if (!document.contains(returnFocusTarget)) {
        focusRestorationObserver?.disconnect();
        focusRestorationObserver = null;
        restoreFallbackFocus();
        return;
      }

      if (tryFocus(returnFocusTarget)) {
        focusRestorationObserver?.disconnect();
        focusRestorationObserver = null;
      }
    });
    focusRestorationObserver.observe(document.body, { attributes: true, attributeFilter: ['disabled', 'hidden'], childList: true, subtree: true });
    return;
  }

  restoreFallbackFocus();
}

function restoreFallbackFocus(): void {
  if (focusFallback.value) {
    tryFocus(focusFallback.value);
  }
}

function tryFocus(element: HTMLElement): boolean {
  if (!isFocusable(element)) {
    return false;
  }

  try {
    element.focus();
  } catch {
    return false;
  }

  return document.activeElement === element || element.contains(document.activeElement);
}

function isFocusable(element: HTMLElement): boolean {
  if (!document.contains(element) || element.closest('[inert]')) {
    return false;
  }

  if ('disabled' in element && element.disabled === true) {
    return false;
  }

  if (element.closest('[hidden]')) {
    return false;
  }

  const style = window.getComputedStyle(element);
  if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') {
    return false;
  }

  if (element.getClientRects().length === 0) {
    return false;
  }

  return element.tabIndex >= -1 || element.isContentEditable;
}
