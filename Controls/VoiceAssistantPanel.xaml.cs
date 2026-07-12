using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using ZapretUI.Models;
using ZapretUI.ViewModels;

namespace ZapretUI.Controls;

public partial class VoiceAssistantPanel : UserControl
{
    public VoiceAssistantPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel oldVm)
            oldVm.VoiceMessages.CollectionChanged -= OnVoiceMessagesChanged;
        if (e.NewValue is MainViewModel newVm)
            newVm.VoiceMessages.CollectionChanged += OnVoiceMessagesChanged;
    }

    private void OnVoiceMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && VoiceChatList.Items.Count > 0)
            VoiceChatList.ScrollIntoView(VoiceChatList.Items[^1]);
    }

    private void OnVoiceInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = true;
    }
}
