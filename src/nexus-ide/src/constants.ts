export const GX_SCHEME = "gxkb18";

// Configuration keys
export const CONFIG_SECTION = "genexus";
export const CONFIG_MCP_PORT = "mcpPort";
export const CONFIG_AUTO_START = "autoStartBackend";
export const CONFIG_KB_PATH = "kbPath";
export const CONFIG_INSTALL_PATH = "installationPath";

// Default values
export const DEFAULT_MCP_PORT = 5000;
export const DEFAULT_STATUS_BAR_TIMEOUT = 5000;
export const DEFAULT_GATEWAY_TIMEOUT = 600000; // 10 minutes
export const DISCOVERY_DELAY = 5000;
export const HEALTH_CHECK_INTERVAL = 10000;
export const HEALTH_CHECK_TIMEOUT = 5000;
export const HEALTH_CHECK_TIMEOUT_INDEXING = 15000;

// Command and View IDs
export const COMMAND_PREFIX = "nexus-ide";
export const VIEW_EXPLORER = "genexusExplorer";
export const VIEW_ACTIONS = "genexusActions";

// State keys
export const STATE_KEY_FOLDER_ADDED = "genexus.kbFolderAdded_V6";
export const STATE_KEY_MCP_DISCOVERY = "genexus.mcpDiscovery_V1";

// Context keys
export const CONTEXT_ACTIVE_PART = "genexus.activePart";
export const ROOT_PARENT_NAME = "Root Module";

// Gateway Modules
export const MODULE_KB = "KB";
export const MODULE_BUILD = "Build";
export const MODULE_SEARCH = "Search";
export const MODULE_ANALYZE = "Analyze";
export const MODULE_HEALTH = "Health";
export const MODULE_REFACTOR = "Refactor";
export const MODULE_WRITE = "Write";
