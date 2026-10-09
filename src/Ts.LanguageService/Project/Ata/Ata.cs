// Ported from tsgo internal/project/ata/ata.go (whole file).
//
// PORT-NOTEs:
//  * Go `(x, error)` returns map to `(value, Exception?)` per plan/05 §4.
//  * Go `context.Context` maps to CancellationToken (plan/01 §4.4).
//  * Go json.Unmarshal calls become hand-rolled Utf8JsonReader parses (AOT-safe,
//    no reflection — plan/07 STJ policy; mirrors the Ts.PackageJson parser).
//  * core.ThrottleGroup is deliberately absent from the C# core (WorkGroup.cs
//    header: "its only production consumer is project/ata"), so it is ported here
//    verbatim from internal/core/workgroup.go:92-123 as a package-local type.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Ts.Collections;

namespace Ts.Project.Ata;

// ata.go:21-25 — TypingsInfo.
public sealed class TypingsInfo
{
    public global::Ts.Core.TypeAcquisition? TypeAcquisition;

    public global::Ts.Core.CompilerOptions? CompilerOptions;

    public global::Ts.Collections.Set<string>? UnresolvedImports;

    // ata.go:27-31 — Equals.
    public bool Equals(TypingsInfo other)
    {
        if (other is null)
        {
            return false;
        }
        return TypeAcquisitionEquals(TypeAcquisition, other.TypeAcquisition) &&
            GetAllowJS(CompilerOptions) == GetAllowJS(other.CompilerOptions) &&
            SetEquals(UnresolvedImports, other.UnresolvedImports);
    }

    // Go: core.TypeAcquisition.Equals (nil-tolerant).
    private static bool TypeAcquisitionEquals(global::Ts.Core.TypeAcquisition? a, global::Ts.Core.TypeAcquisition? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.Equals(b);
    }

    // Go: (*core.CompilerOptions).GetAllowJS (nil receiver ⇒ false).
    private static bool GetAllowJS(global::Ts.Core.CompilerOptions? options)
    {
        if (options is null)
        {
            return false;
        }
        return options.GetAllowJS();
    }

    // Go: (*collections.Set[E]).Equals (nil-tolerant).
    private static bool SetEquals(global::Ts.Collections.Set<string>? a, global::Ts.Collections.Set<string>? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a.Len() != b.Len())
        {
            return false;
        }
        // map-iter: 序无关 — set equality by membership.
        foreach (string key in a.Keys())
        {
            if (!b.Has(key))
            {
                return false;
            }
        }
        return true;
    }
}

// ata.go:33-36 — CachedTyping.
public sealed class CachedTyping
{
    public string TypingsLocation = "";

    public global::Ts.Semver.Version? Version;
}

// ata.go:38-41 — TypingsInstallerOptions.
public sealed class TypingsInstallerOptions
{
    public string TypingsLocation = "";

    public int ThrottleLimit;
}

// ata.go:43-45 — NpmExecutor.
public interface NpmExecutor
{
    (byte[] Output, Exception? Error) NpmInstall(string cwd, string[] args);
}

// ata.go:47-50 — TypingsInstallerHost (Go embeds NpmExecutor + module.ResolutionHost).
public interface TypingsInstallerHost : NpmExecutor, global::Ts.Module.ResolutionHost
{
}

// ata.go:52-65 — TypingsInstaller.
public sealed class TypingsInstaller
{
    private readonly string typingsLocation;
    private readonly TypingsInstallerHost host;

    // Go: initOnce sync.Once.
    private readonly object initOnce = new();
    private bool initialized;

    private readonly global::Ts.Collections.SyncMap<string, CachedTyping> packageNameToTypingLocation = new();

    private readonly global::Ts.Collections.SyncMap<string, bool> missingTypingsSet = new();

    private Dictionary<string, Dictionary<string, string>>? typesRegistry;

    private int installRunCount; // Go: atomic.Int32

    private readonly SemaphoreSlim concurrencySemaphore;

    // ata.go:67-73 — NewTypingsInstaller.
    public static TypingsInstaller NewTypingsInstaller(TypingsInstallerOptions options, TypingsInstallerHost host)
    {
        return new TypingsInstaller(options, host);
    }

    private TypingsInstaller(TypingsInstallerOptions options, TypingsInstallerHost host)
    {
        typingsLocation = options.TypingsLocation;
        this.host = host;
        concurrencySemaphore = new SemaphoreSlim(options.ThrottleLimit);
    }

