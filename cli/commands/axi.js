const { spawn } = require('child_process');
const path = require('path');
const readline = require('readline');
const fs = require('fs');
const {
    getGatewayExePath,
    getToolDefinitionsPath,
    resolveConfigPathNoMutate,
    readJsonFileSafe,
    directoryLooksLikeKnowledgeBase,
    createConfigFile,
    patchClientConfig,
    discoverGeneXusInstallation
} = require('../lib/config');

function parseFieldSelection(raw) {
    if (!raw) return null;
    return raw.split(',').map((v) => v.trim()).filter(Boolean);
}

function pickFields(obj, selectedFields) {
    if (!selectedFields || selectedFields.length === 0) return obj;
    const out = {};
    for (const f of selectedFields) {
        if (Object.prototype.hasOwnProperty.call(obj, f)) {
            out[f] = obj[f];
        }
    }
    return out;
}

function resolveToolCategory(name) {
    const n = (name || '').toLowerCase();
    if (n.includes('read') || n.includes('list') || n.includes('query') || n.includes('inspect')) return 'read';
    if (n.includes('edit') || n.includes('write') || n.includes('create') || n.includes('refactor') || n.includes('add_variable')) return 'write';
    if (n.includes('analyze') || n.includes('summarize') || n.includes('explain') || n.includes('doc')) return 'analysis';
    if (n.includes('lifecycle') || n.includes('test') || n.includes('format') || n.includes('build')) return 'lifecycle';
    return 'other';
}

function usageEnvelope(message, exitCode) {
    return {
        error: { code: 'usage_error', message },
        help: [
            'Run `genexus-mcp help` for command reference.',
            'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` for non-interactive setup.'
        ],
        meta: { exitCode }
    };
}

function operationalErrorEnvelope(message, exitCode, help = []) {
    return {
        error: { code: 'operation_error', message },
        help,
        meta: { exitCode }
    };
}

function buildStatusData(cwd) {
    const configPath = resolveConfigPathNoMutate(cwd);
    const gatewayExePath = getGatewayExePath();
    const gatewayExeFound = fs.existsSync(gatewayExePath);
    const configFound = !!configPath;

    let kbLooksValid = false;
    let kbPath = null;
    let gxPath = null;
    let configSource = null;

    if (process.env.GX_CONFIG_PATH && fs.existsSync(process.env.GX_CONFIG_PATH)) {
        configSource = 'env';
    } else if (configPath) {
        configSource = 'cwd';
    }

    if (configPath) {
        const cfg = readJsonFileSafe(configPath);
        if (cfg) {
            kbPath = cfg.Environment && cfg.Environment.KBPath ? cfg.Environment.KBPath : null;
            gxPath = cfg.GeneXus && cfg.GeneXus.InstallationPath ? cfg.GeneXus.InstallationPath : null;
            if (kbPath) kbLooksValid = directoryLooksLikeKnowledgeBase(kbPath);
        }
    }

    const ready = configFound && gatewayExeFound;
    return { ready, configFound, gatewayExeFound, kbLooksValid, configPath, gatewayExePath, kbPath, gxPath, configSource };
}

async function probeGatewaySpawn(gatewayExePath) {
    if (!fs.existsSync(gatewayExePath)) {
        return { status: 'fail', detail: 'Gateway executable does not exist for spawn probe.' };
    }

    return await new Promise((resolve) => {
        let done = false;
        const finish = (result) => {
            if (done) return;
            done = true;
            resolve(result);
        };

        try {
            const child = spawn(gatewayExePath, ['--axi-spawn-probe'], {
                stdio: 'ignore',
                windowsHide: true,
                env: process.env
            });

            child.once('error', (err) => {
                finish({ status: 'fail', detail: `Spawn probe failed: ${err.message}` });
            });

            child.once('spawn', () => {
                setTimeout(() => {
                    try {
                        child.kill();
                    } catch {
                    }
                    finish({ status: 'pass', detail: 'Gateway process can be spawned (probe launched and terminated).' });
                }, 180);
            });

            setTimeout(() => {
                if (!done) {
                    try {
                        child.kill();
                    } catch {
                    }
                    finish({ status: 'warn', detail: 'Spawn probe timed out; process was force-stopped.' });
                }
            }, 900);
        } catch (err) {
            finish({ status: 'fail', detail: `Spawn probe threw: ${err.message}` });
        }
    });
}

