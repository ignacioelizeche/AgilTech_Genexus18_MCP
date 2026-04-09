#!/usr/bin/env node
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');

const cwdConfigPath = path.join(process.cwd(), 'config.json');
const args = process.argv.slice(2);

// Interactive Setup Wizard
if (args[0] === 'init' || args[0] === 'setup') {
    console.log('================================================');
    console.log('🚀 GeneXus MCP - Zero Configuration Setup Wizard');
    console.log('================================================\n');

    const readline = require('readline');
    const rl = readline.createInterface({ input: process.stdin, output: process.stdout });

    let defaultGx = "C:\\Program Files (x86)\\GeneXus\\GeneXus18";
    const possibleGxPaths = [
        "C:\\Program Files (x86)\\GeneXus\\GeneXus18",
        "C:\\Program Files (x86)\\GeneXus\\GeneXus17",
        "C:\\Program Files\\GeneXus\\GeneXus18"
    ];
    for (const p of possibleGxPaths) {
        if (fs.existsSync(path.join(p, 'genexus.exe'))) { defaultGx = p; break; }
    }

    rl.question('1. Enter your Knowledge Base folder path\n   (Default: ' + process.cwd() + '):\n   > ', (kbAnswer) => {
        const finalKb = kbAnswer.trim() || process.cwd();
        
        rl.question('\n2. Enter your GeneXus Installation path\n   (Default: ' + defaultGx + '):\n   > ', (gxAnswer) => {
            const finalGx = gxAnswer.trim() || defaultGx;
            
            const targetConfigPath = path.join(finalKb, 'config.json');
            const defaultConfig = {
                GeneXus: { InstallationPath: finalGx },
                Environment: { KBPath: finalKb }
            };
            
            try {
                if (!fs.existsSync(finalKb)) fs.mkdirSync(finalKb, { recursive: true });
                fs.writeFileSync(targetConfigPath, JSON.stringify(defaultConfig, null, 2));
                console.log('\n✅ Success! Configuration saved at: ' + targetConfigPath + '\n');
                console.log('If you are using a Global Agent (like Claude Desktop or Antigravity),');
                console.log('you MUST copy this exact path and put it in your AI configuration:\n');
                console.log(`    "env": {`);
                console.log(`       "GX_CONFIG_PATH": "${targetConfigPath.replace(/\\/g, '\\\\')}"`);
                console.log(`    }\n`);
            } catch (err) {
                console.error('\n❌ Failed to save configuration: ' + err.message);
            }
            rl.close();
            process.exit(0);
        });
    });
    return; // Stop execution
}

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
        // Smart Safety: Only auto-generate config if CWD actually looks like a GeneXus KB!
        // Desktop Agents like Claude or Antigravity often execute in their Program Files.
        const filesInCwd = fs.readdirSync(process.cwd());
        const isActuallyAKB = filesInCwd.some(f => f.toLowerCase().endsWith('.gxw') || f.toLowerCase() === 'knowledgebase.connection');

        if (isActuallyAKB) {
            console.error(`[genexus-mcp] Auto-discovered GeneXus at: ${foundGxPath}`);
            console.error(`[genexus-mcp] Generating default config.json for KB at: ${process.cwd()}`);
            
            const defaultConfig = {
                GeneXus: { InstallationPath: foundGxPath },
                Environment: { KBPath: process.cwd() }
            };
            
            fs.writeFileSync(cwdConfigPath, JSON.stringify(defaultConfig, null, 2));
            process.env.GX_CONFIG_PATH = cwdConfigPath;
        } else {
            console.error('[genexus-mcp] ERROR: Zero-Config failed. The current executing directory is NOT a GeneXus Knowledge Base.');
            console.error(`[genexus-mcp] CWD: ${process.cwd()}`);
            console.error('\n[!!] Fix this issue by running the interactive setup wizard:');
            console.error('     npx genexus-mcp init\n');
            process.exit(1);
        }
    } else {
        console.error('[genexus-mcp] ERROR: No config.json found in the current directory!');
        console.error('[genexus-mcp] Auto-discovery for GeneXus installation failed.');
        console.error('\n[!!] Fix this issue by running the interactive setup wizard:');
        console.error('     npx genexus-mcp init\n');
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
