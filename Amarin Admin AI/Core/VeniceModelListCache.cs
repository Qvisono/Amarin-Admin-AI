namespace Amarin.Core;

internal sealed class VeniceModelListCache
{
    private readonly VeniceClient _venice;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<VeniceModelInfo>? _agentic;
    private string? _error;

    public VeniceModelListCache(VeniceClient venice) => _venice = venice;

    public IReadOnlyList<VeniceModelInfo>? Cached => _agentic;

    public string? Error => _error;

    public async Task<IReadOnlyList<VeniceModelInfo>> GetAgenticAsync(
        CancellationToken cancellationToken = default)
    {
        if (_agentic is not null)
        {
            return _agentic;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_agentic is not null)
            {
                return _agentic;
            }

            var list = await _venice.ListTextModelsAsync(cancellationToken).ConfigureAwait(false);
            _agentic = VeniceModelCatalog.FilterAgentic(list);
            _error = null;
            return _agentic;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _agentic = [];
            return _agentic;
        }
        finally
        {
            _gate.Release();
        }
    }
}
