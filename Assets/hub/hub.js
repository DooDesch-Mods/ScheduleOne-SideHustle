/* Side Hustle hub - the page behind the full-screen surface.
 *
 * The mod owns every fact: gamemodes, lobbies, what a click would do and how far a launch has got. It answers
 * `hub.state` with one JSON object and pushes `hub.changed` when that object moved. The page owns only what the
 * player is looking at: the tab, the selected tile or lobby, the search text, the chips and the host form.
 *
 * Every action is one `s1.call`. It answers "" when the mod took it, or a sentence the page shows when it refused.
 * Nothing here waits on the mod: a launch shows up as `status` on the next push.
 *
 * Re-rendering is per section and only when that section's data changed, so a lobby count arriving never throws
 * away a half-typed lobby name or the board's scroll position.
 */

const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
};

// The browser preview serves runtime pictures from a folder; in the game they come from the mod as s1://name.
const pic = name => (window.S1_PREVIEW ? window.S1_PREVIEW.img(name) : 's1://' + name);

// A gamemode without a picture gets its initials on the tile instead of an empty box.
function icon(name, label) {
  if (name) {
    const img = el('img', 'icon');
    img.setAttribute('src', pic(name));
    return img;
  }
  const words = String(label || '').replace(/\(.*?\)/g, ' ').split(/[^A-Za-z0-9]+/).filter(w => w);
  const letters = words.length > 1 ? words[0][0] + words[1][0] : words.length ? words[0].slice(0, 2) : '?';
  return el('div', 'icon mono', letters.toUpperCase());
}

// The engine hands the field's text on the event; a browser keeps it on the element.
const valueOf = e => (e.value !== undefined && e.value !== null ? e.value : e.target.value);

function call(name, arg) {
  let out = '';
  try { out = s1.call(name, arg === undefined ? '' : (typeof arg === 'string' ? arg : JSON.stringify(arg))); }
  catch (e) { console.error('call ' + name + ' threw: ' + e.message); return 'Something went wrong.'; }
  return out || '';
}

function read() {
  const raw = call('hub.state');
  if (!raw) { console.warn('hub.state answered nothing'); return null; }
  try { return JSON.parse(raw); } catch (e) { console.error('unreadable hub state: ' + e.message); return null; }
}

/* ---------- page state ---------- */

let S = null;                 // the mod's last answer
const ui = {
  tab: 'play',
  sel: null,                  // selected gamemode tile (id)
  form: null,                 // open host form: { id, players, visibility, lobbyName, password, policyMode, ... }
  formError: '',
  lobby: null,                // selected lobby id on the board
  chip: 'all',                // gamemode filter on the board
  search: '',
  openSeq: -1,                // last navigation request from the mod that was applied
};
const drawn = {};             // section -> JSON of what it last drew

function changed(section, data) {
  const key = JSON.stringify(data);
  if (drawn[section] === key) return false;
  drawn[section] = key;
  return true;
}

function invalidate() { for (const k in drawn) delete drawn[k]; }

function btn(label, cls, onClick) {
  const b = el('div', cls ? 'btn ' + cls : 'btn', label);
  b.addEventListener('click', e => {
    e.stopPropagation();
    if (b.classList.contains('off')) return;
    onClick();
  });
  return b;
}

// Sideload keeps the native text field of an input that left the document until the rebuild after the one that
// dropped it, as an empty grey bar at the top of the screen. Anything about to drop inputs asks for that second
// rebuild here; 100 ms puts it safely after the first one, which runs on the next frame.
let sweepQueued = false;
function sweepInputs(root) {
  if (!root || sweepQueued || root.querySelectorAll('input').length === 0) return;
  sweepQueued = true;
  setTimeout(() => { sweepQueued = false; document.body.classList.toggle('swept'); }, 100);
}

// An action the mod may refuse. Busy means a launch is already in flight, and the mod refuses that too.
function act(name, arg) {
  const why = call(name, arg);
  if (why) toast(why);
  return why;
}

// A refusal is red; the mod's own notes come with their tone ("ok", "need", "info").
let toastNode = null;
function toast(text, tone) {
  if (toastNode) toastNode.remove();
  toastNode = el('div', 'glass toast ' + (tone || 'bad'), text);
  toastNode.style.cssText = 'position:fixed;left:40px;bottom:28px;padding:12px 18px;font-weight:700';
  document.body.appendChild(toastNode);
  const mine = toastNode;
  setTimeout(() => { if (toastNode === mine) { mine.remove(); toastNode = null; } }, 4000);
}

/* ---------- top bar ---------- */

const TABS = [['play', 'Play'], ['lobbies', 'Lobbies'], ['profiles', 'Profiles'], ['settings', 'Settings']];

function renderTop() {
  const live = S ? S.live : 0;
  const steam = S ? S.steam : 'loading';
  const ver = document.getElementById('ver');
  ver.textContent = steam === 'error' ? 'Steam is not answering'
    : steam === 'loading' && live === 0 ? 'Looking for lobbies...'
    : live === 0 ? 'No open lobbies'
    : live === 1 ? '1 lobby live' : live + ' lobbies live';
  ver.className = live > 0 ? 'ver' : 'ver off';

  const lock = !!(S && S.view && S.view.lock);
  if (!changed('tabs', [ui.tab, live, lock])) return;
  const tabs = document.getElementById('tabs');
  tabs.replaceChildren();
  for (const [id, label] of TABS) {
    const t = el('div', ui.tab === id ? 'tab on' : lock ? 'tab off' : 'tab', label);
    if (id === 'lobbies' && live > 0) {
      t.textContent = '';
      t.appendChild(el('div', null, label));
      t.appendChild(el('div', 'n', live));
    }
    t.addEventListener('click', () => go(id));
    tabs.appendChild(t);
  }
}

function go(tab) {
  // A download or a restart in progress keeps the screen until it is done or cancelled.
  if (S && S.view && S.view.lock) { console.log('hub: tab ' + tab + ' ignored, ' + S.view.key + ' holds the screen'); return; }
  // Profiles and Settings are screens the mod draws; so are the join checks, which a tab leaves.
  if (tab === 'profiles' || tab === 'settings') { act('hub.legacy', tab); refresh(); return; }
  if (S && S.view) { ui.tab = tab; act('hub.open', tab); refresh(); return; }
  if (ui.tab === tab) return;
  // The chat column belongs to the lobby list; another tab takes it down.
  if (ui.tab === 'lobbies' && S && S.chat) act('hub.chat', '');
  ui.tab = tab;
  invalidate();
  render();
}

