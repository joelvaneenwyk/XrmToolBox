﻿using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using XrmToolBox.Extensibility.Manifest;
using XrmToolBox.PluginsStore.DTO;

namespace XrmToolBox.PluginsStore
{
    public class MyLogger : ILogger
    {
        public void Log(LogLevel level, string data)
        {
        }

        public void Log(ILogMessage message)
        {
        }

        public Task LogAsync(LogLevel level, string data)
        {
            return null;
        }

        public Task LogAsync(ILogMessage message)
        {
            return null;
        }

        public void LogDebug(string data)
        {
        }

        public void LogError(string data)
        {
        }

        public void LogInformation(string data)
        {
        }

        public void LogInformationSummary(string data)
        {
        }

        public void LogMinimal(string data)
        {
        }

        public void LogVerbose(string data)
        {
        }

        public void LogWarning(string data)
        {
        }
    }

    public class StoreFromPortal : IToolLibrary

    {
        #region Variables

        public static readonly Version MinCompatibleVersion = new Version(1, 2015, 12, 20);

        private readonly string applicationPluginsFolder;
        private readonly string nugetPluginsFolder;
        private SourceCacheContext cache = new SourceCacheContext { DirectDownload = true, NoCache = true };
        private CancellationToken cancellationToken = CancellationToken.None;
        private FindPackageByIdResource findPackageById;
        private ILogger logger = NullLogger.Instance;
        private PackageSearchResource packageSearch;
        private FileInfo[] plugins;

        #endregion Variables

        #region Constructor

        public StoreFromPortal(bool allowConnectionControlsPreReleaseSearch)
        {
            AllowConnectionControlPreRelease = allowConnectionControlsPreReleaseSearch;

            // Initializing folders variables
            nugetPluginsFolder = Path.Combine(Paths.XrmToolBoxPath, "NugetPlugins");
            applicationPluginsFolder = Paths.PluginsPath;

            // Reading existing plugins files
            plugins = new DirectoryInfo(applicationPluginsFolder).GetFiles();
        }

        #endregion Constructor

        #region Events

        public event EventHandler<ToolInformationEventArgs> OnDownloadingTool;

        public event EventHandler PluginsUpdated;

        #endregion Events

        #region Properties

        public bool AllowConnectionControlPreRelease { get; set; }

        public List<string> Categories { get; set; }

        public int PluginsCount => XrmToolBoxPlugins?.Plugins.Count ?? 0;
        public int PluginsUpdateCount => XrmToolBoxPlugins?.Plugins.Count(p => p.Action == PackageInstallAction.Update) ?? 0;

        public int PluginsUpdatesCount => throw new NotImplementedException();
        public List<IXrmToolBoxLibraryTool> Tools => XrmToolBoxPlugins.Plugins.Select(p => (IXrmToolBoxLibraryTool)p).ToList();
        public XtbPlugins XrmToolBoxPlugins { get; set; }

        #endregion Properties

        public async Task LoadNuget()
        {
            SourceRepository repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
            packageSearch = await repository.GetResourceAsync<PackageSearchResource>();
            findPackageById = await repository.GetResourceAsync<FindPackageByIdResource>();
        }

        public async Task LoadTools(bool fromStorePortal = true)
        {
            plugins = new DirectoryInfo(applicationPluginsFolder).GetFiles();
            XrmToolBoxPlugins = new XtbPlugins();

            await Task.Run(
                () =>
                {
                    string url = "https://www.xrmtoolbox.com/_odata/plugins";
                    do
                    {
                        var tmpPlugins = GetContent<XtbPlugins>(url, fromStorePortal);
                        XrmToolBoxPlugins.Plugins.AddRange(tmpPlugins.Plugins);
                        url = tmpPlugins.NextLink;
                    }
                    while (url != null);
                });

            Categories = XrmToolBoxPlugins.Plugins
                .Where(p => p.CategoriesList != null)
                .SelectMany(p => p.CategoriesList?.Split(','))
                .Distinct()
                .OrderByDescending(s => s)
                .ToList();

            Parallel.ForEach(XrmToolBoxPlugins.Plugins,
                plugin =>
                {
                    AnalyzePackage(plugin);
                });
        }

