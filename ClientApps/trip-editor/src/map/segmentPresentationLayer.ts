import L, { type Map as LeafletMap } from 'leaflet';
import type { EditorSegmentPresentation, SegmentPresentationKey } from '../segments/editorSegmentPresentation';
import { fitCombinedRouteBadgeLabels, placeCombinedRouteBadge, placeProjectedChevrons, placeRouteBadge, projectChevronArm,
  type CombinedRouteBadgeLayout, type PresentationRectangle } from '../segments/segmentPresentationResolver';

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
    pane.setAttribute('aria-hidden', 'true');
  }

  const render = (presentations: readonly EditorSegmentPresentation[], activeKey: SegmentPresentationKey | null): void => {
    currentPresentations = presentations;
    currentActiveKey = activeKey;
    clearRegistry();
    badgeGroup.clearLayers();
    presentations.forEach(presentation => addEntry(presentation, sameKey(presentation.key, activeKey)));
    const active = presentations.find(presentation => sameKey(presentation.key, activeKey));
    if (active) renderBadges(active);
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
        : 'Route direction unavailable', { className: 'trip-rich-tooltip' })
      .addTo(group);
    const chevrons = presentation.directionTrustworthy ? renderChevrons(presentation, active, group) : [];
    const lineElement = line.getElement();
    if (lineElement) {
      lineElement.setAttribute('data-segment-id', presentation.segmentId ?? keyText(presentation.key));
      lineElement.setAttribute('data-segment-presentation-owner', keyText(presentation.key));
      lineElement.setAttribute('data-route-owner', presentation.source === 'S' ? 'saved' : presentation.source === 'D' ? 'draft' : 'work');
      lineElement.setAttribute('data-route-kind', presentation.hasCustomRoute ? 'custom' : 'fallback');
    }
    hit.getElement()?.setAttribute('data-segment-hit-owner', keyText(presentation.key));
    registry.set(keyText(presentation.key), { presentation, group, line, hit, chevrons });
  };

  const renderChevrons = (presentation: EditorSegmentPresentation, active: boolean, group: L.LayerGroup): L.Polyline[] => {
    const projected = presentation.coordinates.map(([longitude, latitude]) => {
      const point = map.latLngToLayerPoint([latitude, longitude]);
      return [point.x, point.y] as [number, number];
    });
    return placeProjectedChevrons(projected, active).map(cue => {
      const points = projectChevronArm(cue, active).map(point => map.layerPointToLatLng([point[0], point[1]]));
      return L.polyline(points, { color: '#852D10', opacity: active ? 1 : 0.72, weight: active ? 3 : 2,
        interactive: false, pane: 'segment-route-role' }).addTo(group);
    });
  };

  const renderBadges = (presentation: EditorSegmentPresentation): void => {
    const size = map.getSize();
    const mapBounds = { left: 0, top: 0, right: size.x, bottom: size.y };
    const controlBounds = visibleControlBounds(map);
    const placedBounds: PresentationRectangle[] = [];
    const blocked: { badge: typeof presentation.anchors.badges[number]; anchor: L.Point }[] = [];
    presentation.anchors.badges.forEach(badge => {
      const anchor = map.latLngToContainerPoint([badge.location[1], badge.location[0]]);
      const dimensions = badgeDimensions(badge.label);
      const placement = placeRouteBadge([anchor.x, anchor.y], dimensions, mapBounds, controlBounds, placedBounds);
      if (placement.fallback) {
        blocked.push({ badge, anchor });
        return;
      }
      placedBounds.push({ left: placement.left, top: placement.top,
        right: placement.left + placement.width, bottom: placement.top + placement.height });
      renderBadgeMarker(badge.location, badge.label, badge.descriptions, anchor, placement);
    });
    if (blocked.length) {
      const labels = blocked.map(item => item.badge.label);
      const layout = fitCombinedRouteBadgeLabels(labels, Math.min(160, mapBounds.right - mapBounds.left - 8));
      const label = labels.join('/');
      const placement = placeCombinedRouteBadge(blocked.map(item => [item.anchor.x, item.anchor.y]),
        layout, mapBounds, controlBounds, placedBounds);
      renderBadgeMarker(blocked[0].badge.location, label, blocked.flatMap(item => item.badge.descriptions),
        blocked[0].anchor, placement, layout);
    }
  };

  /** Adds one pointer-only route-role badge without changing its canonical Place marker. */
  const renderBadgeMarker = (location: readonly [number, number], label: string, descriptions: readonly string[], anchor: L.Point,
    placement: ReturnType<typeof placeRouteBadge>, layout?: CombinedRouteBadgeLayout): void => {
      L.marker([location[1], location[0]], {
        pane: 'segment-route-role',
        interactive: true,
        keyboard: false,
        alt: '',
        icon: routeBadgeIcon(label, placement.left - anchor.x, placement.top - anchor.y, placement.fallback, layout)
      }).bindTooltip(descriptions.map(escapeHtml).join('<br>'), { className: 'trip-rich-tooltip' }).addTo(badgeGroup);
  };

  const rerenderForMovement = (): void => render(currentPresentations, currentActiveKey);
  map.on('zoomend moveend', rerenderForMovement);

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
    map.off('zoomend moveend', rerenderForMovement);
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
function routeBadgeIcon(label: string, leftOffset: number, topOffset: number, fallback: boolean,
  layout?: CombinedRouteBadgeLayout): L.DivIcon {
  const dimensions = layout ?? badgeDimensions(label);
  const pill = label.length > 1 || fallback;
  const content = layout ? layout.lines.map(escapeHtml).join('<br>') : escapeHtml(label);
  const layoutStyle = layout ? ` style="box-sizing:border-box;width:${layout.width}px;height:${layout.height}px"` : '';
  return L.divIcon({
    className: 'segment-route-badge-wrapper',
    html: `<span class="segment-route-badge${pill ? ' segment-route-badge--pill' : ''}${layout ? ' segment-route-badge--wrapped' : ''}" aria-hidden="true"${layoutStyle}>${content}</span>`,
    iconSize: [dimensions.width, dimensions.height],
    iconAnchor: [-leftOffset, -topOffset]
  });
}

/** Returns the fixed application-rendered dimensions used by collision fixtures and Leaflet. */
const badgeDimensions = (label: string): { width: number; height: number } => ({
  width: label.length > 1 ? Math.max(34, 14 + label.length * 9) : 24, height: 24
});

/** Projects visible Leaflet controls into the map container's coordinate system. */
const visibleControlBounds = (map: LeafletMap): PresentationRectangle[] => {
  const containerBounds = map.getContainer().getBoundingClientRect();
  return [...map.getContainer().querySelectorAll<HTMLElement>('.leaflet-control')]
    .filter(element => element.offsetParent !== null)
    .map(element => element.getBoundingClientRect())
    .map(bounds => ({ left: bounds.left - containerBounds.left, top: bounds.top - containerBounds.top,
      right: bounds.right - containerBounds.left, bottom: bounds.bottom - containerBounds.top }));
};

const keyText = (key: SegmentPresentationKey): string => key.kind === 'persisted' ? key.id : key.token;
const sameKey = (left: SegmentPresentationKey, right: SegmentPresentationKey | null): boolean => Boolean(right)
  && left.kind === right!.kind && keyText(left) === keyText(right!);
const escapeHtml = (value: string): string => value.replace(/[&<>"']/g, character => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#039;'
})[character]!);
