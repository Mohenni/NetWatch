// NetWatch - blocking programs through the firewall that is built into Windows.
//
// "Block" adds two ordinary Windows Firewall rules for one program file (one for outgoing, one for
// incoming traffic). "Unblock" deletes exactly those rules again. Every rule NetWatch creates is put
// in the rule group "NetWatch", so they are easy to find in Windows' own firewall window too, and
// NetWatch never touches a rule it did not create.
//
// Nothing in this file runs unless the user presses Block or Unblock (or removes NetWatch).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NetWatch
{
    internal sealed class BlockedApp
    {
        public string Path = "";
        public string Name = "";
        public bool Outgoing;
        public bool Incoming;
    }

    internal static class FirewallManager
    {
        public const string Group = "NetWatch";

        const int ActionBlock = 0;          // NET_FW_ACTION_BLOCK
        const int DirectionIn = 1;          // NET_FW_RULE_DIR_IN
        const int DirectionOut = 2;         // NET_FW_RULE_DIR_OUT
        const int AllProfiles = 0x7FFFFFFF; // NET_FW_PROFILE2_ALL

        static dynamic CreatePolicy()
        {
            Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true);
            return Activator.CreateInstance(type);
        }

        /// <summary>The programs NetWatch is currently blocking. Reading needs no special rights.</summary>
        public static List<BlockedApp> ListBlocked()
        {
            Dictionary<string, BlockedApp> byPath = new Dictionary<string, BlockedApp>(StringComparer.OrdinalIgnoreCase);
            List<BlockedApp> result = new List<BlockedApp>();
            dynamic policy = CreatePolicy();
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    object grouping = rule.Grouping;
                    if (!string.Equals(grouping as string, Group, StringComparison.OrdinalIgnoreCase)) continue;
                    object application = rule.ApplicationName;
                    string path = application as string;
                    if (string.IsNullOrEmpty(path)) continue;
                    if ((int)rule.Action != ActionBlock) continue;
                    if (!(bool)rule.Enabled) continue;

                    BlockedApp app;
                    if (!byPath.TryGetValue(path, out app))
                    {
                        app = new BlockedApp();
                        app.Path = path;
                        app.Name = System.IO.Path.GetFileName(path);
                        byPath[path] = app;
                        result.Add(app);
                    }
                    int direction = (int)rule.Direction;
                    if (direction == DirectionOut) app.Outgoing = true;
                    else if (direction == DirectionIn) app.Incoming = true;
                }
                catch
                {
                    // a rule we cannot read is simply not one of ours
                }
            }
            result.Sort(delegate(BlockedApp a, BlockedApp b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return result;
        }

        /// <summary>Stops one program from using the network. Needs administrator rights.</summary>
        public static void Block(string path, string displayName)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("No program file given.", "path");
            dynamic policy = CreatePolicy();
            RemoveRules(policy, path);      // never leave duplicates behind
            AddRule(policy, path, displayName, DirectionOut);
            AddRule(policy, path, displayName, DirectionIn);
        }

        /// <summary>Removes NetWatch's block for one program. Returns how many rules were deleted.</summary>
        public static int Unblock(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            dynamic policy = CreatePolicy();
            return RemoveRules(policy, path);
        }

        /// <summary>Removes every rule NetWatch created (used when NetWatch is uninstalled).</summary>
        public static int UnblockAll()
        {
            dynamic policy = CreatePolicy();
            return RemoveRules(policy, null);
        }

        /// <summary>
        /// True when Windows Firewall is switched on for the network the PC is using right now,
        /// false when it is off, null when that could not be determined.
        /// </summary>
        public static bool? IsFirewallOn()
        {
            try
            {
                dynamic policy = CreatePolicy();
                int current = (int)policy.CurrentProfileTypes;
                bool any = false;
                int[] profiles = new int[] { 1, 2, 4 };     // domain, private, public
                foreach (int profile in profiles)
                {
                    if ((current & profile) == 0) continue;
                    any = true;
                    if (!(bool)policy.FirewallEnabled[profile]) return false;
                }
                if (any) return true;
                return null;
            }
            catch
            {
                return null;
            }
        }

        static void AddRule(dynamic policy, string path, string displayName, int direction)
        {
            Type type = Type.GetTypeFromProgID("HNetCfg.FWRule", true);
            dynamic rule = Activator.CreateInstance(type);
            rule.Name = RuleName(path, displayName, direction);
            rule.Description = "Added by NetWatch. Stops this program from " +
                               (direction == DirectionOut ? "sending data out." : "receiving incoming connections.") +
                               " Remove it in NetWatch (Blocked tab) or simply delete this rule.";
            rule.ApplicationName = path;
            rule.Direction = direction;
            rule.Action = ActionBlock;
            rule.Profiles = AllProfiles;
            rule.Grouping = Group;
            rule.Enabled = true;
            policy.Rules.Add(rule);
        }

        static int RemoveRules(dynamic policy, string path)
        {
            List<string> names = new List<string>();
            foreach (dynamic rule in policy.Rules)
            {
                try
                {
                    object grouping = rule.Grouping;
                    if (!string.Equals(grouping as string, Group, StringComparison.OrdinalIgnoreCase)) continue;
                    if (path != null)
                    {
                        object application = rule.ApplicationName;
                        if (!string.Equals(application as string, path, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    object name = rule.Name;
                    string text = name as string;
                    if (!string.IsNullOrEmpty(text)) names.Add(text);
                }
                catch
                {
                }
            }
            foreach (string name in names)
                policy.Rules.Remove(name);
            return names.Count;
        }

        // Rule names must be unique per program so that deleting by name can never hit another rule.
        static string RuleName(string path, string displayName, int direction)
        {
            string label = string.IsNullOrEmpty(displayName) ? System.IO.Path.GetFileName(path) : displayName;
            label = label.Replace("|", " ").Trim();
            if (label.Length > 60) label = label.Substring(0, 60);
            return "NetWatch: block " + label + (direction == DirectionOut ? " (outgoing)" : " (incoming)") +
                   " #" + PathTag(path);
        }

        static string PathTag(string path)
        {
            // small stable fingerprint of the file path (FNV-1a), only used to keep rule names unique
            uint hash = 2166136261;
            string lower = path.ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                hash ^= lower[i];
                hash *= 16777619;
            }
            return hash.ToString("X8", CultureInfo.InvariantCulture);
        }
    }
}
