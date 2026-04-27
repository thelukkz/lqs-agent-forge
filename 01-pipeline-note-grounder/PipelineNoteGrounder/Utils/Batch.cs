namespace PipelineNoteGrounder.Utils;

static class Batch
{
    public static async Task<List<TResult>> RunAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        int concurrency,
        Func<TItem, Task<TResult>> work)
    {
        var results = new List<TResult>();
        var semaphore = new SemaphoreSlim(concurrency);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync();
            try   { return await work(item); }
            finally { semaphore.Release(); }
        });

        results.AddRange(await Task.WhenAll(tasks));
        return results;
    }
}
