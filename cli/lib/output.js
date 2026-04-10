const SCHEMA_VERSION = 'axi-cli/1';
const SUPPORTED_FORMATS = new Set(['toon', 'json', 'text']);

function stableSortKeys(obj) {
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return obj;
    const out = {};
    for (const key of Object.keys(obj).sort()) {
        out[key] = obj[key];
    }
    return out;
}

function escapeToonScalar(value) {
    const str = String(value).replace(/\r?\n/g, '\\n');
    if (/^[A-Za-z0-9._:/\\-]+$/.test(str)) return str;
    return `"${str.replace(/"/g, '\\"')}"`;
}

function formatToonValue(value, indent = '') {
    if (value === null || value === undefined) return 'null';
    if (typeof value === 'boolean' || typeof value === 'number') return String(value);
    if (typeof value === 'string') return escapeToonScalar(value);

    if (Array.isArray(value)) {
        if (value.length === 0) return '[]';

        const allObjects = value.every((item) => item && typeof item === 'object' && !Array.isArray(item));
        if (allObjects) {
            const keys = Object.keys(stableSortKeys(value[0]));
            const sameShape = value.every((item) => {
                const itemKeys = Object.keys(stableSortKeys(item));
                return itemKeys.length === keys.length && itemKeys.every((k, idx) => k === keys[idx]);
            });

            if (sameShape && keys.length > 0) {
                const lines = [`[${value.length}]{${keys.join(',')}}:`];
                for (const row of value) {
                    const sortedRow = stableSortKeys(row);
                    lines.push(`${indent}  ${keys.map((k) => escapeToonScalar(sortedRow[k] ?? '')).join(',')}`);
                }
                return `\n${lines.join('\n')}`;
            }
        }

        const lines = [`[${value.length}]:`];
        for (const item of value) {
            if (item && typeof item === 'object') {
                lines.push(`${indent}  -`);
                lines.push(formatToonObject(item, `${indent}    `));
            } else {
                lines.push(`${indent}  - ${formatToonValue(item, `${indent}  `)}`);
            }
        }
        return `\n${lines.join('\n')}`;
    }

    return `\n${formatToonObject(value, `${indent}  `)}`;
}

function formatToonObject(obj, indent = '') {
    const lines = [];
    const stable = stableSortKeys(obj);
    for (const [key, value] of Object.entries(stable)) {
        const rendered = formatToonValue(value, indent);
        if (rendered.startsWith('\n')) {
            lines.push(`${indent}${key}:${rendered}`);
        } else {
            lines.push(`${indent}${key}: ${rendered}`);
        }
    }
    return lines.join('\n');
}

function normalizeEnvelope(envelope) {
    const safe = envelope && typeof envelope === 'object' ? envelope : {};
    const meta = safe.meta && typeof safe.meta === 'object' ? safe.meta : {};
    return {
        ...safe,
        meta: {
            schemaVersion: SCHEMA_VERSION,
            ...meta
        }
    };
}

function renderOutput(envelope, format) {
    const normalized = normalizeEnvelope(envelope);
    const safeFormat = SUPPORTED_FORMATS.has(format) ? format : 'toon';

    if (safeFormat === 'json') {
        return `${JSON.stringify(normalized, null, 2)}\n`;
    }

    if (safeFormat === 'text') {
        const parts = [];
        if (normalized.ok) parts.push(`ok: ${JSON.stringify(normalized.ok)}`);
        if (normalized.error) parts.push(`error: ${JSON.stringify(normalized.error)}`);
        if (Array.isArray(normalized.help) && normalized.help.length > 0) parts.push(`help: ${normalized.help.join(' | ')}`);
        if (normalized.meta) parts.push(`meta: ${JSON.stringify(normalized.meta)}`);
        return `${parts.join('\n')}\n`;
    }

    return `${formatToonObject(normalized)}\n`;
}

function writeStructured(stream, envelope, format) {
    stream.write(renderOutput(envelope, format));
}

module.exports = {
    SCHEMA_VERSION,
    SUPPORTED_FORMATS,
    renderOutput,
    writeStructured,
    formatToonObject,
    normalizeEnvelope
};
