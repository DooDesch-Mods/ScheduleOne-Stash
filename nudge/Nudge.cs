// SPDX-License-Identifier: MIT
// Copyright (c) 2026 DooDesch

using System;
using System.Collections.Generic;
using MelonLoader;
#if !NO_EXTERNAL_FETCH
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MelonLoader.Utils;
#endif

#if NUDGE_STANDALONE
[assembly: MelonInfo(typeof(DooDesch.Nudge.Core), "Nudge", "1.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Stash")]
[assembly: MelonGame("TVGS", "Schedule I")]
#endif

namespace DooDesch.Nudge
{
#if NUDGE_STANDALONE
    /// <summary>Standalone MelonMod entry: drop Nudge.dll into Mods/ and every installed mod gets the update
    /// check, with nothing to compile and no other mod required. Excluded when the file is embedded as linked
    /// source into a host mod, which calls <see cref="Nudge.Watch"/> itself. Both can be present - the first
    /// one through claims the run.</summary>
    public sealed class Core : MelonMod
    {
        public override void OnInitializeMelon() => Nudge.Watch();
    }
#endif

    /// <summary>
    /// Tells the player in the MelonLoader console which of their installed mods are out of date.
    /// Canonical home: https://github.com/DooDesch-Mods/ScheduleOne-Stash
    ///
    /// One call, no arguments, no configuration:
    ///     DooDesch.Nudge.Nudge.Watch();   // once, in OnInitializeMelon
    ///
    /// It checks EVERY loaded mod, not just the one that calls it. MelonLoader already knows each
    /// mod's version and download link (the 5th MelonInfo argument), so a mod that declares
    ///     [assembly: MelonInfo(typeof(Core), "MyMod", "1.0.0", "Me", "https://github.com/me/MyMod")]
    /// is covered whether or not it has ever heard of this file. One mod embedding Nudge is enough
    /// for the whole install; further copies detect each other and stand down.
    ///
    /// The latest version comes from the repo's GitHub release tag. Mods without a github.com link
    /// are skipped silently, as is every network or parse failure - an update check must never cost
    /// a player their game start. Results are cached in UserData/DooDesch/Nudge.txt for six hours,
    /// which keeps a large mod list well inside GitHub's anonymous rate limit.
    ///
    /// Players can turn it off in MelonPreferences.cfg under [DooDesch]: UpdateCheck = false.
    ///
    /// The class is INTERNAL so several mods can each compile it in without a CS0436 duplicate-type
    /// clash; change it to public if you would rather reference it as a shared library. It needs
    /// nothing but MelonLoader and the BCL - no Harmony, no S1API, no Unity, no game types, and it
    /// touches no Unity API, so the worker thread is safe. Identical for IL2CPP and Mono.
    ///
    /// Defining NO_EXTERNAL_FETCH compiles out the endpoint, the HTTP client and the parser, leaving
    /// Watch() as a no-op. Stash/build/Nudge.props sets it for Thunderstore builds.
    /// </summary>
    internal static class Nudge
    {
        /// <summary>
        /// Checks every loaded mod against its GitHub releases and logs the outdated ones.
        /// Returns immediately; the work happens on a worker thread. Call on the main thread, once,
        /// from OnInitializeMelon - by then MelonLoader has registered all mods.
        /// </summary>
        internal static void Watch()
        {
#if !NO_EXTERNAL_FETCH
            // Several mods may each have compiled this file in. The first one through claims the run
            // for the process; the rest return here. AppDomain data is the one store all of them
            // share (on CoreCLR it is AppContext's locked dictionary in the runtime's own CoreLib),
            // and unlike an assembly scan it cannot kill an IL2CPP process. Read-then-write is not
            // atomic, which is fine because OnInitializeMelon runs serially on the main thread.
            const string Claim = "DooDesch.Nudge";
            try
            {
                if (AppDomain.CurrentDomain.GetData(Claim) != null) return;
                AppDomain.CurrentDomain.SetData(Claim, "1");
            }
            catch { return; }

            if (!Enabled()) return;

            // Snapshot on the main thread. RegisteredMelons is a live view of MelonLoader's own list
            // and a mod may unregister itself while initializing, which would break an enumeration
            // running on the worker.
            var mods = new List<Mod>();
            try
            {
                foreach (var m in MelonMod.RegisteredMelons)
                {
                    var info = m?.Info;
                    if (info == null) continue;
                    if (!TryParseRepo(info.DownloadLink, out string owner, out string repo)) continue;
                    mods.Add(new Mod { Name = info.Name, Version = info.Version, Owner = owner, Repo = repo });
                }
            }
            catch { return; }
            if (mods.Count == 0) return;

            Task.Run(() => Run(mods));
#endif
        }

#if !NO_EXTERNAL_FETCH
        private sealed class Mod
        {
            public string Name;
            public string Version;
            public string Owner;
            public string Repo;
            public string Slug => Owner + "/" + Repo;
        }

