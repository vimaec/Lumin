﻿// Copyright (c) XRTK. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace XRTK.Definitions.Platforms
{
    /// <summary>
    /// Used by the XRTK to signal that the feature is available on the Lumin platform.
    /// </summary>
    public class LuminPlatform : BasePlatform
    {
        /// <inheritdoc />
        public override bool IsActive
        {
            get
            {
#if PLATFORM_LUMIN
                return !UnityEngine.Application.isEditor;
#else
                return false;
#endif
            }
        }

        /// <inheritdoc />
        public override bool IsBuildTargetAvailable
        {
            get
            {
#if UNITY_EDITOR
                return UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.Lumin;
#else
                return false;
#endif
            }
        }
    }
}