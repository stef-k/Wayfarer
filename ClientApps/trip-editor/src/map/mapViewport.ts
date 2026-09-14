import type { Map as LeafletMap } from 'leaflet';
import type { EditorCoordinate } from '../types';

/** Serializable viewport shared by the adapter, URL, and metadata capture boundary. */
export interface TripEditorMapView {
  center: EditorCoordinate;
  zoom: number;
}

/** Wrap world copies and round only the map capture representation, never manual input. */
export const canonicalMapView = (view: TripEditorMapView): TripEditorMapView => {
  const longitude = Number((((view.center.longitude + 180) % 360 + 360) % 360 - 180).toFixed(6));
  return {
    center: {
      latitude: Number(Math.max(-90, Math.min(90, view.center.latitude)).toFixed(6)),
      longitude: longitude === 180 ? -180 : longitude
    },
    zoom: Math.max(0, Math.min(19, Math.round(view.zoom)))
  };
};

/** Invalid or missing URL components independently retain the resolved saved/geometry/global base. */
export const resolveUrlMapView = (search: string, base: TripEditorMapView): TripEditorMapView => {
  const parameters = new URLSearchParams(search);
  const read = (key: string, min: number, max: number, fallback: number): number => {
    const raw = parameters.get(key);
    const value = raw?.trim() ? Number(raw) : Number.NaN;
    return Number.isFinite(value) && value >= min && value <= max ? value : fallback;
  };
  return {
    center: {
      latitude: read('lat', -90, 90, base.center.latitude),
      longitude: read('lng', -180, 180, base.center.longitude)
    },
    zoom: Math.round(read('zoom', 0, 19, base.zoom))
  };
};

/** Replace just the viewport keys, retaining query context, hash, and the caller's history state. */
export const replaceUrlMapView = (view: TripEditorMapView, browser: Pick<Window, 'location' | 'history'> = window): void => {
  const canonical = canonicalMapView(view);
  const url = new URL(browser.location.href);
  url.searchParams.set('lat', canonical.center.latitude.toFixed(6));
  url.searchParams.set('lng', canonical.center.longitude.toFixed(6));
  url.searchParams.set('zoom', String(canonical.zoom));
  browser.history.replaceState(browser.history.state, '', url);
};

