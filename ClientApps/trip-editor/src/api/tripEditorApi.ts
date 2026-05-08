import type { EditorTripState } from '../types';

export const loadEditorState = async (endpoint: string): Promise<EditorTripState> => {
  const response = await fetch(endpoint, {
    headers: { Accept: 'application/json' },
    credentials: 'same-origin'
  });

  if (!response.ok) {
    throw new Error(`Trip Editor API returned ${response.status}`);
  }

  return (await response.json()) as EditorTripState;
};
