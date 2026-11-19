using System.Text;
using RaiCore.Pdf.Documents.Model;

namespace RaiCore.Pdf.Documents.Rtf;

/// <summary>
/// Lists: the list table (<c>\listtable</c>) and its overrides (<c>\listoverridetable</c>) with counters per list,
/// old-style paragraph numbering (<c>\pn</c>), and the writer's own label text (<c>\listtext</c>, <c>\pntext</c>) as
/// the fallback.
/// </summary>
internal sealed partial class RtfReader
{
    private readonly List<RtfList> _lists = [];
    private readonly Dictionary<int, RtfListOverride> _overrides = [];
    private readonly Dictionary<int, int?[]> _counters = [];
    private readonly HashSet<int> _started = [];
    private readonly RtfParagraphNumbering?[] _sectionLevels = new RtfParagraphNumbering?[9];

    /// <summary>A level of a list override being read (<c>\lfolevel</c>).</summary>
    private sealed record OverrideLevel(RtfListOverride Owner, int Index);

    private void StartListLevel(Group group)
    {
        var level = new RtfListLevel();
        if (group.Data is RtfList list)
        {
            if (list.Levels.Count < 9)
            {
                list.Levels.Add(level);
            }
        }
        else if (group.Data is OverrideLevel overrideLevel)
        {
            overrideLevel.Owner.Levels[overrideLevel.Index] = level;
        }

        group.Data = level;
        SetDestination(RtfDestination.ListLevel);
    }

    private void ListWord(string word, int parameter, bool hasParameter)
    {
        var on = !hasParameter || parameter != 0;
        switch (Top.Data)
        {
            case RtfList list when word == "listid":
                list.Id = parameter;
                break;
            case RtfListLevel level:
                switch (word)
                {
                    case "levelnfc":
                    case "levelnfcn":
                        level.Format = FormatOfNumber(parameter);
                        break;
                    case "levelstartat":
                        level.Start = Math.Clamp(parameter, -1_000_000, 1_000_000);
                        break;
                    case "levelfollow":
                        level.Suffix = parameter switch
                        {
                            1 => LabelSuffix.Space,
                            2 => LabelSuffix.Nothing,
                            _ => LabelSuffix.Tab,
                        };
                        break;
                    case "levellegal":
                        level.Legal = on;
                        break;
                    case "levelnorestart":
                        level.NoRestart = on;
                        break;
                    default:
                        if (level.Words.Count < 64)
                        {
                            level.Words.Add((word, parameter, hasParameter));
                        }

                        break;
                }

                break;
            case RtfListOverride listOverride:
                if (word == "listid")
                {
                    listOverride.ListId = parameter;
                }
                else if (word == "ls")
                {
                    listOverride.Number = parameter;
                }

                break;
            case OverrideLevel overrideLevel when word == "levelstartat":
                overrideLevel.Owner.Starts[overrideLevel.Index] = Math.Clamp(parameter, -1_000_000, 1_000_000);
                break;
        }
    }

    /// <summary>The number format of a list level's <c>\levelnfc</c> (or old numbering's) code.</summary>
    private static NumberFormat FormatOfNumber(int code) => code switch
    {
        1 => NumberFormat.UpperRoman,
        2 => NumberFormat.LowerRoman,
        3 => NumberFormat.UpperLetter,
        4 => NumberFormat.LowerLetter,
        5 or 7 => NumberFormat.Ordinal,
        22 => NumberFormat.DecimalZero,
        23 => NumberFormat.Bullet,
        255 => NumberFormat.None,
        _ => NumberFormat.Decimal,
    };

