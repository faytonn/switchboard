// Switchboard web UI. No framework and no build step: plain DOM code.
//
// Security rule for this file: text from sessions is only ever put on the page with
// textContent, never innerHTML. A session could send "<img onerror=...>", and textContent
// shows it as plain characters instead of running it. The CSP header is the second line of
// defence.

const $ = (id) => document.getElementById(id);

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  return node;
}

const state = {
  sessions: [],
  current: null,        // id of the open session
  messages: [],         // history of the open session, oldest first
  unread: new Map(),    // session id -> replies to you not seen yet
  permissions: [],      // tool prompts waiting for you, all sessions
  showStopped: false,
  terminalTimer: null,
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

// ---------- sessions ----------

function statusOf(s) {
  if (s.status === 'stopped') return { cls: 'off', text: 'stopped' };
  if (s.window === 'exited') return { cls: 'bad', text: `exited (${s.exitStatus ?? '?'})` };
  if (s.window === 'missing') return { cls: 'bad', text: 'window missing' };
  if (s.status === 'starting') return { cls: 'busy', text: 'starting' };
  return s.channel ? { cls: 'live', text: 'live' } : { cls: 'warn', text: 'no channel' };
}

async function loadSessions() {
  state.sessions = await api('/api/sessions');
  renderSessions();
  renderChatHeader();
}

function renderSessions() {
  const list = $('session-list');
  const visible = state.sessions.filter((s) => state.showStopped || s.status !== 'stopped' || s.id === state.current);
  list.replaceChildren();
  if (visible.length === 0) {
    list.append(el('p', 'empty', 'No sessions running. Start one with +.'));
    return;
  }
  for (const s of visible) {
    const status = statusOf(s);
    const link = el('a', 'session');
    link.href = `#/s/${encodeURIComponent(s.id)}`;
    if (s.id === state.current) link.classList.add('active');

    const dot = el('span', `dot ${status.cls}`);
    dot.title = status.text;
    const name = el('span', 'name', s.id);
    const meta = el('span', 'meta');
    if (s.role === 'manager') meta.append(el('span', 'role', 'manager'), ' · ');
    meta.append(status.text, ' · ', s.projectDir);

    link.append(dot, name);
    // A waiting tool prompt matters more than unread replies: the session is blocked on you.
    const waiting = state.permissions.filter((p) => p.sessionId === s.id).length;
    const unread = state.unread.get(s.id) ?? 0;
    if (waiting > 0) {
      const badge = el('span', 'badge warn', `${waiting} to approve`);
      link.append(badge);
    } else {
      link.append(unread > 0 ? el('span', 'badge', String(unread)) : el('span'));
    }
    link.append(meta);
    list.append(link);
  }
}

// ---------- chat ----------

async function openSession(id) {
  state.current = id;
  state.unread.delete(id);
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
    if (state.current === id) renderMessages();
  } catch (e) {
    toast(e.message);
  }
  // On a phone the keyboard would cover half the chat, so only focus on wide screens.
  if (matchMedia('(min-width: 800px)').matches) $('input').focus();
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
  const s = state.sessions.find((x) => x.id === state.current);
  if (!s) return;
  const status = statusOf(s);
  $('chat-dot').className = `dot ${status.cls}`;
  $('chat-title').textContent = s.id;
  $('chat-sub').textContent = `${s.role === 'manager' ? 'manager · ' : ''}${status.text} · ${s.projectDir}`;
  const running = s.status !== 'stopped';
  $('stop-btn').hidden = !running;
  $('input').disabled = !running;
  $('send').disabled = !running;
  $('input').placeholder = running ? `Message ${s.id}` : 'This session is stopped';
}

function renderMessages() {
  const box = $('messages');
  box.replaceChildren();
  if (state.messages.length === 0) {
    box.append(el('p', 'empty', 'No messages yet. Say hello.'));
    return;
  }
  let lastDay = '';
  for (const m of state.messages) {
    const day = new Date(m.createdAt).toDateString();
    if (day !== lastDay) {
      box.append(el('div', 'day', dayLabel(m.createdAt)));
      lastDay = day;
    }
    box.append(messageEl(m));
  }
  box.scrollTop = box.scrollHeight;
}

// In the chat with session X:
//   out  = you -> X       (right, blue)
//   in   = X -> you       (left)
//   side = X <-> another session, e.g. the manager (centred, dashed)
function messageEl(m) {
  const kind = m.from === null ? 'out' : m.to === null ? 'in' : 'side';
  const wrap = el('div', `msg ${kind}`);
  wrap.dataset.id = String(m.id);
  if (kind === 'side') wrap.append(el('div', 'who', `${m.from} → ${m.to}`));
  wrap.append(el('div', 'body', m.body));
  const meta = el('div', 'meta', timeLabel(m.createdAt));
  if (kind !== 'in') {
    meta.append(' · ');
    meta.append(m.deliveredAt ? el('span', 'tick', 'delivered') : el('span', 'queued', 'queued'));
  }
  wrap.append(meta);
  return wrap;
}

