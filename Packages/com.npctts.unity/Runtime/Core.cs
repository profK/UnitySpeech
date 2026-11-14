// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Core types for NPC speech synthesis

using System;
using System.Threading;

namespace NpcTts
{
    /// <summary>
    /// Request to synthesize speech for an NPC utterance.
/// </summary>
public readonly struct SpeechRequest
{
    public string UtteranceId { get; }
    public string NpcId { get; }
    public string VoiceId { get; }
    public string Language { get; }
    public string Text { get; }
    public TimeSpan? Deadline { get; }

    public SpeechRequest(
        string utteranceId,
        string npcId,
        string voiceId,
        string text,
        string language = "en",
        TimeSpan? deadline = null)
    {
        UtteranceId = utteranceId ?? throw new ArgumentNullException(nameof(utteranceId));
        NpcId = npcId ?? throw new ArgumentNullException(nameof(npcId));
        VoiceId = voiceId ?? throw new ArgumentNullException(nameof(voiceId));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Language = language ?? throw new ArgumentNullException(nameof(language));
        Deadline = deadline;
    }
}

/// <summary>
/// Terminal reason for a failed or cancelled speech request.
/// </summary>
public enum TerminalReason
{
    Ok = 0,
    UnknownVoice = 1,
    QueueOverload = 2,
    WorkerUnavailable = 3,
    Timeout = 4,
    ProtocolError = 5,
    PlaybackFailure = 6,
    InferenceError = 7,
}

/// <summary>
/// Status of a speech handle.
/// </summary>
public enum SpeechStatus
{
    Pending,
    Queued,
    Active,     // Worker is synthesizing
    Playing,    // Audio is playing back
    Finished,
    Cancelled,
    Failed,
}

/// <summary>
/// Handle returned by NpcTtsManager.Speak().
/// Provides status, completion, and cancellation for an utterance.
/// </summary>
public sealed class SpeechHandle : IDisposable
{
    private CancellationTokenSource? _cts;
    private volatile SpeechStatus _status;

    internal SpeechHandle(string utteranceId, string npcId, TerminalReason reason = TerminalReason.Ok)
    {
        UtteranceId = utteranceId;
        NpcId = npcId;
        _status = reason == TerminalReason.Ok ? SpeechStatus.Pending : SpeechStatus.Failed;
        TerminalReason = reason;
        IsCompleted = reason != TerminalReason.Ok;
    }

    public string UtteranceId { get; }
    public string NpcId { get; }
    public SpeechStatus Status => _status;
    public TerminalReason TerminalReason { get; internal set; }
    public bool IsCompleted { get; internal set; }

    /// <summary>
    /// Cancel this utterance. Idempotent.
    /// </summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    internal void SetCancellationTokenSource(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    internal void SetStatus(SpeechStatus status)
    {
        _status = status;
        if (status is SpeechStatus.Finished or SpeechStatus.Cancelled or SpeechStatus.Failed)
            IsCompleted = true;
    }

    public void Dispose()
    {
        Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
}
