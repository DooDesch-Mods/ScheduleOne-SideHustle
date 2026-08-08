/* Side Hustle - the phone app.
 *
 * Three tabs, and which three depends on who you are: a host gets Lobby, everyone else gets Session, and the
 * tab sits in the same place either way so the app does not rearrange itself under a player who changed role.
 *
 * The page never holds authority. Every control asks the mod for a change, the mod answers with what actually
 * happened, and the page re-renders from a fresh read. That is why setMax answers with a NUMBER rather than
 * "ok" - Steam can refuse a seat count, and a control that kept showing its own request would be lying about
 * how many people can get in.
 *
 * The render is a full rebuild. Form controls survive that in this engine, everything else does not, so nothing
 * caches an element across renders.
 */

const $ = (id) => document.getElementById(id);

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
}

/* Sized by CSS alone - the width/height ATTRIBUTES never reach this engine's layout - and tinted through
 * `color`, which is why every glyph ships flat white. */
function icon(name, className) {
  const img = document.createElement('img');
  img.setAttribute('src', 'icons/' + name + '.png');
  img.className = className || 'ico';
  return img;
}

function button(className, label, iconName, onClick) {
  const b = el('button', className);
  if (iconName) b.appendChild(icon(iconName));
  if (label) b.appendChild(el('div', null, label));
  b.addEventListener('click', onClick);
  return b;
}

/**
 * A real tooltip: a floating box over the page, anchored to the element, laid out by nobody.
 *
 * `position: fixed` is this engine's top layer - measured against the viewport, drawn last, clipped by nothing -
 * and `el.rect()` says where the anchor ended up in that same frame. Without the second half a "tooltip" can only
 * be a sibling in the row, which pushes everything along and reads as another button.
 *
 * Above the element when there is room, below it otherwise, and clamped so it never leaves the screen.
 */
function tip(anchor, text) {
  let node = null;
  const hide = () => { if (node) { node.remove(); node = null; } };

  anchor.addEventListener('mouseenter', () => {
    if (node) return;
    const r = anchor.rect();
    if (!r || !r.height) return;   // not laid out yet - nothing to anchor to

    node = el('div', 'tip', text);
    // Measured after it exists but before it is painted, so the width is an estimate. 6.2px per character at
    // 11px is close enough to keep it on screen, and the clamp below covers the rest.
    const w = Math.min(300, 18 + text.length * 6.2);
    let left = r.x + r.width / 2 - w / 2;
    if (left < 6) left = 6;
    if (left + w > 727) left = 727 - w;

    const above = r.y > 30;
    node.style.left = Math.round(left) + 'px';
    node.style.top = Math.round(above ? r.y - 26 : r.y + r.height + 6) + 'px';
    node.style.width = Math.round(w) + 'px';
    document.body.appendChild(node);
  });

  anchor.addEventListener('mouseleave', hide);
  // A click takes the pointer somewhere else and usually re-renders; a tooltip left behind would hang there.
  anchor.addEventListener('click', hide);
}

function ask(name, arg) {
  return s1.call(name, arg === undefined ? '' : arg);
}

function state() {
  const raw = ask('lobby.state');
  if (!raw) return { host: false, inLobby: false };
  try { return JSON.parse(raw); } catch (e) { console.error('unreadable state:', e.message); return { host: false, inLobby: false }; }
}

let tab = s1.storage.get('tab', 'lobby');

/* Every answer the mod gives comes back through here.
 *
 * A toast rather than a line at the foot of the page, because the answer to "did that work" has to arrive
 * where the player is looking, not in the one strip of the screen nobody reads. It leaves on its own after
 * 3.5s - long enough to read six words, short enough that it is gone before the next thing is clicked.
 *
 * The class lands one frame late on purpose: a transition needs to see the starting value painted once,
 * otherwise the box appears already at its end state and nothing moves. */
function toast(text, kind) {
  const host = $('toasts');
  if (!host) return;

  // One at a time: the newest answer replaces the last one. A stack would be a log, not feedback - and every
  // toast is anchored to the same corner, so two of them would sit on top of each other anyway.
  host.replaceChildren();

  const t = el('div', 'toast' + (kind ? ' ' + kind : ''));
  t.appendChild(icon(kind === 'bad' ? 'blocked' : 'ok'));
  t.appendChild(el('div', 'toast-text', text));
  host.appendChild(t);

  setTimeout(() => t.classList.add('in'), 16);
  setTimeout(() => {
    // Only if it is still the current one - a newer toast has already taken the corner.
    if (t.parentElement !== host) return;
    t.classList.remove('in');
    setTimeout(() => { if (t.parentElement === host) t.remove(); }, 220);
  }, 3500);
}

