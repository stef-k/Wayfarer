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

/** Rasterizes one application-owned badge so leaflet-image captures both shape and text. */
export const routeBadgeDataUrl = label => {
    const width = label.length > 1 ? Math.max(34, 14 + label.length * 9) : 24;
    const canvas = document.createElement('canvas');
    canvas.width = width * 2;
    canvas.height = 48;
    const context = canvas.getContext('2d');
    context.scale(2, 2);
    context.fillStyle = '#0057b8';
    context.beginPath();
    context.roundRect(0, 0, width, 24, 12);
    context.fill();
    context.strokeStyle = '#ffffff';
    context.lineWidth = 2;
    context.stroke();
    context.fillStyle = '#ffffff';
    context.font = '700 12px sans-serif';
    context.textAlign = 'center';
    context.textBaseline = 'middle';
    context.fillText(label, width / 2, 12);
    return { url: canvas.toDataURL('image/png'), width, height: 24 };
};

const finiteLocation = input => Number.isFinite(input.longitude) && Number.isFinite(input.latitude)
    ? [input.longitude, input.latitude]
    : null;
