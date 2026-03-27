import json
import time
from pathlib import Path
from datetime import datetime

ANNOUNCEMENT_QUEUE_FILE = Path("announcement_queue.json")
WINDOW_TITLE_CONTAINS = "Evrima RCON"
EXACT_RCON_WINDOW_TITLE = "Evrima RCON - Connected to 68.168.208.54:11218"
REQUIRE_EXISTING_WINDOW = True
RETRY_COOLDOWN_SECONDS = 30


def load_json(path: Path, default):
    if path.exists():
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            return default
    return default


def save_json(path: Path, data):
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def load_queue():
    return load_json(ANNOUNCEMENT_QUEUE_FILE, [])


def save_queue(data):
    save_json(ANNOUNCEMENT_QUEUE_FILE, data)


def find_pending_job(queue_data):
    for job in queue_data:
        if job.get("status") == "PENDING":
            return job
    return None


def find_existing_rcon_window():
    try:
        import pygetwindow as gw
    except Exception:
        print("[RCON GUI EXECUTOR] FAILED (pyautogui/pygetwindow not available)")
        return None

    try:
        titles = [title for title in gw.getAllTitles() if title and title.strip()]
        print(f"[RCON GUI EXECUTOR] Candidate window titles: {titles}")

        selected_title = EXACT_RCON_WINDOW_TITLE if EXACT_RCON_WINDOW_TITLE in titles else None
        if not selected_title:
            for title in titles:
                if "evrima rcon" in title.lower():
                    selected_title = title
                    break

        if not selected_title:
            print("[RCON GUI EXECUTOR] No matching RCON window found")
            return None

        print(f"[RCON GUI EXECUTOR] Selected window: {selected_title}")
        target_window = gw.getWindowsWithTitle(selected_title)[0]
        return target_window
    except Exception:
        return None


def execute_announcement_with_gui(message: str) -> bool:
    try:
        import pyautogui
        import pygetwindow as gw
    except Exception:
        print("[RCON GUI EXECUTOR] FAILED (pyautogui/pygetwindow not available)")
        return False

    try:
        target_window = None
        if REQUIRE_EXISTING_WINDOW:
            target_window = find_existing_rcon_window()
            if not target_window:
                print("[RCON GUI EXECUTOR] Window not found, announcement left pending")
                return False
            print("[RCON GUI EXECUTOR] Found existing window")

        target_window.activate()
        time.sleep(0.6)

        active_window = gw.getActiveWindow()
        active_title = active_window.title if active_window and active_window.title else ""
        print(f"[RCON GUI EXECUTOR] Active window before typing: {active_title}")
        if "evrima rcon" not in active_title.lower():
            print("[RCON GUI EXECUTOR] SAFETY BLOCK - active window is not RCON")
            return False

        command = f"announce {message}"
        pyautogui.typewrite(command, interval=0.02)
        time.sleep(0.2)
        pyautogui.press("enter")
        time.sleep(0.6)

        return True
    except Exception:
        return False


def process_queue_once():
    queue_data = load_queue()
    job = find_pending_job(queue_data)
    if not job:
        return

    message = str(job.get("message", "")).strip()
    now_ts = time.time()
    last_attempt_value = job.get("last_attempt_at")
    if last_attempt_value:
        try:
            elapsed = now_ts - float(last_attempt_value)
            if elapsed < RETRY_COOLDOWN_SECONDS:
                print("[RCON GUI EXECUTOR] Cooldown active, leaving pending")
                return
        except Exception:
            pass

    if not message:
        job["status"] = "FAILED"
        job["completed_at"] = str(datetime.now())
        save_queue(queue_data)
        print("[RCON GUI EXECUTOR] FAILED")
        return

    print(f"[RCON GUI EXECUTOR] Executing announcement: {message}")
    job["last_attempt_at"] = str(now_ts)
    save_queue(queue_data)
    success = execute_announcement_with_gui(message)

    if success:
        job["status"] = "DONE"
        job["completed_at"] = str(datetime.now())
        save_queue(queue_data)
        print("[RCON GUI EXECUTOR] DONE")
    else:
        existing_window = find_existing_rcon_window()
        if REQUIRE_EXISTING_WINDOW and not existing_window:
            print("[RCON GUI EXECUTOR] Window not found, leaving pending")
            return
        job["status"] = "FAILED"
        job["completed_at"] = str(datetime.now())
        save_queue(queue_data)
        print("[RCON GUI EXECUTOR] FAILED")


def main():
    print("[RCON GUI EXECUTOR] started")
    while True:
        try:
            process_queue_once()
        except Exception as e:
            print(f"[RCON GUI EXECUTOR] FAILED {e}")
        time.sleep(3.0)


if __name__ == "__main__":
    main()
