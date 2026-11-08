// G0 only (ADR-012/013): creates the "G0 Probe" phone agent via the Suisse Line API.
// Usage: node tools/g0-probe/create-agent.mjs <tunnel-base-url>
// Reads SUISSE_API_KEY and G0_SECRET from .env. Never prints secrets.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const env = Object.fromEntries(fs.readFileSync(path.join(root, '.env'), 'utf8').split(/\r?\n/)
  .map((l) => l.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/)).filter(Boolean).map((m) => [m[1], m[2]]));
const base = process.argv[2];
if (!base?.startsWith('https://')) throw new Error('usage: create-agent.mjs https://<tunnel>');
if (!env.SUISSE_API_KEY || !env.G0_SECRET) throw new Error('SUISSE_API_KEY / G0_SECRET missing in .env');

const auth = { 'X-Tool-Secret': env.G0_SECRET };
const tool = (name, description, endpoint, parameters = []) => ({ name, description, endpoint, method: 'POST', headers: auth, parameters });

const body = {
  name: 'G0 Probe',
  systemPrompt: [
    'You are a test assistant for a connectivity check. Speak English only.',
    'When the user says "test one", call the tool g0_echo with service set to "haircut" and read out the "say" field of the result.',
    'When the user says "test two", call g0_slots and read out every spoken_label in the result.',
    'When the user says "test three", call g0_slow and read out the "say" field of the result.',
    'When the user says "test four", call g0_error and tell the user exactly what happened.',
    'Never invent a code word or a result; only read what a tool returned. If a tool fails, say so plainly.',
  ].join('\n'),
  greetingMessage: 'Connectivity test ready. Say test one, two, three or four.',
  greetingType: 'text',
  firstSpeaker: 'agent',
  defaultLanguage: 'en',
  timezone: 'Europe/Zurich',
  advancedSettings: { enableRecording: false, maxCallDurationSeconds: 180, silenceTimeoutSeconds: 30 },
  tools: [
    tool('g0_echo', 'Connectivity test. Returns a code word.', `${base}/g0/echo`,
      [{ name: 'service', type: 'string', description: 'The service name, e.g. haircut', required: true }]),
    tool('g0_slots', 'Connectivity test. Returns a list of test time slots.', `${base}/g0/slots`),
    tool('g0_slow', 'Connectivity test. Responds after 8 seconds with a code word.', `${base}/g0/slow?ms=8000`),
    tool('g0_error', 'Connectivity test. Always returns a server error.', `${base}/g0/error`),
  ],
  webhooks: [{
    url: `${base}/g0/webhook`,
    events: ['call.started', 'call.completed', 'tool.called', 'tool.result'],
    headers: auth,
    enabled: true,
    fields: { transcript: false, recordingUrl: false, summary: false },
  }],
};

const res = await fetch('https://app.suisse-line.ch/api/phone-agent', {
  method: 'POST',
  headers: { 'X-API-Key': env.SUISSE_API_KEY, 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});
const text = await res.text();
let json; try { json = JSON.parse(text); } catch { json = null; }
console.log('HTTP', res.status);
const agent = json?.agent ?? json?.data ?? json;
if (!res.ok || !agent) { console.log('body (truncated):', text.slice(0, 600).replaceAll(env.G0_SECRET, '[G0_SECRET]')); process.exit(1); }
console.log('agent id:', agent.id, '| publicId:', agent.publicId, '| status:', agent.agentDeploymentStatus);
console.log('tools stored:', (agent.tools || []).map((t) => `${t.name} ${t.method} hdr=${Object.keys(t.headers || {}).join('+')} params=${(t.parameters || []).length}`).join(' | '));
console.log('webhooks stored:', (agent.webhooks || []).map((w) => `${(w.events || []).join(',')} hdr=${Object.keys(w.headers || {}).join('+')}`).join(' | '));
console.log('language/timezone:', agent.defaultLanguage, agent.timezone, '| recording:', agent.enableRecording);
console.log('top-level keys:', Object.keys(json).join(', '));
