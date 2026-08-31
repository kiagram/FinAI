'use strict';

// Statistics worth putting above the fold; anything else LEAN reports is left
// in the full result file rather than crowding the grid.
const HEADLINE_STATS = [
  'Net Profit', 'Compounding Annual Return', 'Sharpe Ratio', 'Sortino Ratio',
  'Drawdown', 'Win Rate', 'Total Orders', 'Total Fees', 'End Equity'
];

const el = (id) => document.getElementById(id);
const state = { algorithms: [], selectedJob: null, timer: null };

/* ---------- transport ---------- */

// The service may be started with a shared token; the browser keeps whatever
// the user was asked for so a reload does not re-prompt.
const token = {
  get: () => localStorage.getItem('finai-token') || '',
  set: (value) => localStorage.setItem('finai-token', value),
  clear: () => localStorage.removeItem('finai-token')
};

async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (options.body) headers['Content-Type'] = 'application/json';
  const current = token.get();
  if (current) headers['X-FinAI-Token'] = current;

  const response = await fetch(`/api${path}`, { ...options, headers });

  if (response.status === 401) {
    token.clear();
    const supplied = window.prompt('This FinAI instance requires an access token.');
    if (supplied) {
      token.set(supplied);
      return api(path, options);
    }
    throw new Error('An access token is required.');
  }

  if (!response.ok) {
    const detail = await response.json().catch(() => null);
    throw new Error(detail?.error || `Request failed (${response.status}).`);
  }

  return response.status === 204 ? null : response.json();
}

/* ---------- composer ---------- */

async function loadAlgorithms() {
  state.algorithms = await api('/algorithms');
  const select = el('algorithm');
  select.replaceChildren(...state.algorithms.map((algorithm) => {
    const option = document.createElement('option');
    option.value = algorithm.id;
    option.textContent = algorithm.name;
    return option;
  }));
  select.addEventListener('change', renderComposer);
  renderComposer();
}

function selectedAlgorithm() {
  return state.algorithms.find((a) => a.id === el('algorithm').value);
}

function renderComposer() {
  const algorithm = selectedAlgorithm();
  if (!algorithm) return;

  el('algorithm-description').textContent = algorithm.description;

  el('parameters').replaceChildren(...algorithm.parameters.map((parameter) => {
    const field = document.createElement('div');
    field.className = 'field';

    const label = document.createElement('label');
    label.htmlFor = `param-${parameter.name}`;
    label.textContent = parameter.label || parameter.name;

    const range = document.createElement('span');
    range.className = 'range';
    range.textContent = ` (${parameter.min}–${parameter.max})`;
    label.appendChild(range);

    const input = document.createElement('input');
    input.id = `param-${parameter.name}`;
    input.type = 'number';
    input.value = parameter.default;
    input.min = parameter.min;
    input.max = parameter.max;
    input.step = parameter.step || 1;
    input.dataset.parameter = parameter.name;

    field.append(label, input);
    return field;
  }));
}

async function submit() {
  const algorithm = selectedAlgorithm();
  if (!algorithm) return;

  const button = el('run');
  const error = el('compose-error');
  button.disabled = true;
  error.hidden = true;

  try {
    const parameters = {};
    for (const input of el('parameters').querySelectorAll('input[data-parameter]')) {
      parameters[input.dataset.parameter] = input.value;
    }

    const job = await api('/backtests', {
      method: 'POST',
      body: JSON.stringify({ algorithmId: algorithm.id, parameters })
    });

    state.selectedJob = job.id;
    await refresh();
  } catch (failure) {
    error.textContent = failure.message;
    error.hidden = false;
  } finally {
    button.disabled = false;
  }
}

/* ---------- history ---------- */

function renderJobs(jobs) {
  const list = el('jobs');

  if (jobs.length === 0) {
    const empty = document.createElement('li');
    empty.className = 'empty';
    empty.textContent = 'No runs yet.';
    list.replaceChildren(empty);
    return;
  }

  list.replaceChildren(...jobs.map((job) => {
    const item = document.createElement('li');

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'job';
    button.setAttribute('aria-current', String(job.id === state.selectedJob));
    button.addEventListener('click', () => { state.selectedJob = job.id; refresh(); });

    const name = document.createElement('span');
    name.className = 'job-name';
    name.textContent = job.algorithmName;

    const status = document.createElement('span');
    status.className = `badge ${job.status}`;
    status.textContent = job.status;

    const when = document.createElement('span');
    when.className = 'job-when';
    when.append(status);

    button.append(name, when);
    item.append(button);
    return item;
  }));
}

/* ---------- detail ---------- */