async function handleStatus(options, ctx) {
    const data = buildStatusData(ctx.cwd);

    const ok = options.full
        ? {
            ready: data.ready,
            configFound: data.configFound,
            gatewayExeFound: data.gatewayExeFound,
            kbLooksValid: data.kbLooksValid,
            configSource: data.configSource,
            configPath: data.configPath,
            gatewayExePath: data.gatewayExePath,
            kbPath: data.kbPath,
            gxPath: data.gxPath
        }
        : {
            ready: data.ready,
            configFound: data.configFound,
            gatewayExeFound: data.gatewayExeFound,
            kbLooksValid: data.kbLooksValid
        };

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok,
            help: data.ready
                ? ['Run `genexus-mcp tools list --limit 10` to inspect available MCP tools.']
                : [
                    'Run `genexus-mcp doctor --full` for expanded checks.',
                    'Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` to create config.'
                ]
        }
    };
}

async function handleDoctor(options, ctx) {
    const data = buildStatusData(ctx.cwd);
    const toolDefPath = getToolDefinitionsPath();
    const toolDefsExists = fs.existsSync(toolDefPath);
    const gatewayExePath = getGatewayExePath();

    let toolCount = 0;
    if (toolDefsExists) {
        try {
            const parsed = JSON.parse(fs.readFileSync(toolDefPath, 'utf8'));
            if (Array.isArray(parsed)) toolCount = parsed.length;
        } catch {
            toolCount = 0;
        }
    }

    const kbPath = data.kbPath;
    const gxPath = data.gxPath;
    const kbExists = !!(kbPath && fs.existsSync(kbPath));
    const gxExeExists = !!(gxPath && fs.existsSync(path.join(gxPath, 'genexus.exe')));

    const checks = [
        { id: 'config_file', status: data.configFound ? 'pass' : 'fail', detail: data.configFound ? 'GX config file was found.' : 'GX config file is missing.' },
        { id: 'gateway_exe', status: data.gatewayExeFound ? 'pass' : 'fail', detail: data.gatewayExeFound ? 'Gateway executable is available.' : 'Gateway executable is missing.' },
        { id: 'kb_path_exists', status: kbExists ? 'pass' : 'warn', detail: kbExists ? 'Configured KB path exists.' : 'Configured KB path does not exist.' },
        { id: 'kb_shape', status: data.kbLooksValid ? 'pass' : 'warn', detail: data.kbLooksValid ? 'KB folder shape looks valid.' : 'KB markers were not found in configured KB path.' },
        { id: 'gx_installation', status: gxExeExists ? 'pass' : 'warn', detail: gxExeExists ? 'GeneXus installation has genexus.exe.' : 'Configured GeneXus installation is missing genexus.exe.' },
        { id: 'tool_definitions', status: toolDefsExists ? 'pass' : 'warn', detail: toolDefsExists ? `Tool definition file found (${toolCount} tools).` : 'tool_definitions.json is missing.' },
        { id: 'gx_env', status: process.env.GX_CONFIG_PATH ? 'pass' : 'warn', detail: process.env.GX_CONFIG_PATH ? 'GX_CONFIG_PATH env var is set.' : 'GX_CONFIG_PATH env var is not set for this process.' }
    ];

    if (options.full) {
        const probe = await probeGatewaySpawn(gatewayExePath);
        checks.push({ id: 'gateway_spawn_probe', status: probe.status, detail: probe.detail });
    } else {
        checks.push({ id: 'gateway_spawn_probe', status: 'warn', detail: 'Spawn probe skipped by default. Run doctor with --full.' });
    }

    const summary = checks.reduce((acc, row) => {
        acc[row.status] = (acc[row.status] || 0) + 1;
        return acc;
    }, { pass: 0, warn: 0, fail: 0 });

    const defaultFields = ['id', 'status', 'detail'];
    const selectedFields = parseFieldSelection(options.fields) || defaultFields;
    const limited = checks.slice(0, options.limit).map((row) => pickFields(row, selectedFields));

    const help = [];
    if (checks.length > options.limit) {
        help.push(`Run 'genexus-mcp doctor --limit ${checks.length}' for all checks.`);
    }
    if (!options.full) {
        help.push('Run `genexus-mcp doctor --full` to include runtime spawn probe.');
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                summary,
                checks: limited,
                returned: limited.length,
                total: checks.length
            },
            help,
            meta: { fields: selectedFields }
        }
    };
}

