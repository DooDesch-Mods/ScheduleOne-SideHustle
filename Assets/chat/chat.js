/* The ask-the-host column beside a join screen.
 *
 * One call for the whole state, and the mod pushes `chat.changed` when the thread actually moved. The panel is
 * open for a minute at a time while somebody works out whether they can get into a session, so it is deliberately
 * thin: a name, the thread, a field.
 */

const el = (tag, cls, text) => {
  const node = document.createElement(tag);
  if (cls) node.className = cls;
  if (text !== undefined && text !== null) node.textContent = String(text);
  return node;
};

const $ = (id) => document.getElementById(id);

/* What the log was last pinned to the bottom for. Keyed on the last message's TEXT, not the count: a thread is
   capped at 20, so a count guard would stop pinning from message 21 on. */
let pinned = '';

function read() {
  const raw = s1.call('chat.state', '');
  if (!raw) return null;
  try { return JSON.parse(raw); } catch (e) { console.error('unreadable chat state:', e.message); return null; }
}

function render() {
  const s = read();
  if (!s) return;

  $('who').textContent = s.host || 'the host';

  const log = $('log');
  log.replaceChildren();

  const msgs = s.messages || [];
  if (msgs.length === 0) {
    // The empty state says what is worth saying, not what the screen behind it happens to be about: the host
    // can also just let you in, or answer a question that is not about a mod at all.
    const box = el('div', 'empty');
    box.appendChild(el('div', 'empty-note',
      'They are the only one who can send you a mod they built themselves, or let you in without it.'));
    log.appendChild(box);
  } else {
    for (const m of msgs) {
      const b = el('div', m.mine ? 'bubble mine' : 'bubble');
      b.appendChild(el('div', 'bubble-text', m.text));
      log.appendChild(b);
    }
  }
  // Only pin when the thread actually grew. Otherwise a re-render yanks a player who scrolled up to re-read
  // which mod the host named.
  const mark = msgs.length + '|' + (msgs.length ? msgs[msgs.length - 1].text : '');
  if (mark !== pinned) { pinned = mark; log.scrollToEnd(); }
}

function send() {
  const field = $('reply');
  const text = field.value.trim();
  if (!text) return;

  const answer = s1.call('chat.send', text);
  if (answer !== 'ok') {
    // Only ever clear the field once the mod has actually taken the line - and say so on screen, or the player
    // presses the orange arrow, sees nothing move, and presses it again. No cause named: a reliable send returns
    // true even for a peer who quit, and an empty answer from a missing handler lands in the same branch.
    console.error('message refused:', answer);
    const log = $('log');
    const old = log.querySelector('.said');
    if (old) old.remove();
    log.appendChild(el('div', 'said', 'Not sent. Wait a moment and send again.'));
    log.scrollToEnd();
    return;
  }
  field.value = '';
  render();
  // Back into the box for the next line. Deliberately here and not at mount: the column has no data-typing,
  // because parking the caret there on open raises GameInput.IsTyping and kills Escape for the whole screen.
  // Asking for focus after an explicit send is the version that costs nothing.
  field.focus();
}

$('close').addEventListener('click', () => s1.call('chat.close', ''));
$('send').addEventListener('click', send);
$('reply').addEventListener('keydown', (e) => { if (e.key === 'Enter') send(); });

s1.on('chat.changed', render);

render();