function renderDetail(job, log) {
  const panel = el('detail');
  panel.hidden = false;

  el('detail-title').textContent = job.algorithmName;
  el('detail-status').textContent = job.status;
  el('detail-status').className = `badge ${job.status}`;

  const meta = [];
  const parameters = Object.entries(job.parameters || {});
  if (parameters.length) meta.push(parameters.map(([k, v]) => `${k}=${v}`).join(', '));
  if (job.durationSeconds != null) meta.push(`${job.durationSeconds.toFixed(1)}s`);
  if (job.orderCount != null) meta.push(`${job.orderCount} orders`);
  el('detail-meta').textContent = meta.join(' · ');

  const error = el('detail-error');
  error.hidden = !job.error;
  error.textContent = job.error || '';

  const figure = el('equity-figure');
  if (job.equity && job.equity.length > 1) {
    figure.hidden = false;
    el('equity').replaceChildren(equityChart(job.equity));
  } else {
    figure.hidden = true;
  }

  const stats = job.statistics || {};
  const shown = HEADLINE_STATS.filter((key) => key in stats);
  el('stats').replaceChildren(...shown.map((key) => {
    const group = document.createElement('div');
    const term = document.createElement('dt');
    term.textContent = key;
    const value = document.createElement('dd');
    value.textContent = stats[key];
    group.append(term, value);
    return group;
  }));

  el('log').textContent = log.length ? log.join('\n') : 'No output yet.';
}

/* Minimal inline SVG line chart — no external library, so nothing to load. */
function equityChart(points) {
  const width = 720, height = 240, pad = { top: 12, right: 12, bottom: 24, left: 60 };
  const plotWidth = width - pad.left - pad.right;
  const plotHeight = height - pad.top - pad.bottom;

  const times = points.map((p) => p.time);
  const values = points.map((p) => p.value);
  const minTime = Math.min(...times), maxTime = Math.max(...times);
  const minValue = Math.min(...values), maxValue = Math.max(...values);

  // Guard the degenerate case where the curve is perfectly flat.
  const timeSpan = maxTime - minTime || 1;
  const valueSpan = maxValue - minValue || Math.abs(maxValue) || 1;

  const x = (t) => pad.left + ((t - minTime) / timeSpan) * plotWidth;
  const y = (v) => pad.top + (1 - (v - minValue) / valueSpan) * plotHeight;

  const svgns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(svgns, 'svg');
  svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label',
    `Equity from ${formatDate(minTime)} to ${formatDate(maxTime)}, ` +
    `${formatMoney(values[0])} to ${formatMoney(values[values.length - 1])}.`);

  const axis = document.createElementNS(svgns, 'path');
  axis.setAttribute('d', `M${pad.left},${pad.top} V${pad.top + plotHeight} H${pad.left + plotWidth}`);
  axis.setAttribute('fill', 'none');
  axis.setAttribute('stroke', 'currentColor');
  axis.setAttribute('opacity', '0.25');
  svg.append(axis);

  const line = document.createElementNS(svgns, 'path');
  line.setAttribute('d', points.map((p, i) => `${i ? 'L' : 'M'}${x(p.time).toFixed(2)},${y(p.value).toFixed(2)}`).join(' '));
  line.setAttribute('fill', 'none');
  line.setAttribute('stroke', 'var(--accent)');
  line.setAttribute('stroke-width', '1.75');
  line.setAttribute('stroke-linejoin', 'round');
  svg.append(line);

  const label = (text, px, py, anchor) => {
    const node = document.createElementNS(svgns, 'text');
    node.setAttribute('x', px);
    node.setAttribute('y', py);
    node.setAttribute('text-anchor', anchor);
    node.setAttribute('font-size', '11');
    node.setAttribute('fill', 'currentColor');
    node.setAttribute('opacity', '0.65');
    node.textContent = text;
    svg.append(node);
  };

  label(formatMoney(maxValue), pad.left - 8, pad.top + 4, 'end');
  label(formatMoney(minValue), pad.left - 8, pad.top + plotHeight, 'end');
  label(formatDate(minTime), pad.left, height - 6, 'start');
  label(formatDate(maxTime), pad.left + plotWidth, height - 6, 'end');

  return svg;
}

const formatDate = (seconds) => new Date(seconds * 1000).toISOString().slice(0, 10);
const formatMoney = (value) => `$${Math.round(value).toLocaleString('en-US')}`;

/* ---------- polling ---------- */

async function refresh() {
  try {
    const jobs = await api('/backtests?limit=25');
    renderJobs(jobs);

    if (state.selectedJob) {
      const [job, log] = await Promise.all([
        api(`/backtests/${state.selectedJob}`),
        api(`/backtests/${state.selectedJob}/log`)
      ]);
      renderDetail(job, log.lines);
    }

    // Poll quickly only while something is in flight.
    const active = jobs.some((job) => job.status === 'Queued' || job.status === 'Running');
    schedule(active ? 1500 : 10000);
  } catch (failure) {
    const error = el('compose-error');
    error.textContent = failure.message;
    error.hidden = false;
    schedule(10000);
  }
}

function schedule(delay) {
  clearTimeout(state.timer);
  state.timer = setTimeout(refresh, delay);
}

el('run').addEventListener('click', submit);

loadAlgorithms().then(refresh).catch((failure) => {
  const error = el('compose-error');
  error.textContent = failure.message;
  error.hidden = false;
});
