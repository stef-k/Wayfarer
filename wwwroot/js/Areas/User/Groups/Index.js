import { groupError, groupMessage as t } from '../../../util/group-web-messages.js';

/** The server renders joined groups. Leave changes the row only after the existing API succeeds. */
const tbody = document.getElementById('groupsBody');
const leaveGroup = async groupId => {
  try {
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const response = await fetch(`/api/groups/${groupId}/leave`, {
      method: 'POST',
      headers: { RequestVerificationToken: token },
    });
    if (!response.ok || response.redirected) {
      wayfarer.showAlert?.('danger', await groupError(response, 'leave', t('LeaveFailed', 'You could not leave the group.')));
      return;
    }
    tbody.querySelector(`tr[data-group-id="${groupId}"]`)?.remove();
    const empty = document.getElementById('groupsEmptyRow');
    if (empty) empty.hidden = Boolean(tbody.querySelector('tr[data-group-id]'));
    wayfarer.showAlert?.('success', t('LeftGroup', 'You left the group.'));
  } catch {
    wayfarer.showAlert?.('danger', t('Unconfirmed', 'The outcome could not be confirmed. Refresh the page before trying again.'));
  }
};

tbody?.addEventListener('click', event => {
  const groupId = event.target.closest('.js-leave')?.getAttribute('data-group-id');
  if (!groupId) return;
  const text = t('LeaveConfirm', 'Are you sure you want to leave this group?');
  if (wayfarer.showConfirmationModal) {
    wayfarer.showConfirmationModal({
      title: t('LeaveGroup', 'Leave Group'),
      message: text,
      confirmText: t('Leave', 'Leave'),
      onConfirm: () => leaveGroup(groupId),
    });
  } else if (confirm(text)) {
    void leaveGroup(groupId);
  }
});
