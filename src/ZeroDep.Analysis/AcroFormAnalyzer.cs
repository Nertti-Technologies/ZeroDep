using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ZeroDep.Abstractions;
using ZeroDep.Model;
using ZeroDep.Objects;

namespace ZeroDep.Analysis;

/// <summary>
/// Feature C: extracts interactive form fields from a document's AcroForm (ISO 32000-2 §12.7).
/// Fields are bound by name from the dictionary — never by geometry — and checkbox/radio state is
/// read from the widget appearance state (<c>/AS</c>) and field value (<c>/V</c>), not from text.
/// </summary>
internal static class AcroFormAnalyzer
{
    private const int MaxDepth = 50;

    /// <summary>Opens a PDF stream and extracts its AcroForm fields.</summary>
    public static AcroFormReport Analyze(Stream stream, string? password = null) => Analyze(stream, password, PdfLimits.Default);

    /// <summary>Opens a PDF stream within <paramref name="limits"/> and extracts its AcroForm fields.</summary>
    public static AcroFormReport Analyze(Stream stream, string? password, PdfLimits limits)
    {
        using PdfDocument document = PdfDocument.Open(stream, password, limits);
        return Analyze(document);
    }

    /// <summary>Extracts AcroForm fields from an open document.</summary>
    public static AcroFormReport Analyze(PdfDocument document)
    {
        if (document.Resolve(document.Catalog["AcroForm"] ?? PdfNull.Instance) is not PdfDictionary acroForm)
        {
            return new AcroFormReport { HasAcroForm = false };
        }

        Dictionary<int, int> widgetPages = BuildWidgetPageMap(document);
        var fields = new List<FormFieldInfo>();
        var walk = new FieldWalk(document, widgetPages, fields);

        if (document.Resolve(acroForm["Fields"] ?? PdfNull.Instance) is PdfArray fieldRefs)
        {
            foreach (PdfObject fieldRef in fieldRefs.Items)
            {
                Traverse(walk, fieldRef, parentName: string.Empty, inheritedType: null, inheritedValue: null, depth: 0);
            }
        }

        return new AcroFormReport { HasAcroForm = true, HasXfa = acroForm.ContainsKey("XFA"), Fields = fields };
    }

    private static Dictionary<int, int> BuildWidgetPageMap(PdfDocument document)
    {
        var map = new Dictionary<int, int>();
        for (int i = 0; i < document.Pages.Count; i++)
        {
            if (document.Resolve(document.Pages[i].Dictionary["Annots"] ?? PdfNull.Instance) is PdfArray annots)
            {
                foreach (PdfObject annot in annots.Items)
                {
                    if (annot is PdfReference reference && !map.ContainsKey(reference.ObjectNumber))
                    {
                        map[reference.ObjectNumber] = i;
                    }
                }
            }
        }
        return map;
    }

    // The field tree is a tree (ISO 32000-2 §12.7.4: each field has one /Parent), so an object reached a
    // second time is a cycle or a shared node: it is skipped, which keeps the walk linear in the file. The
    // node budget bounds the total work even for a very wide tree.
    private static void Traverse(FieldWalk walk, PdfObject nodeRef, string parentName, PdfName? inheritedType, PdfObject? inheritedValue, int depth)
    {
        if (depth > MaxDepth) return;

        int? objectNumber = nodeRef is PdfReference reference ? reference.ObjectNumber : (int?)null;
        if (objectNumber.HasValue && !walk.Visited.Add(objectNumber.Value)) return;

        int maxFields = walk.Document.Limits.MaxFormFields;
        if (++walk.NodeCount > maxFields)
        {
            throw new PdfLimitExceededException(
                nameof(PdfLimits.MaxFormFields), maxFields, $"AcroForm field limit exceeded ({maxFields} fields).");
        }

        PdfDocument document = walk.Document;
        if (document.Resolve(nodeRef) is not PdfDictionary node) return;

        string? partialName = (document.Resolve(node["T"] ?? PdfNull.Instance) as PdfString)?.GetText();
        string fullyQualifiedName = Combine(parentName, partialName);
        PdfName? fieldType = node["FT"] as PdfName ?? inheritedType;
        PdfObject? value = node["V"] ?? inheritedValue;
        PdfArray? kids = document.Resolve(node["Kids"] ?? PdfNull.Instance) as PdfArray;

        if (kids is not null && HasFieldChild(document, kids))
        {
            foreach (PdfObject kid in kids.Items)
            {
                Traverse(walk, kid, fullyQualifiedName, fieldType, value, depth + 1);
            }
            return;
        }

        walk.Output.Add(BuildField(document, node, objectNumber, fullyQualifiedName, partialName, fieldType, value, kids, walk.WidgetPages));
    }

