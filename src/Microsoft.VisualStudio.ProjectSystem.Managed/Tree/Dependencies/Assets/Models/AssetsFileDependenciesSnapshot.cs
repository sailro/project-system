﻿// Licensed to the .NET Foundation under one or more agreements. The .NET Foundation licenses this file to you under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using NuGet.Packaging;
using NuGet.ProjectModel;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Assets.Models
{
    /// <summary>
    /// Snapshot of data captured from <c>project.assets.json</c>. Immutable.
    /// </summary>
    internal sealed class AssetsFileDependenciesSnapshot
    {
        /// <summary>
        /// Gets the singleton empty instance.
        /// </summary>
        public static AssetsFileDependenciesSnapshot Empty { get; } = new AssetsFileDependenciesSnapshot(null, null);

        /// <summary>
        /// Shared object for parsing the lock file. May be used in parallel.
        /// </summary>
        private static readonly LockFileFormat s_lockFileFormat = new LockFileFormat();

        private readonly ImmutableDictionary<string, AssetsFileTarget> _dataByTarget;

        /// <summary>
        /// The <c>packageFolders</c> array from the assets file. The first is the 'user package folder',
        /// and any others are 'fallback package folders'.
        /// </summary>
        private readonly ImmutableArray<string> _packageFolders;

        /// <summary>
        /// Lazily populated instance of a NuGet type that performs package path resolution.  May be used in parallel.
        /// </summary>
        private FallbackPackagePathResolver? _packagePathResolver;

        /// <summary>
        /// Produces an updated snapshot by reading the <c>project.assets.json</c> file at <paramref name="path"/>.
        /// If the file could not be read, or no changes are detected, the current snapshot (this) is returned.
        /// </summary>
        public AssetsFileDependenciesSnapshot UpdateFromAssetsFile(string path)
        {
            try
            {
                // Parse the file
                using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096 * 10, FileOptions.SequentialScan);

                LockFile lockFile = s_lockFileFormat.Read(fileStream, path);

                return new AssetsFileDependenciesSnapshot(lockFile, this);
            }
            catch
            {
                return this;
            }
        }

        private AssetsFileDependenciesSnapshot(LockFile? lockFile, AssetsFileDependenciesSnapshot? previous)
        {
            if (lockFile == null)
            {
                _dataByTarget = ImmutableDictionary<string, AssetsFileTarget>.Empty;
                return;
            }

            Assumes.NotNull(previous);

            _packageFolders = lockFile.PackageFolders.Select(pf => pf.Path).ToImmutableArray();

            ImmutableDictionary<string, AssetsFileTarget>.Builder dataByTarget = ImmutableDictionary.CreateBuilder<string, AssetsFileTarget>(StringComparers.FrameworkIdentifiers); // TODO review comparer here -- should it be ignore case?

            foreach (LockFileTarget lockFileTarget in lockFile.Targets)
            {
                if (lockFileTarget.RuntimeIdentifier != null)
                {
                    // Skip "target/rid"s and only consume actual targets
                    continue;
                }

                previous._dataByTarget.TryGetValue(lockFileTarget.Name, out AssetsFileTarget? previousTarget);

                dataByTarget.Add(
                    lockFileTarget.Name,
                    new AssetsFileTarget(
                        lockFileTarget.Name,
                        ParseLogMessages(lockFile, previousTarget, lockFileTarget.Name),
                        ParseLibraries(lockFileTarget)));
            }

            _dataByTarget = dataByTarget.ToImmutable();
            return;

            static ImmutableArray<AssetsFileLogMessage> ParseLogMessages(LockFile lockFile, AssetsFileTarget previousTarget, string target)
            {
                if (lockFile.LogMessages.Count == 0)
                {
                    return ImmutableArray<AssetsFileLogMessage>.Empty;
                }

                // Filter log messages to our target
                ImmutableArray<AssetsFileLogMessage> previousLogs = previousTarget?.Logs ?? ImmutableArray<AssetsFileLogMessage>.Empty;
                ImmutableArray<AssetsFileLogMessage>.Builder builder = ImmutableArray.CreateBuilder<AssetsFileLogMessage>();

                int j = 0;
                foreach (IAssetsLogMessage logMessage in lockFile.LogMessages)
                {
                    if (!logMessage.TargetGraphs.Contains(target))
                    {
                        continue;
                    }

                    j++;

                    if (j < previousLogs.Length && previousLogs[j].Equals(logMessage))
                    {
                        // Unchanged, so use previous value
                        builder.Add(previousLogs[j]);
                    }
                    else
                    {
                        builder.Add(new AssetsFileLogMessage(logMessage));
                    }
                }

                return builder.ToImmutable();
            }

            static ImmutableDictionary<string, AssetsFileTargetLibrary> ParseLibraries(LockFileTarget lockFileTarget)
            {
                ImmutableDictionary<string, AssetsFileTargetLibrary>.Builder builder = ImmutableDictionary.CreateBuilder<string, AssetsFileTargetLibrary>(StringComparers.LibraryNames);

                foreach (LockFileTargetLibrary lockFileLibrary in lockFileTarget.Libraries)
                {
                    if (AssetsFileTargetLibrary.TryCreate(lockFileLibrary, out AssetsFileTargetLibrary? library))
                    {
                        builder.Add(library.Name, library);
                    }
                }

                return builder.ToImmutable();
            }
        }

        public bool TryGetLogMessages(string? target, out ImmutableArray<AssetsFileLogMessage> logMessages)
        {
            if (!TryGetTarget(target, out AssetsFileTarget? targetData))
            {
                logMessages = default;
                return false;
            }

            logMessages = targetData.Logs;
            return true;
        }

        public bool TryGetDependencies(string libraryName, string? version, string? target, out ImmutableArray<AssetsFileTargetLibrary> dependencies)
        {
            if (!TryGetTarget(target, out AssetsFileTarget? targetData))
            {
                dependencies = default;
                return false;
            }

            if (!targetData.LibraryByName.TryGetValue(libraryName, out AssetsFileTargetLibrary library))
            {
                dependencies = default;
                return false;
            }

            if (version != null && library.Version != version)
            {
                dependencies = default;
                return false;
            }

            ImmutableArray<AssetsFileTargetLibrary>.Builder builder = ImmutableArray.CreateBuilder<AssetsFileTargetLibrary>(library.Dependencies.Length);

            foreach (string dependencyName in library.Dependencies)
            {
                if (targetData.LibraryByName.TryGetValue(dependencyName, out AssetsFileTargetLibrary dependency))
                {
                    builder.Add(dependency);
                }
            }

            if (builder.Count != library.Dependencies.Length)
            {
                System.Diagnostics.Debug.Fail("At least one advertised dependency was not found");
                dependencies = default;
                return false;
            }

            dependencies = builder.MoveToImmutable();
            return true;
        }

        public bool TryGetPackage(string packageId, string version, string? target, [NotNullWhen(returnValue: true)] out AssetsFileTargetLibrary? assetsFileLibrary)
        {
            if (!TryGetTarget(target, out AssetsFileTarget? targetData))
            {
                assetsFileLibrary = default;
                return false;
            }

            if (targetData.LibraryByName.TryGetValue(packageId, out assetsFileLibrary) &&
                assetsFileLibrary.Type == AssetsFileLibraryType.Package &&
                assetsFileLibrary.Version == version)
            {
                return true;
            }

            assetsFileLibrary = default;
            return false;
        }

        private bool TryGetTarget(string? target, [NotNullWhen(returnValue: true)] out AssetsFileTarget? targetData)
        {
            if (_dataByTarget.Count == 0)
            {
                targetData = default;
                return false;
            }

            if (target == null)
            {
                if (_dataByTarget.Count != 1)
                {
                    // This is unexpected
                    System.Diagnostics.Debug.Fail("No target known, yet more than one target exists");
                    targetData = default;
                    return false;
                }

                targetData = _dataByTarget.First().Value;
            }
            else if (!_dataByTarget.TryGetValue(target, out targetData))
            {
                targetData = default;
                return false;
            }

            return true;
        }

        public bool TryResolvePackagePath(string packageId, string version, out string? fullPath)
        {
            Requires.NotNull(packageId, nameof(packageId));
            Requires.NotNull(version, nameof(version));

            if (_packageFolders.IsEmpty)
            {
                fullPath = default;
                return false;
            }

            try
            {
                _packagePathResolver ??= new FallbackPackagePathResolver(_packageFolders[0], _packageFolders.Skip(1));

                fullPath = _packagePathResolver.GetPackageDirectory(packageId, version);
                return true;
            }
            catch
            {
                fullPath = default;
                return false;
            }
        }
    }
}