import json
import time
import pyautogui
from pathlib import Path
from datetime import datetime

GAME_COMMANDS_FILE = Path("game_commands.json")


def load_commands():
    if GAME_COMMANDS_FILE.exists():
        return json.loads(GAME_COMMANDS_FILE.read_text())
    return []


def save_commands(data):
    GAME_COMMANDS_FILE.write_text(json.dumps(data, indent=2))


def type_command(cmd):
    pyautogui.press("enter")
    time.sleep(0.2)
    pyautogui.write(cmd)
    time.sleep(0.2)
    pyautogui.press("enter")


def command_delay_seconds(command_text: str) -> int:
    command = str(command_text or "").strip().lower()
    if command.startswith("/elder ") and command.endswith(" prime"):
        return 5
    if command.startswith("/hunger "):
        return 3
    return 3


def parse_command_id(command_entry) -> int:
    raw_id = str(command_entry.get("id", "0")).replace("cmd_", "")
    try:
        return int(raw_id)
    except Exception:
        return 0


def build_group_sort_key(group_commands):
    if not group_commands:
        return 0

    first = min(group_commands, key=lambda c: (parse_command_id(c), str(c.get("created_at", ""))))
    return parse_command_id(first)


def get_active_group_commands(commands):
    grouped = {}
    for cmd in commands:
        group_id = cmd.get("claim_group_id")
        if not group_id:
            continue

        status = cmd.get("status")
        if status in {"PENDING", "EXECUTING"}:
            grouped.setdefault(group_id, []).append(cmd)

    if not grouped:
        return None, []

    oldest_group_id = min(grouped.keys(), key=lambda gid: build_group_sort_key(grouped[gid]))
    full_group = [c for c in commands if c.get("claim_group_id") == oldest_group_id]

    full_group.sort(key=lambda c: (
        int(c.get("claim_step", 9999)) if str(c.get("claim_step", "")).isdigit() else 9999,
        parse_command_id(c),
        str(c.get("created_at", "")),
    ))

    return oldest_group_id, full_group


def execute_single_command(commands, cmd):
    command_text = cmd.get("command", "")

    cmd["status"] = "EXECUTING"
    cmd["started_at"] = str(datetime.now())
    save_commands(commands)

    try:
        type_command(command_text)
        cmd["status"] = "DONE"
        cmd["completed_at"] = str(datetime.now())
        cmd.pop("error", None)
        save_commands(commands)
        time.sleep(command_delay_seconds(command_text))
        return True
    except Exception as e:
        cmd["status"] = "FAILED"
        cmd["completed_at"] = str(datetime.now())
        cmd["error"] = str(e)
        save_commands(commands)
        return False


def process_group(commands, group_id, group_commands):
    if not group_commands:
        return False

    group_player = group_commands[0].get("player_name", "Unknown")
    group_steam = group_commands[0].get("steam_id", "Unknown")

    print(f"[GROUP START] group={group_id} player={group_player} steam={group_steam}")

    for cmd in group_commands:
        status = cmd.get("status")
        step = cmd.get("claim_step", "?")
        command_text = cmd.get("command", "")

        if status == "DONE":
            continue

        if status == "FAILED":
            print(f"[GROUP STOP] group={group_id} step={step} already FAILED")
            return True

        if status not in {"PENDING", "EXECUTING"}:
            continue

        print(
            f"[GROUP EXEC] group={group_id} step={step} "
            f"player={group_player} steam={group_steam} cmd={command_text}"
        )

        ok = execute_single_command(commands, cmd)
        if not ok:
            print(
                f"[GROUP FAIL] group={group_id} step={step} "
                f"player={group_player} steam={group_steam} cmd={command_text}"
            )
            return True

    print(f"[GROUP DONE] group={group_id} player={group_player} steam={group_steam}")
    return True


def process_legacy_command(commands):
    for cmd in commands:
        if cmd.get("claim_group_id"):
            continue
        if cmd.get("status") not in {"PENDING", "EXECUTING"}:
            continue

        command_text = cmd.get("command", "")
        print(f"[LEGACY EXEC] id={cmd.get('id')} cmd={command_text}")
        execute_single_command(commands, cmd)
        return True

    return False


def main():
    print("IN-GAME EXECUTOR STARTED")

    while True:
        commands = load_commands()

        group_id, group_commands = get_active_group_commands(commands)
        did_work = False

        if group_id:
            did_work = process_group(commands, group_id, group_commands)
        else:
            did_work = process_legacy_command(commands)

        if not did_work:
            time.sleep(1)


if __name__ == "__main__":
    main()
