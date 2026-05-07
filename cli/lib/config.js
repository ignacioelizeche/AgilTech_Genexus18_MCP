const path = require('path');
const fs = require('fs');
const os = require('os');

function generateConfig(gxPath, kbPath) {
    return {
        GeneXus: { InstallationPath: gxPath },
        Server: { HttpPort: 5000, McpStdio: true, SessionIdleTimeoutMinutes: 10, WorkerIdleTimeoutMinutes: 5 },
        Environment: { KBPath: kbPath }
    };
}

function getGatewayExePath() {
    if (process.env.GENEXUS_MCP_GATEWAY_EXE) {
        return process.env.GENEXUS_MCP_GATEWAY_EXE;
    }
    return path.join(__dirname, '..', '..', 'publish', 'GxMcp.Gateway.exe');
}

function getToolDefinitionsPath() {
    return path.join(__dirname, '..', '..', 'src', 'GxMcp.Gateway', 'tool_definitions.json');
}

function discoverGeneXusInstallation() {
    const possible = [
        'C:\\Program Files (x86)\\GeneXus\\GeneXus18',
        'C:\\Program Files (x86)\\GeneXus\\GeneXus17',
        'C:\\Program Files (x86)\\GeneXus\\GeneXus16',
        'C:\\Program Files\\GeneXus\\GeneXus18',
        'C:\\Program Files\\GeneXus\\GeneXus17'
    ];

    for (const candidate of possible) {
        if (fs.existsSync(path.join(candidate, 'genexus.exe'))) {
            return candidate;
        }
    }

    return null;
}

function directoryLooksLikeKnowledgeBase(dir) {
    try {
        const files = fs.readdirSync(dir);
        const dirs = fs.readdirSync(dir, { withFileTypes: true })
            .filter(d => d.isDirectory())
            .map(d => d.name.toLowerCase());

        // Check for KB file markers
        const hasKbFiles = files.some((f) => {
            const lower = f.toLowerCase();
            return lower.endsWith('.gxw') ||
                   lower === 'knowledgebase.connection' ||
                   lower === 'genexus.ini' ||
                   lower.endsWith('.gxclass') ||
                   lower.endsWith('.gxproc');
        });

        if (hasKbFiles) return true;

        // Check for KB folder structure
        const hasKbFolders = dirs.some((d) =>
            d === '.gx' ||
            d === 'objects' ||
            d === 'web' ||
            d === 'procedures' ||
            d === 'data' ||
            d === 'images'
        );

        return hasKbFolders;
    } catch {
        return false;
    }
}

function autoDetectKbPath(startDir = process.cwd()) {
    let current = startDir;

    while (current && current !== path.dirname(current)) {
        if (directoryLooksLikeKnowledgeBase(current)) {
            return current;
        }
        current = path.dirname(current);
    }

    return null;
}

function readJsonFileSafe(filePath) {
    try {
        const raw = fs.readFileSync(filePath, 'utf8').replace(/^\uFEFF/, '');
        if (!raw.trim()) return {};
        return JSON.parse(raw);
    } catch {
        return null;
    }
}

function resolveConfigPathNoMutate(cwd) {
    const cwdConfigPath = path.join(cwd, 'config.json');
    if (process.env.GX_CONFIG_PATH && fs.existsSync(process.env.GX_CONFIG_PATH)) {
        return process.env.GX_CONFIG_PATH;
    }
    if (fs.existsSync(cwdConfigPath)) {
        return cwdConfigPath;
    }
    return null;
}

function createConfigFile(kbPath, gxPath) {
    const targetConfigPath = path.join(kbPath, 'config.json');
    const nextConfig = generateConfig(gxPath, kbPath);

    if (!fs.existsSync(kbPath)) {
        fs.mkdirSync(kbPath, { recursive: true });
    }

    let changed = true;
    if (fs.existsSync(targetConfigPath)) {
        const current = readJsonFileSafe(targetConfigPath);
        if (current && JSON.stringify(current) === JSON.stringify(nextConfig)) {
            changed = false;
        }
    }

    if (changed) {
        fs.writeFileSync(targetConfigPath, JSON.stringify(nextConfig, null, 2));
    }

    return {
        targetConfigPath,
        config: nextConfig,
        changed
    };
}

