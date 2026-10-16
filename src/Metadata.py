"""Read scalar metadata and one bounded, exactly matched user input; no history output."""
import json
import re
import sqlite3
import sys
import time
from pathlib import Path


def visible_input(text):
    text = text.strip()
    prefix = "<send_user_message_question_reply>"
    suffix = "</send_user_message_question_reply>"
    if text.startswith(prefix) and text.endswith(suffix):
        try:
            replies = json.loads(text[len(prefix):-len(suffix)])
            if isinstance(replies, list) and replies and all(
                isinstance(item, dict) and isinstance(item.get("answer"), str) for item in replies
            ):
                return "\n".join(item["answer"] for item in replies).strip()
        except (ValueError, TypeError):
            pass
    return text


def reverse_lines(stream, deadline, scan_limit=64 * 1024 * 1024):
    """Bounded tail scan, including incomplete last lines without trusting them."""
    stream.seek(0, 2)
    position = stream.tell()
    scanned = 0
    carry = b""
    dropping = False
    while position and scanned < scan_limit and time.monotonic() < deadline:
        size = min(position, 256 * 1024, scan_limit - scanned)
        position -= size
        stream.seek(position)
        chunk = stream.read(size)
        scanned += size
        parts = (chunk + carry).split(b"\n")
        carry = parts[0]
        for line in reversed(parts[1:]):
            if dropping:
                dropping = False
                continue
            if line:
                yield line
        # Avoid retaining an unbounded tool-output or attachment record.
        if len(carry) > 8 * 1024 * 1024:
            carry = b""
            dropping = True
    if position == 0 and carry and not dropping:
        yield carry


def lookup_input(transcript, session_id, turn_id):
    result = {"input_lookup": "not_found"}
    if not transcript or not turn_id:
        return result
    deadline = time.monotonic() + 2.5
    with Path(transcript).open("rb") as stream:
        header = stream.readline(2 * 1024 * 1024)
        try:
            first = json.loads(header)
            meta = first.get("payload", {})
            if first.get("type") != "session_meta" or (meta.get("session_id") or meta.get("id")) != session_id:
                return {"input_lookup": "session_mismatch"}
        except (ValueError, TypeError):
            return {"input_lookup": "unknown_format"}
        for line in reverse_lines(stream, deadline):
            # Skip unrelated large records before JSON decoding.
            if b'"message"' not in line or b'"user"' not in line:
                continue
            try:
                entry = json.loads(line)
                payload = entry.get("payload", {})
                if entry.get("type") != "response_item" or payload.get("type") != "message" or payload.get("role") != "user":
                    continue
                passthrough = payload.get("internal_chat_message_metadata_passthrough") or {}
                retained = (entry.get("metadata") or {}).get("retained_source", {}).get("id", {})
                explicit = [value for value in (passthrough.get("turn_id"), retained.get("turn_id")) if value]
                # No inference from the latest message, timestamps or turn order.
                if not explicit or any(value != turn_id for value in explicit):
                    continue
                if "user.text" not in passthrough.get("content_item_kinds", []):
                    continue
                content = payload.get("content", [])
                text = "\n".join(part["text"] for part in content if part.get("type") == "input_text" and isinstance(part.get("text"), str))
                text = visible_input(text)
                return {"input_lookup": "matched", "input_session_id": session_id,
                        "input_turn_id": turn_id, "user_input_preview": text[:1025],
                        "user_input_was_cut": len(text) > 1025,
                        "user_input_captured_at": entry.get("timestamp"),
                        "input_source": "exact_turn_transcript"}
            except (ValueError, TypeError, AttributeError, KeyError):
                continue
    return result


def normalize(value):
    value = str(value or "").replace("\\", "/")
    value = re.sub(r"^/\?/", "", value)
    value = re.sub(r"^([A-Za-z]):/", lambda m: "/mnt/" + m[1].lower() + "/", value)
    return value.rstrip("/").casefold()


def lookup(database, session_id, assigned_project_id=None, turn_id=None, transcript=None):
    result = {}
    connection = sqlite3.connect("file:" + database + "?mode=ro", uri=True, timeout=0.1)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA query_only=ON")
    try:
        # name is the visible chat title; title may be the full first prompt.
        row = connection.execute(
            "SELECT id, name, substr(title,1,250) AS initial_title, cwd, project_id "
            "FROM threads WHERE id=?", (session_id,)
        ).fetchone()
        if row is None:
            return result
        result["conversation_title"] = (row["name"] or row["initial_title"] or "").strip()[:250]
        result["cwd"] = row["cwd"]
        result["project_id"] = row["project_id"]
        if assigned_project_id or row["project_id"]:
            project = connection.execute("SELECT name FROM projects WHERE id=?", (assigned_project_id or row["project_id"],)).fetchone()
            if project:
                result["project_name"] = project["name"]
                result["project_from_assignment"] = bool(assigned_project_id)
        if not result.get("project_name"):
            cwd = normalize(row["cwd"])
            matches = []
            for project in connection.execute(
                "SELECT p.name, r.path FROM projects p JOIN project_roots r ON p.id=r.project_id"
            ):
                root = normalize(project["path"])
                if root and (cwd == root or cwd.startswith(root + "/")):
                    matches.append((len(root), project["name"]))
            if matches:
                result["project_name"] = max(matches)[1]
        result["metadata_source"] = "live_engine_readonly_sqlite"
        if turn_id:
            try:
                if not transcript:
                    path_row = connection.execute("SELECT rollout_path FROM threads WHERE id=?", (session_id,)).fetchone()
                    transcript = path_row["rollout_path"] if path_row else None
                result.update(lookup_input(transcript, session_id, turn_id))
            except Exception:
                result["input_lookup"] = "unavailable"
        return result
    finally:
        connection.close()


if __name__ == "__main__":
    try:
        def argument(index):
            return sys.argv[index] if len(sys.argv) > index and sys.argv[index] != "-" else None
        value = lookup(sys.argv[1], sys.argv[2], argument(3), argument(4), argument(5))
    except Exception:
        value = {"metadata_source": "unavailable"}
    print(json.dumps(value, ensure_ascii=False))