        #region Connection controls installation

        public async Task<ConnectionControlsUpdateSettings> IsConnectionControlsUpdateAvailable(string currentStoredVersion)
        {
            var ca = typeof(McTools.Xrm.Connection.ConnectionDetail).Assembly;
            var metadata = (await packageSearch.SearchAsync("mscrmtools.xrm.connection", new SearchFilter(AllowConnectionControlPreRelease, SearchFilterType.IsLatestVersion), 0, 1, logger, cancellationToken)).FirstOrDefault();
            var nugetVersion = (await metadata.GetVersionsAsync()).Max(v => v.Version);
            string releasenotes = "N/A";

            using (MemoryStream packageStream = new MemoryStream())
            {
                if (!await findPackageById.CopyNupkgToStreamAsync(
                        "MscrmTools.Xrm.Connection",
                        nugetVersion,
                        packageStream,
                        cache,
                        new MyLogger(),
                        cancellationToken))
                {
                    throw new Exception($"The Nuget package for connection controls ({nugetVersion.Version}) has not been found");
                }

                using (PackageArchiveReader packageReader = new PackageArchiveReader(packageStream))
                {
                    releasenotes = packageReader.NuspecReader.GetReleaseNotes();
                }
            }

            string version = nugetVersion.Version.ToString() + (nugetVersion.IsPrerelease ? "-" + nugetVersion.Release : "");
            // initial run, curr stored version may be null
            var currStoredVer = (currentStoredVersion != null) ? new Version(currentStoredVersion.Split('-')[0]) : null;

            bool isNewVersion = ca.GetName().Version < nugetVersion.Version
                   || ca.GetName().Version == nugetVersion.Version &&
                   new Version(version.Split('-')[0]) == currStoredVer &&
                   version != currentStoredVersion;

            return new ConnectionControlsUpdateSettings
            {
                NewVersion = isNewVersion,
                Version = version,
                ReleaseNotes = releasenotes
            };
        }

        public async Task<ConnectionControlsUpdateSettings> PrepareConnectionControlsUpdate(Control parentControl, bool installOnNextRestart)
        {
            var updates = new PluginUpdates { PreviousProcessId = Process.GetCurrentProcess().Id };

            var nugetVersion = (await AddPackageToInstall("mscrmtools.xrm.connection", updates));
            await AddPackageToInstall("Microsoft.CrmSdk.XrmTooling.CoreAssembly", updates);
            await AddPackageToInstall("Microsoft.CrmSdk.XrmTooling.WpfControls", updates);

            XmlSerializerHelper.SerializeToFile(updates, Path.Combine(Paths.XrmToolBoxPath, "Update.xml"));

            bool returnedValue = false;
            parentControl.Invoke(new Action(() =>
            {
                if (!installOnNextRestart && DialogResult.Yes == MessageBox.Show(parentControl,
                        @"This application needs to restart to install new connection controls. Click Yes to restart this application now",
                        @"Information", MessageBoxButtons.YesNo, MessageBoxIcon.Information))
                {
                    returnedValue = true;
                }
            }));

            return new ConnectionControlsUpdateSettings
            {
                RestartNow = returnedValue,
                Version = nugetVersion.Version + (!string.IsNullOrEmpty(nugetVersion.Release) ? "-" + nugetVersion.Release : "")
            };
        }

