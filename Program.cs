using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string SourceDirectory = "/etc/dpm/sources.list.d";
    private const string CacheDirectory = "/var/cache/dpm";
    private const string IndexDirectory = CacheDirectory + "/list";
    private const string PackageDirectory = CacheDirectory + "/deb";
    private const string DpmStateDirectory = "/var/lib/dpm";
    private const string AutoInstalledFile = DpmStateDirectory + "/auto-installed";
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    static Program()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("dpm/0.1");
    }

    private static ConfirmationMode Confirmation = ConfirmationMode.Prompt;

    private static async Task<int> Main(string[] args)
    {
        var assumeYes = args.Any(argument => argument is "-y" or "--yes");
        var assumeNo = args.Any(argument => argument is "-n" or "--no");
        if (assumeYes && assumeNo)
        {
            Console.Error.WriteLine("Error: -y and -n cannot be used together.");
            return 2;
        }
        Confirmation = assumeYes
            ? ConfirmationMode.Yes
            : assumeNo ? ConfirmationMode.No : ConfirmationMode.Prompt;
        args = args.Where(argument => argument is not ("-y" or "--yes" or "-n" or "--no")).ToArray();

        if (GetEffectiveUid() != 0)
        {
            Console.Error.WriteLine("Error: dpm must be run as root. It will not attempt privilege escalation.");
            return 1;
        }

        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            Directory.CreateDirectory(IndexDirectory);
            Directory.CreateDirectory(PackageDirectory);
            switch (args[0])
            {
                case "update":
                case "up":
                    RequireCount(args, 1);
                    await UpdateAsync();
                    break;
                case "install":
                    RequireCount(args, 2);
                    await UpdateAsync();
                    await InstallAsync(args[1]);
                    break;
                case "reinstall":
                    RequireCount(args, 2);
                    await UpdateAsync();
                    await ReinstallAsync(args[1]);
                    break;
                case "remove":
                case "uninstall":
                    RequireCount(args, 2);
                    await RemoveAsync(args[1], false);
                    break;
                case "purge":
                    RequireCount(args, 2);
                    await RemoveAsync(args[1], true);
                    break;
                case "upgrade":
                    if (args.Length > 2) throw new ArgumentException("Usage: dpm upgrade [package]");
                    await UpgradeAsync(args.Length == 2 ? args[1] : null);
                    break;
                case "check":
                    RequireCount(args, 1);
                    await RunDpkgAsync(["--audit"]);
                    break;
                case "check-filesystem":
                    RequireCount(args, 1);
                    await RunDpkgAsync(["--verify"]);
                    break;
                case "download":
                    RequireCount(args, 2);
                    await DownloadAsync(args[1]);
                    break;
                case "clear":
                    RequireCount(args, 1);
                    ClearPackageCache();
                    break;
                case "search":
                    RequireCount(args, 2);
                    Search(args[1]);
                    break;
                default:
                    PrintUsage();
                    return 2;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            dpm - Debian package manager
            Usage:
              dpm [-y | -n] COMMAND [ARGS...]
              dpm update
              dpm up                    (alias for update)
              dpm install PACKAGE       (updates indexes before installing)
              dpm reinstall PACKAGE    (reinstalls the installed package version)
              dpm remove PACKAGE
              dpm uninstall PACKAGE    (alias for remove)
              dpm purge PACKAGE        (remove package and its configuration files)
              dpm upgrade [PACKAGE]
              dpm check                 (dpkg consistency audit)
              dpm check-filesystem      (verify installed package files)
              dpm download PACKAGE
              dpm clear                 (clear downloaded .deb cache)
              dpm search TERM
            Options:
              -y, --yes                 automatically confirm package/cache changes
              -n, --no                  automatically cancel package/cache changes
            """);
    }

    private static void RequireCount(string[] args, int count)
    {
        if (args.Length != count) throw new ArgumentException("Invalid arguments. Use dpm --help for usage.");
    }

    private static async Task UpdateAsync()
    {
        var sources = ReadSources();
        if (sources.Count == 0)
            throw new InvalidOperationException($"No deb sources found in {SourceDirectory}.");

        var architecture = await GetArchitectureAsync();
        var keyrings = GetKeyrings();
        if (keyrings.Count == 0)
            throw new InvalidOperationException("No APT signing keyrings found; refusing to use unsigned repository metadata.");

        var applicableSources = sources
            .Where(s => s.Architectures is null || s.Architectures.Contains(architecture, StringComparer.Ordinal))
            .ToList();
        if (applicableSources.Count == 0)
            throw new InvalidOperationException($"No configured repository supports architecture {architecture}.");
        var total = applicableSources.Sum(source => source.IsFlat ? 1 : source.Components.Count);
        var done = 0;
        var unchanged = 0;
        RenderIndexProgress(done, total);
        foreach (var source in applicableSources)
        {
            var components = source.IsFlat ? new string?[] { null } : source.Components.Select(component => (string?)component);
            foreach (var component in components)
            {
                if (!await UpdateIndexAsync(source, component, architecture, keyrings))
                    unchanged++;
                RenderIndexProgress(++done, total);
            }
        }
        if (unchanged > 0)
            Console.WriteLine($"有 {unchanged} 个本地索引与服务器一致，已跳过下载。");
    }

    private static void RenderIndexProgress(int completedIndexes, int totalIndexes)
    {
        var percent = completedIndexes * 100 / totalIndexes;
        var width = 20;
        var filled = percent * width / 100;
        var bar = new string('#', filled) + new string('-', width - filled);
        var message = $"正在更新索引 [{bar}] {percent,3}%";
        if (Console.IsOutputRedirected)
            Console.WriteLine(message);
        else
            Console.Write($"\r{message}\u001b[K");
        if (completedIndexes == totalIndexes)
            Console.WriteLine();
    }

    private static async Task<bool> UpdateIndexAsync(
        RepositorySource source,
        string? component,
        string architecture,
        IReadOnlyList<string> keyrings)
    {
        var distUri = source.IsFlat
            ? EnsureTrailingSlash(source.Uri)
            : EnsureTrailingSlash(source.Uri) + "dists/" + Uri.EscapeDataString(source.Suite) + "/";
        var temp = Path.Combine(Path.GetTempPath(), "dpm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var releasePath = Path.Combine(temp, "Release");
            var inReleasePath = Path.Combine(temp, "InRelease");
            if (await TryDownloadToFileAsync(new Uri(distUri + "InRelease"), inReleasePath))
                await VerifyInReleaseAsync(inReleasePath, releasePath, keyrings);
            else
            {
                await DownloadToFileAsync(new Uri(distUri + "Release"), releasePath);
                var signaturePath = Path.Combine(temp, "Release.gpg");
                await DownloadToFileAsync(new Uri(distUri + "Release.gpg"), signaturePath);
                await VerifyReleaseAsync(signaturePath, releasePath, keyrings);
            }

            var releaseText = await File.ReadAllTextAsync(releasePath);
            var checksums = ParseSha256(releaseText);
            string? relativePath = null;
            string? expectedHash = null;
            var indexPaths = source.IsFlat
                ? new[] { "Packages.xz", "Packages.gz", "Packages" }
                : new[]
                {
                    $"{component}/binary-{architecture}/Packages.xz",
                    $"{component}/binary-{architecture}/Packages.gz",
                    $"{component}/binary-{architecture}/Packages"
                };
            foreach (var candidate in indexPaths)
            {
                if (checksums.TryGetValue(candidate, out var hash))
                {
                    relativePath = candidate;
                    expectedHash = hash;
                    break;
                }
            }
            if (relativePath is null || expectedHash is null)
                throw new InvalidDataException(source.IsFlat
                    ? $"No signed flat package index found for {source.Uri}."
                    : $"No signed package index found for {component}/{architecture}.");

            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{source.Uri}|{source.Suite}|{component}|{architecture}"))).ToLowerInvariant()[..20];
            var destination = Path.Combine(IndexDirectory, id + ".Packages");
            var sourcePath = Path.ChangeExtension(destination, ".source");
            var metadataPath = Path.ChangeExtension(destination, ".meta");
            var metadata = new IndexCacheMetadata(
                source.Uri,
                source.Suite,
                component ?? "",
                architecture,
                relativePath,
                expectedHash);
            if (File.Exists(destination) && File.Exists(sourcePath) && File.Exists(metadataPath) &&
                File.ReadAllText(sourcePath).Trim() == source.Uri &&
                IsCachedIndexCurrent(metadataPath, metadata))
            {
                return false;
            }

            var compressedPath = Path.Combine(temp, "Packages.download");
            await DownloadToFileAsync(new Uri(distUri + relativePath), compressedPath);
            if (!await HasHashAsync(compressedPath, expectedHash))
                throw new InvalidDataException($"SHA256 mismatch for {source.Uri} {relativePath}.");

            var stagedIndex = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            var stagedMetadata = metadataPath + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                if (relativePath.EndsWith(".xz", StringComparison.Ordinal))
                    await DecompressAsync("xz", ["-dc", compressedPath], stagedIndex);
                else if (relativePath.EndsWith(".gz", StringComparison.Ordinal))
                    await DecompressAsync("gzip", ["-dc", compressedPath], stagedIndex);
                else
                    File.Copy(compressedPath, stagedIndex, true);
                File.Move(stagedIndex, destination, true);
                await File.WriteAllTextAsync(stagedMetadata, JsonSerializer.Serialize(metadata));
                File.Move(stagedMetadata, metadataPath, true);
            }
            finally
            {
                if (File.Exists(stagedIndex)) File.Delete(stagedIndex);
                if (File.Exists(stagedMetadata)) File.Delete(stagedMetadata);
            }
            var stagedSource = sourcePath + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                await File.WriteAllTextAsync(stagedSource, source.Uri);
                File.Move(stagedSource, sourcePath, true);
            }
            finally
            {
                if (File.Exists(stagedSource)) File.Delete(stagedSource);
            }
            return true;
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static bool IsCachedIndexCurrent(string metadataPath, IndexCacheMetadata expected)
    {
        try
        {
            var cached = JsonSerializer.Deserialize<IndexCacheMetadata>(File.ReadAllText(metadataPath));
            return cached == expected;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task VerifyReleaseAsync(string signature, string release, IReadOnlyList<string> keyrings)
    {
        var arguments = new List<string> { "--status-fd", "1" };
        foreach (var keyring in keyrings)
        {
            arguments.Add("--keyring");
            arguments.Add(keyring);
        }
        arguments.Add(signature);
        arguments.Add(release);
        var result = await RunProcessAsync("gpgv", arguments);
        if (result.ExitCode != 0 || !result.Output.Contains("[GNUPG:] VALIDSIG ", StringComparison.Ordinal))
            throw new InvalidDataException($"Repository Release signature verification failed: {result.Error.Trim()}");
    }

    private static async Task VerifyInReleaseAsync(
        string inRelease,
        string release,
        IReadOnlyList<string> keyrings)
    {
        var arguments = new List<string> { "--status-fd", "1", "--output", release };
        foreach (var keyring in keyrings)
        {
            arguments.Add("--keyring");
            arguments.Add(keyring);
        }
        arguments.Add(inRelease);
        var result = await RunProcessAsync("gpgv", arguments);
        if (result.ExitCode != 0 || !result.Output.Contains("[GNUPG:] VALIDSIG ", StringComparison.Ordinal))
            throw new InvalidDataException($"Repository InRelease signature verification failed: {result.Error.Trim()}");
    }

    private static Dictionary<string, string> ParseSha256(string release)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = release.Split('\n');
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line == "SHA256:") { inSection = true; continue; }
            if (inSection && line.Length > 0 && !char.IsWhiteSpace(line[0])) break;
            if (!inSection || string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3 && fields[0].Length == 64)
                result[fields[2]] = fields[0].ToLowerInvariant();
        }
        return result;
    }

    private static async Task InstallAsync(string packageName)
    {
        var root = SelectCandidate(packageName);
        if (root is null) throw new InvalidOperationException($"Package '{packageName}' was not found in the package indexes.");
        var installed = await GetInstalledAsync();
        var plan = ResolveDependencies([root], installed);
        var changes = CreatePackageChanges(plan, installed);
        if (!ConfirmPackageChanges("安装", changes))
            return;
        var files = await DownloadPackagesAsync(plan.Values.OrderBy(package => package.Name, StringComparer.Ordinal));
        await RunDpkgAsync(["--install", .. files], files.Count, true);
        var autoInstalled = ReadDpmAutoInstalled();
        autoInstalled.RemoveWhere(name => PackageNameMatches(name, root.Name));
        AddNewAutomaticDependencies(autoInstalled, plan, installed, [root.Name]);
        WriteDpmAutoInstalled(autoInstalled);
    }

    private static async Task ReinstallAsync(string packageName)
    {
        var installed = await GetInstalledAsync();
        var current = installed.FirstOrDefault(entry => PackageNameMatches(entry.Key, packageName));
        if (current.Key is null)
            throw new InvalidOperationException($"Package '{packageName}' is not installed.");

        var root = LoadPackages().FirstOrDefault(package =>
            PackageNameMatches(package.Name, current.Key) &&
            package.Version == current.Value.Version);
        if (root is null)
            throw new InvalidOperationException(
                $"Package '{current.Key}' version {current.Value.Version} is not available in the configured repositories.");

        var plan = ResolveDependencies([root], installed);
        var changes = CreatePackageChanges(plan, installed);
        if (!ConfirmPackageChanges("重新安装", changes))
            return;

        var files = await DownloadPackagesAsync(plan.Values.OrderBy(package => package.Name, StringComparer.Ordinal));
        await RunDpkgAsync(["--install", .. files], files.Count, true);
        var autoInstalled = ReadDpmAutoInstalled();
        AddNewAutomaticDependencies(autoInstalled, plan, installed, [root.Name]);
        WriteDpmAutoInstalled(autoInstalled);
    }

    private static async Task RemoveAsync(string packageName, bool purge)
    {
        var installed = await GetInstalledAsync();
        var match = installed.FirstOrDefault(entry => PackageNameMatches(entry.Key, packageName));
        if (match.Key is null)
            throw new InvalidOperationException($"Installed package '{packageName}' was not found.");
        var removalSet = new HashSet<string>([match.Key], StringComparer.Ordinal);
        var autoInstalled = ReadAutoInstalledPackages();
        var orphans = FindOrphanPackages(installed, autoInstalled, removalSet);
        removalSet.UnionWith(orphans);

        var changes = removalSet
            .Select(name => new PackageChange(
                name,
                installed[name].Version,
                null,
                name == match.Key ? null : "自动安装的孤儿依赖"))
            .OrderBy(change => change.Name, StringComparer.Ordinal)
            .ToList();
        var action = purge ? "彻底清除" : "卸载";
        if (!ConfirmPackageChanges(action, changes))
            return;

        var dpkgAction = purge ? "--purge" : "--remove";
        await RunDpkgAsync([dpkgAction, "--", .. removalSet.Order(StringComparer.Ordinal)], removalSet.Count, true);
        var dpmAutoInstalled = ReadDpmAutoInstalled();
        dpmAutoInstalled.ExceptWith(removalSet);
        WriteDpmAutoInstalled(dpmAutoInstalled);
    }

    private static async Task UpgradeAsync(string? packageName)
    {
        var installed = await GetInstalledAsync();
        var candidates = LoadPackages()
            .Where(p => packageName is null || PackageNameMatches(p.Name, packageName))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(group => SelectNewest(group)!)
            .Select(p => new
            {
                Candidate = p,
                Installed = installed.FirstOrDefault(entry => PackageNameMatches(entry.Key, p.Name))
            })
            .Where(item => item.Installed.Key is not null &&
                           IsVersionGreater(item.Candidate.Version, item.Installed.Value.Version))
            .ToList();

        if (candidates.Count == 0)
        {
            Console.WriteLine("没有可升级的软件包。");
            return;
        }

        var plan = ResolveDependencies(candidates.Select(item => item.Candidate).ToList(), installed);
        var changes = CreatePackageChanges(plan, installed);
        if (!ConfirmPackageChanges("升级", changes))
            return;

        var files = await DownloadPackagesAsync(plan.Values.OrderBy(package => package.Name, StringComparer.Ordinal));
        await RunDpkgAsync(["--install", .. files], files.Count, true);
        var autoInstalled = ReadDpmAutoInstalled();
        AddNewAutomaticDependencies(autoInstalled, plan, installed, candidates.Select(item => item.Candidate.Name));
        WriteDpmAutoInstalled(autoInstalled);
    }

    private static void AddNewAutomaticDependencies(
        HashSet<string> autoInstalled,
        IReadOnlyDictionary<string, PackageRecord> plan,
        IReadOnlyDictionary<string, InstalledPackage> installed,
        IEnumerable<string> requestedPackages)
    {
        var requested = requestedPackages.ToHashSet(StringComparer.Ordinal);
        foreach (var package in plan.Values)
        {
            var wasInstalled = installed.Keys.Any(name => PackageNameMatches(name, package.Name));
            if (!wasInstalled && !requested.Contains(package.Name))
                autoInstalled.Add(package.Name);
        }
    }

    private static HashSet<string> FindOrphanPackages(
        IReadOnlyDictionary<string, InstalledPackage> installed,
        IReadOnlySet<string> autoInstalled,
        IReadOnlySet<string> removed)
    {
        var dependencyIndex = BuildInstalledDependencyIndex(installed);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        foreach (var packageName in installed.Keys)
        {
            if (!removed.Contains(packageName) && !IsAutoInstalled(packageName, autoInstalled))
            {
                reachable.Add(packageName);
                pending.Enqueue(packageName);
            }
        }

        while (pending.TryDequeue(out var packageName))
        {
            if (!installed.TryGetValue(packageName, out var package))
                continue;

            foreach (var dependency in ParseDependencyGroups(package.PreDepends)
                         .Concat(ParseDependencyGroups(package.Depends)))
            {
                var automaticCandidates = dependency
                    .SelectMany(alternative => GetInstalledCandidates(dependencyIndex, alternative))
                    .Where(candidate => !removed.Contains(candidate.Key) &&
                                        IsAutoInstalled(candidate.Key, autoInstalled))
                    .DistinctBy(candidate => candidate.Key)
                    .Where(candidate => dependency.Any(alternative =>
                        InstalledPackageSatisfies(candidate.Value, candidate.Key, alternative)))
                    .ToList();
                if (automaticCandidates.Count == 0)
                    continue;

                var satisfiedByManualPackage = dependency
                    .SelectMany(alternative => GetInstalledCandidates(dependencyIndex, alternative))
                    .Where(candidate => !removed.Contains(candidate.Key) &&
                                        !IsAutoInstalled(candidate.Key, autoInstalled))
                    .DistinctBy(candidate => candidate.Key)
                    .Any(candidate => dependency.Any(alternative =>
                        InstalledPackageSatisfies(candidate.Value, candidate.Key, alternative)));
                if (satisfiedByManualPackage)
                    continue;

                foreach (var candidate in automaticCandidates)
                {
                    if (reachable.Add(candidate.Key))
                        pending.Enqueue(candidate.Key);
                }
            }
        }

        return autoInstalled
            .Where(name => installed.ContainsKey(name) &&
                           !removed.Contains(name) &&
                           !reachable.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static InstalledDependencyIndex BuildInstalledDependencyIndex(
        IReadOnlyDictionary<string, InstalledPackage> installed)
    {
        var byName = new Dictionary<string, List<KeyValuePair<string, InstalledPackage>>>(StringComparer.Ordinal);
        var byProvide = new Dictionary<string, List<KeyValuePair<string, InstalledPackage>>>(StringComparer.Ordinal);

        foreach (var entry in installed)
        {
            var packageName = GetPackageBaseName(entry.Key);
            if (!byName.TryGetValue(packageName, out var namedPackages))
                byName[packageName] = namedPackages = [];
            namedPackages.Add(entry);

            foreach (var provide in SplitDependencyExpression(entry.Value.Provides, ','))
            {
                var provided = ParseDependencyAlternative(provide);
                if (provided is null) continue;
                if (!byProvide.TryGetValue(provided.Name, out var providers))
                    byProvide[provided.Name] = providers = [];
                providers.Add(entry);
            }
        }

        return new InstalledDependencyIndex(byName, byProvide);
    }

    private static IEnumerable<KeyValuePair<string, InstalledPackage>> GetInstalledCandidates(
        InstalledDependencyIndex index,
        DependencyAlternative dependency)
    {
        if (index.ByName.TryGetValue(dependency.Name, out var namedPackages))
        {
            foreach (var package in namedPackages)
                yield return package;
        }

        if (index.ByProvide.TryGetValue(dependency.Name, out var providers))
        {
            foreach (var provider in providers)
                yield return provider;
        }
    }

    private static bool InstalledPackageSatisfies(
        InstalledPackage package,
        string packageName,
        DependencyAlternative dependency)
    {
        return (PackageNameMatches(packageName, dependency.Name) &&
                VersionSatisfies(package.Version, dependency)) ||
               InstalledProvides(package.Provides, dependency);
    }

    private static bool IsAutoInstalled(string packageName, IReadOnlySet<string> autoInstalled) =>
        autoInstalled.Contains(packageName) || autoInstalled.Contains(GetPackageBaseName(packageName));

    private static string GetPackageBaseName(string packageName)
    {
        var separator = packageName.IndexOf(':');
        return separator >= 0 ? packageName[..separator] : packageName;
    }

    private static HashSet<string> ReadAutoInstalledPackages() => ReadDpmAutoInstalled();

    private static HashSet<string> ReadDpmAutoInstalled()
    {
        return ReadPackageStateFile(AutoInstalledFile);
    }

    private static HashSet<string> ReadPackageStateFile(string path)
    {
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.Ordinal);
        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void WriteDpmAutoInstalled(IEnumerable<string> packages)
        => WritePackageStateFile(AutoInstalledFile, packages);

    private static void WritePackageStateFile(string path, IEnumerable<string> packages)
    {
        Directory.CreateDirectory(DpmStateDirectory);
        var temporaryFile = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporaryFile, packages.Order(StringComparer.Ordinal));
            File.Move(temporaryFile, path, true);
        }
        finally
        {
            if (File.Exists(temporaryFile))
                File.Delete(temporaryFile);
        }
    }

    private static Dictionary<string, PackageRecord> ResolveDependencies(
        IReadOnlyList<PackageRecord> roots,
        IReadOnlyDictionary<string, InstalledPackage> installed)
    {
        var packages = LoadPackages();
        var availableByName = packages
            .GroupBy(package => package.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var availableByProvide = new Dictionary<string, List<PackageRecord>>(StringComparer.Ordinal);
        foreach (var package in packages)
        foreach (var provide in SplitDependencyExpression(package.Provides, ','))
        {
            var provided = ParseDependencyAlternative(provide);
            if (provided is null) continue;
            if (!availableByProvide.TryGetValue(provided.Name, out var providers))
                availableByProvide[provided.Name] = providers = [];
            providers.Add(package);
        }
        var selected = new Dictionary<string, PackageRecord>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            selected[root.Name] = root;
            ResolvePackage(root);
        }
        return selected;

        void ResolvePackage(PackageRecord package)
        {
            if (!visiting.Add(package.Name))
                return;
            try
            {
                foreach (var dependency in ParseDependencyGroups(package.PreDepends)
                             .Concat(ParseDependencyGroups(package.Depends)))
                {
                    var satisfiedBySelected = dependency.Any(alternative =>
                        selected.Values.Any(candidate =>
                            PackageSatisfies(candidate, alternative)));
                    if (satisfiedBySelected)
                        continue;

                    var satisfiedByInstalled = dependency.Any(alternative =>
                        installed.Any(entry =>
                            !selected.Values.Any(package => PackageNameMatches(entry.Key, package.Name)) &&
                            ((PackageNameMatches(entry.Key, alternative.Name) &&
                              VersionSatisfies(entry.Value.Version, alternative)) ||
                             InstalledProvides(entry.Value.Provides, alternative))));
                    if (satisfiedByInstalled)
                        continue;

                    PackageRecord? candidate = null;
                    foreach (var alternative in dependency)
                    {
                        var alternatives = new List<PackageRecord>();
                        if (availableByName.TryGetValue(alternative.Name, out var namedPackages))
                            alternatives.AddRange(namedPackages);
                        if (availableByProvide.TryGetValue(alternative.Name, out var providers))
                            alternatives.AddRange(providers);

                        candidate = alternatives
                            .Where(package => PackageSatisfies(package, alternative))
                            .DistinctBy(package => $"{package.Name}\n{package.Version}\n{package.Filename}")
                            .Aggregate<PackageRecord, PackageRecord?>(null, (best, current) =>
                                best is null || CompareVersions(current.Version, best.Version) > 0 ? current : best);
                        if (candidate is not null) break;
                    }
                    if (candidate is null)
                    {
                        var expression = string.Join(" | ", dependency.Select(FormatDependency));
                        throw new InvalidOperationException(
                            $"Cannot resolve dependency '{expression}' required by {package.Name} {package.Version}.");
                    }

                    if (selected.TryGetValue(candidate.Name, out var alreadySelected))
                    {
                        if (!dependency.Any(alternative => PackageSatisfies(alreadySelected, alternative)))
                            throw new InvalidOperationException(
                                $"Conflicting dependency requirements select incompatible versions of {candidate.Name}.");
                    }
                    else
                    {
                        selected[candidate.Name] = candidate;
                        ResolvePackage(candidate);
                    }
                }
            }
            finally
            {
                visiting.Remove(package.Name);
            }
        }
    }

    private static List<PackageChange> CreatePackageChanges(
        IReadOnlyDictionary<string, PackageRecord> plan,
        IReadOnlyDictionary<string, InstalledPackage> installed)
    {
        return plan.Values
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .Select(package =>
            {
                var current = installed.FirstOrDefault(entry => PackageNameMatches(entry.Key, package.Name));
                return new PackageChange(
                    package.Name,
                    current.Key is null ? null : current.Value.Version,
                    package.Version);
            })
            .ToList();
    }

    private static IEnumerable<List<DependencyAlternative>> ParseDependencyGroups(string expression)
    {
        foreach (var group in SplitDependencyExpression(expression, ','))
        {
            var alternatives = SplitDependencyExpression(group, '|')
                .Select(ParseDependencyAlternative)
                .Where(alternative => alternative is not null)
                .Cast<DependencyAlternative>()
                .ToList();
            if (alternatives.Count > 0)
                yield return alternatives;
        }
    }

    private static IEnumerable<string> SplitDependencyExpression(string expression, char separator)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '(') depth++;
            else if (expression[i] == ')') depth--;
            else if (expression[i] == separator && depth == 0)
            {
                yield return expression[start..i].Trim();
                start = i + 1;
            }
        }
        if (start < expression.Length)
            yield return expression[start..].Trim();
    }

    private static DependencyAlternative? ParseDependencyAlternative(string expression)
    {
        var value = expression.Trim();
        if (value.Length == 0) return null;
        var versionStart = value.IndexOf('(');
        string? relation = null;
        string? version = null;
        if (versionStart >= 0)
        {
            var versionEnd = value.IndexOf(')', versionStart + 1);
            if (versionEnd < 0)
                throw new InvalidDataException($"Malformed package dependency: {expression}");
            var versionFields = value[(versionStart + 1)..versionEnd]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (versionFields.Length != 2)
                throw new InvalidDataException($"Malformed package version constraint: {expression}");
            relation = versionFields[0];
            version = versionFields[1];
            value = value[..versionStart].Trim();
        }

        var qualifier = value.IndexOf(':');
        var name = qualifier >= 0 ? value[..qualifier] : value;
        if (name.Length == 0 || name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.')))
            throw new InvalidDataException($"Malformed package dependency: {expression}");
        return new DependencyAlternative(name, relation, version);
    }

    private static bool PackageSatisfies(PackageRecord package, DependencyAlternative dependency)
    {
        if (PackageNameMatches(package.Name, dependency.Name) &&
            VersionSatisfies(package.Version, dependency))
            return true;

        foreach (var provide in SplitDependencyExpression(package.Provides, ','))
        {
            var provided = ParseDependencyAlternative(provide);
            if (provided is null || !PackageNameMatches(provided.Name, dependency.Name))
                continue;
            if (dependency.Relation is null)
                return true;
            if (provided.Version is not null && VersionSatisfies(provided.Version, dependency))
                return true;
        }
        return false;
    }

    private static bool InstalledProvides(string provides, DependencyAlternative dependency)
    {
        foreach (var provide in SplitDependencyExpression(provides, ','))
        {
            var provided = ParseDependencyAlternative(provide);
            if (provided is null || !PackageNameMatches(provided.Name, dependency.Name))
                continue;
            if (dependency.Relation is null)
                return true;
            if (provided.Version is not null && VersionSatisfies(provided.Version, dependency))
                return true;
        }
        return false;
    }

    private static bool VersionSatisfies(string version, DependencyAlternative dependency)
    {
        if (dependency.Relation is null || dependency.Version is null)
            return true;
        var comparison = CompareVersions(version, dependency.Version);
        return dependency.Relation switch
        {
            "=" or "==" => comparison == 0,
            "<<" or "<" => comparison < 0,
            "<=" => comparison <= 0,
            ">>" or ">" => comparison > 0,
            ">=" => comparison >= 0,
            _ => throw new InvalidDataException($"Unsupported Debian version relation '{dependency.Relation}'.")
        };
    }

    private static string FormatDependency(DependencyAlternative dependency) =>
        dependency.Relation is null || dependency.Version is null
            ? dependency.Name
            : $"{dependency.Name} ({dependency.Relation} {dependency.Version})";

    private static bool IsVersionGreater(string candidate, string current)
    {
        var result = RunProcessAsync("dpkg", ["--compare-versions", candidate, "gt", current]).GetAwaiter().GetResult();
        return result.ExitCode == 0;
    }

    private static bool ConfirmPackageChanges(string action, IReadOnlyList<PackageChange> changes)
    {
        Console.WriteLine($"下列软件包将会{action}：");
        foreach (var change in changes)
        {
            var versions = change.CurrentVersion is null
                ? change.TargetVersion
                : change.TargetVersion is null
                    ? change.CurrentVersion
                    : $"{change.CurrentVersion} -> {change.TargetVersion}";
            var reason = change.Reason is null ? "" : $" ({change.Reason})";
            Console.WriteLine($"  {change.Name,-36} {versions}{reason}");
        }
        Console.WriteLine($"共 {changes.Count} 个软件包。");

        return ConfirmAction("是否继续？[y/N] ");
    }

    private static bool ConfirmAction(string prompt)
    {
        if (Confirmation == ConfirmationMode.Yes)
        {
            Console.WriteLine("已指定 -y，自动确认。");
            return true;
        }
        if (Confirmation == ConfirmationMode.No)
        {
            Console.WriteLine("已指定 -n，自动取消。");
            return false;
        }
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("检测到非交互输入，默认取消操作。");
            return false;
        }

        Console.Write(prompt);
        var response = Console.ReadLine()?.Trim();
        if (response is "y" or "Y" or "yes" or "YES" or "是")
            return true;
        Console.WriteLine("已取消。");
        return false;
    }

    private static async Task DownloadAsync(string packageName)
    {
        var candidate = SelectCandidate(packageName);
        if (candidate is null) throw new InvalidOperationException($"Package '{packageName}' was not found in the package indexes.");
        var file = (await DownloadPackagesAsync([candidate])).Single();
        Console.WriteLine($"已下载: {file}");
    }

    private static void ClearPackageCache()
    {
        var cachedPackages = Directory.EnumerateFiles(PackageDirectory, "*.deb", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .ToList();
        var totalBytes = cachedPackages.Sum(file => file.Length);
        Console.WriteLine($"将清理 {cachedPackages.Count} 个已下载软件包，释放 {FormatBytes(totalBytes)}。");
        if (!ConfirmAction("是否继续？[y/N] "))
            return;

        foreach (var file in cachedPackages)
            file.Delete();
        Console.WriteLine($"已清理软件包缓存，共删除 {cachedPackages.Count} 个文件。");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }

    private static async Task<List<string>> DownloadPackagesAsync(IEnumerable<PackageRecord> packages)
    {
        var packageList = packages.ToList();
        var cachedFiles = new Dictionary<PackageRecord, string>();
        var pending = new List<PackageRecord>();
        foreach (var package in packageList)
        {
            var destination = GetPackageCachePath(package);
            if (File.Exists(destination) && await HasHashAsync(destination, package.Sha256))
                cachedFiles[package] = destination;
            else
                pending.Add(package);
        }

        var progress = pending.Count == 0 ? null : new PackageDownloadProgress(pending);
        foreach (var package in pending)
            cachedFiles[package] = await DownloadPackageAsync(package, progress);

        return packageList.Select(package => cachedFiles[package]).ToList();
    }

    private static async Task<string> DownloadPackageAsync(
        PackageRecord package,
        PackageDownloadProgress? progress = null)
    {
        var destination = GetPackageCachePath(package);
        if (File.Exists(destination) && await HasHashAsync(destination, package.Sha256))
            return destination;

        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            var relative = GetPackageRelativePath(package);
            var uri = new Uri(EnsureTrailingSlash(package.RepositoryUri) + relative);
            var downloadedBytes = await DownloadPackageWithProgressAsync(uri, temp, package, progress);
            if (!await HasHashAsync(temp, package.Sha256))
                throw new InvalidDataException($"SHA256 mismatch for package {package.Name}; downloaded file was rejected.");
            File.Move(temp, destination, true);
            progress?.CompletePackage(downloadedBytes);
            return destination;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string GetPackageCachePath(PackageRecord package)
    {
        var relative = GetPackageRelativePath(package);
        return Path.Combine(PackageDirectory, Path.GetFileName(relative));
    }

    private static string GetPackageRelativePath(PackageRecord package)
    {
        if (string.IsNullOrWhiteSpace(package.Filename) || string.IsNullOrWhiteSpace(package.Sha256))
            throw new InvalidDataException($"Package {package.Name} is missing its filename or SHA256.");
        var relative = package.Filename.Replace('\\', '/');
        if (relative.StartsWith("./", StringComparison.Ordinal))
            relative = relative[2..];
        if (relative.StartsWith('/') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe package path in index for {package.Name}.");
        return relative;
    }

    private static async Task<long> DownloadPackageWithProgressAsync(
        Uri uri,
        string path,
        PackageRecord package,
        PackageDownloadProgress? progress)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Download failed for {uri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long downloaded = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastUpdate = TimeSpan.Zero;
        progress?.StartPackage(package, totalBytes);
        RenderDownloadProgress(package.Name, downloaded, totalBytes, stopwatch.Elapsed, progress, totalBytes is 0);

        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            if (stopwatch.Elapsed - lastUpdate >= TimeSpan.FromMilliseconds(200))
            {
                RenderDownloadProgress(package.Name, downloaded, totalBytes, stopwatch.Elapsed, progress);
                lastUpdate = stopwatch.Elapsed;
            }
        }

        await output.FlushAsync();
        stopwatch.Stop();
        RenderDownloadProgress(package.Name, downloaded, totalBytes, stopwatch.Elapsed, progress, true);
        return downloaded;
    }

    private static void RenderDownloadProgress(
        string packageName,
        long downloadedBytes,
        long? totalBytes,
        TimeSpan elapsed,
        PackageDownloadProgress? progress,
        bool complete = false)
    {
        var packageMessage = FormatPackageDownloadProgress(packageName, downloadedBytes, totalBytes, elapsed, complete);
        if (progress is null)
            WriteProgressLine(packageMessage, complete);
        else
            progress.Render(packageMessage, downloadedBytes);
    }

    private static string FormatPackageDownloadProgress(
        string packageName,
        long downloadedBytes,
        long? totalBytes,
        TimeSpan elapsed,
        bool complete)
    {
        var percent = totalBytes is > 0
            ? Math.Clamp((int)(downloadedBytes * 100L / totalBytes.Value), 0, complete ? 100 : 99)
            : complete ? 100 : 0;
        const int width = 16;
        var filled = percent * width / 100;
        var bar = new string('#', filled) + new string(' ', width - filled);
        var seconds = elapsed.TotalSeconds;
        var speed = seconds > 0 ? downloadedBytes / seconds / 1_000_000d : 0d;
        var remaining = totalBytes is > 0 && speed > 0
            ? $"{Math.Ceiling(Math.Max(0, totalBytes.Value - downloadedBytes) / (speed * 1_000_000d)):0}s"
            : totalBytes is > 0 ? "--" : "?";
        return $"{packageName} {speed:0.00} MB/s [{bar}] {percent}% {remaining}";
    }

    private static void WriteProgressLine(string message, bool complete)
    {
        if (Console.IsOutputRedirected)
            Console.WriteLine(message);
        else
            Console.Write($"\r{message}\u001b[K");
        if (complete)
            Console.WriteLine();
    }

    private static PackageRecord? SelectCandidate(string name) =>
        SelectNewest(LoadPackages().Where(p => PackageNameMatches(p.Name, name)));

    private static PackageRecord? SelectNewest(IEnumerable<PackageRecord> packages)
    {
        PackageRecord? selected = null;
        foreach (var package in packages)
        {
            if (selected is null || CompareVersions(package.Version, selected.Version) > 0)
                selected = package;
        }
        return selected;
    }

    private static int CompareVersions(string left, string right)
    {
        var result = RunProcessAsync("dpkg", ["--compare-versions", left, "gt", right]).GetAwaiter().GetResult();
        if (result.ExitCode == 0) return 1;
        result = RunProcessAsync("dpkg", ["--compare-versions", left, "lt", right]).GetAwaiter().GetResult();
        return result.ExitCode == 0 ? -1 : 0;
    }

    private static List<PackageRecord> LoadPackages()
    {
        var result = new List<PackageRecord>();
        if (!Directory.Exists(IndexDirectory)) return result;
        foreach (var index in Directory.EnumerateFiles(IndexDirectory, "*.Packages", SearchOption.AllDirectories))
        {
            var sourceFile = Path.ChangeExtension(index, ".source");
            if (!File.Exists(sourceFile)) continue;
            var sourceUri = File.ReadAllText(sourceFile).Trim();
            foreach (var fields in ParseParagraphs(File.ReadAllText(index)))
            {
                if (!fields.TryGetValue("Package", out var name) ||
                    !fields.TryGetValue("Version", out var version) ||
                    !fields.TryGetValue("Filename", out var filename))
                    continue;
                fields.TryGetValue("SHA256", out var hash);
                var size = fields.TryGetValue("Size", out var sizeText) &&
                           long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize) &&
                           parsedSize >= 0
                    ? parsedSize
                    : (long?)null;
                fields.TryGetValue("Description", out var description);
                fields.TryGetValue("Pre-Depends", out var preDepends);
                fields.TryGetValue("Depends", out var depends);
                fields.TryGetValue("Provides", out var provides);
                result.Add(new PackageRecord(name, version, filename, hash ?? "", description ?? "",
                    sourceUri, preDepends ?? "", depends ?? "", provides ?? "", size));
            }
        }
        return result;
    }

    private static IEnumerable<Dictionary<string, string>> ParseParagraphs(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (fields.Count > 0) yield return fields;
                fields = new Dictionary<string, string>(StringComparer.Ordinal);
                current = null;
            }
            else if (char.IsWhiteSpace(line[0]) && current is not null)
                fields[current] += "\n" + line.TrimStart();
            else
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                current = line[..colon];
                fields[current] = line[(colon + 1)..].Trim();
            }
        }
        if (fields.Count > 0) yield return fields;
    }

    private static void Search(string term)
    {
        var matches = LoadPackages()
            .Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        p.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(group => SelectNewest(group)!)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Take(200)
            .ToList();
        foreach (var package in matches)
            Console.WriteLine($"{package.Name,-36} {package.Version,-18} {package.Description.Split('\n')[0]}");
        Console.WriteLine($"找到 {matches.Count} 个软件包。");
    }

    private static async Task<Dictionary<string, InstalledPackage>> GetInstalledAsync()
    {
        var result = await RunProcessAsync("dpkg-query",
            ["-W", "-f=${binary:Package}\\t${Version}\\t${db:Status-Status}\\t${Provides}\\t${Pre-Depends}\\t${Depends}\\n"]);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not query installed packages.");
        var installed = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t', 6);
            if (fields.Length == 6 && fields[2] == "installed")
                installed[fields[0]] = new InstalledPackage(fields[1], fields[3], fields[4], fields[5]);
        }
        return installed;
    }

    private static async Task RunDpkgAsync(
        IReadOnlyList<string> arguments,
        int expectedPackageCount = 0,
        bool showProgress = false)
    {
        var start = new ProcessStartInfo("dpkg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = showProgress,
            RedirectStandardError = showProgress
        };
        if (showProgress)
            start.ArgumentList.Add("--status-fd=1");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dpkg.");
        Task<string>? errorTask = showProgress ? process.StandardError.ReadToEndAsync() : null;
        if (showProgress)
            await ReadDpkgProgressAsync(process, expectedPackageCount);
        await process.WaitForExitAsync();
        var errorOutput = errorTask is null ? "" : await errorTask;
        if (showProgress && process.ExitCode == 0)
            RenderDpkgProgress(expectedPackageCount, expectedPackageCount, null, "完成", true);
        else if (showProgress && !Console.IsOutputRedirected)
            Console.WriteLine();
        if (process.ExitCode != 0)
        {
            var detail = string.Join(' ', errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
            var message = $"dpkg 执行失败，退出码 {process.ExitCode}.";
            if (detail.Length > 0) message += $" 详情: {detail}";
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ReadDpkgProgressAsync(Process process, int totalPackages)
    {
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var removing = false;
        string? currentPackage = null;
        string? currentStage = null;

        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("processing: ", StringComparison.Ordinal))
            {
                var fields = line["processing: ".Length..].Split(':', 2);
                if (fields.Length == 2)
                {
                    currentStage = fields[0].Trim();
                    currentPackage = fields[1].Trim();
                    removing = currentStage is "remove" or "purge";
                    RenderDpkgProgress(completed.Count, totalPackages, currentPackage, currentStage);
                }
            }
            else if (line.StartsWith("status: ", StringComparison.Ordinal))
            {
                var fields = line["status: ".Length..].Split(':', 2);
                if (fields.Length == 2)
                {
                    var package = fields[0].Trim();
                    var status = fields[1].Trim();
                    if ((!removing && status == "installed") ||
                        (removing && status is "config-files" or "not-installed"))
                    {
                        completed.Add(package);
                        currentPackage = package;
                        RenderDpkgProgress(completed.Count, totalPackages, currentPackage, currentStage);
                    }
                }
            }
        }

    }

    private static void RenderDpkgProgress(
        int completed,
        int total,
        string? package,
        string? stage,
        bool forceComplete = false)
    {
        if (total <= 0) return;
        var percent = forceComplete ? 100 : Math.Clamp(completed * 100 / total, 0, 99);
        var width = 20;
        var filled = percent * width / 100;
        var bar = new string('#', filled) + new string('-', width - filled);
        var detail = string.Join(' ', new[] { package, stage }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var message = $"正在运行 [{bar}] {percent,3}% ({completed}/{total} 个软件包完成) {detail}";
        if (Console.IsOutputRedirected)
            Console.WriteLine(message);
        else
            Console.Write($"\r{message}\u001b[K");

        if (forceComplete || percent == 100)
            Console.WriteLine();
    }

    private static async Task DecompressAsync(string command, IReadOnlyList<string> arguments, string destination)
    {
        using var output = File.Create(destination);
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        if (process.ExitCode != 0) throw new InvalidDataException($"{command} failed: {stderr}");
    }

    private static async Task<ProcessResult> RunProcessAsync(string command, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task DownloadToFileAsync(Uri uri, string path)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Download failed for {uri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output);
    }

    private static async Task<bool> TryDownloadToFileAsync(Uri uri, string path)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Download failed for {uri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output);
        return true;
    }

    private static async Task<bool> HasHashAsync(string path, string expected)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected.ToLowerInvariant()));
    }

    private static List<RepositorySource> ReadSources()
    {
        var result = new List<RepositorySource>();
        if (!Directory.Exists(SourceDirectory)) return result;
        foreach (var file in Directory.EnumerateFiles(SourceDirectory).Where(f => f.EndsWith(".list", StringComparison.Ordinal)))
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "deb")
                throw new InvalidDataException($"Unsupported source entry in {file}: {line}");
            var start = 1;
            string? configuredArchitecture = null;
            if (parts[start].StartsWith('['))
            {
                var end = Array.FindIndex(parts, start, p => p.EndsWith(']'));
                if (end < 0 || end == start) throw new InvalidDataException($"Malformed source options in {file}: {line}");
                var option = parts.Skip(start).Take(end - start + 1).Select(p => p.Trim('[', ']')).FirstOrDefault(p => p.StartsWith("arch=", StringComparison.Ordinal));
                if (option is not null) configuredArchitecture = option["arch=".Length..];
                start = end + 1;
            }
            if (parts.Length < start + 2 || !Uri.TryCreate(parts[start], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
                throw new InvalidDataException($"Invalid source URI in {file}: {line}");
            var isFlat = parts[start + 1] == "./";
            if (!isFlat && parts.Length < start + 3)
                throw new InvalidDataException($"Missing repository components in {file}: {line}");
            if (isFlat && parts.Length != start + 2)
                throw new InvalidDataException($"Flat repository entries must not specify components in {file}: {line}");
            result.Add(new RepositorySource(
                uri.ToString(),
                parts[start + 1],
                isFlat ? [] : parts.Skip(start + 2).ToList(),
                configuredArchitecture?.Split(',', StringSplitOptions.RemoveEmptyEntries),
                isFlat));
        }
        return result;
    }

    private static List<string> GetKeyrings()
    {
        var directories = new[] { "/etc/apt/trusted.gpg.d", "/usr/share/keyrings" };
        var keyrings = directories.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d))
            .Where(p => p.EndsWith(".gpg", StringComparison.Ordinal))
            .ToList();
        if (File.Exists("/etc/apt/trusted.gpg")) keyrings.Add("/etc/apt/trusted.gpg");
        return keyrings;
    }

    private static async Task<string> GetArchitectureAsync()
    {
        var result = await RunProcessAsync("dpkg", ["--print-architecture"]);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not determine system architecture.");
        return result.Output.Trim();
    }

    private static bool PackageNameMatches(string indexed, string requested) =>
        indexed.Equals(requested, StringComparison.Ordinal) ||
        indexed.StartsWith(requested + ":", StringComparison.Ordinal);

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private static uint GetEffectiveUid() => geteuid();

    private sealed class PackageDownloadProgress
    {
        private readonly int _packageCount;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long? _totalBytes;
        private long _completedBytes;
        private long _currentBytes;
        private long? _currentExpectedBytes;
        private int _completedPackages;
        private string _currentPackageLine = "";
        private bool _started;

        public PackageDownloadProgress(IReadOnlyList<PackageRecord> packages)
        {
            _packageCount = packages.Count;
            if (packages.All(package => package.Size.HasValue))
                _totalBytes = packages.Sum(package => package.Size!.Value);
        }

        public void StartPackage(PackageRecord package, long? responseBytes)
        {
            _currentBytes = 0;
            _currentExpectedBytes = responseBytes is >= 0 ? responseBytes : package.Size;
            if (_totalBytes is long total && package.Size is long indexedSize &&
                _currentExpectedBytes is long expectedSize)
            {
                _totalBytes = Math.Max(0, total - indexedSize + expectedSize);
            }
        }

        public void Render(string packageLine, long downloadedBytes)
        {
            _currentPackageLine = packageLine;
            _currentBytes = downloadedBytes;
            Draw();
        }

        public void CompletePackage(long downloadedBytes)
        {
            if (_totalBytes is long total && _currentExpectedBytes is long expectedSize)
                _totalBytes = Math.Max(0, total - expectedSize + downloadedBytes);
            _completedBytes += downloadedBytes;
            _currentBytes = 0;
            _completedPackages++;
            _currentPackageLine = "";
            Draw();
            if (_completedPackages == _packageCount && !Console.IsOutputRedirected)
                Console.WriteLine();
        }

        private void Draw()
        {
            var bytesTransferred = _completedBytes + _currentBytes;
            var percent = _totalBytes is > 0
                ? Math.Clamp((int)(bytesTransferred * 100d / _totalBytes.Value), 0, 99)
                : Math.Clamp(_completedPackages * 100 / _packageCount, 0, 99);
            if (_completedPackages == _packageCount)
                percent = 100;

            const int width = 20;
            var filled = percent * width / 100;
            var bar = new string('#', filled) + new string(' ', width - filled);
            var seconds = _stopwatch.Elapsed.TotalSeconds;
            var speed = seconds > 0 ? bytesTransferred / seconds / 1_000_000d : 0d;
            var remaining = _totalBytes is long totalBytes && speed > 0
                ? Math.Ceiling(Math.Max(0, totalBytes - bytesTransferred) / (speed * 1_000_000d))
                : _completedPackages == _packageCount ? 0 : (double?)null;
            var eta = remaining is double secondsRemaining
                ? FormatRemainingTime(secondsRemaining)
                : "?";
            var summary = $"总共[{_completedPackages}/{_packageCount}] [{bar}] {percent}% {speed:0.00} MB/s {eta}";

            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(_currentPackageLine);
                Console.WriteLine(summary);
            }
            else if (!_started)
            {
                Console.WriteLine(_currentPackageLine);
                Console.Write(summary);
                _started = true;
            }
            else
                Console.Write($"\u001b[1A\r{_currentPackageLine}\u001b[K\n\r{summary}\u001b[K");
        }

        private static string FormatRemainingTime(double seconds)
        {
            var totalSeconds = (long)Math.Ceiling(seconds);
            var hours = totalSeconds / 3600;
            var minutes = totalSeconds % 3600 / 60;
            var remainingSeconds = totalSeconds % 60;
            return $"{hours}h {minutes}m {remainingSeconds}s";
        }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint geteuid();

    private sealed record RepositorySource(
        string Uri,
        string Suite,
        List<string> Components,
        string[]? Architectures,
        bool IsFlat);
    private sealed record IndexCacheMetadata(
        string SourceUri,
        string Suite,
        string Component,
        string Architecture,
        string IndexPath,
        string Sha256);
    private sealed record PackageRecord(
        string Name,
        string Version,
        string Filename,
        string Sha256,
        string Description,
        string RepositoryUri,
        string PreDepends,
        string Depends,
        string Provides,
        long? Size);
    private sealed record DependencyAlternative(string Name, string? Relation, string? Version);
    private sealed record PackageChange(string Name, string? CurrentVersion, string? TargetVersion, string? Reason = null);
    private sealed record InstalledPackage(string Version, string Provides, string PreDepends, string Depends);
    private sealed record InstalledDependencyIndex(
        IReadOnlyDictionary<string, List<KeyValuePair<string, InstalledPackage>>> ByName,
        IReadOnlyDictionary<string, List<KeyValuePair<string, InstalledPackage>>> ByProvide);
    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private enum ConfirmationMode
    {
        Prompt,
        Yes,
        No
    }
}
