# Auto-Detect Knowledge Base

This file helps Claude Code automatically detect if the current workspace is a GeneXus Knowledge Base and configure the MCP accordingly.

## KB Detection Markers

The system looks for these files/folders to identify a GeneXus KB:

**File markers:**
- `.gxw` - GeneXus Workspace files
- `knowledgebase.connection` - KB connection file
- `genexus.ini` - GeneXus initialization file
- `.gxclass` - GeneXus class files
- `.gxproc` - GeneXus procedure files

**Folder structure:**
- `.gx/` - GeneXus internal folder
- `objects/` - Objects folder
- `web/` - Web folder
- `procedures/` - Procedures folder
- `data/` - Data folder
- `images/` - Images folder

## Auto-Detection Flow

When you open a folder in VS Code with Claude Code:

1. **CLI Layer** (`cli/lib/config.js`)
   - Detects KB markers recursively walking up parent directories
   - If KB found: Sets `GX_KB_PATH` environment variable automatically
   - User can run `genexus-mcp init` without flags → Auto-detects KB and GeneXus

2. **VS Code Extension** (`src/nexus-ide/src/managers/BackendManager.ts`)
   - Enhanced `findBestKbPath()` with recursive parent search
   - More file markers recognition
   - Priority: config → .gxw search → parent directory walk

3. **Claude Code** (This file)
   - When you open a KB folder, Claude automatically detects it
   - Notifies you that KB was detected
   - Configures MCP to work with that KB

## How It Works

### Scenario 1: Open KB Folder → Run genexus-mcp init

```bash
cd C:\KBs\MyKB
genexus-mcp init

# Output:
# ✓ Auto-detected KB at: C:\KBs\MyKB
# ✓ Auto-detected GeneXus at: C:\Program Files (x86)\GeneXus\GeneXus18
# ✓ Configuration created successfully
```

### Scenario 2: Open KB in VS Code → Extension Auto-Detects

1. Open folder `C:\KBs\MyKB` in VS Code
2. VS Code Extension auto-detects KB
3. MCP server starts automatically
4. You can immediately use genexus-mcp tools

### Scenario 3: Talk to Claude Code in a KB Folder

1. Open KB folder in VS Code with Claude Code
2. Claude detects it's a KB (looks for .gxw, genexus.ini, etc)
3. Auto-configures `GX_KB_PATH` environment variable
4. MCP commands work seamlessly

## Environment Variable

When KB is detected automatically:

```bash
# Claude Code / CLI sets this automatically
GX_KB_PATH=C:\KBs\MyKB

# This is passed to the MCP Gateway, which uses it
# No manual config.json editing needed!
```

## Backward Compatibility

- If `config.json` exists → Still used (no breaking changes)
- If `GX_KB_PATH` is set → Takes precedence
- If nothing found → Falls back to user input

## Testing Auto-Detection

To verify auto-detection is working:

```bash
# Test 1: CLI Auto-Init
cd C:\KBs\YourKB
genexus-mcp init
# Should auto-detect and not ask for --kb and --gx flags

# Test 2: Check env var
echo %GX_KB_PATH%
# Should show your KB path

# Test 3: VS Code Extension
code C:\KBs\YourKB
# Extension should load and show KB objects tree immediately
```

## Configuration Precedence

When looking for a KB, the system tries in this order:

1. `GX_KB_PATH` environment variable (if set)
2. `config.json` configured path (if exists)
3. Current directory is KB (if has markers)
4. Parent directories walking upward (recursive search)
5. Interactive prompt (if nothing found)

## Future Enhancements

- [ ] Support multiple KBs in workspace
- [ ] UI indicator showing detected KB path
- [ ] Quick-switch between KBs
- [ ] .genexus-mcp config file per KB folder