        private async Task<NuGetVersion> AddPackageToInstall(string packageName, PluginUpdates updates)
        {
            var metadata = (await packageSearch.SearchAsync(packageName, new SearchFilter(AllowConnectionControlPreRelease, SearchFilterType.IsLatestVersion), 0, 1, logger, cancellationToken)).FirstOrDefault();
            var nugetVersion = (await metadata.GetVersionsAsync()).Max(v => v.Version);

            using (MemoryStream packageStream = new MemoryStream())
            {
                if (!await findPackageById.CopyNupkgToStreamAsync(
                     packageName,
                     nugetVersion,
                     packageStream,
                     cache,
                     logger,
                     cancellationToken))
                {
                    throw new Exception($"The Nuget package for {packageName} ({nugetVersion.Version}) has not been found");
                }

                var packageFolder = Path.Combine(nugetPluginsFolder, $"{packageName}.{nugetVersion.Version}");
                var currentLocation = Assembly.GetExecutingAssembly().Location;
                var folder = Path.GetDirectoryName(currentLocation);

                if (!Directory.Exists(packageFolder))
                {
                    Directory.CreateDirectory(packageFolder);
                }

                using (PackageArchiveReader packageReader = new PackageArchiveReader(packageStream))
                {
                    packageReader.NuspecReader.GetReleaseNotes();

                    foreach (var packageFile in await packageReader.GetFilesAsync(cancellationToken))
                    {
                        if (packageFile.ToLower().EndsWith(".dll"))
                        {
                            using (var fileStream = File.OpenWrite(Path.Combine(packageFolder, Path.GetFileName(packageFile))))
                            using (var stream = await packageReader.GetStreamAsync(packageFile, cancellationToken))
                            {
                                await stream.CopyToAsync(fileStream);
                            }

                            updates.Plugins.Add(new PluginUpdate
                            {
                                Source = Path.Combine(packageFolder, Path.GetFileName(packageFile)),
                                Destination = Path.Combine(folder, Path.GetFileName(packageFile)),
                                RequireRestart = true
                            });
                        }
                    }
                }
            }

            return nugetVersion;
        }

        #endregion Connection controls installation

        #region Tools Installation

