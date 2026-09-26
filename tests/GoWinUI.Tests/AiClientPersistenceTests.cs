using GoAi.Contracts;
using GoWinUI.App.Pages;
using GoWinUI.App.Services;
using GoWinUI.App.ViewModels;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;
using System.Security.Cryptography;

namespace GoWinUI.Tests;

public sealed class AiClientPersistenceTests
{
    [Fact]
    public async Task MigrationSeedsEditableServiceTriggersAndMatchesOnlyPhraseBoundaries()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IPromptTriggerRepository>();

        var seeded = await repository.ListAsync();

        Assert.Contains(seeded, item => item.Action == PromptTriggerAction.ImageGeneration && item.Phrase == "Erstelle ein Bild");
        Assert.Contains(seeded, item => item.Action == PromptTriggerAction.LiveTranslation && item.Phrase == "Live übersetzen");
        Assert.Contains(seeded, item => item.Action == PromptTriggerAction.VoiceInput && item.Phrase == "Sprachsteuerung");
        Assert.Contains(seeded, item => item.Action == PromptTriggerAction.VideoAnalysis && item.Phrase == "Video analysieren");
        var match = await repository.MatchAsync("Führe Websuche durch: energieeffiziente RLT-Anlagen");
        Assert.NotNull(match);
        Assert.Equal(PromptTriggerAction.WebSearch, match.Trigger.Action);
        Assert.Equal("energieeffiziente RLT-Anlagen", match.RemainingPrompt);
        var editableWebTrigger = seeded.First(item => item.Action == PromptTriggerAction.WebSearch);
        await repository.UpdateAsync(editableWebTrigger with
        {
            Phrase = "Führe Web-Suche durch",
            Revision = editableWebTrigger.Revision,
        }, editableWebTrigger.Revision);
        var customMatch = await repository.MatchAsync("Führe Web‑Suche durch. Die Rakschegleichung.");
        Assert.NotNull(customMatch);
        Assert.Equal(PromptTriggerAction.WebSearch, customMatch.Trigger.Action);
        Assert.Equal("Die Rakschegleichung.", customMatch.RemainingPrompt);
        var imageMatch = await repository.MatchAsync("Bild analysieren. Was ist zu sehen?");
        Assert.NotNull(imageMatch);
        Assert.Equal(PromptTriggerAction.ImageAnalysis, imageMatch.Trigger.Action);
        Assert.Equal("Was ist zu sehen?", imageMatch.RemainingPrompt);
        Assert.Null(await repository.MatchAsync("Vorlesender Text ist kein Sprachbefehl"));
    }

    [Fact]
    public void SystemAudioWindowsAreEncodedAsValidClampedPcm16Wave()
    {
        var wave = SystemAudioCaptionService.CreatePcm16Wave([-2f, -1f, 0f, 0.5f, 1f, 2f]);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wave, 36, 4));
        Assert.Equal(wave.Length - 8, BitConverter.ToInt32(wave, 4));
        Assert.Equal(16_000, BitConverter.ToInt32(wave, 24));
        Assert.Equal(12, BitConverter.ToInt32(wave, 40));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(wave, 44));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(wave, 46));
        Assert.Equal((short)0, BitConverter.ToInt16(wave, 48));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(wave, 52));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(wave, 54));
    }

    [Fact]
    public async Task FinalCaptureBacklogIsSplitIntoBoundedOverlappingWindows()
    {
        // A slow translation can leave a full 20-second WASAPI buffer at stop.
        var samples = Enumerable.Range(0, 16_000 * 20).Select(index => (float)index).ToList();
        var windows = new List<float[]>();
        Assert.True(await SystemAudioCaptionService.SendCompleteWindowsAsync(samples, (window, _) =>
        {
            windows.Add(window.ToArray());
            Assert.Equal(44 + 64_000 * 2, SystemAudioCaptionService.CreatePcm16Wave(window).Length);
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.Equal(5, windows.Count);
        Assert.Equal(40_000, samples.Count);
        for (var index = 1; index < windows.Count; index++)
            Assert.Equal(windows[index - 1][^8_000..], windows[index][..8_000]);
        Assert.Equal(280_000f, samples[0]);
        Assert.Equal(319_999f, samples[^1]);
    }

    [Fact]
    public async Task CaptionDrainExtendsDeadlineWhenWindowsComplete()
    {
        var processor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        await SystemAudioCaptionService.WaitForCaptionDrainAsync(processor.Task, () =>
        {
            if (++observations == 1) return 0;
            processor.TrySetResult();
            return 1;
        }, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.True(processor.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CaptionDrainStillTimesOutWithoutProgress()
    {
        var processor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => SystemAudioCaptionService.WaitForCaptionDrainAsync(
            processor.Task, () => 0, TimeSpan.FromMilliseconds(20), CancellationToken.None));
    }

    [Fact]
    public void OnlyGeneratedScreenCapturesAreBoundToTheSentMessage()
    {
        var screenshot = new AssistantAttachment(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "GO-Screenshot-2026-08-14-121314.png", "image/png", "abc", 42, DateTimeOffset.UtcNow);
        var manuallyAddedImage = screenshot with
        {
            Id = Guid.NewGuid(),
            FileName = "Anlagenfoto.png",
        };
        var screenClip = screenshot with
        {
            Id = Guid.NewGuid(),
            FileName = "GO-Bildschirmclip-2026-08-14-121315.mp4",
            ContentType = "video/mp4",
        };
        var audioCapture = screenshot with
        {
            Id = Guid.NewGuid(),
            FileName = "GO-Systemaudio-2026-08-14-121316.wav",
            ContentType = "audio/wav",
        };

        Assert.True(GoAiAssistantService.IsCapturedMedia(screenshot));
        Assert.True(GoAiAssistantService.IsCapturedMedia(screenClip));
        Assert.True(GoAiAssistantService.IsCapturedMedia(audioCapture));
        Assert.False(GoAiAssistantService.IsCapturedMedia(manuallyAddedImage));
    }

    [Fact]
    public void ScreenClipWriterProducesIndexedUncompressedAvi()
    {
        var path = Path.Combine(Path.GetTempPath(), $"go-screen-clip-{Guid.NewGuid():N}.avi");
        try
        {
            using (var writer = new ScreenClipCaptureService.UncompressedAviWriter(path, 4, 2, 2))
            {
                writer.WriteFrame(new byte[4 * 2 * 4]);
                writer.WriteFrame(Enumerable.Repeat((byte)127, 4 * 2 * 4).ToArray());
                Assert.Equal(2, writer.FrameCount);
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("AVI ", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4));
            Assert.Contains("movi", System.Text.Encoding.ASCII.GetString(bytes));
            Assert.Contains("idx1", System.Text.Encoding.ASCII.GetString(bytes));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ScreenClipTranscoderProducesBrowserCompatibleMp4()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"go-screen-clip-{Guid.NewGuid():N}.avi");
        var destinationPath = Path.ChangeExtension(sourcePath, ".mp4");
        try
        {
            using (var writer = new ScreenClipCaptureService.UncompressedAviWriter(sourcePath, 320, 240, 2))
            {
                for (var frame = 0; frame < 4; frame++)
                {
                    var pixels = Enumerable.Repeat((byte)(frame * 48), 320 * 240 * 4).ToArray();
                    writer.WriteFrame(pixels);
                }
            }

            var result = await ScreenClipCaptureService.TranscodeToMp4Async(
                sourcePath,
                320,
                240,
                CancellationToken.None);

            Assert.Equal(destinationPath, result);
            var bytes = await File.ReadAllBytesAsync(result, CancellationToken.None);
            Assert.True(bytes.Length > 256);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));

            await using var environment = await TestEnvironment.CreateAsync();
            var chats = environment.Get<IChatRepository>();
            var session = await chats.CreateSessionAsync("Video-Vorschau");
            var message = await chats.AddMessageAsync(session.Id, ChatRole.User, "Video", MessageStatus.Completed);
            await using var media = new MemoryStream(bytes, writable: false);
            var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(
                message.Id,
                "test-video",
                "GO-Bildschirmclip-Test.mp4",
                "video/mp4",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length,
                "screen-capture",
                null,
                null,
                media);
            var cacheRoot = Path.Combine(environment.Directory, "preview-cache");
            using var previews = new AssistantArtifactPreviewService(
                environment.Get<IChatArtifactRepository>(),
                environment.Get<IBinaryObjectStore>(),
                cacheRoot);

            var preview = await previews.PrepareAsync(artifact.Id, CancellationToken.None);

            Assert.Equal($"https://{AssistantArtifactPreviewService.VirtualHost}/{artifact.Id:N}/media.mp4", preview.Url);
            var cached = Path.Combine(cacheRoot, artifact.Id.ToString("N"), "media.mp4");
            Assert.True(File.Exists(cached));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(cached));
        }
        finally
        {
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
        }
    }

    [Theory]
    [InlineData("aufnahme.wav", "application/octet-stream", "audio/wav")]
    [InlineData("anlage.MKV", "", "video/x-matroska")]
    [InlineData("foto.webp", null, "image/webp")]
    [InlineData("daten.bin", null, "application/octet-stream")]
    public void AttachmentMediaTypeFallsBackToKnownFileExtensions(
        string fileName,
        string? reported,
        string expected)
    {
        Assert.Equal(expected, AssistantPage.ResolveAttachmentContentType(fileName, reported));
    }

    [Fact]
    public void NaturalMediaQuestionsRouteTheLatestAttachedMediumWithoutAnUploadIdPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        var image = new AssistantAttachment(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "aufnahme.png", "image/png", new string('a', 64), 10, now);
        var video = new AssistantAttachment(
            Guid.NewGuid(), image.SessionId, Guid.NewGuid(), "clip.mp4", "video/mp4", new string('b', 64), 20, now.AddSeconds(1));

        Assert.Equal(
            PromptTriggerAction.VideoAnalysis,
            GoAiAssistantService.InferMediaAnalysisAction("Was ist zu sehen?", [image, video]));
        Assert.Null(GoAiAssistantService.InferMediaAnalysisAction("Erstelle eine Zusammenfassung.", [image, video]));
    }

    [Fact]
    public void TriggerEditorOnlyMarksActualDatabaseChangesAsDirty()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new PromptTrigger(
            Guid.NewGuid(), PromptTriggerAction.WebSearch, "Suche", "Beschreibung",
            PromptTriggerMatchMode.Prefix, true, 100, 7, now, now);
        var editor = new PromptTriggerEditorItem(source);

        Assert.False(editor.IsDirty);
        editor.Phrase = "Suche im Web";
        Assert.True(editor.IsDirty);
        editor.ApplySaved(source with { Phrase = editor.Phrase, Revision = 8 });
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void TriggerEditorTracksAnEditedServiceCategory()
    {
        var now = DateTimeOffset.UtcNow;
        var source = new PromptTrigger(
            Guid.NewGuid(), PromptTriggerAction.WebSearch, "Suche", "Beschreibung",
            PromptTriggerMatchMode.Prefix, true, 100, 4, now, now);
        var editor = new PromptTriggerEditorItem(source);
        var imageGeneration = Assert.Single(
            editor.ActionOptions,
            option => option.Value == PromptTriggerAction.ImageGeneration);

        editor.SelectedActionOption = imageGeneration;

        Assert.True(editor.IsDirty);
        Assert.Equal("Bild generieren", editor.ActionDisplayName);
        Assert.Equal(PromptTriggerAction.ImageGeneration, editor.ToModel().Action);
        editor.ApplySaved(editor.ToModel() with { Revision = 5 });
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task AttachmentsArtifactsAndResumableRunsRemainLinkedToLocalChatState()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Serverlauf");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        var attachments = environment.Get<IAssistantAttachmentRepository>();
        var attachment = await attachments.ImportAsync(
            session.Id,
            "anlage.png",
            "image/png",
            new MemoryStream([1, 2, 3, 4]));
        var artifacts = environment.Get<IChatArtifactRepository>();
        byte[] artifactBytes = [5, 6, 7, 8];
        var artifact = await artifacts.ImportAsync(
            message.Id,
            "artifact-server-1",
            "entwurf.png",
            "image/png",
            Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
            4,
            "image-worker",
            null,
            new Dictionary<string, string> { ["seed"] = "42" },
            new MemoryStream(artifactBytes));
        var runRepository = environment.Get<IGoAiRunRepository>();
        var now = DateTimeOffset.UtcNow;
        var run = await runRepository.CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(), session.Id, message.Id, PromptTriggerAction.ImageGeneration,
            "idem-1", null, 0, "creating", null, null, now, now));
        await runRepository.UpdateAsync(run.Id, "server-run-1", 7, "running", "z-image-turbo");

        Assert.Equal(attachment.Id, Assert.Single(await attachments.ListAsync(session.Id)).Id);
        Assert.Equal(artifact.Id, Assert.Single(await artifacts.ListForMessageAsync(message.Id)).Id);
        var resumable = Assert.Single(await runRepository.ListResumableAsync());
        Assert.Equal("server-run-1", resumable.ServerRunId);
        Assert.Equal(7, resumable.LastEventId);
        Assert.Equal("z-image-turbo", resumable.SelectedModel);
    }

    [Fact]
    public async Task ClientStartupStopsGeneralRunsAndPreservesCodingRunAndEventCursorForResume()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runRepository = environment.Get<IGoAiRunRepository>();
        var normalSession = await chats.CreateSessionAsync("Unterbrochener AI-Lauf");
        var normalMessage = await chats.AddMessageAsync(
            normalSession.Id,
            ChatRole.Assistant,
            string.Empty,
            MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var normalRun = await runRepository.CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(), normalSession.Id, normalMessage.Id, null,
            "startup-normal", "server-normal", 12, "running", "gpt-oss-120b", null, now, now));
        var codingSession = await chats.CreateSessionAsync("Coding Dauerlauf");
        var codingMessage = await chats.AddMessageAsync(codingSession.Id, ChatRole.Assistant, "Erster Schritt erledigt.", MessageStatus.Streaming);
        var codingRun = await runRepository.CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(), codingSession.Id, codingMessage.Id, PromptTriggerAction.Coding,
            "startup-coding", "server-coding", 18_765, "waitingForClient", "coding/model", null, now.AddDays(-3), now));

        var serverRunIds = await GoAiAssistantService.StopPersistedRunsLocallyAsync(
            runRepository,
            chats);

        Assert.Equal(["server-normal"], serverRunIds);
        var resumable = Assert.Single(await runRepository.ListResumableAsync());
        Assert.Equal(codingRun.Id, resumable.Id);
        Assert.Equal(18_765, resumable.LastEventId);
        Assert.Equal("waitingForClient", resumable.State);
        Assert.Equal("Erster Schritt erledigt.", (await chats.GetMessageAsync(codingMessage.Id))!.Content);
        Assert.Equal(MessageStatus.Streaming, (await chats.GetMessageAsync(codingMessage.Id))!.Status);
        var stoppedNormalRun = Assert.IsType<GoAiRunRecord>(await runRepository.GetAsync(normalRun.Id));
        Assert.Equal("cancelled", stoppedNormalRun.State);
        Assert.Equal("client.run_stopped_on_start", stoppedNormalRun.ErrorCode);

        var stoppedNormalMessage = Assert.IsType<ChatMessage>(
            await chats.GetMessageAsync(normalMessage.Id));
        Assert.Equal(MessageStatus.Cancelled, stoppedNormalMessage.Status);
        Assert.Equal("Der vorherige AI-Lauf wurde beim Clientstart gestoppt.", stoppedNormalMessage.Content);
    }

    [Fact]
    public async Task RemovingAnAttachmentTwiceIsIdempotent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Video-Anhang");
        var attachments = environment.Get<IAssistantAttachmentRepository>();
        var attachment = await attachments.ImportAsync(
            session.Id,
            "GO-Bildschirmclip-2026-08-15-010203.mp4",
            "video/mp4",
            new MemoryStream([1, 2, 3, 4]));

        await attachments.RemoveAsync(attachment.Id);
        await attachments.RemoveAsync(attachment.Id);

        Assert.Null(await attachments.GetAsync(attachment.Id));
        Assert.Empty(await attachments.ListAsync(session.Id));
    }

    [Fact]
    public async Task AttachmentAndArtifactMetadataCannotInjectResourceHeaders()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Metadaten");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Ergebnis", MessageStatus.Completed);
        var attachments = environment.Get<IAssistantAttachmentRepository>();
        await Assert.ThrowsAsync<ArgumentException>(() => attachments.ImportAsync(
            session.Id,
            "bild.png\r\nX-Test: injected",
            "image/png",
            new MemoryStream([1])));

        byte[] bytes = [9, 8, 7];
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(
            message.Id,
            "artifact-safe",
            "bild.png",
            "image/png\r\nX-Test: injected",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length,
            "image-worker",
            null,
            null,
            new MemoryStream(bytes));

        Assert.Equal("application/octet-stream", artifact.ContentType);
    }

    [Fact]
    public async Task ArtifactImportAndReadPreservesToolStepAnchor()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Bildposition");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Antwort", MessageStatus.Completed);
        byte[] bytes = [1, 2, 3];
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var source = new MemoryStream(bytes, writable: false);
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(
            message.Id,
            "media-anchor",
            "render.png",
            "image/png",
            sha256,
            bytes.Length,
            "go-ai",
            "server-step-123",
            null,
            source);

        Assert.Equal("server-step-123", artifact.StepId);
        var loaded = await environment.Get<IChatArtifactRepository>().GetAsync(artifact.Id);
        Assert.NotNull(loaded);
        Assert.Equal("server-step-123", loaded.StepId);
    }
}
