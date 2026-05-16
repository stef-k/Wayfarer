import { canonicalImageSource, normalizeNotesHtml } from './notes/notesHtml';
import type { EditorPlace } from './types';

const iconBasePath = '/icons/wayfarer-map-icons/dist/png/marker';
const defaultMarkerColor = 'bg-blue';
const defaultMarkerIcon = 'marker';

/// Builds the static PNG marker path used by the legacy editor and public trip views.
export function placeMarkerIconUrl(iconName: string | null | undefined, markerColor: string | null | undefined): string {
  return `${iconBasePath}/${safePathSegment(markerColor, defaultMarkerColor)}/${safePathSegment(iconName, defaultMarkerIcon)}.png`;
}

/// Converts canonical external notes image URLs to the display-only proxy endpoint.
export function displayImageSource(value: string): string {
  const source = canonicalImageSource(value);
  if (!isExternalHttpUrl(source)) {
    return source;
  }

  return `/Public/ProxyImage?url=${encodeURIComponent(source)}`;
}

/// Returns sanitized, display-only HTML for compact place marker popups.
export function placeNotesPreviewHtml(notesHtml: string, maxCharacters = 180): string {
  const template = document.createElement('template');
  template.innerHTML = normalizeNotesHtml(notesHtml);
  template.content.querySelectorAll<HTMLImageElement>('img').forEach(image => {
    image.setAttribute('src', displayImageSource(image.getAttribute('src') ?? ''));
    image.setAttribute('loading', 'lazy');
    image.setAttribute('alt', image.getAttribute('alt') ?? '');
  });

  truncateTextContent(template.content, maxCharacters);
  return template.innerHTML.trim();
}

/// Provides the accessible label shared by map markers and sidebar marker previews.
export function placeMarkerLabel(place: Pick<EditorPlace, 'name' | 'visitSummary'>): string {
  return place.visitSummary.isVisited
    ? `${place.name}, ${place.visitSummary.visitCount} visit(s)`
    : place.name;
}

function truncateTextContent(root: ParentNode, maxCharacters: number): void {
  let remaining = maxCharacters;
  let truncated = false;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const toRemove: Node[] = [];

  while (walker.nextNode()) {
    const node = walker.currentNode;
    if (truncated) {
      toRemove.push(node);
      continue;
    }

    const text = node.textContent ?? '';
    if (text.length <= remaining) {
      remaining -= text.length;
      continue;
    }

    node.textContent = `${text.slice(0, Math.max(0, remaining)).trimEnd()}...`;
    truncated = true;
  }

  toRemove.forEach(node => node.parentNode?.removeChild(node));
}

function isExternalHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function safePathSegment(value: string | null | undefined, fallback: string): string {
  return encodeURIComponent(value?.trim() || fallback);
}
