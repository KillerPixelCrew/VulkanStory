using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

namespace VulkanStory.Game.Performance;

/// <summary>Caches raw wildcard resolution, including misses, per language service. Formatting remains the original per-call operation.</summary>
internal static class TranslationLookupCache
{
    private readonly record struct Resolution(bool Found, string? Value);
    private sealed class Prefix
    {
        internal readonly Dictionary<char, Prefix> Children = new();
        internal int Order = int.MaxValue;
        internal string? Value;
    }
    private sealed class State
    {
        internal readonly Dictionary<string, Resolution> Matches = new(StringComparer.Ordinal);
        internal readonly Prefix Root = new();
        internal readonly KeyValuePair<Regex, string>[] Regexes;
        internal State(TranslationService service)
        {
            int order = 0;
            foreach (var pair in Wildcards(service))
            {
                Prefix node = Root;
                foreach (char c in pair.Key)
                {
                    if (!node.Children.TryGetValue(c, out var next)) node.Children.Add(c, next = new Prefix());
                    node = next;
                }
                node.Order = order++; node.Value = pair.Value;
            }
            Regexes = RegexesOf(service).Values.ToArray();
        }
        internal Resolution Resolve(string key)
        {
                if (Matches.TryGetValue(key, out var match)) return match;
                Prefix node = Root;
                int order = node.Order;
                string? value = node.Value;
                foreach (char c in key)
                {
                    if (!node.Children.TryGetValue(c, out var next)) break;
                    node = next;
                    // Stock selects the first matching prefix in dictionary order, not the longest prefix.
                    if (node.Order < order) { order = node.Order; value = node.Value; }
                }
                match = new(order != int.MaxValue, value);
                if (!match.Found)
                    foreach (var pair in Regexes)
                        if (pair.Key.IsMatch(key)) { match = new(true, pair.Value); break; }
                if (Matches.Count >= 8192) Matches.Clear();
                Matches.Add(key, match);
                return match;
        }
    }
    private sealed class Owner { internal readonly object Gate = new(); internal State? Cache; }
    private static ConditionalWeakTable<TranslationService, Owner> owners = new();
    private static readonly AccessTools.FieldRef<TranslationService, Dictionary<string, string>> Entries =
        AccessTools.FieldRefAccess<TranslationService, Dictionary<string, string>>("entryCache");
    private static readonly AccessTools.FieldRef<TranslationService, Dictionary<string, string>> Wildcards =
        AccessTools.FieldRefAccess<TranslationService, Dictionary<string, string>>("wildcardCache");
    private static readonly AccessTools.FieldRef<TranslationService, Dictionary<string, KeyValuePair<Regex, string>>> RegexesOf =
        AccessTools.FieldRefAccess<TranslationService, Dictionary<string, KeyValuePair<Regex, string>>>("regexCache");
    private static readonly AccessTools.FieldRef<TranslationService, HashSet<string>> NotFound =
        AccessTools.FieldRefAccess<TranslationService, HashSet<string>>("notFound");
    private static readonly AccessTools.FieldRef<TranslationService, ILogger> Logger =
        AccessTools.FieldRefAccess<TranslationService, ILogger>("logger");
    private static readonly Action<TranslationService> EnsureLoaded = AccessTools.Method(typeof(TranslationService), "EnsureLoaded")!
        .CreateDelegate<Action<TranslationService>>();
    private static readonly Func<TranslationService, string, object[], string> Format = AccessTools.Method(typeof(TranslationService), "Format")!
        .CreateDelegate<Func<TranslationService, string, object[], string>>();
    private static readonly Func<string, string, string> DomainKey = AccessTools.Method(typeof(TranslationService), "KeyWithDomain")!
        .CreateDelegate<Func<string, string, string>>();

    internal static PerformanceFeature Create() => new("translation-lookup-cache", s => s.TranslationLookupCache, () =>
    {
        if (AccessTools.Method(typeof(TranslationService), "GetMatchingIfExists", [typeof(string), typeof(object[])])?.ReturnType != typeof(string) ||
            AccessTools.Method(typeof(TranslationService), "HasTranslation", [typeof(string), typeof(bool), typeof(bool)])?.ReturnType != typeof(bool))
            throw new InvalidOperationException("Translation lookup signatures changed.");
        foreach (string name in Reloads) if (AccessTools.Method(typeof(TranslationService), name) == null)
            throw new MissingMethodException(typeof(TranslationService).FullName, name);
    }, harmony =>
    {
        harmony.Patch(AccessTools.Method(typeof(TranslationService), "GetMatchingIfExists", [typeof(string), typeof(object[])]),
            prefix: new HarmonyMethod(typeof(TranslationLookupCache), nameof(Matching)));
        harmony.Patch(AccessTools.Method(typeof(TranslationService), "HasTranslation", [typeof(string), typeof(bool), typeof(bool)]),
            prefix: new HarmonyMethod(typeof(TranslationLookupCache), nameof(Has)));
        foreach (string name in Reloads)
            harmony.Patch(AccessTools.Method(typeof(TranslationService), name),
                prefix: new HarmonyMethod(typeof(TranslationLookupCache), nameof(ReloadBegin)),
                finalizer: new HarmonyMethod(typeof(TranslationLookupCache), nameof(ReloadEnd)));
    });
    private static readonly string[] Reloads = ["Load", "PreLoad", "PreLoadModWorldConfig", "Invalidate", "UseAssetManager"];
    private static void ReloadBegin(TranslationService __instance, out Owner __state)
    {
        __state = owners.GetOrCreateValue(__instance);
        Monitor.Enter(__state.Gate); __state.Cache = null;
    }
    private static void ReloadEnd(Owner __state)
    { __state.Cache = null; Monitor.Exit(__state.Gate); }

    private static Resolution Resolve(TranslationService service, Owner owner, string key)
    {
        // Exact entries are checked directly so mutations through GetAllEntries remain visible.
        if (Entries(service).TryGetValue(key, out string? value)) return new(true, value);
        return (owner.Cache ??= new State(service)).Resolve(key);
    }
    private static bool Matching(TranslationService __instance, string key, object[] args, ref string? __result)
    {
        if (PerformancePatchSet.Current?.TranslationLookupCache != true) return true;
        Owner owner = owners.GetOrCreateValue(__instance);
        Resolution match;
        lock (owner.Gate)
        {
            EnsureLoaded(__instance);
            match = Resolve(__instance, owner, DomainKey(key, "game"));
        }
        __result = match.Found ? Format(__instance, match.Value!, args) : null;
        return false;
    }
    private static bool Has(TranslationService __instance, string key, bool findWildcarded, bool logErrors, ref bool __result)
    {
        if (PerformancePatchSet.Current?.TranslationLookupCache != true) return true;
        string validKey = DomainKey(key, "game");
        Owner owner = owners.GetOrCreateValue(__instance);
        bool first = false;
        lock (owner.Gate)
        {
            EnsureLoaded(__instance);
            __result = Entries(__instance).ContainsKey(validKey) || (findWildcarded && Resolve(__instance, owner, validKey).Found);
            if (!__result && findWildcarded && logErrors && !validKey.Contains("desc-"))
            {
                HashSet<string> missing = NotFound(__instance);
                lock (missing) first = missing.Add(validKey);
            }
        }
        if (first) Logger(__instance).VerboseDebug("Lang key not found: " + validKey.Replace("{", "{{").Replace("}", "}}"));
        return false;
    }
}