function onMessageEvent(m) {
  const involves = (id) => m.from === id || m.to === id;
  if (state.current && involves(state.current)) {
    if (state.messages.some((x) => x.id === m.id)) return; // already shown
    const box = $('messages');
    const nearBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 120;
    if (state.messages.length === 0) box.replaceChildren();
    state.messages.push(m);
    box.append(messageEl(m));
    if (nearBottom || m.from === null) box.scrollTop = box.scrollHeight;
  }
  // Count replies to you from sessions you aren't looking at.
  if (m.to === null && m.from && m.from !== state.current) {
    state.unread.set(m.from, (state.unread.get(m.from) ?? 0) + 1);
    renderSessions();
  }
}

function onDeliveredEvent({ id }) {
  const m = state.messages.find((x) => x.id === id);
  if (!m) return;
  m.deliveredAt = new Date().toISOString();
  const node = document.querySelector(`.msg[data-id="${id}"] .queued`);
  if (node) node.replaceWith(el('span', 'tick', 'delivered'));
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
  updateTitle();
}

function renderApprovals() {
  const box = $('approvals');
  const mine = state.permissions.filter((p) => p.sessionId === state.current);
  box.hidden = mine.length === 0;
  box.replaceChildren(...mine.map(approvalEl));
}

function approvalEl(p) {
  const card = el('div', 'approval');
  const head = el('div', 'approval-head');
  head.append(el('span', 'tool', p.toolName), el('span', null, 'wants your approval'), el('span', 'when', timeLabel(p.createdAt)));
  card.append(head);
  if (p.description) card.append(el('div', 'approval-desc', p.description));
  // What the tool will do. Read it before allowing.
  card.append(el('pre', null, previewText(p.inputPreview)));

  const actions = el('div', 'approval-actions');
  const dismiss = el('button', 'link', 'Dismiss');
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
  updateTitle();
  if (p.sessionId !== state.current) toast(`${p.sessionId} wants to use ${p.toolName}. Open it to approve.`);
  navigator.vibrate?.(200); // a buzz on Android phones; ignored elsewhere
}

function onPermissionDecidedEvent({ sessionId, requestId }) {
  // requestId null = all of that session's prompts expired (it stopped or restarted).
  if (requestId === null) {
    state.permissions = state.permissions.filter((x) => x.sessionId !== sessionId);
    renderApprovals();
    renderSessions();
    updateTitle();
  } else {
    removePermission(sessionId, requestId);
  }
}

function removePermission(sessionId, requestId) {
  state.permissions = state.permissions.filter((x) => !(x.sessionId === sessionId && x.requestId === requestId));
  renderApprovals();
  renderSessions();
  updateTitle();
}

// "(2) Switchboard" in the tab title, so a waiting prompt shows even in another tab.
function updateTitle() {
  const n = state.permissions.length;
  document.title = n > 0 ? `(${n}) Switchboard` : 'Switchboard';
}

// ---------- terminal peek ----------
// Shows the session's tmux screen as text: the way to see a permission prompt or a dialog
// the session is stuck on, which never shows up as a message.

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

// ---------- small helpers ----------

let toastTimer = null;
function toast(text) {
  const node = $('toast');
  node.textContent = text;
  node.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { node.hidden = true; }, 5000);
}

function timeLabel(iso) {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function dayLabel(iso) {
  const d = new Date(iso);
  const today = new Date();
  const yesterday = new Date(Date.now() - 86_400_000);
  if (d.toDateString() === today.toDateString()) return 'Today';
  if (d.toDateString() === yesterday.toDateString()) return 'Yesterday';
  return d.toLocaleDateString([], { weekday: 'short', day: 'numeric', month: 'short' });
}

// ---------- start ----------

$('composer').addEventListener('submit', sendMessage);
$('input').addEventListener('keydown', onComposerKey);
$('input').addEventListener('input', autosize);
$('stop-btn').addEventListener('click', stopCurrent);
$('term-btn').addEventListener('click', toggleTerminal);
$('term-refresh').addEventListener('click', refreshTerminal);
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

try {
  await api('/api/whoami'); // redirects to the login page if not signed in
  await loadSessions();
  route();
  connectEvents();
} catch (e) {
  if (e.message !== 'Signed out.') toast(e.message);
}
