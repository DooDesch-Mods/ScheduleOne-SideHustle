namespace SideHustle.Sync
{
    internal enum SyncProgressKind
    {
        /// <summary>The resolver began fetching this entry.</summary>
        Started,
        /// <summary>Bytes received so far (<c>Done</c> of <c>Total</c>; Total is 0 when the size is unknown).</summary>
        Bytes,
        /// <summary>The entry is on disk AND its hash matched the host's - the only event that counts as finished.</summary>
        Ready,
        /// <summary>The entry could not be fetched or did not match; <c>Reason</c> says why.</summary>
        Failed,
    }

    /// <summary>
    /// One explicit step of <see cref="SyncResolver.DownloadMissingAsync"/>. <c>Key</c> is <see cref="DiffEntry.Key"/>,
    /// the same text the plan rows are keyed by, so a consumer matches an event to its row with a plain comparison.
    /// Replaces the old "the previous file is done when the next label arrives" inference, which marked a failed or
    /// skipped download as finished.
    /// </summary>
    internal readonly struct SyncProgressEvent
    {
        public readonly string Key;
        public readonly SyncProgressKind Kind;
        public readonly long Done;
        public readonly long Total;
        public readonly string Reason;

        private SyncProgressEvent(string key, SyncProgressKind kind, long done, long total, string reason)
        {
            Key = key; Kind = kind; Done = done; Total = total; Reason = reason;
        }

        internal static SyncProgressEvent Started(string key) => new SyncProgressEvent(key, SyncProgressKind.Started, 0, 0, null);
        internal static SyncProgressEvent Progress(string key, long done, long total) => new SyncProgressEvent(key, SyncProgressKind.Bytes, done, total, null);
        internal static SyncProgressEvent Ready(string key) => new SyncProgressEvent(key, SyncProgressKind.Ready, 0, 0, null);
        internal static SyncProgressEvent Failed(string key, string reason) => new SyncProgressEvent(key, SyncProgressKind.Failed, 0, 0, reason);
    }
}
