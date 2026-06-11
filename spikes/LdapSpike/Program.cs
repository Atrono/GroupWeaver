// LdapSpike — AP 0.2 (Phase 0 throwaway spike, GitHub issue #2)
//
// Proves read-only LDAP access patterns against the lab AD:
//   1. Integrated-auth bind to OU=AGDLP-Lab,DC=agdlp,DC=lab
//   2. Paged subtree enumeration (DirectorySearcher, PageSize = 500)
//   3. Recursive (DFS) group-membership resolution with cycle detection
//
// STRICTLY READ-ONLY: DirectorySearcher/DirectoryEntry reads only.
// No CommitChanges, no property writes — ever (project rule #1).

using System.Diagnostics;
using System.DirectoryServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string LdapPath = "LDAP://localhost/OU=AGDLP-Lab,DC=agdlp,DC=lab";

    private static void Main(string[] args)
    {
        // Optional arg: page size override, to prove paging across page
        // boundaries (194 objects fit into a single 500-object page).
        int pageSize = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 500;

        Console.WriteLine("LdapSpike — read-only LDAP access-pattern spike (AP 0.2)");
        Console.WriteLine($"Bind: {LdapPath} (Integrated Windows Auth, no credentials in code)");
        Console.WriteLine();

        // ------------------------------------------------------------------
        // (2) Paged subtree enumeration: count by objectClass, cache members
        // ------------------------------------------------------------------
        var swEnumerate = Stopwatch.StartNew();

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var groupMembers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var allDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        using (var root = new DirectoryEntry(LdapPath)) // integrated auth: no username/password supplied
        using (var searcher = new DirectorySearcher(root))
        {
            searcher.Filter = "(objectClass=*)";
            searcher.SearchScope = SearchScope.Subtree;
            searcher.PageSize = pageSize; // server-side paging (RFC 2696 paged-results control)
            searcher.PropertiesToLoad.AddRange(new[] { "distinguishedName", "objectClass", "member" });

            using SearchResultCollection results = searcher.FindAll(); // must dispose: unmanaged handle leak otherwise

            foreach (SearchResult result in results)
            {
                total++;
                string dn = (string)result.Properties["distinguishedName"][0];
                allDns.Add(dn);

                string category = Classify(result.Properties["objectClass"]);
                counts[category] = counts.GetValueOrDefault(category) + 1;

                if (category == "group")
                {
                    var members = new List<string>();
                    if (result.Properties.Contains("member"))
                    {
                        foreach (object m in result.Properties["member"])
                        {
                            members.Add((string)m);
                        }
                    }
                    groupMembers[dn] = members;
                }
            }
        }

        swEnumerate.Stop();

        Console.WriteLine($"connected, {groupMembers.Count} groups loaded");
        foreach ((string category, int count) in counts)
        {
            Console.WriteLine($"  {category,-20} {count,4}");
        }
        Console.WriteLine($"  {"total",-20} {total,4}");
        Console.WriteLine($"[2] paged subtree enumeration (PageSize={pageSize}): {swEnumerate.ElapsedMilliseconds} ms");
        Console.WriteLine();

        // ------------------------------------------------------------------
        // (3) Recursive membership resolution: DFS with visited-set so the
        //     seeded circular nesting (A->B->A) terminates
        // ------------------------------------------------------------------
        var swResolve = Stopwatch.StartNew();

        var cycleReports = new List<string>(); // ordered, de-duplicated cycle evidence
        var cycleSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stats = new List<(string Dn, int Direct, int Transitive)>();
        int outsideOuMembers = 0;

        foreach ((string groupDn, List<string> directMembers) in groupMembers)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // every DN reached from this root
            var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { groupDn }; // current recursion stack

            void Walk(string currentDn)
            {
                foreach (string memberDn in groupMembers[currentDn])
                {
                    if (!allDns.Contains(memberDn))
                    {
                        outsideOuMembers++;
                    }

                    if (groupMembers.ContainsKey(memberDn)) // nested group
                    {
                        if (path.Contains(memberDn))
                        {
                            string report = $"circular reference: group '{Rdn(memberDn)}' already visited under group '{Rdn(currentDn)}' ({memberDn} -> {currentDn} -> {memberDn})";
                            if (cycleSeen.Add(report))
                            {
                                cycleReports.Add(report);
                            }
                            continue; // do not descend — this is what terminates A->B->A
                        }

                        if (!visited.Add(memberDn))
                        {
                            continue; // already expanded via another branch
                        }

                        path.Add(memberDn);
                        Walk(memberDn);
                        path.Remove(memberDn);
                    }
                    else
                    {
                        visited.Add(memberDn); // leaf: user/computer/anything that is not a lab group
                    }
                }
            }

            Walk(groupDn);
            stats.Add((groupDn, directMembers.Count, visited.Count));
        }

        swResolve.Stop();

        Console.WriteLine("recursive group-membership resolution (depth-first, visited DN tracking):");
        foreach (string report in cycleReports)
        {
            Console.WriteLine($"  !! {report}");
        }

        Console.WriteLine("  top 5 groups by transitive member count:");
        foreach ((string dn, int direct, int transitive) in stats
                     .OrderByDescending(s => s.Transitive)
                     .ThenBy(s => s.Dn, StringComparer.OrdinalIgnoreCase)
                     .Take(5))
        {
            Console.WriteLine($"    {Rdn(dn),-28} direct={direct,3}  transitive={transitive,3}");
        }

        int emptyGroups = stats.Count(s => s.Direct == 0);
        Console.WriteLine($"  empty groups (no member values): {emptyGroups}");
        Console.WriteLine($"  member DNs pointing outside OU=AGDLP-Lab: {outsideOuMembers}");
        Console.WriteLine($"[3] recursive membership resolution: {swResolve.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Maps the multi-valued objectClass chain to the most specific class we
    /// care about. Order matters: computers carry "user" in their chain, so
    /// "computer" must win before "user".
    /// </summary>
    private static string Classify(ResultPropertyValueCollection objectClasses)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object value in objectClasses)
        {
            values.Add((string)value);
        }

        if (values.Contains("computer")) return "computer";
        if (values.Contains("group")) return "group";
        if (values.Contains("organizationalUnit")) return "organizationalUnit";
        if (values.Contains("user")) return "user";
        return $"other({string.Join('/', values)})";
    }

    /// <summary>First RDN value of a DN, e.g. "CN=GG-Sales,OU=..." -> "GG-Sales".</summary>
    private static string Rdn(string dn)
    {
        string first = dn.Split(',')[0];
        int eq = first.IndexOf('=');
        return eq >= 0 ? first[(eq + 1)..] : first;
    }
}
