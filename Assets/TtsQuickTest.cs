// Quick Play Mode test for the NPC TTS plugin.
// No scene setup required: on Play, this creates the NpcTtsManager,
// waits for the worker to become healthy, and speaks a test line.
//
// Usage: open any scene, press Play, watch the Console (and listen).
// Optionally attach this script to a GameObject — either way it runs,
// because it is driven by RuntimeInitializeOnLoadMethod.

using UnityEngine;
using NpcTts;

public static class TtsQuickTest
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("TtsQuickTest");
        Object.DontDestroyOnLoad(go);

        var manager = go.AddComponent<NpcTtsManager>();
        manager.Endpoint = "http://127.0.0.1:8080";

        // Make sure the scene has an AudioListener.
        if (Object.FindAnyObjectByType<AudioListener>() == null)
            go.AddComponent<AudioListener>();

        manager.OnStarted += h => Debug.Log($"[TtsQuickTest] Started: {h.UtteranceId}");
        manager.OnFinished += h => Debug.Log($"[TtsQuickTest] Finished: {h.UtteranceId}");
        manager.OnCancelled += h => Debug.Log($"[TtsQuickTest] Cancelled: {h.UtteranceId}");
        manager.OnFailed += (h, msg) => Debug.LogError($"[TtsQuickTest] Failed: {h.UtteranceId} — {msg}");

        manager.StartCoroutine(Run(manager));
    }

    private static System.Collections.IEnumerator Run(NpcTtsManager manager)
    {
        Debug.Log("[TtsQuickTest] Waiting for worker health…");
        yield return manager.CheckHealth();

        if (!manager.IsWorkerHealthy)
        {
            Debug.LogError("[TtsQuickTest] Worker is not healthy. Start it with:\n" +
                           "  cd Worker && source .venv/bin/activate && python main.py --device mps");
            yield break;
        }

        Debug.Log("[TtsQuickTest] Worker healthy. Speaking test line…");
        manager.Speak(new SpeechRequest(
            utteranceId: "quick-test-1",
            npcId: "test-npc",
            voiceId: "default",
            text: "Testing, one two three. The speech worker is up and running.",
            language: "en"));
    }
}
