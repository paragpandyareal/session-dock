// Agent Dock hook: forwards a Claude Code hook event (JSON on stdin) to the
// Agent Dock app on 127.0.0.1. It never writes to stdout/stderr and always
// exits 0 within about one second, whether or not the app is running.
'use strict';
const startedAt = Math.round(performance.timeOrigin);
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');

const done = () => process.exit(0);
setTimeout(done, 1000);
process.on('uncaughtException', done);

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => { raw += chunk; });
process.stdin.on('end', send);
process.stdin.on('error', done);

function trunc(value, max) {
  return typeof value === 'string' && value.length > max ? value.slice(0, max) : value;
}

function readConfig() {
  const base = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  const config = JSON.parse(fs.readFileSync(path.join(base, 'AgentDock', 'config.json'), 'utf8'));
  return { port: Number(config.port) || 0, token: String(config.token || '') };
}

function send() {
  let event, port, token;
  try {
    event = JSON.parse(raw);
    ({ port, token } = readConfig());
  } catch {
    return done();
  }
  if (!port || !token || !event || typeof event !== 'object') return done();

  // Keep the payload small: only short scalar tool inputs, no tool output.
  const input = event.tool_input;
  if (input && typeof input === 'object') {
    const slim = {};
    for (const [key, value] of Object.entries(input)) {
      if (typeof value === 'string') slim[key] = trunc(value, 300);
      else if (typeof value === 'number' || typeof value === 'boolean') slim[key] = value;
    }
    event.tool_input = slim;
  }
  delete event.tool_response;
  delete event.background_tasks;
  delete event.session_crons;
  delete event.permission_suggestions;
  for (const key of ['prompt', 'message', 'last_assistant_message']) {
    if (key in event) event[key] = trunc(event[key], 600);
  }

  event.entrypoint = process.env.CLAUDE_CODE_ENTRYPOINT || null;
  event.host_session_id = process.env.CLAUDE_CODE_HOST_SESSION_ID || null; // desktop app session, for deep links
  event.claude_pid = process.ppid;
  event.sent_at = startedAt;

  const body = Buffer.from(JSON.stringify(event), 'utf8');
  const req = http.request({
    host: '127.0.0.1',
    port,
    path: '/event',
    method: 'POST',
    timeout: 900,
    headers: {
      'Content-Type': 'application/json',
      'Content-Length': body.length,
      'X-Sessiondex-Token': token,
    },
  }, res => {
    res.resume();
    res.on('end', done);
  });
  req.on('timeout', () => { req.destroy(); done(); });
  req.on('error', done);
  req.end(body);
}
