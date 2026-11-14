// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Editor window for worker management

using UnityEditor;
using UnityEngine;

namespace NpcTts.Editor
{
    /// <summary>Editor window for managing the TTS worker connection.</summary>
    public class NpcTtsEditorWindow : EditorWindow
    {
        private string _endpoint = "http://127.0.0.1:8080";
        private string _status = "Not connected";
        private string _model = "";
        private NpcTtsManager _manager;
        private Vector2 _scroll;

        [MenuItem("Window/NPC TTS/Worker Manager")]
        public static void ShowWindow()
        {
            GetWindow<NpcTtsEditorWindow>("TTS Worker");
        }

        private void OnEnable()
        {
            _manager = (NpcTtsManager)UnityEngine.Object.FindObjectOfType(typeof(NpcTtsManager));
            if (_manager != null)
                _endpoint = _manager.Endpoint;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Worker Connection", EditorStyles.boldLabel);

            _endpoint = EditorGUILayout.TextField("Endpoint", _endpoint);
            if (GUI.changed && _manager != null)
                _manager.Endpoint = _endpoint;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Check Health", GUILayout.Width(120)))
            {
                if (_manager != null && Application.isPlaying)
                    _manager.StartCoroutine(_manager.CheckHealth());
            }
            if (GUILayout.Button("Run Probe", GUILayout.Width(100)))
            {
                if (_manager != null && Application.isPlaying)
                {
                    var request = new SpeechRequest("probe-1", "probe-npc", "default", "Hello world!");
                    _manager.Speak(request);
                }
                else
                {
                    EditorUtility.DisplayDialog("NPC TTS", "Must be in Play Mode to run probes.", "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Status: " + _status);
            if (!string.IsNullOrEmpty(_model))
                EditorGUILayout.LabelField("Model: " + _model);

            EditorGUILayout.EndScrollView();
        }
    }
}
