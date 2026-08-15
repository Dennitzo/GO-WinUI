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
    public async Task CodingModeAndBoundWorkspacePersistWithTheChatSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IChatRepository>();
        var workspace = Path.Combine(environment.Directory, "arbitrary-language-workspace");
        Directory.CreateDirectory(workspace);
        var fingerprint = WorkspaceRepositoryIndex.CreateWorkspaceFingerprint(workspace);
        var session = await repository.CreateSessionAsync("Coding");

        await repository.SetAssistantContextAsync(
            session.Id,
            AssistantMode.Code,
            workspace,
            fingerprint);

        var restored = await repository.GetSessionAsync(session.Id);
        Assert.NotNull(restored);
        Assert.Equal(AssistantMode.Code, restored.AssistantMode);
        Assert.Equal(Path.GetFullPath(workspace), restored.WorkspacePath);
        Assert.Equal(fingerprint, restored.WorkspaceFingerprint);
    }

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
    public async Task TriggerEditsUseOptimisticRevisionsAndImmediatelyChangeRouting()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var repository = environment.Get<IPromptTriggerRepository>();
        var original = (await repository.ListAsync()).First(item => item.Action == PromptTriggerAction.BricsCad);

        var updated = await repository.UpdateAsync(
            original with { Phrase = "CAD ausführen", Description = "Eigener CAD-Präfix." },
            original.Revision);

        Assert.Equal(original.Revision + 1, updated.Revision);
        Assert.Null(await repository.MatchAsync("In BricsCAD messe den Abstand"));
        Assert.Equal(PromptTriggerAction.BricsCad, (await repository.MatchAsync("CAD ausführen: messe den Abstand"))?.Trigger.Action);
        await Assert.ThrowsAsync<RevisionConflictException>(() =>
            repository.UpdateAsync(updated with { Phrase = "Veraltet" }, original.Revision));
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
    public async Task ClientToolJournalPreventsDuplicateMutationAfterSseReconnect()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Toollauf");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, string.Empty, MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var run = await environment.Get<IGoAiRunRepository>().CreateAsync(new GoAiRunRecord(
            Guid.NewGuid(), session.Id, message.Id, PromptTriggerAction.Code,
            "idem-tool", "run-server-tool", 4, "waitingForClient", "laguna", null, now, now));
        var journal = environment.Get<IClientToolExecutionRepository>();
        var execution = new ClientToolExecutionRecord(
            "proposal-tool-1", run.Id, "run-server-tool", 5, "fs.proposePatch",
            "executing", null, now, now);

        var started = await journal.BeginAsync(execution);
        Assert.Equal("executing", started.State);
        Assert.Null(started.ResultJson);
        var completed = await journal.CompleteAsync(
            execution.ProposalId,
            """{"proposalId":"proposal-tool-1","status":"completed","result":{"patched":true}}""");
        Assert.Equal("completed", completed.State);
        Assert.NotNull(completed.ResultJson);
        await journal.MarkSubmittedAsync(execution.ProposalId);
        Assert.Equal("submitted", (await journal.GetAsync(execution.ProposalId))?.State);

        await Assert.ThrowsAsync<InvalidDataException>(() => journal.BeginAsync(
            execution with { LocalRunId = Guid.NewGuid() }));
        await chats.DeleteSessionAsync(session.Id);
        Assert.Null(await journal.GetAsync(execution.ProposalId));
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
            new MemoryStream(bytes));

        Assert.Equal("application/octet-stream", artifact.ContentType);
    }
}
