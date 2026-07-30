using System.Text;
using System.Collections.Concurrent;
using System.Reflection;
using Clawbrower.Services;

var failures = new List<string>();

Run("Logger_does_not_throw_when_preview_splits_emoji_surrogate_pair", () =>
{
    var json = new string('x', 499) + "😀";
    var preview = json[..500];

    if (!char.IsHighSurrogate(preview[^1]))
        throw new InvalidOperationException("测试数据未在 emoji 代理对中间截断");

    Logger.Info(preview);
});

await RunAsync("Pending_history_RPC_completes_when_client_is_disposed", async () =>
{
    using var client = new GatewayClient("ws://127.0.0.1");
    var pendingField = typeof(GatewayClient).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("找不到 GatewayClient._pending");
    var pendingRequests = pendingField.GetValue(client)
        as ConcurrentDictionary<string, TaskCompletionSource<string?>>
        ?? throw new InvalidOperationException("GatewayClient._pending 类型不符合预期");
    var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    pendingRequests["history-test"] = completion;

    client.Dispose();

    var completed = await Task.WhenAny(completion.Task, Task.Delay(500));
    if (completed != completion.Task)
        throw new TimeoutException("GatewayClient.Dispose 后 pending RPC 仍未结束");

    try
    {
        await completion.Task;
        throw new InvalidOperationException("GatewayClient.Dispose 后 pending RPC 应被取消");
    }
    catch (OperationCanceledException)
    {
    }
});

await RunAsync("History_RPC_started_after_dispose_fails_immediately", async () =>
{
    using var client = new GatewayClient("ws://127.0.0.1");
    client.Dispose();

    var pending = client.SendRpcAsync("chat.history");
    var completed = await Task.WhenAny(pending, Task.Delay(500));
    if (completed != pending)
        throw new TimeoutException("GatewayClient.Dispose 后新建的 RPC 仍永久等待");

    Exception? failure = null;
    try { await pending; }
    catch (Exception ex) { failure = ex; }

    if (failure is not InvalidOperationException)
        throw new InvalidOperationException($"GatewayClient.Dispose 后新建 RPC 应以 InvalidOperationException 失败，实际为 {failure?.GetType().Name ?? "成功"}");
});

Run("SanitizeSurrogates_preserves_intact_emoji", () =>
{
    // 📋 = U+1F4CB = high:0xD83D, low:0xDCCB
    string emoji = "\uD83D\uDCCB";
    string result = MarkdownParser.SanitizeSurrogates(emoji);
    if (result != emoji)
        throw new InvalidOperationException($"完整 emoji 应保留: expected length 2, got {result.Length}");
});

Run("SanitizeSurrogates_removes_orphaned_high_surrogate", () =>
{
    // 孤立的高代理字符
    string orphanHigh = "hello\uD83Dworld";
    string result = MarkdownParser.SanitizeSurrogates(orphanHigh);
    if (result != "helloworld")
        throw new InvalidOperationException($"孤立高代理应被移除: expected 'helloworld', got '{result}'");
});

Run("SanitizeSurrogates_removes_orphaned_low_surrogate", () =>
{
    // 孤立的低代理字符
    string orphanLow = "hello\uDCCBworld";
    string result = MarkdownParser.SanitizeSurrogates(orphanLow);
    if (result != "helloworld")
        throw new InvalidOperationException($"孤立低代理应被移除: expected 'helloworld', got '{result}'");
});

Run("SanitizeSurrogates_mixed_emoji_and_orphans", () =>
{
    // emoji + 孤立高代理 + 普通文本
    string mixed = "before\uD83D\uDCCBmid\uD83Dafter";
    string result = MarkdownParser.SanitizeSurrogates(mixed);
    string expected = "before\uD83D\uDCCBmidafter";
    if (result != expected)
        throw new InvalidOperationException($"混合场景: expected length {expected.Length}, got {result.Length}");
});

Run("SanitizeSurrogates_consecutive_orphaned_surrogates", () =>
{
    // 连续孤立的代理字符
    string consecutive = "\uD83D\uDCCB\uD83D";
    string result = MarkdownParser.SanitizeSurrogates(consecutive);
    string expected = "\uD83D\uDCCB"; // 只保留完整的 emoji
    if (result != expected)
        throw new InvalidOperationException($"连续孤立代理: expected length {expected.Length}, got {result.Length}");
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAILED: {failures.Count} regression test(s)");
    foreach (var failure in failures)
        Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("PASS: 8 regression tests");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
    }
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
    }
}
