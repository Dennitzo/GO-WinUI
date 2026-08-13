using GoAi.Contracts;
using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Speech.Synthesis;

namespace GoAi.Server.Core.Workers;

public sealed class WindowsSpeechService
{
    private readonly GoAiServerOptions _options;

    public WindowsSpeechService(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public Task<WorkerSpeechResult> SynthesizeAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Synthesize(request, cancellationToken), cancellationToken);

    private WorkerSpeechResult Synthesize(SpeechRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_options.ArtifactDirectory, "worker");
        Directory.CreateDirectory(directory);
        var fileName = $"speech-windows-{Guid.NewGuid():N}.wav";
        var path = Path.Combine(directory, fileName);
        using var synthesizer = new SpeechSynthesizer();
        var installed = synthesizer.GetInstalledVoices(CultureInfo.GetCultureInfo("de-DE"))
            .Where(static voice => voice.Enabled)
            .Select(static voice => voice.VoiceInfo)
            .ToArray();
        var selected = installed.FirstOrDefault(static voice =>
                voice.Culture.Name.Equals("de-DE", StringComparison.OrdinalIgnoreCase)
                && voice.Name.Contains("Hedda", StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(static voice => voice.Culture.Name.Equals("de-DE", StringComparison.OrdinalIgnoreCase))
            ?? synthesizer.GetInstalledVoices(CultureInfo.GetCultureInfo("en-US"))
                .Where(static voice => voice.Enabled)
                .Select(static voice => voice.VoiceInfo)
                .FirstOrDefault()
            ?? throw new InvalidOperationException("No Windows speech voice is installed.");
        synthesizer.SelectVoice(selected.Name);
        synthesizer.Rate = Math.Clamp((int)Math.Round(Math.Log(request.Speed, 2) * 5), -10, 10);
        synthesizer.SetOutputToWaveFile(path);
        cancellationToken.ThrowIfCancellationRequested();
        synthesizer.Speak(request.Text);
        synthesizer.SetOutputToNull();
        return new WorkerSpeechResult(
            Path.GetRelativePath(_options.DataDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
            fileName,
            "audio/wav",
            "windows-speech",
            true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["voice"] = selected.Name,
                ["culture"] = selected.Culture.Name,
                ["speed"] = request.Speed.ToString(CultureInfo.InvariantCulture),
            });
    }
}
