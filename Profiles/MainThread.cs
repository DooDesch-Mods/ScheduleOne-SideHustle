using System;
using System.Collections.Concurrent;

namespace SideHustle.Profiles
{
    /// <summary>
    /// Marshals worker-thread results onto the Unity main thread. All HTTP/zip/hash work in this module runs on
    /// Task.Run (pure BCL is thread-safe under Il2CppInterop), but ANY Unity or UI access must happen on the
    /// main thread - workers Post() their completions here and Core.OnUpdate pumps the queue every frame.
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        internal static void Post(Action action)
        {
            if (action != null) Queue.Enqueue(action);
        }

        internal static void Tick()
        {
            // Bounded per frame so a burst of completions cannot hitch a frame.
            int guard = 0;
            while (guard++ < 64 && Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Core.Log?.Warning("[profiles] main-thread action threw: " + e.Message); }
            }
        }
    }
}
