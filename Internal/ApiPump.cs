using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SideHustle.Internal
{
    /// <summary>
    /// The per-frame driver of the public API's moving parts: work posted from other threads, the deferred mod
    /// policy checks, the session roster and the gamemode message channel. Pumped from Core.OnUpdate.
    /// </summary>
    internal static class ApiPump
    {
        private static int _mainThreadId = -1;
        private static bool _firstTick = true;
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        /// <summary>Remember the Unity main thread. Called from Core's init (via API.IsReady) and again on the first
        /// frame, both of which run on it.</summary>
        internal static void CaptureMainThread() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>True on the Unity main thread. Before the thread is known every caller counts as main: the only
        /// code running that early is MelonLoader's init, which is on it.</summary>
        internal static bool OnMainThread => _mainThreadId < 0 || Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>Run <paramref name="action"/> on the main thread at the next frame.</summary>
        internal static void Post(Action action)
        {
            if (action == null) return;
            _queue.Enqueue(action);
        }

        internal static void Tick()
        {
            if (_firstTick)
            {
                _firstTick = false;
                CaptureMainThread();
                // Every mod's OnInitializeMelon has run by the first frame, so the installed mod set is final.
                DescriptorValidator.OnModsSettled();
            }

            // Only what was queued before this frame started: an action that posts again runs next frame.
            int pending = _queue.Count;
            while (pending-- > 0 && _queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Core.Log?.Error($"[api] a queued call from another thread failed: {e}"); }
            }

            PlayerTracker.Tick();
            GamemodeChannel.Tick();
        }
    }
}
