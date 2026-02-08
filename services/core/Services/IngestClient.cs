using GameHub.Shared;

namespace GameHub.Core.Services;

public class IngestClient
{
    private readonly HttpClient _http;
    public IngestClient(HttpClient http) { _http = http; }

    public async Task<IEnumerable<RawGameDto>> FetchSteamAsync() =>
        await _http.GetFromJsonAsync<IEnumerable<RawGameDto>>("/api/export/steam") ?? [];

    public async Task<IEnumerable<RawGameDto>> FetchPsnAsync() =>
        await _http.GetFromJsonAsync<IEnumerable<RawGameDto>>("/api/export/psn") ?? [];

    public async Task<IEnumerable<RawGameDto>> FetchGogAsync() =>
        await _http.GetFromJsonAsync<IEnumerable<RawGameDto>>("/api/export/gog") ?? [];

    public async Task<IEnumerable<RawGameDto>> FetchEpicAsync() =>
        await _http.GetFromJsonAsync<IEnumerable<RawGameDto>>("/api/export/epic") ?? [];
}
