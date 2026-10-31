using System.Net;
using System.Text;
using System.Text.Json;

namespace RobloxUsernameChecker;

public partial class MainPage : ContentPage
{
    private static readonly HttpClient Http = new();
    private CancellationTokenSource? _checkCts;
    private readonly List<(string Name, string Status)> _results = new();

    public MainPage()
    {
        InitializeComponent();
        LoadSavedResults();
        UpdateCounters();
    }

    private async void ImportClicked(object sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Выбери TXT-файл со списком ников"
            });
            if (file is null) return;
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            NamesEditor.Text = await reader.ReadToEndAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка импорта", ex.Message, "OK");
        }
    }

    private void ClearClicked(object sender, EventArgs e)
    {
        if (_checkCts is not null) return;
        NamesEditor.Text = string.Empty;
        _results.Clear();
        ResultsLabel.Text = "Здесь появятся результаты проверки.";
        CheckProgress.Progress = 0;
        ProgressLabel.Text = "Ожидание";
        UpdateCounters();
        SaveResults();
    }

    private async void StartClicked(object sender, EventArgs e)
    {
        if (_checkCts is not null) return;

        var names = (NamesEditor.Text ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            await DisplayAlert("Нет имён", "Добавь хотя бы один ник или импортируй TXT-файл.", "OK");
            return;
        }

        _results.Clear();
        ResultsLabel.Text = string.Empty;
        CheckProgress.Progress = 0;
        _checkCts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        NamesEditor.IsEnabled = false;

        try
        {
            for (var i = 0; i < names.Count; i++)
            {
                _checkCts.Token.ThrowIfCancellationRequested();
                var name = names[i];
                var (status, message) = await ValidateUsernameAsync(name, _checkCts.Token);
                _results.Add((name, status));
                AppendResult(name, message);
                UpdateCounters();
                CheckProgress.Progress = (double)(i + 1) / names.Count;
                ProgressLabel.Text = $"Проверено {i + 1} из {names.Count}";

                if (i < names.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(2), _checkCts.Token);
            }
            ProgressLabel.Text = $"Готово: {_results.Count} из {names.Count}";
        }
        catch (OperationCanceledException)
        {
            ProgressLabel.Text = $"Остановлено. Проверено: {_results.Count}";
        }
        finally
        {
            _checkCts.Dispose();
            _checkCts = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            NamesEditor.IsEnabled = true;
            SaveResults();
        }
    }

    private void StopClicked(object sender, EventArgs e) => _checkCts?.Cancel();

    private static async Task<(string Status, string Message)> ValidateUsernameAsync(string username, CancellationToken token)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                username,
                birthday = "2000-01-01",
                context = "Signup"
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(
                "https://auth.roblox.com/v1/usernames/validate", content, token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return ("Ошибка", "лимит запросов Roblox (429); попробуй позже");

            if (!response.IsSuccessStatusCode)
                return ("Ошибка", $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var code = doc.RootElement.TryGetProperty("code", out var codeElement)
                ? codeElement.GetInt32()
                : -1;

            return code switch
            {
                0 => ("Свободен", "свободен"),
                1 => ("Занят", "уже занят"),
                2 => ("Недопустим", "не подходит по правилам Roblox"),
                3 => ("Недопустим", "слишком короткий"),
                4 => ("Недопустим", "слишком длинный"),
                5 => ("Недопустим", "содержит недопустимые символы"),
                _ => ("Ошибка", $"неизвестный ответ API (код {code})")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return ("Ошибка", "не удалось связаться с Roblox");
        }
    }

    private void AppendResult(string name, string message)
    {
        var line = $"{name} — {message}";
        ResultsLabel.Text = string.IsNullOrEmpty(ResultsLabel.Text) ? line : ResultsLabel.Text + Environment.NewLine + line;
    }

    private void UpdateCounters()
    {
        AvailableCount.Text = _results.Count(x => x.Status == "Свободен").ToString();
        TakenCount.Text = _results.Count(x => x.Status == "Занят").ToString();
        ErrorCount.Text = _results.Count(x => x.Status is "Ошибка" or "Недопустим").ToString();
    }

    private void SaveResults()
    {
        var text = string.Join(Environment.NewLine, _results.Select(x => $"{x.Name}\t{x.Status}"));
        Preferences.Default.Set("saved_results", text);
    }

    private void LoadSavedResults()
    {
        var saved = Preferences.Default.Get("saved_results", string.Empty);
        if (string.IsNullOrWhiteSpace(saved)) return;
        foreach (var line in saved.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2) _results.Add((parts[0], parts[1]));
        }
        ResultsLabel.Text = string.Join(Environment.NewLine, _results.Select(x => $"{x.Name} — {x.Status}"));
    }

    private async void ExportClicked(object sender, EventArgs e)
    {
        var available = _results.Where(x => x.Status == "Свободен").Select(x => x.Name).ToList();
        if (available.Count == 0)
        {
            await DisplayAlert("Нет свободных ников", "Пока нет результатов со статусом «Свободен».", "OK");
            return;
        }

        var path = Path.Combine(FileSystem.CacheDirectory, "available_usernames.txt");
        await File.WriteAllLinesAsync(path, available);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "Свободные Roblox-ники",
            File = new ShareFile(path)
        });
    }
}
