# Plan: Automatic Worker Setup & Launch from the Unity Plugin

**Status:** Proposed
**Scope:** The Unity plugin (`com.npctts.unity`) detects, sets up, launches, and
monitors the Python TTS worker automatically, and shows the *player* progress
feedback while it happens.

---

## 1. Goals

1. **Zero manual setup for the happy path.** On first run the plugin creates the
   Python venv, installs dependencies, downloads the model (with consent), starts
   the worker, and waits until it is warm — all without the user opening a terminal.
2. **Automatic launch/reuse.** On subsequent runs the plugin starts the worker if
   it isn't already running, and reuses it if it is (health check + PID file).
3. **Player-visible progress.** A clear, phased progress display (text + progress
   bar) driven by real events — not fake timers — plus C# events so games can build
   custom UI (loading screens, diegetic NPC lines like "One moment, warming up the
   forge…").
4. **Clean lifecycle.** The plugin's child process is terminated on quit (configurable),
   and survives/tolerizes editor domain reloads.

### Non-goals (v1)

- Bundling or auto-installing a Python interpreter (we detect and give actionable
  errors instead; bundling a standalone Python is a later milestone).
- Automatic model download without user consent (a ~1.7 GB download must be opt-in).
- Windows support beyond "the code paths exist" (macOS/Linux validated first).
- Remote/multiplayer worker hosting (unchanged; autostart only manages a local
  loopback worker).

---

## 2. Current State (what exists today)

| Piece | Location | Notes |
|-------|----------|-------|
| Health polling | `NpcTtsManager.PingWorker()` | GETs `/v1/health` every `_healthInterval` s; sets `IsWorkerHealthy` / `WorkerStatus` |
| Health semantics | `Worker/src/server.py` | `status: "degraded"` = server up, model **not warm**; `"ok"` = ready to speak |
| Sync wait helper | `NpcTtsManager.WaitUntilHealthy(timeout)` | Blocking poll; useful for tests |
| HTTP client | `Runtime/Networking/WorkerClient.cs` | internal, UnityWebRequest-based |
| Reference startup logic | `Worker/start_worker.sh` | Kills old PID, launches `.venv/bin/python main.py`, polls readiness, writes `worker.pid` |
| Model downloader | `Worker/download_models.py` | HF download, no machine-readable progress yet |
| Editor tooling | `Editor/NpcTtsEditorWindow.cs` | Window → NPC TTS → Worker Manager (health check + probe) |

---

## 3. Proposed Architecture

New folder: `Packages/com.npctts.unity/Runtime/WorkerHost/` (same runtime asmdef).

```
NpcTtsManager ──uses──► WorkerLauncher ──runs──► ProcessRunner (System.Diagnostics.Process)
      │                        │                        │ stdout/stderr lines
      │ events                 │ WorkerSetupProgress    ▼
      ▼                        ▼                  SetupProgressParser
OnWorkerSetupProgress   Phase state machine         (pip / download / uvicorn)
OnWorkerReady                  │
OnWorkerSetupFailed            └──► HealthGate (polls /v1/health: degraded → ok)
```

### 3.1 `WorkerSetupPhase` (enum) + `WorkerSetupProgress` (struct)

```csharp
public enum WorkerSetupPhase {
    Idle, DetectPython, EnsureVenv, InstallDependencies, ConsentDownload,
    DownloadModel, StartServer, WaitForWarmup, Ready, Failed
}

public readonly struct WorkerSetupProgress {
    public WorkerSetupPhase Phase;
    public float OverallPercent;    // 0..1, -1 = indeterminate for this step
    public string Message;          // human-readable, player-facing
    public string Detail;           // last raw log line (dev/debug view)
    public string Error;            // non-null only in Failed
}
```

Overall percent = sum of **stage weights** (so the bar is monotonic even when
individual steps are indeterminate):

| Stage | Weight | Percent source |
|-------|--------|----------------|
| DetectPython | 0.02 | complete on success |
| EnsureVenv | 0.08 | indeterminate → 1 on success |
| InstallDependencies | 0.35 | pip line count / collected-package count |
| DownloadModel | 0.40 | bytes done / total (from progress lines) |
| StartServer | 0.05 | process spawned + port bound |
| WaitForWarmup | 0.10 | health `degraded` → `ok` |

If the venv/model already exist, skipped stages collapse and weights renormalize
(e.g., a warm machine with the model installed goes 0 → 100% in ~30 s).

### 3.2 `WorkerLauncher`

Plain class driven by `NpcTtsManager` coroutines (testable, no MonoBehaviour
needed for logic). Responsibilities:

1. **Resolve worker directory** — priority: inspector field → `NPCTTS_WORKER_DIR`
   env var → `<StreamingAssets>/Worker` (bundled builds) → dev checkout-relative
   `../Worker`.
2. **Detect Python** — configured path → `python3` on PATH → common install
   locations; validate `--version` ≥ 3.10. Failure message names the fix
   ("Install Python 3.10+ from python.org, then retry").
3. **Quick path** — if `worker.pid` exists and health answers, adopt the existing
   worker and jump straight to `WaitForWarmup`.
4. **Ensure venv** — `python -m venv .venv` if missing.
5. **Install dependencies** — `.venv/bin/python -m pip install -r requirements.txt`,
   parse stdout for progress.
6. **Consent + download model** — if no model under `Worker/models/` and
   `AutoDownloadModel` is *ask*, raise a consent request (UI shows size ~1.7 GB,
   "Download / Run in stub mode / Cancel"). Stub mode remains a first-class
   fallback so the game is always playable.
7. **Start server** — spawn `.venv/bin/python main.py --port … --device auto`
   with stdout/stderr redirected; write `worker.pid`; tee logs to
   `Logs/npctts-worker.log`.
8. **Warmup gate** — poll `/v1/health`; `degraded` → "Loading speech model…",
   `ok` → `Ready`.
9. **Monitor** — process exit or repeated health failures → re-launch with
   exponential backoff (max N attempts), then `Failed`.
10. **Stop** — `KillWorkerOnQuit` honors `Application.quitting` / `OnDestroy`;
    SIGTERM → wait → SIGKILL; removes the PID file. Stale-PID detection (pid file
    points at a dead/unrelated process) is handled by health + process-name check.

### 3.3 `ProcessRunner`

Thin wrapper over `System.Diagnostics.Process`:
- No shell (`UseShellExecute = false`), argument array, `WorkingDirectory` set
  (paths with spaces are the norm in Unity projects).
- `OutputDataReceived` / `ErrorDataReceived` → queue drained on the main thread
  each frame (events fire on worker threads; Unity APIs are off-limits there).
- Timeout + forced-kill support; child-process flag on Windows
  (`CREATE_NEW_PROCESS_GROUP` so we can send a controlled stop).

### 3.4 `SetupProgressParser` (pure, unit-testable)

Maps raw stdout lines → progress updates:

| Pattern | Meaning |
|---------|---------|
| `Collecting X` / `Downloading X (12.3 MB)` | pip stage progress |
| `Installing collected packages: …` | pip near-done |
| `PROGRESS {"phase":"download","file":…,"bytes_done":N,"total_bytes":M}` | **new machine-readable line** (see §4) |
| `Uvicorn running on http://127.0.0.1:8080` | server is listening → health polling can succeed |
| `No model found. Using stub mode.` | switch UI text to "stub voice" |
| Python tracebacks / `ERROR:` | capture as `Detail`, may trigger `Failed` |

### 3.5 Player-facing UI: `WorkerSetupOverlay`

- `Runtime/UI/WorkerSetupOverlay.cs` + a prefab created by an editor script
  (UGUI: dimmed panel, status `Text`, `Slider`, expandable detail log,
  **Retry** and **Continue in stub mode** buttons).
- `NpcTtsManager.ShowSetupUi` (default **on** when `AutoHostWorker` is on): the
  overlay instantiates itself only while setup is running, and fades out on `Ready`.
- Games that want their own UI just subscribe to the events instead.

### 3.6 `NpcTtsManager` additions

New inspector fields:

| Field | Default | Purpose |
|-------|---------|---------|
| `AutoHostWorker` | `EditorOnly` | `Off` / `EditorOnly` / `Always` (standalone builds too) |
| `WorkerDirectory` | *(empty = auto)* | override path resolution |
| `PythonExecutable` | *(empty = auto)* | override python discovery |
| `AutoDownloadModel` | `Ask` | `Ask` / `Always` / `Never` |
| `ShowSetupUi` | `true` | auto-instantiate the overlay |
| `KillWorkerOnQuit` | `true` | lifecycle policy |

New API/events:

```csharp
public event Action<WorkerSetupProgress> OnWorkerSetupProgress;
public event Action OnWorkerReady;
public event Action<string> OnWorkerSetupFailed;
public event Action<string, Func<bool>> OnDownloadConsentRequested; // (message, accepted?)

public void SetupAndStartWorker();   // manual trigger (also used by editor window)
public void RetryWorkerSetup();
public void StopHostedWorker();
public WorkerSetupProgress CurrentSetupProgress { get; }
```

Behavior changes:
- `Awake()` starts the launcher coroutine when `AutoHostWorker` applies; the
  existing `PingWorker` loop keeps running unchanged (it naturally adopts a worker
  that's already up).
- `Speak()` while setup is running: keep failing fast with `WorkerUnavailable`,
  but `WorkerStatus` now carries the phase string ("setup: downloading model 62%")
  so game code can decide to queue, retry, or show subtitles-only.

### 3.7 Editor integration

**Window → NPC TTS → Worker Manager** gets:
- **Setup & Start Worker** button (runs the same `WorkerLauncher` in the editor,
  streams progress into the window).
- Phase/percent display, "Open log" button, and a **Stop Worker** button.
- Setup state persists across domain reloads via `SessionState` so the window
  re-attaches to the running worker.

---

## 4. Required Worker (Python) Changes

Small, backwards-compatible edits in `Worker/`:

1. **`download_models.py --json-progress`** — emit one
   `PROGRESS {json}` line per file every ~1 % / 2 s with `file`, `bytes_done`,
   `total_bytes`, and a final `{"phase":"done"}` line. (HF `snapshot_download`
   progress callback; also update `Worker/setup_status.json` as a polling fallback
   if stdout ever gets buffered.)
2. **`main.py --ready-file PATH`** (optional nicety) — write the bound port + PID
   on successful startup; mostly subsumed by health polling, so optional.
3. **`worker.pid` ownership** — the worker itself may refresh the pid file at
   startup; keep the format (single PID line) compatible with `start_worker.sh`
   and the new C# launcher.
4. No changes to `/v1/health` schema — `degraded` → `ok` already gives us the
   warmup signal.

---

## 5. Milestones & File Changes

### M1 — Core launcher (start/stop/adopt)
**New:** `Runtime/WorkerHost/WorkerLauncher.cs`, `ProcessRunner.cs`,
`WorkerSetupProgress.cs` (+ `.meta` files generated by Unity)
**Edit:** `NpcTtsManager.cs` (fields, events, `SetupAndStartWorker`, quit hooks)
**Accept:** with a venv + model already present, Play Mode with `AutoHostWorker = EditorOnly`
brings the worker from *not running* to *ready* and speaks; quitting Unity (or stopping
Play Mode with `KillWorkerOnQuit`) kills it; a second launcher adopts the existing one.

### M2 — Full setup pipeline + progress parsing
**New:** `Runtime/WorkerHost/SetupProgressParser.cs`
**Edit:** `Worker/download_models.py` (`--json-progress`), `NpcTtsManager.cs` (consent event)
**Accept:** from a clean `Worker/` (no venv, no model) the pipeline completes with a
monotonic 0→100 % progress fed by real pip/download lines; parser covered by EditMode tests.

### M3 — Player UI
**New:** `Runtime/UI/WorkerSetupOverlay.cs`, overlay prefab (editor-generated under
`Samples~/` or generated at runtime to keep package bytes low)
**Accept:** cold start shows phase text + bar + detail log, consent dialog for the
download, "Continue in stub mode" fallback, fade-out on ready.

### M4 — Editor window + polish
**Edit:** `Editor/NpcTtsEditorWindow.cs` (Setup & Start / Stop buttons, progress display)
**Accept:** one-click editor workflow equivalent to the whole README quick start.

### M5 — Tests, docs, release
**New:** `Tests/EditMode/SetupProgressParserTests.cs`, `WorkerHostPathTests.cs`,
`Worker/tests/test_json_progress.py`; update `build-and-use.md`,
`unity6-getting-started.md`, `troubleshooting.md`, and the editor section of the
runtime API doc.
**Accept:** full green EditMode/PlayMode + pytest suites; cold-start walkthrough on
a fresh macOS user account.

---

## 6. Failure Handling & Messages (player-facing)

| Failure | Detection | Message | Recovery |
|---------|-----------|---------|----------|
| Python missing / too old | `--version` probe | "Python 3.10+ is required for voice. Install it and press Retry." | Retry button, stub mode |
| No network during deps/model install | pip/HF exit code, stderr | "Couldn't reach the package server. Check your connection." | Retry, stub mode |
| Port 8080 in use | uvicorn stderr / health by foreign process | "Another app is using the voice server's port." | Auto-retry on port 8081+ (and update endpoint), or user override |
| Download consent denied | consent event → user cancels | — | Stub mode, voice disabled |
| Disk space | HF error / pre-flight `GetDiskFreeSpace` check | "Not enough disk space (need ~2 GB)." | Stub mode |
| Worker crashes at runtime | process exit / health N consecutive fails | "Voice server restarted." (silent, ≤N times) | Backoff relaunch → eventually Failed |
| Play-mode stop mid-setup | coroutine teardown | — | Launcher kills its own child unless another instance adopted it |

---

## 7. Risks & Decisions

1. **Standalone builds don't contain `Worker/`.** Decision for v1: `AutoHostWorker`
   defaults to **editor-only**. Shipping a standalone build with autostart requires
   bundling the worker sources into `StreamingAssets` (and a documented
   "first-run setup" that downloads deps/model); tracked as a follow-up milestone.
2. **Editor domain reloads** kill the driving coroutines but not the child process.
   Mitigation: PID-file adoption on re-Awake (the quick path), `SessionState` for
   editor UI state, and never killing a worker we didn't adopt.
3. **Console-app flash on Windows** — hide the child window via
   `CreateNoWindow = true`.
4. **pip installs into a venv inside the project** — acceptable (already the
   documented layout), but the consent step should state total size (deps + model).
5. **Race between two editors / game + editor** — single-instance via the PID file
   plus "adopt if healthy" makes concurrent launches safe in practice; a lock file
   can be added if flakiness shows up in tests.
6. **Security posture unchanged** — child process binds loopback only
   (`main.py` default), and the plugin never spawns shells.

---

## 8. Verification Plan

- **Unit (EditMode):** `SetupProgressParser` (fixture pip/uvicorn/HF output),
  worker-dir resolution precedence, stage-weight math, PID-file parsing/stale detection.
- **Integration (Python):** pytest for `--json-progress` against a mocked downloader.
- **PlayMode:** parser→event plumbing with a fake process source (no real python
  dependency in CI); overlay show/hide logic.
- **Manual cold-start drill (macOS):** rename `.venv` + `models/`, press Play, watch
  0→100 %, speak a line; repeat with consent denied (stub fallback) and with the
  worker already running (adoption path).
