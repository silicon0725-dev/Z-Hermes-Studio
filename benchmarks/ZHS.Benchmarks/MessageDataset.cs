using ZHS.Presentation.Chat;

namespace ZHS.Benchmarks;

/// <summary>
/// 确定性消息数据集（固定 seed）。混合比例固定（ADR-0003 §2）：
/// 纯文本 40% / Markdown 25% / 代码块 15% / 工具调用与长输出 12% / 附件引用 4% / 错误与中断 4%。
/// </summary>
public static class MessageDataset
{
    public static List<ChatMessageViewModel> Generate(int count, int seed = 20261017)
    {
        var random = new Random(seed);
        var messages = new List<ChatMessageViewModel>(count);
        for (var index = 0; index < count; index++)
        {
            var roll = random.Next(100);
            var role = index % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant;
            var text = roll switch
            {
                < 40 => PlainText(random, index),
                < 65 => Markdown(random, index),
                < 80 => Code(random, index),
                < 92 => ToolOutput(random, index),
                < 96 => Attachment(random, index),
                _ => ErrorText(random, index),
            };
            messages.Add(new ChatMessageViewModel(role, text));
        }

        return messages;
    }

    private static string PlainText(Random random, int index)
        => $"消息 {index}：" + new string('这', 20 + random.Next(120)) + "。";

    private static string Markdown(Random random, int index)
        => $"### 小节 {index}\n\n- 要点一 {new string('项', 8 + random.Next(30))}\n- 要点二 {new string('点', 8 + random.Next(30))}\n\n| 列A | 列B |\n|---|---|\n| {index} | 数据 |\n\n**加粗结论** 与 *斜体补充*。";

    private static string Code(Random random, int index)
        => $"```csharp\n// 代码块 {index}\npublic sealed class Sample{index}\n{{\n    public int Value {{ get; }} = {random.Next(1000)};\n\n    public string Compute(string input)\n    {{\n        var builder = new System.Text.StringBuilder();\n        for (var i = 0; i < {20 + random.Next(80)}; i++)\n        {{\n            builder.Append(input).Append(i);\n        }}\n        return builder.ToString();\n    }}\n}}\n```";

    private static string ToolOutput(Random random, int index)
        => $"· toolCall（call_{index:x6}）\nstdout: " + string.Join("\n", Enumerable.Range(0, 15 + random.Next(60)).Select(line => $"  [{line}] " + new string('x', 40 + random.Next(200))));

    private static string Attachment(Random random, int index)
        => $"📎 附件 artifact-{index}-{random.Next(9999):0000}.json（{random.Next(512)} KB，摘要：{new string('摘', 24)}…）";

    private static string ErrorText(Random random, int index)
        => $"（回合失败：HTTP {400 + random.Next(599)} — upstream error {new string('e', 30)}）";
}
