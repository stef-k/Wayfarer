/** Resolves a wall time in the edit-start zone; nonexistent/ambiguous DST times never silently pick an instant. */
export const locationWallTimeUtc = (wall, zone, ambiguity = '') => {
    const match = /^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d)(?::(\d\d)(\.\d{1,6})?)?$/.exec(wall);
    if (!match) throw new Error('Enter a valid local date and time.');
    const [, year, month, day, hour, minute, seconds = '00', fraction = ''] = match;
    const base = new Date(0);
    base.setUTCFullYear(Number(year), Number(month) - 1, Number(day));
    base.setUTCHours(Number(hour), Number(minute), Number(seconds), 0);
    const nominal = base.getTime();
    const formatter = new Intl.DateTimeFormat('sv-SE', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' });
    const formatted = value => formatter.format(new Date(value)).replace(' ', 'T');
    const wanted = `${year}-${month}-${day}T${hour}:${minute}:${seconds}`;
    const offsets = new Set();
    for (let hours = -36; hours <= 36; hours += 6) {
        const probe = nominal + hours * 3600000;
        offsets.add(Date.parse(`${formatted(probe)}Z`) - probe);
    }
    const candidates = [...offsets].map(offset => nominal - offset).filter(value => formatted(value) === wanted).sort((a, b) => a - b);
    if (!candidates.length) throw new Error(`That local time does not exist in ${zone}. Choose another time.`);
    if (candidates.length > 1 && !['earlier', 'later'].includes(ambiguity)) throw new Error(`That local time occurs twice in ${zone}. Choose earlier or later.`);
    const chosen = candidates[ambiguity === 'later' ? candidates.length - 1 : 0];
    return new Date(chosen).toISOString().replace('.000Z', `${fraction}Z`);
};

/** Dirty-field selection preserves untouched values, independent fields and microsecond UTC originals. */
export const locationEditCommand = (initial, current, explicitManual, clearNotes, zone, ambiguity) => {
    const changes = {};
    const groups = new Set(['uuid']);
    if (Number(current.Latitude) !== Number(initial.Latitude) || Number(current.Longitude) !== Number(initial.Longitude)) {
        if (!current.Latitude.trim() || !current.Longitude.trim()) throw new Error('Both coordinates are required.');
        changes.position = { op: 'set', latitude: Number(current.Latitude), longitude: Number(current.Longitude) };
        if (!Number.isFinite(changes.position.latitude) || !Number.isFinite(changes.position.longitude)) throw new Error('Coordinates must be finite.');
        groups.add('position'); groups.add('address');
    }
    if (current.LocalTimestamp !== initial.LocalTimestamp) {
        changes.eventUtc = { op: 'set', value: locationWallTimeUtc(current.LocalTimestamp, zone, ambiguity) }; groups.add('eventUtc');
    }
    if (clearNotes || current.Notes !== initial.Notes) {
        changes.notes = clearNotes ? { op: 'clear' } : { op: 'set', html: current.Notes }; groups.add('notesHtml');
    }
    if (current.SelectedActivityId !== initial.SelectedActivityId) {
        changes.activity = current.SelectedActivityId ? { op: 'set', id: Number(current.SelectedActivityId) } : { op: 'clear' }; groups.add('activityTypeId');
    }
    if (current.Altitude !== initial.Altitude) {
        changes.altitude = current.Altitude.trim() ? { op: 'set', value: Number(current.Altitude) } : { op: 'clear' }; groups.add('altitude');
    }
    if (explicitManual.length) {
        changes.manualAddress = { op: 'set', fields: Object.fromEntries(explicitManual.map(name => [name[0].toLowerCase() + name.slice(1), current[name] || null])) };
        groups.add('position'); groups.add('address');
    }
    return { changes, groups: [...groups] };
};
