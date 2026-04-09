#!/usr/bin/env node
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

const cwdConfigPath = path.join(process.cwd(), 'config.json');

// Check if config.json exists in CWD. If so, bind it to GX_CONFIG_PATH.
if (fs.existsSync(cwdConfigPath)) {
    process.env.GX_CONFIG_PATH = cwdConfigPath;
} else if (!process.env.GX_CONFIG_PATH) {
    const possibleGxPaths = [
        "C:\\Program Files (x86)\\GeneXus\\GeneXus18",
        "C:\\Program Files (x86)\\GeneXus\\GeneXus17",
        "C:\\Program Files (x86)\\GeneXus\\GeneXus16",
        "C:\\Program Files\\GeneXus\\GeneXus18",
        "C:\\Program Files\\GeneXus\\GeneXus17"
    ];

    let foundGxPath = null;
    for (const p of possibleGxPaths) {
        if (fs.existsSync(path.join(p, 'genexus.exe'))) {
            foundGxPath = p;
            break;
        }
    }

    if (foundGxPath) {
        console.error(`[genexus-mcp] Auto-discovered GeneXus at: ${foundGxPath}`);
        console.error(`[genexus-mcp] Generating default config.json for KB at: ${process.cwd()}`);
        
        const defaultConfig = {
            GeneXus: { InstallationPath: foundGxPath },
            Environment: { KBPath: process.cwd() }
        };
        
        fs.writeFileSync(cwdConfigPath, JSON.stringify(defaultConfig, null, 2));
        process.env.GX_CONFIG_PATH = cwdConfigPath;
    } else {
        console.error('[genexus-mcp] ERROR: No config.json found in the current directory!');
        console.error('[genexus-mcp] Auto-discovery for GeneXus installation failed.');
        console.error('[genexus-mcp] Please create a config.json file manually with at least the KBPath and GeneXus InstallationPath.');
        process.exit(1);
    }
}

// Locate the bundled .NET executable inside the publish folder
const gatewayExePath = path.join(__dirname, '..', 'publish', 'GxMcp.Gateway.exe');

if (!fs.existsSync(gatewayExePath)) {
    console.error(`[genexus-mcp] ERROR: The gateway executable was not found at ${gatewayExePath}`);
    console.error(`[genexus-mcp] Please ensure you installed the package correctly on a Windows environment.`);
    process.exit(1);
}

// Pass everything transparently through stdio
const child = spawn(gatewayExePath, process.argv.slice(2), {
    stdio: 'inherit',
    env: process.env,
    windowsHide: true,
    shell: true
});

child.on('error', (err) => {
    console.error('[genexus-mcp] ERROR: Failed to start the MCP Gateway process:', err.message);
    process.exit(1);
});

child.on('exit', (code, signal) => {
    if (signal) {
        process.exit(1);
    }
    process.exit(code || 0);
});