function patchClientConfig(targetConfigPath) {
    const claudeWin = path.join(os.homedir(), 'AppData', 'Roaming', 'Claude', 'claude_desktop_config.json');
    const claudeMac = path.join(os.homedir(), 'Library', 'Application Support', 'Claude', 'claude_desktop_config.json');
    const antigravityCfg = path.join(os.homedir(), '.gemini', 'antigravity', 'mcp_config.json');
    const claudeCodeCfg = path.join(os.homedir(), '.claude.json');

    const clients = [
        { path: claudeWin, name: 'Claude Desktop (Windows)' },
        { path: claudeMac, name: 'Claude Desktop (macOS)' },
        { path: antigravityCfg, name: 'Antigravity' },
        { path: claudeCodeCfg, name: 'Claude Code' }
    ];

    const patched = [];
    const failed = [];

    for (const client of clients) {
        if (!fs.existsSync(client.path)) continue;

        try {
            const parsed = readJsonFileSafe(client.path);
            if (parsed === null) {
                failed.push({ client: client.name, reason: 'Invalid JSON' });
                continue;
            }

            const cfgObj = parsed || {};
            cfgObj.mcpServers = cfgObj.mcpServers || {};
            cfgObj.mcpServers.genexus = {
                command: process.platform === 'win32' ? 'npx.cmd' : 'npx',
                args: ['-y', 'genexus-mcp@latest'],
                env: { GX_CONFIG_PATH: targetConfigPath }
            };

            fs.writeFileSync(client.path, JSON.stringify(cfgObj, null, 2));
            patched.push(client.name);
        } catch (err) {
            failed.push({ client: client.name, reason: err && err.message ? err.message : 'Unknown error' });
        }
    }

    return { patched, failed };
}

function applyLauncherConfigOrExit({ cwd, stderr, quiet }) {
    const log = (msg) => {
        if (!quiet) stderr.write(`${msg}\n`);
    };

    const cwdConfigPath = path.join(cwd, 'config.json');

    if (process.env.GX_CONFIG_PATH) {
        return { ok: true };
    }

    if (fs.existsSync(cwdConfigPath)) {
        process.env.GX_CONFIG_PATH = cwdConfigPath;
        return { ok: true };
    }

    const foundGxPath = discoverGeneXusInstallation();
    if (!foundGxPath) {
        log('[genexus-mcp] ERROR: No config.json found and GeneXus installation auto-discovery failed.');
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    if (!directoryLooksLikeKnowledgeBase(cwd)) {
        log('[genexus-mcp] ERROR: Zero-config failed because current directory is not a GeneXus KB.');
        log(`[genexus-mcp] CWD: ${cwd}`);
        log('[genexus-mcp] Fix with: npx genexus-mcp init --interactive');
        return { ok: false };
    }

    log(`[genexus-mcp] Auto-discovered GeneXus at: ${foundGxPath}`);
    log(`[genexus-mcp] Generating default config.json for KB at: ${cwd}`);

    const defaultConfig = generateConfig(foundGxPath, cwd);
    fs.writeFileSync(cwdConfigPath, JSON.stringify(defaultConfig, null, 2));
    process.env.GX_CONFIG_PATH = cwdConfigPath;

    return { ok: true };
}

module.exports = {
    generateConfig,
    getGatewayExePath,
    getToolDefinitionsPath,
    discoverGeneXusInstallation,
    directoryLooksLikeKnowledgeBase,
    autoDetectKbPath,
    readJsonFileSafe,
    resolveConfigPathNoMutate,
    createConfigFile,
    patchClientConfig,
    applyLauncherConfigOrExit
};