        private const int CacheHours = 6;

        private static readonly Lazy<HttpClient> _http = new Lazy<HttpClient>(() =>
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("DooDesch-Nudge/1.0");   // GitHub rejects a request without one
            return c;
        });

        private static bool Enabled()
        {
            try
            {
                var cat = MelonPreferences.CreateCategory("DooDesch", "DooDesch Mods");
                // CreateEntry throws when the entry already exists, and another mod's copy of this
                // file may have made it first.
                var entry = cat.GetEntry<bool>("UpdateCheck")
                            ?? cat.CreateEntry("UpdateCheck", true, "Update check",
                                "Check on start whether your installed mods have a newer release, and say so in the console.");
                return entry.Value;
            }
            catch { return true; }
        }

        private static async Task Run(List<Mod> mods)
        {
            try
            {
                var cache = ReadCache();
                var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool cacheChanged = false;

                foreach (var mod in mods)
                {
                    if (latest.ContainsKey(mod.Slug)) continue;   // two mods, one repo - ask once

                    if (cache.TryGetValue(mod.Slug, out var hit) && now - hit.Stamp < CacheHours * 3600)
                    {
                        latest[mod.Slug] = hit.Tag;
                        continue;
                    }

                    string tag = await Fetch(mod.Owner, mod.Repo).ConfigureAwait(false);
                    if (tag == null) break;              // rate limited - stop, keep what we have
                    latest[mod.Slug] = tag;
                    cache[mod.Slug] = (now, tag);
                    cacheChanged = true;
                }

                if (cacheChanged) WriteCache(cache);

                var stale = new List<Mod>();
                foreach (var mod in mods)
                    if (latest.TryGetValue(mod.Slug, out string tag) && IsNewer(tag, mod.Version))
                        stale.Add(mod);
                Announce(stale, latest);
            }
            catch (Exception e)
            {
                // Nothing here is worth interrupting a game start for, but a swallowed exception in a
                // fire-and-forget task is invisible, so leave a trace for whoever is debugging.
                MelonDebug.Msg("[Nudge] update check failed: " + e);
            }
        }

