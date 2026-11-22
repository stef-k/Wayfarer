import { message } from '../localization/messages.js';
import { formatInviteRows, refreshInviteRows, removeInviteRow } from './group-invitation-rows.js';
import { groupError, groupMessage as t } from './group-web-messages.js';

/** Binds the scoped picker and returns the submitted selection and reset operation. */
const bindUserSearch = (form, groupId) => {
  const results = document.getElementById('userResults');
  const input = document.getElementById('userSearch');
  const button = document.getElementById('userSearchBtn');
  const spinner = document.getElementById('userSearchSpinner');
  const status = document.getElementById('groupUserSearchStatus');
  const invitee = form.elements.inviteeUserId;

  // Clearing the visible picker also clears its submitted exact account ID.
  const clearResults = () => {
    results.replaceChildren();
    invitee.value = '';
    status.textContent = '';
  };

  /** Searches the existing scoped picker and keeps the hidden selection synchronized. */
  const searchUsers = async () => {
    if (input.disabled) return;
    const query = input.value.trim();
    clearResults();
    if (query.length < 2) return;

    input.disabled = button.disabled = true;
    spinner.classList.remove('d-none');
    status.textContent = t('SearchBusy', 'Searching for users…');
    try {
      const response = await fetch(`/api/users/search?${new URLSearchParams({ query, groupId })}`);
      if (!response.ok || response.redirected) {
        status.textContent = await groupError(response, 'search', t('SearchFailed', 'Users could not be loaded. Please try again.'));
        return;
      }
      const users = await response.json();
      users.forEach(user => {
        const label = user.displayName ? `${user.userName} (${user.displayName})` : user.userName;
        results.add(new Option(label, user.id));
      });
      invitee.value = results.value;
      status.textContent = users.length ? '' : t('SearchEmpty', 'No matching users.');
      if (users.length) results.classList.remove('is-invalid');
    } catch {
      status.textContent = t('SearchFailed', 'Users could not be loaded. Please try again.');
    } finally {
      spinner.classList.add('d-none');
      input.disabled = button.disabled = false;
    }
  };

  let searchTimer;
  const searchNow = () => {
    clearTimeout(searchTimer);
    void searchUsers();
  };
  button.addEventListener('click', searchNow);
  input.addEventListener('input', () => {
    clearTimeout(searchTimer);
    clearResults();
    searchTimer = setTimeout(searchUsers, 300);
  });
  input.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      searchNow();
    }
    if (event.key === 'Escape') {
      clearTimeout(searchTimer);
      input.value = '';
      clearResults();
    }
  });
  results.addEventListener('change', () => { invitee.value = results.value; });

  return { input, results, invitee, clearResults };
};

/** Sends an area action with its rendered antiforgery token. */
const submit = async (actionForm, action, failure, applied) => {
  const body = new FormData(actionForm);
  const submitButton = actionForm.querySelector('[type="submit"]');
  if (submitButton.disabled) return;
  submitButton.disabled = true;
  try {
    const response = await fetch(`${actionForm.action}Ajax`, {
      method: 'POST',
      body,
      headers: { RequestVerificationToken: body.get('__RequestVerificationToken') || '' },
    });
    if (!response.ok || response.redirected) {
      wayfarer.showAlert?.('danger', await groupError(response, action, failure));
      return;
    }
    const data = await response.json();
    if (data?.success) applied(body);
    else wayfarer.showAlert?.('danger', failure);
  } catch {
    wayfarer.showAlert?.('danger', t('Unconfirmed', 'The outcome could not be confirmed. Refresh the page before trying again.'));
  } finally {
    submitButton.disabled = false;
  }
};

