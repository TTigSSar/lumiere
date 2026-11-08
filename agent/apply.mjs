// Applies agent/lumiere.agent.json to Suisse Line (ADR-013).
// Usage: node agent/apply.mjs --base-url https://<host> [--dry-run]
// Reads SUISSE_API_KEY, TOOL_SHARED_SECRET and LUMIERE_AGENT_ID from .env (real env vars win).
// Without LUMIERE_AGENT_ID it creates the agent (POST /api/phone-agent); otherwise PATCH /api/phone-agent/{id}.
// Never prints secrets: --dry-run prints the payload with the tool secret redacted.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const API = 'https://app.suisse-line.ch/api/phone-agent';

const envFile = path.join(root, '.env');
const fileEnv = fs.existsSync(envFile)
  ? Object.fromEntries(fs.readFileSync(envFile, 'utf8').split(/\r?\n/)
    .map((l) => l.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$/)).filter(Boolean).map((m) => [m[1], m[2]]))
  : {};
const env = (k) => process.env[k] || fileEnv[k] || '';

const args = process.argv.slice(2);
const dryRun = args.includes('--dry-run');
const baseUrl = (args[args.indexOf('--base-url') + 1] || '').replace(/\/+$/, '');
if (!args.includes('--base-url') || !baseUrl.startsWith('https://')) {
  console.error('usage: node agent/apply.mjs --base-url https://<host> [--dry-run]');
  process.exit(2);
}

const secret = env('TOOL_SHARED_SECRET');
if (!secret && !dryRun) { console.error('TOOL_SHARED_SECRET missing in .env'); process.exit(1); }

const template = fs.readFileSync(path.join(here, 'lumiere.agent.json'), 'utf8');
const render = (s) => template.replaceAll('{{BASE_URL}}', baseUrl).replaceAll('{{TOOL_SHARED_SECRET}}', s);
const leftover = render('x').match(/\{\{[A-Z_]+\}\}/);
if (leftover) { console.error(`unresolved placeholder ${leftover[0]}`); process.exit(1); }

const agentId = env('LUMIERE_AGENT_ID');
const [method, url] = agentId ? ['PATCH', `${API}/${encodeURIComponent(agentId)}`] : ['POST', API];

if (dryRun) {
  console.log(`DRY RUN — would ${method} ${url}`);
  if (!secret) console.log('note: TOOL_SHARED_SECRET is empty in .env (shown redacted anyway)');
  console.log(render('[REDACTED]'));
  process.exit(0);
}

const apiKey = env('SUISSE_API_KEY');
if (!apiKey) { console.error('SUISSE_API_KEY missing in .env'); process.exit(1); }

const res = await fetch(url, {
  method,
  headers: { 'X-API-Key': apiKey, 'Content-Type': 'application/json' },
  body: render(secret),
});
const text = await res.text();
const redact = (s) => s.replaceAll(secret, '[REDACTED]').replaceAll(apiKey, '[REDACTED]');
let json; try { json = JSON.parse(text); } catch { json = null; }
console.log(`${method} ${url} -> HTTP ${res.status}`);
if (!res.ok) { console.log('body (truncated):', redact(text.slice(0, 600))); process.exit(1); }
const agent = json?.agent ?? json?.data ?? json ?? {};
console.log('agent id:', agent.id, '| tools:', (agent.tools || []).map((t) => t.name).join(', '));
if (!agentId && agent.id) console.log(`set LUMIERE_AGENT_ID=${agent.id} in .env to PATCH next time`);
