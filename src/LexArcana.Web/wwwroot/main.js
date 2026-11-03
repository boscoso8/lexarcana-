import { dotnet } from './_framework/dotnet.js';

const canvas = document.getElementById('canvas');
const form = document.getElementById('modulo');
const button = document.getElementById('entra');
const status = document.getElementById('stato');
const touch = matchMedia('(pointer: coarse)').matches || navigator.maxTouchPoints > 0;

// Le dita sul canvas, in pixel del canvas: le legge il client a ogni fotogramma.
const fingers = new Map();
function trackTouches(event) {
  event.preventDefault();
  const rect = canvas.getBoundingClientRect();
  const sx = canvas.width / rect.width, sy = canvas.height / rect.height;
  fingers.clear();
  for (const t of event.touches) fingers.set(t.identifier, [(t.clientX - rect.left) * sx, (t.clientY - rect.top) * sy]);
}
for (const type of ['touchstart', 'touchmove', 'touchend', 'touchcancel']) canvas.addEventListener(type, trackTouches, { passive: false });

// Le funzioni che il codice C# importa (JSImport).
globalThis.lexarcana = {
  touches: () => { const out = []; for (const [id, [x, y]] of fingers) out.push(id, x, y); return out; },
  askText: (question) => window.prompt(question) ?? null,   // su iPhone apre la tastiera
  load: (key) => { try { return localStorage.getItem(key); } catch { return null; } },
  save: (key, value) => { try { localStorage.setItem(key, value); } catch { } },
};

const { getAssemblyExports, getConfig, runMain } = await dotnet
  .withModuleConfig({ canvas })
  .create();
await runMain();
const { LexArcana: { Web: { Bridge } } } = await getAssemblyExports(getConfig().mainAssemblyName);

const saved = Bridge.SavedCharacter();
if (saved) {
  const [player, roman, vocation, level] = saved.split('|');
  document.getElementById('nome').value = player;
  document.getElementById('salvato').textContent = `Bentornato: ${roman}, ${vocation.toLowerCase()} di livello ${level}.`;
}
button.disabled = false;
button.textContent = 'Entra';

// Un ?nome=… nell'indirizzo entra subito (comodo per i segnalibri sulla schermata Home e per le prove).
const params = new URLSearchParams(location.search);
if (params.get('vocazione')) document.getElementById('vocazione').value = params.get('vocazione');
if (params.get('nome')) { document.getElementById('nome').value = params.get('nome'); queueMicrotask(() => form.requestSubmit()); }

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  button.disabled = true;
  button.textContent = 'Mi collego…';
  status.textContent = '';
  const ws = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`;
  document.getElementById('ingresso').style.display = 'none';
  canvas.style.display = 'block';
  const problem = await Bridge.Start(ws, document.getElementById('nome').value.trim(),
    document.getElementById('vocazione').value, document.getElementById('parola').value,
    touch || params.get('touch') === '1', innerWidth, innerHeight);
  if (problem) {
    canvas.style.display = 'none';
    document.getElementById('ingresso').style.display = '';
    status.textContent = problem;
    button.disabled = false;
    button.textContent = 'Riprova';
    return;
  }
  canvas.focus();
  addEventListener('resize', () => Bridge.Resize(innerWidth, innerHeight));
  // niente scorrimento né zoom del browser mentre si gioca
  for (const type of ['touchmove', 'gesturestart', 'contextmenu']) canvas.addEventListener(type, e => e.preventDefault(), { passive: false });
  const frame = () => { if (Bridge.Frame()) requestAnimationFrame(frame); };
  requestAnimationFrame(frame);
});
