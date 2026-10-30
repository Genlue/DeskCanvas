using Notes.Models;
using ReactiveUI;

namespace Notes.ViewModels;

/// <summary>
/// Single, long-lived view model for the widget. Kept in sync with the model
/// in place (no DataContext swaps) so the editing session, focus and caret
/// survive saves and external updates.
/// </summary>
public class NoteViewModel : ReactiveObject
{
    private NoteModel noteModel;

    public NoteViewModel(NoteModel noteModel) => this.noteModel = noteModel;

    /// <summary>
    /// The current model — the single source of truth during the session
    /// (the bindings write through to it, the view persists it on focus loss).
    /// </summary>
    public NoteModel Model => noteModel;

    /// <summary>
    /// Replace the underlying model and notify the bindings.
    /// </summary>
    public void Update(NoteModel newModel)
    {
        noteModel = newModel;
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Content));
        this.RaisePropertyChanged(nameof(Updated));
    }

    /// <summary>
    /// Writable so the TwoWay TextBox bindings keep the model in sync while
    /// typing (the view still persists on focus loss).
    /// </summary>
    public string? Title
    {
        get => noteModel.Title;
        set
        {
            if (value == noteModel.Title) return;
            noteModel = noteModel with { Title = value };
            this.RaisePropertyChanged();
        }
    }

    public string? Content
    {
        get => noteModel.Content;
        set
        {
            if (value == noteModel.Content) return;
            noteModel = noteModel with { Content = value };
            this.RaisePropertyChanged();
        }
    }

    public string? Updated => noteModel.Updated?.ToString("g", Thread.CurrentThread.CurrentUICulture);
}