    private void NumberingWord(string word, int parameter, bool hasParameter)
    {
        if (Top.Data is not RtfParagraphNumbering numbering)
        {
            return;
        }

        switch (word)
        {
            case "pnlvlbody":
                numbering.Level = 0;
                break;
            case "pnlvlblt":
                numbering.Level = 10;
                break;
            case "pnlvlcont":
                numbering.Level = 11;
                break;
            case "pnlvl":
                numbering.Level = Math.Clamp(parameter, 1, 9);
                break;
            case "pndec":
            case "pncard":
                (numbering.Format, numbering.HasFormat) = (NumberFormat.Decimal, true);
                break;
            case "pnucrm":
                (numbering.Format, numbering.HasFormat) = (NumberFormat.UpperRoman, true);
                break;
            case "pnlcrm":
                (numbering.Format, numbering.HasFormat) = (NumberFormat.LowerRoman, true);
                break;
            case "pnucltr":
                (numbering.Format, numbering.HasFormat) = (NumberFormat.UpperLetter, true);
                break;
            case "pnlcltr":
                (numbering.Format, numbering.HasFormat) = (NumberFormat.LowerLetter, true);
                break;
            case "pnord":
            case "pnordt":
                (numbering.Format, numbering.HasFormat) = (NumberFormat.Ordinal, true);
                break;
            case "pnstart":
                numbering.Start = Math.Clamp(parameter, -1_000_000, 1_000_000);
                break;
            case "pnf":
                numbering.Font = parameter;
                break;
            case "pnindent":
                numbering.Indent = Math.Clamp(parameter, -MaxTwips, MaxTwips);
                break;
            case "pnhang":
                numbering.Hang = !hasParameter || parameter != 0;
                break;
            case "pntxtb":
                Top.Destination = RtfDestination.NumberingBefore;
                break;
            case "pntxta":
                Top.Destination = RtfDestination.NumberingAfter;
                break;
        }
    }

    /// <summary>
    /// A paragraph's label: from its list (<c>\ls</c>, counted here), else its old-style numbering (the writer's
    /// <c>\pntext</c>, else counted), else the writer's <c>\listtext</c>; or <c>null</c>.
    /// </summary>
    private ListLabel? Label(Story story, RtfParagraphState state, RtfCharState mark, ParagraphBuilder? builder, ref ParagraphFormat format)
    {
        if (mark.Hidden || mark.Deleted)
        {
            return null;
        }

        var labelChars = mark with { Underline = UnderlineStyle.None, Highlight = 0, Background = 0 };
        if (state.List > 0 && ListLabel(state, labelChars, ref format) is { } listLabel)
        {
            story.Numbered = (-1, 0);
            return listLabel;
        }

        var captured = builder?.LabelText?.ToString().Trim();
        if (state.Numbering is { } numbering)
        {
            var definition = numbering;
            if (numbering.Level is >= 1 and <= 9 && !numbering.HasFormat && _sectionLevels[numbering.Level - 1] is { } section)
            {
                definition = section;
            }

            string text;
            if (captured is { Length: > 0 })
            {
                text = captured;
            }
            else if (definition.Level == 10)
            {
                text = definition.Before.Length > 0 ? definition.Before : "\u2022";
            }
            else if (definition.Level == 11)
            {
                text = string.Empty;
            }
            else
            {
                var number = story.Numbered.Level == definition.Level ? story.Numbered.Number + 1 : definition.Start;
                story.Numbered = (definition.Level, number);
                text = definition.Before + Numbering.Format(number, definition.Format) + definition.After;
                return new ListLabel(text, Format(labelChars), LabelSuffix.Tab);
            }

            story.Numbered = (-1, 0);
            return text.Length > 0 ? new ListLabel(text, Format(builder is not null && captured is { Length: > 0 } ? LabelOf(builder, labelChars) : labelChars), LabelSuffix.Tab) : null;
        }

        story.Numbered = (-1, 0);
        return captured is { Length: > 0 } ? new ListLabel(captured, Format(LabelOf(builder!, labelChars)), LabelSuffix.Tab) : null;
    }

