namespace PipelineNoteGrounder.Utils;

static class Batch
{
    public static async Task<List<TResult>> RunAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        int concurrency,
        Func<TItem, Task<TResult>> work,
        int delayMs = 0)
    {
        var results = new List<TResult>();
        var semaphore = new SemaphoreSlim(concurrency);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await work(item);
                if (delayMs > 0)
                    await Task.Delay(delayMs);
                return result;
            }
            finally { semaphore.Release(); }
        });

        results.AddRange(await Task.WhenAll(tasks));
        return results;
    }
}
