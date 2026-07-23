using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHustle.Shared
{
    /// <summary>One mod inside a named profile, identified by where it comes from.</summary>
    internal sealed class ProfileModRef
    {
        /// <summary>"base" (hardlink from the real Mods folder) | "thunderstore" (package cache) | "local"
        /// (a file the user dropped into the profile by hand - never rebuilt from elsewhere).</summary>
        public string Source { get; set; } = "";
        /// <summary>DLL file name for base/local refs, e.g. "Litterally.dll".</summary>
        public string File { get; set; }
        /// <summary>Thunderstore "Owner-Name" for thunderstore refs.</summary>
        public string FullName { get; set; }
        /// <summary>Exact pinned package version for thunderstore refs.</summary>
        public string Version { get; set; }
        /// <summary>The DLL file names this thunderstore package contributes (from the cache classification).</summary>
        public List<string> Files { get; set; }
        /// <summary>True when this ref entered the profile only as a DEPENDENCY of another install (apt-style
        /// auto-installed marker). Explicitly installing the same package later promotes it back to false. Absent
        /// in profiles written by older builds, which deserializes to false = treated as manually chosen.</summary>
        public bool AsDependency { get; set; }
    }

    internal sealed class ProfileBuildInfo
    {
        public string Path { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string BuiltAt { get; set; } = "";
        /// <summary>Mod file names this build dropped ENTIRELY as wrong-runtime Mono builds (dual-variant skips
        /// are routine and not listed). Null/absent = none. Read by the next session to inform the player.</summary>
        public List<string> ExcludedWrongRuntime { get; set; }
    }

    internal sealed class ProfileDef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Created { get; set; } = "";
        public string Modified { get; set; } = "";
        public List<ProfileModRef> Mods { get; set; } = new List<ProfileModRef>();
        public ProfileBuildInfo Build { get; set; }
        /// <summary>Key (hash) of the ExcludedWrongRuntime set the one-time "mods were disabled" dialog was last
        /// shown for - the set changing re-arms the dialog; an unchanged set only gets a small toast.</summary>
        public string RuntimeNoticeKey { get; set; }
    }

    /// <summary>A committed profile switch the boot plugin consumes on the next start (no prompt).</summary>
    internal sealed class PendingSwitch
    {
        public string ProfileId { get; set; } = "";
        public string ContinueToken { get; set; } = "";
    }

    internal sealed class ProfileSettings
    {
        public int PromptSeconds { get; set; } = 4;
        public bool PromptEnabled { get; set; } = true;
        public string DefaultProfileId { get; set; } = "";
    }

    internal sealed class ProfilesFile
    {
        public int Version { get; set; } = 1;
        public ProfileSettings Settings { get; set; } = new ProfileSettings();
        /// <summary>The profile id the player last booted with ("" = full mod set).</summary>
        public string LastChoice { get; set; } = "";
        public PendingSwitch PendingSwitch { get; set; }
        public List<ProfileDef> Profiles { get; set; } = new List<ProfileDef>();
    }

    /// <summary>
    /// profiles.json IO - the single source of truth for named profiles, readable by BOTH assemblies: the mod
    /// (manager UI, engine) and the boot plugin (picker, before the game exists). Pure BCL on purpose. Writes
    /// are atomic (tmp + replace) because the plugin and the mod both touch the file; reads tolerate a missing
    /// or unparseable file by returning a fresh document (the plugin must never block a boot on bad JSON).
    /// </summary>
    internal static class ProfileStore
    {
        internal const int SchemaVersion = 1;

        // The process-local handoff contract between the boot plugin and the mod (no assembly coupling):
        // the plugin sets these after a redirect; the mod reads them to know its session and continue intent.
        internal const string EnvActiveProfile = "SIDEHUSTLE_ACTIVE_PROFILE";
        internal const string EnvContinueToken = "SIDEHUSTLE_CONTINUE";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        internal static string PathFor(string gameRoot) =>
            Path.Combine(gameRoot, "UserData", "SideHustle", "profiles.json");

        /// <summary>Load the registry; a missing/corrupt file yields a fresh empty document, never null. A NEWER
        /// schema is preserved as-is in memory but flagged read-only via <paramref name="writable"/> so an older
        /// SideHustle cannot destroy fields it does not know.</summary>
        internal static ProfilesFile Load(string path, out bool writable)
        {
            writable = true;
            try
            {
                if (!File.Exists(path)) return new ProfilesFile();
                var doc = JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(path), JsonOpts) ?? new ProfilesFile();
                if (doc.Version > SchemaVersion) writable = false;
                doc.Settings ??= new ProfileSettings();
                doc.Profiles ??= new List<ProfileDef>();
                return doc;
            }
            catch
            {
                writable = false;   // unparseable: readable default, but never overwrite what might be user data
                return new ProfilesFile();
            }
        }

        /// <summary>Atomic write (tmp + replace). Returns false on any failure - callers treat that as "state not
        /// persisted" and must not assume the switch/edit happened.</summary>
        internal static bool Save(string path, ProfilesFile data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }
    }
}