    /// <summary>The character format of a captured label: its own, in the paragraph's font when its font is a symbol font.</summary>
    private RtfCharState LabelOf(ParagraphBuilder builder, RtfCharState fallback)
    {
        var chars = builder.LabelChars with { Underline = UnderlineStyle.None, Highlight = 0, Background = 0, Hidden = false, Deleted = false };
        return FontOf(chars.Font) is { Symbol: true } ? chars with { Font = fallback.Font } : chars;
    }

    /// <summary>The label of a list paragraph, counted, or <c>null</c> when its list or level is not defined.</summary>
    private ListLabel? ListLabel(RtfParagraphState state, RtfCharState chars, ref ParagraphFormat format)
    {
        if (!_overrides.TryGetValue(state.List, out var listOverride) || ListById(listOverride.ListId) is not { } list
            || LevelOf(listOverride, list, state.Level) is not { } definition)
        {
            return null;
        }

        if (!_counters.TryGetValue(list.Id, out var counters))
        {
            counters = new int?[9];
            _counters[list.Id] = counters;
        }

        if (_started.Add(state.List))
        {
            for (var l = 0; l < 9; l++)
            {
                if (listOverride.Starts[l] is { } start)
                {
                    counters[l] = start - 1;
                }
            }
        }

        var level = state.Level;
        counters[level] = counters[level] is { } current ? current + 1 : definition.Start;
        for (var deeper = level + 1; deeper < 9; deeper++)
        {
            if (LevelOf(listOverride, list, deeper) is not { NoRestart: true })
            {
                counters[deeper] = null;
            }
        }

        var text = new StringBuilder();
        var levelText = definition.Text;
        var length = levelText.Length > 0 ? Math.Min(levelText[0], levelText.Length - 1) : 0;
        for (var i = 1; i <= length; i++)
        {
            var c = levelText[i];
            if (c < 9)
            {
                var referenced = LevelOf(listOverride, list, c);
                var value = counters[c] ?? referenced?.Start ?? 1;
                var numberFormat = referenced?.Format ?? NumberFormat.Decimal;
                if (definition.Legal && c != level && numberFormat != NumberFormat.None)
                {
                    numberFormat = NumberFormat.Decimal;
                }

                text.Append(Numbering.Format(value, numberFormat == NumberFormat.Bullet ? NumberFormat.Decimal : numberFormat));
            }
            else if (c >= ' ')
            {
                text.Append(c);
            }
        }

        // The level's character format, and its indents unless the paragraph has its own.
        var labelChars = chars;
        var indents = state;
        foreach (var (word, parameter, hasParameter) in definition.Words)
        {
            if (!ApplyCharacter(ref labelChars, word, parameter, hasParameter) && !state.HasIndent && word is "li" or "lin" or "fi")
            {
                ApplyParagraph(ref indents, word, parameter, hasParameter);
            }
        }

        if (!state.HasIndent)
        {
            format = format with { IndentStart = indents.LeftIndent / 20.0, FirstLineIndent = indents.FirstIndent / 20.0 };
        }

        var label = text.ToString();
        if (label.Length == 0)
        {
            return null;
        }

        if (FontOf(labelChars.Font) is { Symbol: true })
        {
            label = MapSymbols(label, labelChars.Font);
            labelChars.Font = chars.Font;
        }

        return new ListLabel(label, Format(labelChars with { Hidden = false }), definition.Suffix);
    }

    private RtfList? ListById(int id)
    {
        foreach (var list in _lists)
        {
            if (list.Id == id)
            {
                return list;
            }
        }

        return null;
    }

    /// <summary>A level of a list, with the override's own definition of it; a simple list's one level for all levels.</summary>
    private static RtfListLevel? LevelOf(RtfListOverride listOverride, RtfList list, int level) =>
        level is < 0 or > 8 ? null
        : listOverride.Levels[level] ?? (level < list.Levels.Count ? list.Levels[level] : list.Levels.Count == 1 ? list.Levels[0] : null);
}
