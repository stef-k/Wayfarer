const forbiddenElements = 'script, style, iframe, object, embed, link, meta, base, form, input, button, textarea, select, option';
const allowedQuillFontClasses = new Set(['ql-font-serif', 'ql-font-monospace']);
const allowedQuillListKinds = new Set(['bullet', 'ordered']);

/// Normalizes Trip Editor notes to the canonical user HTML stored by save payloads.
export function normalizeNotesHtml(value: string): string {
  const template = document.createElement('template');
  template.innerHTML = value.trim();

  template.content.querySelectorAll(forbiddenElements).forEach(element => {
    element.remove();
  });
  template.content.querySelectorAll('span.ql-ui').forEach(element => {
    element.remove();
  });
  template.content.querySelectorAll('*').forEach(element => {
    normalizeElementAttributes(element);
  });
  template.content.querySelectorAll<HTMLImageElement>('img').forEach(image => {
    const source = canonicalImageSource(image.getAttribute('src') ?? '');
    if (!isAllowedImageSource(source)) {
      image.remove();
      return;
    }

    image.setAttribute('src', source);
  });

  const html = template.innerHTML.trim();
  return html === '<p><br></p>' ? '' : html;
}

/// Restores proxied display URLs to the original external image URL saved in notes.
export function canonicalImageSource(value: string): string {
  const trimmedValue = stripUrlBoundaryControls(value);
  try {
    const url = new URL(trimmedValue, window.location.origin);
    if (url.origin !== window.location.origin || url.pathname !== '/Public/ProxyImage') {
      return trimmedValue;
    }

    return stripUrlBoundaryControls(url.searchParams.get('url') ?? trimmedValue);
  } catch {
    return trimmedValue;
  }
}

/// Detects embedded images before Quill can move them into the editor DOM.
export function containsDataImageReference(value: string): boolean {
  return compactUrlText(value).includes('data:image');
}

/// Detects embedded image URLs entered through the image dialog.
export function isDataImageSource(value: string): boolean {
  return compactUrlScheme(value).startsWith('data:image');
}

/// Rejects every non-http(s) image source after canonical proxy URL restoration.
export function isUnsafeImageSource(value: string): boolean {
  return !isAllowedImageSource(canonicalImageSource(value));
}

function normalizeElementAttributes(element: Element): void {
  Array.from(element.attributes).forEach(attribute => {
    const name = attribute.name.toLowerCase();
    if (name === 'class') {
      normalizeClassAttribute(element);
      return;
    }

    if (!isAllowedElementAttribute(element, name) || isUnsafeAttributeUrl(element, name, attribute.value)) {
      element.removeAttribute(attribute.name);
    }
  });
}

function normalizeClassAttribute(element: Element): void {
  const allowedClasses = Array.from(element.classList).filter(className => isAllowedClass(element, className));
  if (allowedClasses.length > 0) {
    element.setAttribute('class', allowedClasses.join(' '));
    return;
  }

  element.removeAttribute('class');
}

function isAllowedClass(element: Element, className: string): boolean {
  // Quill's font dropdown stores user-visible font choices as span classes.
  return element.tagName.toLowerCase() === 'span' && allowedQuillFontClasses.has(className);
}

function isAllowedElementAttribute(element: Element, name: string): boolean {
  const tagName = element.tagName.toLowerCase();
  // Quill 2 stores the user-selected list kind on list items.
  return (tagName === 'a' && name === 'href')
    || (tagName === 'img' && name === 'src')
    || (tagName === 'li' && name === 'data-list' && isAllowedQuillListKind(element));
}

function isAllowedQuillListKind(element: Element): boolean {
  return allowedQuillListKinds.has(element.getAttribute('data-list') ?? '');
}

function isAllowedImageSource(value: string): boolean {
  const trimmedValue = stripUrlBoundaryControls(value);
  if (!trimmedValue || !compactUrlScheme(trimmedValue).startsWith('http')) {
    return false;
  }

  try {
    const url = new URL(trimmedValue);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function isUnsafeAttributeUrl(element: Element, name: string, value: string): boolean {
  if (name !== 'href' && name !== 'src' && name !== 'xlink:href') {
    return false;
  }

  if (element instanceof HTMLImageElement && name === 'src') {
    return false;
  }

  const normalizedValue = compactUrlScheme(value);
  return normalizedValue.startsWith('javascript:') || normalizedValue.startsWith('data:') || normalizedValue.startsWith('vbscript:');
}

function compactUrlScheme(value: string): string {
  return compactUrlText(stripUrlBoundaryControls(value).slice(0, 64));
}

function compactUrlText(value: string): string {
  return value.replace(/[\u0000-\u0020\u007f-\u009f]+/g, '').toLowerCase();
}

function stripUrlBoundaryControls(value: string): string {
  return value.replace(/^[\u0000-\u0020\u007f-\u009f]+|[\u0000-\u0020\u007f-\u009f]+$/g, '');
}
