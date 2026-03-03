import json
import subprocess
import os
import time
import threading

def load_config():
    config_path = os.path.join(os.path.dirname(__file__), "..", "src", "nexus-ide", "backend", "config.json")
    if os.path.exists(config_path):
        with open(config_path, "r") as f:
            return json.load(f)
    return {}

config = load_config()
worker_path = os.environ.get("GX_WORKER_EXE") or config.get("WorkerExecutable") or r"C:\Projetos\GenexusMCP\src\GxMcp.Worker\bin\Release\GxMcp.Worker.exe"
kb_path = os.environ.get("GX_KB_PATH") or config.get("KBPath")
gx_dir = os.environ.get("GX_PROGRAM_DIR") or config.get("InstallationPath")

def run_validation():
    env = os.environ.copy()
    env["GX_KB_PATH"] = kb_path
    env["GX_PROGRAM_DIR"] = gx_dir
    
    print(f"Starting Worker with KB: {kb_path}")
    process = subprocess.Popen(
        [worker_path, "--kb", kb_path],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding='utf-8',
        env=env,
        bufsize=0
    )
    
    sdk_ready = threading.Event()
    
    def read_stderr():
        while True:
            line = process.stderr.readline()
            if not line: break
            if "Full SDK Initialization SUCCESS" in line or "Worker SDK ready" in line:
                sdk_ready.set()
                
    stderr_thread = threading.Thread(target=read_stderr, daemon=True)
    stderr_thread.start()

    print("Waiting for SDK initialization...")
    if not sdk_ready.wait(timeout=60):
        print("Failed to initialize SDK in time.")
        process.kill()
        return

    print("SDK Initialized. Running validation commands...")

    def call_worker_live(method, action, target, recursive=False):
        payload = {
            "jsonrpc": "2.0",
            "id": int(time.time() * 1000),
            "method": method,
            "action": action,
            "target": target,
            "params": {"recursive": recursive}
        }
        process.stdin.write(json.dumps(payload) + "\n")
        process.stdin.flush()
        
        while True:
            line = process.stdout.readline()
            if not line: return {"error": "Process died"}
            if line.startswith('{"jsonrpc"'):
                return json.loads(line)

    candidates = ["ConsultaBoletosPagosBradesco", "SdtUsuario", "VTipoPessoa"]
    results = {}

    for cand in candidates:
        print(f"\nProcessing: {cand}")
        cand_res = {}
        
        print(f"  - InjectContext (recursive=True)")
        cand_res["inject_recursive"] = call_worker_live("analyze", "InjectContext", cand, recursive=True)
        
        print(f"  - GetConversionContext (Inspect with Domains)")
        cand_res["inspect"] = call_worker_live("analyze", "GetConversionContext", cand)
        
        print(f"  - GetDataContext (Fallback check)")
        cand_res["data_ctx"] = call_worker_live("analyze", "GetDataContext", cand)
        
        results[cand] = cand_res

    # Special check: Look for a Business Component manually in the index to test it
    print("\nProcessing special case: Business Component")
    bc_cand = "DVT0001" # From earlier Transaction list, high probability of being BC
    # For now, let's just try to inject it
    print(f"  - InjectContext for BC candidate: {bc_cand}")
    results["BC_Test"] = call_worker_live("analyze", "InjectContext", bc_cand)

    with open("validation_results_elite.json", "w", encoding='utf-8') as f:
        json.dump(results, f, indent=2)

    print("\nValidation complete. Results saved to validation_results_elite.json")
    process.terminate()

if __name__ == "__main__":
    run_validation()
