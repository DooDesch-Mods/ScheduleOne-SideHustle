/* The main-menu state column. Read-only: it says what is true, it never changes anything.
 *
 * Everything comes from one call, and the mod pushes `menu.changed` when something it shows has actually moved -
 * a lobby count that just came back, a message that arrived, a profile that was torn down. No polling.
 */

const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
};

function card(cls) {
  const box = el('div', cls ? 'card ' + cls : 'card');
  return box;
}

/** A card with a small caption and one strong line under it. */
function stat(label, value, valueCls) {
  const box = card();
  box.appendChild(el('div', 'label', label));
  box.appendChild(el('div', valueCls ? 'value ' + valueCls : 'value', value));
  return box;
}

function read() {
  const raw = s1.call('menu.state', '');
  if (!raw) return null;
  try { return JSON.parse(raw); } catch (e) { console.error('unreadable menu state:', e.message); return null; }
}

function render() {
  const panel = document.getElementById('panel');
  panel.replaceChildren();

  const s = read();
  if (!s) return;

  // Which mod set the game booted with. Only shown when it is NOT the normal one - a player in their ordinary
  // install does not need to be told they are in their ordinary install.
  if (s.isProfile) {
    const box = card('running');
    box.appendChild(el('div', 'label', 'RUNNING A PROFILE'));
    const row = el('div', 'row');
    row.appendChild(el('div', 'dot live'));
    row.appendChild(el('div', 'value', s.profile || 'a curated mod set'));
    box.appendChild(row);
    box.appendChild(el('div', 'note', 'Your normal mods are untouched. "Restore my mods" puts them back.'));
    panel.appendChild(box);
  }

  // Sessions, and the number that actually matters: how many of them would let someone in. A published lobby whose
  // host never marked it ready looks identical in the browser and does nothing when you pick it.
  if (s.counted) {
    const box = card();
    box.appendChild(el('div', 'label', 'PUBLISHED SESSIONS'));
    const row = el('div', 'row');
    row.appendChild(el('div', s.joinable > 0 ? 'dot live' : 'dot'));
    row.appendChild(el('div', s.joinable > 0 ? 'value count live' : 'value count', String(s.lobbies)));
    row.appendChild(el('div', 'note', s.lobbies === 1 ? 'session listed' : 'sessions listed'));
    box.appendChild(row);
    box.appendChild(el('div', 'note',
      s.lobbies === 0 ? 'Nobody is hosting right now.'
        : s.joinable === 0 ? 'None of them is taking players.'
        : s.joinable + ' of them ' + (s.joinable === 1 ? 'is' : 'are') + ' taking players.'));
    panel.appendChild(box);
  }

  if (s.unread > 0) {
    const box = card('messages');
    box.appendChild(el('div', 'label', 'MESSAGES'));
    const row = el('div', 'row');
    row.appendChild(el('div', 'dot warn'));
    row.appendChild(el('div', 'value count', String(s.unread)));
    row.appendChild(el('div', 'note', s.unread === 1 ? 'unread' : 'unread'));
    box.appendChild(row);
    box.appendChild(el('div', 'note', 'In the Lobby app on your phone once you are in a session.'));
    panel.appendChild(box);
  }

  if (s.lastError) {
    const box = card('alert');
    box.appendChild(el('div', 'label', 'LAST SESSION'));
    box.appendChild(el('div', 'value', s.lastError));
    panel.appendChild(box);
  }

  panel.appendChild(el('div', 'foot', 'Side Hustle ' + (s.version || '')));
}

s1.on('menu.changed', render);
render();
