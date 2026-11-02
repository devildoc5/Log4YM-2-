using Log4YM.Contracts.Models;
using Log4YM.Server.Core.Database;

namespace Log4YM.Server.Services;

public interface ISettingsService
{
    Task<UserSettings> GetSettingsAsync(string id = "default");
    Task<UserSettings> SaveSettingsAsync(UserSettings settings);
}

public class SettingsService : ISettingsService
{
    private readonly ISettingsRepository _repository;

    public SettingsService(ISettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<UserSettings> GetSettingsAsync(string id = "default")
    {
        var settings = await _repository.GetAsync(id);
        if (settings == null)
            return new UserSettings { Id = id };

        // Ensure nested settings objects are never null (handles documents
        // created before new settings sections were added to the schema)
        settings.Station ??= new();
        settings.Qrz ??= new();
        settings.Appearance ??= new();
        settings.Rotator ??= new();
        settings.Radio ??= new();
        settings.Map ??= new();
        settings.Cluster ??= new();
        settings.SpotStatus ??= new();
        settings.Ai ??= new();
        settings.Lotw ??= new();
        settings.Logbooks ??= new();
        settings.Wsjtx ??= new();

        return settings;
    }

    public async Task<UserSettings> SaveSettingsAsync(UserSettings settings)
    {
        // The Settings panel posts the whole object, but logbooks and the WSJT-X listener are
        // saved through their own endpoints and an older frontend won't send them at all.
        // Keep the stored copies so a general "Save" never wipes them.
        var existing = await _repository.GetAsync(settings.Id ?? "default");
        if (existing != null)
        {
            settings.Logbooks = existing.Logbooks ?? new();
            settings.Wsjtx = existing.Wsjtx ?? new();

            settings.Lotw ??= new();
            var storedLotw = existing.Lotw ?? new();
            settings.Lotw.LastQslDownload = storedLotw.LastQslDownload;
            // Blank password means "unchanged", so clearing the field by accident doesn't log you out
            if (string.IsNullOrEmpty(settings.Lotw.Password))
                settings.Lotw.Password = storedLotw.Password;
            if (settings.Lotw.Username == null)
                settings.Lotw.Username = storedLotw.Username;
        }

        return await _repository.UpsertAsync(settings);
    }
}
