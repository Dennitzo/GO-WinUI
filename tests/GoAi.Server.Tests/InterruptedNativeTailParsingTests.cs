using GoAi.Server.Core.Runs;

namespace GoAi.Server.Tests;

public sealed class InterruptedNativeTailParsingTests
{
    [Theory]
    [InlineData(" \tGedanke\r\n</think>\n Antwort 🧪  ", true, "\n Antwort 🧪  ", " \tGedanke\r\n")]
    [InlineData("\n Unvollständiger Gedanke  ", true, "", "\n Unvollständiger Gedanke  ")]
    [InlineData("\tDirekte Antwort\n  ", false, "\tDirekte Antwort\n  ", "")]
    [InlineData("Gedanke</think>\nAntwort  <｜end▁of▁sentence｜>", true, "\nAntwort  ", "Gedanke")]
    [InlineData("\nAntwort  <｜end▁of▁sentence｜>", false, "\nAntwort  ", "")]
    [InlineData("", true, "", "")]
    public void NativeTailPreservesExactReasoningAndAnswerBoundaries(
        string raw, bool reasoningEnabled, string expectedContent, string expectedReasoning)
    {
        Assert.True(RunProcessor.TryParseInterruptedDeepSeekTail(raw, reasoningEnabled, out var content, out var reasoning));
        Assert.Equal(expectedContent, content);
        Assert.Equal(expectedReasoning, reasoning);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Antwort\uFFFD", true)]
    [InlineData("Antwort\uFFFD", false)]
    [InlineData("Gedanke</think><｜tool▁calls▁begin｜>", true)]
    [InlineData("<|tool_call|>{\"path\":\"datei.txt\"}", false)]
    [InlineData("Vorher<｜end▁of▁sentence｜>Nachher", false)]
    [InlineData("Gedanke</think>Antwort", false)]
    [InlineData("<think>Unerwarteter Gedanke", false)]
    public void UncertainDecodingOrToolProtocolNeverBecomesAnInventedChatMessage(string? raw, bool reasoningEnabled)
    {
        Assert.False(RunProcessor.TryParseInterruptedDeepSeekTail(raw, reasoningEnabled, out var content, out var reasoning));
        Assert.Empty(content);
        Assert.Empty(reasoning);
    }
}