    // ata.go:75-85 — IsKnownTypesPackageName.
    public bool IsKnownTypesPackageName(global::Ts.TsPath.Path projectID, string name, global::Ts.Vfs.IFileSystem fs, global::Ts.Project.Logging.Logger logger)
    {
        // We want to avoid looking this up in the registry as that is expensive. So first check that it's actually an NPM package.
        (NameValidationResult validationResult, _, _) = PackageNameValidation.ValidatePackageName(name);
        if (validationResult != NameValidationResult.NameOk)
        {
            return false;
        }
        // Strada did this lazily - is that needed here to not waiting on and returning false on first request
        Init(projectID.Value, fs, logger);
        return typesRegistry!.ContainsKey(name);
    }

    // !!! sheetal currently we use latest instead of core.VersionMajorMinor()
    // ata.go:88 — tsVersionToUse.
    private const string TsVersionToUse = "latest";

    // ata.go:90-100 — TypingsInstallRequest.
    public sealed class TypingsInstallRequest
    {
        public global::Ts.TsPath.Path ProjectID;

        public TypingsInfo? TypingsInfo;

        public string[] FileNames = [];

        public string ProjectRootPath = "";

        public global::Ts.Core.CompilerOptions? CompilerOptions;

        public string CurrentDirectory = "";

        public Func<string, global::Ts.Core.ScriptKind> GetScriptKind = null!;

        public global::Ts.Vfs.IFileSystem FS = null!;

        public global::Ts.Project.Logging.Logger? Logger;
    }

    // ata.go:102-105 — TypingsInstallResult.
    public sealed class TypingsInstallResult
    {
        public string[] TypingsFiles = [];

        public string[] FilesToWatch = [];
    }

    // ata.go:107-115 — InstallTypings.
    public (TypingsInstallResult? Result, Exception? Error) InstallTypings(TypingsInstallRequest request)
    {
        (TypingsInstallResult? result, Exception? err) = DiscoverAndInstallTypings(request);
        if (err == null)
        {
            Array.Sort(result!.TypingsFiles, StringComparer.Ordinal);
            Array.Sort(result.FilesToWatch, StringComparer.Ordinal);
            request.Logger!.Log("ATA:: Got install request for: " + request.ProjectID.Value);
        }
        return (result, err);
    }

    // ata.go:117-155 — discoverAndInstallTypings.
    private (TypingsInstallResult? Result, Exception? Error) DiscoverAndInstallTypings(TypingsInstallRequest request)
    {
        Init(request.ProjectID.Value, request.FS, request.Logger!);

        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            request.FS,
            request.Logger!,
            request.TypingsInfo!,
            request.FileNames,
            request.ProjectRootPath,
            packageNameToTypingLocation,
            typesRegistry!);

        int requestId = Interlocked.Increment(ref installRunCount);
        // install typings
        if (newTypingNames.Length > 0)
        {
            string[] filteredTypings = FilterTypings(request.ProjectID, request.Logger!, newTypingNames);
            if (filteredTypings.Length != 0)
            {
                (string[]? typingsFiles, Exception? err) = InstallTypingsWorker(request.ProjectID, request.TypingsInfo!, requestId, cachedTypingPaths, filteredTypings, request.Logger!);
                if (err != null)
                {
                    return (null, err);
                }
                return (new TypingsInstallResult
                {
                    TypingsFiles = typingsFiles!,
                    FilesToWatch = filesToWatch,
                }, null);
            }
            request.Logger!.Log("ATA:: All typings are known to be missing or invalid - no need to install more typings");
        }
        else
        {
            request.Logger!.Log("ATA:: No new typings were requested as a result of typings discovery");
        }

