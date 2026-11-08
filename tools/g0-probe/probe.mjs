// G0 probe — throwaway discovery instrument (ADR-012). Not product code.
// Captures exactly what Suisse Line live tools send, and returns controlled responses
// so we can verify that tool results reach the assistant and how long it waits.
//
// Run:   node tools/g0-probe/probe.mjs
// Expose: cloudflared tunnel --url http://localhost:8787   (temporary HTTPS URL)
//
// Routes (any method):
//   /g0/echo           → returns code word "pineapple"
//   /g0/slots          → returns a structured list of 3 fake slots (labelled FAKE)
//   /g0/slow?ms=8000   → waits ms (max 30000) then returns code word "tangerine-<ms>"
//   /g0/error          → HTTP 500 with a JSON error body
//   /health            → 200 ok
//
// Captures (full headers with secrets redacted, full body) go to tools/g0-probe/captures/
// which is gitignored — bodies may contain the caller's phone number.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');
loadDotEnv(path.join(repoRoot, '.env'));

const PORT = Number(process.env.G0_PORT || 8787);
const SECRET = process.env.G0_SECRET || '';
const captureDir = path.join(here, 'captures');
fs.mkdirSync(captureDir, { recursive: true });
const captureFile = path.join(captureDir, `${new Date().toISOString().slice(0, 10)}.jsonl`);

const SENSITIVE = /(authorization|cookie|secret|token|api[-_]?key|signature|password)/i;

const server = http.createServer(async (req, res) => {
  const started = Date.now();
  const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
  const rawBody = await readBody(req, 64 * 1024);
  let body = rawBody;
  try { body = rawBody ? JSON.parse(rawBody) : null; } catch { /* keep raw text */ }

  const headers = {};
  let secretMatch = null;
  for (const [name, value] of Object.entries(req.headers)) {
    if (SENSITIVE.test(name)) {
      const v = Array.isArray(value) ? value.join(',') : String(value);
      headers[name] = `[redacted len=${v.length}]`;
      if (SECRET && v.length > 0) secretMatch = secretMatch || safeEqual(v.replace(/^Bearer\s+/i, ''), SECRET);
    } else {
      headers[name] = value;
    }
  }

  const { status, payload } = await route(url);
  const elapsedMs = Date.now() - started;

  fs.appendFileSync(captureFile, JSON.stringify({
    at: new Date().toISOString(), method: req.method, path: url.pathname,
    query: Object.fromEntries(url.searchParams), headers, secretMatch, body, responseStatus: status, elapsedMs,
  }) + '\n');

  console.log(`[${new Date().toISOString()}] ${req.method} ${url.pathname}${url.search} → ${status} in ${elapsedMs} ms`);
  console.log(`  header names: ${Object.keys(req.headers).join(', ')}`);
  console.log(`  shared-secret header matched G0_SECRET: ${secretMatch === null ? 'no sensitive header seen' : secretMatch}`);
  console.log(`  body keys: ${body && typeof body === 'object' ? describeKeys(body) : typeof body}`);

  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(payload));
});

async function route(url) {
  switch (url.pathname) {
    case '/health':
      return { status: 200, payload: { ok: true } };
    case '/g0/echo':
      return { status: 200, payload: {
        ok: true,
        say: 'The test system received your request. The code word is pineapple.',
        code_word: 'pineapple',
      } };
    case '/g0/slots':
      return { status: 200, payload: {
        ok: true,
        note: 'FAKE test data for connectivity testing only',
        today: 'Sunday, 8 November 2026',
        slots: [
          { slot_id: 'test-1', spoken_label: 'Tuesday, 10 November at 10:00' },
          { slot_id: 'test-2', spoken_label: 'Tuesday, 10 November at 14:30' },
          { slot_id: 'test-3', spoken_label: 'Wednesday, 11 November at 09:15' },
        ],
      } };
    case '/g0/slow': {
      const ms = Math.min(Math.max(Number(url.searchParams.get('ms')) || 5000, 0), 30000);
      await new Promise((r) => setTimeout(r, ms));
      return { status: 200, payload: { ok: true, say: `The code word is tangerine ${ms}.`, code_word: `tangerine-${ms}` } };
    }
    case '/g0/webhook':
      return { status: 200, payload: { ok: true } };
    case '/g0/error':
      return { status: 500, payload: { ok: false, error_code: 'TEST_ERROR', say: 'The test system reported an error. The code word is lemon.' } };
    default:
      return { status: 404, payload: { ok: false, error_code: 'NOT_FOUND' } };
  }
}

function readBody(req, limit) {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (chunk) => { if (data.length < limit) data += chunk; });
    req.on('end', () => resolve(data));
    req.on('error', () => resolve(data));
  });
}

function safeEqual(a, b) {
  const ab = Buffer.from(a), bb = Buffer.from(b);
  return ab.length === bb.length && crypto.timingSafeEqual(ab, bb);
}

function describeKeys(obj, prefix = '', depth = 0) {
  if (depth > 3 || obj === null || typeof obj !== 'object') return '';
  return Object.entries(obj).map(([k, v]) => {
    const key = prefix ? `${prefix}.${k}` : k;
    return v && typeof v === 'object' && !Array.isArray(v) ? `${key}{${describeKeys(v, '', depth + 1)}}` : key;
  }).join(', ');
}

function loadDotEnv(file) {
  if (!fs.existsSync(file)) return;
  for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/);
    if (m && !(m[1] in process.env)) process.env[m[1]] = m[2];
  }
}

server.listen(PORT, () => {
  console.log(`G0 probe listening on http://localhost:${PORT}`);
  console.log(`Captures → ${captureFile}`);
  if (!SECRET) console.log('WARNING: G0_SECRET not set — header forwarding cannot be verified.');
});
