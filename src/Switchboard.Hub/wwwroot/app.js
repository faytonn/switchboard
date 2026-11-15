// Switchboard web UI. No framework and no build step: plain DOM code.
//
// Security rule for this file: text from sessions is only ever put on the page with
// textContent, never innerHTML. A session could send "<img onerror=...>", and textContent
// shows it as plain characters instead of running it. The CSP header is the second line of
// defence (and it's why colours are set with classes, not style attributes).

const $ = (id) => document.getElementById(id);

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

const state = {
  sessions: [],
  current: null,          // id of the open session
  messages: [],           // history of the open session, oldest first
  unread: new Map(),      // session id -> replies to you not seen yet
  permissions: [],        // tool prompts waiting for you, all sessions
  activity: new Map(),    // session id -> idle | working | approval (pushed by the hub)
  expanded: new Set(),    // ids of agent-to-agent lines the user opened
  showStopped: false,
  terminalTimer: null,
  focusApprovals: false,  // scroll to the approval cards once the chat opens
};

// ---------- API ----------

// Every call carries X-Switchboard: the hub requires it for cookie-authenticated writes, and a
// page on another site can't add it (that would need CORS, which the hub doesn't allow).
async function api(path, { method = 'GET', body } = {}) {
  const headers = { 'X-Switchboard': '1' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(path, {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401) {
    location.replace('/login.html');
    throw new Error('Signed out.');
  }
  const data = res.status === 204 ? null : await res.json().catch(() => null);
  if (!res.ok) throw new Error(data?.error ?? `HTTP ${res.status}`);
  return data;
}

// ---------- session tiles: icon (or initials) on the session's colour ----------

const SVG = 'http://www.w3.org/2000/svg';

// Without a chosen colour, the same id always gets the same one: a small string hash picks
// one of 8 palette classes (.c0 ... .c7), defined in app.css for light and dark mode.
function hashColor(id) {
  let hash = 2166136261;
  for (const ch of id) hash = Math.imul(hash ^ ch.codePointAt(0), 16777619);
  return (hash >>> 0) % 8;
}

// The colour class for a session: the one stored for it, or the hashed default.
// `look` overrides what's stored (the picker's live preview).
function tileColorClass(id, look) {
  const color = look ? look.color : sessionById(id)?.color;
  return `c${color ?? hashColor(id)}`;
}

// "api-fix" -> "AF", "manager" -> "M".
function initials(id) {
  const parts = id.split('-').filter(Boolean);
  return (parts.length > 1 ? parts[0][0] + parts[1][0] : id[0]).toUpperCase();
}

// One icon from the vendored Lucide sprite: <svg><use href="/icons/lucide.svg#ticket"></svg>.
// Same-origin file, so the strict CSP needs no exception.
function iconSvg(name) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('class', 'icon');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(SVG, 'use');
  use.setAttribute('href', `/icons/lucide.svg#${name}`);
  svg.append(use);
  return svg;
}

// The session's tile, like an app icon. Sizes: lg, md, '' (44px), sm, xs.
function sessionTile(id, size = '', look) {
  const icon = look ? look.icon : sessionById(id)?.icon;
  const node = el('span', `tile ${size} ${tileColorClass(id, look)}`);
  node.setAttribute('aria-hidden', 'true');
  if (icon) node.append(iconSvg(icon));
  else node.textContent = initials(id);
  return node;
}

// ---------- session state ----------

function sessionById(id) {
  return state.sessions.find((s) => s.id === id);
}

function waitingCount(id) {
  return state.permissions.filter((p) => p.sessionId === id).length;
}

// One answer to "what is this session doing?", used by the list, the header and the chat.
function statusOf(s) {
  if (!s) return { key: 'off', text: 'unknown' };
  if (s.status === 'stopped') return { key: 'off', text: 'stopped' };
  if (s.window === 'exited') return { key: 'bad', text: `exited (${s.exitStatus ?? '?'})` };
  if (s.window === 'missing') return { key: 'bad', text: 'window missing' };
  if (s.status === 'starting') return { key: 'working', text: 'starting' };
  const activity = state.activity.get(s.id) ?? s.activity;
  if (activity === 'approval' || waitingCount(s.id) > 0) return { key: 'waiting', text: 'waiting for approval' };
  if (!s.channel) return { key: 'bad', text: 'no channel' };
  if (activity === 'working') return { key: 'working', text: 'working' };
  return { key: 'live', text: 'idle' };
}

async function loadSessions() {
  state.sessions = await api('/api/sessions');
  for (const s of state.sessions) {
    if (s.activity) state.activity.set(s.id, s.activity);
    else state.activity.delete(s.id);
  }
  renderSessions();
  renderChatHeader();
}

// ---------- session list ----------

function renderSessions() {
  const list = $('session-list');
  const visible = state.sessions.filter((s) => state.showStopped || s.status !== 'stopped' || s.id === state.current);
  list.replaceChildren();
  if (visible.length === 0) {
    list.append(el('p', 'empty', 'No sessions running. Start one with +.'));
    return;
  }
  for (const s of visible) list.append(sessionRow(s));
}

function sessionRow(s) {
  const status = statusOf(s);
  const link = el('a', 'session');
  link.href = `#/s/${encodeURIComponent(s.id)}`;
  if (s.id === state.current) link.classList.add('active');
  if (s.status === 'stopped') link.classList.add('stopped');

  const wrap = el('span', 'tile-wrap');
  wrap.append(sessionTile(s.id), el('span', `presence ${status.key}`));
  wrap.title = status.text;

  const main = el('div', 'row-main');
  const top = el('div', 'row-top');
  top.append(el('span', 'name', s.id));
  if (s.role === 'manager') top.append(el('span', 'tag', 'manager'));
  if (s.lastMessage) top.append(el('span', 'row-time', relativeTime(s.lastMessage.createdAt, true)));

  const bottom = el('div', 'row-bottom');
  bottom.append(previewEl(s, status));
  const unread = state.unread.get(s.id) ?? 0;
  if (unread > 0 && status.key !== 'waiting') bottom.append(el('span', 'count', String(unread)));

  main.append(top, bottom);
  link.append(wrap, main);
  // Tapping a row that waits for approval opens the chat scrolled to the Allow/Deny card.
  link.addEventListener('click', () => { state.focusApprovals = status.key === 'waiting'; });
  return link;
}

function previewEl(s, status) {
  if (status.key === 'waiting') return el('span', 'preview waiting', 'Waiting for approval');
  if (status.key === 'working') {
    const node = el('span', 'preview working dots', 'working');
    return node;
  }
  if (status.key === 'off' || status.key === 'bad') return el('span', 'preview', status.text);
  const m = s.lastMessage;
  if (!m) return el('span', 'preview', `${s.projectDir}`);
  const who = m.from === null ? 'You: ' : m.from === s.id ? '' : `${m.from}: `;
  return el('span', 'preview', who + m.body.replace(/\s+/g, ' '));
}

// ---------- chat ----------

async function openSession(id) {
  state.current = id;
  state.unread.delete(id);
  state.expanded.clear();
  document.body.classList.add('in-chat');
  $('placeholder').hidden = true;
  $('chat-bar').hidden = false;
  $('composer').hidden = false;
  closeTerminal();
  renderSessions();
  renderChatHeader();
  renderApprovals();
  state.messages = [];
  $('messages').replaceChildren(el('p', 'empty', 'Loading…'));
  try {
    state.messages = await api(`/api/sessions/${encodeURIComponent(id)}/messages?limit=200`);
    if (state.current === id) renderMessages(true);
  } catch (e) {
    toast(e.message);
  }
  if (state.focusApprovals) {
    state.focusApprovals = false;
    focusApproval();
  } else if (matchMedia('(min-width: 800px)').matches) {
    // On a phone the keyboard would cover half the chat, so only focus on wide screens.
    $('input').focus();
  }
}

function closeSession() {
  state.current = null;
  document.body.classList.remove('in-chat');
  $('placeholder').hidden = false;
  $('chat-bar').hidden = true;
  $('composer').hidden = true;
  $('approvals').hidden = true;
  $('messages').replaceChildren();
  closeTerminal();
  renderSessions();
}

function renderChatHeader() {
  const s = sessionById(state.current);
  if (!s) return;
  const status = statusOf(s);
  const wrap = el('span', 'tile-wrap');
  wrap.append(sessionTile(s.id, 'md'), el('span', `presence ${status.key}`));
  $('chat-avatar').replaceChildren(wrap);
  $('chat-title').textContent = s.id;
  $('chat-role').hidden = s.role !== 'manager';

  const sub = $('chat-sub');
  sub.replaceChildren();
  if (status.key === 'working') sub.append(el('span', 'working dots', 'working'));
  else if (status.key !== 'waiting') sub.append(status.text);
  if (status.key !== 'waiting') sub.append(' · ');
  sub.append(s.projectDir);

  // A clear, clickable "waiting for approval" chip: jumps to the Allow/Deny card, or opens the
  // terminal if the prompt wasn't relayed (sessions started before the relay existed).
  $('chat-approval').hidden = status.key !== 'waiting';

  const running = s.status !== 'stopped';
  $('stop-btn').hidden = !running;
  $('input').disabled = !running;
  $('send').disabled = !running;
  $('input').placeholder = running ? `Message ${s.id}` : 'This session is stopped';
  renderTyping();
}

// Messages in the chat with session X:
//   out      you -> X                         right, accent bubble
//   in       X -> you                         left, with X's avatar and colour
//   activity X <-> another session (manager)  one muted line, tap to expand
function kindOf(m) {
  if (m.from === null) return 'out';
  if (m.to === null) return 'in';
  return 'activity';
}

// Re-renders the whole chat (at most 200 messages, so it's cheap). Stays at the bottom if
// you were there; otherwise keeps your place while you scroll back through history.
function renderMessages(toBottom = false) {
  const box = $('messages');
  const nearBottom = toBottom || box.scrollHeight - box.scrollTop - box.clientHeight < 140;
  box.replaceChildren();
  if (state.messages.length === 0) {
    box.append(el('p', 'empty', 'No messages yet. Say hello.'));
    renderTyping();
    return;
  }
  let lastDay = '';
  state.messages.forEach((m, i) => {
    const day = new Date(m.createdAt).toDateString();
    if (day !== lastDay) {
      box.append(el('div', 'day', dayLabel(m.createdAt)));
      lastDay = day;
    }
    const prev = state.messages[i - 1];
    const next = state.messages[i + 1];
    box.append(kindOf(m) === 'activity' ? activityEl(m) : messageEl(m, !sameGroup(prev, m), !sameGroup(m, next)));
  });
  renderTyping();
  if (nearBottom) box.scrollTop = box.scrollHeight;
}

// Bubbles from the same sender within 5 minutes form a group: one avatar, one timestamp.
function sameGroup(a, b) {
  if (!a || !b || kindOf(a) !== kindOf(b) || kindOf(a) === 'activity') return false;
  if (a.from !== b.from) return false;
  if (new Date(a.createdAt).toDateString() !== new Date(b.createdAt).toDateString()) return false;
  return new Date(b.createdAt) - new Date(a.createdAt) < 5 * 60_000;
}

function messageEl(m, first, last) {
  const kind = kindOf(m);
  const row = el('div', `msg ${kind}`);
  row.dataset.id = String(m.id);
  if (first) row.classList.add('first');
  if (last) row.classList.add('last');

  if (kind === 'in') {
    row.classList.add(tileColorClass(m.from));
    const slot = el('span', 'avatar-slot');
    if (last) slot.append(sessionTile(m.from, 'sm'));
    row.append(slot);
  }

  // No sender name: in a session's chat, every "in" bubble is from that session, and its
  // avatar and colour already say so.
  const col = el('div', 'bubble-col');
  col.append(el('div', 'bubble', m.body));
  if (last) {
    const meta = el('div', 'meta');
    meta.append(timeEl(m.createdAt));
    if (kind === 'out') {
      meta.append(' · ');
      meta.append(m.deliveredAt ? el('span', 'tick', 'delivered') : el('span', 'queued', 'queued'));
    }
    col.append(meta);
  }
  row.append(col);
  return row;
}

// Agent-to-agent message: a <details> element, so expanding works without any JavaScript.
function activityEl(m) {
  const details = el('details', `activity ${tileColorClass(m.from)}`);
  details.dataset.id = String(m.id);
  if (state.expanded.has(m.id)) details.open = true;
  details.addEventListener('toggle', () => {
    if (details.open) state.expanded.add(m.id);
    else state.expanded.delete(m.id);
  });

  const summary = el('summary');
  const route = el('span', 'route');
  route.append(m.from, el('span', 'arrow', '→'), m.to);
  const gist = el('span', 'gist', m.body.replace(/\s+/g, ' '));
  const when = timeEl(m.createdAt, 'when');
  summary.append(sessionTile(m.from, 'xs'), route, gist, when);
  if (!m.deliveredAt) summary.append(el('span', 'queued', 'queued'));

  details.append(summary, el('div', 'full', m.body));
  return details;
}

// While the session works, a "typing" bubble sits at the end of the chat.
function renderTyping() {
  const box = $('messages');
  box.querySelector('.typing')?.remove();
  const s = sessionById(state.current);
  if (!s || statusOf(s).key !== 'working') return;
  const row = el('div', `typing ${tileColorClass(s.id)}`);
  const bubble = el('div', 'bubble');
  bubble.append(el('span'), el('span'), el('span'));
  bubble.setAttribute('aria-label', `${s.id} is working`);
  row.append(sessionTile(s.id, 'sm'), bubble);
  box.append(row);
  const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 200;
  if (nearBottom) box.scrollTop = box.scrollHeight;
}

function onMessageEvent(m) {
  const involves = (id) => m.from === id || m.to === id;
  if (state.current && involves(state.current) && !state.messages.some((x) => x.id === m.id)) {
    state.messages.push(m);
    renderMessages(m.from === null); // your own message: always jump to it
  }
  // Count replies to you from sessions you aren't looking at.
  if (m.to === null && m.from && m.from !== state.current) {
    state.unread.set(m.from, (state.unread.get(m.from) ?? 0) + 1);
  }
  // Keep the list preview current without reloading everything.
  for (const id of [m.from, m.to]) {
    const s = id && sessionById(id);
    if (s) s.lastMessage = { from: m.from, to: m.to, body: m.body, createdAt: m.createdAt };
  }
  renderSessions();
}

function onDeliveredEvent({ id }) {
  const m = state.messages.find((x) => x.id === id);
  if (!m) return;
  m.deliveredAt = new Date().toISOString();
  renderMessages();
}

function onActivityEvent({ id, activity }) {
  state.activity.set(id, activity);
  renderSessions();
  if (id === state.current) renderChatHeader();
}

async function sendMessage(e) {
  e.preventDefault();
  const input = $('input');
  const text = input.value.trim();
  if (!text || !state.current) return;
  $('send').disabled = true;
  try {
    const result = await api(`/api/sessions/${encodeURIComponent(state.current)}/messages`, {
      method: 'POST',
      body: { text },
    });
    input.value = '';
    autosize();
    if (!result.delivered) toast('Queued: the session has no channel right now. It gets it when it reconnects.');
  } catch (err) {
    toast(err.message);
  } finally {
    $('send').disabled = false;
    input.focus();
  }
}

// Enter sends on a keyboard; on a touch screen Enter makes a new line and the button sends.
function onComposerKey(e) {
  const touch = matchMedia('(pointer: coarse)').matches;
  if (e.key === 'Enter' && !e.shiftKey && !touch && !e.isComposing) {
    e.preventDefault();
    $('composer').requestSubmit();
  }
}

// Grow the text box with its content, up to 40% of the screen; only then show a scrollbar.
function autosize() {
  const input = $('input');
  input.style.height = 'auto';
  const max = window.innerHeight * 0.4;
  input.style.height = `${Math.min(input.scrollHeight, max)}px`;
  input.style.overflowY = input.scrollHeight > max ? 'auto' : 'hidden';
}

async function stopCurrent() {
  const id = state.current;
  if (!id || !confirm(`Stop session "${id}"? Claude Code in it will exit.`)) return;
  $('stop-btn').disabled = true;
  try {
    await api(`/api/sessions/${encodeURIComponent(id)}/stop`, { method: 'POST', body: {} });
    await loadSessions();
  } catch (e) {
    toast(e.message);
  } finally {
    $('stop-btn').disabled = false;
  }
}

// ---------- permission prompts ----------
// When a session wants to run a tool its permission mode doesn't allow (a Bash command in
// acceptEdits, say), Claude Code shows a prompt in the terminal AND relays it here. Whichever
// answer comes first wins: the terminal dialog closes when you tap Allow or Deny here.

async function loadPermissions() {
  state.permissions = await api('/api/permissions');
  renderApprovals();
  renderSessions();
  renderChatHeader();
  updateTitle();
}

function renderApprovals() {
  const box = $('approvals');
  const mine = state.permissions.filter((p) => p.sessionId === state.current);
  box.hidden = mine.length === 0;
  box.replaceChildren(...mine.map(approvalEl));
}

// The "waiting for approval" chip: go to the card, or to the terminal if there's no card
// (a session whose channel server predates the relay only shows the prompt on screen).
function focusApproval() {
  const card = $('approvals').querySelector('.approval');
  if (card) {
    card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    card.querySelector('.allow')?.focus({ preventScroll: true });
  } else if ($('terminal').hidden) {
    toggleTerminal();
  }
}

function approvalEl(p) {
  const card = el('div', 'approval');
  const head = el('div', 'approval-head');
  head.append(el('span', 'tool', p.toolName), el('span', null, 'wants your approval'), timeEl(p.createdAt, 'when'));
  card.append(head);
  if (p.description) card.append(el('div', 'approval-desc', p.description));
  // What the tool will do. Read it before allowing.
  card.append(el('pre', null, previewText(p.inputPreview)));

  const actions = el('div', 'approval-actions');
  const dismiss = el('button', 'text-btn', 'Dismiss');
  dismiss.type = 'button';
  dismiss.title = 'Hide this without answering (for example, if you answered in the terminal)';
  const deny = el('button', 'deny', 'Deny');
  deny.type = 'button';
  const allow = el('button', 'allow', 'Allow');
  allow.type = 'button';
  const buttons = [dismiss, deny, allow];
  const decide = async (behavior) => {
    buttons.forEach((b) => { b.disabled = true; });
    try {
      await api(`/api/sessions/${encodeURIComponent(p.sessionId)}/permissions/${p.requestId}`, {
        method: 'POST',
        body: { behavior },
      });
      removePermission(p.sessionId, p.requestId);
    } catch (e) {
      toast(e.message);
      buttons.forEach((b) => { b.disabled = false; });
      loadPermissions().catch(() => {});
    }
  };
  dismiss.addEventListener('click', () => decide('dismiss'));
  deny.addEventListener('click', () => decide('deny'));
  allow.addEventListener('click', () => decide('allow'));
  actions.append(...buttons);
  card.append(actions, el('p', 'hint', 'Applies to this one call only.'));
  return card;
}

// Claude Code sends the tool's arguments as JSON text. For a shell command, show just the
// command; otherwise pretty-print. If it isn't valid JSON (it may be cut off at 3,500
// characters), show it exactly as received.
function previewText(raw) {
  try {
    const args = JSON.parse(raw);
    if (typeof args?.command === 'string') return args.command;
    return JSON.stringify(args, null, 2);
  } catch {
    return raw;
  }
}

function onPermissionEvent(p) {
  state.permissions = state.permissions.filter((x) => !(x.sessionId === p.sessionId && x.requestId === p.requestId));
  state.permissions.push(p);
  renderApprovals();
  renderSessions();
  renderChatHeader();
  updateTitle();
  if (p.sessionId !== state.current) toast(`${p.sessionId} wants to use ${p.toolName}. Open it to approve.`);
  navigator.vibrate?.(200); // a buzz on Android phones; ignored elsewhere
}

function onPermissionDecidedEvent({ sessionId, requestId }) {
  // requestId null = all of that session's prompts expired (it stopped or restarted).
  state.permissions = state.permissions.filter((x) =>
    !(x.sessionId === sessionId && (requestId === null || x.requestId === requestId)));
  renderApprovals();
  renderSessions();
  renderChatHeader();
  updateTitle();
}

function removePermission(sessionId, requestId) {
  onPermissionDecidedEvent({ sessionId, requestId });
}

// "(2) Switchboard" in the tab title, so a waiting prompt shows even in another tab.
function updateTitle() {
  const n = state.permissions.length;
  document.title = n > 0 ? `(${n}) Switchboard` : 'Switchboard';
}

// ---------- terminal peek ----------
// Shows the session's tmux screen as text: the way to see a prompt or a dialog the session is
// stuck on that never shows up as a message.

async function refreshTerminal() {
  if (!state.current) return;
  try {
    const result = await api(`/api/sessions/${encodeURIComponent(state.current)}/screen`);
    const pre = $('term-screen');
    pre.textContent = result.screen;
    pre.scrollTop = pre.scrollHeight;
  } catch (e) {
    $('term-screen').textContent = e.message;
  }
}

function toggleTerminal() {
  if ($('terminal').hidden) {
    $('terminal').hidden = false;
    $('term-btn').setAttribute('aria-pressed', 'true');
    refreshTerminal();
    // Refresh every 3 seconds while open and the page is visible.
    state.terminalTimer = setInterval(() => { if (!document.hidden) refreshTerminal(); }, 3000);
  } else {
    closeTerminal();
  }
}

function closeTerminal() {
  $('terminal').hidden = true;
  $('term-btn').setAttribute('aria-pressed', 'false');
  clearInterval(state.terminalTimer);
  state.terminalTimer = null;
}

// ---------- icon and colour picker (tap the tile in the chat header) ----------

const picker = { id: null, icon: null, color: null, names: null };

async function openPicker() {
  const s = sessionById(state.current);
  if (!s) return;
  try {
    picker.names ??= await api('/api/icons');
  } catch (e) {
    toast(e.message);
    return;
  }
  picker.id = s.id;
  picker.icon = s.icon ?? null;
  picker.color = s.color ?? null;
  $('tile-for').textContent = s.id;
  $('tile-search').value = '';
  $('tile-status').textContent = '';
  renderPicker();
  $('tile-dialog').showModal();
}

function renderPicker() {
  $('tile-preview').replaceChildren(sessionTile(picker.id, 'lg', picker));

  // Colours: "Auto" (the hashed default) and the 8 palette colours.
  const colors = [{ value: null, cls: `c${hashColor(picker.id)}`, label: 'Auto' }];
  for (let i = 0; i < 8; i++) colors.push({ value: i, cls: `c${i}`, label: '' });
  $('tile-colors').replaceChildren(...colors.map((c) => {
    const b = el('button', `swatch ${c.cls}`, c.label);
    b.type = 'button';
    b.setAttribute('role', 'radio');
    b.setAttribute('aria-checked', String(picker.color === c.value));
    b.setAttribute('aria-label', c.value === null ? 'Automatic colour' : `Colour ${c.value + 1}`);
    b.addEventListener('click', () => { picker.color = c.value; renderPicker(); });
    return b;
  }));

  // Icons: "initials" first, then every icon whose name matches the search.
  const query = $('tile-search').value.trim().toLowerCase();
  const names = picker.names.filter((n) => !query || n.includes(query));
  const choices = [null, ...names].map((name) => {
    const b = el('button', `icon-choice ${tileColorClass(picker.id, picker)}`);
    b.type = 'button';
    b.setAttribute('role', 'radio');
    b.setAttribute('aria-checked', String(picker.icon === name));
    b.title = name ?? 'Initials';
    b.setAttribute('aria-label', name ?? 'Initials');
    if (name) b.append(iconSvg(name));
    else b.textContent = initials(picker.id);
    b.addEventListener('click', () => { picker.icon = name; renderPicker(); });
    return b;
  });
  $('tile-icons').replaceChildren(...choices);
}

async function savePicker(e) {
  e.preventDefault();
  $('tile-save').disabled = true;
  try {
    await api(`/api/sessions/${encodeURIComponent(picker.id)}/appearance`, {
      method: 'PUT',
      body: { icon: picker.icon, color: picker.color },
    });
    $('tile-dialog').close();
    await loadSessions();
    renderMessages();
  } catch (err) {
    $('tile-status').textContent = err.message;
  } finally {
    $('tile-save').disabled = false;
  }
}

// ---------- start dialog ----------

function openStartDialog() {
  $('start-status').textContent = '';
  $('start-status').classList.remove('error');
  $('start-dialog').showModal();
}

async function submitStart(e) {
  e.preventDefault();
  const form = $('start-form');
  const data = new FormData(form);
  const status = $('start-status');
  const id = String(data.get('id')).trim();
  status.classList.remove('error');
  status.textContent = 'Starting… Claude Code boots, its dialogs are answered, then its channel connects. This can take up to a minute.';
  $('start-submit').disabled = true;
  try {
    await api('/api/sessions', {
      method: 'POST',
      body: {
        id,
        projectDir: String(data.get('projectDir')).trim(),
        permissionMode: data.get('permissionMode'),
        manager: data.get('manager') === 'on',
      },
    });
    $('start-dialog').close();
    form.reset();
    await loadSessions();
    location.hash = `#/s/${encodeURIComponent(id)}`;
  } catch (err) {
    status.classList.add('error');
    status.textContent = err.message;
  } finally {
    $('start-submit').disabled = false;
  }
}

// ---------- live events (Server-Sent Events) ----------

let events = null;

function connectEvents() {
  events?.close();
  events = new EventSource('/api/events');
  // "hello" arrives on every (re)connect. Reload what's on screen, so anything that
  // happened while we were disconnected (phone asleep, hub restarted) shows up.
  events.addEventListener('hello', () => {
    setConnected(true);
    refreshAll();
  });
  events.addEventListener('message', (e) => onMessageEvent(JSON.parse(e.data)));
  events.addEventListener('delivered', (e) => onDeliveredEvent(JSON.parse(e.data)));
  events.addEventListener('sessions', () => loadSessions().catch(() => {}));
  events.addEventListener('activity', (e) => onActivityEvent(JSON.parse(e.data)));
  events.addEventListener('permission', (e) => onPermissionEvent(JSON.parse(e.data)));
  events.addEventListener('permission-decided', (e) => onPermissionDecidedEvent(JSON.parse(e.data)));
  events.onerror = () => {
    setConnected(false);
    // The browser retries by itself after network errors, but gives up for good after an
    // HTTP error such as 401. Check whether we're still signed in (api() redirects if not),
    // then reconnect ourselves.
    if (events.readyState === EventSource.CLOSED) {
      setTimeout(() => api('/api/whoami').then(connectEvents).catch(() => {}), 3000);
    }
  };
}

function setConnected(on) {
  $('conn').classList.toggle('on', on);
  $('conn').title = on ? 'Live updates: connected' : 'Live updates: reconnecting…';
}

async function refreshAll() {
  try {
    await loadSessions();
    await loadPermissions();
    if (state.current) {
      state.messages = await api(`/api/sessions/${encodeURIComponent(state.current)}/messages?limit=200`);
      renderMessages();
    }
  } catch (e) {
    toast(e.message);
  }
}

// ---------- routing: #/s/<id> opens a session, so the phone's back button works ----------

function route() {
  const match = location.hash.match(/^#\/s\/(.+)$/);
  if (match) {
    const id = decodeURIComponent(match[1]);
    if (id !== state.current) openSession(id);
  } else if (state.current) {
    closeSession();
  }
}

// ---------- time ----------

// Soft timestamps: "now", "5 min", "14:05"; older days show the weekday or date.
// The exact time is in the tooltip. Labels are refreshed every minute.
function relativeTime(iso, short = false) {
  const date = new Date(iso);
  const minutes = Math.floor((Date.now() - date) / 60_000);
  if (minutes < 1) return 'now';
  if (minutes < 60) return short ? `${minutes}m` : `${minutes} min`;
  const time = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  if (date.toDateString() === new Date().toDateString()) return time;
  if (Date.now() - date < 6 * 86_400_000) {
    const day = date.toLocaleDateString([], { weekday: 'short' });
    return short ? day : `${day} ${time}`;
  }
  return date.toLocaleDateString([], { day: 'numeric', month: 'short' });
}

function timeEl(iso, className) {
  const node = el('time', className, relativeTime(iso));
  node.dateTime = iso;
  node.title = new Date(iso).toLocaleString();
  return node;
}

function tickTimes() {
  document.querySelectorAll('time[datetime]').forEach((t) => { t.textContent = relativeTime(t.dateTime); });
  renderSessions();
}

function dayLabel(iso) {
  const d = new Date(iso);
  const yesterday = new Date(Date.now() - 86_400_000);
  if (d.toDateString() === new Date().toDateString()) return 'Today';
  if (d.toDateString() === yesterday.toDateString()) return 'Yesterday';
  return d.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long' });
}

// ---------- small helpers ----------

let toastTimer = null;
function toast(text) {
  const node = $('toast');
  node.textContent = text;
  node.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { node.hidden = true; }, 5000);
}

// ---------- start ----------

$('composer').addEventListener('submit', sendMessage);
$('input').addEventListener('keydown', onComposerKey);
$('input').addEventListener('input', autosize);
$('stop-btn').addEventListener('click', stopCurrent);
$('term-btn').addEventListener('click', toggleTerminal);
$('term-refresh').addEventListener('click', refreshTerminal);
$('chat-approval').addEventListener('click', focusApproval);
$('chat-avatar').addEventListener('click', openPicker);
$('tile-form').addEventListener('submit', savePicker);
$('tile-cancel').addEventListener('click', () => $('tile-dialog').close());
$('tile-search').addEventListener('input', renderPicker);
$('new-btn').addEventListener('click', openStartDialog);
$('start-form').addEventListener('submit', submitStart);
$('start-cancel').addEventListener('click', () => $('start-dialog').close());
$('show-stopped').addEventListener('change', (e) => { state.showStopped = e.target.checked; renderSessions(); });
$('logout').addEventListener('click', async () => {
  await fetch('/auth/logout', { method: 'POST', headers: { 'X-Switchboard': '1' }, credentials: 'same-origin' });
  location.replace('/login.html');
});
window.addEventListener('hashchange', route);
// Coming back to the tab (phone unlocked): make sure the live connection is up.
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && events?.readyState === EventSource.CLOSED) connectEvents();
});
setInterval(tickTimes, 60_000);

try {
  await api('/api/whoami'); // redirects to the login page if not signed in
  await loadSessions();
  await loadPermissions();
  route();
  connectEvents();
} catch (e) {
  if (e.message !== 'Signed out.') toast(e.message);
}