        return (new TypingsInstallResult
        {
            TypingsFiles = cachedTypingPaths,
            FilesToWatch = filesToWatch,
        }, null);
        // !!! sheetal events to send
        // this.event(response, "setTypings");
    }

    // ata.go:157-254 — installTypings.
    private (string[]? Result, Exception? Error) InstallTypingsWorker(
        global::Ts.TsPath.Path projectID,
        TypingsInfo typingsInfo,
        int requestID,
        string[] currentlyCachedTypings,
        string[] filteredTypings,
        global::Ts.Project.Logging.Logger logger)
    {
        _ = typingsInfo;
        // !!! sheetal events to send
        // send progress event
        // this.sendResponse({
        // 	kind: EventBeginInstallTypes,
        // 	eventId: requestId,
        // 	typingsInstallerVersion: version,
        // 	projectName: req.projectName,
        // } as BeginInstallTypes);

        // const body: protocol.BeginInstallTypesEventBody = {
        // 	eventId: response.eventId,
        // 	packages: response.packagesToInstall,
        // };
        // const eventName: protocol.BeginInstallTypesEventName = "beginInstallTypes";
        // this.event(body, eventName);

        var scopedTypings = new string[filteredTypings.Length];
        for (int i = 0; i < filteredTypings.Length; i++)
        {
            string packageName = filteredTypings[i];
            scopedTypings[i] = $"@types/{packageName}@{TsVersionToUse}"; // @tscore.VersionMajorMinor) // This is normally @tsVersionMajorMinor but for now lets use latest
        }

        (string[] _, bool ok) = InstallWorker(projectID, requestID, scopedTypings, logger);
        if (ok)
        {
            logger.Logf("ATA:: Installed typings {0}", DiscoverTypingsApi.FormatList(filteredTypings));
            var installedTypingFiles = new List<string>();
            var resolver = new global::Ts.Module.Resolver(host, new global::Ts.Core.CompilerOptions { ModuleResolution = global::Ts.Core.ModuleResolutionKind.NodeNext }, "", "");
            foreach (string packageName in filteredTypings)
            {
                string typingFile = TypingToFileName(resolver, packageName);
                if (typingFile == "")
                {
                    logger.Logf("ATA:: Failed to find typing file for package '{0}'", packageName);
                    missingTypingsSet.Store(packageName, true);
                    continue;
                }

                // packageName is guaranteed to exist in typesRegistry by filterTypings
                Dictionary<string, string> distTags = typesRegistry![packageName];
                if (!distTags.TryGetValue("ts" + global::Ts.Core.TsVersion.VersionMajorMinor(), out string? useVersion))
                {
                    distTags.TryGetValue("latest", out useVersion);
                }
                global::Ts.Semver.Version newVersion = global::Ts.Semver.Semver.MustParse(useVersion!);
                var newTyping = new CachedTyping { TypingsLocation = typingFile, Version = newVersion };
                packageNameToTypingLocation.Store(packageName, newTyping);
                installedTypingFiles.Add(typingFile);
            }
            logger.Logf("ATA:: Installed typing files {0}", DiscoverTypingsApi.FormatList(installedTypingFiles));

            return ([.. currentlyCachedTypings, .. installedTypingFiles], null);
        }

        // DO we really need these events
        // this.event(response, "setTypings");
        logger.Logf("ATA:: install request failed, marking packages as missing to prevent repeated requests: {0}", DiscoverTypingsApi.FormatList(filteredTypings));
        foreach (string typing in filteredTypings)
        {
            missingTypingsSet.Store(typing, true);
        }

        return (null, new InvalidOperationException("npm install failed"));

        // !!! sheetal events to send
        // const response: EndInstallTypes = { ... }
        // (commented Go tail preserved verbatim in the repo history)
    }

    // ata.go:256-278 — installWorker.
    private (string[] PackageNames, bool Ok) InstallWorker(
        global::Ts.TsPath.Path projectID,
        int requestId,
        string[] packageNames,
        global::Ts.Project.Logging.Logger logger)
    {
        _ = projectID;
        logger.Logf("ATA:: #{0} with cwd: {1} arguments: {2}", requestId, typingsLocation, DiscoverTypingsApi.FormatList(packageNames));
        Exception? err = InstallNpmPackages(CancellationToken.None, packageNames, concurrencySemaphore, packages =>
        {
            var npmArgs = new List<string>
            {
                "install",
                "--ignore-scripts",
            };
            npmArgs.AddRange(packages);
            npmArgs.AddRange(["--save-dev", "--user-agent=\"typesInstaller/" + global::Ts.Core.TsVersion.Version() + "\""]);
            (byte[] output, Exception? npmErr) = host.NpmInstall(typingsLocation, npmArgs.ToArray());
            if (npmErr != null)
            {
                logger.Logf("ATA:: Output is: {0}", System.Text.Encoding.UTF8.GetString(output));
                return npmErr;
            }
            return null;
        });
        logger.Logf("TI:: npm install #{0} completed", requestId);
        return (packageNames, err == null);
    }

    // ata.go:280-316 — installNpmPackages (ThrottleGroup-batched npm installs).
    // (internal: Go package-private; the unit test needs same-package access.)
    internal static Exception? InstallNpmPackages(
        CancellationToken ctx,
        string[] packageNames,
        SemaphoreSlim concurrencySemaphore,
        Func<string[], Exception?> installPackages)
    {
        ThrottleGroup tg = ThrottleGroup.NewThrottleGroup(ctx, concurrencySemaphore);

        int currentCommandStart = 0;
        int currentCommandEnd = 0;
        int currentCommandSize = 100;

        for (int i = 0; i < packageNames.Length; i++)
        {
            string packageName = packageNames[i];
            currentCommandSize = currentCommandSize + packageName.Length + 1;
            if (currentCommandSize < 8000)
            {
                currentCommandEnd++;
            }
            else
            {
                string[] packages = packageNames[currentCommandStart..currentCommandEnd];
                tg.Go(() => installPackages(packages));
                currentCommandStart = currentCommandEnd;
                currentCommandSize = 100 + packageName.Length + 1;
                currentCommandEnd++;
            }
        }

        // Handle the final batch
        if (currentCommandStart < packageNames.Length)
        {
            string[] packages = packageNames[currentCommandStart..currentCommandEnd];
            tg.Go(() => installPackages(packages));
        }

        return tg.Wait();
    }

    // ata.go:318-349 — filterTypings.
    private string[] FilterTypings(
        global::Ts.TsPath.Path projectID,
        global::Ts.Project.Logging.Logger logger,
        string[] typingsToInstall)
    {
        _ = projectID;
        var result = new List<string>();
        foreach (string typing in typingsToInstall)
        {
            string typingKey = global::Ts.Module.Module.MangleScopedPackageName(typing);
            if (missingTypingsSet.TryLoad(typingKey, out _))
            {
                logger.Logf("ATA:: '{0}':: '{1}' is in missingTypingsSet - skipping...", typing, typingKey);
                continue;
            }
            (NameValidationResult validationResult, string name, bool isScopeName) = PackageNameValidation.ValidatePackageName(typing);
            if (validationResult != NameValidationResult.NameOk)
            {
                // add typing name to missing set so we won't process it again
                missingTypingsSet.Store(typingKey, true);
                logger.Log("ATA:: " + PackageNameValidation.RenderPackageNameValidationFailure(typing, validationResult, name, isScopeName));
                continue;
            }
            if (!typesRegistry!.TryGetValue(typingKey, out Dictionary<string, string>? typesRegistryEntry))
            {
                logger.Logf("ATA:: '{0}':: Entry for package '{1}' does not exist in local types registry - skipping...", typing, typingKey);
                continue;
            }
            if (packageNameToTypingLocation.TryLoad(typingKey, out CachedTyping? typingLocation) && DiscoverTypingsApi.IsTypingUpToDate(typingLocation!, typesRegistryEntry!))
            {
                logger.Logf("ATA:: '{0}':: '{1}' already has an up-to-date typing - skipping...", typing, typingKey);
                continue;
            }
            result.Add(typingKey);
        }
        return result.ToArray();
    }

    // ata.go:351-390 — init.
    private void Init(string projectID, global::Ts.Vfs.IFileSystem fs, global::Ts.Project.Logging.Logger logger)
    {
        _ = projectID;
        if (initialized)
        {
            return;
        }
        lock (initOnce)
        {
            if (initialized)
            {
                return;
            }
            logger.Log("ATA:: Global cache location '" + typingsLocation + "'"); //, safe file path '" + safeListPath + "', types map path '" + typesMapLocation + "`")
            ProcessCacheLocation(fs, logger);

            // !!! sheetal handle npm path here if we would support it
            // (commented Go tail omitted; see Go source lines 356-365)

            EnsureTypingsLocationExists(fs, logger);
            logger.Log("ATA:: Updating types-registry@latest npm package...");
            (byte[] _, Exception? err) = host.NpmInstall(typingsLocation, ["install", "--ignore-scripts", "types-registry@latest"]);
            if (err == null)
            {
                logger.Log("ATA:: Updated types-registry npm package");
            }
            else
            {
                logger.Logf("ATA:: Error updating types-registry package: {0}", err);
                // !!! sheetal events to send
                // (commented Go tail omitted; see Go source lines 373-385)
            }

            typesRegistry = LoadTypesRegistryFile(fs, logger);
            initialized = true;
        }
    }

    // ata.go:392-402 — npmConfig/npmDependecyEntry/npmLock.
    private sealed class NpmConfig
    {
        public Dictionary<string, string>? DevDependencies;
    }

    private sealed class NpmLock
    {
        public Dictionary<string, NpmDependencyEntry>? Dependencies;

        public Dictionary<string, NpmDependencyEntry>? Packages;
    }

    private sealed class NpmDependencyEntry
    {
        public string Version = "";
    }

    // ata.go:404-458 — processCacheLocation.
    private void ProcessCacheLocation(global::Ts.Vfs.IFileSystem fs, global::Ts.Project.Logging.Logger logger)
    {
        logger.Log("ATA:: Processing cache location " + typingsLocation);
        string packageJson = global::Ts.TsPath.TsPath.CombinePaths(typingsLocation, "package.json");
        string packageLockJson = global::Ts.TsPath.TsPath.CombinePaths(typingsLocation, "package-lock.json");
        logger.Log("ATA:: Trying to find '" + packageJson + "'...");
        if (fs.FileExists(packageJson) && fs.FileExists(packageLockJson))
        {
            var npmConfig = new NpmConfig();
            string npmConfigContents = ParseNpmConfigOrLock(fs, logger, packageJson, npmConfig);
            var npmLock = new NpmLock();
            string npmLockContents = ParseNpmConfigOrLock(fs, logger, packageLockJson, npmLock);

            logger.Log("ATA:: Loaded content of " + packageJson + ": " + npmConfigContents);
            logger.Log("ATA:: Loaded content of " + packageLockJson + ": " + npmLockContents);

            // !!! sheetal strada uses Node10
            var resolver = new global::Ts.Module.Resolver(host, new global::Ts.Core.CompilerOptions { ModuleResolution = global::Ts.Core.ModuleResolutionKind.NodeNext }, "", "");
            if (npmConfig.DevDependencies != null && (npmLock.Packages != null || npmLock.Dependencies != null))
            {
                // map-iter: 序无关 — each dev dependency is cached independently.
                foreach (string key in npmConfig.DevDependencies.Keys)
                {
                    // Go: npmLock.Packages["node_modules/"+key] — a read on a nil map is a
                    // safe zero-value miss in Go; only one of Packages/Dependencies is
                    // populated depending on the lockfile version (05-conventions.md §7.2).
                    NpmDependencyEntry? npmLockValue = null;
                    bool npmLockValueExists = npmLock.Packages != null && npmLock.Packages.TryGetValue("node_modules/" + key, out npmLockValue);
                    if (!npmLockValueExists)
                    {
                        npmLockValueExists = npmLock.Dependencies != null && npmLock.Dependencies.TryGetValue(key, out npmLockValue);
                    }
                    if (!npmLockValueExists)
                    {
                        // if package in package.json but not package-lock.json, skip adding to cache so it is reinstalled on next use
                        continue;
                    }
                    // key is @types/<package name>
                    string packageName = global::Ts.TsPath.TsPath.GetBaseFileName(key);
                    if (packageName == "")
                    {
                        continue;
                    }
                    string typingFile = TypingToFileName(resolver, packageName);
                    if (typingFile == "")
                    {
                        missingTypingsSet.Store(packageName, true);
                        continue;
                    }
                    if (packageNameToTypingLocation.TryLoad(packageName, out CachedTyping? existingTypingFile))
                    {
                        if (existingTypingFile!.TypingsLocation == typingFile)
                        {
                            continue;
                        }
                        logger.Log("ATA:: New typing for package " + packageName + " from " + typingFile + " conflicts with existing typing file " + existingTypingFile.TypingsLocation);
                    }
                    logger.Log("ATA:: Adding entry into typings cache: " + packageName + " => " + typingFile);
                    string version = npmLockValue!.Version;
                    if (version == "")
                    {
                        continue;
                    }
                    global::Ts.Semver.Version newVersion = global::Ts.Semver.Semver.MustParse(version);
                    var newTyping = new CachedTyping { TypingsLocation = typingFile, Version = newVersion };
                    packageNameToTypingLocation.Store(packageName, newTyping);
                }
            }
        }
        logger.Log("ATA:: Finished processing cache location " + typingsLocation);
    }

    // ata.go:460-464 — parseNpmConfigOrLock (two specializations of the Go generic).
    private static string ParseNpmConfigOrLock(global::Ts.Vfs.IFileSystem fs, global::Ts.Project.Logging.Logger logger, string location, NpmConfig config)
    {
        _ = logger;
        string contents = "";
        if (fs.TryReadFile(location, out string read))
        {
            contents = read;
        }
        config.DevDependencies = JsonDecodeStringMap(contents, "devDependencies");
        return contents;
    }

    private static string ParseNpmConfigOrLock(global::Ts.Vfs.IFileSystem fs, global::Ts.Project.Logging.Logger logger, string location, NpmLock config)
    {
        _ = logger;
        string contents = "";
        if (fs.TryReadFile(location, out string read))
        {
            contents = read;
        }
        (config.Dependencies, config.Packages) = JsonDecodeNpmLock(contents);
        return contents;
    }

    // ata.go:466-477 — ensureTypingsLocationExists.
    private void EnsureTypingsLocationExists(global::Ts.Vfs.IFileSystem fs, global::Ts.Project.Logging.Logger logger)
    {
        string npmConfigPath = global::Ts.TsPath.TsPath.CombinePaths(typingsLocation, "package.json");
        logger.Log("ATA:: Npm config file: " + npmConfigPath);

        if (!fs.FileExists(npmConfigPath))
        {
            logger.Logf("ATA:: Npm config file: '{0}' is missing, creating new one...", npmConfigPath);
            try
            {
                fs.WriteFile(npmConfigPath, "{ \"private\": true }");
            }
            catch (Exception err)
            {
                logger.Logf("ATA:: Npm config file write failed: {0}", err);
            }
        }
    }

    // ata.go:479-482 — typingToFileName.
    private string TypingToFileName(global::Ts.Module.Resolver resolver, string packageName)
    {
        (global::Ts.Module.ResolvedModule result, _) = resolver.ResolveModuleName(packageName, global::Ts.TsPath.TsPath.CombinePaths(typingsLocation, "index.d.ts"), global::Ts.Core.ModuleKind.None, null);
        return result.ResolvedFileName;
    }

    // ata.go:484-500 — loadTypesRegistryFile.
    private Dictionary<string, Dictionary<string, string>> LoadTypesRegistryFile(global::Ts.Vfs.IFileSystem fs, global::Ts.Project.Logging.Logger logger)
    {
        string typesRegistryFile = global::Ts.TsPath.TsPath.CombinePaths(typingsLocation, "node_modules/types-registry/index.json");
        if (fs.TryReadFile(typesRegistryFile, out string typesRegistryFileContents))
        {
            Dictionary<string, Dictionary<string, Dictionary<string, string>>>? entries = JsonDecodeTypesRegistry(typesRegistryFileContents);
            if (entries is not null && entries.TryGetValue("entries", out Dictionary<string, Dictionary<string, string>>? typesRegistry))
            {
                return typesRegistry;
            }
            logger.Logf("ATA:: Error when loading types registry file '{0}'", typesRegistryFile);
        }
        else
        {
            logger.Logf("ATA:: Error reading types registry file '{0}'", typesRegistryFile);
        }
        return [];
    }

    // Hand-rolled decoders for the three Go json.Unmarshal shapes above (Utf8JsonReader,
    // AOT-safe). Malformed input yields null/empty like Go's ignored json error.
    private static Dictionary<string, string>? JsonDecodeStringMap(string json, string fieldName)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
            if (!JsonDocumentPeekObject(ref reader, out Dictionary<string, JsonElement>? root) || root is null)
            {
                return null;
            }
            if (root.TryGetValue(fieldName, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonElement> kv in JsonEnumerateObject(value))
                {
                    if (kv.Value.ValueKind == JsonValueKind.String)
                    {
                        result[kv.Key] = kv.Value.GetString()!;
                    }
                }
                return result;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (Dictionary<string, NpmDependencyEntry>? Dependencies, Dictionary<string, NpmDependencyEntry>? Packages) JsonDecodeNpmLock(string json)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
            if (!JsonDocumentPeekObject(ref reader, out Dictionary<string, JsonElement>? root) || root is null)
            {
                return (null, null);
            }
            Dictionary<string, NpmDependencyEntry>? DecodeEntryMap(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                var result = new Dictionary<string, NpmDependencyEntry>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonElement> kv in JsonEnumerateObject(element))
                {
                    if (kv.Value.ValueKind == JsonValueKind.Object &&
                        kv.Value.TryGetProperty("version", out JsonElement version) &&
                        version.ValueKind == JsonValueKind.String)
                    {
                        result[kv.Key] = new NpmDependencyEntry { Version = version.GetString()! };
                    }
                }
                return result;
            }
            Dictionary<string, NpmDependencyEntry>? dependencies = root.TryGetValue("dependencies", out JsonElement deps) ? DecodeEntryMap(deps) : null;
            Dictionary<string, NpmDependencyEntry>? packages = root.TryGetValue("packages", out JsonElement pkgs) ? DecodeEntryMap(pkgs) : null;
            return (dependencies, packages);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, string>>>? JsonDecodeTypesRegistry(string json)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
            if (!JsonDocumentPeekObject(ref reader, out Dictionary<string, JsonElement>? root) || root is null)
            {
                return null;
            }
            var result = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> kv in root)
            {
                if (kv.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var entries = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, JsonElement> entry in JsonEnumerateObject(kv.Value))
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var distTags = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, JsonElement> tag in JsonEnumerateObject(entry.Value))
                    {
                        if (tag.Value.ValueKind == JsonValueKind.String)
                        {
                            distTags[tag.Key] = tag.Value.GetString()!;
                        }
                    }
                    entries[entry.Key] = distTags;
                }
                result[kv.Key] = entries;
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool JsonDocumentPeekObject(ref Utf8JsonReader reader, out Dictionary<string, JsonElement>? root)
    {
        root = null;
        if (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            root = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                root[property.Name] = property.Value.Clone();
            }
            return true;
        }
        return false;
    }

    private static IEnumerable<KeyValuePair<string, JsonElement>> JsonEnumerateObject(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            yield return new KeyValuePair<string, JsonElement>(property.Name, property.Value.Clone());
        }
    }
}

