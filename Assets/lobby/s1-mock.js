/* A stand-in for the mod behind the Lobby app, so the page can be checked in a browser.
 *
 * It answers the same call names with the same wire formats the C# side does, including the two answers that are
 * easy to get wrong: `lobby.setMax` replies with the seat count Steam ACCEPTED (not "ok"), and `lobby.togglePublish`
 * replies "1"/"0". A mock that answered "ok" to those would let a page ship that never handles a refusal.
 */

const scenarios = {};
let current = 'hosting, public';
let listeners = {};

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

scenarios['hosting, public'] = () => base();

scenarios['friends only, password set'] = () => ({
  ...base(), public: false, hasPassword: true, password: 'kettle', members: 3,
});

scenarios['password set, restarted since'] = () => ({
  // The case the page has to render honestly: the lobby carries a hash, so the mod knows a password EXISTS and
  // cannot say what it is.
  ...base(), hasPassword: true, password: '', members: 4, max: 4,
});

scenarios['full, 32 seats, published'] = () => ({
  ...base(), members: 32, max: 32, published: true, enforce: true,
});

scenarios['seats below headcount'] = () => ({ ...base(), members: 5, max: 3 });

scenarios['long name, no publishing'] = () => ({
  ...base(),
  name: 'Kings of Cul-de-Sac - Sunday co-op, bring your own fertiliser',
  canPublish: false,
  members: 1,
});

scenarios['client in a session'] = () => ({ ...base(), host: false, hostName: 'fadestyle', members: 3, enforce: true });

scenarios['client, host on Mono'] = () => ({ ...base(), host: false, hostName: 'Shlongulusrex_69', runtime: 'mono', members: 2 });

scenarios['no session at all'] = () => ({ host: false, inLobby: false });

let model = scenarios[current]();

// Steam refuses to go past this in the mock, so the page's "Steam kept it at N" path is reachable.
const STEAM_HARD_LIMIT = 32;

const s1 = {
  orientation: 'landscape',
  setOrientation(v) { this.orientation = v; },
  storage: {
    _v: {},
    get(k, d) { return k in this._v ? this._v[k] : d; },
    set(k, v) { this._v[k] = v; },
    remove(k) { delete this._v[k]; },
    clear() { this._v = {}; },
  },
  on(name, fn) { (listeners[name] = listeners[name] || []).push(fn); },
  call(name, arg) {
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
        let n = (arg || '').trim().slice(0, 48);
        model.name = n || 'DooDesch';
        return 'ok';
      }

      case 'lobby.setPassword': {
        const pw = (arg || '').trim();
        model.hasPassword = pw.length > 0;
        model.password = pw;
        return 'ok';
      }

      case 'lobby.setVisibility':
        model.public = arg === 'pub';
        return 'ok';

      case 'lobby.setMax': {
        const want = parseInt(arg, 10);
        if (isNaN(want)) return 'error';
        model.max = Math.max(2, Math.min(Math.min(model.ceiling, STEAM_HARD_LIMIT), want));
        return String(model.max);
      }

      case 'lobby.setEnforce':
        model.enforce = arg === '1';
        return 'ok';

      case 'lobby.togglePublish':
        if (!model.canPublish) return 'error';
        model.published = !model.published;
        return model.published ? '1' : '0';

      default:
        console.warn('[mock] no handler for', name);
        return '';
    }
  },
};

const __mock = {
  scenarios: Object.keys(scenarios),
  pick(name) {
    current = name;
    model = scenarios[name]();
    for (const fn of listeners['lobby.changed'] || []) fn('mock');
  },
};
