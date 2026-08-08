/* A stand-in for the mod behind the Lobby app, so the page can be checked in a browser.
 *
 * It answers the same call names with the same wire formats the C# side does, including the two answers that are
 * easy to get wrong: `lobby.setMax` replies with the seat count Steam ACCEPTED (not "ok"), and `lobby.togglePublish`
 * replies "1"/"0". A mock that answered "ok" to those would let a page ship that never handles a refusal.
 *
 * Everything around it - the stage, the fenced DOM, the storage, the back and orientation events - is shared and
 * lives in sideload-preview.js. Only the parts that are the Lobby are here.
 */

function base() {
  return {
    host: true,
    name: "Dennis' place",
    hasPassword: false,
    password: '',
    public: true,
    members: 2,
    max: 4,
    ceiling: 32,
    enforce: false,
    canPublish: true,
    published: false,
    inLobby: true,
    hostName: 'DooDesch',
    runtime: 'il2cpp',
  };
}

let model = base();

// Steam refuses to go past this in the mock, so the page's "Steam kept it at N" path is reachable.
const STEAM_HARD_LIMIT = 32;

// The bridge, handed over by the shell before the page runs. It is how this file pushes at the page instead of only
// answering it - which is what the mod does when somebody joins, somebody leaves or the session ends.
let host = null;
export const ready = (s1) => { host = s1; };

// The same payload the mod sends with the push. The page re-renders on the event itself and ignores it.
const changed = () => host?.emit('lobby.changed', 'mock');

export function call(name, argument = '') {
  switch (name) {
    case 'lobby.state':
      return JSON.stringify(model);

    case 'lobby.players': {
      if (!model.inLobby) return '[]';
      // Mirrors what the C# side emits, including its order: host first, then friends, then by name. The
      // host and "you" must never land on the same row - that collision is exactly what a mock is for.
      const others = ['DooDesch', 'xAkitoh', 'godofn00bs', 'DonyThePony', 'Shlongulusrex_69'];
      const hostName = model.host ? 'DooDesch' : (model.hostName || 'fadestyle');
      const out = [{ name: hostName, host: true, self: !!model.host, friend: !model.host }];
      let i = 0;
      while (out.length < Math.min(model.members, 6)) {
        const n = others[i++ % others.length];
        if (n === hostName) continue;
        out.push({ name: n, host: false, self: !model.host && out.length === 1, friend: out.length === 2 });
      }
      return JSON.stringify(out);
    }

    case 'lobby.setName': {
      const n = (argument || '').trim().slice(0, 48);
      model.name = n || 'DooDesch';
      return 'ok';
    }

    case 'lobby.setPassword': {
      const pw = (argument || '').trim();
      model.hasPassword = pw.length > 0;
      model.password = pw;
      return 'ok';
    }

    case 'lobby.setVisibility':
      model.public = argument === 'pub';
      return 'ok';

    case 'lobby.setMax': {
      const want = parseInt(argument, 10);
      if (isNaN(want)) return 'error';
      model.max = Math.max(2, Math.min(Math.min(model.ceiling, STEAM_HARD_LIMIT), want));
      return String(model.max);
    }

    case 'lobby.setEnforce':
      model.enforce = argument === '1';
      return 'ok';

    case 'lobby.togglePublish':
      if (!model.canPublish) return 'error';
      model.published = !model.published;
      return model.published ? '1' : '0';

    default:
      console.warn(`[preview] no stand-in for s1.call("${name}")`);
      return '';
  }
}

// Each state is a WHOLE model rather than a patch on the one before it, so the chips can be clicked in any order and
// still show what their label says.
const enter = (next) => { model = next; changed(); };

// The states a host only reaches by playing - a full session, a client's view, a lobby that ended. Without a handle
// on them those are the parts of the app that can only be looked at in the game, which is the trip the preview
// exists to avoid.
export const scenarios = {
  'hosting, public': () => enter(base()),

  'friends only, password set': () => enter({
    ...base(), public: false, hasPassword: true, password: 'kettle', members: 3,
  }),

  'password set, restarted since': () => enter({
    // The case the page has to render honestly: the lobby carries a hash, so the mod knows a password EXISTS and
    // cannot say what it is.
    ...base(), hasPassword: true, password: '', members: 4, max: 4,
  }),

  'full, 32 seats, published': () => enter({ ...base(), members: 32, max: 32, published: true, enforce: true }),

  'seats below headcount': () => enter({ ...base(), members: 5, max: 3 }),

  'long name, no publishing': () => enter({
    ...base(),
    name: 'Kings of Cul-de-Sac - Sunday co-op, bring your own fertiliser',
    canPublish: false,
    members: 1,
  }),

  'client in a session': () => enter({ ...base(), host: false, hostName: 'fadestyle', members: 3, enforce: true }),

  'client, host on Mono': () => enter({
    ...base(), host: false, hostName: 'Shlongulusrex_69', runtime: 'mono', members: 2,
  }),

  'no session at all': () => enter({ host: false, inLobby: false }),
};