/* ---------- Play ---------- */

function modeById(id) { return S && S.modes ? S.modes.find(m => m.id === id) : null; }

// The Play tab keeps two slots: the top one holds the last gamemode and public co-op or the open host form, the
// other the tiles.
// Each is rebuilt only when its own data moved, so a lobby count arriving never resets a half-typed lobby name.
let playTop = null;
let playTiles = null;

function renderPlay(main) {
  if (!playTop || !playTop.parentElement) {
    sweepInputs(main);
    main.replaceChildren();
    playTop = el('div', 'slot');
    playTop.style.flex = 'none';
    main.appendChild(playTop);
    main.appendChild(el('div', 'h', 'GAMEMODES'));
    const scroll = el('div', 'tilescroll');
    playTiles = el('div', 'tiles');
    scroll.appendChild(playTiles);
    main.appendChild(scroll);
    delete drawn.playTop; delete drawn.tiles; delete drawn.formLobbies;
  }

  if (changed('playTop', ui.form ? ['form', ui.form.id] : ['hero', S.vanilla, S.cont, lobbiesOf('vanilla'), S.busy])) {
    sweepInputs(playTop);
    playTop.replaceChildren();
    formLobbies = null;
    if (ui.form) { playTop.appendChild(buildForm(ui.form)); delete drawn.formLobbies; }
    else {
      // The player's own save is the vanilla Continue beside the page. The last gamemode comes first here, then public
      // vanilla co-op; when the last session was vanilla co-op, the co-op card offers it instead of a strip.
      const cont = S.cont && S.cont.id !== 'vanilla' ? S.cont : null;
      if (cont) playTop.appendChild(buildContinue(cont));
      if (S.vanilla) playTop.appendChild(buildFeature(S.vanilla, S.cont && S.cont.id === 'vanilla' ? S.cont : null));
    }
  }
  if (ui.form && changed('formLobbies', [lobbiesOf(ui.form.id), S.busy])) fillFormLobbies();

  if (changed('tiles', [S.modes, S.busy, ui.sel])) {
    playTiles.replaceChildren();
    for (const m of S.modes || []) playTiles.appendChild(buildTile(m));
    if (!S.modes || S.modes.length === 0) playTiles.appendChild(el('div', 'quiet', 'No gamemodes installed.'));
  }
}

// The public co-op card: the player's own save opened to anyone on the left, the public lobbies taking players on the right.
// Vanilla only has friends-only co-op, so open lobbies are what Side Hustle adds.
function buildFeature(v, cont) {
  const box = el('div', 'glass feature');
  const off = S.busy ? ' off' : '';

  const left = el('div', 'fl');
  const head = el('div', 'fh');
  if (v.icon) head.appendChild(icon(v.icon));
  const t = el('div', 't');
  t.appendChild(el('div', 'k', 'PUBLIC CO-OP'));
  t.appendChild(el('div', 'nm', 'Play your save with anyone'));
  t.appendChild(el('div', 'd', cont ? cont.summary : v.detail));
  head.appendChild(t);
  left.appendChild(head);
  left.appendChild(el('div', 'sp'));
  const acts = el('div', 'acts');
  if (cont) acts.appendChild(btn(cont.label, 'go' + off, () => act('hub.' + cont.action, cont.id)));
  else if (v.canHost) acts.appendChild(btn(v.hostLabel, 'go' + off, () => act('hub.vanillaHost', 'vanilla')));
  acts.appendChild(btn(v.canHost ? 'Other save' : 'Pick a save', (v.canHost || cont ? '' : 'go') + off, () => act('hub.legacy', 'vanillasaves')));
  left.appendChild(acts);
  box.appendChild(left);

  // The state line and the way to the board share one row; the newest lobbies follow under it.
  const right = el('div', 'fr');
  const list = lobbiesOf('vanilla');
  const state = el('div', 'fs');
  const live = el('div', list.length > 0 ? 'live' : 'quiet');
  if (list.length > 0) live.appendChild(el('div', 'dot'));
  live.appendChild(el('div', null, list.length === 0 ? (S.steam === 'error' ? 'Steam is not answering.' : 'Nobody is hosting right now.')
    : list.length === 1 ? '1 public lobby live' : list.length + ' public lobbies live'));
  state.appendChild(live);
  state.appendChild(btn(list.length > 3 ? 'All ' + list.length : 'Browse lobbies', 'sm',
    () => { ui.chip = 'vanilla'; ui.lobby = null; go('lobbies'); }));
  right.appendChild(state);
  for (const l of list.slice(0, 3)) {
    const r = el('div', 'lr');
    const lt = el('div', 't');
    lt.appendChild(el('div', 'nm', l.name || l.host));
    lt.appendChild(el('div', 'sub ' + stateClass(l.state), l.stateText));
    r.appendChild(lt);
    r.appendChild(el('div', 'cnt', seatText(l)));
    r.appendChild(btn('Join', 'sm' + (l.canJoin && !S.busy ? '' : ' off'), () => join(l)));
    right.appendChild(r);
  }
  box.appendChild(right);
  return box;
}

// The last gamemode session, one click from running again.
function buildContinue(c) {
  const box = el('div', 'glass cont');
  box.appendChild(icon(c.icon, c.name));
  const t = el('div', 't');
  const line = el('div', 'line');
  line.appendChild(el('div', 'k', 'LAST GAMEMODE'));
  line.appendChild(el('div', 'nm', c.name));
  t.appendChild(line);
  t.appendChild(el('div', 'd', c.summary));
  box.appendChild(t);
  const off = S.busy ? ' off' : '';
  if (c.settings) box.appendChild(btn('Change settings', 'sm' + off, () => openForm(c.id)));
  box.appendChild(btn(c.label, 'go' + off, () => act('hub.' + c.action, c.id)));
  return box;
}

