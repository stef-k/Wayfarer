import { locale, message } from '../../../wwwroot/js/localization/messages.js';

/** Preserves field bindings and maps known legacy validation text only at the Web presentation boundary. */
export const editorFieldError = (error: string): string => {
  const fields: Record<string, string> = {
    'Name is required.': 'Editor.NameRequired',
    'Name must contain 1 through 200 characters.': 'Editor.NameLength',
    'The field is required.': 'Editor.FieldRequired',
    'Latitude must be between -90 and 90.': 'Editor.LatitudeMustBeBetween90And90',
    'Longitude must be between -180 and 180.': 'Editor.LongitudeMustBeBetween180And180'
  };
  const key = fields[error];
  if (key) return message(key, error);
  return locale === 'en-US' ? error : message('Editor.InvalidField', 'Review the value in this field.');
};
