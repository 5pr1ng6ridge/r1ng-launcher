using System.Windows.Input;
using System.Windows.Media;
using R1ng.Launcher.Core;

namespace R1ng.Launcher.ViewModels;

/// <summary>One row in the result list.</summary>
public sealed class ResultItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _isPinned;

    public ResultItemViewModel(SearchResultItem model, LauncherViewModel owner, SearchContext? context = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Context = context;
        _isPinned = owner.IsPinned(model);
        TogglePinCommand = new RelayCommand(() => owner.TogglePin(this), () => !string.IsNullOrWhiteSpace(Model.Target));
        OpenCommand = new RelayCommand(() => owner.Open(this));
        OpenContainingFolderCommand = new RelayCommand(() => owner.OpenContainingFolder(this));
        CopyPathCommand = new RelayCommand(() => owner.CopyPath(this));
        PreviewCommand = new RelayCommand(() => owner.Preview(this), () => !string.IsNullOrWhiteSpace(Model.Target));
        HideCommand = new RelayCommand(() => owner.Hide(this));
    }

    public SearchResultItem Model { get; }

    public SearchContext? Context { get; }

    public bool IsPinned
    {
        get => _isPinned;
        internal set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinHeader));
                OnPropertyChanged(nameof(Source));
            }
        }
    }

    public string PinHeader => IsPinned ? "取消置顶" : "置顶到历史";

    public ICommand TogglePinCommand { get; }

    public string Title => Model.Title;

    public string Detail => Model.Detail;

    public string TypeLabel => Model.TypeLabel;

    public string Source => Model.Source is AppSources.History or AppSources.Pinned
        ? (IsPinned ? AppSources.Pinned : AppSources.History) : Model.Source;

    public string Tooltip => string.IsNullOrEmpty(Model.Detail) ? Model.CopyText : $"{Model.CopyText}\n{Model.Detail}";

    /// <summary>Fallback glyph shown while (or instead of) a real shell icon.</summary>
    public string Glyph => Model.Kind switch
    {
        ResultKind.Application => "\uE71D",
        ResultKind.Folder => "\uE8B7",
        ResultKind.File => "\uE8A5",
        _ => "\uE81C",
    };

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
                OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public ICommand OpenCommand { get; }

    public ICommand OpenContainingFolderCommand { get; }

    public ICommand CopyPathCommand { get; }

    /// <summary>Sends this row (never the selection) to QuickLook; never opens it or records history.</summary>
    public ICommand PreviewCommand { get; }

    /// <summary>Hides this row's item from every result list until it is restored.</summary>
    public ICommand HideCommand { get; }
}
