import L, { type Map as LeafletMap } from 'leaflet';
import type { EditorSegmentPresentation, SegmentPresentationKey } from '../segments/editorSegmentPresentation';
import { placeProjectedChevrons } from '../segments/segmentPresentationResolver';

type RegistryEntry = {
  presentation: EditorSegmentPresentation;
  group: L.LayerGroup;
  line: L.Polyline;
  hit: L.Polyline;
  chevrons: L.Polyline[];
};

/** Owns replace-only saved/draft Segment lines, hit layers, chevrons, and active badges. */
export const createSegmentPresentationLayer = (
  map: LeafletMap,
  onSelected: (key: SegmentPresentationKey) => boolean | Promise<boolean>
): {
  render: (presentations: readonly EditorSegmentPresentation[], activeKey: SegmentPresentationKey | null) => void;
  dispose: () => void;
  snapshot: () => unknown;
} => {
  const registry = new Map<string, RegistryEntry>();
  const badgeGroup = L.layerGroup().addTo(map);
  let currentPresentations: readonly EditorSegmentPresentation[] = [];
  let currentActiveKey: SegmentPresentationKey | null = null;
  map.createPane('segment-route-role');
  const pane = map.getPane('segment-route-role');
  if (pane) {
    pane.style.zIndex = '590';
    pane.style.pointerEvents = 'none';
    pane.setAttribute('aria-hidden', 'true');
  }

  const render = (presentations: readonly EditorSegmentPresentation[], activeKey: SegmentPresentationKey | null): void => {
    currentPresentations = presentations;
    currentActiveKey = activeKey;
    clearRegistry();
    badgeGroup.clearLayers();
    presentations.forEach(presentation => addEntry(presentation, sameKey(presentation.key, activeKey)));
    const active = presentations.find(presentation => sameKey(presentation.key, activeKey));
    if (active?.directionTrustworthy) renderBadges(active);
  };

  const addEntry = (presentation: EditorSegmentPresentation, active: boolean): void => {
    if (presentation.coordinates.length < 2) return;
    const latLngs = presentation.coordinates.map(([longitude, latitude]) => L.latLng(latitude, longitude));
    const group = L.layerGroup().addTo(map);
    const line = L.polyline(latLngs, {
      color: active ? '#0284c7' : '#0ea5e9',
      opacity: active ? 1 : 0.68,
      weight: active ? 5 : 3,
      interactive: false
    }).addTo(group);
    const hit = L.polyline(latLngs, { opacity: 0, weight: 16, className: 'segment-route-hit' })
      .on('click', () => void onSelected(presentation.key))
      .bindTooltip(presentation.directionTrustworthy
        ? presentation.anchors.anchors.map(anchor => `${anchor.label} — ${anchor.roleText} — ${anchor.displayName}`).join('<br>')
        : 'Route direction unavailable')
      .addTo(group);
    const chevrons = presentation.directionTrustworthy ? renderChevrons(presentation, active, group) : [];
    line.getElement()?.setAttribute('data-segment-presentation-owner', keyText(presentation.key));
    hit.getElement()?.setAttribute('data-segment-hit-owner', keyText(presentation.key));
    registry.set(keyText(presentation.key), { presentation, group, line, hit, chevrons });
  };

  const renderChevrons = (presentation: EditorSegmentPresentation, active: boolean, group: L.LayerGroup): L.Polyline[] => {
    const projected = presentation.coordinates.map(([longitude, latitude]) => {
      const point = map.latLngToLayerPoint([latitude, longitude]);
      return [point.x, point.y] as [number, number];
    });
    return placeProjectedChevrons(projected, active).map(cue => {
      const radians = cue.angle * Math.PI / 180;
      const length = active ? 10 : 8;
      const width = active ? 4 : 3;
      const backX = cue.x - Math.cos(radians) * length;
      const backY = cue.y - Math.sin(radians) * length;
      const normalX = -Math.sin(radians) * width;
      const normalY = Math.cos(radians) * width;
      return L.polyline([
        map.layerPointToLatLng([backX + normalX, backY + normalY]),
        map.layerPointToLatLng([cue.x, cue.y]),
        map.layerPointToLatLng([backX - normalX, backY - normalY])
      ], { color: active ? '#075985' : '#0369a1', opacity: active ? 1 : 0.72, weight: active ? 3 : 2, interactive: false, pane: 'segment-route-role' }).addTo(group);
    });
  };

  const renderBadges = (presentation: EditorSegmentPresentation): void => {
    presentation.anchors.badges.forEach(badge => {
      L.marker([badge.location[1], badge.location[0]], {
        pane: 'segment-route-role',
        interactive: false,
        keyboard: false,
        alt: '',
        icon: routeBadgeIcon(badge.label)
      }).addTo(badgeGroup);
    });
  };

  const rerenderForZoom = (): void => render(currentPresentations, currentActiveKey);
  map.on('zoomend', rerenderForZoom);

  const clearRegistry = (): void => {
    registry.forEach(entry => {
      entry.hit.unbindTooltip();
      entry.hit.off();
      entry.group.clearLayers();
      entry.group.remove();
    });
    registry.clear();
  };

  const dispose = (): void => {
    map.off('zoomend', rerenderForZoom);
    clearRegistry();
    badgeGroup.clearLayers();
    badgeGroup.remove();
    pane?.remove();
  };

  return {
    render,
    dispose,
    snapshot: () => ({
      segments: [...registry.values()].map(entry => ({
        id: keyText(entry.presentation.key), source: entry.presentation.source, visible: true,
        active: sameKey(entry.presentation.key, currentActiveKey), orientation: entry.presentation.orientation,
        lineCount: 1, hitLayerCount: 1, chevronCount: entry.chevrons.length,
        anchors: entry.presentation.anchors.anchors.map(anchor => ({ label: anchor.label, role: anchor.roleText }))
      })),
      routeBadgeCount: badgeGroup.getLayers().length
    })
  };
};

/** Produces one decorative pointer-transparent badge without touching the Place marker DOM. */
function routeBadgeIcon(label: string): L.DivIcon {
  const pill = label.length > 1;
  return L.divIcon({
    className: 'segment-route-badge-wrapper',
    html: `<span class="segment-route-badge${pill ? ' segment-route-badge--pill' : ''}" aria-hidden="true">${escapeHtml(label)}</span>`,
    iconSize: pill ? [34, 22] : [22, 22],
    iconAnchor: [-11, 20]
  });
}

const keyText = (key: SegmentPresentationKey): string => key.kind === 'persisted' ? key.id : key.token;
const sameKey = (left: SegmentPresentationKey, right: SegmentPresentationKey | null): boolean => Boolean(right)
  && left.kind === right!.kind && keyText(left) === keyText(right!);
const escapeHtml = (value: string): string => value.replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;'
})[character]!);