/** Own movement origin until moveend (after zoomend), including animated commands and popup auto-pan. */
export const createMapViewport = (map: LeafletMap, options: {
  onCaptured?: (view: TripEditorMapView) => void;
  canCapture: () => boolean;
}) => {
  const element = map.getContainer();
  let ready = false;
  let userMovement = false;
  let pendingGesture = false;
  let gestureTimer: ReturnType<typeof setTimeout> | undefined;
  let lastView = '';
  let commandOwned = false;
  let commandFrame: number | undefined;
  let moving = false;
  let zooming = false;

  const getView = (): TripEditorMapView => {
    const center = map.getCenter();
    return canonicalMapView({ center: { latitude: center.lat, longitude: center.lng }, zoom: map.getZoom() });
  };
  const diagnostics = (): TripEditorMapView => {
    const view = getView();
    element.dataset.tripEditorMapLat = view.center.latitude.toFixed(6);
    element.dataset.tripEditorMapLng = view.center.longitude.toFixed(6);
    element.dataset.tripEditorMapZoom = String(view.zoom);
    return view;
  };
  const clearGesture = (): void => {
    clearTimeout(gestureTimer);
    pendingGesture = false;
  };
  const transient = (): void => {
    clearGesture();
    userMovement = false;
  };
  const releaseCommand = (): void => {
    if (commandFrame === undefined && !moving && !zooming) commandOwned = false;
  };
  // Commands revoke capture for their entire movement; terminal events never grant user authority.
  const navigate = <T>(command: () => T): T => {
    transient();
    commandOwned = true;
    if (commandFrame !== undefined) window.cancelAnimationFrame(commandFrame);
    commandFrame = -1; // Synchronous terminal events must also wait for any queued animation start.
    try {
      return command();
    } finally {
      // Leaflet queues animated zoom starts for the next frame. Release no-op commands after it.
      commandFrame = window.requestAnimationFrame(() => {
        commandFrame = undefined;
        releaseCommand();
      });
    }
  };
  const armGesture = (): void => {
    clearGesture();
    pendingGesture = options.canCapture();
    // Leaflet wheel input is debounced; an input at a zoom limit must not authorize a later command.
    gestureTimer = setTimeout(clearGesture, map.options.wheelDebounceTime! + 100);
  };
  const startMovement = (event: { type: string }): void => {
    if (event.type === 'zoomstart') zooming = true;
    else moving = true;
    if (pendingGesture) {
      userMovement = !commandOwned;
      clearGesture();
    }
  };
  const startDrag = (): void => {
    clearGesture();
    userMovement = !commandOwned && options.canCapture();
  };
  const finishMovement = (): void => {
    const view = diagnostics();
    const serialized = JSON.stringify(view);
    const capture = userMovement && !commandOwned && options.canCapture();
    userMovement = false;
    moving = false;
    releaseCommand();
    if (!ready || serialized === lastView) return;
    lastView = serialized;
    replaceUrlMapView(view);
    if (capture) options.onCaptured?.(view);
  };
  const finishZoom = (): void => {
    zooming = false;
    diagnostics();
    releaseCommand();
  };
  const wheel = (): void => { if (map.scrollWheelZoom.enabled()) armGesture(); };
  const doubleClick = (): void => { if (map.doubleClickZoom.enabled()) armGesture(); };
  const key = (event: KeyboardEvent): void => {
    if (event.key === 'Escape') transient(); // Cancelled box zoom must release its movement authority.
    // Leaflet handles these keys only with map focus, ignoring modified keys other than Shift.
    if (event.target === element && map.keyboard.enabled() && !event.altKey && !event.ctrlKey && !event.metaKey &&
        [37, 38, 39, 40, 187, 107, 61, 171, 189, 109, 54, 173].includes(event.keyCode)) armGesture();
  };
  const touch = (event: TouchEvent): void => {
    if (event.touches.length === 2 && map.touchZoom.enabled()) {
      clearGesture();
      pendingGesture = options.canCapture();
    }
  };
  // Leaflet also exposes pinch via pointer events on devices without native touch events.
  const pointers = new Set<number>();
  const pointerDown = (event: PointerEvent): void => {
    if (event.pointerType === 'mouse') return;
    pointers.add(event.pointerId);
    if (pointers.size === 2 && map.touchZoom.enabled()) {
      clearGesture();
      pendingGesture = options.canCapture();
    }
  };
  const pointerUp = (event: PointerEvent): void => { pointers.delete(event.pointerId); clearGesture(); };
  const zoomButtons = element.querySelectorAll('.leaflet-control-zoom-in, .leaflet-control-zoom-out');
  zoomButtons.forEach(button => button.addEventListener('click', armGesture, true));
  element.addEventListener('wheel', wheel, { capture: true, passive: true });
  element.addEventListener('dblclick', doubleClick, true);
  element.addEventListener('keydown', key, true);
  element.addEventListener('touchstart', touch, { capture: true, passive: true });
  element.addEventListener('touchend', clearGesture, true);
  element.addEventListener('touchcancel', clearGesture, true);
  element.addEventListener('pointerdown', pointerDown, true);
  document.addEventListener('pointerup', pointerUp, true);
  document.addEventListener('pointercancel', pointerUp, true);
  window.addEventListener('resize', transient);
  map.on('movestart zoomstart', startMovement);
  map.on('dragstart boxzoomstart', startDrag);
  map.on('autopanstart', transient);
  map.on('zoomend', finishZoom);
  map.on('moveend', finishMovement);

  return {
    getView,
    navigate,
    /** Initial placement is synchronous and records a baseline without writing the URL. */
    initialize: (place: () => void): void => {
      navigate(place);
      lastView = JSON.stringify(diagnostics());
      ready = true;
    },
    dispose: (): void => {
      clearGesture();
      if (commandFrame !== undefined) window.cancelAnimationFrame(commandFrame);
      zoomButtons.forEach(button => button.removeEventListener('click', armGesture, true));
      element.removeEventListener('wheel', wheel, true);
      element.removeEventListener('dblclick', doubleClick, true);
      element.removeEventListener('keydown', key, true);
      element.removeEventListener('touchstart', touch, true);
      element.removeEventListener('touchend', clearGesture, true);
      element.removeEventListener('touchcancel', clearGesture, true);
      element.removeEventListener('pointerdown', pointerDown, true);
      document.removeEventListener('pointerup', pointerUp, true);
      document.removeEventListener('pointercancel', pointerUp, true);
      window.removeEventListener('resize', transient);
      map.off('movestart zoomstart', startMovement);
      map.off('dragstart boxzoomstart', startDrag);
      map.off('autopanstart', transient);
      map.off('zoomend', finishZoom);
      map.off('moveend', finishMovement);
    }
  };
};