// Ported from tsgo internal/core/workgroup.go:92-123 — ThrottleGroup, hosted here
// because the C# core deliberately omits it (its only production consumer is this
// package; see Ts.Core.WorkGroup.cs header). Semaphore-slot semantics are preserved
// with SemaphoreSlim; goroutines become background threads.
internal sealed class ThrottleGroup
{
    private readonly SemaphoreSlim semaphore;
    private readonly CountdownWaitGroup group = new();
    private Exception? firstError;

    // workgroup.go:99-104 — NewThrottleGroup creates a new ThrottleGroup with the given
    // context and semaphore for concurrency limiting.
    public static ThrottleGroup NewThrottleGroup(CancellationToken ctx, SemaphoreSlim semaphore)
    {
        return new ThrottleGroup(semaphore);
    }

    private ThrottleGroup(SemaphoreSlim semaphore)
    {
        this.semaphore = semaphore;
    }

    // workgroup.go:106-118 — Go runs the given function, but first acquires a slot from
    // the semaphore. The semaphore slot is released when the function completes.
    public void Go(Func<Exception?> fn)
    {
        group.Go(() =>
        {
            // Acquire semaphore slot - this will block until a slot is available
            semaphore.Wait();
            try
            {
                Exception? err = fn();
                if (err is not null && firstError is null)
                {
                    firstError = err;
                }
            }
            finally
            {
                // Release semaphore slot when done
                semaphore.Release();
            }
        });
    }

    // workgroup.go:120-123 — Wait waits for all goroutines to complete and returns the
    // first error encountered, if any.
    public Exception? Wait()
    {
        group.Wait();
        return firstError;
    }
}

// Go: sync.WaitGroup + wg.Go, as used by Queue/ThrottleGroup in this package.
internal sealed class CountdownWaitGroup
{
    private int count;
    private readonly ManualResetEvent idle = new(true);

    public void Go(Action fn)
    {
        lock (idle)
        {
            count++;
            idle.Reset();
        }
        var thread = new Thread(() =>
        {
            try
            {
                fn();
            }
            finally
            {
                lock (idle)
                {
                    count--;
                    if (count == 0)
                    {
                        idle.Set();
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "ts-ata-throttle",
        };
        thread.Start();
    }

    public void Wait()
    {
        idle.WaitOne();
    }
}
