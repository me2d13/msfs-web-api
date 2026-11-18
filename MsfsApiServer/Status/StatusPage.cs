using System.Net;

namespace MsfsApiServer.Status
{
    /// <summary>
    /// The built-in status page. The exe serves it at "/" and "/status"; the numbers come from
    /// /api/stats, which the page re-reads every two seconds.
    /// </summary>
    public static class StatusPage
    {
        /// <summary>Renders the page, with the www directory spelled out for people who browse without JavaScript.</summary>
        public static string Html(string wwwDir)
        {
            return Template.Replace("__WWW_DIR__", WebUtility.HtmlEncode(wwwDir));
        }

        private const string Template = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>MSFS Web API - status</title>
<style>
  :root { color-scheme: light dark; }
  body { font-family: system-ui, "Segoe UI", Arial, sans-serif; margin: 1.5em auto; max-width: 62em; padding: 0 1em; line-height: 1.45; }
  h1 { font-size: 1.5em; margin: 0 0 0.2em 0; }
  h2 { font-size: 1.05em; margin: 1.6em 0 0.5em 0; border-bottom: 1px solid #8884; padding-bottom: 0.2em; }
  table { border-collapse: collapse; width: 100%; }
  th, td { text-align: left; vertical-align: top; padding: 0.25em 0.6em 0.25em 0; border-bottom: 1px solid #8882; }
  table.info th { width: 13em; font-weight: 600; }
  table.vars td.num, table.vars th.num { text-align: right; font-variant-numeric: tabular-nums; }
  code, .mono { font-family: ui-monospace, Consolas, "Courier New", monospace; font-size: 0.92em; word-break: break-all; }
  .muted { color: #8888; }
  .badge { display: inline-block; padding: 0.15em 0.6em; border-radius: 1em; font-size: 0.85em; font-weight: 600; color: #fff; }
  .badge.ok { background: #2e7d32; }
  .badge.down { background: #b3261e; }
  .badge.warn { background: #a16207; }
  .error { background: #b3261e; color: #fff; padding: 0.5em 0.8em; border-radius: 4px; margin: 0.8em 0; }
  header nav { margin-top: 0.3em; font-size: 0.9em; }
  .stream { margin: 0 0 1em 0; }
  .chips code { display: inline-block; background: #8882; border-radius: 3px; padding: 0 0.35em; margin: 0.1em 0.25em 0.1em 0; }
  ul.files { list-style: none; padding-left: 0; }
  ul.files li { margin: 0.2em 0; }
  input[type=text] { padding: 0.15em 0.4em; }
  footer { margin-top: 2em; font-size: 0.85em; }
</style>
</head>
<body>
<header>
  <h1>MSFS Web API <span class="muted" id="version"></span></h1>
  <span class="badge" id="sim-badge">...</span>
  <span class="muted" id="uptime"></span>
  <nav>
    <a href="/swagger">API doc (Swagger)</a> &middot;
    <a href="/api/stats">/api/stats</a> &middot;
    <a href="https://github.com/me2d13/msfs-web-api" target="_blank" rel="noreferrer">GitHub</a>
  </nav>
</header>

<div id="error" class="error" hidden></div>

<section>
  <h2>Server</h2>
  <table class="info" id="server"><tbody></tbody></table>
</section>

<section>
  <h2>Configuration</h2>
  <table class="info" id="config"><tbody></tbody></table>
</section>

<section>
  <h2>Streaming</h2>
  <div id="streams"><span class="muted">loading...</span></div>
</section>

<section>
  <h2>SimConnect</h2>
  <div id="sim"></div>
</section>

<section>
  <h2>Variables <span class="muted" id="totals"></span></h2>
  <p>
    <label>Filter <input type="text" id="filter" size="20" placeholder="part of the name"></label>
    <label><input type="checkbox" id="auto" checked> auto refresh</label>
    <button id="refresh">refresh now</button>
  </p>
  <table class="vars" id="vars">
    <thead>
      <tr>
        <th>Variable</th><th>Kind</th>
        <th class="num">Reads</th><th class="num">Writes</th><th class="num">No value</th>
        <th class="num">Last value</th><th>Last update</th>
      </tr>
    </thead>
    <tbody></tbody>
  </table>
</section>

<section>
  <h2>Local files</h2>
  <p class="muted">Place html files into <code>__WWW_DIR__</code> to appear in this section.</p>
  <ul class="files" id="files"></ul>
</section>

<footer class="muted">
  Values come from <code>/api/stats</code>; the page re-reads them every two seconds while auto refresh is on.
</footer>

<script>
const REFRESH_MS = 2000;
let timer = null;

const el = (id) => document.getElementById(id);

function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function duration(seconds) {
  if (seconds == null) return '-';
  const s = Math.max(0, Math.floor(seconds));
  const d = Math.floor(s / 86400), h = Math.floor(s % 86400 / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
  const parts = [];
  if (d) parts.push(d + 'd');
  if (h) parts.push(h + 'h');
  if (m) parts.push(m + 'm');
  if (!d && !h) parts.push(sec + 's');
  return parts.join(' ');
}

function age(iso) {
  if (!iso) return '<span class="muted">never</span>';
  const seconds = (Date.now() - Date.parse(iso)) / 1000;
  if (isNaN(seconds)) return '<span class="muted">-</span>';
  if (seconds < 1.5) return 'just now';
  if (seconds < 60) return seconds.toFixed(1) + ' s ago';
  if (seconds < 3600) return Math.round(seconds / 60) + ' min ago';
  if (seconds < 86400) return Math.round(seconds / 3600) + ' h ago';
  return Math.round(seconds / 86400) + ' d ago';
}

function bytes(n) {
  if (n == null) return '-';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
  return (n / 1048576).toFixed(1) + ' MB';
}

function num(value) {
  if (value == null) return '<span class="muted">-</span>';
  if (Number.isInteger(value)) return value.toLocaleString();
  return Number(value.toFixed(4)).toLocaleString();
}

function plural(count, word) {
  return count + ' ' + word + (count === 1 ? '' : 's');
}

function row(name, value) {
  return '<tr><th>' + esc(name) + '</th><td>' + value + '</td></tr>';
}

function mono(value) {
  return value ? '<span class="mono">' + esc(value) + '</span>' : '<span class="muted">-</span>';
}

function link(url, text) {
  return '<a href="' + esc(url) + '">' + esc(text ?? url) + '</a>';
}

function render(data) {
  const server = data.server || {};
  const sim = data.sim || {};
  const config = data.config || {};
  const totals = data.totals || {};

  document.title = server.exeName ? 'MSFS Web API - ' + server.exeName : 'MSFS Web API - status';
  el('version').textContent = server.version ? 'v' + server.version.split('+')[0] : '';

  const badge = el('sim-badge');
  badge.textContent = 'MSFS: ' + (sim.connected ? 'CONNECTED' : 'DISCONNECTED');
  badge.className = 'badge ' + (sim.connected ? 'ok' : 'down');
  el('uptime').textContent = 'running for ' + duration(server.uptimeSeconds);

  el('server').querySelector('tbody').innerHTML = [
    row('Executable', esc(server.exeName || '-') + (server.version ? ' <span class="muted">v' + esc(server.version.split('+')[0]) + '</span>' : '')),
    row('Path', mono(server.exePath)),
    row('Directory', mono(server.baseDir)),
    row('Listening on', esc('port ' + (server.port || '?')) + (server.localIp ? ', network address ' + esc(server.localIp) : '')),
    row('URLs', link(server.url, server.url) + ' &middot; ' + link(server.networkUrl, server.networkUrl)),
    row('Log file', mono(server.logFilePath) + (server.logFilePath ? ' <span class="muted">(' + (server.logFileExists ? bytes(server.logFileSizeBytes) : 'not created') + ')</span>' : '')),
    row('Process', esc('pid ' + server.processId + ' on ' + server.machineName)),
    row('Runtime', esc(server.framework || '-')),
    row('Started', esc(new Date(server.startedUtc).toLocaleString()) + ' <span class="muted">(' + duration(server.uptimeSeconds) + ' ago)</span>'),
  ].join('');

  const configFile = config.filePath
    ? mono(config.filePath) + ' ' + (config.fileLoaded
        ? '<span class="badge ok">loaded</span>'
        : config.fileFound
          ? '<span class="badge warn">failed: ' + esc(config.loadError || 'unknown error') + '</span>'
          : '<span class="badge warn">not found, using defaults</span>')
    : '<span class="muted">none</span>';

  el('config').querySelector('tbody').innerHTML = [
    row('Config file', configFile),
    row('Command line', config.commandLineOverrides && config.commandLineOverrides.length
      ? config.commandLineOverrides.map(o => '<code>' + esc(o) + '</code>').join(' ')
      : '<span class="muted">no overrides</span>'),
    row('Log level', esc(config.logLevel || 'default')),
    row('UDP streaming', config.udpEnabled ? 'enabled in config' : '<span class="muted">disabled in config</span>'),
    row('Serial streaming', config.serialEnabled ? 'enabled in config' : '<span class="muted">disabled in config</span>'),
  ].join('');

  el('streams').innerHTML = [streamBlock('UDP', data.udp, 'udp.enabled'), streamBlock('Serial', data.serial, 'serial.enabled')].join('');

  const areas = (sim.clientDataAreas || []).map(a => '<code>' + esc(a.name) + '</code> <span class="muted">0x' + (a.dataAreaId >>> 0).toString(16) + '</span>').join(', ');
  el('sim').innerHTML =
    '<p>Connection: <span class="badge ' + (sim.connected ? 'ok' : 'down') + '">' + (sim.connected ? 'CONNECTED' : 'DISCONNECTED') + '</span></p>' +
    '<p>Client data areas seen this session (' + (sim.knownClientDataAreas || 0) + '): ' + (areas || '<span class="muted">none yet</span>') + '</p>';

  el('totals').textContent = '(' + plural(totals.variables || 0, 'variable') + ', ' + plural(totals.reads || 0, 'read') + ', ' +
    plural(totals.writes || 0, 'write') + ', ' + (totals.noValue || 0) + ' without a value)';

  const filter = el('filter').value.trim().toLowerCase();
  const variables = (data.variables || []).filter(v => !filter || v.name.toLowerCase().includes(filter));
  el('vars').querySelector('tbody').innerHTML = variables.length
    ? variables.map(v => '<tr>' +
        '<td><span class="mono">' + esc(v.name) + '</span></td>' +
        '<td>' + esc(v.kind) + '</td>' +
        '<td class="num">' + (v.reads || 0) + '</td>' +
        '<td class="num">' + (v.writes || 0) + '</td>' +
        '<td class="num">' + (v.noValue ? '<span title="timeout, missing area or not connected">' + v.noValue + '</span>' : '0') + '</td>' +
        '<td class="num">' + num(v.lastValue) + '</td>' +
        '<td>' + age(v.lastReadUtc || v.lastWriteUtc) + '</td>' +
      '</tr>').join('')
    : '<tr><td colspan="7" class="muted">' + (filter ? 'no variable matches the filter' : 'nothing looked up yet') + '</td></tr>';

  const www = data.www || {};
  el('files').innerHTML = (www.files || []).length
    ? www.files.map(f => '<li>' + link(f.url, f.title) + ' <span class="muted">' + esc(f.name) + ' &middot; ' + bytes(f.sizeBytes) + ' &middot; changed ' + age(f.modifiedUtc) + '</span></li>').join('')
    : '<li class="muted">no html files in ' + esc(www.dir || 'www') + ' yet</li>';
}

function streamBlock(name, stream, configKey) {
  if (!stream) {
    return '<div class="stream"><strong>' + name + '</strong>: <span class="muted">not running (' + configKey + ' is false, or it failed to start - see the log file)</span></div>';
  }

  const state = stream.connected
    ? '<span class="badge ok">sending</span>'
    : '<span class="badge warn">reconnecting</span>';

  const details = [
    stream.target ? esc(stream.target) : null,
    'every ' + esc(stream.intervalMs) + ' ms',
    esc(plural(stream.variableCount, 'variable')),
  ].filter(Boolean).join(', ');

  const counts = [
    esc(stream.sent) + ' ' + (name === 'UDP' ? 'packets' : 'lines') + ' sent',
    'last ' + age(stream.lastSentUtc),
    stream.consecutiveFailures ? '<span class="badge warn">' + esc(stream.consecutiveFailures) + ' consecutive failures</span>' : null,
    stream.lastError ? '<span class="muted">last error: ' + esc(stream.lastError) + '</span>' : null,
  ].filter(Boolean).join(' &middot; ');

  return '<div class="stream">' +
    '<div><strong>' + name + '</strong> ' + state + ' &rarr; ' + details + '</div>' +
    '<div class="muted">' + counts + '</div>' +
    '<div class="chips">' + (stream.variables || []).map(v => '<code>' + esc(v) + '</code>').join('') + '</div>' +
  '</div>';
}

let lastData = null;

async function refresh() {
  try {
    const response = await fetch('/api/stats', { cache: 'no-store' });
    if (!response.ok) throw new Error('HTTP ' + response.status);
    lastData = await response.json();
    render(lastData);
    el('error').hidden = true;
  } catch (e) {
    const box = el('error');
    box.textContent = 'Could not read /api/stats: ' + e.message + ' (showing the last values)';
    box.hidden = false;
  }
}

function schedule() {
  if (timer) clearInterval(timer);
  timer = el('auto').checked ? setInterval(refresh, REFRESH_MS) : null;
}

el('auto').addEventListener('change', schedule);
el('refresh').addEventListener('click', refresh);
el('filter').addEventListener('input', () => { if (lastData) render(lastData); });

refresh();
schedule();
</script>
</body>
</html>
""";
    }
}
