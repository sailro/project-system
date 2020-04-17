﻿// Licensed to the .NET Foundation under one or more agreements. The .NET Foundation licenses this file to you under the MIT license. See the LICENSE.md file in the project root for more information.

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Assets.Models
{
    /// <summary>
    /// Enumeration of types of library found in the assets file.
    /// </summary>
    internal enum AssetsFileLibraryType : byte
    {
        Package,
        Project
    }
}