        public bool PerformInstallation(PluginUpdates updates, Form form)
        {
            if (updates.Plugins.Any(p => p.RequireRestart))
            {
                XmlSerializerHelper.SerializeToFile(updates, Path.Combine(Paths.XrmToolBoxPath, "Update.xml"));

                if (form is StoreFormFromPortal storeForm)
                {
                    form.Invoke(new Action(() =>
                    {
                        if (DialogResult.Yes == MessageBox.Show(form,
                                @"This application needs to restart to install updated tools (or new tools that share some files with already installed tools). Click Yes to restart this application now",
                                @"Information", MessageBoxButtons.YesNo, MessageBoxIcon.Information))
                        {
                            storeForm.AskForPluginsClosing();

                            Application.Restart();
                        }
                    }));
                }

                return false;
            }

            foreach (var pu in updates.Plugins)
            {
                try
                {
                    // Can install plugin directly
                    var destinationDirectory = Path.GetDirectoryName(pu.Destination);
                    if (destinationDirectory == null)
                    {
                        continue;
                    }

                    if (!Directory.Exists(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }
                    File.Copy(pu.Source, pu.Destination, true);
                }
                catch (Exception error)
                {
                    MessageBox.Show("An error occured while copying files: " + error.Message +
                                    "\r\n\r\nCopy has been aborted", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }

            PluginsUpdated?.Invoke(this, new EventArgs());
            return true;
        }

        public async Task<PluginUpdates> PrepareInstallationPackages(List<XtbPlugin> pluginsToInstall)
        {
            var pus = new PluginUpdates { PreviousProcessId = Process.GetCurrentProcess().Id };
            int i = 0;
            foreach (var plugin in pluginsToInstall)
            {
                OnDownloadingTool?.Invoke(this, new ToolInformationEventArgs { ToolName = plugin.Name, ProgressPercentage = i * 100 / pluginsToInstall.Count });
                i++;

                var version = new NuGetVersion(plugin.Version);

                using (MemoryStream packageStream = new MemoryStream())
                {
                    if (!await findPackageById.CopyNupkgToStreamAsync(
                         plugin.NugetId.ToLower(),
                         version,
                         packageStream,
                         cache,
                         logger,
                         cancellationToken))
                    {
                        throw new Exception($"The Nuget package for tool {plugin.NugetId} ({version}) has not been found");
                    }

                    if (plugin.Action == PackageInstallAction.Unavailable)
                    {
                        if (!string.IsNullOrEmpty(plugin.ProjectUrl))
                        {
                            var message =
                                $"{plugin.Name} is incompatible with this version of XrmToolBox.\nOpen project URL?";
                            if (DialogResult.Yes == MessageBox.Show(message, "Incompatible tool", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation))
                            {
                                Process.Start(plugin.ProjectUrl);
                            }
                        }
                        else
                        {
                            MessageBox.Show(
                                $"{plugin.Name} is incompatible with this version of XrmToolBox.",
                                "Incompatible tool", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                        }
                        continue;
                    }

                    var packageFolder = Path.Combine(nugetPluginsFolder, $"{plugin.NugetId}.{version.Version}");

                    using (PackageArchiveReader packageReader = new PackageArchiveReader(packageStream))
                    {
                        foreach (var packageFile in await packageReader.GetFilesAsync(cancellationToken))
                        {
                            if (packageFile.ToLower().IndexOf("plugins/") >= 0)
                            {
                                if (!Directory.Exists(packageFolder))
                                {
                                    Directory.CreateDirectory(packageFolder);
                                }

                                var relativeFilePath = packageFile.Remove(0, packageFile.ToLower().IndexOf("plugins/") + 8);
                                var filePath = Path.Combine(packageFolder, relativeFilePath);
                                var fi = new FileInfo(filePath);

                                if (!Directory.Exists(fi.Directory.FullName))
                                {
                                    Directory.CreateDirectory(fi.Directory.FullName);
                                }

                                using (var fileStream = File.OpenWrite(filePath))
                                using (var stream = await packageReader.GetStreamAsync(packageFile, cancellationToken))
                                {
                                    await stream.CopyToAsync(fileStream);
                                }

                                var destinationFile = Path.Combine(Paths.PluginsPath, relativeFilePath);

                                // XrmToolBox restart is required when a plugin has to be
                                // updated or when a new plugin shares files with other
                                // plugin(s) already installed
                                if (plugin.RequiresXtbRestart)
                                {
                                    pus.Plugins.Add(new PluginUpdate
                                    {
                                        Source = filePath,
                                        Destination = destinationFile,
                                        RequireRestart = true
                                    });
                                }
                                else if (plugin.Action == PackageInstallAction.Install)
                                {
                                    pus.Plugins.Add(new PluginUpdate
                                    {
                                        Source = filePath,
                                        Destination = destinationFile,
                                        RequireRestart = false
                                    });
                                }
                            }
                        }
                    }
                }
            }

            return pus;
        }

        private void AnalyzePackage(XtbPlugin plugin)
        {
            var files = plugin.Files;

            bool install = false, update = false, otherFilesFound = false;

            Version version;

            if (string.IsNullOrEmpty(plugin.MinimalXrmToolBoxVersion) || !Version.TryParse(plugin.MinimalXrmToolBoxVersion, out version))
            {
                plugin.Compatibilty = CompatibleState.Other;
            }
            else
            {
                plugin.Compatibilty = IsPluginDependencyCompatible(version);
            }

            var currentVersion = new Version(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
            var currentVersionFound = false;

            var manifest = ManifestLoader.LoadDefaultManifest();

            foreach (var file in files)
            {
                if (Path.GetFileName(file).Length == 0)
                    continue;

                var directoryName = Path.GetDirectoryName(file);
                if (directoryName == null)
                {
                    continue;
                }

                if (directoryName.ToLower().EndsWith("plugins"))
                {
                    // Only check version of files in the Plugins folder
                    var existingPluginFile =
                        plugins.FirstOrDefault(p => file.ToLower().EndsWith(p.Name.ToLower()));
                    if (existingPluginFile == null)
                    {
                        install = true;
                    }
                    else
                    {
                        var pluginInfo = manifest.PluginMetadata
                            .FirstOrDefault(p => p.AssemblyFilename.ToLower() == existingPluginFile.FullName.ToLower());

                        // If a file is found, we check version only if the file
                        // contains classes that implement IXrmToolBoxPlugin
                        if (pluginInfo == null)
                        {
                            otherFilesFound = true;
                            continue;
                        }

                        var existingFileVersion = new Version(pluginInfo.Version);

                        if (existingFileVersion < currentVersion)
                        {
                            currentVersion = existingFileVersion;
                            currentVersionFound = true;
                        }

                        if (existingFileVersion < new Version(plugin.Version))
                        {
                            update = true;
                        }
                    }
                }
            }

            if (currentVersionFound)
            {
                plugin.CurrentVersion = currentVersion;
            }

            if (otherFilesFound || update)
            {
                plugin.RequiresXtbRestart = true;
            }

            if (plugin.Compatibilty != CompatibleState.Compatible)
            {
                plugin.Action = PackageInstallAction.Unavailable;
            }
            else if (update)
            {
                plugin.Action = PackageInstallAction.Update;
            }
            else if (install)
            {
                plugin.Action = PackageInstallAction.Install;
            }
            else
            {
                plugin.Action = PackageInstallAction.None;
            }
        }

        #endregion Tools Installation

        #region Tools Uninstallation

        public void PerformUninstallation(PluginDeletions deletions)
        {
            string filePath = Path.Combine(Paths.XrmToolBoxPath, "Deletion.xml");

            if (File.Exists(filePath))
            {
                using (StreamReader reader = new StreamReader(filePath))
                {
                    var existingPds = (PluginDeletions)XmlSerializerHelper.Deserialize(reader.ReadToEnd(), typeof(PluginDeletions));
                    deletions.Plugins.AddRange(existingPds.Plugins);
                }
            }

            XmlSerializerHelper.SerializeToFile(deletions, filePath);

            if (DialogResult.Yes == MessageBox.Show(
                "This application needs to restart to remove tools. Click Yes to restart this application now",
                "Information", MessageBoxButtons.YesNo, MessageBoxIcon.Information))
            {
                Application.Restart();
            }
        }

        public PluginDeletions PrepareUninstallPlugins(List<XtbPlugin> pluginsTodelete)
        {
            var pds = new PluginDeletions { PreviousProcessId = Process.GetCurrentProcess().Id };

            // Get list of files to delete
            foreach (var plugin in pluginsTodelete)
            {
                var conflicts = XrmToolBoxPlugins.Plugins.Where(p =>
                    p.Id != plugin.Id
                    && (p.Action == PackageInstallAction.None || p.Action == PackageInstallAction.Update)
                    && plugin.Files.Any(ff => p.Files.Contains(ff))).ToList();

                if (conflicts.Any())
                {
                    var conflictedFiles = conflicts.SelectMany(c => c.Files).Select(f => f);

                    pds.Plugins.Add(new PluginDeletion
                    {
                        Conflict = true,
                        Files = plugin.Files.Where(f => !conflictedFiles.Contains(f)).Select(f => Path.GetFileName(f)).ToList()
                    });
                }
                else
                {
                    pds.Plugins.Add(new PluginDeletion
                    {
                        Files = plugin.Files.Select(f => Path.GetFileName(f)).ToList()
                    });
                }
            }

            return pds;
        }

        public void UninstallByFileName(string fileName)
        {
            var plugin = GetPluginByFileName(fileName.ToLower());

            if (plugin != null)
            {
                var pds = PrepareUninstallPlugins(new List<XtbPlugin> { plugin });
                PerformUninstallation(pds);
            }
        }

        #endregion Tools Uninstallation

        #region Utils

        public long CalculateCacheFolderSize()
        {
            if (Directory.Exists(nugetPluginsFolder))
            {
                var size = GetDirectorySize(nugetPluginsFolder);
                return size / 1024 / 1024;
            }

            return 0;
        }

        public long CleanCacheFolder()
        {
            if (Directory.Exists(nugetPluginsFolder))
            {
                foreach (var item in new DirectoryInfo(nugetPluginsFolder).GetFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        Directory.Delete(item.FullName, true);
                    }
                    else
                    {
                        File.Delete(item.FullName);
                    }
                }

                return CalculateCacheFolderSize();
            }

            return 0;
        }

        public XtbPlugin GetPluginByFileName(string filename)
        {
            if (XrmToolBoxPlugins == null)
            {
                LoadTools().Wait();
            }

            return XrmToolBoxPlugins.Plugins.FirstOrDefault(p => p.Files.Any(f => f.ToLower().IndexOf(filename.ToLower(), StringComparison.Ordinal) >= 0));
        }

        public string GetPluginProjectUrlByFileName(string fileName)
        {
            XtbPlugin plugin = GetPluginByFileName(fileName);
            return plugin?.ProjectUrl;
        }

        public XtbPlugin GetPluginUpdateByFile(string filepath)
        {
            var fi = new FileInfo(filepath);
            var plugin = XrmToolBoxPlugins.Plugins.FirstOrDefault(p => p.Files.Any(f => f.ToLower().Contains(fi.Name.ToLower())));
            if (plugin != null && plugin.Action == PackageInstallAction.Update)
            {
                return plugin;
            }

            return null;
        }

        IXrmToolBoxLibraryTool IToolLibrary.GetPluginUpdateByFile(string location)
        {
            throw new NotImplementedException();
        }

        public void InstallOneToolUpdate(IXrmToolBoxLibraryTool tool, bool onNextRestart, Form form)
        {
            PrepareInstallationPackages(new List<XtbPlugin> { (XtbPlugin)tool }).Wait();

            if (!onNextRestart)
            {
                if (DialogResult.Yes == MessageBox.Show(form,
                        @"This application needs to restart to install updated tools (or new tools that share some files with already installed tools). Click Yes to restart this application now",
                        @"Information", MessageBoxButtons.YesNo, MessageBoxIcon.Information))
                {
                    Application.Restart();
                }
            }
        }

        Task<IConnectionControlUpdateSettings> IToolLibrary.IsConnectionControlsUpdateAvailable(string connectionControlsVersion)
        {
            throw new NotImplementedException();
        }

        public Task<IConnectionControlUpdateSettings> PrepareConnectionControlsUpdate(Form form, bool restart)
        {
            throw new NotImplementedException();
        }

        private T GetContent<T>(string url, bool fromStoreFromPortalForm) where T : new()
        {
            try
            {
                var request = WebRequest.CreateHttp(url);
                var response = request.GetResponse();
                using (Stream dataStream = response.GetResponseStream())
                {
                    if (dataStream != null)
                    {
                        var serializer = new DataContractJsonSerializer(typeof(T),
                            new DataContractJsonSerializerSettings
                            {
                                UseSimpleDictionaryFormat = true,
                                DateTimeFormat = new DateTimeFormat("yyyy-MM-dd'T'HH:mm:ss", new DateTimeFormatInfo { FullDateTimePattern = "yyyy-MM-dd'T'HH:mm:ss" })
                            });

                        return (T)serializer.ReadObject(dataStream);
                    }
                }
            }
            catch (WebException error)
            {
                if (fromStoreFromPortalForm &&
                    (error.Status == WebExceptionStatus.ConnectFailure
                    || error.Status == WebExceptionStatus.NameResolutionFailure
                    || error.Status == WebExceptionStatus.ProxyNameResolutionFailure))
                {
                    throw new Exception($"Unable to connect to {url}. Please check your network settings", error);
                }
            }
            return new T();
        }

        private long GetDirectorySize(string path)
        {
            string[] files = Directory.GetFiles(path);
            string[] subdirectories = Directory.GetDirectories(path);

            long size = files.Sum(x => new FileInfo(x).Length);
            foreach (string s in subdirectories)
                size += GetDirectorySize(s);

            return size;
        }

        /// <summary>
        /// Verify version plugin is built for with current XTB version and a compatibility list
        /// </summary>
        /// <param name="xtbDependencyVersion"></param>
        /// <returns></returns>
        private CompatibleState IsPluginDependencyCompatible(Version xtbDependencyVersion)
        {
            if (xtbDependencyVersion >= MinCompatibleVersion
                && xtbDependencyVersion <= Assembly.GetEntryAssembly().GetName().Version)
            {
                return CompatibleState.Compatible;
            }

            if (xtbDependencyVersion < MinCompatibleVersion)
            {
                return CompatibleState.DoesntFitMinimumVersion;
            }

            if (xtbDependencyVersion > Assembly.GetEntryAssembly().GetName().Version)
            {
                return CompatibleState.RequireNewVersionOfXtb;
            }

            return CompatibleState.Other;
        }

        #endregion Utils
    }
}