using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace winapp_GUI;

public class ProgressCard : INotifyPropertyChanged
{
    private string? _title;
    public string? Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<string> SubItems { get; set; } = new();
    public bool IsExpandable => SubItems != null && SubItems.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public static ProgressCard Start(string title, ObservableCollection<ProgressCard>? cardCollection)
    {
        if (cardCollection == null)
        {
            cardCollection = new ObservableCollection<ProgressCard>();
        }
        var card = new ProgressCard { Title = title };
        cardCollection.Add(card);
        return card;
    }

    public void AddSubItem(string message)
    {
        SubItems.Add(message);
    }

    public void MarkSuccess()
    {
        if (Title != null && !Title.StartsWith("✅ "))
        {
            Title = "✅ " + Title;
        }
    }

    public void MarkFailure()
    {
        if (Title != null && !Title.StartsWith("❌ "))
        {
            Title = "❌ " + Title;
        }
    }

    public static ProgressCard StartWithMessages(string title, ObservableCollection<ProgressCard> cardCollection, params string[] messages)
    {
        var card = Start(title, cardCollection);
        foreach (var msg in messages)
        {
            card.AddSubItem(msg);
        }
        return card;
    }

    public static ProgressCard ShowError(string title, ObservableCollection<ProgressCard>? cardCollection, params string[] messages)
    {
        if (cardCollection == null)
        {
            cardCollection = new ObservableCollection<ProgressCard>();
        }
        var card = Start(title, cardCollection);
        foreach (var msg in messages)
        {
            card.AddSubItem(msg);
        }
        card.MarkFailure();
        return card;
    }
}