function say(text, kind) {
  toast(text, kind);
  render();
}

/* ---- the diagnosis strip ----
 *
 * The focal moment of the whole app. A host opens this because somebody said "I can't get in", so the first
 * thing on screen names the reason and carries the one control that removes it. Ranked by how completely each
 * one blocks a join: no seat beats not-listed, because a listed lobby with no seats is still a wall.
 */
function blockers(s) {
  const out = [];
  // First because it is total: without the hash on the lobby there is nothing for a joiner to check the mod list
  // against, so the sync step fails for everyone, however many seats are free.
  if (s.modlist === 'missing') {
    out.push({
      icon: 'modset',
      line: 'Your mod list is not on the lobby. Joining fails for everyone.',
      label: 'Publish again',
      act: republish,
      can: true,
    });
  }
  if (s.members >= s.max) {
    out.push({
      icon: 'seats',
      line: 'Full. Nobody else can get in.',
      label: 'One more seat',
      act: () => bumpSeats(1),
      can: s.max < s.ceiling,
    });
  }
  if (!s.public) {
    out.push({ icon: 'visibility', line: 'Friends only. Nobody can find this in the browser.', label: 'Make public', act: () => setVisibility(true), can: true });
  }
  if (s.hasPassword) {
    out.push({
      icon: 'password',
      line: s.password ? 'Password: ' + s.password : 'Password set.',
      label: 'Remove', act: () => setPassword(''), can: true,
    });
  }
  if (s.enforce) {
    out.push({ icon: 'modset', line: 'They need your mod set first.', label: 'Make optional', act: () => setEnforce(false), can: true });
  }
  if (s.canPublish && !s.published) {
    out.push({ icon: 'publish', line: 'Not listed anywhere.', label: 'Publish', act: togglePublish, can: true });
  }
  return out;
}

function diagnosis(s) {
  const found = blockers(s);
  const strip = el('div', found.length ? 'diag blocked' : 'diag');

  if (found.length === 0) {
    strip.appendChild(icon('ok'));
    const text = el('div', 'diag-text');
    text.appendChild(el('div', 'diag-line', 'Open. ' + s.members + ' of ' + s.max + ' seats taken.'));
    text.appendChild(el('div', 'diag-more', 'Anyone can find this and join.'));
    strip.appendChild(text);
    return strip;
  }

  const first = found[0];
  strip.appendChild(icon(first.icon));
  const text = el('div', 'diag-text');
  text.appendChild(el('div', 'diag-line', first.line));
  // The rest are counted, not listed: a strip that grows to five lines stops being the thing you read first.
  if (found.length > 1) {
    const rest = found.slice(1).map((b) => b.line.replace(/[.:].*$/, '')).join(', ');
    text.appendChild(el('div', 'diag-more', 'Also: ' + rest.toLowerCase() + '.'));
  }
  strip.appendChild(text);
  if (first.can) strip.appendChild(button('btn go wide', first.label, null, first.act));
  return strip;
}

/* ---- actions ---- */

/* A failed control changed nothing, so it gets a toast and NOT a re-render. The rebuild is what threw away the
 * name somebody had just typed and left them looking at an empty box. */

/* The seat count the host has clicked their way to but Steam has not been told about yet, and the timer that will
 * tell it. Null means "no pending change" - the mod's number is the truth. */
let seatsWanted = null;
let seatsTimer = null;

function shownMax(s) {
  return seatsWanted === null ? s.max : seatsWanted;
}

/* One lobby-data write per burst of clicks, not one per click: the number moves at once, Steam hears about it
 * 400ms after the last step. Holding + used to stall the app for a frame on every press. */
function bumpSeats(by, show) {
  const s = state();
  const base = shownMax(s);
  const next = Math.max(2, Math.min(s.ceiling, base + by));
  if (next === base) return;
  seatsWanted = next;
  if (show) show(next);
  if (seatsTimer) clearTimeout(seatsTimer);
  seatsTimer = setTimeout(commitSeats, 400);
}