function buildTile(m) {
  const box = el('div', 'glass tile' + (m.kind === 'ghost' ? ' ghost' : '') + (ui.sel === m.id ? ' sel' : ''));
  box.addEventListener('click', () => { ui.sel = ui.sel === m.id ? null : m.id; render(); });

  const head = el('div', 'head');
  head.appendChild(icon(m.icon, m.name));
  const t = el('div', 't');
  t.appendChild(el('div', 'nm', m.name));
  t.appendChild(el('div', 'sub', m.sub));
  head.appendChild(t);
  box.appendChild(head);

  if (m.live > 0) {
    const live = el('div', 'live');
    live.appendChild(el('div', 'dot'));
    live.appendChild(el('div', null, m.live === 1 ? '1 lobby live' : m.live + ' lobbies live'));
    box.appendChild(live);
  } else {
    box.appendChild(el('div', 'quiet', m.desc || (m.kind === 'mode' && m.join ? 'No lobbies' : ' ')));
  }

  box.appendChild(el('div', 'sp'));
  const acts = el('div', 'acts');
  const off = S.busy ? ' off' : '';
  for (const a of m.actions || []) {
    acts.appendChild(btn(a.label, 'sm' + (a.go ? ' go' : '') + off, () => tileAction(m, a.id)));
  }
  box.appendChild(acts);
  return box;
}

function tileAction(m, id) {
  ui.sel = m.id;
  switch (id) {
    case 'host': openForm(m.id); return;
    case 'join': ui.chip = m.id; ui.lobby = null; go('lobbies'); return;
    default: act('hub.' + id, m.id); return;   // play, load, vanillaHost, hostAgain
  }
}

/* ---------- inline host form ---------- */

let formLobbies = null;

function openForm(id) {
  const raw = call('hub.hostForm', id);
  let f = null;
  try { f = raw ? JSON.parse(raw) : null; } catch (e) { console.error('unreadable host form: ' + e.message); }
  if (!f || f.error) { toast(f && f.error ? f.error : 'This gamemode cannot be hosted.'); return; }
  f.password = '';
  ui.form = f;
  ui.sel = id;
  ui.formError = '';
  if (ui.tab !== 'play') { ui.tab = 'play'; invalidate(); }
  render();
}

function closeForm() {
  ui.form = null;
  render();
}

function buildForm(f) {
  const box = el('div', 'glass detail');

  const head = el('div', 'dhead');
  head.appendChild(icon(f.icon, f.name));
  const t = el('div', 't');
  t.appendChild(el('div', 'nm', f.name));
  t.appendChild(el('div', 'sub', f.author ? 'by ' + f.author : 'Host a lobby'));
  head.appendChild(t);
  const hacts = el('div', 'acts');
  if (f.solo) hacts.appendChild(btn('Play solo', null, () => act('hub.play', f.id)));
  head.appendChild(hacts);
  box.appendChild(head);

  const cols = el('div', 'cols');
  const form = el('div', 'form');
  form.appendChild(el('div', 'h', 'HOST'));

  // Players: a stepper between 2 and what Steam and the gamemode allow.
  const pRow = field('Players');
  const step = el('div', 'step');
  const minus = el('div', 'pm', '-');
  const val = el('div', 'v', f.players);
  const plus = el('div', 'pm', '+');
  const setPlayers = n => { f.players = Math.max(f.min || 2, Math.min(f.max, n)); val.textContent = String(f.players); };
  minus.addEventListener('click', () => setPlayers(f.players - 1));
  plus.addEventListener('click', () => setPlayers(f.players + 1));
  step.appendChild(minus); step.appendChild(val); step.appendChild(plus);
  pRow.appendChild(step);
  pRow.appendChild(el('div', 'cap', 'max ' + f.max));
  form.appendChild(pRow);

  form.appendChild(toggleRow('Lobby', [['0', 'Public'], ['1', 'Friends only']], String(f.visibility),
    v => { f.visibility = Number(v); }));

  const nRow = field('Name');
  const name = el('input', 'inp');
  name.value = f.lobbyName || '';
  name.setAttribute('placeholder', f.defaultName || 'Lobby name');
  name.addEventListener('input', e => { f.lobbyName = valueOf(e); });
  name.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
  nRow.appendChild(name);
  form.appendChild(nRow);

  const wRow = field('Password');
  const pw = el('input', 'inp');
  pw.setAttribute('type', 'password');
  pw.setAttribute('placeholder', 'None');
  pw.addEventListener('input', e => { f.password = valueOf(e); });
  pw.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
  wRow.appendChild(pw);
  form.appendChild(wRow);

  if (f.policy) {
    form.appendChild(toggleRow('Mods', [['0', 'All installed'], ['1', 'Required only']], String(f.policyMode),
      v => { f.policyMode = Number(v); }));
    form.appendChild(el('div', 'fnote', 'Required only restarts the game with just the mods this gamemode needs.'));
  }

  const err = el('div', 'ferr', ui.formError || '');
  if (!ui.formError) err.style.display = 'none';
  form.appendChild(err);
  form.appendChild(el('div', 'sp'));

  const acts = el('div', 'facts');
  acts.appendChild(btn('Host lobby', 'go big', submit));
  acts.appendChild(btn('Cancel', null, closeForm));
  acts.appendChild(el('div', 'sp'));
  if (f.settings) acts.appendChild(btn('More settings', 'sm', () => act('hub.legacy', 'hostform:' + f.id)));
  form.appendChild(acts);
  cols.appendChild(form);

  formLobbies = el('div', 'lob');
  cols.appendChild(formLobbies);
  box.appendChild(cols);

  function submit() {
    const why = call('hub.host', {
      id: f.id, players: f.players, visibility: f.visibility,
      lobbyName: f.lobbyName || '', password: f.password || '', policyMode: f.policyMode || 0,
    });
    ui.formError = why;
    err.textContent = why;
    err.style.display = why ? 'flex' : 'none';
    if (!why) closeForm();
  }
  return box;
}

function field(label) {
  const row = el('div', 'f');
  row.appendChild(el('div', 'lbl', label));
  return row;
}

function toggleRow(label, options, current, onPick) {
  const row = field(label);
  const tog = el('div', 'tog');
  const nodes = [];
  for (const [value, text] of options) {
    const o = el('div', value === current ? 'o on' : 'o', text);
    o.addEventListener('click', () => {
      for (const n of nodes) n.classList.remove('on');
      o.classList.add('on');
      onPick(value);
    });
    nodes.push(o);
    tog.appendChild(o);
  }
  row.appendChild(tog);
  return row;
}

// Gamemode ids are matched without case everywhere else (C# side), so the list does the same.
const modeKey = m => String(m || '').toLowerCase();
// A lobby without a seat limit reports max 0: show the head count alone, never "3/0".
const seatText = l => l.max > 0 ? l.members + '/' + l.max : String(l.members);
function lobbiesOf(id) { return (S && S.lobbies ? S.lobbies : []).filter(l => modeKey(l.mode) === modeKey(id)); }