async function handleToolsList(options, ctx) {
    const toolDefPath = getToolDefinitionsPath();
    if (!fs.existsSync(toolDefPath)) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('tool_definitions.json not found.', ctx.EXIT_CODES.ERROR, ['Run `genexus-mcp doctor` to inspect installation health.'])
        };
    }

    let parsed;
    try {
        parsed = JSON.parse(fs.readFileSync(toolDefPath, 'utf8'));
    } catch (err) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(`Failed to parse tool_definitions.json: ${err.message}`, ctx.EXIT_CODES.ERROR, ['Validate the JSON file and rerun tools list.'])
        };
    }

    if (!Array.isArray(parsed)) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('tool_definitions.json is not an array.', ctx.EXIT_CODES.ERROR)
        };
    }

    const query = (options.query || '').toLowerCase();
    const rowsAll = parsed.map((tool) => {
        const description = typeof tool.description === 'string' ? tool.description : '';
        const truncated = !options.full && description.length > 160;
        return {
            name: tool.name || 'unknown',
            status: 'available',
            category: resolveToolCategory(tool.name || ''),
            required: Array.isArray(tool.inputSchema && tool.inputSchema.required) ? tool.inputSchema.required.length : 0,
            description: truncated ? `${description.slice(0, 160)}...` : description,
            descriptionChars: description.length,
            truncated
        };
    });

    const rowsFiltered = query
        ? rowsAll.filter((row) => row.name.toLowerCase().includes(query) || row.description.toLowerCase().includes(query))
        : rowsAll;

    const totals = rowsFiltered.reduce((acc, row) => {
        acc[row.category] = (acc[row.category] || 0) + 1;
        return acc;
    }, {});

    const defaultFields = ['name', 'status', 'required'];
    const selectedFields = parseFieldSelection(options.fields) || defaultFields;
    const rows = rowsFiltered.slice(0, options.limit).map((row) => pickFields(row, selectedFields));

    const help = [];
    if (rowsFiltered.length === 0) {
        help.push('No tools matched the current filter. Try `genexus-mcp tools list --fields name,status` without --query.');
    }
    if (rowsFiltered.length > options.limit) {
        help.push(`Run 'genexus-mcp tools list --limit ${rowsFiltered.length}${query ? ` --query ${query}` : ''}' for all matching items.`);
    }
    if (rowsFiltered.some((row) => row.truncated) && !options.full) {
        help.push('Run `genexus-mcp tools list --full --fields name,description` to view full descriptions.');
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                tools: rows,
                returned: rows.length,
                total: rowsFiltered.length,
                empty: rowsFiltered.length === 0
            },
            help,
            meta: {
                fields: selectedFields,
                query: options.query || null,
                totalByCategory: totals
            }
        }
    };
}

async function handleConfigShow(options, ctx) {
    const configPath = resolveConfigPathNoMutate(ctx.cwd);
    if (!configPath) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('No config.json was found. Set GX_CONFIG_PATH or place config.json in current directory.', ctx.EXIT_CODES.ERROR, ['Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>"` to create one.'])
        };
    }

    const config = readJsonFileSafe(configPath);
    if (config === null) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope('config.json exists but is not valid JSON.', ctx.EXIT_CODES.ERROR, ['Fix config.json and rerun `genexus-mcp config show`.'])
        };
    }

    const raw = fs.readFileSync(configPath, 'utf8');
    const rawChars = raw.length;
    const truncateLimit = 1200;
    const truncated = !options.full && rawChars > truncateLimit;

    const compact = {
        path: configPath,
        kbPath: config.Environment && config.Environment.KBPath ? config.Environment.KBPath : null,
        gxPath: config.GeneXus && config.GeneXus.InstallationPath ? config.GeneXus.InstallationPath : null,
        httpPort: config.Server && config.Server.HttpPort ? config.Server.HttpPort : null,
        mcpStdio: config.Server && typeof config.Server.McpStdio === 'boolean' ? config.Server.McpStdio : null,
        raw: truncated ? `${raw.slice(0, truncateLimit)}\n... (truncated, ${rawChars} chars total)` : raw
    };

    const selectedFields = parseFieldSelection(options.fields);
    const payload = selectedFields ? pickFields(compact, selectedFields) : compact;

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: payload,
            help: truncated ? ['Run `genexus-mcp config show --full` to view complete config content.'] : [],
            meta: { truncated, rawChars }
        }
    };
}

async function runInteractiveInit(ctx) {
    const defaultGx = discoverGeneXusInstallation() || 'C:\\Program Files (x86)\\GeneXus\\GeneXus18';

    if (!ctx.options.quiet) {
        ctx.stderr.write('GeneXus MCP setup wizard\n\n');
    }

    const rl = readline.createInterface({ input: process.stdin, output: ctx.stderr });
    const question = (text) => new Promise((resolve) => rl.question(text, (answer) => resolve(answer)));

    try {
        const kbAnswer = await question(`1) Knowledge Base folder path (default: ${ctx.cwd}):\n> `);
        const finalKb = String(kbAnswer || '').trim() || ctx.cwd;

        const gxAnswer = await question(`\n2) GeneXus installation path (default: ${defaultGx}):\n> `);
        const finalGx = String(gxAnswer || '').trim() || defaultGx;

        const created = createConfigFile(finalKb, finalGx);
        const patchResult = patchClientConfig(created.targetConfigPath);

        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'init',
                    mode: 'interactive',
                    configPath: created.targetConfigPath,
                    noOp: !created.changed,
                    clientsPatchedCount: patchResult.patched.length
                },
                help: patchResult.patched.length === 0 ? ['Set `GX_CONFIG_PATH` in your MCP client env to the generated config path.'] : [],
                meta: {
                    patchedClients: patchResult.patched,
                    failedClients: patchResult.failed
                }
            }
        };
    } catch (err) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(`Interactive init failed: ${err.message}`, ctx.EXIT_CODES.ERROR)
        };
    } finally {
        rl.close();
    }
}

