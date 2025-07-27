namespace Gateway;

public class Controller
{
    private readonly Repository _repository;

    public Controller(Repository repository)
    {
        _repository = repository;
    }

    public async Task PurgePaymentsAsync()
    {
        await _repository.PurgePaymentsAsync();
    }

    public async Task<SummaryResponse> GetSummaryAsync(DateTime? from, DateTime? to)
    {
        return await _repository.GetSummaryAsync(from, to);
    }
}