function fillFormLobbies() {
  if (!formLobbies || !ui.form) return;
  formLobbies.replaceChildren();
  formLobbies.appendChild(el('div', 'h', 'LOBBIES'));
  const list = lobbiesOf(ui.form.id);
  if (list.length === 0) { formLobbies.appendChild(el('div', 'quiet', 'Nobody is hosting this right now.')); return; }
  for (const l of list.slice(0, 5)) {
    const r = el('div', 'lr');
    const t = el('div', 't');
    t.appendChild(el('div', 'nm', l.name || l.host));
    t.appendChild(el('div', 'sub ' + stateClass(l.state), l.stateText));
    r.appendChild(t);
    r.appendChild(el('div', 'cnt', seatText(l)));
    r.appendChild(btn('Join', 'sm' + (l.canJoin && !S.busy ? '' : ' off'), () => join(l)));
    formLobbies.appendChild(r);
  }
  if (list.length > 5) {
    const more = btn('All ' + list.length + ' lobbies', 'sm', () => { ui.chip = ui.form.id; go('lobbies'); });
    formLobbies.appendChild(more);
  }
}

/* ---------- Lobbies: the board ---------- */

let boardList = null;
let boardPass = null;
const rows = new Map();       // lobby id -> { node, members, cells }

function stateClass(s) { return s === 'ready' ? 'ok' : s === 'install' || s === 'version' || s === 'notready' ? 'need' : s === 'full' ? 'bad' : ''; }

function visibleLobbies() {
  const q = ui.search.trim().toLowerCase();
  const vanillaFirst = (a, b) => (b.mode === 'vanilla') - (a.mode === 'vanilla');
  return (S.lobbies || []).slice().sort(vanillaFirst).filter(l => {
    if (ui.chip !== 'all' && modeKey(l.mode) !== modeKey(ui.chip)) return false;
    if (S.prefs && S.prefs.hideFull && l.max > 0 && l.members >= l.max) return false;
    if (S.prefs && S.prefs.hideLocked && l.locked) return false;
    if (q && (l.host + ' ' + l.name + ' ' + l.modeName).toLowerCase().indexOf(q) < 0) return false;
    return true;
  });
}

function renderLobbies(main) {
  // The frame (title, search, chips) is rebuilt only when the chips change; the rows are reconciled in place.
  const chips = chipData();
  const frameKey = { chips, chip: ui.chip, prefs: S.prefs, steam: S.steam, live: S.live };
  if (changed('board', frameKey) || !boardList || !boardList.parentElement) buildBoard(main, chips);
  syncRows();
  renderPass();
}

function chipData() {
  const counts = new Map();
  for (const l of S.lobbies || []) {
    const k = modeKey(l.mode);
    const c = counts.get(k) || { id: l.mode, name: l.modeName, n: 0 };
    c.n++;
    counts.set(k, c);
  }
  // Vanilla co-op always comes first, then the busiest gamemode.
  return [...counts.values()].sort((a, b) => (b.id === 'vanilla') - (a.id === 'vanilla') || b.n - a.n || a.name.localeCompare(b.name));
}

// One search field for the page's lifetime, so a rebuilt board takes the same field over instead of dropping one
// (see sweepInputs).
let searchField = null;
function searchBox() {
  if (searchField) return searchField;
  searchField = el('input', 'search');
  searchField.setAttribute('placeholder', 'Search host or gamemode');
  searchField.value = ui.search;
  searchField.addEventListener('input', e => { ui.search = valueOf(e); syncRows(); renderPass(); });
  return searchField;
}

function buildBoard(main, chips) {
  sweepInputs(main);
  main.replaceChildren();
  rows.clear();
  const board = el('div', 'glass board');

  const ttl = el('div', 'ttl');
  ttl.appendChild(el('div', 'nm', 'Open lobbies'));
  if (S.live > 0) {
    const live = el('div', 'live');
    live.appendChild(el('div', 'dot'));
    live.appendChild(el('div', null, S.live + ' live'));
    ttl.appendChild(live);
  }
  ttl.appendChild(el('div', 'sp'));
  ttl.appendChild(searchBox());
  board.appendChild(ttl);

  const row = el('div', 'chips');
  row.appendChild(chip('All', ui.chip === 'all', () => setChip('all')));
  for (const c of chips) row.appendChild(chip(c.name + ' ' + c.n, ui.chip === c.id, () => setChip(c.id)));
  row.appendChild(el('div', 'sp'));
  const prefs = S.prefs || {};
  row.appendChild(chip('Hide full', !!prefs.hideFull, () => act('hub.pref', { hideFull: !prefs.hideFull })));
  row.appendChild(chip('Hide locked', !!prefs.hideLocked, () => act('hub.pref', { hideLocked: !prefs.hideLocked })));
  board.appendChild(row);

  boardList = el('div', 'list');
  board.appendChild(boardList);
  boardPass = el('div', 'passslot');
  board.appendChild(boardPass);
  main.appendChild(board);
}

function chip(label, on, onClick) {
  const c = el('div', on ? 'chip on' : 'chip', label);
  c.addEventListener('click', onClick);
  return c;
}

function setChip(id) {
  ui.chip = id;
  drawn.board = null;
  render();
}

function syncRows() {
  if (!boardList) return;
  const list = visibleLobbies();
  const want = new Set(list.map(l => l.id));

  for (const [id, r] of rows) if (!want.has(id)) { r.node.remove(); rows.delete(id); }

  // Steam is not answering, or nobody hosts: one sentence and the way forward, never an empty board.
  const emptyNode = boardList.querySelector('.empty');
  if (list.length === 0) {
    if (!emptyNode) boardList.appendChild(buildEmpty());
    return;
  }
  if (emptyNode) emptyNode.remove();

  // Known rows keep their place and new ones join at the end, so the board never jumps under the pointer.
  for (const l of list) {
    const r = rows.get(l.id);
    if (!r) { const fresh = buildRow(l); rows.set(l.id, fresh); boardList.appendChild(fresh.node); continue; }
    updateRow(r, l);
  }
}

