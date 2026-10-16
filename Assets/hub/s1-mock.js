/* A stand-in for the mod behind the hub surface, so the page can be checked in a browser.
 *
 * Same call names and wire formats as Menu/HubSurface.cs. Scenario buttons in preview.html switch between the
 * states a player only reaches by playing: a launch in flight, Steam not answering, no lobbies, a fresh install.
 */
(function () {
  const listeners = {};
  const emit = (name, payload) => (listeners[name] || []).forEach(fn => fn({ type: name, value: payload }));

  const modes = [
    { id: 'prophunt', name: 'PropHunt', icon: 'icon/prophunt', sub: 'Multiplayer', desc: '', kind: 'mode', live: 4,
      actions: [{ id: 'host', label: 'Host', go: true }, { id: 'join', label: 'Join' }] },
    { id: 'inkubator', name: 'Inkubator', icon: 'icon/inkubator', sub: 'Solo', desc: 'Tattoo editor', kind: 'mode', live: 0,
      actions: [{ id: 'play', label: 'Play', go: true }] },
    { id: 'rvrepairvan', name: 'RV Repair Van', icon: 'icon/rvrepairvan', sub: 'Not installed', desc: '', kind: 'ghost', live: 1,
      actions: [{ id: 'join', label: 'Join, installs the mod' }] },
    { id: 'debugstub', name: 'Debug stub', icon: 'icon/sidehustle', sub: 'Solo and multiplayer', desc: 'No lobbies', kind: 'mode', live: 0,
      actions: [{ id: 'host', label: 'Host', go: true }, { id: 'play', label: 'Solo' }] },
  ];

  const lobbies = [
    { id: '2001', mode: 'vanilla', modeName: 'Vanilla co-op', icon: 'icon/vanilla', host: 'Dex', name: 'chill grind, day 80', members: 2, max: 4, locked: false, askPassword: false, state: 'ready', stateText: 'Same mods', before: 'Joins straight away.', canJoin: true },
    { id: '2002', mode: 'vanilla', modeName: 'Vanilla co-op', icon: 'icon/vanilla', host: 'Rosa', name: 'Rosa', members: 1, max: 4, locked: false, askPassword: false, state: 'install', stateText: '3 mods to sync', before: 'Downloads 3 mods into a separate profile, then restarts.', canJoin: true },
    { id: '1001', mode: 'prophunt', modeName: 'PropHunt', icon: 'icon/prophunt', host: 'Kev', name: "Kev's lobby", members: 7, max: 8, locked: false, askPassword: false, state: 'ready', stateText: 'Ready', before: 'Joins straight away.', canJoin: true },
    { id: '1002', mode: 'rvrepairvan', modeName: 'RV Repair Van', icon: 'icon/rvrepairvan', host: 'Mara', name: 'Mara', members: 2, max: 4, locked: false, askPassword: false, state: 'install', stateText: 'Not installed', before: 'Installs the gamemode in a separate profile, then restarts.', canJoin: true },
    { id: '1003', mode: 'vanilla', modeName: 'Vanilla co-op', icon: 'icon/vanilla', host: 'tagteam', name: 'day 41 grind', members: 3, max: 6, locked: true, askPassword: false, state: 'ready', stateText: 'Mods sync on join', before: 'Compares your mods with the host before joining.', canJoin: true },
    { id: '1004', mode: 'prophunt', modeName: 'PropHunt', icon: 'icon/prophunt', host: 'Lena', name: 'Lena', members: 2, max: 8, locked: true, askPassword: true, state: 'version', stateText: 'Other version', before: 'The host runs another PropHunt build. Joining may fail.', canJoin: true },
    { id: '1005', mode: 'prophunt', modeName: 'PropHunt', icon: 'icon/prophunt', host: 'Big Lobby', name: 'Big Lobby', members: 14, max: 16, locked: false, askPassword: false, state: 'ready', stateText: 'Ready', before: 'Joins straight away.', canJoin: true },
  ];

  const model = {
    version: '3.0.0', busy: false, steam: 'ok', live: 12,
    vanilla: { icon: 'icon/vanilla', detail: 'Save 2, day 41, $12,400', hostLabel: 'Host save 2', canHost: true },
    cont: { id: 'prophunt', name: 'PropHunt', icon: 'icon/prophunt', summary: '8 players, public, required mods only', label: 'Host again', action: 'hostAgain', settings: true },
    modes, lobbies, status: null, prefs: { hideFull: false, hideLocked: false }, open: { seq: 0, tab: 'play' },
  };

  const hostStatus = () => ({
    title: 'HOSTING PROPHUNT',
    stations: [
      { label: 'Mods checked', state: 'done', note: 'Required only' },
      { label: 'Restart', state: 'now', note: 'in 7s' },
      { label: 'Creating lobby', state: 'todo' },
      { label: 'Loading world', state: 'todo' },
    ],
    progress: 30, error: '',
    actions: [{ id: 'restartNow', label: 'Restart now', go: true }, { id: 'cancel', label: 'Cancel' }],
  });

  const joinStatus = () => ({
    title: 'JOINING PROPHUNT',
    stations: [
      { label: 'Lobby found', state: 'done', note: 'Kev' },
      { label: 'Host mods read', state: 'done', note: '9 mods' },
      { label: 'Entering lobby', state: 'now', note: '' },
      { label: 'Loading world', state: 'todo' },
    ],
    progress: -1, error: '',
    actions: [{ id: 'cancel', label: 'Cancel' }],
  });

  const changed = () => emit('hub.changed', '');

  function call(name, arg) {
    const a = arg && arg[0] === '{' ? JSON.parse(arg) : arg;
    switch (name) {
      case 'hub.state': return JSON.stringify(model);
      case 'hub.hostForm':
        if (a === 'inkubator') return JSON.stringify({ error: 'This gamemode cannot be hosted.' });
        return JSON.stringify({ id: a, name: modes.find(m => m.id === a)?.name || a, icon: 'icon/' + (a === 'debugstub' ? 'sidehustle' : a),
          author: 'DooDesch', players: 8, min: 2, max: 16, visibility: 0, lobbyName: '', defaultName: "Kev's PropHunt",
          policy: a === 'prophunt', policyMode: 1, settings: true, solo: a === 'debugstub' });
      case 'hub.host':
        if (!a.players) return 'Pick a player count.';
        model.busy = true; model.status = hostStatus(); changed(); return '';
      case 'hub.hostAgain': model.busy = true; model.status = hostStatus(); changed(); return '';
      case 'hub.join': {
        const l = lobbies.find(x => x.id === a.id);
        if (l && l.askPassword && a.password !== 'pw') return 'Incorrect password.';
        model.busy = true; model.status = joinStatus(); changed(); return '';
      }
      case 'hub.status':
        if (a === 'cancel') { model.busy = false; model.status = null; changed(); }
        return '';
      case 'hub.pref': Object.assign(model.prefs, a); changed(); return '';
      case 'hub.refresh': model.steam = 'loading'; changed(); setTimeout(() => { model.steam = 'ok'; changed(); }, 800); return '';
      case 'hub.play': case 'hub.vanillaHost': case 'hub.load':  case 'hub.legacy':
        console.log('[mock] ' + name + ' ' + (arg || '')); return '';
      default: console.warn('[mock] no handler for ' + name); return '';
    }
  }

  window.s1 = { call, on: (name, fn) => { (listeners[name] = listeners[name] || []).push(fn); } };

  const pics = { 'icon/prophunt': 'PropHunt', 'icon/inkubator': 'Inkubator', 'icon/vanilla': 'van', 'icon/rvrepairvan': 'RVRepairVan', 'icon/sidehustle': 'SideHustle' };
  window.S1_PREVIEW = {
    img: n => 'preview/' + (pics[n] || 'SideHustle') + '.jpg',
    scenario(which) {
      model.busy = false; model.status = null; model.steam = 'ok'; model.lobbies = lobbies; model.live = 12;
      model.cont = model.cont || null;
      if (which === 'hosting') { model.busy = true; model.status = hostStatus(); }
      if (which === 'joining') { model.busy = true; model.status = joinStatus(); }
      if (which === 'failed') { model.status = Object.assign(joinStatus(), { stations: joinStatus().stations.map((s, i) => i === 2 ? Object.assign({}, s, { state: 'fail' }) : s), error: 'Could not enter the lobby within 20s.', actions: [{ id: 'cancel', label: 'Close' }] }); }
      if (which === 'steam') { model.steam = 'error'; model.lobbies = []; model.live = 0; }
      if (which === 'empty') { model.lobbies = []; model.live = 0; }
      if (which === 'vanillacont') { model.cont = { id: 'vanilla', name: 'Vanilla co-op', icon: 'icon/vanilla', summary: 'Save 2, 4 players, public', label: 'Host again', action: 'hostAgain', settings: false }; }
      else if (which !== 'return') model.cont = { id: 'prophunt', name: 'PropHunt', icon: 'icon/prophunt', summary: '8 players, public, required mods only', label: 'Host again', action: 'hostAgain', settings: true };
      if (which === 'seats') { lobbies[0].members = lobbies[0].members === 7 ? 8 : 7; }
      if (which === 'return') { model.open = { seq: (model.open.seq || 0) + 1, tab: 'play', mode: 'prophunt', form: true }; }
      changed();
    },
    back: () => emit('hub.back', ''),
  };
})();
