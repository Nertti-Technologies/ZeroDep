using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using ZeroDep.Abstractions;
using ZeroDep.Content;
using ZeroDep.Objects;

namespace ZeroDep.Content.Tests;

/// <summary>
/// The interpreter's budgets from <see cref="PdfLimits"/> (ADR-0010 phase 2): every unit counts against both the
/// page budget and the document's <see cref="ResourceBudget"/>, and font maps follow
/// <see cref="PdfLimits.MaxCMapEntries"/>.
/// </summary>
public sealed class ContentBudgetTests
{
    private static readonly PdfDictionary NoResources = new PdfDictionary(new Dictionary<string, PdfObject>());

    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);

    // 'n' path-ending operators: one work unit each.
    private static byte[] Tokens(int count) => B(string.Concat(Enumerable.Repeat("n ", count)));

    private static ContentInterpreter Interpreter(ResourceBudget budget, Dictionary<int, PdfObject>? objects = null)
        => new ContentInterpreter(
            o => objects is not null && o is PdfReference r && objects.TryGetValue(r.ObjectNumber, out PdfObject? v) ? v : o,
            s => s.GetRawBytes(),
            budget);

    [Fact]
    public void PageBudget_ComesFromTheLimits()
    {
        var budget = new ResourceBudget(new PdfLimits { MaxWorkPerPage = 100 });
        ContentInterpreter interpreter = Interpreter(budget);

        Assert.NotNull(interpreter.RunAll(Tokens(100), NoResources, Matrix.Identity));
        var ex = Assert.Throws<PdfLimitExceededException>(() => interpreter.RunAll(Tokens(101), NoResources, Matrix.Identity));
        Assert.Equal(nameof(PdfLimits.MaxWorkPerPage), ex.LimitName);
        Assert.Equal(100, ex.Limit);
    }

    [Fact]
    public void EveryPage_ChargesTheDocumentBudget()
    {
        // Each page fits its own budget; together they exceed the document's.
        var budget = new ResourceBudget(new PdfLimits { MaxWorkPerPage = 100, MaxWorkPerDocument = 250 });
        ContentInterpreter interpreter = Interpreter(budget);

        interpreter.RunAll(Tokens(90), NoResources, Matrix.Identity);
        interpreter.RunAll(Tokens(90), NoResources, Matrix.Identity);
        Assert.Equal(180, budget.Work);

        var ex = Assert.Throws<PdfLimitExceededException>(() => interpreter.RunAll(Tokens(90), NoResources, Matrix.Identity));
        Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), ex.LimitName);
        Assert.Equal(250, ex.Limit);
    }

    [Fact]
    public void DocumentBreach_IsReportedWhenBothBudgetsAreExceeded()
    {
        var budget = new ResourceBudget(new PdfLimits { MaxWorkPerPage = 50, MaxWorkPerDocument = 50 });

        var ex = Assert.Throws<PdfLimitExceededException>(() => Interpreter(budget).RunAll(Tokens(60), NoResources, Matrix.Identity));
        Assert.Equal(nameof(PdfLimits.MaxWorkPerDocument), ex.LimitName);
    }

    [Fact]
    public void StandaloneInterpreter_UsesTheDefaultLimits()
    {
        var interpreter = new ContentInterpreter(o => o, s => s.GetRawBytes());

        Assert.Equal(PdfLimits.Default.MaxWorkPerPage, interpreter.MaxWorkPerPage);
        Assert.Throws<ArgumentNullException>(() => new ContentInterpreter(o => o, s => s.GetRawBytes(), null!));
    }

    [Fact]
    public void FontMapOverTheEntryLimit_IsDroppedAndReported()
    {
        // A /ToUnicode map with three entries under a limit of two: the map is dropped, so the text falls back,
        // and the run reports that a font map was lost.
        byte[] cmap = B("begincmap\n3 beginbfchar\n<41><0058>\n<42><0059>\n<43><005A>\nendbfchar\nendcmap\n");
        var objects = new Dictionary<int, PdfObject>
        {
            [10] = new PdfStream(new PdfDictionary(new Dictionary<string, PdfObject>()), cmap),
        };
        var font = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type1"),
            ["BaseFont"] = new PdfName("Helvetica"),
            ["ToUnicode"] = new PdfReference(10, 0),
        });
        var resources = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Font"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["F1"] = font }),
        });
        byte[] content = B("BT /F1 12 Tf (AB) Tj ET");

        ContentResult small = Interpreter(new ResourceBudget(new PdfLimits { MaxCMapEntries = 2 }), objects).RunAll(content, resources, Matrix.Identity);
        Assert.True(small.FontMapLimitExceeded);
        Assert.Equal("AB", Assert.Single(small.TextRuns).Text);

        ContentResult normal = Interpreter(new ResourceBudget(PdfLimits.Default), objects).RunAll(content, resources, Matrix.Identity);
        Assert.False(normal.FontMapLimitExceeded);
        Assert.Equal("XY", Assert.Single(normal.TextRuns).Text);
    }

    [Fact]
    public void ToUnicodeMap_FollowsTheInjectedEntryLimit()
    {
        byte[] cmap = B("begincmap\n3 beginbfchar\n<41><0058>\n<42><0059>\n<43><005A>\nendbfchar\nendcmap\n");

        Assert.Equal(3, ToUnicodeCMap.Parse(cmap, 3).Count);
        var ex = Assert.Throws<PdfLimitExceededException>(() => ToUnicodeCMap.Parse(cmap, 2));
        Assert.Equal(nameof(PdfLimits.MaxCMapEntries), ex.LimitName);
        Assert.Equal(2, ex.Limit);
    }
}
