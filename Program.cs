using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private const string SourceDirectory = "/etc/dpm/sources.list.d";
    private const string CacheDirectory = "/var/cache/dpm";
    private const string IndexDirectory = CacheDirectory + "/list";
    private const string PackageDirectory = CacheDirectory + "/deb";
    private const string DpmStateDirectory = "/var/lib/dpm";
    private const string AutoInstalledFile = DpmStateDirectory + "/auto-installed";
    private const string DpmLockFile = "/run/lock/dpm.lock";
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const string GitHubRepository = "zizhao-7891/dpm";
    private const string LatestReleaseApi = "https://api.github.com/repos/" + GitHubRepository + "/releases/latest";
    private const string GitHubProxyPrefix = "https://gh-proxy.org/";
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
    private static List<PackageRecord>? PackageCache;

    private static async Task<int> Main(string[] args)
    {
        var assumeYes = args.Any(argument => argument is "-y" or "--yes");
        var assumeNo = args.Any(argument => argument is "-n" or "--no");
        var fixBroken = args.Any(argument => argument is "-f" or "-fix" or "--fix-broken");
        if (assumeYes && assumeNo)
        {
            Console.Error.WriteLine("Error: -y and -n cannot be used together.");
            return 2;
        }
        Confirmation = assumeYes
            ? ConfirmationMode.Yes
            : assumeNo ? ConfirmationMode.No : ConfirmationMode.Prompt;
        args = args.Where(argument =>
            argument is not ("-y" or "--yes" or "-n" or "--no" or "-f" or "-fix" or "--fix-broken"))
            .ToArray();
        if (fixBroken && args.Length == 0)
            args = ["fix"];
        if (fixBroken && args[0] is not ("install" or "fix" or "fix-broken"))
        {
            Console.Error.WriteLine("Error: -f/--fix-broken can only be used with install or fix.");
            return 2;
        }

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
            using var operationLock = AcquireDpmLock();
            Directory.CreateDirectory(IndexDirectory);
            Directory.CreateDirectory(PackageDirectory);
            switch (args[0])
            {
                case "update":
                case "up":
                    RequireCount(args, 1);
                    await UpdateAsync();
                    break;
                case "check-sources":
                    RequireCount(args, 1);
                    await CheckSourcesAsync();
                    break;
                case "install":
                    if (args.Length < 2 && !fixBroken)
                        throw new ArgumentException("Usage: dpm install PACKAGE [PACKAGE ...]");
                    await UpdateAsync();
                    await InstallAsync(args[1..], fixBroken);
                    break;
                case "fix":
                case "fix-broken":
                    if (args.Length > 1) throw new ArgumentException("Usage: dpm fix");
                    await UpdateAsync();
                    await InstallAsync([], true);
                    break;
                case "reinstall":
                    RequireCount(args, 2);
                    await UpdateAsync();
                    await ReinstallAsync(args[1]);
                    break;
                case "remove":
                case "uninstall":
                    if (args.Length < 2) throw new ArgumentException("Usage: dpm remove PACKAGE [PACKAGE ...]");
                    await RemoveAsync(args[1..], false);
                    break;
                case "purge":
                    if (args.Length < 2) throw new ArgumentException("Usage: dpm purge PACKAGE [PACKAGE ...]");
                    await RemoveAsync(args[1..], true);
                    break;
                case "upgrade":
                    if (args.Length > 2) throw new ArgumentException("Usage: dpm upgrade [package]");
                    await UpgradeAsync(args.Length == 2 ? args[1] : null);
                    break;
                case "upgrade-dpm":
                    RequireCount(args, 1);
                    await UpgradeDpmAsync();
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

    private static FileStream AcquireDpmLock()
    {
        var stream = new FileStream(
            DpmLockFile,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        if (flock(stream.SafeFileHandle, LockExclusive | LockNonBlocking) == 0)
            return stream;

        var error = Marshal.GetLastPInvokeError();
        stream.Dispose();
        if (error is 11 or 35)
            throw new InvalidOperationException(
                "Another dpm process is already running. Wait for it to finish before starting another operation.");
        throw new IOException($"Could not acquire dpm lock file '{DpmLockFile}' (errno {error}).");
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int flock(SafeFileHandle fileDescriptor, int operation);

    private static void PrintUsage()
    {
        Console.WriteLine("""
            dpm - Debian package manager
            Usage:
              dpm [-y | -n] COMMAND [ARGS...]
              dpm update
              dpm up                    (alias for update)
              dpm check-sources         (check configured source suite, architecture, and indexes)
              dpm install PACKAGE|FILE.deb [PACKAGE|FILE.deb ...] (updates indexes before installing)
              dpm fix                   (repair missing dependencies and configure pending packages)
              dpm -f | -fix             (alias for fix)
              dpm -f install PACKAGE... (repair existing dependencies and install packages)
              dpm reinstall PACKAGE    (reinstalls the installed package version)
              dpm remove PACKAGE [PACKAGE ...]
              dpm uninstall PACKAGE [PACKAGE ...] (alias for remove)
              dpm purge PACKAGE [PACKAGE ...] (remove packages and their configuration files)
              dpm upgrade [PACKAGE]
              dpm upgrade-dpm           (upgrade dpm from the latest GitHub release)
              dpm check                 (dpkg consistency audit)
              dpm check-filesystem      (verify installed package files)
              dpm download PACKAGE
              dpm clear                 (clear downloaded .deb cache)
              dpm search TERM
            Options:
              -y, --yes                 automatically confirm package/cache changes
              -n, --no                  automatically cancel package/cache changes
              -f, -fix, --fix-broken    repair broken dependencies
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

    private static async Task CheckSourcesAsync()
    {
        var sources = ReadSources();
        if (sources.Count == 0)
            throw new InvalidOperationException($"No deb sources found in {SourceDirectory}.");

        var architecture = await GetArchitectureAsync();
        var keyrings = GetKeyrings();
        if (keyrings.Count == 0)
            throw new InvalidOperationException("No APT signing keyrings found; refusing to use unsigned repository metadata.");

        var applicableSources = sources
            .Where(source => source.Architectures is null ||
                             source.Architectures.Contains(architecture, StringComparer.Ordinal))
            .ToList();
        if (applicableSources.Count == 0)
            throw new InvalidOperationException($"No configured repository supports architecture {architecture}.");

        var failures = 0;
        foreach (var source in applicableSources)
        {
            try
            {
                await ValidateSourceAsync(source, architecture, keyrings);
                Console.WriteLine($"可用: {source.Uri} {source.Suite}");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                                       InvalidOperationException or TaskCanceledException)
            {
                failures++;
                Console.Error.WriteLine($"不可用: {source.Uri} {source.Suite}: {ex.Message}");
            }
        }

        Console.WriteLine($"软件源判定完成：{applicableSources.Count - failures} 个可用，{failures} 个不可用。");
        if (failures > 0)
            throw new InvalidOperationException($"{failures} configured software source(s) failed validation.");
    }

    private static async Task ValidateSourceAsync(
        RepositorySource source,
        string architecture,
        IReadOnlyList<string> keyrings)
    {
        var distUri = source.IsFlat
            ? EnsureTrailingSlash(source.Uri)
            : EnsureTrailingSlash(source.Uri) + "dists/" + Uri.EscapeDataString(source.Suite) + "/";
        var temp = Path.Combine(Path.GetTempPath(), "dpm-check-" + Guid.NewGuid().ToString("N"));
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
            if (!source.IsFlat)
            {
                var releaseSuite = GetReleaseField(releaseText, "Suite");
                var codename = GetReleaseField(releaseText, "Codename");
                if (!SuiteMatches(source.Suite, releaseSuite, codename))
                    throw new InvalidDataException(
                        $"configured suite '{source.Suite}' does not match signed Release Suite '{releaseSuite ?? "(missing)"}' or Codename '{codename ?? "(missing)"}'.");

                var releaseArchitectures = GetReleaseField(releaseText, "Architectures");
                if (releaseArchitectures is not null &&
                    !releaseArchitectures.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Contains(architecture, StringComparer.Ordinal))
                    throw new InvalidDataException(
                        $"signed Release metadata does not support architecture {architecture}.");

                var releaseComponents = GetReleaseField(releaseText, "Components");
                if (releaseComponents is not null)
                {
                    var declaredComponents = releaseComponents.Split(
                        (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var missingComponents = source.Components
                        .Where(component =>
                            !declaredComponents.Contains(component, StringComparer.Ordinal) &&
                            !declaredComponents.Contains("updates/" + component, StringComparer.Ordinal))
                        .ToArray();
                    if (missingComponents.Length > 0)
                        throw new InvalidDataException(
                            $"signed Release metadata does not provide configured component(s): {string.Join(", ", missingComponents)}.");
                }
            }

            foreach (var component in source.IsFlat ? new string?[] { null } : source.Components.Select(value => (string?)value))
            {
                var indexPaths = source.IsFlat
                    ? new[] { "Packages.xz", "Packages.gz", "Packages" }
                    : new[]
                    {
                        $"{component}/binary-{architecture}/Packages.xz",
                        $"{component}/binary-{architecture}/Packages.gz",
                        $"{component}/binary-{architecture}/Packages"
                    };
                if (!indexPaths.Any(checksums.ContainsKey))
                    throw new InvalidDataException(source.IsFlat
                        ? $"signed Release metadata has no flat package index for {source.Uri}."
                        : $"signed Release metadata has no package index for {component}/{architecture}.");
            }
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static string? GetReleaseField(string release, string field)
    {
        var prefix = field + ":";
        foreach (var line in release.Split('\n'))
        {
            var normalized = line.TrimEnd('\r');
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                return normalized[prefix.Length..].Trim();
        }
        return null;
    }

    private static bool SuiteMatches(string configuredSuite, string? releaseSuite, string? codename)
    {
        if (configuredSuite.Equals(releaseSuite, StringComparison.Ordinal) ||
            configuredSuite.Equals(codename, StringComparison.Ordinal))
            return true;
        return codename is not null &&
               configuredSuite.StartsWith(codename + "-", StringComparison.Ordinal);
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

    private static async Task InstallAsync(
        IReadOnlyList<string> packageArguments,
        bool fixBroken = false)
    {
        var requestedRoots = new List<PackageRecord>(packageArguments.Count);
        foreach (var packageArgument in packageArguments)
        {
            var root = await LoadLocalPackageAsync(packageArgument) ?? SelectCandidate(packageArgument);
            if (root is null)
                throw new InvalidOperationException(
                    $"Package '{packageArgument}' was not found in the package indexes.");
            requestedRoots.Add(root);
        }

        var conflictingRoots = requestedRoots
            .GroupBy(root => root.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(root => root.Version).Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (conflictingRoots is not null)
            throw new InvalidOperationException(
                $"Multiple requested versions of package '{conflictingRoots.Key}' cannot be installed in one transaction.");
        requestedRoots = requestedRoots.DistinctBy(root => root.Name, StringComparer.Ordinal).ToList();

        var installed = await GetInstalledAsync();
        var repairRoots = fixBroken ? FindBrokenDependencyRoots(installed) : [];
        var pendingConfiguration = fixBroken ? await GetPendingConfigurationAsync() : "";
        var roots = requestedRoots.Concat(repairRoots)
            .GroupBy(root => root.Name, StringComparer.Ordinal)
            .Select(group =>
            {
                if (group.Select(root => root.Version).Distinct(StringComparer.Ordinal).Skip(1).Any())
                    throw new InvalidOperationException(
                        $"Conflicting versions of package '{group.Key}' prevent automatic repair.");
                return group.First();
            })
            .ToList();

        if (roots.Count == 0 && string.IsNullOrWhiteSpace(pendingConfiguration))
        {
            Console.WriteLine(fixBroken ? "没有发现需要修复的软件包。" : "没有需要安装的软件包。");
            return;
        }

        var plan = ResolveDependencies(roots, installed);
        var orderedPlan = OrderPackagesForInstall(plan, installed);
        var changes = CreatePackageChanges(plan, installed);
        if (!string.IsNullOrWhiteSpace(pendingConfiguration))
            Console.WriteLine("dpkg 检测到尚未配置的软件包；安装依赖后将尝试配置待处理的软件包。");
        if (!ConfirmPackageChanges("安装", changes))
            return;

        if (orderedPlan.Count > 0)
        {
            var files = await DownloadPackagesAsync(orderedPlan);
            await InstallPackagesAsync(files, orderedPlan);
        }
        if (!string.IsNullOrWhiteSpace(pendingConfiguration))
            await RunDpkgAsync(["--configure", "--pending"]);

        var autoInstalled = ReadDpmAutoInstalled();
        autoInstalled.RemoveWhere(name =>
            requestedRoots.Any(root => PackageNameMatches(name, root.Name)));
        AddNewAutomaticDependencies(
            autoInstalled, plan, installed, requestedRoots.Select(root => root.Name));
        WriteDpmAutoInstalled(autoInstalled);
    }

    private static List<PackageRecord> FindBrokenDependencyRoots(
        IReadOnlyDictionary<string, InstalledPackage> installed)
    {
        var packages = LoadPackages();
        var installedIndex = BuildInstalledDependencyIndex(installed);
        var roots = new Dictionary<string, PackageRecord>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var entry in installed)
        foreach (var dependency in ParseDependencyGroups(entry.Value.PreDepends)
                     .Concat(ParseDependencyGroups(entry.Value.Depends)))
        {
            if (dependency.Any(alternative =>
                    GetInstalledCandidates(installedIndex, alternative).Any(candidate =>
                        InstalledPackageSatisfies(candidate.Value, candidate.Key, alternative))))
                continue;

            var candidate = SelectDependencyCandidate(packages, dependency);
            if (candidate is null)
            {
                var expression = string.Join(" | ", dependency.Select(FormatDependency));
                unresolved.Add($"{entry.Key}: {expression}");
                continue;
            }

            if (roots.TryGetValue(candidate.Name, out var existing) &&
                !string.Equals(existing.Version, candidate.Version, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Conflicting versions of {candidate.Name} are needed to repair installed dependencies.");
            roots[candidate.Name] = candidate;
        }

        if (unresolved.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                unresolved.Take(20).Select(dependency => $"  {dependency}"));
            if (unresolved.Count > 20)
                details += $"{Environment.NewLine}  ... 还有 {unresolved.Count - 20} 项";
            throw new InvalidOperationException(
                $"已配置的软件源无法满足以下依赖：{Environment.NewLine}{details}");
        }

        return roots.Values.ToList();
    }

    private static PackageRecord? SelectDependencyCandidate(
        IReadOnlyList<PackageRecord> packages,
        IReadOnlyList<DependencyAlternative> dependency)
    {
        foreach (var alternative in dependency)
        {
            var candidate = packages
                .Where(package => PackageSatisfies(package, alternative))
                .Aggregate<PackageRecord, PackageRecord?>(
                    null,
                    (best, current) =>
                        best is null || CompareVersions(current.Version, best.Version) > 0
                            ? current
                            : best);
            if (candidate is not null)
                return candidate;
        }

        return null;
    }

    private static async Task<string> GetPendingConfigurationAsync()
    {
        var result = await RunProcessAsync("dpkg", ["--audit"]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not audit dpkg state: {result.Error.Trim()}");
        return result.Output.Trim();
    }

    private static async Task<PackageRecord?> LoadLocalPackageAsync(string packageArgument)
    {
        var isPath = File.Exists(packageArgument) ||
                     Path.IsPathRooted(packageArgument) ||
                     packageArgument.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) ||
                     packageArgument.Contains(Path.DirectorySeparatorChar) ||
                     packageArgument.Contains(Path.AltDirectorySeparatorChar);
        if (!isPath)
            return null;

        var path = Path.GetFullPath(packageArgument);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Local package file was not found: {packageArgument}");

        var result = await RunProcessAsync("dpkg-deb", ["--field", path]);
        if (result.ExitCode != 0)
            throw new InvalidDataException(
                $"Could not read local Debian package '{packageArgument}': {result.Error.Trim()}");

        var fields = ParseParagraphs(result.Output).FirstOrDefault()
                     ?? throw new InvalidDataException($"Local package '{packageArgument}' has no control metadata.");
        if (!fields.TryGetValue("Package", out var name) ||
            !fields.TryGetValue("Version", out var version) ||
            !fields.TryGetValue("Architecture", out var packageArchitecture))
            throw new InvalidDataException(
                $"Local package '{packageArgument}' is missing Package, Version, or Architecture metadata.");

        var systemArchitecture = await GetArchitectureAsync();
        if (packageArchitecture != "all" && packageArchitecture != systemArchitecture)
            throw new InvalidDataException(
                $"Local package '{packageArgument}' is for {packageArchitecture}, but this system is {systemArchitecture}.");

        fields.TryGetValue("Description", out var description);
        fields.TryGetValue("Pre-Depends", out var preDepends);
        fields.TryGetValue("Depends", out var depends);
        fields.TryGetValue("Provides", out var provides);
        fields.TryGetValue("Recommends", out var recommends);
        fields.TryGetValue("Section", out var section);
        fields.TryGetValue("Tag", out var tags);
        var isMetaPackage = string.Equals(section, "metapackages", StringComparison.OrdinalIgnoreCase) ||
                            (tags ?? "").Split([',', ' ', '\t', '\r', '\n'],
                                StringSplitOptions.RemoveEmptyEntries)
                            .Contains("role::metapackage", StringComparer.OrdinalIgnoreCase);

        return new PackageRecord(name, version, Path.GetFileName(path), "", description ?? "",
            "", preDepends ?? "", depends ?? "", provides ?? "", recommends ?? "",
            isMetaPackage, new FileInfo(path).Length, path);
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
        var orderedPlan = OrderPackagesForInstall(plan, installed);
        var changes = CreatePackageChanges(plan, installed);
        if (!ConfirmPackageChanges("重新安装", changes))
            return;

        var files = await DownloadPackagesAsync(orderedPlan);
        await InstallPackagesAsync(files, orderedPlan);
        var autoInstalled = ReadDpmAutoInstalled();
        AddNewAutomaticDependencies(autoInstalled, plan, installed, [root.Name]);
        WriteDpmAutoInstalled(autoInstalled);
    }

    private static async Task RemoveAsync(IReadOnlyList<string> packageNames, bool purge)
    {
        var installed = await GetInstalledAsync();
        var removalSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var packageName in packageNames)
        {
            var match = installed.FirstOrDefault(entry => PackageNameMatches(entry.Key, packageName));
            if (match.Key is null)
                throw new InvalidOperationException($"Installed package '{packageName}' was not found.");
            removalSet.Add(match.Key);
        }

        var autoInstalled = ReadAutoInstalledPackages();
        var orphans = FindOrphanPackages(installed, autoInstalled, removalSet);
        removalSet.UnionWith(orphans);
        var dependents = FindBlockingDependents(installed, removalSet);
        if (dependents.Count > 0)
        {
            var details = string.Join(
                Environment.NewLine,
                dependents.Take(20).Select(dependent => $"  {dependent}"));
            var remaining = dependents.Count - 20;
            if (remaining > 0)
                details += $"{Environment.NewLine}  ... 还有 {remaining} 个软件包";
            throw new InvalidOperationException(
                $"无法移除请求的软件包：仍有未计划移除的软件包依赖它们：{Environment.NewLine}{details}");
        }

        var changes = removalSet
            .Select(name => new PackageChange(
                name,
                installed[name].Version,
                null,
                packageNames.Any(packageName => PackageNameMatches(name, packageName))
                    ? null
                    : "自动安装的孤儿依赖"))
            .OrderBy(change => change.Name, StringComparer.Ordinal)
            .ToList();
        var action = purge ? "彻底清除" : "卸载";
        if (!ConfirmPackageChanges(action, changes))
            return;

        var dpkgAction = purge ? "--purge" : "--remove";
        await RunDpkgAsync(
            [dpkgAction, "--", .. removalSet.Order(StringComparer.Ordinal)],
            removalSet.Count,
            true,
            removalSet);
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

        var upgradeCandidates = candidates
            .Select(item => item.Candidate)
            .OrderBy(package => package.Name, StringComparer.Ordinal)
            .ToArray();
        var heldBack = new List<(PackageRecord Package, string Reason)>();
        Dictionary<string, PackageRecord> plan;
        try
        {
            plan = ResolveDependencies(upgradeCandidates, installed);
        }
        catch (InvalidOperationException)
        {
            var compatibleCandidates = new List<PackageRecord>();
            AddCompatibleUpgradeBatch(upgradeCandidates);
            if (compatibleCandidates.Count == 0)
            {
                Console.WriteLine("升级候选存在依赖冲突，没有可安全升级的软件包。");
                PrintHeldBackUpgrades(heldBack);
                return;
            }

            plan = ResolveDependencies(compatibleCandidates, installed);
            PrintHeldBackUpgrades(heldBack);

            void AddCompatibleUpgradeBatch(PackageRecord[] batch)
            {
                try
                {
                    ResolveDependencies(compatibleCandidates.Concat(batch).ToList(), installed);
                    compatibleCandidates.AddRange(batch);
                }
                catch (InvalidOperationException ex)
                {
                    if (batch.Length == 1)
                    {
                        heldBack.Add((batch[0], ex.Message));
                        return;
                    }

                    var midpoint = batch.Length / 2;
                    AddCompatibleUpgradeBatch(batch[..midpoint]);
                    AddCompatibleUpgradeBatch(batch[midpoint..]);
                }
            }
        }

        var orderedPlan = OrderPackagesForInstall(plan, installed);
        var changes = CreatePackageChanges(plan, installed);
        if (!ConfirmPackageChanges("升级", changes))
            return;

        var files = await DownloadPackagesAsync(orderedPlan);
        await InstallPackagesAsync(files, orderedPlan);
        var autoInstalled = ReadDpmAutoInstalled();
        AddNewAutomaticDependencies(autoInstalled, plan, installed, candidates.Select(item => item.Candidate.Name));
        WriteDpmAutoInstalled(autoInstalled);
    }

    private static void PrintHeldBackUpgrades(
        IReadOnlyList<(PackageRecord Package, string Reason)> heldBack)
    {
        if (heldBack.Count == 0)
            return;

        Console.WriteLine($"因依赖冲突暂缓升级 {heldBack.Count} 个软件包：");
        foreach (var (package, reason) in heldBack.Take(20))
            Console.WriteLine($"  {package.Name} {package.Version}: {reason}");
        if (heldBack.Count > 20)
            Console.WriteLine($"  ……另有 {heldBack.Count - 20} 个软件包");
    }

    private static async Task UpgradeDpmAsync()
    {
        var installed = await GetInstalledAsync();
        if (!installed.TryGetValue("dpm", out var current))
            throw new InvalidOperationException("The dpm package is not registered as installed.");

        using var releaseResponse = await Http.GetAsync(LatestReleaseApi);
        if (!releaseResponse.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Could not query the latest dpm release: HTTP {(int)releaseResponse.StatusCode} {releaseResponse.ReasonPhrase}.");
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync();
        using var release = await JsonDocument.ParseAsync(releaseStream);

        var releaseRoot = release.RootElement;
        if (!releaseRoot.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The latest GitHub release has no tag name.");
        var tag = tagElement.GetString()!;
        if (!tag.StartsWith('v') || tag.Length == 1)
            throw new InvalidDataException($"Invalid dpm release tag '{tag}'.");
        var targetVersion = tag[1..];
        var versionComparison = CompareVersions(targetVersion, current.Version);
        if (versionComparison == 0)
        {
            Console.WriteLine($"dpm 已是最新版本（{current.Version}）。");
            return;
        }

        var architecture = await GetArchitectureAsync();
        var assetName = $"dpm_{targetVersion}_{architecture}.deb";
        if (!releaseRoot.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The latest GitHub release has no package assets.");

        JsonElement selectedAsset = default;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                nameElement.GetString() == assetName)
            {
                selectedAsset = asset;
                break;
            }
        }

        if (selectedAsset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException(
                $"The latest dpm release does not provide a package for {architecture} ({assetName}).");
        if (!selectedAsset.TryGetProperty("browser_download_url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            downloadUri.Host != "github.com" ||
            !downloadUri.AbsolutePath.StartsWith(
                $"/{GitHubRepository}/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("The latest dpm release contains an unexpected package URL.");
        if (!selectedAsset.TryGetProperty("digest", out var digestElement) ||
            digestElement.ValueKind != JsonValueKind.String ||
            !digestElement.GetString()!.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException("The latest dpm package does not provide a SHA256 digest.");

        var expectedHash = digestElement.GetString()!["sha256:".Length..];
        if (expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The latest dpm package has an invalid SHA256 digest.");

        var publisher = releaseRoot.TryGetProperty("author", out var authorElement) &&
                        authorElement.ValueKind == JsonValueKind.Object &&
                        authorElement.TryGetProperty("login", out var loginElement) &&
                        loginElement.ValueKind == JsonValueKind.String
            ? loginElement.GetString()
            : null;
        var releaseNotes = releaseRoot.TryGetProperty("body", out var bodyElement) &&
                           bodyElement.ValueKind == JsonValueKind.String
            ? bodyElement.GetString()?.Trim()
            : null;
        Console.WriteLine($"GitHub Release: dpm {targetVersion} ({architecture})");
        Console.WriteLine($"本地版本: {current.Version}");
        Console.WriteLine($"维护者: {publisher ?? "未知"}");
        Console.WriteLine("更新日志:");
        Console.WriteLine(string.IsNullOrWhiteSpace(releaseNotes) ? "（无）" : releaseNotes);
        if (versionComparison < 0)
            Console.WriteLine("注意：本地版本高于 GitHub Release，将按 GitHub Release 版本回退。");
        var changes = new[]
        {
            new PackageChange("dpm", current.Version, targetVersion)
        };
        if (!ConfirmPackageChanges("升级", changes))
            return;

        var currentLocale = new[]
            {
                Environment.GetEnvironmentVariable("LC_ALL"),
                Environment.GetEnvironmentVariable("LC_MESSAGES"),
                Environment.GetEnvironmentVariable("LANG")
            }
            .FirstOrDefault(locale => !string.IsNullOrWhiteSpace(locale));
        if (IsChineseUtf8Locale(currentLocale))
            downloadUri = new Uri(GitHubProxyPrefix + downloadUri.AbsoluteUri);

        var temporaryPackage = Path.Combine(
            PackageDirectory, $"dpm-upgrade-{Guid.NewGuid():N}.deb");
        try
        {
            Console.WriteLine($"正在下载 dpm {targetVersion} ({architecture})…");
            await DownloadToFileAsync(downloadUri, temporaryPackage);
            if (!await HasHashAsync(temporaryPackage, expectedHash))
                throw new InvalidDataException("Downloaded dpm package SHA256 does not match the GitHub release digest.");

            var metadata = await RunProcessAsync("dpkg-deb", ["--field", temporaryPackage]);
            if (metadata.ExitCode != 0)
                throw new InvalidDataException(
                    $"Could not inspect downloaded dpm package: {metadata.Error.Trim()}");
            var fields = ParseParagraphs(metadata.Output).FirstOrDefault()
                         ?? throw new InvalidDataException("Downloaded dpm package has no control metadata.");
            if (!fields.TryGetValue("Package", out var packageName) ||
                packageName != "dpm" ||
                !fields.TryGetValue("Version", out var packageVersion) ||
                packageVersion != targetVersion ||
                !fields.TryGetValue("Architecture", out var packageArchitecture) ||
                packageArchitecture != architecture)
                throw new InvalidDataException(
                    "Downloaded package metadata does not match the requested dpm release and system architecture.");

            var dpkgArguments = new List<string>();
            if (versionComparison < 0)
                dpkgArguments.Add("--force-downgrade");
            dpkgArguments.Add("--install");
            dpkgArguments.Add(temporaryPackage);
            await RunDpkgAsync(
                dpkgArguments,
                1,
                true,
                new HashSet<string>(["dpm"], StringComparer.Ordinal));
            Console.WriteLine($"dpm 已升级到 {targetVersion}。");
        }
        finally
        {
            if (File.Exists(temporaryPackage))
                File.Delete(temporaryPackage);
        }
    }

    private static bool IsChineseUtf8Locale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return false;
        var normalized = locale.Split('@', 2)[0];
        return normalized.Equals("zh_CN.UTF-8", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("zh_CN.utf8", StringComparison.OrdinalIgnoreCase);
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

    private static async Task InstallPackagesAsync(
        IReadOnlyList<string> files,
        IReadOnlyList<PackageRecord> packages)
    {
        try
        {
            await RunDpkgAsync(
                ["--install", .. files],
                files.Count,
                true,
                packages.Select(package => package.Name).ToHashSet(StringComparer.Ordinal));
        }
        catch (InvalidOperationException installError)
        {
            Console.Error.WriteLine(
                "警告: dpkg 安装未完全成功，正在重试配置待配置的软件包。");
            try
            {
                await RunDpkgAsync(["--configure", "--pending"]);
            }
            catch (InvalidOperationException configureError)
            {
                throw new InvalidOperationException(
                    $"{installError.Message} 自动重试配置也失败: {configureError.Message}",
                    installError);
            }

            var installed = await GetInstalledAsync();
            var incomplete = packages
                .Where(package =>
                {
                    var current = installed.FirstOrDefault(entry =>
                        PackageNameMatches(entry.Key, package.Name));
                    return current.Key is null ||
                           !string.Equals(current.Value.Version, package.Version, StringComparison.Ordinal);
                })
                .Select(package => $"{package.Name} {package.Version}")
                .ToList();
            if (incomplete.Count > 0)
                throw new InvalidOperationException(
                    $"{installError.Message} 重试配置后仍未安装或版本不匹配: {string.Join(", ", incomplete)}");

            Console.Error.WriteLine(
                "警告: 重试配置成功，已确认本次计划中的软件包均已安装。");
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
                         .Concat(ParseDependencyGroups(package.Depends))
                         .Concat(ParseDependencyGroups(package.Recommends)))
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

        return installed.Keys
            .Where(name => IsAutoInstalled(name, autoInstalled) &&
                           !removed.Contains(name) &&
                           !reachable.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<string> FindBlockingDependents(
        IReadOnlyDictionary<string, InstalledPackage> installed,
        IReadOnlySet<string> removed)
    {
        var dependencyIndex = BuildInstalledDependencyIndex(installed);
        var dependents = new List<string>();
        foreach (var entry in installed)
        {
            if (removed.Contains(entry.Key))
                continue;

            AddBlockingDependencies(entry.Key, entry.Value.PreDepends, "Pre-Depends");
            AddBlockingDependencies(entry.Key, entry.Value.Depends, "Depends");
        }

        return dependents;

        void AddBlockingDependencies(string packageName, string expression, string field)
        {
            foreach (var dependency in ParseDependencyGroups(expression))
            {
                var candidates = dependency
                    .SelectMany(alternative => GetInstalledCandidates(dependencyIndex, alternative))
                    .DistinctBy(candidate => candidate.Key)
                    .Where(candidate => dependency.Any(alternative =>
                        InstalledPackageSatisfies(candidate.Value, candidate.Key, alternative)))
                    .ToList();
                if (!candidates.Any(candidate => removed.Contains(candidate.Key)) ||
                    candidates.Any(candidate => !removed.Contains(candidate.Key)))
                    continue;

                var alternatives = string.Join(" | ", dependency.Select(FormatDependency));
                dependents.Add($"{packageName} ({field}: {alternatives})");
            }
        }
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
        var selectedByName = new Dictionary<string, List<PackageRecord>>(StringComparer.Ordinal);
        var selectedByProvide = new Dictionary<string, List<PackageRecord>>(StringComparer.Ordinal);
        var installedIndex = BuildInstalledDependencyIndex(installed);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
            AddSelected(root);

        foreach (var root in roots)
            ResolvePackage(root);
        return selected;

        void AddSelected(PackageRecord package)
        {
            selected[package.Name] = package;
            AddIndexedPackage(selectedByName, GetPackageBaseName(package.Name), package);
            if (!string.Equals(package.Name, GetPackageBaseName(package.Name), StringComparison.Ordinal))
                AddIndexedPackage(selectedByName, package.Name, package);
            foreach (var provide in SplitDependencyExpression(package.Provides, ','))
            {
                var provided = ParseDependencyAlternative(provide);
                if (provided is not null)
                    AddIndexedPackage(selectedByProvide, provided.Name, package);
            }
        }

        void ResolvePackage(PackageRecord package)
        {
            if (resolved.Contains(package.Name) || !visiting.Add(package.Name))
                return;
            try
            {
                var dependencyGroups = ParseDependencyGroups(package.PreDepends)
                    .Concat(ParseDependencyGroups(package.Depends))
                    .Select(dependency => (Dependency: dependency, Optional: false))
                    .Concat(ParseDependencyGroups(package.Recommends)
                        .Select(dependency => (Dependency: dependency, Optional: true)));

                foreach (var (dependency, optional) in dependencyGroups)
                {
                    var satisfiedBySelected = dependency.Any(alternative =>
                        GetPackageCandidates(selectedByName, selectedByProvide, alternative).Any(candidate =>
                            PackageSatisfies(candidate, alternative)));
                    if (satisfiedBySelected)
                        continue;

                    var satisfiedByInstalled = dependency.Any(alternative =>
                        GetInstalledCandidates(installedIndex, alternative).Any(entry =>
                            !selectedByName.ContainsKey(GetPackageBaseName(entry.Key)) &&
                            InstalledPackageSatisfies(entry.Value, entry.Key, alternative)));
                    if (satisfiedByInstalled)
                    {
                        ExpandInstalledMetapackage(dependency);
                        continue;
                    }

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
                        if (optional)
                            continue;

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
                        AddSelected(candidate);
                        ResolvePackage(candidate);
                    }
                }
                resolved.Add(package.Name);
            }
            finally
            {
                visiting.Remove(package.Name);
            }
        }

        void ExpandInstalledMetapackage(IReadOnlyList<DependencyAlternative> dependency)
        {
            foreach (var alternative in dependency)
            {
                foreach (var entry in GetInstalledCandidates(installedIndex, alternative))
                {
                    var packageName = GetPackageBaseName(entry.Key);
                    if (selectedByName.ContainsKey(packageName) ||
                        !InstalledPackageSatisfies(entry.Value, entry.Key, alternative) ||
                        !availableByName.TryGetValue(packageName, out var availableVersions))
                        continue;

                    var installedMetapackage = availableVersions.FirstOrDefault(candidate =>
                        candidate.IsMetaPackage &&
                        string.Equals(candidate.Version, entry.Value.Version, StringComparison.Ordinal));
                    if (installedMetapackage is null)
                        continue;

                    ResolvePackage(installedMetapackage);
                    return;
                }
            }
        }
    }

    private static void AddIndexedPackage(
        IDictionary<string, List<PackageRecord>> index,
        string name,
        PackageRecord package)
    {
        if (!index.TryGetValue(name, out var packages))
            index[name] = packages = [];
        packages.Add(package);
    }

    private static IEnumerable<PackageRecord> GetPackageCandidates(
        IReadOnlyDictionary<string, List<PackageRecord>> byName,
        IReadOnlyDictionary<string, List<PackageRecord>> byProvide,
        DependencyAlternative dependency)
    {
        if (byName.TryGetValue(dependency.Name, out var namedPackages))
        {
            foreach (var package in namedPackages)
                yield return package;
        }
        if (byProvide.TryGetValue(dependency.Name, out var providers))
        {
            foreach (var package in providers)
                yield return package;
        }
    }

    private static IReadOnlyList<PackageRecord> OrderPackagesForInstall(
        IReadOnlyDictionary<string, PackageRecord> plan,
        IReadOnlyDictionary<string, InstalledPackage> installed)
    {
        var dependencies = plan.Values.ToDictionary(
            package => package.Name,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var package in plan.Values)
        foreach (var group in ParseDependencyGroups(package.PreDepends))
        {
            PackageRecord? selectedDependency = null;
            foreach (var alternative in group)
            {
                selectedDependency = plan.Values
                    .Where(candidate => PackageSatisfies(candidate, alternative))
                    .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (selectedDependency is not null)
                    break;
            }

            if (selectedDependency is not null)
            {
                dependencies[package.Name].Add(selectedDependency.Name);
                continue;
            }

            var satisfiedByInstalled = group.Any(alternative =>
                installed.Any(entry =>
                    !plan.Values.Any(candidate => PackageNameMatches(entry.Key, candidate.Name)) &&
                    InstalledPackageSatisfies(entry.Value, entry.Key, alternative)));
            if (!satisfiedByInstalled)
            {
                var expression = string.Join(" | ", group.Select(FormatDependency));
                throw new InvalidOperationException(
                    $"Cannot order unresolved pre-dependency '{expression}' required by {package.Name} {package.Version}.");
            }
        }

        var states = new Dictionary<string, byte>(StringComparer.Ordinal);
        var ordered = new List<PackageRecord>(plan.Count);
        foreach (var package in plan.Values.OrderBy(package => package.Name, StringComparer.Ordinal))
            Visit(package);
        return ordered;

        void Visit(PackageRecord package)
        {
            if (states.TryGetValue(package.Name, out var state))
            {
                if (state == 1)
                    throw new InvalidOperationException(
                        $"Circular Pre-Depends detected while ordering package {package.Name}.");
                return;
            }

            states[package.Name] = 1;
            foreach (var dependencyName in dependencies[package.Name].Order(StringComparer.Ordinal))
                Visit(plan[dependencyName]);
            states[package.Name] = 2;
            ordered.Add(package);
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
            if (versionEnd < 0 || !string.IsNullOrWhiteSpace(value[(versionEnd + 1)..]))
                throw new InvalidDataException($"Malformed package dependency: {expression}");
            var versionFields = value[(versionStart + 1)..versionEnd]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (versionFields.Length == 2)
            {
                relation = versionFields[0];
                version = versionFields[1];
            }
            else if (versionFields.Length == 1)
            {
                foreach (var candidate in new[] { "<<", "<=", ">>", ">=", "==", "=", "<", ">" })
                {
                    if (!versionFields[0].StartsWith(candidate, StringComparison.Ordinal) ||
                        versionFields[0].Length == candidate.Length)
                        continue;
                    relation = candidate;
                    version = versionFields[0][candidate.Length..];
                    break;
                }
            }
            if (relation is null || version is null ||
                relation is not ("=" or "==" or "<<" or "<" or "<=" or ">>" or ">" or ">="))
                throw new InvalidDataException($"Malformed package version constraint: {expression}");
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
        => CompareVersions(candidate, current) > 0;

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
            if (package.LocalPath is not null)
            {
                if (!File.Exists(package.LocalPath))
                    throw new FileNotFoundException($"Local package file was not found: {package.LocalPath}");
                cachedFiles[package] = package.LocalPath;
                continue;
            }

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
        var leftEpochEnd = left.IndexOf(':');
        var rightEpochEnd = right.IndexOf(':');
        var leftEpoch = leftEpochEnd < 0 ? "0" : left[..leftEpochEnd];
        var rightEpoch = rightEpochEnd < 0 ? "0" : right[..rightEpochEnd];
        if (!IsAsciiDigits(leftEpoch) || !IsAsciiDigits(rightEpoch))
            throw new InvalidDataException($"Invalid Debian package version: {left} or {right}.");

        var epochComparison = CompareNumericStrings(leftEpoch, rightEpoch);
        if (epochComparison != 0)
            return epochComparison;

        var leftUpstream = leftEpochEnd < 0 ? left : left[(leftEpochEnd + 1)..];
        var rightUpstream = rightEpochEnd < 0 ? right : right[(rightEpochEnd + 1)..];
        var leftRevisionEnd = leftUpstream.LastIndexOf('-');
        var rightRevisionEnd = rightUpstream.LastIndexOf('-');
        var leftRevision = leftRevisionEnd < 0 ? "0" : leftUpstream[(leftRevisionEnd + 1)..];
        var rightRevision = rightRevisionEnd < 0 ? "0" : rightUpstream[(rightRevisionEnd + 1)..];
        var leftVersion = leftRevisionEnd < 0 ? leftUpstream : leftUpstream[..leftRevisionEnd];
        var rightVersion = rightRevisionEnd < 0 ? rightUpstream : rightUpstream[..rightRevisionEnd];

        var upstreamComparison = CompareVersionPart(leftVersion, rightVersion);
        return upstreamComparison != 0
            ? upstreamComparison
            : CompareVersionPart(leftRevision, rightRevision);
    }

    private static List<PackageRecord> LoadPackages()
    {
        if (PackageCache is not null)
            return PackageCache;
        var result = new List<PackageRecord>();
        if (!Directory.Exists(IndexDirectory))
            return PackageCache = result;
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
                fields.TryGetValue("Recommends", out var recommends);
                fields.TryGetValue("Section", out var section);
                fields.TryGetValue("Tag", out var tags);
                var isMetaPackage = string.Equals(section, "metapackages", StringComparison.OrdinalIgnoreCase) ||
                                    (tags ?? "").Split([',', ' ', '\t', '\r', '\n'],
                                        StringSplitOptions.RemoveEmptyEntries)
                                    .Contains("role::metapackage", StringComparer.OrdinalIgnoreCase);
                result.Add(new PackageRecord(name, version, filename, hash ?? "", description ?? "",
                    sourceUri, preDepends ?? "", depends ?? "", provides ?? "", recommends ?? "",
                    isMetaPackage, size));
            }
        }
        return PackageCache = result;
    }

    private static bool IsAsciiDigits(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static int CompareNumericStrings(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.Compare(left, right, StringComparison.Ordinal);
    }

    private static int CompareVersionPart(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length || rightIndex < right.Length)
        {
            while ((leftIndex < left.Length && !IsAsciiDigit(left[leftIndex])) ||
                   (rightIndex < right.Length && !IsAsciiDigit(right[rightIndex])))
            {
                var leftIsNonDigit = leftIndex < left.Length && !IsAsciiDigit(left[leftIndex]);
                var rightIsNonDigit = rightIndex < right.Length && !IsAsciiDigit(right[rightIndex]);
                var leftOrder = leftIsNonDigit ? VersionCharacterOrder(left[leftIndex]) : 0;
                var rightOrder = rightIsNonDigit ? VersionCharacterOrder(right[rightIndex]) : 0;
                if (leftOrder != rightOrder)
                    return leftOrder.CompareTo(rightOrder);
                if (leftIsNonDigit) leftIndex++;
                if (rightIsNonDigit) rightIndex++;
            }

            while (leftIndex < left.Length && left[leftIndex] == '0') leftIndex++;
            while (rightIndex < right.Length && right[rightIndex] == '0') rightIndex++;

            var leftDigitStart = leftIndex;
            var rightDigitStart = rightIndex;
            while (leftIndex < left.Length && IsAsciiDigit(left[leftIndex])) leftIndex++;
            while (rightIndex < right.Length && IsAsciiDigit(right[rightIndex])) rightIndex++;
            var leftDigitLength = leftIndex - leftDigitStart;
            var rightDigitLength = rightIndex - rightDigitStart;
            var digitLengthComparison = leftDigitLength.CompareTo(rightDigitLength);
            if (digitLengthComparison != 0)
                return digitLengthComparison;
            if (leftDigitLength > 0)
            {
                var digitComparison = string.Compare(
                    left, leftDigitStart, right, rightDigitStart, leftDigitLength,
                    StringComparison.Ordinal);
                if (digitComparison != 0)
                    return digitComparison;
            }
        }

        return 0;
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';

    private static int VersionCharacterOrder(char character)
    {
        if (character == '~') return -1;
        if (character == '\0') return 0;
        if (char.IsAsciiLetter(character)) return character;
        return character + 256;
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
            ["-W", "-f=${binary:Package}\\t${Version}\\t${db:Status-Status}\\t${Provides}\\t${Pre-Depends}\\t${Depends}\\t${Recommends}\\n"]);
        if (result.ExitCode != 0) throw new InvalidOperationException("Could not query installed packages.");
        var installed = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t', 7);
            if (fields.Length == 7 && fields[2] == "installed")
                installed[fields[0]] = new InstalledPackage(fields[1], fields[3], fields[4], fields[5], fields[6]);
        }
        return installed;
    }

    private static async Task RunDpkgAsync(
        IReadOnlyList<string> arguments,
        int expectedPackageCount = 0,
        bool showProgress = false,
        IReadOnlySet<string>? progressPackages = null)
    {
        var start = new ProcessStartInfo("dpkg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = showProgress,
            RedirectStandardError = showProgress
        };
        if (showProgress)
            start.ArgumentList.Add("--status-fd=2");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dpkg.");
        var outputTask = showProgress ? process.StandardOutput.ReadToEndAsync() : null;
        var errorOutput = showProgress
            ? await ReadDpkgProgressAsync(process, expectedPackageCount, progressPackages)
            : "";
        await process.WaitForExitAsync();
        if (outputTask is not null)
            await outputTask;
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

    private static async Task<string> ReadDpkgProgressAsync(
        Process process,
        int totalPackages,
        IReadOnlySet<string>? progressPackages)
    {
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new StringBuilder();
        string? currentPackage = null;
        string? currentStage = null;

        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("processing: ", StringComparison.Ordinal))
            {
                var fields = line["processing: ".Length..].Split(':', 2);
                if (fields.Length != 2)
                    continue;

                currentStage = fields[0].Trim();
                currentPackage = fields[1].Trim();
                if (currentStage is not ("install" or "configure" or "remove" or "purge") ||
                    !IsExpectedProgressPackage(currentPackage))
                    continue;

                RenderDpkgProgress(completed.Count, totalPackages, currentPackage, currentStage);
            }
            else if (line.StartsWith("status: ", StringComparison.Ordinal))
            {
                var fields = line["status: ".Length..].Split(':', 2);
                if (fields.Length == 2)
                {
                    var package = fields[0].Trim();
                    var status = fields[1].Trim();
                    var isCurrentPackage = string.Equals(package, currentPackage, StringComparison.Ordinal);
                    var isExpectedPackage = IsExpectedProgressPackage(package);
                    var isCompleted = currentStage switch
                    {
                        "install" or "configure" => status == "installed",
                        "remove" => status is "config-files" or "not-installed",
                        "purge" => status == "not-installed",
                        _ => false
                    };
                    if (isCurrentPackage && isExpectedPackage && isCompleted &&
                        completed.Add(package))
                    {
                        currentPackage = package;
                        RenderDpkgProgress(completed.Count, totalPackages, currentPackage, currentStage);
                    }
                }
            }
            else
            {
                diagnostics.AppendLine(line);
            }
        }
        return diagnostics.ToString();

        bool IsExpectedProgressPackage(string package) =>
            progressPackages is null ||
            progressPackages.Any(expected =>
                PackageNameMatches(package, expected) || PackageNameMatches(expected, package));
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
        const int width = 16;
        var filled = percent * width / 100;
        var bar = new string('#', filled) + new string('-', width - filled);
        var action = stage switch
        {
            "install" or "configure" => "安装",
            "remove" => "卸载",
            "purge" => "清除",
            "完成" => "完成",
            _ => "处理"
        };
        var detail = string.IsNullOrWhiteSpace(package) ? "" : $" {package}";
        var message = $"{action} [{bar}] {percent,3}% {completed}/{total}{detail}";
        if (Console.IsOutputRedirected)
        {
            if (forceComplete)
                Console.WriteLine($"完成 ({total}/{total})");
        }
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
        string Recommends,
        bool IsMetaPackage,
        long? Size,
        string? LocalPath = null);
    private sealed record DependencyAlternative(string Name, string? Relation, string? Version);
    private sealed record PackageChange(string Name, string? CurrentVersion, string? TargetVersion, string? Reason = null);
    private sealed record InstalledPackage(
        string Version,
        string Provides,
        string PreDepends,
        string Depends,
        string Recommends);
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