    /// <summary>The state shared by one field-tree walk: the visited set and the node budget.</summary>
    private sealed class FieldWalk
    {
        public FieldWalk(PdfDocument document, Dictionary<int, int> widgetPages, List<FormFieldInfo> output)
        {
            Document = document;
            WidgetPages = widgetPages;
            Output = output;
        }

        public PdfDocument Document { get; }

        public Dictionary<int, int> WidgetPages { get; }

        public List<FormFieldInfo> Output { get; }

        public HashSet<int> Visited { get; } = new HashSet<int>();

        public int NodeCount { get; set; }
    }

    private static bool HasFieldChild(PdfDocument document, PdfArray kids)
    {
        foreach (PdfObject kid in kids.Items)
        {
            if (document.Resolve(kid) is PdfDictionary dict && dict.ContainsKey("T")) return true;
        }
        return false;
    }

    private static FormFieldInfo BuildField(PdfDocument document, PdfDictionary node, int? objectNumber, string fullyQualifiedName, string? partialName, PdfName? fieldType, PdfObject? value, PdfArray? kids, Dictionary<int, int> widgetPages)
    {
        string type = fieldType?.Value ?? string.Empty;
        string? label = (document.Resolve(node["TU"] ?? PdfNull.Instance) as PdfString)?.GetText();
        int? pageIndex = ResolvePage(objectNumber, kids, widgetPages);
        BoundingBox? rect = ResolveRect(document, node, kids);

        bool? isChecked = null;
        string? valueText;
        if (type == "Btn")
        {
            string? state = StateName(document, node, value);
            isChecked = state is not null && state != "Off";
            valueText = state;
        }
        else
        {
            valueText = ValueToString(document, value);
        }

        return new FormFieldInfo
        {
            FullyQualifiedName = fullyQualifiedName,
            PartialName = partialName,
            Label = label,
            FieldType = type,
            Value = valueText,
            IsChecked = isChecked,
            PageIndex = pageIndex,
            Rect = rect,
        };
    }

    private static string? StateName(PdfDocument document, PdfDictionary node, PdfObject? value)
    {
        if (document.Resolve(value ?? PdfNull.Instance) is PdfName valueName) return valueName.Value;
        if (node["AS"] is PdfName appearanceState) return appearanceState.Value;
        return null;
    }

    private static int? ResolvePage(int? objectNumber, PdfArray? kids, Dictionary<int, int> widgetPages)
    {
        if (objectNumber.HasValue && widgetPages.TryGetValue(objectNumber.Value, out int page)) return page;
        if (kids is not null)
        {
            foreach (PdfObject kid in kids.Items)
            {
                if (kid is PdfReference reference && widgetPages.TryGetValue(reference.ObjectNumber, out int kidPage))
                {
                    return kidPage;
                }
            }
        }
        return null;
    }

    private static BoundingBox? ResolveRect(PdfDocument document, PdfDictionary node, PdfArray? kids)
    {
        BoundingBox? own = ReadRect(document, node);
        if (own is not null) return own;
        if (kids is not null)
        {
            foreach (PdfObject kid in kids.Items)
            {
                if (document.Resolve(kid) is PdfDictionary widget)
                {
                    BoundingBox? r = ReadRect(document, widget);
                    if (r is not null) return r;
                }
            }
        }
        return null;
    }

    private static BoundingBox? ReadRect(PdfDocument document, PdfDictionary dict)
    {
        if (document.Resolve(dict["Rect"] ?? PdfNull.Instance) is PdfArray a && a.Count >= 4)
        {
            double V(int i) => document.Resolve(a[i]) is PdfNumber n ? n.AsDouble : 0;
            double llx = V(0), lly = V(1), urx = V(2), ury = V(3);
            return new BoundingBox(Math.Min(llx, urx), Math.Min(lly, ury), Math.Abs(urx - llx), Math.Abs(ury - lly));
        }
        return null;
    }

    private static string Combine(string parent, string? partial)
    {
        if (string.IsNullOrEmpty(partial)) return parent;
        return string.IsNullOrEmpty(parent) ? partial! : parent + "." + partial;
    }

    private static string? ValueToString(PdfDocument document, PdfObject? value)
    {
        if (value is null) return null;
        PdfObject resolved = document.Resolve(value);
        switch (resolved)
        {
            case PdfString s: return s.GetText();
            case PdfName n: return n.Value;
            case PdfBoolean b: return b.Value ? "true" : "false";
            case PdfInteger i: return i.Value.ToString(CultureInfo.InvariantCulture);
            case PdfReal r: return r.Value.ToString(CultureInfo.InvariantCulture);
            case PdfArray a:
            {
                var parts = new List<string>();
                foreach (PdfObject item in a.Items)
                {
                    if (document.Resolve(item) is PdfString s) parts.Add(s.GetText());
                }
                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            default: return null;
        }
    }
}
