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

// An action the mod may refuse. Busy means a launch is already in flight, and the mod refuses that too.
function act(name, arg) {
  const why = call(name, arg);
  if (why) toast(why);
  return why;
}

let toastNode = null;
function toast(text) {
  if (toastNode) toastNode.remove();
  toastNode = el('div', 'glass toast', text);
  toastNode.style.cssText = 'position:fixed;left:40px;bottom:28px;padding:12px 18px;color:#F1707A;font-weight:700';
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

  if (!changed('tabs', [ui.tab, live])) return;
  const tabs = document.getElementById('tabs');
  tabs.replaceChildren();
  for (const [id, label] of TABS) {
    const t = el('div', ui.tab === id ? 'tab on' : 'tab', label);
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
  // Profiles and Settings are still the uGUI screens; the mod swaps to them and back.
  if (tab === 'profiles' || tab === 'settings') { act('hub.legacy', tab); return; }
  if (ui.tab === tab) return;
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

  const right = el('div', 'fr');
  const list = lobbiesOf('vanilla');
  const live = el('div', list.length > 0 ? 'live' : 'quiet');
  if (list.length > 0) live.appendChild(el('div', 'dot'));
  live.appendChild(el('div', null, list.length === 0 ? (S.steam === 'error' ? 'Steam is not answering.' : 'Nobody is hosting right now.')
    : list.length === 1 ? '1 public lobby live' : list.length + ' public lobbies live'));
  right.appendChild(live);
  for (const l of list.slice(0, 3)) {
    const r = el('div', 'lr');
    const lt = el('div', 't');
    lt.appendChild(el('div', 'nm', l.name || l.host));
    lt.appendChild(el('div', 'sub ' + stateClass(l.state), l.stateText));
    r.appendChild(lt);
    r.appendChild(el('div', 'cnt', l.members + '/' + l.max));
    r.appendChild(btn('Join', 'sm' + (l.canJoin && !S.busy ? '' : ' off'), () => join(l)));
    right.appendChild(r);
  }
  right.appendChild(el('div', 'sp'));
  right.appendChild(btn(list.length > 3 ? 'All ' + list.length + ' vanilla lobbies' : 'Browse lobbies', 'sm',
    () => { ui.chip = 'vanilla'; ui.lobby = null; go('lobbies'); }));
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
  step.appendChild(el('div', 'cap', 'max ' + f.max));
  pRow.appendChild(step);
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

function lobbiesOf(id) { return (S && S.lobbies ? S.lobbies : []).filter(l => l.mode === id); }

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
    r.appendChild(el('div', 'cnt', l.members + '/' + l.max));
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
    if (ui.chip !== 'all' && l.mode !== ui.chip) return false;
    if (S.prefs && S.prefs.hideFull && l.members >= l.max) return false;
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
    const c = counts.get(l.mode) || { id: l.mode, name: l.modeName, n: 0 };
    c.n++;
    counts.set(l.mode, c);
  }
  // Vanilla co-op always comes first, then the busiest gamemode.
  return [...counts.values()].sort((a, b) => (b.id === 'vanilla') - (a.id === 'vanilla') || b.n - a.n || a.name.localeCompare(b.name));
}

// One search field for the page's lifetime. A dropped input leaves its native text field behind as an empty
// grey bar at the top of the screen, so a rebuilt board takes the same field over.
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
  const data = l ? [l.id, l.modeName, l.host, l.members, l.max, l.before, l.canJoin, S.busy] : null;
  if (!changed('pass', data)) return;
  boardPass.replaceChildren();
  if (!l) return;
  const pass = el('div', 'pass');
  pass.appendChild(seg('GAMEMODE', l.modeName));
  pass.appendChild(seg('HOST', l.host || 'Unknown'));
  pass.appendChild(seg('SEATS', l.max > 0 ? l.members + ' of ' + l.max : String(l.members)));
  const w = seg('BEFORE YOU JOIN', l.before);
  w.className = 'seg w';
  pass.appendChild(w);
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

function closeDialog() { if (dialog) { dialog.remove(); dialog = null; } }

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
  if (ui.tab === 'lobbies') { playTop = null; renderLobbies(main); }
  else { boardList = null; boardPass = null; renderPlay(main); }
}

function refresh() {
  const next = read();
  if (!next) return;
  S = next;
  applyOpen();
  render();
}

s1.on('hub.changed', refresh);
s1.on('hub.back', back);
refresh();