function commitSeats() {
  seatsTimer = null;
  const want = seatsWanted;
  seatsWanted = null;
  if (want === null) return;
  const got = parseInt(ask('lobby.setMax', String(want)), 10);
  // The exception to the toast-only rule above: the stepper is showing a number nobody accepted, so it has to go
  // back to the one Steam is handing out.
  if (isNaN(got)) { say('Could not change the seats.', 'bad'); return; }
  // Steam decides, not the page.
  if (got !== want) say('Steam kept it at ' + got + '.', 'bad');
  else say(got + ' seats.', 'ok');
}

function setVisibility(pub) {
  if (ask('lobby.setVisibility', pub ? 'pub' : 'priv') !== 'ok') { toast('Could not change who can find you.', 'bad'); return; }
  say(pub ? 'Listed publicly.' : 'Friends only now.', 'ok');
}

function setPassword(pw) {
  if (ask('lobby.setPassword', pw) !== 'ok') { toast('Steam would not take the password. Try again in a moment.', 'bad'); return; }
  say(pw.trim() ? 'Password set.' : 'Password removed.', 'ok');
}

function setEnforce(on) {
  const answer = ask('lobby.setEnforce', on ? '1' : '0');
  if (answer === 'nolist') { toast('This session has no mod list to check joiners against.', 'bad'); return; }
  if (answer !== 'ok') { toast('Could not change the mod requirement.', 'bad'); return; }
  say(on ? 'Mod set required. Unsynced players are removed.' : 'Mod set no longer required.', 'ok');
}

function republish() {
  if (ask('lobby.republish') !== 'ok') { toast('Could not put the mod list on the lobby.', 'bad'); return; }
  say('Mod list published. Joining works again.', 'ok');
}

function togglePublish() {
  const answer = ask('lobby.togglePublish');
  if (answer === 'n/a') { toast('Your session is already listed by Side Hustle itself.', 'bad'); return; }
  if (answer !== '0' && answer !== '1') { toast('Could not change publishing.', 'bad'); return; }
  say(answer === '1' ? 'Published.' : 'Withdrawn.', 'ok');
}

/* ---- host rows ---- */

function head(row, iconName, label, tail) {
  const h = el('div', 'row-head');
  h.appendChild(icon(iconName));
  h.appendChild(el('div', 'label', label));
  if (tail) h.appendChild(el('div', 'tail', tail));
  row.appendChild(h);
  return h;
}

function seatsRow(s) {
  const shown = shownMax(s);
  const row = el('div', 'row');
  head(row, 'seats', 'Seats', shown < s.members ? 'below the headcount' : 'up to ' + s.ceiling);
  const line = el('div', 'seatline');
  const stepper = el('div', 'stepper');
  // Handed to bumpSeats so a click can move the digit without a full rebuild, which is what makes holding the
  // stepper feel like a stepper.
  const num = el('div', 'seatnum', shown);
  const show = (v) => { num.textContent = String(v); };
  stepper.appendChild(button(shown <= 2 ? 'step off' : 'step', null, 'minus', () => bumpSeats(-1, show)));
  stepper.appendChild(num);
  stepper.appendChild(button(shown >= s.ceiling ? 'step off' : 'step', null, 'plus', () => bumpSeats(1, show)));
  line.appendChild(stepper);
  row.appendChild(line);
  return row;
}

function passwordRow(s) {
  const row = el('div', 'row');
  head(row, s.hasPassword ? 'password' : 'password-off', 'Join password', s.hasPassword ? 'Set' : 'Open');
  // Said plainly, because the alternative is a host who thinks this locks the door.
  // Only while a password actually exists. With none set there is nothing to be wrong about, and the line is
  // exactly the 18px that pushes this column past the screen - so the common case fits without scrolling and
  // the case that needs the warning pays for it.
  if (s.hasPassword) row.appendChild(el('div', 'hint warn', 'Only checked in the Side Hustle browser.'));

  const control = el('div', 'control');
  const field = el('input', 'field');
  field.setAttribute('maxlength', '32');
  field.setAttribute('placeholder', s.hasPassword && !s.password ? 'Set password' : 'No password');
  field.value = s.password || '';
  field.addEventListener('keydown', (e) => { if (e.key === 'Enter') setPassword(field.value); });
  control.appendChild(field);
  control.appendChild(button('btn', 'Save', null, () => setPassword(field.value)));
  if (s.hasPassword) control.appendChild(button('btn bad icon-only', null, 'close', () => setPassword('')));
  row.appendChild(control);
  return row;
}

