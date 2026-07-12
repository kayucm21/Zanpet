using System.IO;
using System.Runtime.InteropServices;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace ZapretUI.Services;

/// <summary>Uses Windows OneCore voices (ru-RU, en-US, etc.) — visible in Windows Settings.</summary>
internal static class WinRtSpeechBackend
{
    public static bool IsAvailable { get; } = CheckAvailable();

    public static IReadOnlyList<(string Code, string Label, bool Installed)> ListVoices()
    {
        if (!IsAvailable) return [];

        try
        {
            return SpeechSynthesizer.AllVoices
                .Select(v => (v.Language, v.DisplayName, true))
                .DistinctBy(v => v.Language)
                .OrderBy(v => v.Language, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static async Task<bool> SpeakAsync(string text, string cultureCode, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text))
            return false;

        SpeechSynthesizer? synth = null;
        MediaPlayer? player = null;
        try
        {
            synth = new SpeechSynthesizer();
            var voice = PickVoice(cultureCode);
            if (voice is not null)
                synth.Voice = voice;

            using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct).ConfigureAwait(false);
            player = new MediaPlayer();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            player.MediaEnded += (_, _) => done.TrySetResult();
            player.MediaFailed += (_, _) => done.TrySetResult();
            player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            player.Play();

            await done.Task.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { player?.Pause(); } catch { }
            try { player?.Dispose(); } catch { }
            try { synth?.Dispose(); } catch { }
        }
    }

    private static VoiceInformation? PickVoice(string cultureCode)
    {
        string code = string.IsNullOrWhiteSpace(cultureCode) ? "ru-RU" : cultureCode.Trim();
        string two = code.Length >= 2 ? code[..2] : code;

        return SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                   v.Language.Equals(code, StringComparison.OrdinalIgnoreCase))
               ?? SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                   v.Language.StartsWith(two, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CheckAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            _ = SpeechSynthesizer.AllVoices.Count;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class WinRtAsync
{
    public static Task<T> AsTask<T>(this Windows.Foundation.IAsyncOperation<T> op, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Completed = (asyncOp, status) =>
        {
            switch (status)
            {
                case Windows.Foundation.AsyncStatus.Completed:
                    tcs.TrySetResult(asyncOp.GetResults());
                    break;
                case Windows.Foundation.AsyncStatus.Canceled:
                    tcs.TrySetCanceled(ct.IsCancellationRequested ? ct : new CancellationToken(true));
                    break;
                case Windows.Foundation.AsyncStatus.Error:
                    tcs.TrySetException(asyncOp.ErrorCode);
                    break;
            }
        };
        if (ct.CanBeCanceled)
            ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }
}
