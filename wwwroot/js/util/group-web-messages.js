import { date, message } from '../localization/messages.js';
import { formatOffsetLabel, getViewerTimeZone } from './datetime.js';

const legacyErrors = new Map([
  ['A pending invitation already exists for this user in the specified group', ['DuplicateInvite', 'This user already has a pending invitation to the group.']],
  ['InviteeUserId required', ['SelectInvitee', 'Please select a user to invite.']],
  ['InviteeUserId must identify an existing user', ['AccountUnavailable', 'This account is no longer available. Select another account.']],
  ['This account is no longer available. Select another account.', ['AccountUnavailable', 'This account is no longer available. Select another account.']],
  ['Invitation expired', ['InvitationExpired', 'This invitation has expired.']],
  ['Invitation is not pending', ['InvitationSettled', 'This invitation is no longer pending.']],
  ['Invitation predates departure or membership history is unavailable', ['InvitationBeforeDeparture', 'This invitation is unavailable. Ask for a new invitation.']],
  ['Cannot remove the last manager from an Organization group.', ['LastManagerRemove', 'Cannot remove the last manager from an Organization group.']],
  ['Cannot remove owner from Organization group without an eligible Manager successor. Promote a member to Manager first or delete the group.',
    ['OwnerRemoveSuccession', 'Promote an eligible member to Group Manager before removing the Organization’s owner, or delete the group.']],
  ['You are the owner of this Organization group and there is no eligible Manager successor. Promote a member to Manager first or delete the group.',
    ['OwnerLeaveSuccession', 'Promote an eligible member to Group Manager before leaving the Organization, or delete the group.']],
  ['You are the last manager of this Organization group. Transfer or add another manager before leaving.',
    ['LastManagerLeave', 'Transfer or add another Group Manager before leaving this Organization.']],
  ['The successor already owns a group with the same name.', ['SuccessorDuplicateName', 'The successor already owns a group with the same name.']],
]);

/** Group phrases use the locale snapshot of the initiating document. */
export const groupMessage = (key, fallback, ...args) => message(`Groups.${key}`, fallback, ...args);

/** Interprets the response only at the Web display boundary. */
export const groupError = async (response, action, fallback) => {
  if (response.status === 401 || response.redirected)
    return groupMessage('SessionEnded', 'Sign in again and retry.');
  if (response.status === 403)
    return groupMessage('Forbidden', 'You do not have permission to perform this group action.');
  if (response.status === 404) {
    return ['accept', 'decline', 'revoke'].includes(action)
      ? groupMessage('InvitationMissing', 'This invitation is no longer available.')
      : groupMessage('GroupMissing', 'This group is no longer available.');
  }
  const data = await response.json().catch(() => null);
  if (data?.message === 'User already an active member') {
    return action === 'accept' ? groupMessage('AlreadyJoined', 'You are already an active member.')
      : groupMessage('AlreadyMember', 'This user is already an active member.');
  }
  const known = legacyErrors.get(data?.message);
  return known ? groupMessage(...known) : fallback;
};

/** Uses the selected document locale and existing timezone/offset semantics for displayed dates. */
export const groupDateTime = (iso, timeZone = getViewerTimeZone()) => {
  if (!iso) return '';
  const instant = new Date(iso);
  if (!Number.isFinite(instant.getTime())) return '';
  try {
    const formatted = date(instant, { dateStyle: 'short', timeStyle: 'short', timeZone });
    return `${formatted} ${formatOffsetLabel(instant, timeZone)}`.trim();
  } catch {
    return '';
  }
};