        /// <summary>The repo's latest release tag, "" when there is none or the answer was unusable,
        /// null when GitHub rate-limited us and the rest of the run should be abandoned.</summary>
        private static async Task<string> Fetch(string owner, string repo)
        {
            try
            {
                string url = "https://api.github.com/repos/" + Uri.EscapeDataString(owner)
                             + "/" + Uri.EscapeDataString(repo) + "/releases/latest";
                using (var res = await _http.Value.GetAsync(url).ConfigureAwait(false))
                {
                    if (res.StatusCode == HttpStatusCode.Forbidden || (int)res.StatusCode == 429) return null;
                    // 404 is ordinary: a repo can be private, renamed, or have only prereleases.
                    if (!res.IsSuccessStatusCode) return "";
                    string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                    // The release object opens with tag_name, well before any field that could carry
                    // the same text in a description, so the first match is the tag.
                    var m = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!m.Success) return "";
                    // Tags are conventionally "v1.2.3" but the mod's own version is not, and the two sit
                    // next to each other in the banner - drop the prefix here so both read the same way.
                    string tag = m.Groups[1].Value;
                    return tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
                }
            }
            catch { return ""; }
        }

        private static void Announce(List<Mod> stale, Dictionary<string, string> latest)
        {
            if (stale.Count == 0) return;

            int pad = 0;
            foreach (var mod in stale) if (mod.Name.Length > pad) pad = mod.Name.Length;

            // A named logger, not the static one: static MelonLogger derives the "[...]" prefix from a stack
            // walk, and on a worker thread that lands on whichever assembly happens to be there - so the
            // block would be signed by a random mod, or by the host mod that merely compiled this file in.
            var log = new MelonLogger.Instance("Nudge");
            log.Msg(ConsoleColor.Yellow, stale.Count == 1
                ? "1 mod is out of date"
                : stale.Count.ToString(CultureInfo.InvariantCulture) + " mods are out of date");
            foreach (var mod in stale)
                log.Msg(ConsoleColor.Yellow, "  " + mod.Name.PadRight(pad)
                    + "  " + mod.Version + " -> " + latest[mod.Slug]
                    + "   https://github.com/" + mod.Slug + "/releases");
        }

        /// <summary>Owner and repo out of a GitHub URL - the repo page or any subpath of it. Only the
        /// canonical host counts; gist and raw links are not release sources.</summary>
        private static bool TryParseRepo(string url, out string owner, out string repo)
        {
            owner = repo = null;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
            owner = parts[0];
            repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? parts[1].Substring(0, parts[1].Length - 4) : parts[1];
            return repo.Length > 0;
        }

        /// <summary>Whether the release tag is a later version than the installed one. A leading "v" and
        /// trailing zero segments are cosmetic, so "v1.2.3", "1.2.3" and "1.2.3.0" are the same version.
        /// Anything that does not parse as numbers counts as not newer - a mod with an exotic version
        /// string is left alone rather than announced on a guess.</summary>
        private static bool IsNewer(string tag, string installed)
        {
            var remote = Segments(tag);
            var local = Segments(installed);
            if (remote == null || local == null) return false;
            for (int i = 0; i < 4; i++)
            {
                int r = i < remote.Count ? remote[i] : 0;
                int l = i < local.Count ? local[i] : 0;
                if (r != l) return r > l;
            }
            return false;
        }

        private static List<int> Segments(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            int cut = v.IndexOfAny(new[] { '-', '+' });   // a prerelease or build suffix: compare the numbers before it
            if (cut >= 0) v = v.Substring(0, cut);
            var parts = v.Split('.');
            if (parts.Length > 4) return null;
            var result = new List<int>();
            foreach (var part in parts)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int seg)) return null;
                result.Add(seg);
            }
            return result.Count > 0 ? result : null;
        }

        private static string CachePath() =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "DooDesch", "Nudge.txt");

        private static Dictionary<string, (long Stamp, string Tag)> ReadCache()
        {
            var map = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = CachePath();
                if (!File.Exists(path)) return map;
                foreach (var line in File.ReadAllLines(path))
                {
                    var f = line.Split('\t');
                    if (f.Length == 3 && long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long stamp))
                        map[f[0]] = (stamp, f[2]);
                }
            }
            catch { /* a lost cache costs one API call, nothing more */ }
            return map;
        }

        private static void WriteCache(Dictionary<string, (long Stamp, string Tag)> map)
        {
            try
            {
                string path = CachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                foreach (var kv in map)
                    sb.Append(kv.Key).Append('\t')
                      .Append(kv.Value.Stamp.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(kv.Value.Tag).Append('\n');
                // Several game instances can share one UserData folder (the multiplayer test copies do),
                // so write through a process-private file and swap it in - never straight onto the target.
                string tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            catch { /* same as above */ }
        }
#endif
    }
}
