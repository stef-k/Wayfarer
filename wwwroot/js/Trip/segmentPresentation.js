/** Derives a zero-based locale-independent ASCII bijective base-26 label. */
export const alphabeticAnchorLabel = position => {
    if (!Number.isSafeInteger(position) || position < 0) throw new TypeError('Anchor position must be a non-negative integer.');
    let remaining = position + 1;
    let label = '';
    while (remaining > 0) {
        remaining -= 1;
        label = String.fromCharCode(65 + remaining % 26) + label;
        remaining = Math.floor(remaining / 26);
    }
    return label;
};

/** Recalculates all labels and coalesces repeated canonical Places for this Segment only. */
export const resolveViewerAnchors = inputs => {
    let viaNumber = 0;
    const anchors = inputs.map((input, index) => {
        if (input.position !== index || !Number.isSafeInteger(input.position)) throw new TypeError('Anchor positions must be complete and ordered.');
        const role = String(input.role).toLowerCase().startsWith('via') ? 'via' : String(input.role).toLowerCase();
        if (role === 'via') viaNumber += 1;
        const roleText = role === 'start' ? 'Start' : role === 'end' ? 'End' : `Via ${viaNumber}`;
        return { ...input, role, roleText, label: alphabeticAnchorLabel(index), location: finiteLocation(input) };
    });
    const badges = new Map();
    anchors.forEach(anchor => {
        if (!anchor.placeId || !anchor.location) return;
        const existing = badges.get(anchor.placeId);
        if (existing) existing.label += `/${anchor.label}`;
        else badges.set(anchor.placeId, { placeId: anchor.placeId, label: anchor.label, location: anchor.location });
    });
    return {
        anchors,
        badges: [...badges.values()],
        compactTrail: anchors.map(anchor => `${anchor.label} ${anchor.name}`).join(' → '),
        tooltip: anchors.map(anchor => `${anchor.label} — ${anchor.roleText} — ${anchor.name}`).join('<br>'),
        accessibleName: anchors.length >= 2
            ? `Segment from ${anchors[0].name}${anchors.length > 2 ? ` via ${anchors.slice(1, -1).map(anchor => anchor.name).join(', then ')}` : ''} to ${anchors.at(-1).name}`
            : 'Segment journey unavailable'
    };
};

/** Returns a forward-presented coordinate copy while preserving the persisted input. */
export const presentViewerCoordinates = (coordinates, orientation) => orientation === 'reversed'
    ? [...coordinates].reverse().map(point => [...point])
    : coordinates.map(point => [...point]);

/** Places issue-approved cues from coordinates already projected into layer pixels. */
export const placeViewerChevrons = (points, active) => {
    const cumulative = [0];
    for (let index = 1; index < points.length; index += 1) {
        if (!points[index].every(Number.isFinite)) return [];
        cumulative.push(cumulative[index - 1] + Math.hypot(points[index][0] - points[index - 1][0], points[index][1] - points[index - 1][1]));
    }
    const length = cumulative.at(-1) ?? 0;
    if (length < 24) return [];
    const count = Math.min(active ? 8 : 4, Math.max(1, Math.floor((length - 48) / (active ? 72 : 120)) + 1));
    const distances = length < 48
        ? active ? [length / 2] : []
        : Array.from({ length: count }, (_, index) => 24 + (index + 1) * (length - 48) / (count + 1));
    const interpolate = distance => {
        let index = 1;
        while (index < cumulative.length - 1 && distance > cumulative[index]) index += 1;
        const leg = cumulative[index] - cumulative[index - 1];
        const ratio = leg ? (distance - cumulative[index - 1]) / leg : 0;
        return [points[index - 1][0] + (points[index][0] - points[index - 1][0]) * ratio,
            points[index - 1][1] + (points[index][1] - points[index - 1][1]) * ratio];
    };
    return distances.flatMap(distance => {
        const point = interpolate(distance);
        const before = interpolate(Math.max(0, distance - 6));
        const after = interpolate(Math.min(length, distance + 6));
        const dx = after[0] - before[0];
        const dy = after[1] - before[1];
        return Math.hypot(dx, dy) < 4 ? [] : [{ x: point[0], y: point[1], angle: Math.atan2(dy, dx) * 180 / Math.PI }];
    });
};

const routeBadgeOffsets = [[10, -18], [-34, -18], [10, -48], [-34, -48], [18, -34], [-42, -34]];

/** Chooses the first bounded route-badge position clear of controls and prior active badges. */
export const placeRouteBadge = (anchor, size, mapBounds, controlBounds, placedBounds) => {
    const candidate = (offset, offsetIndex, fallback = false) => ({
        left: anchor[0] + offset[0], top: anchor[1] + offset[1], width: size.width, height: size.height, offsetIndex, fallback
    });
    const clear = placement => {
        const rectangle = {...placement, right: placement.left + placement.width, bottom: placement.top + placement.height};
        return rectangle.left >= mapBounds.left && rectangle.top >= mapBounds.top
            && rectangle.right <= mapBounds.right && rectangle.bottom <= mapBounds.bottom
            && ![...controlBounds, ...placedBounds].some(blocker => rectangle.left < blocker.right
                && rectangle.right > blocker.left && rectangle.top < blocker.bottom && rectangle.bottom > blocker.top);
    };
    for (let index = 0; index < routeBadgeOffsets.length; index += 1) {
        const placement = candidate(routeBadgeOffsets[index], index);
        if (clear(placement)) return placement;
    }
    return candidate(routeBadgeOffsets[0], -1, true);
};

