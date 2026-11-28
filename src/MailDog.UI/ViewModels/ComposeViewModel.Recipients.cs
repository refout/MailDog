using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Html;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

public sealed partial class ComposeViewModel
{
    /// <summary>把已用分号/逗号结束的地址转成 chip，输入框只保留还没提交的文本。</summary>
    private string? CommitCompletedRecipients(
        string text,
        ObservableCollection<RecipientChipViewModel> target,
        string hasProperty,
        string valueProperty)
    {
        var lastSeparator = Math.Max(text.LastIndexOf(';'), text.LastIndexOf(','));
        if (lastSeparator < 0)
        {
            return null;
        }

        var pending = text[(lastSeparator + 1)..].TrimStart();
        var kept = new List<string>();
        var recognized = false;
        foreach (var token in SplitRecipientTokens(text[..(lastSeparator + 1)]))
        {
            if (ParseRecipientToken(token) is { } recipient)
            {
                recognized = true;
                AddRecipient(target, recipient.Label, recipient.Address, hasProperty, valueProperty);
            }
            else
            {
                kept.Add(token);
            }
        }

        if (!recognized)
        {
            return null;
        }

        if (pending.Length > 0)
        {
            kept.Add(pending);
        }

        return string.Join("; ", kept);
    }

    private void SelectSuggestion(
        RecipientSuggestionViewModel suggestion,
        ObservableCollection<RecipientChipViewModel> target,
        Func<string> getText,
        Action<string> setText,
        ObservableCollection<RecipientSuggestionViewModel> suggestions,
        string hasRecipientsProperty,
        string valueProperty,
        string hasSuggestionsProperty)
    {
        AddRecipient(
            target,
            suggestion.Name,
            suggestion.Address,
            hasRecipientsProperty,
            valueProperty);
        SetRecipientText(RemoveLastRecipientToken(getText()), setText);
        suggestions.Clear();
        OnPropertyChanged(hasSuggestionsProperty);
        OnPropertyChanged(valueProperty);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void AddRecipient(
        ObservableCollection<RecipientChipViewModel> target,
        string label,
        string address,
        string hasProperty,
        string valueProperty)
    {
        if (target.Any(item =>
                string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var chip = new RecipientChipViewModel(
            label,
            address,
            item =>
            {
                target.Remove(item);
                NotifyRecipientState(hasProperty, valueProperty);
            });
        target.Add(chip);
        NotifyRecipientState(hasProperty, valueProperty);
    }

    private void NotifyRecipientState(string hasProperty, string valueProperty)
    {
        OnPropertyChanged(hasProperty);
        OnPropertyChanged(valueProperty);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SeedRecipients(
        string text,
        ObservableCollection<RecipientChipViewModel> target,
        string hasProperty,
        string valueProperty)
    {
        foreach (var token in SplitRecipientTokens(text))
        {
            if (ParseRecipientToken(token) is { } recipient)
            {
                AddRecipient(target, recipient.Label, recipient.Address, hasProperty, valueProperty);
            }
        }
    }

    private void SetRecipientText(string value, Action<string> setText)
    {
        _updatingRecipientText = true;
        try
        {
            setText(value);
        }
        finally
        {
            _updatingRecipientText = false;
        }
    }

    private static string BuildRecipientValue(
        IEnumerable<RecipientChipViewModel> chips,
        string input)
    {
        var addresses = chips
            .Select(chip => chip.Address)
            .ToList();
        foreach (var token in SplitRecipientTokens(input))
        {
            if (ParseRecipientToken(token) is not { } recipient)
            {
                continue;
            }

            if (!addresses.Any(address =>
                    string.Equals(address, recipient.Address, StringComparison.OrdinalIgnoreCase)))
            {
                addresses.Add(recipient.Address);
            }
        }

        return string.Join("; ", addresses);
    }

    private void UpdateSuggestions(
        string text,
        ObservableCollection<RecipientSuggestionViewModel> target,
        ObservableCollection<RecipientChipViewModel> chips,
        string hasProperty,
        Action<RecipientSuggestionViewModel> select)
    {
        target.Clear();

        var token = LastRecipientToken(text).Trim();
        if (token.StartsWith('<'))
        {
            token = token[1..];
        }

        var query = token.Trim().Trim('<', '>', ' ');
        if (query.Length < 2)
        {
            OnPropertyChanged(hasProperty);
            return;
        }

        var existing = SplitRecipientTokens(text)
            .Select(ParseRecipientToken)
            .Where(recipient => recipient is not null)
            .Select(recipient => recipient!.Value.Address)
            .ToList();
        existing.AddRange(chips.Select(chip => chip.Address));
        var existingSet = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matches = _contacts
            .Where(contact => !existingSet.Contains(contact.Address))
            .Where(contact => contact.Address.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || contact.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(contact =>
                contact.Address.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(contact => contact.Name, StringComparer.CurrentCulture)
            .Take(5)
            .Select(contact => new RecipientSuggestionViewModel(
                contact.Name,
                contact.Address,
                select));

        foreach (var suggestion in matches)
        {
            target.Add(suggestion);
        }

        OnPropertyChanged(hasProperty);
    }

    private static string RemoveLastRecipientToken(string text)
    {
        var separator = Math.Max(text.LastIndexOf(';'), text.LastIndexOf(','));
        var prefix = separator >= 0 ? text[..(separator + 1)].TrimEnd() : string.Empty;
        return prefix.Length == 0 ? string.Empty : $"{prefix} ";
    }

    private static string LastRecipientToken(string text)
    {
        var separator = Math.Max(text.LastIndexOf(';'), text.LastIndexOf(','));
        return separator >= 0 ? text[(separator + 1)..] : text;
    }

    private static string[] SplitRecipientTokens(string text)
        => text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();

    private static RecipientToken? ParseRecipientToken(string token)
    {
        var value = token.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        if (open >= 0 && close > open)
        {
            var address = value[(open + 1)..close].Trim();
            if (!address.Contains('@'))
            {
                return null;
            }

            var label = value[..open].Trim().Trim('"').Trim();
            return new RecipientToken(address, string.IsNullOrWhiteSpace(label) ? address : label);
        }

        return value.Contains('@') && !value.Contains(' ')
            ? new RecipientToken(value, value)
            : null;
    }

    private readonly record struct RecipientToken(string Address, string Label);

}
