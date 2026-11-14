// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Editor build tools (minimal for now)

using UnityEngine;
using UnityEditor;
using UnityEditor.Callbacks;

namespace NpcTts.Editor
{
    /// <summary>Minimal build tools.</summary>
    public static class NpcTtsBuildTools
    {
        [MenuItem("Window/NPC TTS/Open Worker Manager")]
        public static void OpenWindow()
        {
            NpcTtsEditorWindow.ShowWindow();
        }
    }

    /// <summary>Postprocess callback for TTS-specific build settings.</summary>
    public class NpcTtsPostprocessor
    {
        [PostProcessBuild(1)]
        public static void OnPostprocessBuild(BuildTarget target, string path)
        {
            Debug.Log("[NpcTts] Built player at " + path + " for " + target);
        }
    }
}
