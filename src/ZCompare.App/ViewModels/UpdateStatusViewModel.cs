using ZCompare.App.Infrastructure;
using ZCompare.App.Services;

namespace ZCompare.App.ViewModels;

internal sealed class UpdateStatusViewModel : ObservableObject
{
    private bool _isVisible;
    private string _message = string.Empty;
    private Uri? _downloadUri;

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public Uri? DownloadUri
    {
        get => _downloadUri;
        private set => SetProperty(ref _downloadUri, value);
    }

    public void Show(UpdateCheckResult result)
    {
        DownloadUri = result.DownloadUri;
        Message = $"发现 ZCompare v{result.Version.ToString(3)}";
        IsVisible = true;
    }

    public void Dismiss() => IsVisible = false;
}
