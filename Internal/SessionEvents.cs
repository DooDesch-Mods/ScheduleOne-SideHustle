using System;
using System.Collections.Generic;

namespace SideHustle.Internal
{
    /// <summary>
    /// A subscriber list for one public API event. Handlers are run from a snapshot, each in its own try/catch:
    /// one mod's broken handler is logged with its stack trace and never stops the next mod's handler, and a
    /// handler that subscribes or unsubscribes while the event runs cannot break the loop.
    /// </summary>
    internal sealed class Subscribers<T> where T : Delegate
    {
        private readonly string _name;
        private readonly List<T> _list = new List<T>();
        private readonly object _lock = new object();

        internal Subscribers(string name) { _name = name; }

        internal void Add(T handler)
        {
            if (handler == null) { Core.Log?.Warning($"[api] a null {_name} handler was ignored."); return; }
            lock (_lock) _list.Add(handler);
        }

        internal bool Remove(T handler)
        {
            if (handler == null) return false;
            lock (_lock) return _list.Remove(handler);
        }

        internal int Count { get { lock (_lock) return _list.Count; } }

        internal void Raise(Action<T> invoke)
        {
            T[] snapshot;
            lock (_lock) snapshot = _list.ToArray();
            foreach (var h in snapshot)
            {
                try { invoke(h); }
                catch (Exception e)
                {
                    Core.Log?.Error($"[api] a {_name} handler from {OwnerOf(h)} threw: {e}");
                }
            }
        }

        internal static string OwnerOf(Delegate d)
        {
            try { return d?.Method?.DeclaringType?.Assembly?.GetName()?.Name ?? "an unknown mod"; }
            catch (Exception e) { return "an unknown mod (" + e.GetType().Name + ")"; }
        }
    }

    /// <summary>
    /// The one place a gamemode session starts and ends. Every launch path (hub singleplayer, World singleplayer,
    /// host, join) reports here after its launch callback, and every exit path (ReturnToHub, mod-policy restart,
    /// aborted session) ends here. That is what makes OnExitToHub run exactly once per launch context and
    /// SessionStarted/SessionEnded run as a pair.
    /// </summary>
    internal static class SessionEvents
    {
        internal static readonly Subscribers<Action> Ready = new Subscribers<Action>("Ready");
        internal static readonly Subscribers<Action<LaunchContext>> Started = new Subscribers<Action<LaunchContext>>("SessionStarted");
        internal static readonly Subscribers<Action<string>> Ended = new Subscribers<Action<string>>("SessionEnded");
        internal static readonly Subscribers<Action<SessionPlayer>> Joined = new Subscribers<Action<SessionPlayer>>("PlayerJoined");
        internal static readonly Subscribers<Action<SessionPlayer>> Left = new Subscribers<Action<SessionPlayer>>("PlayerLeft");

        /// <summary>The session that raised SessionStarted and has not ended yet, or null.</summary>
        internal static LaunchContext Current { get; private set; }

        internal static void RaiseReady()
        {
            Core.Log?.Msg($"[api] ready (API version {API.ApiVersion}, {Ready.Count} Ready handler(s)).");
            Ready.Raise(h => h());
        }

        /// <summary>Called right after a launch callback returned. Skips (and logs) a context the callback already
        /// ended, so a gamemode that returns to the hub from inside its launch callback never sees a start after
        /// its end.</summary>
        internal static void RaiseStarted(LaunchContext ctx)
        {
            if (ctx == null)
            {
                Core.Log?.Msg("[api] SessionStarted skipped: the session ended inside its launch callback.");
                return;
            }
            string id = ctx.Descriptor?.Id ?? "?";
            if (ctx.ExitRaised)
            {
                Core.Log?.Msg($"[api] SessionStarted skipped for '{id}': it ended inside its launch callback.");
                return;
            }
            if (ctx.StartRaised)
            {
                Core.Log?.Warning($"[api] SessionStarted for '{id}' was requested twice; the second request was ignored.");
                return;
            }
            if (Current != null && !ReferenceEquals(Current, ctx))
            {
                // A new launch while an old session never reported its end. End the old one first so handlers see a
                // clean SessionEnded/SessionStarted pair instead of two overlapping sessions.
                Core.Log?.Warning($"[api] '{Current.Descriptor?.Id}' was still marked running when '{id}' started; ending it first.");
                EndCurrent();
            }

            ctx.StartRaised = true;
            Current = ctx;
            PlayerTracker.Begin(ctx);
            Core.Log?.Msg($"[api] session started: '{id}' as {ctx.Role}, {PlayerTracker.Count} player(s), {Started.Count} handler(s).");
            Started.Raise(h => h(ctx));
        }

        /// <summary>
        /// End a launch context: run the gamemode's OnExitToHub once, then - if this context had started - raise
        /// SessionEnded. A second call for the same context is ignored and logged.
        /// </summary>
        internal static void Exit(LaunchContext ctx)
        {
            if (ctx == null)
            {
                Core.Log?.Warning("[api] an exit was requested without a session context; OnExitToHub not called.");
                return;
            }
            string id = ctx.Descriptor?.Id ?? "?";
            if (ctx.ExitRaised)
            {
                Core.Log?.Msg($"[api] OnExitToHub for '{id}' already ran for this session; skipped.");
                return;
            }
            ctx.ExitRaised = true;

            var cb = ctx.Descriptor?.OnExitToHub;
            if (cb != null)
            {
                try { cb(ctx); }
                catch (Exception e) { Core.Log?.Error($"[api] OnExitToHub of '{id}' threw: {e}"); }
            }

            if (ReferenceEquals(Current, ctx)) EndCurrent();
        }

        /// <summary>
        /// True when ReturnToHub must do nothing because this context already ended (Side Hustle aborted it, or an
        /// earlier ReturnToHub finished). Logs the skip.
        /// </summary>
        internal static bool SkipReturn(LaunchContext ctx)
        {
            if (ctx == null || !ctx.ExitRaised) return false;
            Core.Log?.Msg($"[api] ReturnToHub for '{ctx.Descriptor?.Id ?? "?"}' ignored: that session already ended.");
            return true;
        }

        private static void EndCurrent()
        {
            var ended = Current;
            Current = null;
            PlayerTracker.End();
            string id = ended?.Descriptor?.Id ?? "";
            Core.Log?.Msg($"[api] session ended: '{id}', {Ended.Count} handler(s).");
            Ended.Raise(h => h(id));
        }
    }
}
