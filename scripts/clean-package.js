const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const publishDir = path.join(root, 'publish');

function safeRemove(targetPath) {
    try {
        if (fs.existsSync(targetPath)) {
            fs.rmSync(targetPath, { recursive: true, force: true });
        }
    } catch {
    }
}

function removeMatchingLogs(dir) {
    if (!fs.existsSync(dir)) return;
    const entries = fs.readdirSync(dir, { withFileTypes: true });
    for (const entry of entries) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            removeMatchingLogs(full);
            continue;
        }

        const lower = entry.name.toLowerCase();
        if (lower.endsWith('.log') || lower.endsWith('.prev.log') || lower.includes('panic')) {
            safeRemove(full);
        }
    }
}

removeMatchingLogs(publishDir);
safeRemove(path.join(publishDir, 'worker', 'cache'));
safeRemove(path.join(publishDir, 'worker', 'search_index.json'));
safeRemove(path.join(publishDir, 'worker', 'DataTracing.log'));

console.log('[prepack] cleaned transient publish logs/cache');
