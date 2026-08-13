using GoAi.Server.Core.Configuration;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace GoAi.Server.Core.Security;

public sealed class WorkerKeyStore : IDisposable
{
    private static readonly string[] WorkerNames = ["speech", "media", "image"];
    private readonly GoAiServerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkerKeyStore(IOptions<GoAiServerOptions> options)
    {
        _options = options.Value;
    }

    public async Task EnsureKeysAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.SecretDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var workerName in WorkerNames)
            {
                var path = _options.GetWorkerKeyPath(workerName);
                if (File.Exists(path))
                {
                    continue;
                }

                var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                var temporary = path + $".{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(temporary, value, cancellationToken).ConfigureAwait(false);
                try
                {
                    File.Move(temporary, path, overwrite: false);
                }
                catch (IOException) when (File.Exists(path))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadAsync(string workerName, CancellationToken cancellationToken = default)
    {
        await EnsureKeysAsync(cancellationToken).ConfigureAwait(false);
        var value = (await File.ReadAllTextAsync(_options.GetWorkerKeyPath(workerName), cancellationToken).ConfigureAwait(false)).Trim();
        if (value.Length < 32)
        {
            throw new InvalidOperationException($"Internal key for worker '{workerName}' is invalid.");
        }

        return value;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