function buildEmpty() {
  const box = el('div', 'empty');
  if (S.steam === 'error') {
    box.appendChild(el('div', 'msg', 'Steam is not answering.'));
    box.appendChild(btn('Retry', null, () => act('hub.refresh')));
    return box;
  }
  const filtered = (S.lobbies || []).length > 0;
  const m = ui.chip !== 'all' ? modeById(ui.chip) : null;
  box.appendChild(el('div', 'msg', filtered ? 'No lobby matches.'
    : S.steam === 'loading' ? 'Looking for lobbies...'
    : m ? 'Nobody is hosting ' + m.name + ' right now.' : 'Nobody is hosting right now.'));
  if (m && m.actions && m.actions.some(a => a.id === 'host')) box.appendChild(btn('Host ' + m.name, 'go', () => openForm(m.id)));
  else if (!filtered) box.appendChild(btn('Host a lobby', 'go', () => go('play')));
  return box;
}

function buildRow(l) {
  const node = el('div', ui.lobby === l.id ? 'r sel' : 'r');
  node.addEventListener('click', () => selectLobby(l.id));
  node.appendChild(icon(l.icon, l.modeName));
  const c1 = el('div', 'c1');
  const nm = el('div', 'nm', l.modeName);
  const sub = el('div', 'sub', l.name || l.host);
  c1.appendChild(nm); c1.appendChild(sub);
  node.appendChild(c1);
  const c2 = el('div', 'c2', lockText(l));
  node.appendChild(c2);
  const c3 = el('div', 'c3');
  node.appendChild(c3);
  const c4 = el('div', 'c4 ' + stateClass(l.state), l.stateText);
  node.appendChild(c4);
  const r = { node, c2, c3, c4, sub, key: '' };
  updateRow(r, l, true);
  return r;
}

function lockText(l) { return l.locked ? 'Password' : l.host ? 'Host ' + l.host : 'Open'; }

function updateRow(r, l, first) {
  const key = [l.members, l.max, l.locked, l.state, l.stateText, l.name, l.host].join('|');
  if (r.key === key) return;
  const seatsMoved = !first && r.members !== l.members;
  r.key = key;
  r.members = l.members;
  r.sub.textContent = l.name || l.host;
  r.c2.textContent = lockText(l);
  r.c4.textContent = l.stateText;
  r.c4.className = 'c4 ' + stateClass(l.state);
  r.c3.replaceChildren();
  // Without a seat limit only the taken seats are drawn.
  const seats = l.max > 0 ? l.max : l.members;
  if (seats <= 12) {
    for (let i = 0; i < seats; i++) r.c3.appendChild(el('div', i < l.members ? 'seat' : 'seat e'));
  } else if (l.max <= 0) {
    r.c3.appendChild(el('div', 'seatn', String(l.members)));
  } else {
    r.c3.appendChild(el('div', 'seatn', l.members + ' of ' + l.max));
  }
  // A row whose seats changed glows for a moment instead of moving.
  if (seatsMoved) {
    r.node.classList.add('glow');
    setTimeout(() => r.node.classList.remove('glow'), 900);
  }
}

function selectLobby(id) {
  ui.lobby = ui.lobby === id ? null : id;
  // Sideload's classList has no two-argument toggle.
  for (const [rid, r] of rows) {
    if (rid === ui.lobby) r.node.classList.add('sel');
    else r.node.classList.remove('sel');
  }
  renderPass();
}

function renderPass() {
  if (!boardPass) return;
  const l = ui.lobby ? (S.lobbies || []).find(x => x.id === ui.lobby) : null;
  const data = l ? [l.id, l.modeName, l.host, l.members, l.max, l.before, l.canJoin, l.chat, l.chatOpen, S.busy] : null;
  if (!changed('pass', data)) return;
  boardPass.replaceChildren();
  if (!l) return;
  const pass = el('div', 'pass');
  const m = seg('GAMEMODE', l.modeName);
  m.className = 'seg gm';
  pass.appendChild(m);
  pass.appendChild(seg('HOST', l.host || 'Unknown'));
  pass.appendChild(seg('SEATS', l.max > 0 ? l.members + ' of ' + l.max : String(l.members)));
  const w = seg('BEFORE YOU JOIN', l.before);
  w.className = 'seg w';
  pass.appendChild(w);
  // A host who takes messages can be asked first, for a password or a seat.
  if (l.chat) {
    const c = el('div', l.chatOpen ? 'ask on' : 'ask');
    c.appendChild(lu('chat'));
    c.appendChild(el('div', null, l.chatOpen ? 'Close chat' : 'Chat'));
    c.addEventListener('click', () => act('hub.chat', l.id));
    pass.appendChild(c);
  }
  const j = el('div', l.canJoin && !S.busy ? 'join' : 'join off', 'Join');
  j.addEventListener('click', () => { if (l.canJoin && !S.busy) join(l); });
  pass.appendChild(j);
  boardPass.appendChild(pass);
}

function seg(label, value) {
  const s = el('div', 'seg');
  s.appendChild(el('div', 'h', label));
  s.appendChild(el('div', 'v', value));
  return s;
}

/* ---------- join, with the password overlay ---------- */

let dialog = null;

// A lobby the mod flags with a warning (a vanilla host that never marked the save ready) asks first; the mod
// would join it anyway, because the flag can lag behind a host who finished loading a second ago.
function join(l, anyway) {
  if (l.warn && !anyway) { confirmJoin(l); return; }
  if (!l.locked || !l.askPassword) { act('hub.join', { id: l.id, password: '', anyway: !!anyway }); return; }
  askPassword(l, !!anyway);
}

function confirmJoin(l) {
  closeDialog();
  const scrim = el('div', 'scrim');
  const box = el('div', 'glass dialog');
  box.appendChild(el('div', 'nm', l.stateText));
  box.appendChild(el('div', 'sub', l.warn));
  const acts = el('div', 'acts');
  acts.appendChild(btn('Cancel', null, closeDialog));
  acts.appendChild(btn('Try anyway', 'go', () => { closeDialog(); join(l, true); }));
  box.appendChild(acts);
  scrim.appendChild(box);
  scrim.addEventListener('click', e => { if (e.target === scrim) closeDialog(); });
  document.body.appendChild(scrim);
  dialog = scrim;
}

