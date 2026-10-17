#!/usr/bin/env python3
"""After each tool call: log the calling agent's context size and warn the agent once its context grows large.
`context-size.py --report [SESSION_ID]` prints the latest size of each agent in the session (default: this one).

A tool call inside a subagent reports the main session's transcript and the subagent's id; the subagent's own
transcript sits beside it, in <session>/subagents/agent-<id>.jsonl. Each agent's sizes go to
<session>/context-sizes/<agent>.jsonl, next to the transcripts, so no checkout or worktree is written to. The
transcript is written a little after the tool call, so a size can lag the current step by one, and parallel tool
calls of one agent can log, and warn about, the same size twice."""
import json
import os
import sys
from datetime import datetime, timezone

# Every turn re-sends the whole context: past 150k tokens an agent pays for it on each step and recalls its early
# turns less well; past 300k a fresh agent given a hand-back summary is cheaper than going on.
THRESHOLDS = (150_000, 300_000)
# Lines are read from the end in chunks; one line can be a large tool result, so give up past the limit.
CHUNK = 64 * 1024
TAIL_LIMIT = 8 * 1024 * 1024
SIZES = "context-sizes"
MAIN = "main"


def entries(path):
    """The file's JSON lines, last first, read backwards up to TAIL_LIMIT bytes. A line that does not parse, such
    as one still being written, is skipped."""
    with open(path, "rb") as file:
        end = file.seek(0, os.SEEK_END)
        start = end
        rest = b""
        while start > 0 and end - start < TAIL_LIMIT:
            chunk_end, start = start, max(0, start - CHUNK)
            file.seek(start)
            lines = (file.read(chunk_end - start) + rest).split(b"\n")
            rest = lines.pop(0) if start > 0 else b""
            for line in reversed(lines):
                try:
                    yield json.loads(line)
                except ValueError:
                    continue


def context_size(transcript):
    """The tokens sent with the agent's latest model request (input, cache reads and cache writes), or None if the
    transcript holds none yet. A subagent's transcript may not exist until its first call is written."""
    if not os.path.exists(transcript):
        return None
    for entry in entries(transcript):
        message = entry.get("message")
        if entry.get("type") != "assistant" or not isinstance(message, dict):
            continue
        usage = message.get("usage") or {}
        tokens = sum(usage.get(field) or 0 for field in
                     ("input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"))
        if tokens:
            return tokens
    return None


def crossed(previous, current):
    """The highest threshold the context passed since the previous size, or None."""
    passed = [limit for limit in THRESHOLDS if previous < limit <= current]
    return passed[-1] if passed else None


def warning(tokens, limit, lead=False):
    """The warning for an agent past the limit; the lead (the main session, which dispatches and merges) keeps
    docs/plans/status.md and compacts instead of handing back."""
    head = f"Your context is large: {tokens:,} tokens, past {limit:,}. Every further step re-sends all of it. "
    if lead:
        if limit < max(THRESHOLDS):
            return head + "Update docs/plans/status.md; consider asking the owner to /compact at the next milestone."
        return head + "Update docs/plans/status.md now and ask the owner to run /compact; dispatch nothing new until then."
    if limit < max(THRESHOLDS):
        return head + ("Plan to finish at a natural point, and keep your work committed as you go, so a fresh agent "
                       "can pick it up.")
    return head + ("Hand back now. Commit what passes the checks; save the rest with `git diff --binary HEAD > "
                   "<your scratchpad>/<slice>-wip.patch` (after `git add -N` for new files). Report the commit "
                   "the patch applies to (`git rev-parse HEAD`, after your commits), the patch path, exactly where "
                   "you stopped and what is left.")


def previous_size(log):
    """The size last logged for this agent, or 0 when it has none."""
    if not os.path.exists(log):
        return 0
    return next((entry["tokens"] for entry in entries(log)), 0)


def append(path, entry):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "a", encoding="utf-8") as file:
        file.write(json.dumps(entry) + "\n")


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def on_tool_use(payload, session):
    """Logs the agent's size; returns the hook output that warns it, or None."""
    agent = payload.get("agent_id")
    transcript = os.path.join(session, "subagents", f"agent-{agent}.jsonl") if agent else payload["transcript_path"]
    tokens = context_size(transcript)
    if tokens is None:
        return None
    log = os.path.join(session, SIZES, f"{agent or MAIN}.jsonl")
    limit = crossed(previous_size(log), tokens)
    append(log, {"time": now(), "agent_type": payload.get("agent_type") or MAIN, "tokens": tokens})
    if limit is None:
        return None
    text = warning(tokens, limit, lead=not agent)
    return {"hookSpecificOutput": {"hookEventName": "PostToolUse", "additionalContext": text}}


def report(sizes):
    """One line per agent, largest context first, from the session's size logs."""
    rows = []
    for name in os.listdir(sizes):
        if latest := next(entries(os.path.join(sizes, name)), None):
            rows.append((latest["tokens"], os.path.splitext(name)[0], latest["agent_type"], latest["time"]))
    lines = [f"{'agent':<18} {'type':<16} {'tokens':>9}  updated"]
    lines += [f"{agent:<18} {kind:<16} {tokens:>9,}  {time}"
              for tokens, agent, kind, time in sorted(rows, reverse=True)]
    return "\n".join(lines)


def sizes_of(session_id):
    """The size-log folder of the session with that id, under any project of the Claude config folder, or None."""
    projects = os.path.join(os.environ.get("CLAUDE_CONFIG_DIR") or os.path.expanduser("~/.claude"), "projects")
    if not os.path.isdir(projects):
        return None
    for project in os.listdir(projects):
        sizes = os.path.join(projects, project, session_id, SIZES)
        if os.path.isdir(sizes):
            return sizes
    return None


def main():
    if sys.argv[1:2] == ["--report"]:
        session_id = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("CLAUDE_CODE_SESSION_ID")
        sizes = sizes_of(session_id) if session_id else None
        print(report(sizes) if sizes else f"No context sizes logged for session {session_id or '(none given)'}.")
        return
    # No error handling: the tool has already run, and an error exits 1, which Claude Code shows without blocking.
    payload = json.load(sys.stdin)
    output = on_tool_use(payload, os.path.splitext(payload["transcript_path"])[0])
    if output:
        print(json.dumps(output))


if __name__ == "__main__":
    main()