function visibilityRow(s) {
  const row = el('div', 'row');
  head(row, 'visibility', 'Who can find you', s.public ? 'Anyone' : 'Invite only');
  const toggle = el('div', 'toggle');
  toggle.appendChild(button(s.public ? 'half on' : 'half', 'Public', null, () => { if (!s.public) setVisibility(true); }));
  toggle.appendChild(button(!s.public ? 'half on' : 'half', 'Friends', null, () => { if (s.public) setVisibility(false); }));
  row.appendChild(toggle);
  return row;
}

function enforceRow(s) {
  const row = el('div', 'row');
  head(row, 'modset', 'Require my mod set', s.enforce ? 'Required' : 'Optional');
  const toggle = el('div', 'toggle');
  toggle.appendChild(button(s.enforce ? 'half on' : 'half', 'Required', null, () => { if (!s.enforce) setEnforce(true); }));
  toggle.appendChild(button(!s.enforce ? 'half on' : 'half', 'Optional', null, () => { if (s.enforce) setEnforce(false); }));
  row.appendChild(toggle);
  return row;
}

function publishRow(s) {
  const row = el('div', 'row');
  const h = head(row, 'publish', 'Publish this session');
  const st = el('div', 'state');
  st.appendChild(el('div', s.published ? 'dot live' : 'dot'));
  st.appendChild(el('div', s.published ? 'state-text live' : 'state-text', s.published ? 'Listed' : 'Not listed'));
  h.appendChild(st);

  // The switch only applies to a co-op lobby Side Hustle did not start. Hosting a Side Hustle session, publishing
  // over it would rewrite the name, seats, visibility and join manifest with vanilla values, so the mod refuses -
  // and a button that answers "could not" is worse than no button. Say why instead.
  if (!s.canPublish) {
    row.appendChild(el('div', 'hint', 'Your session is already listed by Side Hustle itself.'));
    return row;
  }

  row.appendChild(button('btn wide', s.published ? 'Unpublish' : 'Publish', null, togglePublish));
  return row;
}

function nameRow(s) {
  const row = el('div', 'row');
  head(row, 'name', 'Lobby name', 'browser card');
  const control = el('div', 'control');
  const field = el('input', 'field');
  field.setAttribute('maxlength', '48');
  field.setAttribute('placeholder', 'Lobby name');
  field.value = s.name || '';
  const save = () => {
    if (ask('lobby.setName', field.value) !== 'ok') {
      toast('Steam would not take the name. Try again in a moment.', 'bad');
      return;
    }
    say('Name changed.', 'ok');
  };
  field.addEventListener('keydown', (e) => { if (e.key === 'Enter') save(); });
  control.appendChild(field);
  control.appendChild(button('btn', 'Save', null, save));
  row.appendChild(control);
  return row;
}

/* ---- tabs ---- */

function renderLobby(body, s) {
  body.appendChild(diagnosis(s));
  const cols = el('div', 'cols');
  const left = el('div', 'col');
  const right = el('div', 'col');
  // Left is the way in - the reason this app gets opened. Everything else is on the right.
  left.appendChild(seatsRow(s));
  left.appendChild(passwordRow(s));
  left.appendChild(visibilityRow(s));
  right.appendChild(publishRow(s));
  right.appendChild(enforceRow(s));
  right.appendChild(nameRow(s));
  cols.appendChild(left);
  cols.appendChild(right);
  body.appendChild(cols);
}

function fact(list, iconName, key, value, kind) {
  const f = el('div', 'fact');
  f.appendChild(icon(iconName));
  f.appendChild(el('div', 'fact-key', key));
  f.appendChild(el('div', kind ? 'fact-val ' + kind : 'fact-val', value));
  list.appendChild(f);
}

