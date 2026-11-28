const contract = 'trip-metadata-receipt-2';
const timestamp = '2026-11-28T09:55:00Z';
const routes = [
  [/\/editor\/metadata\/conditional$/, 'metadata'],
  [/\/editor\/share-progress\/conditional$/, 'editor-sharing'],
  [/\/User\/Trip\/[^/]+\/share-progress\/conditional$/, 'viewer-sharing']
];

/** Supplies permits, receipts and owner reads to mounted browser tests at their fetch boundary. */
export function tripReceiptReplies(owner, write, { lookup, next } = {}) {
  const permits = new Map();
  return async (url, init) => {
    const request = new URL(url, location.href);
    const [path, operation] = request.pathname.split('/operations');
    const kind = routes.find(([pattern]) => pattern.test(path))?.[1];
    if (!kind) return next(url, init);
    const metadata = owner();
    if (operation === '' && init.method === 'POST') {
      const permit = {
        contract, subjectId: 'account', adapter: 'web', kind, tripId: metadata.id,
        requestDigest: JSON.parse(init.body).requestDigest, executeBeforeUtc: '2026-11-28T10:00:00Z',
        operationId: crypto.randomUUID()
      };
      permits.set(permit.operationId, permit);
      return Response.json({ ...permit, proof: permit.operationId });
    }
    if (operation) return lookup(permits.get(operation.slice(1)));
    if (!init?.method || init.method === 'GET')
      return Response.json({ subjectId: 'account', metadata, tags: [] });
    const permit = permits.get(init.headers['X-Wayfarer-Trip-Attempt']);
    if (!permit || permit.kind !== kind || JSON.parse(init.body).receiptContract !== contract)
      throw new Error('Expected a receipt-authorized write');
    return write(url, init, permit);
  };
}

/** Builds wire evidence with the permit correlation and retention used by the mounted transport. */
export function tripReceipt(permit, state, effects) {
  return {
    ...permit, state, historicalOutcome: state === 'terminalUnapplied' ? 'unapplied' : state,
    futureEffects: false, completedAtUtc: timestamp, receiptRetainUntilUtc: '2026-12-28T10:00:00Z',
    ...(state === 'applied' ? { updatedAtUtc: timestamp } : {}), ...effects
  };
}