/** Searches finite blocker-edge coordinates for one bounded combined pill, then falls back deterministically. */
export const placeCombinedRouteBadge = (anchors, size, mapBounds, controlBounds, placedBounds) => {
    const inset = 4;
    const gap = 4;
    const usable = {left: mapBounds.left + inset, top: mapBounds.top + inset,
        right: mapBounds.right - inset, bottom: mapBounds.bottom - inset};
    const blockers = [...controlBounds, ...placedBounds].map(blocker => ({
        left: blocker.left - gap, top: blocker.top - gap, right: blocker.right + gap, bottom: blocker.bottom + gap
    }));
    const preferred = placeRouteBadge(anchors[0], size, mapBounds, controlBounds, placedBounds);
    const clampX = value => Math.max(usable.left, Math.min(value, usable.right - size.width));
    const clampY = value => Math.max(usable.top, Math.min(value, usable.bottom - size.height));
    const bounded = (left, top) => ({...preferred, left: clampX(left), top: clampY(top),
        width: size.width, height: size.height, offsetIndex: -1, fallback: true});
    const preferredBounded = bounded(preferred.left, preferred.top);
    const xValues = uniqueSorted([usable.left, usable.right - size.width,
        ...blockers.flatMap(blocker => [blocker.left - size.width, blocker.right])].map(clampX));
    const yValues = uniqueSorted([usable.top, usable.bottom - size.height,
        ...blockers.flatMap(blocker => [blocker.top - size.height, blocker.bottom])].map(clampY));
    return yValues.flatMap(top => xValues.map(left => bounded(left, top))).find(candidate => {
        const rectangle = {...candidate, right: candidate.left + candidate.width, bottom: candidate.top + candidate.height};
        return rectangle.left >= usable.left && rectangle.top >= usable.top
            && rectangle.right <= usable.right && rectangle.bottom <= usable.bottom
            && !blockers.some(blocker => rectangle.left < blocker.right && rectangle.right > blocker.left
                && rectangle.top < blocker.bottom && rectangle.bottom > blocker.top);
    }) ?? preferredBounded;
};

/** Wraps only between semantic tokens unless one token alone must be split to preserve all characters. */
export const fitCombinedRouteBadgeLabels = (labels, maximumWidth) => {
    const width = Math.max(1, maximumWidth);
    const characterCapacity = Math.max(1, Math.floor((width - 14) / 9));
    const fittedTokens = labels.flatMap(label => label.length <= characterCapacity ? [label]
        : Array.from({length: Math.ceil(label.length / characterCapacity)}, (_, index) =>
            label.slice(index * characterCapacity, (index + 1) * characterCapacity)));
    const lines = [];
    fittedTokens.forEach(token => {
        const combined = lines.length ? `${lines.at(-1)}/${token}` : token;
        if (lines.length && routeBadgeDimensions(combined).width > width) lines.push(token);
        else if (lines.length) lines[lines.length - 1] = combined;
        else lines.push(token);
    });
    return {labels: [...labels], lines, width, height: 10 + lines.length * 14};
};

/** Rasterizes one application-owned badge so leaflet-image captures both shape and text. */
export const routeBadgeDataUrl = (label, layout = null) => {
    const lines = layout?.lines ?? [label];
    const {width, height} = layout ?? routeBadgeDimensions(label);
    const canvas = document.createElement('canvas');
    canvas.width = width * 2;
    canvas.height = height * 2;
    const context = canvas.getContext('2d');
    context.scale(2, 2);
    context.fillStyle = '#0057b8';
    context.beginPath();
    context.roundRect(0, 0, width, height, 12);
    context.fill();
    context.strokeStyle = '#ffffff';
    context.lineWidth = 2;
    context.stroke();
    context.fillStyle = '#ffffff';
    context.font = '700 12px sans-serif';
    context.textAlign = 'center';
    context.textBaseline = 'middle';
    lines.forEach((line, index) => context.fillText(line, width / 2, 5 + 7 + index * 14));
    return { url: canvas.toDataURL('image/png'), width, height };
};

/** Returns the production badge box without rasterizing an image that may be combined away. */
export const routeBadgeDimensions = label => ({width: label.length > 1 ? Math.max(34, 14 + label.length * 9) : 24, height: 24});
const uniqueSorted = values => [...new Set(values)].sort((left, right) => left - right);

const finiteLocation = input => Number.isFinite(input.longitude) && Number.isFinite(input.latitude)
    ? [input.longitude, input.latitude]
    : null;