function renderSession(body, s) {
  const wrap = el('div', 'one');
  const facts = el('div', 'facts');
  fact(facts, 'name', "Whose game", s.hostName || 'Unknown');
  fact(facts, 'seats', 'Seats taken', s.members + ' of ' + s.max, s.members >= s.max ? 'warn' : null);
  fact(facts, 'visibility', 'Listed', s.public ? 'Public' : 'Friends only', s.public ? 'live' : null);
  fact(facts, 'password', 'Password', s.hasPassword ? 'Required' : 'None');
  fact(facts, 'modset', 'Host mod set', s.enforce ? 'Required' : 'Optional', s.enforce ? 'warn' : null);
  if (s.runtime) fact(facts, 'publish', 'Game branch', s.runtime === 'mono' ? 'Mono' : 'IL2CPP', s.runtime === 'mono' ? 'warn' : 'live');
  wrap.appendChild(facts);
  body.appendChild(wrap);
}

function renderPlayers(body) {
  const wrap = el('div', 'one');
  let people = [];
  const raw = ask('lobby.players');
  if (raw) { try { people = JSON.parse(raw); } catch (e) { console.error('unreadable roster:', e.message); } }

  if (people.length === 0) {
    body.appendChild(emptyState('players', 'Nobody here yet', 'The moment somebody joins, they show up in this list.'));
    return;
  }
  for (const p of people) {
    const person = el('div', p.self ? 'person me' : 'person');
    person.appendChild(el('div', 'person-name', p.name + (p.self ? '  (you)' : '')));
    if (p.host) person.appendChild(el('div', 'tag host', 'HOST'));
    if (p.friend) person.appendChild(el('div', 'tag friend', 'FRIEND'));
    wrap.appendChild(person);
  }
  body.appendChild(wrap);
}

/* ---- chat ----
 *
 * Not a general messenger - Side Hustle already requires WhatsDab for that. This carries one thread per person
 * who found the published lobby and could NOT get into it, so the host hears "hey, that mod isn't on Nexus"
 * instead of never learning anyone tried.
 */
let openThread = null;
let muteArmed = null;   // die Person, fuer die gerade nachgefragt wird

