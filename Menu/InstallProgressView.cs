using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Profiles;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>One member of an install plan: a package the dependency closure says must be in the cache.</summary>
    internal sealed class InstallPlanRow
    {
        public string FullName;
        public string Version;
        public long Size;       // zip bytes from the index (0 = unknown)
        public bool Cached;     // already extracted in the package cache
        public string Key;      // "{FullName} {Version}" - exactly what the engine reports as CurrentItem
        /// <summary>Pre-download heuristic: the package NAME carries a "mono" token (e.g. "CashDrops_MONO").
        /// The authoritative check happens after extraction from the real DLL metadata.</summary>
        public bool NameLooksMono;
    }

    /// <summary>
    /// The install screen: the resolved dependency closure as live status rows (queued / cached / downloading with
    /// MB / done / failed), a byte-weighted overall progress bar, and a Cancel button. Built once; the returned
    /// <see cref="Controller"/> updates everything in place from engine progress reports (already marshalled to the
    /// main thread by the caller). Terminal states swap the footer to Retry/Back on failure or flash success.
    /// </summary>
    internal static class InstallProgressView
    {
        internal sealed class Controller
        {
            internal Text Status;
            internal Image BarFill;
            internal Transform Footer;
            internal Button CancelButton;
            internal Text CancelLabel;

            internal readonly Dictionary<string, Text> Rows = new Dictionary<string, Text>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, InstallPlanRow> Plan = new Dictionary<string, InstallPlanRow>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal string ActiveKey;
            internal long ActiveBytes;
            internal long TotalBytes;
            internal bool Terminal;

            // A size the bar can count for members whose zip size the index does not know.
            private const long FallbackSize = 250_000;

            internal void Report(ProfileProgress pp)
            {
                if (Terminal || pp == null) return;
                try
                {
                    if (pp.Phase == "Resolving") { SetStatus("Resolving dependencies..."); return; }
                    if (pp.Phase != "Downloading") return;

                    string key = pp.CurrentItem ?? "";
                    if (!string.Equals(key, ActiveKey, StringComparison.OrdinalIgnoreCase))
                    {
                        // A report for a new member means the previous one finished (download or cache hit).
                        if (ActiveKey != null) MarkDone(ActiveKey);
                        ActiveKey = key;
                        ActiveBytes = 0;
                    }

                    if (Plan.TryGetValue(key, out var row))
                    {
                        if (pp.BytesTotal > 0)
                        {
                            ActiveBytes = Math.Min(pp.BytesDone, pp.BytesTotal);
                            SetRow(key, $">  {key}  -  {Mb(pp.BytesDone)} / {Mb(pp.BytesTotal)}", Theme.AccentBorder);
                        }
                        else if (!Done.Contains(key))
                        {
                            SetRow(key, $">  {key}  -  {(row.Cached ? "from cache" : "starting...")}", Theme.AccentBorder);
                        }
                    }

                    SetStatus($"Downloading {Math.Min(pp.Done + 1, Math.Max(1, pp.Total))} of {Math.Max(1, pp.Total)}...");
                    UpdateBar();
                }
                catch (Exception e) { Fault("reporting progress", e); }
            }

            internal void SetApplying()
            {
                if (Terminal) return;
                if (ActiveKey != null) MarkDone(ActiveKey);
                SetStatus("Applying to the profile...");
                // Past the download phase there is nothing left to cancel (the token is only observed while
                // downloading), so retire the button rather than let it show a misleading "Cancelling...".
                try { if (CancelButton != null) CancelButton.interactable = false; } catch (Exception e) { Fault("retiring the cancel button", e); }
                try { Components.SetProgressBar(BarFill, 1f); } catch (Exception e) { Fault("filling the bar", e); }
            }

            internal void ShowSuccess(string message)
            {
                Terminal = true;
                try
                {
                    foreach (var key in Plan.Keys.ToList()) MarkDone(key);
                    SetStatus(message);
                    if (BarFill != null) { BarFill.color = Theme.Success; Components.SetProgressBar(BarFill, 1f); }
                    if (CancelButton != null) CancelButton.interactable = false;
                }
                catch (Exception e) { Fault("showing success", e); }
            }

            internal void ShowError(string message, Action onRetry, Action onBack)
            {
                Terminal = true;
                try
                {
                    if (ActiveKey != null && !Done.Contains(ActiveKey))
                        SetRow(ActiveKey, $"-  {ActiveKey}  -  failed", Theme.DangerText);
                    SetStatus(message);
                    if (BarFill != null) BarFill.color = Theme.Danger;

                    if (Footer == null) return;
                    if (CancelButton != null) UnityEngine.Object.Destroy(CancelButton.gameObject);

                    Scaffold.FooterButton(Footer, "Back", Theme.Button, left: true, onClick: onBack);
                    Scaffold.FooterButton(Footer, "Try again", Theme.Accent, left: false, onClick: onRetry);

                    Interactions.PolishButtons(Footer);
                }
                catch (Exception e) { Fault("showing the error", e); }
            }

            internal void SetCancelling()
            {
                try
                {
                    if (CancelButton != null) CancelButton.interactable = false;
                    if (CancelLabel != null) CancelLabel.text = "Cancelling...";
                    SetStatus("Cancelling...");
                }
                catch (Exception e) { Fault("showing the cancel state", e); }
            }

            /// <summary>Terminal WARNING state: the install itself worked, but with a runtime problem the player
            /// must see (e.g. the whole package is a Mono build). Like ShowError, but without Retry - retrying
            /// would not change anything.</summary>
            internal void ShowWarning(string message, Action onBack)
            {
                Terminal = true;
                try
                {
                    SetStatus(message);
                    if (BarFill != null) { BarFill.color = Theme.Warning; Components.SetProgressBar(BarFill, 1f); }

                    if (Footer == null) return;
                    if (CancelButton != null) UnityEngine.Object.Destroy(CancelButton.gameObject);

                    Scaffold.FooterButton(Footer, "Back", Theme.Button, left: true, onClick: onBack);

                    Interactions.PolishButtons(Footer);
                }
                catch (Exception e) { Fault("showing the warning", e); }
            }

            /// <summary>Post-terminal per-row runtime note ("Mono variant skipped" / "Mono build - will not
            /// load") - applied after ShowSuccess/ShowWarning marked the rows, so it deliberately ignores
            /// <see cref="Terminal"/>.</summary>
            internal void SetRuntimeNote(string key, string note, bool warn)
            {
                try
                {
                    SetRow(key, $"{(warn ? "!" : "+")}  {key}  -  {note}", warn ? Theme.WarningText : Theme.TextMuted);
                }
                catch (Exception e) { Fault("setting a row note", e); }
            }

            // Sync downloads report explicit steps keyed by DiffEntry.Key (see SyncProgressEvent) instead of the
            // profile engine's "a new member means the previous one finished" inference above.
            internal void SyncStarted(string key)
            {
                if (Terminal) return;
                ActiveKey = key; ActiveBytes = 0;
                if (Plan.ContainsKey(key) && !Done.Contains(key)) SetRow(key, $">  {key}  -  starting...", Theme.AccentBorder);
            }

            internal void SyncBytes(string key, long done, long total)
            {
                if (Terminal || Done.Contains(key)) return;
                ActiveKey = key;
                ActiveBytes = total > 0 ? Math.Min(done, total) : done;
                if (Plan.ContainsKey(key))
                    SetRow(key, total > 0 ? $">  {key}  -  {Mb(done)} / {Mb(total)}" : $">  {key}  -  {Mb(done)}", Theme.AccentBorder);
                UpdateBar();
            }

            internal void SyncReady(string key)
            {
                if (Terminal) return;
                MarkDone(key);
            }

            internal void SyncFailed(string key, string reason)
            {
                if (Terminal) return;
                SetRow(key, string.IsNullOrEmpty(reason) ? $"-  {key}  -  failed" : $"-  {key}  -  failed: {reason}", Theme.DangerText);
                if (string.Equals(key, ActiveKey, StringComparison.OrdinalIgnoreCase)) { ActiveKey = null; ActiveBytes = 0; }
            }

            private void MarkDone(string key)
            {
                if (!Done.Add(key)) return;
                if (Plan.TryGetValue(key, out var row))
                    SetRow(key, $"+  {key}  -  {(row.Cached ? "already cached" : "done")}", Theme.SuccessText);
                if (string.Equals(key, ActiveKey, StringComparison.OrdinalIgnoreCase)) { ActiveKey = null; ActiveBytes = 0; }
                UpdateBar();
            }

            private void UpdateBar()
            {
                try
                {
                    if (BarFill == null || TotalBytes <= 0) return;
                    long done = 0;
                    foreach (var key in Done)
                        if (Plan.TryGetValue(key, out var r)) done += SizeOf(r);
                    if (ActiveKey != null && Plan.TryGetValue(ActiveKey, out var active))
                        done += Math.Min(ActiveBytes, SizeOf(active));
                    Components.SetProgressBar(BarFill, (float)done / TotalBytes);
                }
                catch (Exception e) { Fault("updating the bar", e); }
            }

            // The view can be torn down mid-report; that is expected once, and a broken painter would otherwise log
            // on every progress tick.
            private bool _faulted;
            private void Fault(string what, Exception e)
            {
                if (_faulted) return;
                _faulted = true;
                Core.Log?.Warning($"[install] progress view fault while {what}: {e.Message}");
            }

            private void SetRow(string key, string text, Color color)
            {
                if (Rows.TryGetValue(key, out var t) && t != null) { t.text = text; t.color = color; }
            }

            private void SetStatus(string text)
            {
                if (Status != null) Status.text = text;
            }

            internal static long SizeOf(InstallPlanRow r) => r.Size > 0 ? r.Size : FallbackSize;

            private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0") + " MB";
        }

        internal static Controller Build(Transform formHost, string headline, IReadOnlyList<InstallPlanRow> plan,
            IReadOnlyList<string> unresolved, Action onCancel)
        {
            var c = new Controller();

            var header = UIFactory.Panel("header", formHost, Theme.Clear);
            var hrt = header.GetComponent<RectTransform>();
            hrt.anchorMin = new Vector2(0, 1); hrt.anchorMax = new Vector2(1, 1); hrt.pivot = new Vector2(0.5f, 1);
            hrt.offsetMin = new Vector2(Scaffold.Pad, -96); hrt.offsetMax = new Vector2(-Scaffold.Pad, 0);

            var title = UIFactory.Text("headline", headline, header.transform, Theme.H3, TextAnchor.UpperLeft, FontStyle.Bold);
            title.color = Theme.TextPrimary; title.raycastTarget = false;
            var trt = title.rectTransform; trt.anchorMin = new Vector2(0, 1); trt.anchorMax = new Vector2(1, 1); trt.pivot = new Vector2(0.5f, 1);
            trt.offsetMin = new Vector2(0, -30); trt.offsetMax = new Vector2(0, -4);

            c.Status = UIFactory.Text("status", "Starting...", header.transform, Theme.Body, TextAnchor.UpperLeft);
            c.Status.color = Theme.TextMuted; c.Status.raycastTarget = false;
            var srt = c.Status.rectTransform; srt.anchorMin = new Vector2(0, 1); srt.anchorMax = new Vector2(1, 1); srt.pivot = new Vector2(0.5f, 1);
            srt.offsetMin = new Vector2(0, -58); srt.offsetMax = new Vector2(0, -34);

            var bar = Components.ProgressBar(header.transform, out var fill, 10f);
            c.BarFill = fill;
            var brt2 = bar.GetComponent<RectTransform>();
            brt2.anchorMin = new Vector2(0, 1); brt2.anchorMax = new Vector2(1, 1); brt2.pivot = new Vector2(0.5f, 1);
            brt2.offsetMin = new Vector2(0, -78); brt2.offsetMax = new Vector2(0, -68);

            var page = Scaffold.Create(formHost, topInset: 102f, spacing: 4f);
            c.Footer = page.Footer;
            var content = page.Content;

            // A Cancel that nothing listens to is a dead button. It is only built when the caller has something to stop.
            if (onCancel != null)
            {
                var cancelBtn = Scaffold.FooterButton(page.Footer, "Cancel", Theme.Button, left: true,
                    onClick: () => { c.SetCancelling(); onCancel(); });
                c.CancelButton = cancelBtn; c.CancelLabel = cancelBtn.GetComponentInChildren<Text>();
            }

            if (plan != null)
                foreach (var row in plan)
                {
                    c.Plan[row.Key] = row;
                    c.TotalBytes += Controller.SizeOf(row);
                    string label = row.Cached ? $"=  {row.Key}  -  already cached" : $"·  {row.Key}  -  queued";
                    var color = row.Cached ? Theme.TextMuted : Theme.TextPrimary;
                    if (row.NameLooksMono) { label += "  -  may be for the other game version"; color = Theme.WarningText; }
                    c.Rows[row.Key] = NoteRow(content, label, color);
                }

            if (unresolved != null)
                foreach (var missing in unresolved)
                    NoteRow(content, $"!  {missing}  -  skipped", Theme.WarningText);

            Interactions.PolishButtons(formHost);
            return c;
        }

        private static Text NoteRow(RectTransform content, string text, Color color)
        {
            Scaffold.Note(content, text, color);
            return content.GetChild(content.childCount - 1).GetComponentInChildren<Text>();
        }
    }
}
