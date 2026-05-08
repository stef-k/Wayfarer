import { readonly, ref } from 'vue';

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

  const returnFocusTarget = document.activeElement instanceof HTMLElement ? document.activeElement : null;
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
  const returnFocusTarget = activeDialog.value?.returnFocusTarget ?? null;
  activeSettled = true;
  activeResolver = null;
  activeDialog.value = null;

  resolver(confirmed);
  window.setTimeout(() => restoreFocus(returnFocusTarget), 0);
};

function restoreFocus(returnFocusTarget: HTMLElement | null): void {
  if (returnFocusTarget && document.contains(returnFocusTarget)) {
    returnFocusTarget.focus();
    return;
  }

  if (focusFallback.value && document.contains(focusFallback.value)) {
    focusFallback.value.focus();
  }
}