function renderChat(body) {
  let threads = [];
  const raw = ask('chat.threads');
  if (raw) { try { threads = JSON.parse(raw); } catch (e) { console.error('unreadable threads:', e.message); } }

  if (threads.length === 0) {
    const wrap = el('div', 'one');
    wrap.appendChild(emptyState('chat', 'Nobody has messaged you',
      ask('chat.accepting') === '1'
        ? 'Someone who finds your session but cannot get in can send you a line. It lands here.'
        : 'You have messages from strangers switched off, so nothing reaches you here.'));
    const mutedEmpty = mutedBlock();
    if (mutedEmpty) wrap.appendChild(mutedEmpty);
    wrap.appendChild(acceptingRow());
    body.appendChild(wrap);
    return;
  }

  if (!threads.some((t) => t.id === openThread)) openThread = threads[0].id;

  const cols = el('div', 'cols tall');
/** Everyone muted, with the way back. A mute is one click next to a thread, so it WILL be hit by accident -
 *  and once it is, the thread is gone and there is nothing left to undo it from. The name is kept for exactly
 *  this. The conversation itself does not come back; only the next thing they say. */
function mutedBlock() {
  let muted = [];
  const raw = ask('chat.muted');
  if (raw) { try { muted = JSON.parse(raw); } catch (e) { console.error('unreadable mute list:', e.message); } }
  if (muted.length === 0) return null;

  const box = el('div', 'mutedbox');
  box.appendChild(el('div', 'mutedhead', muted.length === 1 ? '1 person muted' : muted.length + ' people muted'));
  for (const m of muted) {
    const row = el('div', 'mutedrow');
    row.appendChild(el('div', 'mutedname', m.name));
    row.appendChild(button('btn small', 'Unmute', null, () => { ask('chat.unmute', m.id); render(); }));
    box.appendChild(row);
  }
  return box;
}

/* One element for the whole session, re-appended on every render.
 *
 * Sideload rescues a form control across a rebuild by DOM IDENTITY - it looks the element up in a map built from
 * the previous paint. A field the script re-creates is therefore a different key, gets a fresh empty control, and
 * loses whatever was typed. That matters here more than anywhere: the page re-renders whenever the thread moves,
 * which is precisely while the person waiting for an answer is writing their next line. */
let composeField = null;

function composeInput() {
  if (composeField) return composeField;
  composeField = el('input', 'field');
  composeField.setAttribute('maxlength', '240');
  composeField.setAttribute('placeholder', 'Write a reply');
  // data-typing: while this is on screen the caret comes back here, so typing a reply does not walk the player
  // through the game world. Correct on the PHONE - the player is standing in the world and loose letters are key
  // bindings - and deliberately absent from the menu column, which would trap Escape instead.
  composeField.setAttribute('data-typing', '');
  return composeField;
}

  const threadcol = el('div', 'col threadcol');
  const list = el('div', 'threadlist');
  for (const t of threads) {
    const row = el('div', 'thread' + (t.id === openThread ? ' on' : ''));
    const main = el('div', 'thread-main');
    main.appendChild(el('div', 'thread-name', t.name));
    main.appendChild(el('div', 'thread-last', t.last));
    row.appendChild(main);
    if (t.unread) row.appendChild(el('div', 'dot live'));
    row.addEventListener('click', () => {
      // Clear the draft when the thread changes, or a line meant for one stranger follows to the next.
      if (t.id !== openThread) { openThread = t.id; if (composeField) composeField.value = ''; }
      render();
    });
    list.appendChild(row);
  }
  threadcol.appendChild(list);
  const muted = mutedBlock();
  if (muted) threadcol.appendChild(muted);
  cols.appendChild(threadcol);

  const pane = el('div', 'col');
  const openName = (threads.find((t) => t.id === openThread) || {}).name || '';

  // Mute is destructive and its icon cannot explain itself: this engine raises no hover event and :hover may only
  // repaint, so a tooltip is not buildable. Two clicks instead - the first one turns the icon into the question.
  const chead = el('div', 'chathead');
  chead.appendChild(el('div', 'chathead-name', openName));
  if (muteArmed === openThread) {
    chead.appendChild(button('btn bad', 'Block them?', 'mute', () => {
      ask('chat.mute', openThread);
      openThread = null;
      muteArmed = null;
      say('Blocked. They cannot reach you again.', 'ok');
    }));
    chead.appendChild(button('btn', 'Keep', null, () => { muteArmed = null; render(); }));
  } else {
    const mute = button('btn icon-only', null, 'mute', () => { muteArmed = openThread; render(); });
    // A real hover tooltip - the page hears mouseenter/mouseleave since Sideload 1.11. :hover alone could only
    // repaint the icon, never put a label next to it.
    tip(mute, 'Block them from messaging you');
    chead.appendChild(mute);
  }
  pane.appendChild(chead);

  let msgs = [];
  const mraw = ask('chat.messages', openThread);
  if (mraw) { try { msgs = JSON.parse(mraw); } catch (e) { console.error('unreadable thread:', e.message); } }

  const log = el('div', 'chatlog');
  for (const m of msgs) {
    const b = el('div', m.mine ? 'bubble mine' : 'bubble');
    b.appendChild(el('div', 'bubble-text', m.text));
    log.appendChild(b);
  }
  log.scrollToEnd();
  pane.appendChild(log);

  const compose = el('div', 'control');
  const field = composeInput();
  const send = () => {
    const text = field.value.trim();
    if (!text) return;
    // id and text on two lines - the wire format the mod's SendChat expects.
    if (ask('chat.send', openThread + String.fromCharCode(10) + text) !== 'ok') { say('Could not send that.', 'bad'); return; }
    field.value = '';
    render();
    // Give the caret back. Sending is one message in a conversation, not the end of one, and having to click into
    // the box again for every line is the whole reason this felt worse than WhatsDab - whose field is static markup
    // the rebuild never touches. Asked for AFTER the render, which is the pass that re-paints the control.
    field.focus();
  };
  field.addEventListener('keydown', (e) => { if (e.key === 'Enter') send(); });
  compose.appendChild(field);
  compose.appendChild(button('btn icon-only', null, 'send', send));
  pane.appendChild(compose);

  cols.appendChild(pane);
  body.appendChild(cols);
}

/** The one switch that turns the whole relay off - shown where a host looks when they wonder why it is quiet. */
function acceptingRow() {
  const on = ask('chat.accepting') === '1';
  const row = el('div', 'row');
  head(row, 'chat', 'Let strangers message me', on ? 'On' : 'Off');
  const toggle = el('div', 'toggle');
  toggle.appendChild(button(on ? 'half on' : 'half', 'On', null, () => { if (!on) { ask('chat.accepting', '1'); say('Strangers can reach you.', 'ok'); } }));
  toggle.appendChild(button(!on ? 'half on' : 'half', 'Off', null, () => { if (on) { ask('chat.accepting', '0'); say('Strangers are refused.', 'ok'); } }));
  row.appendChild(toggle);
  return row;
}