/** Applies live hints to the durable pending rows and the currently mounted roster. */
const bindRosterStream = (groupId, roster, rosterRow, removeRosterRow) => {
  const pendingJoins = new Map();

  /** Renders an accepted invite as an active Member with an existing, localized remove form. */
  const addRosterRow = async userId => {
    if (rosterRow(userId) || pendingJoins.has(userId)) return;
    const read = Symbol();
    pendingJoins.set(userId, read);
    try {
      const response = await fetch(`/api/users/${encodeURIComponent(userId)}/basic`);
      if (!response.ok) throw new Error('Group roster read failed');
      const user = await response.json();
      if (pendingJoins.get(userId) !== read || rosterRow(userId)) return;

      const row = roster.insertRow();
      row.dataset.userId = userId;
      for (const value of [user.userName, user.displayName, t('RoleMember', 'Member'), t('StatusActive', 'Active')]) {
        row.insertCell().textContent = value ?? '';
      }
      const removeForm = document.getElementById('groupMemberRemoveTemplate').content.cloneNode(true);
      removeForm.querySelector('[name="userId"]').value = userId;
      removeForm.querySelector('button').setAttribute('aria-label',
        t('RemoveNamed', 'Remove {0} from the group', user.userName));
      row.insertCell().append(removeForm);
    } catch {
      if (pendingJoins.get(userId) === read) {
        wayfarer.showAlert?.('warning', t('RosterRefreshFailed', 'Refresh this page to update the roster.'));
      }
    } finally {
      if (pendingJoins.get(userId) === read) pendingJoins.delete(userId);
    }
  };

  if (typeof EventSource === 'undefined') return;
  const stream = new EventSource(`/api/sse/group/${groupId}`);
  stream.onmessage = event => {
    let data;
    try { data = JSON.parse(event.data); } catch { return; }
    switch (data?.type) {
      case 'member-joined':
        if (data.invitationId) removeInviteRow(data.invitationId);
        if (data.userId) {
          wayfarer.showAlert?.('success', t('MemberJoined', 'A user joined the group.'));
          void addRosterRow(data.userId);
        }
        break;
      case 'member-left':
      case 'member-removed':
        if (data.userId) {
          pendingJoins.delete(data.userId);
          removeRosterRow(data.userId);
          if (data.type === 'member-left') wayfarer.showAlert?.('warning', t('MemberLeft', 'A member left the group.'));
          else wayfarer.showAlert?.('info', t('MemberRemoved', 'Member removed.'));
        }
        break;
      case 'invite-created':
        void refreshInviteRows();
        break;
      case 'invite-declined':
        if (data.invitationId) {
          removeInviteRow(data.invitationId);
          wayfarer.showAlert?.('secondary', t('InviteDeclined', 'Invitation declined.'));
        }
        break;
      case 'invite-revoked':
        if (data.inviteId) removeInviteRow(data.inviteId);
        break;
    }
  };
  window.addEventListener('pagehide', () => stream.close(), { once: true });
};

/** Runs both Members pages against their own existing area routes. */
export const bindGroupMembers = () => {
  const form = document.getElementById('inviteForm');
  if (!form) return;
  const groupId = form.elements.groupId.value;
  const { input, results, invitee, clearResults } = bindUserSearch(form, groupId);

  form.addEventListener('submit', event => {
    event.preventDefault();
    if (!invitee.value) {
      results.classList.add('is-invalid');
      wayfarer.showAlert?.('danger', t('SelectInvitee', 'Please select a user to invite.'));
      return;
    }

    void submit(form, 'invite', t('InviteFailed', 'Failed to send invite.'), () => {
      wayfarer.showAlert?.('success', t('InviteSent', 'Invitation sent.'));
      input.value = '';
      clearResults();
      void refreshInviteRows();
    });
  });

  const roster = document.querySelector('#rosterTable tbody');
  const rosterRow = id => roster.querySelector(`tr[data-user-id="${CSS.escape(id)}"]`);
  const removeRosterRow = id => rosterRow(id)?.remove();

  // Delegate so new pending-invitation and roster rows retain the same actions.
  document.addEventListener('submit', event => {
    const actionForm = event.target.closest('form.js-confirm.js-ajax');
    if (!actionForm) return;
    event.preventDefault();
    const remove = actionForm.action.endsWith('/RemoveMember');
    const onConfirm = () => remove
      ? submit(actionForm, 'remove', t('RemoveFailed', 'The member could not be removed.'), body => {
        removeRosterRow(body.get('userId'));
        wayfarer.showAlert?.('success', t('MemberRemoved', 'Member removed.'));
      })
      : submit(actionForm, 'revoke', t('RevokeFailed', 'Unable to revoke invitation.'), body => {
        removeInviteRow(body.get('inviteId'));
        wayfarer.showAlert?.('success', t('InviteRevoked', 'Invitation canceled.'));
      });
    const title = actionForm.dataset.confirmTitle || message('Shell.ConfirmAction', 'Confirm Action');
    const text = actionForm.dataset.confirmMessage || message('Shell.ConfirmMessage', 'Are you sure?');
    const confirmText = actionForm.querySelector('[type="submit"]').textContent.trim();
    if (wayfarer.showConfirmationModal) {
      wayfarer.showConfirmationModal({ title, message: text, confirmText, onConfirm });
    } else if (confirm(text)) {
      void onConfirm();
    }
  });

  formatInviteRows(document);
  bindRosterStream(groupId, roster, rosterRow, removeRosterRow);
};
