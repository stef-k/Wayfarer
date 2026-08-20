/** Inputs for switching to and installing one authoritative Segment. */
export interface AuthoritativeSegmentActivation<TSegment> {
  segment: TSegment;
  isAlreadyActive: boolean;
  activateTarget: () => Promise<boolean>;
  installAuthoritativeSegment: (segment: TSegment) => void;
}

/** Installs authoritative ownership only after the active-target guard succeeds. */
export async function activateAuthoritativeSegment<TSegment>(
  activation: AuthoritativeSegmentActivation<TSegment>
): Promise<boolean> {
  if (activation.isAlreadyActive) return true;
  if (!(await activation.activateTarget())) return false;
  activation.installAuthoritativeSegment(activation.segment);
  return true;
}
