using HermesDesktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HermesDesktop.Views;

/// <summary>
/// Picks a DataTemplate for the chat ListView based on item type:
/// ChatMessageItem → node-card message; ChatEventItem → inline event pill.
/// </summary>
public sealed class ChatListItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MessageTemplate { get; set; }
    public DataTemplate? EventTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => Select(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => Select(item);

    private DataTemplate Select(object item) => item switch
    {
        ChatEventItem   => EventTemplate!,
        ChatMessageItem => MessageTemplate!,
        _               => MessageTemplate!,
    };
}
