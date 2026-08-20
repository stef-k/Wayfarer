import { readonly, ref, type DeepReadonly, type Ref } from 'vue';

export interface SegmentSemanticTaint {
  /** Whether safe-add preservation remains valid for the active draft's operation history. */
  isSafe: DeepReadonly<Ref<boolean>>;
  /** Records an operation that prevents later additions from being classified as semantically safe. */
  markUnsafe: () => void;
  /** Starts clean operation history after an authoritative draft/baseline replacement. */
  resetFromAuthoritativeBaseline: () => void;
}

/** Creates the parent-owned semantic history authority for one active Segment draft at a time. */
export function createSegmentSemanticTaint(): SegmentSemanticTaint {
  const isSafe = ref(true);

  return {
    isSafe: readonly(isSafe),
    markUnsafe: () => { isSafe.value = false; },
    resetFromAuthoritativeBaseline: () => {
      isSafe.value = true;
    }
  };
}
