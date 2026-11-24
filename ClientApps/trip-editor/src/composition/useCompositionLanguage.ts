import { onUnmounted, watch } from 'vue';
import { holdLanguageNavigation, registerLanguageDraft } from '../../../../wwwroot/js/localization/languageNavigation.js';

/** Connects the mounted workspace's choices and inspection state to the shell selector. */
export function useCompositionLanguage(hasDraft: () => boolean, isHeld: () => boolean): void {
  const unregister = registerLanguageDraft(hasDraft);
  const stop = watch(isHeld, (held, _, cleanup) => {
    if (held) cleanup(holdLanguageNavigation());
  }, { immediate: true, flush: 'sync' });
  onUnmounted(() => { stop(); unregister(); });
}