function askPassword(l, anyway) {
  closeDialog();
  const scrim = el('div', 'scrim');
  const box = el('div', 'glass dialog');
  box.appendChild(el('div', 'nm', 'Password required'));
  box.appendChild(el('div', 'sub', 'Enter the password for ' + (l.host ? l.host + "'s" : 'this') + ' lobby.'));
  const input = el('input', 'inp');
  input.setAttribute('type', 'password');
  input.setAttribute('placeholder', 'Password');
  let typed = '';
  input.addEventListener('input', e => { typed = valueOf(e); });
  input.addEventListener('keydown', e => { if (e.key === 'Enter') send(); });
  box.appendChild(input);
  const err = el('div', 'ferr', '');
  err.style.display = 'none';
  box.appendChild(err);
  const acts = el('div', 'acts');
  acts.appendChild(btn('Cancel', null, closeDialog));
  acts.appendChild(btn('Join', 'go', send));
  box.appendChild(acts);
  scrim.appendChild(box);
  scrim.addEventListener('click', e => { if (e.target === scrim) closeDialog(); });
  document.body.appendChild(scrim);
  dialog = scrim;
  input.focus();

  function send() {
    const why = call('hub.join', { id: l.id, password: typed, anyway: anyway });
    if (!why) { closeDialog(); return; }
    err.textContent = why;
    err.style.display = 'flex';
  }
}

function closeDialog() { if (dialog) { sweepInputs(dialog); dialog.remove(); dialog = null; } }

/* ---------- screens the mod draws: settings, profiles, the join checks, the vanilla host ---------- */

// The mod sends such a screen as `view`: a list of parts and the ids of what each click runs. Each top-level part
// is rebuilt only when its own JSON moved. The text of a field is left out of that comparison and the field node
// is kept by its key, so typing never rebuilds a field and a rebuilt part takes the same field over.
const pv = { key: null, frame: null, root: null, nodes: [], shapes: [], dialog: null, dialogShape: '' };
const fields = new Map();     // field key -> { node, value, sent, act, commit, on }

const shapeOf = v => JSON.stringify(v, (k, x) => (k === 'value' ? undefined : x));

function viewCall(id, value) {
  if (!id || !S || !S.view) return '';
  return call('hub.view', { key: S.view.key, rev: S.view.rev, id, value: value === undefined || value === null ? '' : String(value) });
}

// A click: the mod's answer moves the state, so it is read now instead of on the next push.
function viewAct(id, value) {
  if (!id) return;
  const why = viewCall(id, value);
  if (why) toast(why);
  refresh();
}

function lu(name, cls) {
  const i = el('img', cls ? 'lu ' + cls : 'lu');
  i.setAttribute('src', 'icons/' + name + '.png');
  return i;
}

function dialogText() { const f = fields.get('dialog'); return f ? f.value : ''; }

function vbtn(b) {
  const n = el('div', 'btn' + (b.cls ? ' ' + b.cls : '') + (b.off ? ' off' : ''));
  if (b.icon) n.appendChild(lu(b.icon));
  n.appendChild(document.createTextNode(b.label));
  n.addEventListener('click', e => {
    e.stopPropagation();
    if (b.off) return;
    viewAct(b.act, S && S.view && S.view.dialog ? dialogText() : '');
  });
  return n;
}

function kids(box, list) {
  for (const k of list || []) {
    const n = k && k.t ? part(k) : k && k.label !== undefined ? vbtn(k) : null;
    if (n) box.appendChild(n);
  }
  return box;
}

function acts(list, cls) { return kids(el('div', cls || 'acts'), list); }

function textCol(name, sub, extra) {
  const t = el('div', 't');
  t.appendChild(el('div', 'nm', name));
  if (sub) t.appendChild(el('div', 'sub', sub));
  if (extra) t.appendChild(extra);
  return t;
}

function vinput(p) {
  let f = fields.get(p.key);
  if (!f) {
    const node = el('input', p.cls || 'inp');
    if (p.pw) node.setAttribute('type', 'password');
    node.value = p.value || '';
    f = { node, value: p.value || '', sent: p.value || '' };
    node.addEventListener('input', e => { f.value = valueOf(e); if (f.act) viewCall(f.act, f.value); });
    node.addEventListener('keydown', e => { if (e.key === 'Enter') commitField(f, true); });
    // `change` comes when the field loses focus with a new text: a setting is saved without an Enter.
    node.addEventListener('change', () => { if (p.key !== 'dialog') commitField(f, false); });
    fields.set(p.key, f);
    if (p.key === 'dialog') node.focus();
  }
  f.act = p.act;
  f.commit = p.commit;
  f.node.setAttribute('placeholder', p.ph || '');
  return f.node;
}

function commitField(f, enter) {
  if (!f.commit) return;
  if (!enter && f.value === f.sent) return;
  f.sent = f.value;
  viewAct(f.commit, f.value);
}

function bigbar(pct, tone) {
  const b = el('div', 'bigbar' + (tone ? ' ' + tone : ''));
  const fill = el('div', 'fill');
  fill.style.width = Math.max(0, Math.min(100, pct)) + '%';
  b.appendChild(fill);
  return b;
}

