using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lavari.Application.Services;
using Lavari.Domain.Errors;

namespace Lavari.Infrastructure.Documents;

/// <summary>
/// Open-source MIT-licensed Open XML SDK Word template renderer.
/// Handles split Word text runs, repeating {{#Lines}}...{{/Lines}} table rows, optional values, and placeholder validation.
/// </summary>
public sealed partial class OpenXmlWordTemplateService : IWordTemplateService
{
    [GeneratedRegex(@"\{\{[^}]+\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    public IReadOnlyList<string> ExtractPlaceholders(byte[] templateDocxBytes)
    {
        using var ms = new MemoryStream(templateDocxBytes, writable: false);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new ValidationServiceException("Word template has no body.", ErrorCodes.TemplateValidationFailed);

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in body.Descendants<Paragraph>())
        {
            var fullText = string.Concat(p.Descendants<Text>().Select(t => t.Text));
            foreach (Match m in PlaceholderRegex().Matches(fullText))
            {
                found.Add(m.Value);
            }
        }
        return found.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public byte[] RenderDocument(
        byte[] templateDocxBytes,
        IReadOnlyDictionary<string, string> scalarValues,
        IReadOnlyList<IReadOnlyDictionary<string, string>> lineItems)
    {
        using var ms = new MemoryStream();
        ms.Write(templateDocxBytes, 0, templateDocxBytes.Length);
        ms.Position = 0;

        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var body = doc.MainDocumentPart?.Document?.Body
                ?? throw new ValidationServiceException("Word template is missing its main document body.", ErrorCodes.TemplateValidationFailed);

            // 1. Expand repeating {{#Lines}}...{{/Lines}} table rows cleanly before scalar substitution.
            var templateRows = body.Descendants<TableRow>()
                .Where(tr => tr.InnerText.Contains("{{#Lines}}", StringComparison.Ordinal))
                .ToList();

            foreach (var templateRow in templateRows)
            {
                var parentTable = templateRow.Parent;
                if (parentTable is null)
                {
                    continue;
                }

                if (lineItems.Count == 0)
                {
                    var emptyRow = (TableRow)templateRow.CloneNode(true);
                    ReplacePlaceholdersInElement(emptyRow, new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["{{#Lines}}"] = string.Empty,
                        ["{{/Lines}}"] = string.Empty,
                        ["{{Line.Description}}"] = "(Total-only document — no itemized lines)",
                        ["{{Line.UnitPrice}}"] = string.Empty,
                        ["{{Line.Quantity}}"] = string.Empty,
                        ["{{Line.Total}}"] = string.Empty
                    });
                    parentTable.InsertBefore(emptyRow, templateRow);
                }
                else
                {
                    foreach (var line in lineItems)
                    {
                        var cloned = (TableRow)templateRow.CloneNode(true);
                        var lineMap = new Dictionary<string, string>(line, StringComparer.Ordinal)
                        {
                            ["{{#Lines}}"] = string.Empty,
                            ["{{/Lines}}"] = string.Empty
                        };
                        ReplacePlaceholdersInElement(cloned, lineMap);
                        parentTable.InsertBefore(cloned, templateRow);
                    }
                }

                templateRow.Remove();
            }

            // 2. Substitute all remaining scalar placeholders across paragraphs, handling split runs.
            ReplacePlaceholdersInElement(body, scalarValues);

            // 3. Validate that no unresolved {{...}} placeholders remain.
            var unresolved = new List<string>();
            foreach (var p in body.Descendants<Paragraph>())
            {
                var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
                foreach (Match m in PlaceholderRegex().Matches(text))
                {
                    unresolved.Add(m.Value);
                }
            }

            if (unresolved.Count > 0)
            {
                throw new ValidationServiceException(
                    $"Document template contains unresolved placeholders: {string.Join(", ", unresolved.Distinct())}",
                    ErrorCodes.TemplateValidationFailed);
            }

            doc.MainDocumentPart!.Document!.Save();
        }

        return ms.ToArray();
    }

    private static void ReplacePlaceholdersInElement(
        DocumentFormat.OpenXml.OpenXmlElement root,
        IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var paragraph in root.Descendants<Paragraph>())
        {
            var texts = paragraph.Descendants<Text>().ToList();
            if (texts.Count == 0)
            {
                continue;
            }

            var combined = string.Concat(texts.Select(t => t.Text));
            if (!combined.Contains("{{", StringComparison.Ordinal))
            {
                continue;
            }

            var updated = combined;
            foreach (var (key, value) in replacements)
            {
                if (updated.Contains(key, StringComparison.Ordinal))
                {
                    updated = updated.Replace(key, value ?? string.Empty, StringComparison.Ordinal);
                }
            }

            if (!string.Equals(updated, combined, StringComparison.Ordinal))
            {
                // Preserve formatting of the first run's Text node and clear split remainder runs.
                texts[0].Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve;
                texts[0].Text = updated;
                for (var i = 1; i < texts.Count; i++)
                {
                    texts[i].Text = string.Empty;
                }
            }
        }
    }
}