function emptyState(iconName, title, line) {
  const empty = el('div', 'empty');
  empty.appendChild(icon(iconName));
  empty.appendChild(el('div', 'empty-title', title));
  empty.appendChild(el('div', 'empty-line', line));
  return empty;
}

/* ---- render ---- */

/* Which tabs exist right now.
 *
 * Outside a session there used to be no tab strip at all - the screen went straight to "No session running". But a
 * message can arrive while nothing is running, and it does: the icon takes an unread badge and the phone raises a
 * notification, both of which lead to a screen with no way through to what they are about. So the conversation stays
 * reachable whenever there is one, or anyone to unmute. */
function tabsFor(s) {
  const home = { id: 'lobby', label: s.host ? 'Lobby' : 'Session', icon: 'lobby' };
  const chat = { id: 'chat', label: 'Chat', icon: 'chat' };
  if (!s.inLobby) return s.hasChat ? [home, chat] : [home];
  return [home, chat, { id: 'players', label: 'Players', icon: 'players' }];
}

function render() {
  // Ein Tooltip haengt an <body> und ueberlebt damit den Neuaufbau von #body - sein Anker und dessen
  // mouseleave-Handler aber nicht. Ohne das bleibt er stehen, bis die App zugeht.
  for (const stale of document.querySelectorAll('.tip')) stale.remove();

  const s = state();
  const body = $('body');
  const tabsEl = $('tabs');
  body.replaceChildren();
  tabsEl.replaceChildren();

  $('sub').textContent = !s.inLobby ? 'Not in a session'
    : s.host ? (s.public ? 'Hosting, public' : 'Hosting, friends only')
    : 'In ' + (s.hostName ? s.hostName + "'s" : 'someone else\'s') + ' session';

  const seatsEl = $('seats');
  seatsEl.replaceChildren();
  if (s.inLobby) {
    seatsEl.className = s.members >= s.max ? 'seats full' : 'seats';
    seatsEl.appendChild(icon('seats'));
    seatsEl.appendChild(el('div', null, s.members + ' / ' + shownMax(s)));
  } else {
    seatsEl.className = 'seats';
    seatsEl.appendChild(el('div', null, '-'));
  }

  const tabs = tabsFor(s);
  // The session ending can take the tab the player was standing on with it.
  if (!tabs.some((t) => t.id === tab)) tab = tabs[0].id;

  // A strip with one tab is a strip that does nothing - the screen below it is already the only thing there is.
  if (tabs.length > 1) {
    for (const t of tabs) {
      const b = el('button', t.id === tab ? 'tab on' : 'tab');
      b.appendChild(icon(t.icon));
      b.appendChild(el('div', null, t.label));
      if (t.id === 'chat' && s.unread > 0) b.appendChild(el('div', 'tab-badge', s.unread));
      b.addEventListener('click', () => { if (tab !== t.id) { tab = t.id; s1.storage.set('tab', tab); render(); } });
      tabsEl.appendChild(b);
    }
  }

  if (tab === 'chat') renderChat(body);
  else if (!s.inLobby) {
    body.appendChild(emptyState('lobby', 'No session running',
      'Host or join a game from the Side Hustle menu. Host one and this is where you set seats and who can find you.'));
  }
  else if (tab === 'players') renderPlayers(body);
  else if (s.host) renderLobby(body, s);
  else renderSession(body, s);
}

/* Right-click and Escape both mean back. On a side tab, step home; standing on the home tab, do NOT take the
 * event, and Sideload closes the app - which is what a player pressing Escape twice expects. */
document.addEventListener('back', (e) => {
  if (tab === 'lobby') return;
  e.preventDefault();
  tab = 'lobby';
  s1.storage.set('tab', tab);
  render();
});

document.addEventListener('orientationchange', render);

// Somebody joined, left, or the session ended: re-read rather than guess. Any toast on screen stays - it is
// still the answer to what the player just did, and a state change from somebody else does not make it wrong.
s1.on('lobby.changed', render);

render();