function part(p) {
  switch (p.t) {
    case 'head': {
      const n = el('div', 'vh');
      if (p.back) {
        const b = el('div', 'back');
        b.appendChild(lu('back'));
        b.appendChild(document.createTextNode(p.backLabel || 'Back'));
        b.addEventListener('click', () => viewAct(p.back));
        n.appendChild(b);
      }
      n.appendChild(textCol(p.name, p.sub));
      if (p.acts && p.acts.length) n.appendChild(acts(p.acts));
      return n;
    }
    case 'dhead': {
      const n = el('div', 'dhead');
      if (p.mono) n.appendChild(el('div', 'icon mono', p.mono));
      n.appendChild(textCol(p.name, p.sub));
      if (p.acts && p.acts.length) n.appendChild(acts(p.acts));
      return n;
    }
    case 'card': {
      const n = el('div', 'glass card' + (p.grow ? ' grow' : ''));
      if (p.h) n.appendChild(el('div', 'h', p.h));
      kids(n, p.kids);
      const sets = n.querySelectorAll('.set');
      if (sets.length) sets[sets.length - 1].classList.add('last');
      return n;
    }
    case 'two': {
      const n = el('div', 'two');
      n.appendChild(kids(el('div', 'l'), p.l));
      n.appendChild(kids(el('div', 'r2'), p.r));
      return n;
    }
    case 'cols': {
      const n = el('div', 'cols');
      n.appendChild(kids(el('div', 'form'), p.l));
      n.appendChild(kids(el('div', 'form'), p.r));
      return n;
    }
    case 'scroll': return kids(el('div', 'scroll'), p.kids);
    case 'line': return kids(el('div', 'row gap'), p.kids);
    case 'set': {
      const n = el('div', 'set');
      const t = textCol(p.name, p.sub);
      if (p.note) t.appendChild(el('div', 'note', p.note));
      n.appendChild(t);
      if (p.ctl) n.appendChild(part(p.ctl));
      return n;
    }
    case 'tog': {
      const n = el('div', 'tog');
      const opts = [];
      (p.opts || []).forEach((o, i) => {
        const x = el('div', i === p.cur ? 'o on' : 'o', o.s);
        x.addEventListener('click', () => {
          if (i === p.cur) return;
          for (const y of opts) y.classList.remove('on');
          x.classList.add('on');
          viewAct(p.act, i);
        });
        opts.push(x);
        n.appendChild(x);
      });
      return n;
    }
    case 'input': return vinput(p);
    case 'field': {
      const n = el('div', 'f');
      n.appendChild(el('div', 'lbl', p.label));
      if (p.ctl) n.appendChild(part(p.ctl));
      if (p.cap) n.appendChild(el('div', 'cap', p.cap));
      return n;
    }
    case 'step': {
      const n = el('div', 'step');
      const minus = el('div', 'pm', '-');
      const plus = el('div', 'pm', '+');
      minus.addEventListener('click', () => { if (p.v > p.min) viewAct(p.act, p.v - 1); });
      plus.addEventListener('click', () => { if (p.v < p.max) viewAct(p.act, p.v + 1); });
      n.appendChild(minus);
      n.appendChild(el('div', 'v', p.v));
      n.appendChild(plus);
      return n;
    }
    case 'gh': {
      const n = el('div', 'gh');
      if (p.icon) n.appendChild(lu(p.icon, p.tone));
      n.appendChild(el('div', 'h', p.label));
      if (p.count) n.appendChild(el('div', 'c', p.count));
      return n;
    }
    case 'row': {
      const n = el('div', 'm' + (p.dep ? ' dep' : '') + (p.sel ? ' sel' : ''));
      if (p.act) n.addEventListener('click', () => viewAct(p.act));
      if (p.icon) n.appendChild(lu(p.icon, p.itone ? 'ic ' + p.itone : 'ic'));
      if (p.pic) {
        const img = el('img', 'icon');
        img.setAttribute('src', pic(p.pic));
        n.appendChild(img);
      } else if (p.mono) n.appendChild(el('div', 'icon mono', p.mono));
      const t = el('div', 't');
      if (p.tag) {
        const nl = el('div', 'nl');
        nl.appendChild(el('div', 'nm', p.name));
        nl.appendChild(el('div', 'tag', p.tag));
        t.appendChild(nl);
      } else t.appendChild(el('div', 'nm', p.name));
      if (p.sub) t.appendChild(el('div', 'sub', p.sub));
      n.appendChild(t);
      if (p.pct >= 0) {
        const pr = el('div', 'pr');
        const b = el('div', 'bar');
        const fill = el('div', 'fill');
        fill.style.width = Math.max(0, Math.min(100, p.pct)) + '%';
        b.appendChild(fill);
        pr.appendChild(b);
        pr.appendChild(el('div', 'v', p.st));
        n.appendChild(pr);
      } else if (p.live) {
        const l = el('div', 'live');
        l.appendChild(el('div', 'dot'));
        l.appendChild(el('div', null, p.st));
        n.appendChild(l);
      } else if (p.st) n.appendChild(el('div', 'st' + (p.tone ? ' ' + p.tone : ''), p.st));
      if (p.acts && p.acts.length) n.appendChild(acts(p.acts));
      return n;
    }
    case 'say': {
      const n = el('div', 'say' + (p.tone ? ' ' + p.tone : ''));
      if (p.icon) n.appendChild(lu(p.icon, p.tone));
      n.appendChild(textCol(p.name, p.sub));
      if (p.acts && p.acts.length) n.appendChild(acts(p.acts));
      return n;
    }
    case 'prog': {
      const n = el('div', 'prog');
      n.appendChild(bigbar(p.pct, p.tone));
      if (p.label) n.appendChild(el('div', 'v', p.label));
      return n;
    }
    case 'foot': {
      const n = el('div', 'foot');
      if (p.note) n.appendChild(el('div', 'quiet', p.note));
      kids(n, p.l);
      if (!p.note) n.appendChild(el('div', 'sp'));
      kids(n, p.r);
      return n;
    }
    case 'quiet': return el('div', p.tone ? 'quiet ' + p.tone : 'quiet', p.text);
    case 'sp': return el('div', 'sp');
    case 'title': return el('div', 'nm2', p.text);
    case 'h': return el('div', 'h', p.text);
    case 'fnote': return el('div', 'fnote', p.text);
    case 'big': {
      const n = el('div', 'big');
      if (p.icon) n.appendChild(lu(p.icon, 'bigic now'));
      n.appendChild(el('div', 'nm', p.name));
      if (p.sub) n.appendChild(el('div', 'sub', p.sub));
      if (p.pct >= 0) {
        const pr = el('div', 'prog w');
        pr.appendChild(bigbar(p.pct, p.tone));
        if (p.label) pr.appendChild(el('div', 'v', p.label));
        n.appendChild(pr);
      }
      if (p.acts && p.acts.length) n.appendChild(acts(p.acts));
      return n;
    }
    case 'save': {
      const n = el('div', 'sv' + (p.sel ? ' sel' : '') + (p.empty ? ' empty' : ''));
      if (p.act) n.addEventListener('click', () => viewAct(p.act));
      n.appendChild(el('div', 'no', p.no));
      n.appendChild(textCol(p.name, p.sub));
      if (p.cash) n.appendChild(el('div', 'cash', p.cash));
      return n;
    }
    case 'saves': return kids(el('div', 'saves'), p.kids);
    case 'facts': {
      const n = el('div', 'facts');
      n.appendChild(el('div', 'sp'));
      return kids(n, p.kids);
    }
  }
  console.warn('hub view: unknown part ' + p.t);
  return el('div');
}

function frameClass(frame) {
  return frame === 'board' ? 'glass board pv' : frame === 'detail' ? 'glass detail pv' : 'pvm';
}

