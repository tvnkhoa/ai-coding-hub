#!/usr/bin/env node
// Claude Code hook for Claude Code Mini Alert.
//
// Claude Code writes an AskUserQuestion / ExitPlanMode tool call to the session transcript only
// after the owner answers it, so a HUD that tails transcripts cannot see a question while it is
// open. This hook leaves a small marker file while such a prompt is on screen:
//
//   PreToolUse  (AskUserQuestion|ExitPlanMode)  -> write  <dir>/<session_id>.json
//   PostToolUse (AskUserQuestion|ExitPlanMode)  -> delete it
//   UserPromptSubmit / Stop                     -> delete it (prompt dismissed with Esc)
//
// <dir> is %USERPROFILE%\.claude\hud-pending (override with CLAUDE_HUD_PENDING_DIR).
// It never blocks Claude: it prints nothing and always exits 0.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const dir = process.env.CLAUDE_HUD_PENDING_DIR || path.join(os.homedir(), '.claude', 'hud-pending');
const PROMPTS = new Set(['AskUserQuestion', 'ExitPlanMode']);

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (d) => { raw += d; });
process.stdin.on('end', () => {
  try {
    const e = JSON.parse(raw || '{}');
    const sid = e.session_id;
    if (typeof sid !== 'string' || !/^[\w-]{1,100}$/.test(sid)) return;
    const file = path.join(dir, `${sid}.json`);

    if (e.hook_event_name === 'PreToolUse' && PROMPTS.has(e.tool_name)) {
      const q = Array.isArray(e.tool_input?.questions) ? e.tool_input.questions[0] : undefined;
      const marker = {
        session_id: sid,
        tool_use_id: typeof e.tool_use_id === 'string' ? e.tool_use_id : null,
        tool: e.tool_name,
        header: typeof q?.header === 'string' ? q.header : null,
        question: typeof q?.question === 'string' ? q.question : null,
        at: new Date().toISOString(),
      };
      fs.mkdirSync(dir, { recursive: true });
      const tmp = `${file}.${process.pid}.tmp`;
      fs.writeFileSync(tmp, JSON.stringify(marker));
      fs.renameSync(tmp, file);
    } else {
      fs.rmSync(file, { force: true });
    }
  } catch {
    // Never disturb the session.
  }
});