async function handleInit(options, ctx) {
    if (options.interactive) {
        return runInteractiveInit({ ...ctx, options });
    }

    if (!options.kb || !options.gx) {
        return {
            exitCode: ctx.EXIT_CODES.USAGE,
            envelope: usageEnvelope('Missing required flags for non-interactive init. Use --kb and --gx.', ctx.EXIT_CODES.USAGE)
        };
    }

    try {
        const created = createConfigFile(options.kb, options.gx);
        let patchResult = { patched: [], failed: [] };
        if (options.writeClients) {
            patchResult = patchClientConfig(created.targetConfigPath);
        }

        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    action: 'init',
                    mode: 'non_interactive',
                    configPath: created.targetConfigPath,
                    configFound: true,
                    noOp: !created.changed,
                    clientsPatchedCount: patchResult.patched.length
                },
                help: patchResult.patched.length === 0 && !options.writeClients
                    ? ['Run `genexus-mcp init --kb "<kbPath>" --gx "<geneXusPath>" --write-clients` to patch supported clients.']
                    : [],
                meta: {
                    patchedClients: patchResult.patched,
                    failedClients: patchResult.failed
                }
            }
        };
    } catch (err) {
        return {
            exitCode: ctx.EXIT_CODES.ERROR,
            envelope: operationalErrorEnvelope(`Failed to write configuration: ${err.message}`, ctx.EXIT_CODES.ERROR)
        };
    }
}

function commandHelpMap() {
    return {
        status: {
            usage: 'genexus-mcp status [--full] [--format toon|json|text] [--quiet] [--no-color]',
            examples: ['genexus-mcp status', 'genexus-mcp status --full --format json']
        },
        doctor: {
            usage: 'genexus-mcp doctor [--full] [--fields f1,f2] [--limit N] [--format toon|json|text]',
            examples: ['genexus-mcp doctor', 'genexus-mcp doctor --full --format json']
        },
        tools: {
            usage: 'genexus-mcp tools list [--query text] [--fields f1,f2] [--limit N] [--full] [--format ...]',
            examples: ['genexus-mcp tools list', 'genexus-mcp tools list --query read --fields name,category --format json']
        },
        config: {
            usage: 'genexus-mcp config show [--full] [--fields f1,f2] [--format ...]',
            examples: ['genexus-mcp config show', 'genexus-mcp config show --full --format json']
        },
        init: {
            usage: 'genexus-mcp init --kb <path> --gx <path> [--write-clients] [--format ...] OR genexus-mcp init --interactive',
            examples: ['genexus-mcp init --kb "C:\\KBs\\MyKB" --gx "C:\\Program Files (x86)\\GeneXus\\GeneXus18"', 'genexus-mcp init --interactive']
        }
    };
}

function collapseHome(absPath) {
    const home = require('os').homedir();
    if (!absPath || !home) return absPath;
    if (absPath.toLowerCase().startsWith(home.toLowerCase())) {
        return `~${absPath.slice(home.length)}`;
    }
    return absPath;
}

async function handleHelp(targetCommand, ctx) {
    const binPath = collapseHome(process.argv[1] || process.execPath);
    const map = commandHelpMap();
    if (targetCommand && map[targetCommand]) {
        return {
            exitCode: ctx.EXIT_CODES.OK,
            envelope: {
                ok: {
                    bin: binPath,
                    command: targetCommand,
                    usage: map[targetCommand].usage,
                    examples: map[targetCommand].examples,
                    defaults: {
                        format: 'toon',
                        limit: 100
                    }
                },
                help: []
            }
        };
    }

    return {
        exitCode: ctx.EXIT_CODES.OK,
        envelope: {
            ok: {
                bin: binPath,
                command: 'genexus-mcp',
                description: 'GeneXus MCP launcher and AXI-oriented utility CLI',
                commands: ['status', 'doctor', 'tools list', 'config show', 'init', 'help'],
                defaults: { format: 'toon', limit: 100 }
            },
            help: [
                'Run `genexus-mcp <command> --help` for subcommand help.',
                'Without AXI subcommands, CLI works as MCP launcher passthrough.'
            ]
        }
    };
}

module.exports = {
    parseFieldSelection,
    pickFields,
    handleStatus,
    handleDoctor,
    handleToolsList,
    handleConfigShow,
    handleInit,
    handleHelp,
    usageEnvelope,
    operationalErrorEnvelope,
    commandHelpMap
};