function renderView(main) {
  const v = S.view;
  const key = v.key + '#' + v.seq;
  if (pv.key !== key || !pv.root || !pv.root.parentElement || pv.frame !== v.frame) {
    sweepInputs(main);
    main.replaceChildren();
    fields.clear();
    pv.key = key;
    pv.frame = v.frame;
    pv.root = el('div', frameClass(v.frame));
    pv.nodes = [];
    pv.shapes = [];
    main.appendChild(pv.root);
  }

  const parts = v.parts || [];
  for (let i = 0; i < parts.length; i++) {
    const shape = shapeOf(parts[i]);
    if (pv.shapes[i] === shape && pv.nodes[i]) continue;
    const fresh = part(parts[i]);
    const old = pv.nodes[i];
    if (old) { pv.root.insertBefore(fresh, old); sweepInputs(old); old.remove(); }
    else pv.root.appendChild(fresh);
    pv.nodes[i] = fresh;
    pv.shapes[i] = shape;
  }
  while (pv.nodes.length > parts.length) {
    const old = pv.nodes.pop();
    pv.shapes.pop();
    sweepInputs(old);
    old.remove();
  }
  renderViewDialog(v.dialog);
}

function renderViewDialog(dlg) {
  const shape = dlg ? shapeOf(dlg) : '';
  if (shape === pv.dialogShape && (!dlg || pv.dialog)) return;
  pv.dialogShape = shape;
  const old = pv.dialog;
  pv.dialog = null;
  if (!dlg) {
    fields.delete('dialog');
    if (old) { sweepInputs(old); old.remove(); }
    return;
  }
  closeDialog();
  const scrim = el('div', 'scrim');
  const box = el('div', 'glass dialog');
  const head = el('div', 'row');
  head.appendChild(el('div', 'nm', dlg.name));
  head.appendChild(el('div', 'sp'));
  const x = el('div', 'xbtn');
  x.appendChild(lu('close'));
  x.addEventListener('click', () => viewAct(dlg.cancel));
  head.appendChild(x);
  box.appendChild(head);
  if (dlg.sub) box.appendChild(el('div', 'sub', dlg.sub));
  if (dlg.items && dlg.items.length) {
    const list = el('div', 'dis');
    for (const it of dlg.items) list.appendChild(el('div', 'di', '- ' + it.s));
    box.appendChild(list);
  }
  if (dlg.input) box.appendChild(part(dlg.input));
  if (dlg.error) box.appendChild(el('div', 'ferr', dlg.error));
  const row = el('div', 'acts');
  row.appendChild(el('div', 'sp'));
  kids(row, dlg.acts);
  box.appendChild(row);
  scrim.appendChild(box);
  scrim.addEventListener('click', e => { if (e.target === scrim) viewAct(dlg.cancel); });
  document.body.appendChild(scrim);
  pv.dialog = scrim;
  if (old) { sweepInputs(old); old.remove(); }
}

// The mod took the screen back (a tab, a session, a restart): the page draws its own tabs again.
function leaveView() {
  if (!pv.root && !pv.dialog) return;
  if (pv.dialog) { sweepInputs(pv.dialog); pv.dialog.remove(); }
  pv.key = null; pv.root = null; pv.nodes = []; pv.shapes = []; pv.dialog = null; pv.dialogShape = '';
  fields.clear();
  playTop = null; boardList = null; boardPass = null;
  invalidate();
}

/* ---------- status panel ---------- */

function renderStatus() {
  const st = S ? S.status : null;
  if (!changed('status', st)) return;
  const side = document.getElementById('side');
  side.replaceChildren();
  if (!st) return;
  const box = el('div', 'glass status');
  box.appendChild(el('div', 'h', st.title));
  const stns = el('div', 'stns');
  box.appendChild(stns);
  for (const s of st.stations || []) {
    const row = el('div', 'stn ' + (s.state || 'todo'));
    row.appendChild(el('div', 'd'));
    row.appendChild(el('div', 'lb', s.label));
    if (s.note) row.appendChild(el('div', 'note', s.note));
    stns.appendChild(row);
    if (s.state === 'now' && st.progress >= 0) {
      const bar = el('div', 'bar');
      const fill = el('div', 'fill');
      fill.style.width = Math.max(0, Math.min(100, st.progress)) + '%';
      bar.appendChild(fill);
      stns.appendChild(bar);
    }
  }
  if (st.error) box.appendChild(el('div', 'serr', st.error));
  if (st.actions && st.actions.length) {
    const acts = el('div', 'sacts');
    for (const a of st.actions) acts.appendChild(btn(a.label, a.go ? 'go' : null, () => act('hub.status', a.id)));
    box.appendChild(acts);
  }
  side.appendChild(box);
}

/* ---------- navigation requests from the mod ---------- */

// After a session, a refusal or a restart the mod reopens the hub where the player was: a gamemode's tile,
// its host form or its lobbies. Each request carries a sequence number so it is applied once.
function applyOpen() {
  const o = S && S.open;
  if (!o || o.seq === ui.openSeq) return;
  ui.openSeq = o.seq;
  closeDialog();
  ui.sel = o.mode || null;
  ui.lobby = null;
  if (o.tab === 'lobbies') { ui.form = null; ui.chip = o.mode || 'all'; ui.tab = 'lobbies'; }
  else { ui.tab = 'play'; if (!o.form) ui.form = null; }
  invalidate();
  if (o.form && o.mode) openForm(o.mode);
}

// Right-click steps back one level; the mod raises it because a surface has no back gesture of its own.
function back() {
  if (dialog) { closeDialog(); return; }
  if (S && S.view) {
    if (S.view.dialog) viewAct(S.view.dialog.cancel);
    else if (S.view.back) viewAct(S.view.back);
    return;
  }
  if (ui.form) { closeForm(); return; }
  if (ui.tab === 'lobbies' && ui.lobby) { selectLobby(ui.lobby); return; }
  if (ui.tab !== 'play') { go('play'); return; }
  if (ui.sel) { ui.sel = null; render(); }
}

/* ---------- render ---------- */

function render() {
  renderTop();
  renderStatus();
  if (!S) return;
  const main = document.getElementById('main');
  const page = main.parentElement;
  const want = (S.view ? S.view.chat : S.chat) ? 'page chat' : 'page';
  if (page.className !== want) page.className = want;
  if (S.view) { renderView(main); return; }
  leaveView();
  if (ui.tab === 'lobbies') { playTop = null; renderLobbies(main); }
  else { boardList = null; boardPass = null; renderPlay(main); }
}

function refresh() {
  const next = read();
  if (!next) return;
  S = next;
  applyOpen();
  if (S.view) ui.tab = S.view.tab || ui.tab;
  render();
}

s1.on('hub.changed', refresh);
s1.on('hub.back', back);
s1.on('hub.toast', raw => {
  try { const t = JSON.parse(raw); toast(t.text, t.tone || 'info'); }
  catch (e) { console.error('unreadable hub toast: ' + e.message); }
});
refresh();
