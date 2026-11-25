using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using WalletPK.Common.Primitives;
using WalletPK.Ledger.Application.Reports;

namespace WalletPK.Ledger.Infrastructure.Reports;

/// <summary>
/// The monthly statement as a PDF (M12.5, QuestPDF): a summary box, then every entry with its running
/// balance, A4 with page numbers. Times are Pakistan time.
/// </summary>
internal sealed class StatementPdf : IStatementRenderer
{
    private static readonly TimeSpan Pakistan = TimeSpan.FromHours(5);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const string Ink = "#1F2937";
    private const string Muted = "#6B7280";
    private const string Accent = "#0F766E";
    private const string Rule = "#E5E7EB";

    static StatementPdf()
    {
        // Free for individuals and organisations under USD 1M annual revenue (https://www.questpdf.com/license/).
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Render(MonthlyStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(style => style.FontSize(9).FontColor(Ink));

            page.Header().Element(header => Header(header, statement));
            page.Content().PaddingVertical(12).Column(column =>
            {
                column.Spacing(14);
                column.Item().Element(summary => Summary(summary, statement));
                column.Item().Element(table => Entries(table, statement));
                if (!statement.Complete)
                {
                    column.Item().Text($"This month is still in progress: the statement runs to {Local(statement.To):d MMM yyyy, HH:mm} and later entries are not included.")
                        .FontColor(Muted).Italic();
                }
            });
            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"Generated {Local(statement.GeneratedAt):d MMM yyyy, HH:mm} (Pakistan time). Questions about an entry? Quote its reference.")
                    .FontSize(7).FontColor(Muted);
                row.ConstantItem(70).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7).FontColor(Muted));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        })).GeneratePdf();
    }

    private static void Header(IContainer container, MonthlyStatement statement) =>
        container.BorderBottom(1).BorderColor(Accent).PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("WalletPK").FontSize(18).Bold().FontColor(Accent);
                column.Item().Text("Wallet statement").FontSize(11).FontColor(Muted);
            });
            row.RelativeItem().AlignRight().Column(column =>
            {
                column.Item().AlignRight().Text(statement.Month.Name).FontSize(13).Bold();
                column.Item().AlignRight().Text($"Wallet {statement.WalletNumber ?? "—"}").FontColor(Muted);
                column.Item().AlignRight().Text($"{Local(statement.From):d MMM yyyy} to {Local(statement.To.AddTicks(-1)):d MMM yyyy}").FontColor(Muted);
            });
        });

    private static void Summary(IContainer container, MonthlyStatement statement) =>
        container.Background("#F0FDFA").Padding(10).Row(row =>
        {
            Figure(row.RelativeItem(), "Opening balance", statement.Opening);
            Figure(row.RelativeItem(), "Money in", statement.MoneyIn);
            Figure(row.RelativeItem(), "Money out", statement.MoneyOut);
            Figure(row.RelativeItem(), "Closing balance", statement.Closing, bold: true);
            row.RelativeItem().Column(column =>
            {
                column.Item().Text("Entries").FontSize(8).FontColor(Muted);
                column.Item().Text(statement.Entries.Count.ToString("N0", Invariant)).FontSize(12).SemiBold();
            });
        });

    private static void Figure(IContainer container, string label, Money amount, bool bold = false) =>
        container.Column(column =>
        {
            column.Item().Text(label).FontSize(8).FontColor(Muted);
            var text = column.Item().Text(Rupees(amount)).FontSize(12);
            if (bold)
            {
                text.Bold().FontColor(Accent);
            }
            else
            {
                text.SemiBold();
            }
        });

    private static void Entries(IContainer container, MonthlyStatement statement) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(62);
                columns.RelativeColumn(4);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
            });

            table.Header(header =>
            {
                foreach (var (title, right) in new[] { ("Date", false), ("Description", false), ("In", true), ("Out", true), ("Balance", true) })
                {
                    var cell = header.Cell().BorderBottom(1).BorderColor(Ink).PaddingVertical(4);
                    (right ? cell.AlignRight() : cell).Text(title).SemiBold();
                }
            });

            table.Cell().ColumnSpan(4).Element(Row).Text("Opening balance").FontColor(Muted);
            table.Cell().Element(Row).AlignRight().Text(Rupees(statement.Opening)).FontColor(Muted);

            foreach (var entry in statement.Entries)
            {
                table.Cell().Element(Row).Text(text =>
                {
                    text.Line(Local(entry.PostedAt).ToString("d MMM", Invariant));
                    text.Span(Local(entry.PostedAt).ToString("HH:mm", Invariant)).FontSize(7).FontColor(Muted);
                });
                table.Cell().Element(Row).Text(text =>
                {
                    text.Line(entry.Title);
                    text.Span($"Ref {entry.TransactionId.ToString()[..8].ToUpperInvariant()}").FontSize(7).FontColor(Muted);
                });
                table.Cell().Element(Row).AlignRight().Text(entry.Credit is { } credit ? Rupees(credit) : "").FontColor(Accent);
                table.Cell().Element(Row).AlignRight().Text(entry.Debit is { } debit ? Rupees(debit) : "");
                table.Cell().Element(Row).AlignRight().Text(Rupees(entry.BalanceAfter));
            }

            if (statement.Entries.Count == 0)
            {
                table.Cell().ColumnSpan(5).Element(Row).AlignCenter().Text("No transactions this month.").FontColor(Muted);
            }

            table.Cell().ColumnSpan(4).Element(Row).Text("Closing balance").SemiBold();
            table.Cell().Element(Row).AlignRight().Text(Rupees(statement.Closing)).SemiBold();
        });

    private static IContainer Row(IContainer container) => container.BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4);

    private static DateTimeOffset Local(DateTimeOffset value) => value.ToOffset(Pakistan);

    private static string Rupees(Money amount) =>
        string.Create(Invariant, $"Rs. {amount.MinorUnits / 100m:N2}");
}
